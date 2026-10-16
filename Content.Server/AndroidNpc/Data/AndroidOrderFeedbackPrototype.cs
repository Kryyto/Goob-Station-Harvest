// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Prototypes;

namespace Content.Server.AndroidNpc.Data;

/// <summary>
/// Template messages the android uses when acknowledging or failing orders, for one language.
/// The prototype id is the language code ("en", "fr").
/// </summary>
[Prototype]
public sealed partial class AndroidOrderFeedbackPrototype : IPrototype
{
    /// <inheritdoc />
    [IdDataField] public string ID { get; private set; } = default!;

    /// <summary>
    /// Acknowledgement prefix, e.g. "Acknowledged.".
    /// </summary>
    [DataField("acknowledgementPrefix")] public string AcknowledgementPrefix { get; private set; } = "Acknowledged.";

    /// <summary>
    /// Response when the issuer is not authorized.
    /// </summary>
    [DataField("notAuthorized")] public string NotAuthorized { get; private set; } = "Sorry, I do not take orders from you.";

    /// <summary>
    /// Response when the issuer has no valid job/id.
    /// </summary>
    [DataField("noAuthority")] public string NoAuthority { get; private set; } = "No authority.";

    /// <summary>
    /// Acknowledgement for a FollowMe order.
    /// </summary>
    [DataField("ackFollowMe")] public string AckFollowMe { get; private set; } = "Following.";

    /// <summary>
    /// Acknowledgement for a ReturnToWork order.
    /// </summary>
    [DataField("ackReturnToWork")] public string AckReturnToWork { get; private set; } = "Returning to work.";

    /// <summary>
    /// Acknowledgement for a GoToDepartment order.
    /// </summary>
    [DataField("ackGoToDepartment")] public string AckGoToDepartment { get; private set; } = "Heading to {target}.";

    /// <summary>
    /// Acknowledgement for a GoToPerson order.
    /// </summary>
    [DataField("ackGoToPerson")] public string AckGoToPerson { get; private set; } = "On my way.";

    /// <summary>
    /// Acknowledgement for a TakeBreak order.
    /// </summary>
    [DataField("ackTakeBreak")] public string AckTakeBreak { get; private set; } = "Taking a break.";

    /// <summary>
    /// Fallback acknowledgement for unknown order types.
    /// </summary>
    [DataField("ackDefault")] public string AckDefault { get; private set; } = "Understood.";

    /// <summary>
    /// Placeholder target used when none was extracted.
    /// </summary>
    [DataField("defaultTarget")] public string DefaultTarget { get; private set; } = "the requested area";

    /// <summary>
    /// Failure feedback when an order expires.
    /// </summary>
    [DataField("orderExpired")] public string OrderExpired { get; private set; } = "{name}: Order from {issuer} has expired; returning to work.";

    /// <summary>
    /// Failure feedback when the issuer is lost during FollowMe.
    /// </summary>
    [DataField("orderLost")] public string OrderLost { get; private set; } = "{name}: I lost {issuer}.";

    /// <summary>
    /// Failure feedback when an order target cannot be located.
    /// </summary>
    [DataField("orderTargetNotFound")] public string OrderTargetNotFound { get; private set; } = "{name}: I cannot locate {target}.";

    /// <summary>
    /// Failure feedback when a destination is unreachable.
    /// </summary>
    [DataField("orderUnreachable")] public string OrderUnreachable { get; private set; } = "{name}: Destination unreachable.";

    /// <summary>
    /// Response for "what are you doing" when idle.
    /// </summary>
    [DataField("statusReportIdle")] public string StatusReportIdle { get; private set; } = "I am currently idle.";

    /// <summary>
    /// Response for "what are you doing" when working with no specific sub-task.
    /// </summary>
    [DataField("statusReportWorking")] public string StatusReportWorking { get; private set; } = "I am working.";

    /// <summary>
    /// Response for "what are you doing" when patrolling.
    /// </summary>
    [DataField("statusReportPatrolling")] public string StatusReportPatrolling { get; private set; } = "I am patrolling.";

    /// <summary>
    /// Response for "what are you doing" when cleaning a puddle.
    /// </summary>
    [DataField("statusReportCleaningPuddle")] public string StatusReportCleaningPuddle { get; private set; } = "I am cleaning a puddle.";

    /// <summary>
    /// Response for "what are you doing" when picking up trash.
    /// </summary>
    [DataField("statusReportCleaningTrash")] public string StatusReportCleaningTrash { get; private set; } = "I am picking up trash.";

    /// <summary>
    /// Response for "what are you doing" when fleeing.
    /// </summary>
    [DataField("statusReportFleeing")] public string StatusReportFleeing { get; private set; } = "I am fleeing from danger.";

    /// <summary>
    /// Response for "what are you doing" when on a break.
    /// </summary>
    [DataField("statusReportOnBreak")] public string StatusReportOnBreak { get; private set; } = "I am on a break.";

    /// <summary>
    /// Response for "what are you doing" when following someone.
    /// </summary>
    [DataField("statusReportFollowing")] public string StatusReportFollowing { get; private set; } = "I am following {issuer}.";

    /// <summary>
    /// Response for "what are you doing" when going to a department.
    /// </summary>
    [DataField("statusReportGoingToDepartment")] public string StatusReportGoingToDepartment { get; private set; } = "I am going to {target}.";

    /// <summary>
    /// Response for "what are you doing" when going to a person.
    /// </summary>
    [DataField("statusReportGoingToPerson")] public string StatusReportGoingToPerson { get; private set; } = "I am going to {target}.";

    /// <summary>
    /// Fallback response for "what are you doing" when the android's state is unclear.
    /// </summary>
    [DataField("statusReportUnknown")] public string StatusReportUnknown { get; private set; } = "I am currently active.";

    /// <summary>
    /// Response when asked to restate a bar/kitchen order but the android doesn't have one active.
    /// </summary>
    [DataField("noActiveCustomerOrder")] public string NoActiveCustomerOrder { get; private set; } = "I don't have an order right now.";

    [DataField("statusReportCannotWork")] public string StatusReportCannotWork { get; private set; } = "I cannot continue working right now.";

    [DataField("statusReportKeepDistance")] public string StatusReportKeepDistance { get; private set; } = "I will keep my distance.";

    [DataField("statusReportNoTask")] public string StatusReportNoTask { get; private set; } = "No active task.";

    [DataField("statusReportServing")] public string StatusReportServing { get; private set; } = "I am preparing an order for {target}.";

    [DataField("statusReportLeaving")] public string StatusReportLeaving { get; private set; } = "My shift is over, I am heading to cryostorage.";

    /// <summary>
    /// Shown when examining an android: how to talk to it. {name} is its first name.
    /// </summary>
    [DataField("examineHint")] public string ExamineHint { get; private set; } = "To talk to it, start with its first name: \"{name}, follow me\".";

    [DataField("forcedOrderIssuer")] public string ForcedOrderIssuer { get; private set; } = "Central Command";

    /// <summary>
    /// Lines the android says as a customer at the bar or kitchen. {0} is the item name, or the tip for thanks.
    /// </summary>
    [DataField("customer")] public AndroidCustomerLines Customer { get; private set; } = new();
}

/// <summary>
/// Voice lines of an android customer on break.
/// </summary>
[DataDefinition]
public sealed partial class AndroidCustomerLines
{
    [DataField("barOrder")] public List<string> BarOrder { get; private set; } = new();

    [DataField("kitchenOrder")] public List<string> KitchenOrder { get; private set; } = new();

    [DataField("barReminder")] public List<string> BarReminder { get; private set; } = new();

    [DataField("kitchenReminder")] public List<string> KitchenReminder { get; private set; } = new();

    [DataField("grumpy")] public List<string> Grumpy { get; private set; } = new();

    [DataField("thanks")] public List<string> Thanks { get; private set; } = new();
}
