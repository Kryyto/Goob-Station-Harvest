// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Server.AndroidNpc.Components;

/// <summary>
/// Tracks the tools an android is holding and needs for its job.
/// </summary>
[RegisterComponent]
public sealed partial class AndroidInventoryComponent : Component
{
    /// <summary>
    /// Prototype IDs required to be owned by the android.
    /// </summary>
    [DataField("requiredLoadout")] public List<string> RequiredLoadout { get; set; } = new();

    /// <summary>
    /// Tools found missing by a full inventory rescan, and when to look again.
    /// </summary>
    [ViewVariables]
    public Dictionary<string, TimeSpan> MissingToolRecheckAt { get; } = new();

    /// <summary>
    /// Items owned by the android, such as mop, trash bag and wet floor signs.
    /// </summary>
    [ViewVariables]
    public List<EntityUid> OwnedItems { get; set; } = new();

    /// <summary>
    /// Item currently in the active hand, if any.
    /// </summary>
    [ViewVariables]
    public EntityUid? EquippedItem { get; set; }

    /// <summary>
    /// The work tool that was equipped before a temporary tool (e.g. flashlight) was swapped in.
    /// Used to restore the active tool after a one-off toggle.
    /// </summary>
    [ViewVariables]
    public EntityUid? PreviousEquippedItem { get; set; }

    /// <summary>
    /// Target the android is currently using an item on, if any.
    /// </summary>
    [ViewVariables]
    public EntityUid? CurrentToolTarget { get; set; }

    /// <summary>
    /// Whether the current tool use has been started.
    /// </summary>
    [ViewVariables]
    public bool ToolUseStarted { get; set; }

    /// <summary>
    /// Whether the last cleaned target was a liquid mess.
    /// </summary>
    [ViewVariables]
    public bool LastTargetWasLiquid { get; set; }

    /// <summary>
    /// Wet floor signs this android has placed and should pick back up once the puddle is gone.
    /// </summary>
    [ViewVariables]
    public List<EntityUid> PlacedSigns { get; set; } = new();

    /// <summary>
    /// Sink, drain, water source or disposal unit the android is currently walking to in order to
    /// service one of its tools.
    /// </summary>
    [ViewVariables]
    public EntityUid? ServiceTarget { get; set; }

    /// <summary>
    /// What the android intends to do once it reaches <see cref="ServiceTarget"/>.
    /// </summary>
    [ViewVariables]
    public AndroidServiceKind ServiceKind { get; set; } = AndroidServiceKind.None;

    /// <summary>
    /// Position the android should move to after priming a cleaner grenade so it does not stand in the foam.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? CleanadeFleeTarget { get; set; }

    /// <summary>
    /// How long the android should avoid the area it just cleaned with a grenade.
    /// </summary>
    [ViewVariables]
    public TimeSpan? CleanadeFleeUntil { get; set; }

    /// <summary>
    /// Charger the android is walking to in order to refill its flashlight.
    /// </summary>
    [ViewVariables]
    public EntityUid? FlashlightCharger { get; set; }

    /// <summary>
    /// Power cell removed from the flashlight while it sits in the charger.
    /// </summary>
    [ViewVariables]
    public EntityUid? FlashlightCell { get; set; }

    /// <summary>
    /// Current step of the flashlight recharge process.
    /// </summary>
    [ViewVariables]
    public FlashlightChargeStage FlashlightChargeStage { get; set; } = FlashlightChargeStage.None;

    /// <summary>
    /// A pest (e.g., a mouse) the android is currently hunting.
    /// </summary>
    [ViewVariables]
    public EntityUid? PestTarget { get; set; }

    /// <summary>
    /// When the current pest target was chosen; a chase that lasts too long is abandoned.
    /// </summary>
    [ViewVariables]
    public TimeSpan PestTargetSince { get; set; }

    /// <summary>
    /// Kitchen appliance the android will drag a dead pest to for disposal.
    /// </summary>
    [ViewVariables]
    public EntityUid? PestDisposalTarget { get; set; }

    /// <summary>
    /// The last pest the android shouted at while attacking, so it does not spam exclamations.
    /// </summary>
    [ViewVariables]
    public EntityUid? LastPestAttackShoutTarget { get; set; }

    /// <summary>
    /// When the android last shouted while attacking.
    /// </summary>
    [ViewVariables]
    public TimeSpan LastPestAttackShoutTime { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Cached walkable coordinates for the current tool target, so we do not re-resolve the same
    /// tile every tick.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? CachedToolTargetCoords { get; set; }

    /// <summary>
    /// When the cached tool target coordinates were last computed.
    /// </summary>
    [ViewVariables]
    public TimeSpan CachedToolTargetCoordsTime { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Last cached raw target coordinates that produced the cached tool target coordinates.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? CachedToolTargetRawCoords { get; set; }

    /// <summary>
    /// Cached walkable coordinates for the current service target.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? CachedServiceTargetCoords { get; set; }

    [ViewVariables]
    public TimeSpan CachedServiceTargetCoordsTime { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Cached walkable coordinates for the current pest disposal target.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? CachedPestDisposalCoords { get; set; }

    [ViewVariables]
    public TimeSpan CachedPestDisposalCoordsTime { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Cached coordinates for the current pest target.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? CachedPestTargetCoords { get; set; }

    [ViewVariables]
    public TimeSpan CachedPestTargetCoordsTime { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Cached coordinates for the flashlight charger.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? CachedFlashlightChargerCoords { get; set; }

    [ViewVariables]
    public TimeSpan CachedFlashlightChargerCoordsTime { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Generic cache for the last resolved walkable target. Avoids recomputing the same BFS every tick.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? ResolvedTargetRaw { get; set; }

    [ViewVariables]
    public EntityCoordinates? ResolvedTargetCoords { get; set; }

    [ViewVariables]
    public Vector2 ResolvedTargetOwnerPos { get; set; } = Vector2.Zero;

    [ViewVariables]
    public TimeSpan ResolvedTargetTime { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Number of consecutive NoPath ticks for the current move; used to avoid giving up instantly.
    /// </summary>
    [ViewVariables]
    public int ConsecutiveNoPathCount { get; set; }

    [ViewVariables]
    public TimeSpan NoPathStartTime { get; set; } = TimeSpan.Zero;

    [ViewVariables]
    public TimeSpan NoPathLastReregisterTime { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Last time the android was seen making path progress. Used to detect stuck movement.
    /// </summary>
    [ViewVariables]
    public TimeSpan LastPathProgressTime { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// World position the android was at the last time it made path progress.
    /// </summary>
    [ViewVariables]
    public Vector2 LastPathProgressPosition { get; set; } = Vector2.Zero;
}

/// <summary>
/// Steps of refilling a flashlight's power cell in a charger.
/// </summary>
public enum FlashlightChargeStage : byte
{
    /// <summary>
    /// Not currently recharging.
    /// </summary>
    None,

    /// <summary>
    /// The cell was removed from the flashlight and is being carried to the charger.
    /// </summary>
    CellInHand,

    /// <summary>
    /// The cell has been inserted into the charger.
    /// </summary>
    CellInCharger,

    /// <summary>
    /// The cell has been removed from the charger and is being carried back to the flashlight.
    /// </summary>
    CellRemoved,

    /// <summary>
    /// The cell has been reinserted into the flashlight.
    /// </summary>
    Done
}

/// <summary>
/// The kind of tool maintenance an android is travelling to perform.
/// </summary>
public enum AndroidServiceKind : byte
{
    /// <summary>
    /// Nothing needs servicing.
    /// </summary>
    None,

    /// <summary>
    /// The bucket needs its dirty water dumped and/or fresh water added.
    /// </summary>
    Bucket,

    /// <summary>
    /// The trash bag needs to be emptied into a disposal unit.
    /// </summary>
    Trash,
}
