// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.AndroidNpc.BehaviorTree.Actions;
using Content.Server.AndroidNpc.BehaviorTree.Nodes;
using Content.Server.AndroidNpc.Components;
using Content.Server.AndroidNpc.Systems;
using Content.Server.AndroidNpc.Utilities;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.Server.AndroidNpc.BehaviorTree.Trees;

/// <summary>
/// Executes the android's active player order. Shared by every job.
/// </summary>
public static class OrderTree
{
    public static IBehaviorNode Build()
    {
        return new SequenceNode("Order")
        {
            new ConditionNode((owner, bb) => bb.EntityManager.HasComponent<AndroidOrderComponent>(owner)),
            new SelectorNode
            {
                new SequenceNode("FollowMe")
                {
                    IsOrder(AndroidOrderType.FollowMe),
                    new FollowMeAction()
                },
                new SequenceNode("ReturnToWork")
                {
                    IsOrder(AndroidOrderType.ReturnToWork),
                    Clear()
                },
                new SequenceNode("TakeBreak")
                {
                    IsOrder(AndroidOrderType.TakeBreak),
                    new TakeBreakAction(),
                    Clear()
                },
                GoTo("GoToPerson", AndroidOrderType.GoToPerson, GetPersonTarget,
                    (owner, bb) => bb.EntityManager.TryGetComponent<AndroidComponent>(owner, out var android) ? android.PersonalSpace : 1.5f),
                GoTo("GoToDepartment", AndroidOrderType.GoToDepartment, GetDepartmentTarget, (_, _) => 0.5f),
                // Go where a mess was reported, then resume work so the scan picks it up.
                GoTo("CleanArea", AndroidOrderType.CleanArea, GetCleanAreaTarget, (_, _) => 1f)
            }
        };
    }

    private static ConditionNode IsOrder(AndroidOrderType type)
    {
        return new ConditionNode((owner, bb) =>
            bb.EntityManager.TryGetComponent<AndroidOrderComponent>(owner, out var order) && order.OrderType == type);
    }

    private static DoNode Clear() => new((owner, bb) => bb.System<AndroidOrderSystem>().ClearOrder(owner));

    /// <summary>
    /// Walks to the order's target then clears the order; reports to the issuer if the target cannot be found.
    /// </summary>
    private static IBehaviorNode GoTo(
        string name,
        AndroidOrderType type,
        Func<EntityUid, AndroidBlackboard, AndroidOrderComponent, EntityCoordinates?> target,
        Func<EntityUid, AndroidBlackboard, float> range)
    {
        return new SequenceNode(name)
        {
            IsOrder(type),
            new SelectorNode
            {
                new SequenceNode
                {
                    new MoveToAction(
                        (owner, bb) => bb.EntityManager.TryGetComponent<AndroidOrderComponent>(owner, out var order)
                            ? target(owner, bb, order)
                            : null,
                        range),
                    Clear()
                },
                new DoNode((owner, bb) => bb.System<AndroidOrderSystem>().FailOrder(owner, f => f.OrderTargetNotFound))
            }
        };
    }

    private static EntityCoordinates? GetPersonTarget(EntityUid owner, AndroidBlackboard bb, AndroidOrderComponent order)
    {
        if (string.IsNullOrWhiteSpace(order.Target) ||
            !new PersonNameResolver(bb.EntityManager).TryResolvePerson(owner, order.Target, out var person))
        {
            return null;
        }

        return bb.CoordinatesOf(person);
    }

    private static EntityCoordinates? GetDepartmentTarget(EntityUid owner, AndroidBlackboard bb, AndroidOrderComponent order)
    {
        if (string.IsNullOrWhiteSpace(order.Target) || bb.CoordinatesOf(owner) is not { } origin)
            return null;

        var locator = new NavMapBeaconLocator(bb.EntityManager);
        if (locator.TryFindBeacon(origin, order.Target, out var coords))
            return coords;

        // Spoken names such as "l'infirmerie" go through the same location aliases as conversation.
        return bb.System<AndroidDialogueSystem>().Classifier.FindLocation(order.Target) is { } location &&
               locator.TryFindBeacon(origin, location, out coords)
            ? coords
            : null;
    }

    private static EntityCoordinates? GetCleanAreaTarget(EntityUid owner, AndroidBlackboard bb, AndroidOrderComponent order)
    {
        return order.TargetCoordinates is { } coords && coords.IsValid(bb.EntityManager) ? coords : null;
    }
}
