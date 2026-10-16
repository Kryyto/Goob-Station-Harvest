// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;
using Robust.Shared.ViewVariables;

namespace Content.Server.AndroidNpc.Components;

/// <summary>
/// Core marker and behavior state for an android NPC.
/// </summary>
[RegisterComponent]
public sealed partial class AndroidComponent : Component
{
    /// <summary>
    /// Job role assigned to this android.
    /// </summary>
    [DataField("job")] public AndroidJob Job { get; set; } = AndroidJob.Janitor;

    /// <summary>
    /// High-level behavioral status.
    /// </summary>
    [DataField("status")] public AndroidStatus Status { get; set; } = AndroidStatus.Idle;

    /// <summary>
    /// Identifier of the active behavior tree task, if any.
    /// </summary>
    [DataField("currentTaskId")] public string? CurrentTaskId { get; set; }

    /// <summary>
    /// Targets that have repeatedly failed recently, with expiry and failure count.
    /// </summary>
    [ViewVariables]
    public Dictionary<EntityUid, (TimeSpan Expiry, int FailCount)> BlacklistedTargets { get; set; } = new();

    /// <summary>
    /// How full the android's consumables are, from 0 to 1.
    /// </summary>
    [DataField("supplyLevel")] public float SupplyLevel { get; set; } = 1.0f;

    /// <summary>
    /// Time until which the android should remain idle. Used by idle actions.
    /// </summary>
    [ViewVariables] public TimeSpan IdleUntil { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Coordinate the android is fleeing towards, if any.
    /// </summary>
    [ViewVariables] public EntityCoordinates FleeTarget { get; set; } = EntityCoordinates.Invalid;

    /// <summary>
    /// Until this time the android evacuates because an emergency was reported to it.
    /// </summary>
    [ViewVariables] public TimeSpan EmergencyUntil { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Last time the android took a real hit; it flees for a few seconds afterwards.
    /// </summary>
    [ViewVariables] public TimeSpan LastHurtAt { get; set; }

    /// <summary>
    /// Last line the android said, for debugging and tests.
    /// </summary>
    [ViewVariables] public string? LastSaid { get; set; }

    /// <summary>
    /// The last few lines the android said, oldest first.
    /// </summary>
    [ViewVariables] public List<string> RecentLines { get; } = new();

    /// <summary>
    /// When the behavior tree runs next, and when it last ran (for its frame time).
    /// </summary>
    [ViewVariables] public TimeSpan NextTreeTickAt { get; set; }

    [ViewVariables] public TimeSpan LastTreeTickAt { get; set; }

    /// <summary>
    /// Cached work spot of a service android (bar or kitchen), refreshed at <see cref="WorkSpotRefreshAt"/>.
    /// </summary>
    [ViewVariables] public EntityCoordinates? WorkSpot { get; set; }

    [ViewVariables] public TimeSpan WorkSpotRefreshAt { get; set; }

    /// <summary>
    /// Tile a service android stands on while tending its venue, changed every so often to look busy.
    /// </summary>
    [ViewVariables] public EntityCoordinates? TendSpot { get; set; }

    [ViewVariables] public TimeSpan NextTendMoveAt { get; set; }

    /// <summary>
    /// Cryostorage the android walks to while leaving.
    /// </summary>
    [ViewVariables] public EntityCoordinates? LeaveTarget { get; set; }

    /// <summary>
    /// The android is removed at this time even if it never reached cryostorage.
    /// </summary>
    [ViewVariables] public TimeSpan LeaveDeadline { get; set; }

    /// <summary>
    /// Behavior tree trace of the last tick, recorded only while an admin inspects the android.
    /// </summary>
    [ViewVariables] public List<string>? Trace { get; set; }

    /// <summary>
    /// Preferred distance in tiles to keep from people when following or interacting with them.
    /// Set to a random value between 1 and 3 at initialization.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    [DataField("personalSpace")]
    public float PersonalSpace { get; set; } = 1.5f;

    /// <summary>
    /// Earliest time at which the android is allowed to say another random idle line.
    /// </summary>
    [ViewVariables]
    public TimeSpan NextChatterTime { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Current phase of an ordered micro-break. Stored on the component because the tree is shared.
    /// </summary>
    [ViewVariables]
    public AndroidBreakState BreakState { get; set; } = AndroidBreakState.None;

    /// <summary>
    /// Time at which the current micro-break should end.
    /// </summary>
    [ViewVariables]
    public TimeSpan BreakUntil { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Chair the android has chosen to sit on during its break, if any.
    /// </summary>
    [ViewVariables]
    public EntityUid? BreakChair { get; set; }

    /// <summary>
    /// Seats tried this break; after a few failures the android rests where it stands.
    /// </summary>
    [ViewVariables]
    public int BreakSeatAttempts { get; set; }

    /// <summary>
    /// Free tile the android stands on during a break without a seat, kept apart from other androids.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? BreakSpot { get; set; }

    /// <summary>
    /// Earliest time at which the android is allowed to take its next automatic micro-break.
    /// </summary>
    [ViewVariables]
    public TimeSpan NextBreakAt { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Coordinates of the bar beacon on the current grid, cached so the android does not need to
    /// rediscover it every time it goes on break.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates BarLocation { get; set; } = EntityCoordinates.Invalid;

    /// <summary>
    /// Coordinates of the kitchen beacon on the current grid, cached the same way as <see cref="BarLocation"/>.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates KitchenLocation { get; set; } = EntityCoordinates.Invalid;

    /// <summary>
    /// Which venue the android picked for its current/last break. Chosen randomly each break.
    /// </summary>
    [ViewVariables]
    public AndroidBreakVenue BreakVenue { get; set; } = AndroidBreakVenue.Bar;

    /// <summary>
    /// Whether the android has already placed its bartender/cook order for the current break.
    /// Prevents re-ordering every tick while resting.
    /// </summary>
    [ViewVariables]
    public bool HasOrderedThisBreak { get; set; }

    /// <summary>
    /// Whether <see cref="BreakChair"/> is an actual seat/anchor at the chosen <see cref="BreakVenue"/>,
    /// as opposed to a fallback resupply desk. Only breaks taken at the venue place an order.
    /// </summary>
    [ViewVariables]
    public bool BreakAtVenue { get; set; }

    /// <summary>
    /// Earliest time at which each behavior branch is allowed to be re-entered after a failure.
    /// This prevents the android from getting stuck re-trying the same failing task every tick.
    /// </summary>
    [ViewVariables]
    public Dictionary<string, TimeSpan> BranchCooldowns { get; set; } = new();
}

/// <summary>
/// Phases of an android's micro-break.
/// </summary>
public enum AndroidBreakState
{
    None,
    Finding,
    Moving,
    Sitting,
    Resting
}

/// <summary>
/// Which service venue an android's break is taking place at.
/// </summary>
public enum AndroidBreakVenue
{
    Bar,
    Kitchen
}
