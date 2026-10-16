// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AndroidNpc.Components;
using Content.Server.AndroidNpc.Data;
using Content.Server.AndroidNpc.Services;
using Content.Server.AndroidNpc.Utilities;
using Content.Server.Radio.EntitySystems;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Radio;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.AndroidNpc.Systems;

/// <summary>
/// Raised on an android when a crew member addresses it with something that is not a recognized order.
/// </summary>
public sealed class AndroidConversationEvent : EntityEventArgs
{
    public readonly EntityUid Source;
    public readonly string Message;

    public AndroidConversationEvent(EntityUid source, string message)
    {
        Source = source;
        Message = message;
    }
}

/// <summary>
/// Free conversation with androids, ported from the Android-generic-responses project.
/// Each message is classified into a typed intent and tone by deterministic phrase/keyword rules and,
/// when configured, a THX-01 typed-decision server. The android answers with a scripted line for that intent and
/// tone, and some intents have an in-game effect (follow, clean an area, lend a tool, evacuate, alert security).
/// </summary>
public sealed class AndroidConversationSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly AndroidDialogueSystem _dialogue = default!;
    [Dependency] private readonly AndroidOrderSystem _orders = default!;
    [Dependency] private readonly AndroidInventorySystem _inventory = default!;
    [Dependency] private readonly RadioSystem _radio = default!;
    [Dependency] private readonly AndroidServiceSystem _service = default!;

    public const string ModelSource = "model";
    public const string FallbackSource = "fallback";
    public const string MenuSource = "menu_rule";

    private const float BeaconNameRange = 40f;


    private readonly ConcurrentQueue<(int Id, AndroidConversationPrediction? Prediction)> _results = new();
    private readonly Dictionary<int, PendingConversation> _pending = new();
    private readonly Dictionary<string, HashSet<string>> _intentSets = new();

    private IAndroidConversationBackend _backend = default!;
    private int _nextRequestId;

    private sealed record PendingConversation(
        EntityUid Android,
        EntityUid Speaker,
        string Message,
        string Text,
        string Language,
        AndroidIntentMatch? Rule,
        int Tone,
        string ConversationId);

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<AndroidComponent, AndroidConversationEvent>(OnConversation);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);

        var config = _prototypeManager.AndroidConfig();
        var apiKey = string.IsNullOrWhiteSpace(config.ConversationApiKeyEnvVar)
            ? null
            : Environment.GetEnvironmentVariable(config.ConversationApiKeyEnvVar);

        _backend = new Thx01ConversationBackend(config.ConversationEndpoint, config.ConversationTimeoutSeconds, apiKey);
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<AndroidConversationPrototype>())
            _intentSets.Clear();
    }

    /// <summary>
    /// Replaces the conversation backend, e.g. with a disabled one so tests resolve replies synchronously.
    /// </summary>
    public void SetBackend(IAndroidConversationBackend backend)
    {
        _backend = backend;
    }

    /// <summary>
    /// Classifier built from the current rules, exposed for diagnostics and tests.
    /// </summary>
    public AndroidIntentClassifier Classifier => _dialogue.Classifier;

    private void OnConversation(EntityUid uid, AndroidComponent android, AndroidConversationEvent ev)
    {
        if (ev.Source == uid || !CanReply(uid))
            return;

        var conversation = EnsureComp<AndroidConversationComponent>(uid);
        var now = _timing.CurTime;

        if (now < conversation.NextReplyAt || conversation.PendingRequest != null)
            return;

        if (GetConversation(android.Job) is not { } proto)
            return;

        conversation.NextReplyAt = now + TimeSpan.FromSeconds(_prototypeManager.AndroidConfig().DialogueRateLimitSeconds);

        var text = AndroidAddressing.StripAddress(EntityManager, _prototypeManager, uid, ev.Message);
        var language = _dialogue.DetectLanguage(text);
        var rule = Classifier.Classify(text, language, GetIntentSet(proto));

        // Naming something on a cook's or bartender's menu is an order, unless it is a question about the menu.
        if (rule?.Intent is not ("menu_question" or "complaint" or "directions" or "emergency") &&
            proto.Effects.FirstOrDefault(e => e.Value == AndroidConversationEffect.PrepareOrder).Key is { } orderIntent &&
            _service.MentionsMenuItem(android.Job, text))
        {
            rule = new AndroidIntentMatch(orderIntent, MenuSource);
        }
        var tone = Classifier.DetectTone(ev.Message);
        var request = new PendingConversation(uid, ev.Source, ev.Message, text, language, rule, tone, proto.ID);

        var query = BuildQuery(proto, text, language);

        if (!_backend.Enabled || query == null || rule?.Source == AndroidIntentClassifier.PhraseSource)
        {
            Resolve(request, null);
            return;
        }

        var id = ++_nextRequestId;
        conversation.PendingRequest = id;
        _pending[id] = request;

        var backend = _backend;
        _ = Task.Run(async () =>
        {
            AndroidConversationPrediction? prediction = null;
            try
            {
                prediction = await backend.PredictAsync(query, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                prediction = null;
            }

            _results.Enqueue((id, prediction));
        });
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        while (_results.TryDequeue(out var result))
        {
            if (!_pending.Remove(result.Id, out var request) || Deleted(request.Android))
                continue;

            if (TryComp<AndroidConversationComponent>(request.Android, out var conversation) &&
                conversation.PendingRequest == result.Id)
            {
                conversation.PendingRequest = null;
            }

            if (result.Prediction == null)
                Log.Debug($"[AndroidNpc] Conversation backend unavailable, using local rules for {ToPrettyString(request.Android)}");

            if (CanReply(request.Android))
                Resolve(request, result.Prediction);
        }
    }

    /// <summary>
    /// Builds the backend query for a message from the job's context and its questions in the detected language
    /// (or the first language defined). Returns null if the job has no backend questions.
    /// </summary>
    public AndroidConversationQuery? BuildQuery(AndroidConversationPrototype proto, string text, string language)
    {
        var questions = proto.BackendQuestions.FirstOrDefault(q => q.Lang == language) ?? proto.BackendQuestions.FirstOrDefault();

        if (questions == null || questions.IntentCriteria.Count == 0)
            return null;

        return new AndroidConversationQuery(
            text,
            language,
            proto.BackendState,
            questions.IntentInstructions,
            questions.IntentCriteria,
            questions.ToneInstructions,
            questions.ToneCriteria);
    }

    private void Resolve(PendingConversation request, AndroidConversationPrediction? prediction)
    {
        if (!_prototypeManager.TryIndex<AndroidConversationPrototype>(request.ConversationId, out var proto))
            return;

        var config = _prototypeManager.AndroidConfig();

        var intents = GetIntentSet(proto);
        string intent;
        string source;

        if (request.Rule is { } rule)
        {
            intent = rule.Intent;
            source = rule.Source;
        }
        else if (prediction?.Intent is { } predicted &&
                 intents.Contains(predicted) &&
                 prediction.IntentConfidence >= (config.ConversationMinConfidence))
        {
            intent = predicted;
            source = ModelSource;
        }
        else
        {
            intent = intents.Contains(Classifier.FallbackIntent) ? Classifier.FallbackIntent : intents.FirstOrDefault() ?? Classifier.FallbackIntent;
            source = FallbackSource;
        }

        var modelTone = prediction == null
            ? 0
            : Thx01ConversationBackend.ToneLevel(
                prediction.ToneProbabilities,
                config.ConversationThreatThreshold,
                config.ConversationImpatientThreshold);

        var tone = Math.Max(request.Tone, modelTone);
        var lines = Classifier.GetLanguage(request.Language);
        var reply = PickReply(proto, request.Language, intent, tone, request.Message.Contains('?'));
        var confidentEnough = source != ModelSource ||
                              prediction?.IntentConfidence >= (config.ConversationEffectMinConfidence);
        var effect = tone >= 2 || !confidentEnough ? AndroidConversationEffect.None : proto.Effects.GetValueOrDefault(intent);

        if (tone >= 2)
            AlertSecurity(request, lines);
        else
            reply = ApplyEffect(request, proto, effect, lines, reply);

        var conversation = EnsureComp<AndroidConversationComponent>(request.Android);
        conversation.LastIntent = intent;
        conversation.LastTone = tone;
        conversation.LastLanguage = request.Language;
        conversation.LastSource = source;
        conversation.LastReply = reply;
        conversation.LastEffect = effect;

        Log.Debug($"[AndroidNpc] {ToPrettyString(request.Android)} heard \"{request.Text}\": intent={intent} ({source}), tone={tone}, lang={request.Language}, effect={effect}");

        if (!string.IsNullOrWhiteSpace(reply))
            _dialogue.Reply(request.Android, reply);
    }

    private string? ApplyEffect(
        PendingConversation request,
        AndroidConversationPrototype proto,
        AndroidConversationEffect effect,
        AndroidConversationLanguage? lines,
        string? reply)
    {
        var android = request.Android;

        switch (effect)
        {
            case AndroidConversationEffect.FollowMe:
            case AndroidConversationEffect.ReturnToWork:
            case AndroidConversationEffect.CleanArea:
                if (!_orders.CanOrder(request.Speaker, android))
                    return lines?.NotAuthorized ?? reply;

                if (effect == AndroidConversationEffect.FollowMe)
                    _orders.IssueOrder(android, request.Speaker, AndroidOrderType.FollowMe, null);
                else if (effect == AndroidConversationEffect.ReturnToWork)
                    _orders.IssueOrder(android, request.Speaker, AndroidOrderType.ReturnToWork, null);
                else
                    IssueCleanArea(request);

                return reply;

            case AndroidConversationEffect.GiveSupply:
                return GiveSupply(request, proto, lines, reply);

            case AndroidConversationEffect.PrepareOrder:
                // An order not on the menu is answered with what is prepared instead.
                return _service.TryQueueRequest(android, request.Speaker, request.Text, request.Language, out var substitution) && substitution != null
                    ? substitution
                    : reply;

            case AndroidConversationEffect.Emergency:
                if (TryComp<AndroidComponent>(android, out var androidComp))
                    androidComp.EmergencyUntil = _timing.CurTime + TimeSpan.FromSeconds(_prototypeManager.AndroidConfig().EmergencyFleeSeconds);

                if (lines != null && _prototypeManager.AndroidConfig() is { } config)
                    SendRadio(android, lines.Emergency, request.Speaker, lines, config.EmergencyRadioChannel);

                return reply;

            default:
                return reply;
        }
    }

    private void IssueCleanArea(PendingConversation request)
    {
        var androidCoords = Transform(request.Android).Coordinates;
        var location = Classifier.FindLocation(request.Text);
        EntityCoordinates target;

        if (location != null && new NavMapBeaconLocator(EntityManager).TryFindBeacon(androidCoords, location, out var beacon))
            target = beacon.Value;
        else
        {
            location = null;
            target = Transform(request.Speaker).Coordinates;
        }

        _orders.IssueOrder(request.Android, request.Speaker, AndroidOrderType.CleanArea, location, target);
    }

    private string? GiveSupply(PendingConversation request, AndroidConversationPrototype proto, AndroidConversationLanguage? lines, string? reply)
    {
        var folded = AndroidIntentClassifier.Fold(request.Text);
        var rule = proto.Supplies.FirstOrDefault(s => Classifier.MatchesAny(s.Patterns, folded));

        if (rule == null)
            return reply;

        if (!_orders.CanOrder(request.Speaker, request.Android))
            return lines?.NotAuthorized ?? reply;

        if (!TryComp<AndroidInventoryComponent>(request.Android, out var inventory) ||
            !_inventory.TryGiveItem(request.Android, inventory, rule.Items, request.Speaker, _prototypeManager.AndroidConfig().HandOverRange, out _))
        {
            return lines?.NoSupply ?? reply;
        }

        return reply;
    }

    private void AlertSecurity(PendingConversation request, AndroidConversationLanguage? lines)
    {
        if (lines == null || _prototypeManager.AndroidConfig() is not { } config)
            return;

        SendRadio(request.Android, lines.Threat, request.Speaker, lines, config.SecurityRadioChannel);
    }

    private void SendRadio(EntityUid android, string template, EntityUid speaker, AndroidConversationLanguage lines, ProtoId<RadioChannelPrototype> channel)
    {
        if (string.IsNullOrWhiteSpace(template) || !_prototypeManager.HasIndex(channel))
            return;

        var location = new NavMapBeaconLocator(EntityManager)
            .TryFindNearestBeaconName(Transform(android).Coordinates, BeaconNameRange, out var name)
            ? name
            : lines.NoLocation;

        var message = template
            .Replace("{speaker}", Name(speaker), StringComparison.Ordinal)
            .Replace("{location}", location, StringComparison.Ordinal);

        _radio.SendRadioMessage(android, message, channel, android);
    }

    /// <summary>
    /// Picks a scripted reply for the intent and tone: the informative first variant for questions,
    /// a random one otherwise. Falls back to the default language when the requested one has no line.
    /// </summary>
    public string? PickReply(AndroidConversationPrototype proto, string language, string intent, int tone, bool question)
    {
        var response = proto.Responses.FirstOrDefault(r => r.Lang == language && r.Intent == intent) ??
                       proto.Responses.FirstOrDefault(r => r.Intent == intent);

        if (response == null)
            return null;

        var variants = response.ForTone(tone);
        if (variants.Count == 0)
            variants = response.Calm;

        if (variants.Count == 0)
            return null;

        return question ? variants[0] : _random.Pick(variants);
    }

    /// <summary>
    /// Returns the conversation data for a job, if any.
    /// </summary>
    public AndroidConversationPrototype? GetConversation(AndroidJob job) => _prototypeManager.AndroidConversation(job);

    /// <summary>
    /// Intents available to a job: those it has scripted replies for.
    /// </summary>
    public IReadOnlySet<string> GetIntentSet(AndroidConversationPrototype proto)
    {
        if (!_intentSets.TryGetValue(proto.ID, out var set))
            _intentSets[proto.ID] = set = proto.Responses.Select(r => r.Intent).ToHashSet();

        return set;
    }

    private bool CanReply(EntityUid uid)
    {
        return !Deleted(uid) &&
               !(TryComp<MobStateComponent>(uid, out var mobState) && mobState.CurrentState is MobState.Dead or MobState.Critical);
    }

}
