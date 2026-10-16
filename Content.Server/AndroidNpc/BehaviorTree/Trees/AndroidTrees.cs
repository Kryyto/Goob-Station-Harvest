// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.AndroidNpc.BehaviorTree.Actions;
using Content.Server.AndroidNpc.BehaviorTree.Nodes;
using Content.Server.AndroidNpc.Components;
using Content.Server.AndroidNpc.Systems;

namespace Content.Server.AndroidNpc.BehaviorTree.Trees;

/// <summary>
/// Assembles the behavior tree of a job: the shared high-priority branches (leaving for cryostorage,
/// self-preservation, player orders) followed by the job's own work branches.
/// </summary>
public static class AndroidTrees
{
    public static IBehaviorNode Build(AndroidJob job)
    {
        var root = new SelectorNode(job.ToString())
        {
            new SequenceNode("LeaveForCryo")
            {
                new ConditionNode((owner, bb) => bb.EntityManager.TryGetComponent<AndroidComponent>(owner, out var android) &&
                                                 android.Status == AndroidStatus.Leaving),
                new MoveToAction((owner, bb) => bb.EntityManager.GetComponent<AndroidComponent>(owner).LeaveTarget, 1f,
                    (owner, bb) => bb.System<AndroidLifecycleSystem>().FinishLeaving(owner)),
                new DoNode((owner, bb) => bb.System<AndroidLifecycleSystem>().FinishLeaving(owner))
            },
            new SequenceNode("Flee")
            {
                new ConditionNode((owner, bb) => bb.EntityManager.TryGetComponent<AndroidComponent>(owner, out var android) &&
                                                 android.Status == AndroidStatus.Fleeing),
                new FleeAction()
            },
            OrderTree.Build()
        };

        var branches = job switch
        {
            AndroidJob.Janitor => JanitorTree.Branches(),
            _ => ServiceTree.Branches(),
        };

        foreach (var branch in branches)
            root.Add(branch);

        return root;
    }

    /// <summary>
    /// Automatic break every BreakIntervalSeconds, unless the android is busy with an order or fleeing.
    /// An ongoing break keeps the branch active so its state machine can finish.
    /// </summary>
    public static IBehaviorNode TakeBreak()
    {
        return new SequenceNode("Break")
        {
            new ConditionNode((owner, bb) =>
                bb.EntityManager.TryGetComponent<AndroidComponent>(owner, out var android) &&
                !bb.EntityManager.HasComponent<AndroidOrderComponent>(owner) &&
                android.Status != AndroidStatus.Fleeing &&
                (android.BreakState != AndroidBreakState.None || bb.Timing.CurTime >= android.NextBreakAt)),
            new TakeBreakAction()
        };
    }
}
