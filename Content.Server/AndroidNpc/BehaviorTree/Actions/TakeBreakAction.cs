// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using System.Linq;
using Content.Server.AndroidNpc.BehaviorTree;
using Content.Server.AndroidNpc.BehaviorTree.Actions;
using Content.Server.AndroidNpc.BehaviorTree.Nodes;
using Content.Server.AndroidNpc.Components;
using Content.Server.AndroidNpc.Data;
using Content.Server.AndroidNpc.Systems;
using Content.Server.AndroidNpc.Utilities;
using Content.Server.NPC.Pathfinding;
using Content.Shared.Buckle;
using Content.Shared.Buckle.Components;
using Content.Shared.CombatMode;
using Content.Shared.Examine;
using Content.Shared.Interaction;
using Content.Shared.Pinpointer;
using Content.Shared.Tag;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.AndroidNpc.BehaviorTree.Actions;

/// <summary>
/// Finds the bar or kitchen (chosen randomly each break), walks to a nearby chair, sits down, and
/// waits out the break placing a food/drink order (see <see cref="AndroidCustomerSystem"/>).
/// If neither venue is reachable, falls back to the android's job resupply point (e.g. janitor closet).
/// All per-entity state is kept on <see cref="AndroidComponent"/> because the tree is shared.
/// </summary>
public sealed class TakeBreakAction : IBehaviorNode
{
    // Larger search radii so androids can find the venue even when it is at the edge of a big station.
    private const float ChairSearchRadius = 12f;
    private const float ResupplyChairSearchRadius = 16f;
    private const float AnchorStandOffRange = 2.5f;
    private const int MaxSeatAttempts = 3;
    private const int MaxStepAsides = 3;
    private const float CrowdedDistance = 0.6f;
    private static readonly TimeSpan FailedSeatMemory = TimeSpan.FromMinutes(2);
    private static readonly ProtoId<TagPrototype> ChairTag = "Chair";
    private static readonly ProtoId<TagPrototype> BarStoolTag = "BarStool";

    private readonly MoveToAction _moveTo;

    public TakeBreakAction()
    {
        // Sit on chairs exactly; stand a little away from other anchors so androids without a seat do not pile up.
        _moveTo = new MoveToAction(
            (owner, blackboard) => GetBreakChairCoordinates(owner, blackboard),
            (owner, blackboard) => blackboard.EntityManager.TryGetComponent<AndroidComponent>(owner, out var android) &&
                                   (android.BreakSpot != null || android.BreakChair is { } seat && blackboard.EntityManager.HasComponent<StrapComponent>(seat))
                ? 0.5f
                : AnchorStandOffRange)
        {
            // Breaks may be across the station, so be patient while the navmesh builds long paths.
            NoPathRetryWindow = 5.0f
        };
    }

    /// <inheritdoc />
    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        if (blackboard.EntityManager.Deleted(owner))
            return BehaviorStatus.Failure;

        if (!blackboard.EntityManager.TryGetComponent<AndroidComponent>(owner, out var android))
            return BehaviorStatus.Failure;

        if (android.BreakState == AndroidBreakState.None)
            android.BreakState = AndroidBreakState.Finding;

        return android.BreakState switch
        {
            AndroidBreakState.Finding => TickFinding(owner, blackboard, android),
            AndroidBreakState.Moving => TickMoving(owner, blackboard, android, frameTime),
            AndroidBreakState.Sitting => TickSitting(owner, blackboard, android),
            AndroidBreakState.Resting => TickResting(owner, blackboard, android),
            _ => BehaviorStatus.Failure
        };
    }

    /// <summary>
    /// The job's break venue, if fixed: a bartender rests in the kitchen and a cook at the bar.
    /// </summary>
    private static AndroidBreakVenue? FixedVenue(AndroidComponent android, AndroidBlackboard blackboard)
    {
        return blackboard.Prototype.AndroidJobData(android.Job)?.BreakVenue;
    }

    private static EntityCoordinates? GetBreakChairCoordinates(EntityUid owner, AndroidBlackboard blackboard)
    {
        if (blackboard.EntityManager.Deleted(owner))
            return null;

        if (!blackboard.EntityManager.TryGetComponent<AndroidComponent>(owner, out var android))
            return null;

        if (android.BreakSpot is { } spot)
            return spot;

        if (android.BreakChair is not { } chair || blackboard.EntityManager.Deleted(chair))
            return null;

        if (!blackboard.EntityManager.TryGetComponent<TransformComponent>(chair, out var xform))
            return null;

        return xform.Coordinates;
    }

    private BehaviorStatus TickFinding(EntityUid owner, AndroidBlackboard blackboard, AndroidComponent android)
    {
        var now = blackboard.Timing.CurTime;
        var config = blackboard.Config;
        var breakDuration = TimeSpan.FromSeconds(config.BreakDurationSeconds);

        // A retry after a failed seat keeps the venue and the break timer.
        if (android.BreakSeatAttempts == 0)
        {
            android.Status = AndroidStatus.OnBreak;
            android.BreakUntil = now + breakDuration;
            android.HasOrderedThisBreak = false;
            android.BreakVenue = FixedVenue(android, blackboard) ??
                                 (blackboard.Random.Prob(0.5f) ? AndroidBreakVenue.Bar : AndroidBreakVenue.Kitchen);
        }

        var (seat, atVenue) = FindBreakSeat(owner, blackboard, android.BreakVenue);
        android.BreakChair = seat;
        android.BreakAtVenue = atVenue;

        // If the preferred venue could not be reached at all, try the other one before
        // giving up and falling back to the resupply desk. A job with a fixed venue never swaps.
        if (seat == null && FixedVenue(android, blackboard) == null)
        {
            android.BreakVenue = android.BreakVenue == AndroidBreakVenue.Bar ? AndroidBreakVenue.Kitchen : AndroidBreakVenue.Bar;
            (seat, atVenue) = FindBreakSeat(owner, blackboard, android.BreakVenue);
            android.BreakChair = seat;
            android.BreakAtVenue = atVenue;
        }

        // Leaving combat mode when resting prevents the android from permanently staring
        // in the direction of its last attack.
        if (blackboard.EntityManager.TryGetComponent<CombatModeComponent>(owner, out var combat) &&
            combat.IsInCombatMode)
        {
            blackboard.EntityManager.System<SharedCombatModeSystem>().SetInCombatMode(owner, false, combat);
        }

        // Without a seat, stand on a free tile of its own near the venue (or where it is) instead of piling up.
        if (android.BreakChair is not { } seatOrAnchor || !blackboard.EntityManager.HasComponent<StrapComponent>(seatOrAnchor))
        {
            var center = blackboard.CoordinatesOf(android.BreakChair) ?? blackboard.CoordinatesOf(owner);
            android.BreakSpot = center is { } c ? AndroidSpots.PickFreeTile(blackboard, owner, c, 3f) : null;
            android.BreakChair = null;
        }

        if (android.BreakChair is { } chair && blackboard.EntityManager.HasComponent<StrapComponent>(chair))
            blackboard.Claims.TryClaim(owner, chair, breakDuration + TimeSpan.FromMinutes(5));

        if (android.BreakChair != null || android.BreakSpot != null)
        {
            android.BreakState = AndroidBreakState.Moving;
            // Reset the shared move action so it targets this android's seat or spot.
            blackboard.Steering.Unregister(owner);
            return BehaviorStatus.Running;
        }

        android.BreakState = AndroidBreakState.Resting;
        return BehaviorStatus.Running;
    }

    private BehaviorStatus TickMoving(EntityUid owner, AndroidBlackboard blackboard, AndroidComponent android, float frameTime)
    {
        if (android.BreakSpot == null && (android.BreakChair == null || blackboard.EntityManager.Deleted(android.BreakChair.Value)))
        {
            if (android.BreakChair is { } abandoned)
                blackboard.Claims.ReleaseClaim(abandoned);

            android.BreakChair = null;
            android.BreakState = AndroidBreakState.Resting;
            return BehaviorStatus.Running;
        }

        var moveStatus = _moveTo.Tick(owner, blackboard, frameTime);

        if (moveStatus == BehaviorStatus.Running)
            return BehaviorStatus.Running;

        if (moveStatus == BehaviorStatus.Failure)
            return Retry(owner, blackboard, android);

        // Only real seats can be sat on; a stand-off spot or anchor means resting where it stands.
        android.BreakState = android.BreakChair is { } target && blackboard.EntityManager.HasComponent<StrapComponent>(target)
            ? AndroidBreakState.Sitting
            : AndroidBreakState.Resting;
        return BehaviorStatus.Running;
    }

    /// <summary>
    /// The seat or spot could not be reached: remember it and try another; after a few failures the android
    /// skips this break and goes back to work rather than resting wherever it got stuck.
    /// </summary>
    private static BehaviorStatus Retry(EntityUid owner, AndroidBlackboard blackboard, AndroidComponent android)
    {
        if (android.BreakChair is { } abandoned)
        {
            blackboard.Claims.ReleaseClaim(abandoned);
            blackboard.Blacklist.RecordFailure(android, abandoned, 1, FailedSeatMemory, blackboard.Timing.CurTime);
        }

        android.BreakChair = null;
        android.BreakSpot = null;
        android.BreakSeatAttempts++;

        if (android.BreakSeatAttempts < MaxSeatAttempts)
        {
            android.BreakState = AndroidBreakState.Finding;
            return BehaviorStatus.Running;
        }

        blackboard.System<AndroidBreakSystem>().EndBreak(owner);
        android.NextBreakAt = blackboard.Timing.CurTime + TimeSpan.FromSeconds(blackboard.Config.BreakIntervalSeconds);
        return BehaviorStatus.Success;
    }

    /// <summary>
    /// Another android stands within <see cref="CrowdedDistance"/> of this one.
    /// </summary>
    private static bool IsCrowded(EntityUid owner, AndroidBlackboard blackboard)
    {
        if (!blackboard.EntityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform))
            return false;

        var position = blackboard.Transform.GetWorldPosition(ownerXform);
        var query = blackboard.EntityManager.EntityQueryEnumerator<AndroidComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out _, out var xform))
        {
            if (uid != owner && xform.MapID == ownerXform.MapID &&
                (blackboard.Transform.GetWorldPosition(xform) - position).Length() < CrowdedDistance)
            {
                return true;
            }
        }

        return false;
    }


    private BehaviorStatus TickSitting(EntityUid owner, AndroidBlackboard blackboard, AndroidComponent android)
    {
        if (android.BreakChair == null || blackboard.EntityManager.Deleted(android.BreakChair.Value))
        {
            if (android.BreakChair is { } abandoned)
                blackboard.Claims.ReleaseClaim(abandoned);

            android.BreakChair = null;
            android.BreakState = AndroidBreakState.Resting;
            return BehaviorStatus.Running;
        }

        var interaction = blackboard.EntityManager.System<SharedInteractionSystem>();
        if (!interaction.InRangeUnobstructed(owner, android.BreakChair.Value, SharedInteractionSystem.InteractionRange))
            return Retry(owner, blackboard, android);

        var buckle = blackboard.EntityManager.System<SharedBuckleSystem>();
        if (buckle.TryBuckle(owner, owner, android.BreakChair.Value))
        {
            android.BreakState = AndroidBreakState.Resting;
            return BehaviorStatus.Running;
        }

        // Someone else took the seat: remember it and look for another one.
        blackboard.Claims.ReleaseClaim(android.BreakChair.Value);
        blackboard.Blacklist.RecordFailure(android, android.BreakChair.Value, 1, FailedSeatMemory, blackboard.Timing.CurTime);
        android.BreakChair = null;
        android.BreakSeatAttempts++;
        android.BreakState = android.BreakSeatAttempts < MaxSeatAttempts ? AndroidBreakState.Finding : AndroidBreakState.Resting;
        return BehaviorStatus.Running;
    }

    private BehaviorStatus TickResting(EntityUid owner, AndroidBlackboard blackboard, AndroidComponent android)
    {
        var now = blackboard.Timing.CurTime;
        var customerSystem = blackboard.EntityManager.System<AndroidCustomerSystem>();

        // Whatever led here, an android resting on its feet never stays on top of another one: it steps aside.
        if (android.BreakSeatAttempts < MaxSeatAttempts + MaxStepAsides &&
            !(blackboard.EntityManager.TryGetComponent<BuckleComponent>(owner, out var buckled) && buckled.Buckled) &&
            IsCrowded(owner, blackboard) &&
            blackboard.CoordinatesOf(owner) is { } here &&
            AndroidSpots.PickFreeTile(blackboard, owner, here, 3f) is { } aside)
        {
            android.BreakSeatAttempts++;
            android.BreakSpot = aside;
            android.BreakState = AndroidBreakState.Moving;
            blackboard.Steering.Unregister(owner);
            return BehaviorStatus.Running;
        }

        // Only place an order if we actually made it to the bar/kitchen, not the resupply fallback.
        if (android.BreakAtVenue && !android.HasOrderedThisBreak)
        {
            android.HasOrderedThisBreak = true;
            customerSystem.StartOrder(owner, android.BreakVenue);
        }

        if (!android.BreakAtVenue)
        {
            var breakSystem = blackboard.EntityManager.System<AndroidBreakSystem>();
            breakSystem.TrySmoke(owner);
        }

        // Keep waiting past the normal break timer while a food/drink order is still active;
        // the order finishes on its own (served or grumpy) and clears itself.
        if (now >= android.BreakUntil && !customerSystem.HasActiveOrder(owner))
        {
            blackboard.System<AndroidBreakSystem>().EndBreak(owner);

            var config = blackboard.Config;
            var breakInterval = TimeSpan.FromSeconds(config.BreakIntervalSeconds);
            android.NextBreakAt = now + breakInterval;

            // After a break, head to a different cardinal part of the station.
            PatrolAction.SetRandomPatrolNudge(owner, blackboard, clearVisited: true);
            return BehaviorStatus.Success;
        }

        return BehaviorStatus.Running;
    }

    /// <summary>
    /// Finds somewhere for the android to take its break at the given venue. Returns the seat/anchor
    /// to walk to, and whether it is an actual venue seat (as opposed to the resupply fallback).
    /// </summary>
    private static (EntityUid? Seat, bool AtVenue) FindBreakSeat(EntityUid owner, AndroidBlackboard blackboard, AndroidBreakVenue venue)
    {
        if (!blackboard.EntityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform))
            return (null, false);

        var ownerGrid = ownerXform.GridUid;
        if (!ownerGrid.HasValue || !blackboard.EntityManager.EntityExists(ownerGrid.Value))
            return (null, false);

        var transform = blackboard.EntityManager.System<SharedTransformSystem>();
        var tagSystem = blackboard.EntityManager.System<TagSystem>();

        blackboard.EntityManager.TryGetComponent<AndroidComponent>(owner, out var android);
        var cachedLocation = venue == AndroidBreakVenue.Kitchen ? android?.KitchenLocation : android?.BarLocation;

        EntityUid? venueBeacon = null;
        EntityCoordinates venueCoords;

        if (cachedLocation is { } cached && cached.IsValid(blackboard.EntityManager))
        {
            venueCoords = cached;
        }
        else if (venue == AndroidBreakVenue.Kitchen)
        {
            // Kitchens don't have their own nav beacon, so anchor on an appliance that is
            // effectively unique to the kitchen instead (dinnerware vendor, ChefDrobe, or a sink).
            if (!KitchenLocator.TryFindKitchenAnchor(blackboard.EntityManager, ownerGrid.Value, ownerXform.Coordinates, out var kitchenAnchor))
                return (FindResupplySeat(owner, blackboard, ownerXform, ownerGrid.Value, transform, tagSystem), false);

            venueBeacon = kitchenAnchor;
            venueCoords = blackboard.EntityManager.GetComponent<TransformComponent>(kitchenAnchor).Coordinates;
            if (android != null)
                android.KitchenLocation = venueCoords;
        }
        else
        {
            var locator = new NavMapBeaconLocator(blackboard.EntityManager);
            if (!locator.TryFindBeacon(ownerXform.Coordinates, "bar", out venueBeacon, out var located))
                return (FindResupplySeat(owner, blackboard, ownerXform, ownerGrid.Value, transform, tagSystem), false);

            venueCoords = located.Value;
            if (android != null)
                android.BarLocation = venueCoords;
        }

        var venuePos = transform.ToMapCoordinates(venueCoords).Position;

        // Only stools in sight of the venue: a seat behind the wall is in maintenance, not at the bar.
        // Regular chairs at a nearby table do not count as being at the bar/kitchen counter.
        var chair = FindChairNear(owner, ownerGrid.Value, venueCoords, ChairSearchRadius, blackboard, tagSystem, BarStoolTag);
        if (chair.HasValue)
            return (chair.Value, true);

        // No free stool: still "arrive" at the venue, standing on a free tile of the room next to its anchor.
        if (venueBeacon.HasValue)
            return (venueBeacon.Value, true);

        // The cached location didn't have a live entity reference (e.g. from a previous break);
        // re-resolve a fresh anchor now so there's still somewhere concrete to stand.
        if (venue == AndroidBreakVenue.Kitchen)
        {
            if (KitchenLocator.TryFindKitchenAnchor(blackboard.EntityManager, ownerGrid.Value, ownerXform.Coordinates, out var freshKitchenAnchor))
                return (freshKitchenAnchor, true);
        }
        else
        {
            var freshLocator = new NavMapBeaconLocator(blackboard.EntityManager);
            if (freshLocator.TryFindBeacon(ownerXform.Coordinates, "bar", out var freshBeacon, out _))
                return (freshBeacon.Value, true);
        }

        return (FindResupplySeat(owner, blackboard, ownerXform, ownerGrid.Value, transform, tagSystem), false);
    }

    private static EntityUid? FindResupplySeat(
        EntityUid owner,
        AndroidBlackboard blackboard,
        TransformComponent ownerXform,
        EntityUid ownerGrid,
        SharedTransformSystem transform,
        TagSystem tagSystem)
    {
        var invSys = blackboard.EntityManager.System<AndroidInventorySystem>();
        var job = invSys.GetJobFor(owner);
        if (job == null)
            return null;

        var locator = new NavMapBeaconLocator(blackboard.EntityManager);
        if (!locator.TryFindResupplyBeacon(ownerXform.Coordinates, job, out var resupplyEntity, out var resupplyCoords))
            return null;

        var resupplyPos = transform.ToMapCoordinates(resupplyCoords.Value).Position;
        var resupplyChair = FindChairNear(owner, ownerGrid, resupplyCoords.Value, ResupplyChairSearchRadius, blackboard, tagSystem, ChairTag);

        // No chair at the resupply point; rest at the desk itself.
        return resupplyChair ?? resupplyEntity;
    }

    private static EntityUid? FindChairNear(
        EntityUid owner,
        EntityUid grid,
        EntityCoordinates anchorCoords,
        float radius,
        AndroidBlackboard blackboard,
        TagSystem tagSystem,
        ProtoId<TagPrototype> requiredTag)
    {
        var examine = blackboard.EntityManager.System<ExamineSystemShared>();
        var anchorMap = blackboard.Transform.ToMapCoordinates(anchorCoords);
        var anchor = anchorMap.Position;
        var transform = blackboard.EntityManager.System<SharedTransformSystem>();
        blackboard.EntityManager.TryGetComponent<AndroidComponent>(owner, out var android);
        EntityUid? bestChair = null;
        var bestChairDist = radius * radius;

        var strapQuery = blackboard.EntityManager.EntityQueryEnumerator<StrapComponent, TagComponent, TransformComponent>();
        while (strapQuery.MoveNext(out var chair, out var strap, out var tag, out var chairXform))
        {
            if (!chairXform.Anchored || !strap.Enabled)
                continue;

            if (chairXform.GridUid != grid)
                continue;

            if (!tagSystem.HasTag(tag, requiredTag) ||
                !examine.InRangeUnOccluded(anchorMap, transform.GetMapCoordinates(chair, chairXform), radius, null))
            {
                continue;
            }

            if (strap.BuckledEntities.Count > 0 ||
                blackboard.Claims.IsClaimedByOther(chair, owner) ||
                android != null && blackboard.Blacklist.IsBlacklisted(android, chair, 1))
            {
                continue;
            }

            var chairPos = transform.GetWorldPosition(chairXform);
            var dist = (chairPos - anchor).LengthSquared();

            if (dist < bestChairDist)
            {
                bestChairDist = dist;
                bestChair = chair;
            }
        }

        return bestChair;
    }

}
