// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Server.AndroidNpc.Components;

/// <summary>
/// Short-term awareness context for an android.
/// </summary>
[RegisterComponent]
public sealed partial class AndroidMemoryComponent : Component
{
    /// <summary>
    /// Entities the android considers dirty and worth cleaning, sorted by distance.
    /// </summary>
    [ViewVariables]
    public List<EntityUid> KnownDirtyTargets { get; set; } = new();

    /// <summary>
    /// Floor tile coordinates with cleanable decals the android has spotted.
    /// </summary>
    [ViewVariables]
    public List<EntityCoordinates> KnownDirtyTiles { get; set; } = new();

    /// <summary>
    /// Tile the android is currently walking to scrub.
    /// </summary>
    [ViewVariables]
    public EntityCoordinates? CurrentDirtyTile { get; set; }

    /// <summary>
    /// When the android last refilled its supplies.
    /// </summary>
    [ViewVariables] public TimeSpan LastSupplyRefillTime { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// When the android last took a break.
    /// </summary>
    [ViewVariables] public TimeSpan LastBreakTime { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Recent notable interactions for context-aware dialogue.
    /// </summary>
    [ViewVariables]
    public Queue<(EntityUid Source, string Type, TimeSpan Time)> RecentInteractions { get; set; } = new();

    /// <summary>
    /// Current patrol department index.
    /// </summary>
    [ViewVariables] public int PatrolIndex { get; set; } = 0;

    /// <summary>
    /// When the android last scanned the area.
    /// </summary>
    [ViewVariables] public TimeSpan LastScanTime { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Current patrol destination.
    /// </summary>
    [ViewVariables] public EntityCoordinates PatrolTarget { get; set; } = EntityCoordinates.Invalid;

    /// <summary>
    /// A small cardinal directional nudge so the android explores different parts of the station.
    /// </summary>
    [ViewVariables] public Vector2 PatrolDirection { get; set; } = Vector2.Zero;

    /// <summary>
    /// Number of consecutive failed patrol moves. Used to switch to random fallback.
    /// </summary>
    [ViewVariables] public int PatrolFailureCount { get; set; } = 0;

    /// <summary>
    /// Whether the android is in random-walk fallback because the configured patrol route is unreachable.
    /// </summary>
    [ViewVariables] public bool PatrolRandomFallback { get; set; } = false;

    /// <summary>
    /// APCs the janitor has patrolled to recently, with the time of last visit.
    /// </summary>
    [ViewVariables]
    public Dictionary<EntityUid, TimeSpan> VisitedApcs { get; set; } = new();

    /// <summary>
    /// The APC the android is currently walking to during an APC patrol.
    /// </summary>
    [ViewVariables] public EntityUid? CurrentPatrolApc { get; set; } = null;

    /// <summary>
    /// The last status for which the android spoke a contextual line, to avoid repeating it.
    /// </summary>
    [ViewVariables] public AndroidStatus? LastAnnouncedStatus { get; set; }

    /// <summary>
    /// Cigarette the android is currently smoking during a break, if any.
    /// </summary>
    [ViewVariables] public EntityUid? SmokingCigarette { get; set; }
}
