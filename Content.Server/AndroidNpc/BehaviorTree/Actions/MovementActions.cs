// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Numerics;
using System;
using Content.Server.AndroidNpc.BehaviorTree.Nodes;
using Content.Server.AndroidNpc.Components;
using Content.Server.AndroidNpc.Data;
using Content.Server.AndroidNpc.Systems;
using Content.Server.AndroidNpc.Utilities;
using Content.Server.Atmos.Components;
using Content.Server.Atmos.EntitySystems;
using Content.Server.NPC.Pathfinding;
using Content.Server.Power.Components;
using Content.Shared.Atmos;
using Content.Shared.Climbing.Components;
using Content.Shared.Mobs.Components;
using Content.Shared.Physics;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Components;
using Robust.Shared.Map;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Physics;

namespace Content.Server.AndroidNpc.BehaviorTree.Actions;

/// <summary>
/// Moves the android to the current tool target.
/// </summary>
public sealed class MoveToTargetAction : IBehaviorNode
{
    private readonly MoveToAction _moveTo;

    /// <summary>
    /// Creates a move action that targets <see cref="AndroidInventoryComponent.CurrentToolTarget"/>.
    /// </summary>
    public MoveToTargetAction(float range = 1.0f)
    {
        _moveTo = new MoveToAction((owner, blackboard) =>
        {
            if (!blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
                return null;

            if (!inventory.CurrentToolTarget.HasValue || blackboard.EntityManager.Deleted(inventory.CurrentToolTarget.Value))
                return null;

            if (!blackboard.EntityManager.TryGetComponent<TransformComponent>(inventory.CurrentToolTarget.Value, out var targetXform))
                return null;

            return targetXform.Coordinates;
        }, range);
    }

    /// <inheritdoc />
    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        var status = _moveTo.Tick(owner, blackboard, frameTime);

        if (status == BehaviorStatus.Failure &&
            blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory) &&
            inventory.CurrentToolTarget is { } target)
        {
            RecordFailure(owner, blackboard, inventory, target);
        }

        return status;
    }

    private static void RecordFailure(EntityUid owner, AndroidBlackboard blackboard, AndroidInventoryComponent inventory, EntityUid target)
    {
        blackboard.Log.Debug($"[MoveToTarget] Recording failure for target {target}");

        if (!blackboard.EntityManager.TryGetComponent<AndroidComponent>(owner, out var android))
            return;

        var config = blackboard.Config;
        var threshold = config.BlacklistThreshold;
        var ttl = TimeSpan.FromSeconds(config.BlacklistTtlSeconds);

        blackboard.Blacklist.RecordFailure(android, target, threshold, ttl, blackboard.Timing.CurTime);
        blackboard.Claims.ReleaseClaim(target);

        if (inventory.CurrentToolTarget == target)
            inventory.CurrentToolTarget = null;
    }

}

/// <summary>
/// Follows the active order's issuer, giving up if they are lost for too long.
/// </summary>
public sealed class FollowMeAction : IBehaviorNode
{
    private readonly MoveToAction _moveTo;

    public FollowMeAction()
    {
        _moveTo = new MoveToAction((owner, blackboard) =>
        {
            if (!blackboard.EntityManager.TryGetComponent<AndroidOrderComponent>(owner, out var order))
                return null;

            if (!order.Issuer.IsValid() || blackboard.EntityManager.Deleted(order.Issuer))
                return null;

            if (!blackboard.EntityManager.TryGetComponent<TransformComponent>(order.Issuer, out var targetXform))
                return null;

            return targetXform.Coordinates;
        }, (owner, blackboard) =>
        {
            if (blackboard.EntityManager.TryGetComponent<AndroidComponent>(owner, out var android))
                return android.PersonalSpace;

            return 1.5f;
        });
    }

    /// <inheritdoc />
    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        if (!blackboard.EntityManager.TryGetComponent<AndroidOrderComponent>(owner, out var order))
            return BehaviorStatus.Failure;

        if (!order.Issuer.IsValid() || blackboard.EntityManager.Deleted(order.Issuer))
        {
            GiveUp(owner, blackboard, order, lost: true);
            return BehaviorStatus.Failure;
        }

        if (!blackboard.EntityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform) ||
            !blackboard.EntityManager.TryGetComponent<TransformComponent>(order.Issuer, out var issuerXform))
        {
            GiveUp(owner, blackboard, order, lost: false);
            return BehaviorStatus.Failure;
        }

        var distance = (blackboard.Transform.GetWorldPosition(issuerXform) - blackboard.Transform.GetWorldPosition(ownerXform)).Length();
        var config = blackboard.Config;
        var lostDistance = config.FollowLostDistance;
        var lostTimeout = TimeSpan.FromSeconds(config.FollowLostTimeoutSeconds);

        if (distance > lostDistance)
        {
            // The issuer is far away. Start or continue the lost timer.
            order.FollowLostTime ??= blackboard.Timing.CurTime;

            if (blackboard.Timing.CurTime - order.FollowLostTime >= lostTimeout)
            {
                GiveUp(owner, blackboard, order, lost: true);
                return BehaviorStatus.Failure;
            }
        }
        else
        {
            // The issuer is back in range; reset the lost timer.
            order.FollowLostTime = null;
        }

        var status = _moveTo.Tick(owner, blackboard, frameTime);

        if (status == BehaviorStatus.Success)
            return BehaviorStatus.Running;

        if (status == BehaviorStatus.Failure)
        {
            order.PathFailures++;

            if (order.PathFailures >= 3)
            {
                GiveUp(owner, blackboard, order, lost: false);
                return BehaviorStatus.Failure;
            }

            return BehaviorStatus.Running;
        }

        return status;
    }

    private static void GiveUp(EntityUid owner, AndroidBlackboard blackboard, AndroidOrderComponent order, bool lost)
    {
        blackboard.System<AndroidOrderSystem>().FailOrder(owner, f => lost ? f.OrderLost : f.OrderUnreachable);
    }
}

/// <summary>
/// Picks a point away from the android and moves there, avoiding space and dangerous atmos.
/// </summary>
public sealed class FleeAction : IBehaviorNode
{
    private readonly MoveToAction _moveTo;
    private readonly AtmosphereSystem _atmosphere;
    private readonly SharedMapSystem _mapSystem;
    private readonly SharedPhysicsSystem _physics;
    private readonly IEntityManager _entityManager;

    /// <summary>
    /// Creates a flee action that prefers safe tiles while moving away from danger.
    /// </summary>
    public FleeAction()
    {
        _atmosphere = EntitySystem.Get<AtmosphereSystem>();
        _mapSystem = EntitySystem.Get<SharedMapSystem>();
        _physics = EntitySystem.Get<SharedPhysicsSystem>();
        _entityManager = IoCManager.Resolve<IEntityManager>();

        _moveTo = new MoveToAction((owner, blackboard) =>
        {
            if (!_entityManager.TryGetComponent<AndroidComponent>(owner, out var android))
                return null;

            if (!_entityManager.TryGetComponent<TransformComponent>(owner, out var xform))
                return null;

            if (android.FleeTarget.IsValid(_entityManager) && !android.FleeTarget.Equals(EntityCoordinates.Invalid))
                return android.FleeTarget;

            var target = PickSafeFleePoint(owner, xform, blackboard);
            android.FleeTarget = target ?? xform.Coordinates;
            return android.FleeTarget;
        });
    }

    /// <inheritdoc />
    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        var status = _moveTo.Tick(owner, blackboard, frameTime);

        if (status == BehaviorStatus.Success)
        {
            if (_entityManager.TryGetComponent<AndroidComponent>(owner, out var android))
                android.FleeTarget = EntityCoordinates.Invalid;

            return BehaviorStatus.Success;
        }

        return status;
    }

    private EntityCoordinates? PickSafeFleePoint(EntityUid owner, TransformComponent xform, AndroidBlackboard blackboard)
    {
        var gridUid = xform.GridUid;
        var mapId = xform.MapID;

        if (gridUid is null && !TryGetMapEntity(mapId, out _))
            return null;

        var transform = _entityManager.System<SharedTransformSystem>();
        var ownerWorld = transform.ToMapCoordinates(xform.Coordinates).Position;
        var actualGrid = gridUid ?? xform.ParentUid;

        // Rotate the sampled directions by a random offset so different androids
        // and different flee attempts don't always prefer the same orientation.
        var baseAngle = blackboard.Random.NextFloat() * MathF.Tau;

        // Cast rays in several directions and pick the first open, safe tile.
        // This prevents fleeing straight into walls or room corners.
        for (var distance = 8f; distance >= 1f; distance -= 1f)
        {
            for (var i = 0; i < 12; i++)
            {
                var angle = baseAngle + MathF.Tau * i / 12f;
                var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                var candidateWorld = ownerWorld + dir * distance;

                var ray = new CollisionRay(ownerWorld, dir, (int) CollisionGroup.MobMask);

                bool Ignored(EntityUid hit)
                {
                    // Other mobs and climbable furniture don't count as walls for this check.
                    return hit == owner ||
                           _entityManager.HasComponent<MobStateComponent>(hit) ||
                           _entityManager.HasComponent<ClimbableComponent>(hit);
                }

                if (_physics.IntersectRayWithPredicate(mapId, ray, distance, Ignored, true).Any())
                    continue;

                var candidate = transform.ToCoordinates(
                    new Entity<TransformComponent?>(actualGrid, null),
                    new MapCoordinates(candidateWorld, mapId));

                if (!candidate.IsValid(_entityManager))
                    continue;

                if (gridUid is not null && !IsTileSafe(gridUid.Value, mapId, candidate))
                    continue;

                return candidate;
            }
        }

        // If no safe distant tile was found, at least stay where we are instead
        // of running into space or superheated atmos.
        return null;
    }

    private bool IsTileSafe(EntityUid gridUid, MapId mapId, EntityCoordinates coordinates)
    {
        Entity<MapAtmosphereComponent?>? mapAtmos = null;

        if (_mapSystem.TryGetMap(mapId, out var mapUid) && mapUid is { } map)
        {
            _entityManager.TryGetComponent(map, out MapAtmosphereComponent? mapComp);
            mapAtmos = new Entity<MapAtmosphereComponent?>(map, mapComp);
        }

        if (!_entityManager.TryGetComponent<MapGridComponent>(gridUid, out var mapGrid))
            return false;

        var tile = _mapSystem.CoordinatesToTile(gridUid, mapGrid, coordinates);

        _entityManager.TryGetComponent(gridUid, out GridAtmosphereComponent? gridComp);
        var gridAtmos = new Entity<GridAtmosphereComponent?>(gridUid, gridComp);

        if (_atmosphere.IsTileSpace(gridAtmos, mapAtmos, tile))
            return false;

        var mixture = _atmosphere.GetTileMixture(gridAtmos, mapAtmos, tile);
        if (mixture is null)
            return false;

        return _atmosphere.IsMixtureProbablySafe(mixture);
    }

    private bool TryGetMapEntity(MapId mapId, out EntityUid? mapUid)
    {
        return _mapSystem.TryGetMap(mapId, out mapUid);
    }
}

/// <summary>
/// Cycles through the android's configured department patrol route using station nav beacons.
/// If the route is not reachable it falls back to a random nearby walk so the android never idles.
/// </summary>
public sealed class PatrolAction : IBehaviorNode
{
    private const int MaxRouteFailures = 3;
    private const int MaxRandomFailures = 3;
    private const float RandomPatrolRange = 32f;
    private const float RandomPatrolMinRange = 12f;
    private const float ApcPatrolNudge = 24f;
    private const float MinApcPatrolDistance = 24f;

    private readonly MoveToAction _moveTo;

    public PatrolAction()
    {
        _moveTo = new MoveToAction(
            GetNextPatrolTarget,
            0.5f);
    }

    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        if (!blackboard.EntityManager.TryGetComponent<AndroidMemoryComponent>(owner, out var memory) ||
            !blackboard.EntityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform))
        {
            return BehaviorStatus.Running;
        }

        // Refresh the target if we don't have one.
        if (!memory.PatrolTarget.IsValid(blackboard.EntityManager) ||
            memory.PatrolTarget.Equals(EntityCoordinates.Invalid))
        {
            var target = GetNextPatrolTarget(owner, blackboard);
            memory.PatrolTarget = target ?? EntityCoordinates.Invalid;

            if (!memory.PatrolTarget.IsValid(blackboard.EntityManager))
                return BehaviorStatus.Running;
        }

        var status = _moveTo.Tick(owner, blackboard, frameTime);

        if (status == BehaviorStatus.Success)
        {
            // Reached this waypoint; clear it so the next tick picks another.
            if (memory.CurrentPatrolApc is { } apc && blackboard.EntityManager.EntityExists(apc))
            {
                memory.VisitedApcs[apc] = blackboard.Timing.CurTime;
                memory.CurrentPatrolApc = null;
            }

            memory.PatrolTarget = EntityCoordinates.Invalid;
            memory.PatrolFailureCount = 0;
            return BehaviorStatus.Running;
        }

        if (status == BehaviorStatus.Failure)
        {
            memory.PatrolTarget = EntityCoordinates.Invalid;
            memory.PatrolFailureCount++;

            if (memory.PatrolRandomFallback)
            {
                // Random point failed; try the configured route again after a few attempts.
                if (memory.PatrolFailureCount >= MaxRandomFailures)
                {
                    memory.PatrolRandomFallback = false;
                    memory.PatrolFailureCount = 0;
                }
            }
            else
            {
                // Route point failed; move to the next department. After a few consecutive failures
                // give up on the route and switch to random walk.
                if (memory.PatrolFailureCount >= MaxRouteFailures)
                {
                    memory.PatrolRandomFallback = true;
                    memory.PatrolFailureCount = 0;
                }
                else
                {
                    memory.PatrolIndex = (memory.PatrolIndex + 1) % Math.Max(1, GetPatrolRouteCount(owner, blackboard));
                }
            }

            return BehaviorStatus.Running;
        }

        return status;
    }

    private static EntityCoordinates? GetNextPatrolTarget(EntityUid owner, AndroidBlackboard blackboard)
    {
        if (!blackboard.EntityManager.TryGetComponent<AndroidMemoryComponent>(owner, out var memory))
            return null;

        if (!blackboard.EntityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform))
            return null;

        if (memory.PatrolTarget.IsValid(blackboard.EntityManager) && !memory.PatrolTarget.Equals(EntityCoordinates.Invalid))
            return memory.PatrolTarget;

        if (memory.PatrolRandomFallback)
            return GetRandomWalkTarget(owner, ownerXform, blackboard);

        // Prefer patrolling to APCs so the android actually covers the station.
        var apcTarget = GetApcPatrolTarget(owner, ownerXform, memory, blackboard);
        if (apcTarget is { } apc)
            return apc;

        var job = blackboard.EntityManager.System<AndroidInventorySystem>().GetJobFor(owner);
        if (job == null || job.PatrolRoute.Count == 0)
            return GetRandomWalkTarget(owner, ownerXform, blackboard);

        var locator = new NavMapBeaconLocator(blackboard.EntityManager);

        // Try each route entry once, wrapping around, to find a reachable department.
        for (var i = 0; i < job.PatrolRoute.Count; i++)
        {
            var routeIndex = (memory.PatrolIndex + i) % job.PatrolRoute.Count;
            var destination = job.PatrolRoute[routeIndex];

            if (locator.TryFindBeacon(ownerXform.Coordinates, destination, out var coords))
            {
                memory.PatrolIndex = (routeIndex + 1) % job.PatrolRoute.Count;
                return coords.Value;
            }
        }

        // No beacon on the route is currently available; wander nearby instead.
        return GetRandomWalkTarget(owner, ownerXform, blackboard);
    }

    private static EntityCoordinates? GetApcPatrolTarget(EntityUid owner, TransformComponent ownerXform, AndroidMemoryComponent memory, AndroidBlackboard blackboard)
    {
        if (ownerXform.GridUid is not { Valid: true } gridUid)
            return null;

        var ownerPos = blackboard.Transform.GetWorldPosition(ownerXform);
        var apcs = new List<(EntityUid Uid, float Distance, EntityCoordinates Coords)>();

        var query = blackboard.EntityManager.AllEntityQueryEnumerator<ApcComponent, TransformComponent>();
        while (query.MoveNext(out var apc, out _, out var xform))
        {
            if (blackboard.EntityManager.Deleted(apc) ||
                xform.GridUid != gridUid ||
                xform.ParentUid != gridUid)
            {
                continue;
            }

            var apcPos = blackboard.Transform.GetWorldPosition(xform);
            var distance = (apcPos - ownerPos).Length();
            apcs.Add((apc, distance, xform.Coordinates));
        }

        if (apcs.Count == 0)
            return null;

        // Apply a strong directional nudge so androids explore different cardinal
        // directions after resupplying, breaking, or on spawn rather than ping-ponging
        // between the nearest APCs.
        if (memory.PatrolDirection != Vector2.Zero)
        {
            apcs.Sort((a, b) =>
            {
                var aPos = blackboard.Transform.GetWorldPosition(blackboard.EntityManager.GetComponent<TransformComponent>(a.Uid));
                var bPos = blackboard.Transform.GetWorldPosition(blackboard.EntityManager.GetComponent<TransformComponent>(b.Uid));

                var aDir = aPos - ownerPos;
                var bDir = bPos - ownerPos;

                if (aDir.LengthSquared() < 0.0001f)
                    aDir = Vector2.Zero;
                else
                    aDir = Vector2.Normalize(aDir);

                if (bDir.LengthSquared() < 0.0001f)
                    bDir = Vector2.Zero;
                else
                    bDir = Vector2.Normalize(bDir);

                var aScore = a.Distance - Vector2.Dot(aDir, memory.PatrolDirection) * ApcPatrolNudge;
                var bScore = b.Distance - Vector2.Dot(bDir, memory.PatrolDirection) * ApcPatrolNudge;

                return aScore.CompareTo(bScore);
            });
        }
        else
        {
            apcs.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        }

        var now = blackboard.Timing.CurTime;
        var cooldown = TimeSpan.FromSeconds(45);

        // Prefer APCs at least MinApcPatrolDistance away so the android crosses into other areas
        // instead of orbiting the same few local APCs.
        EntityCoordinates? farCandidate = null;
        EntityUid? farApc = null;

        foreach (var (uid, dist, coords) in apcs)
        {
            if (memory.VisitedApcs.TryGetValue(uid, out var lastVisit) && now - lastVisit < cooldown)
                continue;

            if (dist >= MinApcPatrolDistance)
            {
                farCandidate = coords;
                farApc = uid;
                break;
            }
        }

        if (farCandidate.HasValue)
        {
            memory.CurrentPatrolApc = farApc;
            return farCandidate.Value;
        }

        foreach (var (uid, _, coords) in apcs)
        {
            if (memory.VisitedApcs.TryGetValue(uid, out var lastVisit) && now - lastVisit < cooldown)
                continue;

            memory.CurrentPatrolApc = uid;
            return coords;
        }

        // Every APC has been visited recently; clear the history and start again with the best one.
        memory.VisitedApcs.Clear();
        memory.CurrentPatrolApc = apcs[0].Uid;
        return apcs[0].Coords;
    }

    private static EntityCoordinates? GetRandomWalkTarget(EntityUid owner, TransformComponent ownerXform, AndroidBlackboard blackboard)
    {
        if (ownerXform.GridUid is not { Valid: true } gridUid)
            return null;

        var pathfinding = blackboard.EntityManager.System<PathfindingSystem>();
        var ownerGridCoords = blackboard.Transform.ToCoordinates(
            new Entity<TransformComponent?>(gridUid, null),
            blackboard.Transform.GetMapCoordinates(ownerXform));

        for (var attempts = 0; attempts < 16; attempts++)
        {
            var offset = blackboard.Random.NextVector2(RandomPatrolMinRange, RandomPatrolRange);

            var target = ownerGridCoords.Offset(offset);
            if (!target.IsValid(blackboard.EntityManager))
                continue;

            // Pick a point the pathfinder actually knows about; otherwise the android will just
            // bounce off walls forever.
            if (pathfinding.GetPoly(target) == null)
                continue;

            return target;
        }

        // Fallback to a nearby point even if unverified, so the action can keep running.
        var finalOffset = blackboard.Random.NextVector2(1f, RandomPatrolRange);

        return ownerGridCoords.Offset(finalOffset);
    }

    private static int GetPatrolRouteCount(EntityUid owner, AndroidBlackboard blackboard)
    {
        var job = blackboard.EntityManager.System<AndroidInventorySystem>().GetJobFor(owner);
        return job?.PatrolRoute.Count ?? 0;
    }

    /// <summary>
    /// Gives the android a fresh random cardinal nudge and a new route index.
    /// Called on spawn, after resupply, and after a break.
    /// </summary>
    public static void SetRandomPatrolNudge(EntityUid owner, AndroidBlackboard blackboard, bool clearVisited = false)
    {
        if (!blackboard.EntityManager.TryGetComponent<AndroidMemoryComponent>(owner, out var memory))
            return;

        var routeCount = GetPatrolRouteCount(owner, blackboard);
        memory.PatrolIndex = routeCount > 0 ? blackboard.Random.Next(routeCount) : 0;

        var dir = blackboard.Random.Next(4);
        memory.PatrolDirection = dir switch
        {
            0 => new Vector2(1, 0),
            1 => new Vector2(-1, 0),
            2 => new Vector2(0, 1),
            _ => new Vector2(0, -1)
        };

        if (clearVisited)
            memory.VisitedApcs.Clear();
    }
}
