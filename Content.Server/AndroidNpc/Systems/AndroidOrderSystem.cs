// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.AndroidNpc.Components;
using Content.Server.AndroidNpc.Data;
using Content.Server.AndroidNpc.Utilities;
using Content.Shared.Access.Systems;
using Content.Shared.Fluids.Components;
using Content.Shared.Mind;
using Content.Shared.Speech;
using Content.Shared.Speech.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server.AndroidNpc.Systems;

/// <summary>
/// Parses spoken orders addressed to androids, checks the speaker's authority, and owns the order lifecycle:
/// issue, expiry and failure reports (spoken, or over the radio when the issuer is out of earshot).
/// Addressed messages that are not orders are forwarded to <see cref="AndroidConversationSystem"/>.
/// </summary>
public sealed class AndroidOrderSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly AccessReaderSystem _access = default!;
    [Dependency] private readonly SharedMindSystem _mind = default!;
    [Dependency] private readonly AndroidBreakSystem _breaks = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly AndroidCustomerSystem _customer = default!;
    [Dependency] private readonly AndroidDialogueSystem _dialogue = default!;

    /// <summary>
    /// Anyone standing this close may give orders, regardless of access.
    /// </summary>
    private const float ProximityOrderRange = 4f;

    private OrderPhraseMatcher _phraseMatcher = default!;

    private AndroidOrderFeedbackPrototype FeedbackFor(string? language) => _prototypeManager.AndroidFeedback(language);

    public override void Initialize()
    {
        base.Initialize();

        _phraseMatcher = new OrderPhraseMatcher(_prototypeManager.AndroidPhrases());
        SubscribeLocalEvent<AndroidComponent, ListenEvent>(OnListen);
    }

    private void OnListen(EntityUid uid, AndroidComponent component, ListenEvent ev)
    {
        // Radio messages are prefixed; orders must be spoken in person.
        if (ev.Message.StartsWith('[') || component.Status == AndroidStatus.Leaving)
            return;

        var address = AndroidAddressing.GetAddress(EntityManager, _prototypeManager, uid, ev.Message);
        if (address == AndroidAddress.None)
            return;

        // Addressed as a group ("androids, ..."): everyone obeys orders, but only the nearest one answers.
        var answers = address == AndroidAddress.Name || IsGroupSpokesperson(uid, ev.Source);

        RecordInteraction(uid, ev.Source);
        var language = _dialogue.DetectLanguage(ev.Message);
        var feedback = FeedbackFor(language);

        if (!_phraseMatcher.TryMatch(ev.Message, out var orderType, out var target))
        {
            if (answers)
                RaiseLocalEvent(uid, new AndroidConversationEvent(ev.Source, ev.Message));

            return;
        }

        switch (orderType)
        {
            // Anyone may ask for a status report or for the android's own bar/kitchen order.
            case AndroidOrderType.StatusReport:
                if (answers)
                    SpeakLines(uid, GetStatusReport(uid, language));
                return;
            case AndroidOrderType.RepeatOrder:
                if (answers && !_customer.TryRestateOrder(uid))
                    _dialogue.Reply(uid, feedback.NoActiveCustomerOrder);
                return;
        }

        if (!CanOrder(ev.Source, uid))
        {
            if (answers)
                _dialogue.Reply(uid, feedback.NotAuthorized);
            return;
        }

        IssueOrder(uid, ev.Source, orderType, target, language: language);

        if (answers)
            _dialogue.Reply(uid, $"{feedback.AcknowledgementPrefix} {GetOrderAck(feedback, orderType, target)}");
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
    /// Gives an order to the android without any spoken acknowledgement. Authorization must be checked
    /// beforehand with <see cref="CanOrder"/>. <paramref name="issuerName"/> overrides the issuer's entity name.
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
        var hadBreakOrder = TryComp<AndroidOrderComponent>(android, out var existing) && existing.OrderType == AndroidOrderType.TakeBreak;
        var order = EnsureComp<AndroidOrderComponent>(android);

        // A break order replaced by another command: stand up and reset the break state.
        if (hadBreakOrder && orderType != AndroidOrderType.TakeBreak)
            CancelBreak(android);

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

    /// <summary>
    /// Returns true if the issuer may give orders to the android: by job/access, or by standing close to it.
    /// </summary>
    public bool CanOrder(EntityUid issuer, EntityUid android)
    {
        if (IsAuthorizedByAccess(issuer, android))
            return true;

        return TryComp<TransformComponent>(issuer, out var issuerXform) &&
               TryComp<TransformComponent>(android, out var androidXform) &&
               issuerXform.MapID == androidXform.MapID &&
               (_transform.GetWorldPosition(issuerXform) - _transform.GetWorldPosition(androidXform)).Length() <= ProximityOrderRange;
    }

    /// <summary>
    /// Returns true if the issuer carries a universal command access or one of the job's order access tags.
    /// </summary>
    public bool IsAuthorizedByAccess(EntityUid issuer, EntityUid android)
    {
        if (issuer == android)
            return true;

        if (!_mind.TryGetMind(issuer, out _, out _))
            return false;

        var tags = _access.FindAccessTags(issuer);
        if (tags.Count == 0)
            return false;

        foreach (var tag in _prototypeManager.AndroidConfig().UniversalOrderAccess)
        {
            if (tags.Contains(tag))
                return true;
        }

        if (!TryComp<AndroidComponent>(android, out var comp) || _prototypeManager.AndroidJobData(comp.Job) is not { } job)
            return false;

        foreach (var tag in job.OrderAccessTags)
        {
            if (tags.Contains(tag))
                return true;
        }

        return false;
    }

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
                AndroidOrderType.GoToDepartment or AndroidOrderType.CleanArea =>
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
            case AndroidStatus.OnBreak:
                lines.Add(feedback.StatusReportOnBreak);
                return lines;
            case AndroidStatus.Idle:
                lines.Add(feedback.StatusReportIdle);
                return lines;
        }

        lines.Add(feedback.StatusReportWorking);

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
    private void SpeakLines(EntityUid android, List<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
            _dialogue.Speak(android, AndroidData.Format(lines[i], ("name", Name(android))), 0.5f + 0.8f * i);
    }

    private void CancelBreak(EntityUid uid) => _breaks.EndBreak(uid);
}
