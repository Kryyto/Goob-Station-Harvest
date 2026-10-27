// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.NPC.HTN;
using Content.Shared._Harvest.AdminSquad;
using Robust.Shared.Map;

namespace Content.Server._Harvest.AdminSquad;

/// <summary>
/// A unit following an admin squad order. An HTN NPC has its root task replaced by <c>AdminSquadCompound</c>
/// until the order is released; a mob without AI gets an HTN for the time of the order; an android follows
/// the <c>AdminSquad</c> branch of its behavior tree.
/// </summary>
[RegisterComponent, Access(typeof(AdminSquadSystem))]
public sealed partial class AdminSquadOrderedComponent : Component
{
    /// <summary>
    /// The root task to restore on release, null when the unit had no HTN of its own.
    /// </summary>
    [ViewVariables]
    public HTNCompoundTask? PreviousRootTask;

    /// <summary>
    /// The HTN was added for the order and is removed on release.
    /// </summary>
    [ViewVariables]
    public bool AddedHtn;

    /// <summary>
    /// The unit's own <c>NavSmash</c> blackboard value, restored on release.
    /// </summary>
    [ViewVariables]
    public bool? PreviousNavSmash;

    [ViewVariables]
    public AdminSquadOrder Order;

    [ViewVariables]
    public EntityCoordinates Destination;

    /// <summary>
    /// What the unit is attacking: the target of an attack order, or an obstacle in its way.
    /// </summary>
    [ViewVariables]
    public EntityUid? AttackTarget;

    /// <summary>
    /// <see cref="AttackTarget"/> is an obstacle: once it is down or open the unit goes on with its order.
    /// </summary>
    [ViewVariables]
    public bool AttackingObstacle;

    /// <summary>
    /// The attack target to come back to once the obstacle in the way is down.
    /// </summary>
    [ViewVariables]
    public EntityUid? ResumeAttack;

    /// <summary>
    /// Where the unit was when it last got closer to its goal, to tell when it is stuck.
    /// </summary>
    [ViewVariables]
    public MapCoordinates ProgressPosition;

    [ViewVariables]
    public TimeSpan ProgressTime;
}
