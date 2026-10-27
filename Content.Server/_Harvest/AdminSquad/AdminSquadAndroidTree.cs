// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Server.AndroidNpc.BehaviorTree.Actions;
using Content.Server.AndroidNpc.BehaviorTree.Nodes;
using Robust.Shared.Map;

namespace Content.Server._Harvest.AdminSquad;

/// <summary>
/// The branch an android follows while it has an admin squad order, ahead of everything else in its tree:
/// attack its target, walk to its destination, else stand still.
/// </summary>
public static class AdminSquadAndroidTree
{
    public static IBehaviorNode Build()
    {
        return new SequenceNode("AdminSquad")
        {
            new ConditionNode((owner, bb) => bb.System<AdminSquadSystem>().IsOrdered(owner)),
            new SelectorNode("AdminSquadOrder")
            {
                new SequenceNode("AdminSquadAttack")
                {
                    new ConditionNode((owner, bb) => bb.System<AdminSquadSystem>().GetAttackTarget(owner) != null),
                    new MoveToAction((owner, bb) =>
                            bb.System<AdminSquadSystem>().GetAttackTarget(owner) is { } target
                                ? new EntityCoordinates(target, Vector2.Zero)
                                : null,
                        AdminSquadSystem.AttackRange),
                    new TickNode((owner, bb) =>
                    {
                        bb.System<AdminSquadSystem>().TryMeleeAttack(owner);
                        return BehaviorStatus.Running;
                    }),
                },
                new SequenceNode("AdminSquadMove")
                {
                    new ConditionNode((owner, bb) => bb.System<AdminSquadSystem>().GetMoveGoal(owner) != null),
                    new MoveToAction((owner, bb) => bb.System<AdminSquadSystem>().GetMoveGoal(owner),
                        AdminSquadSystem.ArrivalRange),
                },
                new TickNode((_, _) => BehaviorStatus.Running),
            },
        };
    }
}
