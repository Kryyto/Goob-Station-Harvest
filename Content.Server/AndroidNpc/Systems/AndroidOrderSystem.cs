// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Server.AndroidNpc.Components;
using Content.Server.AndroidNpc.Data;
using Content.Server.AndroidNpc.Utilities;
using Content.Server.Chat.Systems;
using Content.Server.Radio;
using Content.Shared.Radio.Components;
using Content.Shared.Radio;
using Content.Shared.Chat;
using Content.Shared.Fluids.Components;
using Content.Shared.Speech;
using Content.Shared.Speech.Components;
using Content.Shared.Verbs;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server.AndroidNpc.Systems;

/// <summary>
/// Parses spoken orders addressed to androids (anyone may give them) and owns the order lifecycle:
/// issue, expiry and failure reports (spoken, or over the radio when the issuer is out of earshot).
/// Addressed messages that are not orders are forwarded to <see cref="AndroidConversationSystem"/>.
/// </summary>
public sealed class AndroidOrderSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly AndroidBreakSystem _breaks = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly AndroidCustomerSystem _customer = default!;
    [Dependency] private readonly AndroidDialogueSystem _dialogue = default!;
    [Dependency] private readonly AndroidServiceSystem _service = default!;
    [Dependency] private readonly ChatSystem _chat = default!;
    [Dependency] private readonly AndroidMedicSystem _medic = default!;

    private const int MenuSampleSize = 8;

    private static readonly VerbCategory OrdersCategory = new("android-verb-category", "/Textures/Interface/character.svg.192dpi.png");

    /// <summary>
    /// Orders offered in the android's context menu; picking one makes the player say it out loud.
    /// </summary>
    private static readonly (AndroidOrderType Order, string Label)[] ContextOrders =
    {
        (AndroidOrderType.FollowMe, "android-verb-follow-me"),
        (AndroidOrderType.ReturnToWork, "android-verb-return-to-work"),
        (AndroidOrderType.TakeBreak, "android-verb-take-break"),
        (AndroidOrderType.StatusReport, "android-verb-status-report"),
        (AndroidOrderType.Capabilities, "android-verb-capabilities"),
        (AndroidOrderType.Menu, "android-verb-menu"),
        (AndroidOrderType.HealMe, "android-verb-heal-me"),
        (AndroidOrderType.RepeatOrder, "android-verb-repeat-order"),
    };

    private OrderPhraseMatcher _phraseMatcher = default!;

    private AndroidOrderFeedbackPrototype FeedbackFor(string? language) => _prototypeManager.AndroidFeedback(language);

    public override void Initialize()
    {
        base.Initialize();

        _phraseMatcher = new OrderPhraseMatcher(_prototypeManager.AndroidPhrases());
        SubscribeLocalEvent<AndroidComponent, ListenEvent>(OnListen);
        SubscribeLocalEvent<AndroidComponent, RadioReceiveEvent>(OnRadio);
        SubscribeLocalEvent<AndroidComponent, GetVerbsEvent<Verb>>(OnGetVerbs);
    }

    private void OnGetVerbs(EntityUid uid, AndroidComponent component, GetVerbsEvent<Verb> args)
    {
        if (args.User == uid || !args.CanInteract || component.Status == AndroidStatus.Leaving ||
            !TryComp<AndroidIdentityComponent>(uid, out var identity))
        {
            return;
        }

        var lines = FeedbackFor(null).ContextMenu;
        var user = args.User;

        foreach (var (order, label) in ContextOrders)
        {
            if (order == AndroidOrderType.Menu && !_service.Menu(component.Job).Items.Any() ||
                order == AndroidOrderType.HealMe && !HasComp<AndroidMedicComponent>(uid) ||
                order == AndroidOrderType.RepeatOrder && !(TryComp<AndroidCustomerOrderComponent>(uid, out var waiting) && !waiting.Served) ||
                lines.GetValueOrDefault(order) is not { } line)
            {
                continue;
            }

            var spoken = AndroidData.Format(line, ("name", identity.FirstName));
            args.Verbs.Add(new Verb
            {
                Text = Loc.GetString(label),
                Category = OrdersCategory,
                Act = () => _chat.TrySendInGameICMessage(user, spoken, InGameICChatType.Speak, ChatTransmitRange.Normal),
            });
        }
    }

    private void OnListen(EntityUid uid, AndroidComponent component, ListenEvent ev)
    {
        // Radio messages arrive through OnRadio as well; their spoken echo is prefixed.
        if (!ev.Message.StartsWith('['))
            Heard(uid, component, ev.Source, ev.Message, null);
    }

    /// <summary>
    /// A radio message on one of the android's channels, e.g. "Fernand, il y a un blessé au bar" on Common.
    /// It is handled like speech and answered on the same channel.
    /// </summary>
    private void OnRadio(EntityUid uid, AndroidComponent component, ref RadioReceiveEvent args)
    {
        Heard(uid, component, args.MessageSource, args.OriginalChatMsg.Message, args.Channel.ID);
    }

    private void Heard(EntityUid uid, AndroidComponent component, EntityUid source, string message, string? radio)
    {
        // Androids ignore each other: a bartender saying "here is your beer, Gérard" to an android customer
        // is not a question for Gérard.
        if (component.Status == AndroidStatus.Leaving || source == uid || HasComp<AndroidComponent>(source))
            return;

        var address = AndroidAddressing.GetAddress(EntityManager, _prototypeManager, uid, message);
        if (address == AndroidAddress.None)
            return;

        // Addressed as a group ("androids, ..."): everyone obeys orders, but only one answers: the nearest one,
        // or on the radio the first android on the channel.
        var answers = address == AndroidAddress.Name ||
                      (radio == null ? IsGroupSpokesperson(uid, source) : IsRadioSpokesperson(uid, radio));

        RecordInteraction(uid, source);
        var language = _dialogue.DetectLanguage(message);
        var feedback = FeedbackFor(language);
        ProtoId<RadioChannelPrototype>? channel = radio;

        if (!_phraseMatcher.TryMatch(message, out var orderType, out var target))
        {
            if (answers)
                RaiseLocalEvent(uid, new AndroidConversationEvent(source, message, channel));

            return;
        }

        switch (orderType)
        {
            // Anyone may ask for a status report or for the android's own bar/kitchen order.
            case AndroidOrderType.StatusReport:
                if (answers)
                    SpeakLines(uid, GetStatusReport(uid, language), channel);
                return;
            case AndroidOrderType.RepeatOrder:
                if (answers && !_customer.TryRestateOrder(uid))
                    _dialogue.Reply(uid, feedback.NoActiveCustomerOrder, channel);
                return;
            case AndroidOrderType.Capabilities:
                if (answers)
                    SpeakLines(uid, GetCapabilities(uid, component, language), channel);
                return;
            case AndroidOrderType.Menu:
                if (answers)
                    _dialogue.Reply(uid, GetMenuAnswer(component, language), channel);
                return;
            // Only a paramedic answers a call for care, even one made to the whole group.
            case AndroidOrderType.HealMe or AndroidOrderType.Injured:
                if (HasComp<AndroidMedicComponent>(uid))
                    _dialogue.Reply(uid, _medic.Call(uid, source, orderType == AndroidOrderType.HealMe, target, language), channel);
                else if (address == AndroidAddress.Name)
                    _dialogue.Reply(uid, feedback.NotAMedic, channel);
                return;
        }

        IssueOrder(uid, source, orderType, target, language: language);

        if (answers)
            _dialogue.Reply(uid, $"{feedback.AcknowledgementPrefix} {GetOrderAck(feedback, orderType, target)}", channel);
    }

    /// <summary>
    /// For a group message on the radio, the android with the lowest id on that channel answers for the group.
    /// </summary>
    private bool IsRadioSpokesperson(EntityUid android, string channel)
    {
        var query = EntityQueryEnumerator<AndroidComponent, ActiveRadioComponent>();
        while (query.MoveNext(out var uid, out var comp, out var radio))
        {
            if (comp.Status != AndroidStatus.Leaving && radio.Channels.Contains(channel) && uid.Id < android.Id)
                return false;
        }

        return true;
    }

    /// <summary>
    /// The android closest to the speaker among those who heard a group message; it answers for the group.
    /// </summary>
    public bool IsGroupSpokesperson(EntityUid android, EntityUid speaker)
    {
        if (!TryComp<TransformComponent>(speaker, out var speakerXform))
            return true;

        var origin = _transform.GetWorldPosition(speakerXform);
        EntityUid? nearest = null;
        var nearestDist = float.MaxValue;
        var query = EntityQueryEnumerator<AndroidComponent, ActiveListenerComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var comp, out var listener, out var xform))
        {
            if (comp.Status == AndroidStatus.Leaving || xform.MapID != speakerXform.MapID)
                continue;

            // Only androids within their own hearing range actually received the message.
            var dist = (_transform.GetWorldPosition(xform) - origin).Length();
            if (dist > listener.Range || dist >= nearestDist)
                continue;

            nearestDist = dist;
            nearest = uid;
        }

        return nearest == null || nearest == android;
    }

    /// <summary>
    /// Gives an order to the android without any spoken acknowledgement. <paramref name="issuerName"/> overrides
    /// the issuer's entity name.
    /// </summary>
    public void IssueOrder(
        EntityUid android,
        EntityUid issuer,
        AndroidOrderType orderType,
        string? target,
        EntityCoordinates? targetCoordinates = null,
        string? issuerName = null,
        string? language = null)
    {
        // Any order interrupts a break, ordered or automatic: the android stands up and its break timer restarts.
        if (orderType != AndroidOrderType.TakeBreak && TryComp<AndroidComponent>(android, out var comp) &&
            (comp.BreakState != AndroidBreakState.None || comp.Status == AndroidStatus.OnBreak ||
             TryComp<AndroidOrderComponent>(android, out var existing) && existing.OrderType == AndroidOrderType.TakeBreak))
        {
            CancelBreak(android);
            comp.NextBreakAt = _timing.CurTime + TimeSpan.FromSeconds(_prototypeManager.AndroidConfig().BreakIntervalSeconds);
        }

        var order = EnsureComp<AndroidOrderComponent>(android);

        order.OrderType = orderType;
        order.Issuer = issuer;
        order.IssuerName = issuerName ?? Name(issuer);
        order.Target = target;
        order.TargetCoordinates = targetCoordinates;
        order.Language = language ?? _prototypeManager.AndroidConfig().Language;
        order.IssuedAt = _timing.CurTime;
        order.ExpiresAt = _timing.CurTime + TimeSpan.FromSeconds(_prototypeManager.AndroidConfig().OrderExpirySeconds);
        order.PathFailures = 0;
        order.FollowLostTime = null;
    }

    /// <summary>
    /// Ends the android's current order with a report to its issuer, in the language the order was given in.
    /// The selected template may use {name}, {issuer} and {target}.
    /// </summary>
    public void FailOrder(EntityUid android, Func<AndroidOrderFeedbackPrototype, string> template)
    {
        if (!TryComp<AndroidOrderComponent>(android, out var order))
            return;

        var feedback = FeedbackFor(order.Language);
        var message = AndroidData.Format(template(feedback),
            ("name", Name(android)),
            ("issuer", order.IssuerName),
            ("target", order.Target ?? feedback.DefaultTarget));

        _dialogue.Report(android, message, order.Issuer);
        ClearOrder(android);
    }

    /// <summary>
    /// Removes the android's order and sends it back to work.
    /// </summary>
    public void ClearOrder(EntityUid android)
    {
        if (TryComp<AndroidOrderComponent>(android, out var order) && order.OrderType == AndroidOrderType.TakeBreak)
            CancelBreak(android);

        RemComp<AndroidOrderComponent>(android);

        if (TryComp<AndroidComponent>(android, out var comp) && comp.Status == AndroidStatus.FollowingOrder)
            comp.Status = AndroidStatus.Working;
    }

    /// <summary>
    /// Returns true if the message matches any configured order phrase.
    /// </summary>
    public bool IsOrderMessage(string message) => _phraseMatcher.TryMatch(message, out _, out _);

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<AndroidOrderComponent, AndroidComponent>();

        while (query.MoveNext(out var uid, out var order, out _))
        {
            if (order.ExpiresAt <= now)
                FailOrder(uid, f => f.OrderExpired);
        }
    }

    private void RecordInteraction(EntityUid android, EntityUid source)
    {
        if (!TryComp<AndroidMemoryComponent>(android, out var memory))
            return;

        memory.RecentInteractions.Enqueue((source, "Spoke", _timing.CurTime));

        while (memory.RecentInteractions.Count > 10)
            memory.RecentInteractions.Dequeue();
    }

    /// <summary>
    /// Builds the short lines answering "what are you doing".
    /// </summary>
    public List<string> GetStatusReport(EntityUid uid, string? language = null)
    {
        var feedback = FeedbackFor(language);
        var lines = new List<string>();

        if (!TryComp<AndroidComponent>(uid, out var android))
        {
            lines.Add(feedback.StatusReportUnknown);
            return lines;
        }

        if (TryComp<AndroidOrderComponent>(uid, out var order))
        {
            var target = order.Target ?? feedback.DefaultTarget;
            lines.Add(order.OrderType switch
            {
                AndroidOrderType.GoToDepartment or AndroidOrderType.CleanArea or AndroidOrderType.RespondToCall =>
                    AndroidData.Format(feedback.StatusReportGoingToDepartment, ("target", target)),
                AndroidOrderType.GoToPerson => AndroidData.Format(feedback.StatusReportGoingToPerson, ("target", target)),
                AndroidOrderType.TakeBreak => feedback.StatusReportOnBreak,
                _ => AndroidData.Format(feedback.StatusReportFollowing, ("issuer", order.IssuerName)),
            });

            if (order.OrderType == AndroidOrderType.FollowMe)
                lines.Add(feedback.StatusReportKeepDistance);

            return lines;
        }

        switch (android.Status)
        {
            case AndroidStatus.Fleeing:
                lines.Add(feedback.StatusReportFleeing);
                lines.Add(feedback.StatusReportCannotWork);
                return lines;
            case AndroidStatus.Leaving:
                lines.Add(feedback.StatusReportLeaving);
                return lines;
            case AndroidStatus.Fighting:
                lines.Add(feedback.StatusReportFighting);
                return lines;
            case AndroidStatus.OnBreak:
                lines.Add(feedback.StatusReportOnBreak);
                return lines;
            case AndroidStatus.Idle:
                lines.Add(feedback.StatusReportIdle);
                return lines;
        }

        lines.Add(feedback.StatusReportWorking);

        if (TryComp<AndroidMedicComponent>(uid, out var medic) && medic.Patient is { } patient)
        {
            lines.Add(AndroidData.Format(feedback.StatusReportTreating, ("target", Name(patient))));
            return lines;
        }

        if (TryComp<AndroidServiceComponent>(uid, out var service) && service.Current is { } request)
        {
            lines.Add(AndroidData.Format(feedback.StatusReportServing, ("target", request.CustomerName)));
            return lines;
        }

        if (TryComp<AndroidInventoryComponent>(uid, out var inventory) &&
            inventory.CurrentToolTarget is { } toolTarget && !Deleted(toolTarget))
        {
            lines.Add(HasComp<PuddleComponent>(toolTarget) ? feedback.StatusReportCleaningPuddle : feedback.StatusReportCleaningTrash);
            return lines;
        }

        if (TryComp<AndroidMemoryComponent>(uid, out var memory) &&
            memory.PatrolTarget.IsValid(EntityManager) &&
            !memory.PatrolTarget.Equals(EntityCoordinates.Invalid))
        {
            lines.Add(feedback.StatusReportPatrolling);
            return lines;
        }

        lines.Add(feedback.StatusReportNoTask);
        return lines;
    }

    private List<string> GetCapabilities(EntityUid uid, AndroidComponent android, string language)
    {
        var lines = new List<string>();
        if (_dialogue.LinesFor(uid, language) is { } jobLines && _dialogue.Pick(jobLines.Capabilities) is { } line)
            lines.Add(line);

        lines.Add(FeedbackFor(language).CapabilitiesOrders);
        return lines;
    }

    private string GetMenuAnswer(AndroidComponent android, string language)
    {
        var feedback = FeedbackFor(language);
        var count = _service.Menu(android.Job).Items.Count();
        if (count == 0)
            return feedback.MenuNone;

        return AndroidData.Format(feedback.MenuSample,
            ("count", count.ToString()),
            ("items", string.Join(", ", _service.MenuSample(android.Job, language, MenuSampleSize))));
    }

    private static string GetOrderAck(AndroidOrderFeedbackPrototype feedback, AndroidOrderType type, string? target)
    {
        var template = type switch
        {
            AndroidOrderType.FollowMe => feedback.AckFollowMe,
            AndroidOrderType.ReturnToWork => feedback.AckReturnToWork,
            AndroidOrderType.GoToDepartment => feedback.AckGoToDepartment,
            AndroidOrderType.GoToPerson => feedback.AckGoToPerson,
            AndroidOrderType.TakeBreak => feedback.AckTakeBreak,
            _ => feedback.AckDefault,
        };

        return AndroidData.Format(template, ("target", target ?? feedback.DefaultTarget));
    }

    /// <summary>
    /// Says several short lines half a second apart instead of one wall of text.
    /// </summary>
    private void SpeakLines(EntityUid android, List<string> lines, ProtoId<RadioChannelPrototype>? radio = null)
    {
        for (var i = 0; i < lines.Count; i++)
            _dialogue.Speak(android, AndroidData.Format(lines[i], ("name", Name(android))), 0.5f + 0.8f * i, radio);
    }

    private void CancelBreak(EntityUid uid) => _breaks.EndBreak(uid);
}
