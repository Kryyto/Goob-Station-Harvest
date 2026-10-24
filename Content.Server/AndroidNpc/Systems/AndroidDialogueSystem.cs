// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Server.AndroidNpc.Components;
using Content.Server.AndroidNpc.Data;
using Content.Server.AndroidNpc.Utilities;
using Content.Server.Chat.Systems;
using Content.Server.Radio.EntitySystems;
using Content.Shared.Chat;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Radio;
using Content.Shared.Speech;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.AndroidNpc.Systems;

/// <summary>
/// Everything an android says: status announcements, random chatter, and reports to a crew member,
/// spoken when they are in earshot and sent over the job's radio channel otherwise.
/// Lines come from the job's <see cref="AndroidJobPrototype"/>.
/// </summary>
public sealed class AndroidDialogueSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly RadioSystem _radio = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;

    private static readonly ProtoId<AndroidIntentRulesPrototype> RulesId = "Default";

    private static readonly TimeSpan MinChatterInterval = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Lines actually sent to chat per tick. Chat is expensive, so a group of androids answering at once is
    /// spread over several ticks instead of stalling one.
    /// </summary>
    private const int MaxLinesPerTick = 2;
    private const int RecentLineCount = 5;

    private sealed record QueuedLine(TimeSpan At, EntityUid Android, string Message, ProtoId<RadioChannelPrototype>? Radio);

    private readonly List<QueuedLine> _queue = new();
    private static readonly TimeSpan MaxChatterInterval = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Language, intent and tone rules shared by orders and conversation.
    /// </summary>
    public AndroidIntentClassifier Classifier { get; private set; } = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        LoadRules();
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<AndroidIntentRulesPrototype>())
            LoadRules();
    }

    private void LoadRules()
    {
#pragma warning disable RA0039 // Code defaults used only when the YAML prototype is missing.
        Classifier = new AndroidIntentClassifier(_prototypeManager.TryIndex(RulesId, out var rules) ? rules : new AndroidIntentRulesPrototype());
#pragma warning restore RA0039
    }

    /// <summary>
    /// Language of what a crew member said, falling back to the server language when nothing gives it away.
    /// </summary>
    public string DetectLanguage(string text) => Classifier.DetectLanguage(text, _prototypeManager.AndroidConfig().Language);

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        FlushQueue(now);

        var query = EntityQueryEnumerator<AndroidComponent, AndroidMemoryComponent, AndroidIdentityComponent>();

        while (query.MoveNext(out var uid, out var android, out var memory, out _))
        {
            // Androids on break smoke silently instead of babbling.
            if (!CanSpeak(uid) || android.Status == AndroidStatus.OnBreak)
                continue;

            if (memory.LastAnnouncedStatus != android.Status)
            {
                var previous = memory.LastAnnouncedStatus;
                memory.LastAnnouncedStatus = android.Status;

                if (PickStatusLine(android, previous) is { } statusLine)
                {
                    Speak(uid, statusLine);
                    android.NextChatterTime = now + RandomInterval();
                    continue;
                }
            }

            if (now < android.NextChatterTime)
                continue;

            android.NextChatterTime = now + RandomInterval();

            if (PickChatter(android, memory) is { } line)
                Speak(uid, line);
        }
    }

    /// <summary>
    /// Queues a line to be said after the delay. Androids never speak from inside an event handler, so replies
    /// cannot cascade through other listeners within a single tick.
    /// </summary>
    public void Speak(EntityUid source, string message, float delaySeconds = 0f, ProtoId<RadioChannelPrototype>? radio = null)
    {
        Enqueue(source, message, radio, delaySeconds);
    }

    /// <summary>
    /// Queues a reply after a short random "thinking" delay, so several androids never answer in the same tick.
    /// A message heard on the radio is answered on the same channel.
    /// </summary>
    public void Reply(EntityUid source, string message, ProtoId<RadioChannelPrototype>? radio = null)
    {
        Enqueue(source, message, radio, _random.NextFloat(0.4f, 1.4f));
    }

    private void Enqueue(EntityUid source, string message, ProtoId<RadioChannelPrototype>? radio, float delaySeconds)
    {
        if (!Deleted(source) && !string.IsNullOrWhiteSpace(message))
            _queue.Add(new QueuedLine(_timing.CurTime + TimeSpan.FromSeconds(delaySeconds), source, message, radio));
    }

    private void FlushQueue(TimeSpan now)
    {
        if (_queue.Count == 0)
            return;

        _queue.Sort((a, b) => a.At.CompareTo(b.At));

        var sent = 0;
        var i = 0;
        while (i < _queue.Count && sent < MaxLinesPerTick && _queue[i].At <= now)
        {
            var line = _queue[i];
            _queue.RemoveAt(i);

            if (Deleted(line.Android) || !CanSpeak(line.Android))
                continue;

            if (line.Radio is { } channel)
                _radio.SendRadioMessage(line.Android, line.Message, channel, line.Android);
            else
                _chat.TrySendInGameICMessage(line.Android, line.Message, InGameICChatType.Speak, ChatTransmitRange.Normal);

            if (TryComp<AndroidComponent>(line.Android, out var android))
            {
                android.LastSaid = line.Message;
                android.RecentLines.Add(line.Message);
                if (android.RecentLines.Count > RecentLineCount)
                    android.RecentLines.RemoveAt(0);
            }

            sent++;
        }
    }

    /// <summary>
    /// Returns true while the android still has lines waiting to be said.
    /// </summary>
    public bool HasQueuedLines(EntityUid android) => _queue.Any(l => l.Android == android);

    /// <summary>
    /// Says a random line from the list, formatted with the given placeholder values. Does nothing for an empty list.
    /// </summary>
    public void SpeakRandom(EntityUid source, List<string> lines, params (string Key, string Value)[] values)
    {
        if (Pick(lines) is { } line)
            Speak(source, AndroidData.Format(line, values));
    }

    /// <summary>
    /// Reports to a crew member: spoken if they are within the configured hearing range, otherwise sent on the
    /// android's job radio channel so a far-away issuer still learns what happened.
    /// </summary>
    public void Report(EntityUid android, string message, EntityUid? listener)
    {
        if (Deleted(android) || string.IsNullOrWhiteSpace(message))
            return;

        if (listener is { } target && !InEarshot(android, target) &&
            TryComp<AndroidComponent>(android, out var comp) &&
            _prototypeManager.AndroidJobData(comp.Job)?.RadioChannel is { } channel &&
            _prototypeManager.HasIndex<RadioChannelPrototype>(channel))
        {
            Enqueue(android, message, channel, 0f);
            return;
        }

        Speak(android, message);
    }

    /// <summary>
    /// Returns true if the listener is close enough to hear the android speak.
    /// </summary>
    public bool InEarshot(EntityUid android, EntityUid listener)
    {
        if (Deleted(listener) ||
            !TryComp<TransformComponent>(android, out var androidXform) ||
            !TryComp<TransformComponent>(listener, out var listenerXform) ||
            androidXform.MapID != listenerXform.MapID)
        {
            return false;
        }

        var range = _prototypeManager.AndroidConfig().HearingRange;
        var distance = (_transform.GetWorldPosition(androidXform) - _transform.GetWorldPosition(listenerXform)).Length();
        return distance <= range;
    }

    public string? Pick(List<string> lines) => lines.Count == 0 ? null : _random.Pick(lines);

    /// <summary>
    /// The android's job lines in the given language, or in the server language.
    /// </summary>
    public AndroidJobLines? LinesFor(EntityUid android, string? language = null)
    {
        return TryComp<AndroidComponent>(android, out var comp)
            ? _prototypeManager.AndroidJobData(comp.Job)?.LinesFor(language ?? _prototypeManager.AndroidConfig().Language)
            : null;
    }

    private TimeSpan RandomInterval()
    {
        return TimeSpan.FromSeconds(_random.Next((int) MinChatterInterval.TotalSeconds, (int) MaxChatterInterval.TotalSeconds + 1));
    }

    private bool CanSpeak(EntityUid uid)
    {
        if (TryComp<MobStateComponent>(uid, out var mobState) && mobState.CurrentState is MobState.Dead or MobState.Critical)
            return false;

        return HasComp<SpeechComponent>(uid);
    }

    private string? PickStatusLine(AndroidComponent android, AndroidStatus? previous)
    {
        if (_prototypeManager.AndroidJobData(android.Job)?.LinesFor(_prototypeManager.AndroidConfig().Language) is not { } lines)
            return null;

        return android.Status switch
        {
            AndroidStatus.OnBreak => Pick(lines.Break),
            AndroidStatus.Fleeing => Pick(lines.Flee),
            AndroidStatus.Working when previous is AndroidStatus.OnBreak or AndroidStatus.FollowingOrder => Pick(lines.BackToWork),
            _ => null,
        };
    }

    private string? PickChatter(AndroidComponent android, AndroidMemoryComponent memory)
    {
        if (_prototypeManager.AndroidJobData(android.Job)?.LinesFor(_prototypeManager.AndroidConfig().Language) is not { } lines)
            return null;

        if (android.Status == AndroidStatus.Fleeing)
            return Pick(lines.Flee);

        if (memory.KnownDirtyTargets.Count > 0 && lines.Mess.Count > 0)
            return Pick(lines.Mess);

        return android.Status == AndroidStatus.Working ? Pick(lines.Idle) : null;
    }
}
