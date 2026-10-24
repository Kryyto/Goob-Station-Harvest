// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Server.AndroidNpc.Components;

/// <summary>
/// Active order issued to an android by a superior.
/// </summary>
public enum AndroidOrderType
{
    FollowMe,
    ReturnToWork,
    GoToDepartment,
    GoToPerson,
    TakeBreak,
    StatusReport,
    RepeatOrder,
    CleanArea,

    /// <summary>
    /// "What can you do?": the android describes its job and the orders it takes.
    /// </summary>
    Capabilities,

    /// <summary>
    /// "What can you serve?": a cook or bartender lists some of its menu.
    /// </summary>
    Menu,

    /// <summary>
    /// "Heal me": a paramedic treats the speaker.
    /// </summary>
    HealMe,

    /// <summary>
    /// "Someone is hurt at the bar": a paramedic goes there.
    /// </summary>
    Injured,

    /// <summary>
    /// A paramedic walking to a reported casualty; it then treats whoever needs it there.
    /// </summary>
    RespondToCall
}

/// <summary>
/// Holds the state of an active order for an android.
/// </summary>
[RegisterComponent]
public sealed partial class AndroidOrderComponent : Component
{
    /// <summary>
    /// Type of the active order.
    /// </summary>
    [DataField("orderType")] public AndroidOrderType OrderType { get; set; }

    /// <summary>
    /// Entity that issued the order.
    /// </summary>
    [DataField("issuer")] public EntityUid Issuer { get; set; }

    /// <summary>
    /// Cached name of the issuer for feedback messages.
    /// </summary>
    [DataField("issuerName")] public string IssuerName { get; set; } = string.Empty;

    /// <summary>
    /// Optional target: department tag, person EntityUid, etc.
    /// </summary>
    [DataField("target")] public string? Target { get; set; }

    /// <summary>
    /// Optional target location, e.g. where a mess was reported for CleanArea.
    /// </summary>
    [ViewVariables] public EntityCoordinates? TargetCoordinates { get; set; }

    /// <summary>
    /// Language the order was given in; reports about it use the same language.
    /// </summary>
    [ViewVariables] public string Language { get; set; } = "en";

    /// <summary>
    /// Game time when the order was issued.
    /// </summary>
    [DataField("issuedAt")] public TimeSpan IssuedAt { get; set; }

    /// <summary>
    /// Game time when the order expires.
    /// </summary>
    [DataField("expiresAt")] public TimeSpan ExpiresAt { get; set; }

    /// <summary>
    /// For FollowMe: when the issuer was last out of range.
    /// </summary>
    [DataField("followLostTime")] public TimeSpan? FollowLostTime { get; set; }

    /// <summary>
    /// Number of consecutive pathfinding failures for this order.
    /// </summary>
    [ViewVariables] public int PathFailures { get; set; }
}
