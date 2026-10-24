// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.AndroidNpc.BehaviorTree.Actions;
using Content.Server.AndroidNpc.BehaviorTree.Nodes;
using Content.Server.AndroidNpc.Components;
using Content.Server.AndroidNpc.Systems;
using Content.Server.AndroidNpc.Utilities;

namespace Content.Server.AndroidNpc.BehaviorTree.Trees;

/// <summary>
/// Work branches of the paramedic android: rescue patients, restock its kits at medbay, take breaks, patrol.
/// </summary>
public static class MedicTree
{
    private const string ResupplyBranch = "Resupply";

    public static IEnumerable<IBehaviorNode> Branches()
    {
        yield return new SequenceNode("Rescue")
        {
            new ConditionNode((owner, bb) => bb.System<AndroidMedicSystem>().SelectPatient(owner) != null),
            new SelectorNode
            {
                // Critical patients and bodies go to medbay once stabilized.
                new SequenceNode("Carry")
                {
                    new ConditionNode((owner, bb) => bb.System<AndroidMedicSystem>().MustCarry(owner)),
                    new SelectorNode
                    {
                        new TickNode((owner, bb) => bb.System<AndroidMedicSystem>().StartCarry(owner)),
                        // Walk up to the patient; the pull starts on a later tick, never carry nothing to medbay.
                        new SequenceNode
                        {
                            new MoveToAction((owner, bb) => bb.System<AndroidMedicSystem>().PatientCoordinates(owner), 1f),
                            new ConditionNode((_, _) => false)
                        }
                    },
                    new MoveToAction((owner, bb) => bb.System<AndroidMedicSystem>().CarryCoordinates(owner), 1.2f,
                        (owner, bb) => Drop(owner, bb)),
                    new DoNode((owner, bb) => bb.System<AndroidMedicSystem>().FinishCarry(owner))
                },
                new SelectorNode("Care")
                {
                    new TickNode((owner, bb) => bb.System<AndroidMedicSystem>().Care(owner)),
                    new MoveToAction((owner, bb) => bb.System<AndroidMedicSystem>().PatientCoordinates(owner), 1f, Drop)
                }
            }
        };

        yield return new SequenceNode(ResupplyBranch)
        {
            new ConditionNode((owner, bb) =>
                !bb.IsBranchOnCooldown(ResupplyBranch) &&
                bb.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory) &&
                bb.System<AndroidInventorySystem>().NeedsResupply(owner, inventory)),
            new MoveToAction(
                (owner, bb) => bb.CoordinatesOf(owner) is { } origin &&
                               bb.System<AndroidInventorySystem>().GetJobFor(owner) is { } job &&
                               new NavMapBeaconLocator(bb.EntityManager).TryFindResupplyBeacon(origin, job, out var coords)
                    ? coords
                    : null,
                0.5f,
                (_, bb) => bb.SetBranchCooldown(ResupplyBranch)),
            new RestockAtAction()
        };

        yield return AndroidTrees.TakeBreak();

        yield return new SequenceNode("Patrol")
        {
            new PatrolAction()
        };
    }

    private static void Drop(EntityUid owner, AndroidBlackboard bb)
    {
        if (bb.EntityManager.TryGetComponent<AndroidMedicComponent>(owner, out var medic))
            bb.System<AndroidMedicSystem>().DropPatient(owner, medic);
    }
}
