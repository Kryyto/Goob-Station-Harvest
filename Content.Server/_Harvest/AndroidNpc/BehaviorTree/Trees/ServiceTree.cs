// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server._Harvest.AndroidNpc.BehaviorTree.Actions;
using Content.Server._Harvest.AndroidNpc.BehaviorTree.Nodes;
using Content.Server._Harvest.AndroidNpc.Components;
using Content.Server._Harvest.AndroidNpc.Systems;
using Content.Server._Harvest.AndroidNpc.Utilities;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Random;

namespace Content.Server._Harvest.AndroidNpc.BehaviorTree.Trees;

/// <summary>
/// Work branches of the cook and bartender androids: serve pending orders, take breaks, stay at the venue.
/// </summary>
public static class ServiceTree
{
    private const float TendRange = 4f;
    private const float MinTendSeconds = 10f;
    private const float MaxTendSeconds = 20f;

    public static IEnumerable<IBehaviorNode> Branches()
    {
        // Customers come before breaks.
        yield return new SequenceNode("Serve")
        {
            new ConditionNode((owner, bb) => bb.System<AndroidServiceSystem>().GetServeCoordinates(owner) != null),
            new MoveToAction((owner, bb) => bb.System<AndroidServiceSystem>().GetServeCoordinates(owner), 1.5f),
            new TickNode((owner, bb) => bb.System<AndroidServiceSystem>().TickPreparation(owner))
        };

        // Money customers left on the venue's tables.
        yield return new SequenceNode("CollectTips")
        {
            new ConditionNode((owner, bb) => bb.System<AndroidServiceSystem>().GetTipCoordinates(owner, GetWorkSpot(owner, bb)) != null),
            new MoveToAction((owner, bb) => bb.System<AndroidServiceSystem>().GetTipCoordinates(owner, GetWorkSpot(owner, bb)), 1.3f),
            new TickNode((owner, bb) => bb.System<AndroidServiceSystem>().TryCollectTip(owner))
        };

        yield return AndroidTrees.TakeBreak();

        // Walk to a different free tile of the venue every so often, never stopping in a doorway.
        yield return new SequenceNode("Tend")
        {
            new ConditionNode(UpdateTendSpot),
            new MoveToAction((owner, bb) => bb.EntityManager.GetComponent<AndroidComponent>(owner).TendSpot, 0.4f,
                (owner, bb) => bb.EntityManager.GetComponent<AndroidComponent>(owner).TendSpot = null)
        };
    }

    private static bool UpdateTendSpot(EntityUid owner, AndroidBlackboard bb)
    {
        if (!bb.EntityManager.TryGetComponent<AndroidComponent>(owner, out var android) || GetWorkSpot(owner, bb) is not { } workSpot)
            return false;

        var now = bb.Timing.CurTime;
        var inDoorway = bb.CoordinatesOf(owner) is { } here && AndroidSpots.IsOnDoor(bb, here);

        if (android.TendSpot == null || now >= android.NextTendMoveAt || inDoorway)
        {
            android.TendSpot = AndroidSpots.PickFreeTile(bb, owner, workSpot, TendRange);
            android.NextTendMoveAt = now + TimeSpan.FromSeconds(bb.Random.NextFloat(MinTendSeconds, MaxTendSeconds));
            bb.Steering.Unregister(owner);
        }

        return android.TendSpot != null;
    }

    private static readonly TimeSpan WorkSpotRefresh = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The job's work spot, cached for a minute since finding it scans the whole station.
    /// </summary>
    private static EntityCoordinates? GetWorkSpot(EntityUid owner, AndroidBlackboard bb)
    {
        if (!bb.EntityManager.TryGetComponent<AndroidComponent>(owner, out var android))
            return null;

        if (bb.Timing.CurTime < android.WorkSpotRefreshAt)
            return android.WorkSpot;

        android.WorkSpot = FindWorkSpot(owner, bb, android);
        android.WorkSpotRefreshAt = bb.Timing.CurTime + WorkSpotRefresh;
        return android.WorkSpot;
    }

    /// <summary>
    /// The job's work beacon; kitchens usually have none, so "kitchen" falls back to the kitchen appliances.
    /// </summary>
    private static EntityCoordinates? FindWorkSpot(EntityUid owner, AndroidBlackboard bb, AndroidComponent android)
    {
        if (bb.Prototype.AndroidJobData(android.Job)?.WorkBeacon is not { } beacon ||
            !bb.EntityManager.TryGetComponent<TransformComponent>(owner, out var xform))
        {
            return null;
        }

        if (new NavMapBeaconLocator(bb.EntityManager).TryFindBeacon(xform.Coordinates, beacon, out var coords))
            return coords;

        if (beacon == "kitchen" && xform.GridUid is { } grid &&
            KitchenLocator.TryFindKitchenAnchor(bb.EntityManager, grid, xform.Coordinates, out var anchor))
        {
            return bb.CoordinatesOf(anchor);
        }

        return null;
    }
}
