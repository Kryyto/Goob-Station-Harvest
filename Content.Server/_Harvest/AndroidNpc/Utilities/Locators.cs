// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Linq;
using System.Text.RegularExpressions;
using Content.Server._Harvest.AndroidNpc.Components;
using Content.Server._Harvest.AndroidNpc.Data;
using Content.Server._Harvest.AndroidNpc.BehaviorTree;
using Content.Server.NPC.Pathfinding;
using Content.Shared.Access;
using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.NPC;
using Content.Shared.StationRecords;
using Robust.Shared.Prototypes;
using Content.Shared.Humanoid;
using Content.Shared.Pinpointer;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Map;
using Robust.Shared.Random;
using Robust.Shared.Map.Components;

namespace Content.Server._Harvest.AndroidNpc.Utilities;

/// <summary>
/// Resolves department names and patrol route entries to actual station <see cref="NavMapBeaconComponent"/> coordinates.
/// </summary>
public sealed class NavMapBeaconLocator
{
    private readonly IEntityManager _entityManager;

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hallways"] = "station-beacon-service",
        ["medical"] = "station-beacon-medical",
        ["science"] = "station-beacon-science",
        ["engineering"] = "station-beacon-engineering",
        ["service"] = "station-beacon-service",
        ["command"] = "station-beacon-command",
        ["security"] = "station-beacon-security",
        ["supply"] = "station-beacon-supply",
        ["cargo"] = "station-beacon-cargo",
        ["cargo bay"] = "station-beacon-cargo-bay",
        ["janitor"] = "station-beacon-janitor",
        ["janitor's closet"] = "station-beacon-janitor",
        ["maintenance"] = "station-beacon-janitor",
        ["robotics"] = "station-beacon-robotics",
        ["kitchen"] = "station-beacon-kitchen",
        ["bar"] = "station-beacon-bar",
        ["botany"] = "station-beacon-botany",
        ["hydroponics"] = "station-beacon-botany",
        ["medbay"] = "station-beacon-medbay",
        ["chemistry"] = "station-beacon-chemistry",
        ["atmospherics"] = "station-beacon-atmos",
        ["tool storage"] = "station-beacon-tools",
        ["dormitories"] = "station-beacon-dorms",
        ["dorms"] = "station-beacon-dorms",
        ["library"] = "station-beacon-library",
        ["chapel"] = "station-beacon-chapel",
        ["theater"] = "station-beacon-theater",
        ["evac"] = "station-beacon-evac",
        ["arrivals"] = "station-beacon-arrivals",
    };

    public NavMapBeaconLocator(IEntityManager entityManager)
    {
        _entityManager = entityManager;
    }

    /// <summary>
    /// Tries to find the nearest enabled nav beacon that matches the given query.
    /// The query may be a station beacon key, a department alias, or a substring of the beacon's visible text.
    /// </summary>
    public bool TryFindBeacon(
        EntityCoordinates origin,
        string? query,
        [NotNullWhen(true)] out EntityCoordinates? coordinates)
    {
        return TryFindBeacon(origin, query, out _, out coordinates);
    }

    /// <summary>
    /// Tries to find the nearest enabled nav beacon that matches the given query, returning both
    /// the beacon entity and its coordinates.
    /// </summary>
    public bool TryFindBeacon(
        EntityCoordinates origin,
        string? query,
        [NotNullWhen(true)] out EntityUid? entity,
        [NotNullWhen(true)] out EntityCoordinates? coordinates)
    {
        entity = null;
        coordinates = null;

        if (string.IsNullOrWhiteSpace(query))
            return false;

        var normalized = query.Trim();
        var beaconKey = ResolveBeaconKey(normalized);

        if (!TryGetOriginGrid(origin, out var originGridUid))
            return false;

        EntityUid? best = null;
        var bestDist = float.PositiveInfinity;

        var queryAll = _entityManager.AllEntityQueryEnumerator<NavMapBeaconComponent, TransformComponent>();
        while (queryAll.MoveNext(out var uid, out var beacon, out var xform))
        {
            if (!beacon.Enabled || _entityManager.Deleted(uid))
                continue;

            // Only consider beacons on the same grid as the android.
            if (xform.GridUid != originGridUid)
                continue;

            if (!Matches(beacon, normalized, beaconKey))
                continue;

            var distance = xform.Coordinates.Position - origin.Position;
            var distSquared = distance.LengthSquared();

            if (distSquared < bestDist)
            {
                best = uid;
                bestDist = distSquared;
            }
        }

        if (best is not { } bestUid)
            return false;

        entity = bestUid;
        coordinates = _entityManager.GetComponent<TransformComponent>(bestUid).Coordinates;
        return true;
    }

    /// <summary>
    /// Finds the place a message names: a known alias ("l'infirmerie" is medical) or any beacon's map name.
    /// <paramref name="name"/> is the beacon's name as shown on the station map.
    /// </summary>
    public bool TryFindPlace(
        EntityCoordinates origin,
        string text,
        AndroidIntentClassifier classifier,
        [NotNullWhen(true)] out string? name,
        [NotNullWhen(true)] out EntityCoordinates? coordinates)
    {
        if (classifier.FindLocation(text) is { } location && TryFindBeacon(origin, location, out var beacon, out coordinates))
        {
            var nav = _entityManager.GetComponent<NavMapBeaconComponent>(beacon.Value);
            name = nav.Text ?? (nav.DefaultText is { } locId ? Loc.GetString(locId) : location);
            return true;
        }

        return TryFindBeaconInText(origin, text, out name, out coordinates);
    }

    /// <summary>
    /// Finds a place by the name of its beacon as shown on the station map ("il y a un blessé en chimie",
    /// "go to atmos"): the longest beacon name the text contains as whole words, on the android's grid.
    /// </summary>
    public bool TryFindBeaconInText(
        EntityCoordinates origin,
        string text,
        [NotNullWhen(true)] out string? name,
        [NotNullWhen(true)] out EntityCoordinates? coordinates)
    {
        name = null;
        coordinates = null;

        if (!TryGetOriginGrid(origin, out var originGridUid))
            return false;

        var folded = $" {string.Join(' ', Regex.Split(AndroidIntentClassifier.Fold(text), @"[^\p{L}\p{N}]+"))} ";
        var bestLength = 0;
        var bestDist = float.MaxValue;
        var query = _entityManager.AllEntityQueryEnumerator<NavMapBeaconComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var beacon, out var xform))
        {
            if (!beacon.Enabled || xform.GridUid != originGridUid)
                continue;

            var label = beacon.Text ?? (beacon.DefaultText is { } locId ? Loc.GetString(locId) : null);
            if (string.IsNullOrWhiteSpace(label))
                continue;

            var key = string.Join(' ', Regex.Split(AndroidIntentClassifier.Fold(label), @"[^\p{L}\p{N}]+").Where(t => t.Length > 0));
            if (key.Length < 3 || key.Length < bestLength || !folded.Contains($" {key} ", StringComparison.Ordinal))
                continue;

            var dist = (xform.Coordinates.Position - origin.Position).LengthSquared();
            if (key.Length == bestLength && dist >= bestDist)
                continue;

            bestLength = key.Length;
            bestDist = dist;
            name = label;
            coordinates = xform.Coordinates;
        }

        return name != null;
    }

    /// <summary>
    /// Returns the visible name of the nearest enabled beacon on the same grid within <paramref name="maxRange"/>.
    /// </summary>
    public bool TryFindNearestBeaconName(
        EntityCoordinates origin,
        float maxRange,
        [NotNullWhen(true)] out string? name)
    {
        name = null;

        if (!TryGetOriginGrid(origin, out var originGridUid))
            return false;

        var bestDist = maxRange * maxRange;
        var query = _entityManager.AllEntityQueryEnumerator<NavMapBeaconComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var beacon, out var xform))
        {
            if (!beacon.Enabled || _entityManager.Deleted(uid) || xform.GridUid != originGridUid)
                continue;

            var text = beacon.Text ?? (beacon.DefaultText is { } locId ? Loc.GetString(locId) : null);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var distSquared = (xform.Coordinates.Position - origin.Position).LengthSquared();
            if (distSquared > bestDist)
                continue;

            bestDist = distSquared;
            name = text;
        }

        return name != null;
    }

    /// <summary>
    /// Tries to find the nearest nav beacon for the android's job resupply point.
    /// </summary>
    public bool TryFindResupplyBeacon(
        EntityCoordinates origin,
        AndroidJobPrototype job,
        [NotNullWhen(true)] out EntityCoordinates? coordinates)
    {
        return TryFindResupplyBeacon(origin, job, out _, out coordinates);
    }

    /// <summary>
    /// Tries to find the nearest nav beacon for the android's job resupply point,
    /// returning both the beacon entity and its coordinates.
    /// </summary>
    public bool TryFindResupplyBeacon(
        EntityCoordinates origin,
        AndroidJobPrototype job,
        [NotNullWhen(true)] out EntityUid? entity,
        [NotNullWhen(true)] out EntityCoordinates? coordinates)
    {
        entity = null;
        coordinates = null;

        var resupplyKey = job.ResupplyBeacon;

        if (string.IsNullOrWhiteSpace(resupplyKey))
            return false;

        return TryFindBeacon(origin, resupplyKey, out entity, out coordinates);
    }

    private static string ResolveBeaconKey(string query)
    {
        if (Aliases.TryGetValue(query, out var alias))
            return alias;

        return query;
    }

    private static bool Matches(NavMapBeaconComponent beacon, string query, string beaconKey)
    {
        if (beacon.DefaultText is { } defaultText &&
            NameMatcher.ContainsIgnoreCaseAndAccents(defaultText.Id, beaconKey))
        {
            return true;
        }

        if (beacon.Text is { } text &&
            NameMatcher.ContainsIgnoreCaseAndAccents(text, query))
        {
            return true;
        }

        return false;
    }

    private bool TryGetOriginGrid(EntityCoordinates origin, out EntityUid originGridUid)
    {
        originGridUid = origin.EntityId;

        if (!_entityManager.EntityExists(originGridUid))
            return false;

        return true;
    }
}

/// <summary>
/// Finds a reasonable "kitchen" anchor point on a grid. Most station kitchens don't have their own
/// nav beacon (unlike the bar), so instead this looks for appliances that are effectively unique to
/// kitchens: the dinnerware vending machine or the chef's uniform vendor (ChefDrobe). As a last
/// resort it falls back to the nearest plain sink, since those also show up in other rooms.
/// </summary>
public static class KitchenLocator
{
    private static readonly string[] PrimaryAnchorPrototypes =
    {
        "VendingMachineDinnerware",
        "VendingMachineChefDrobe",
    };

    private static readonly string[] FallbackAnchorPrototypes =
    {
        "Sink",
        "SinkWide",
        "SinkStemless",
    };

    /// <summary>
    /// Tries to find the nearest kitchen anchor entity on the given grid, relative to the given origin.
    /// </summary>
    public static bool TryFindKitchenAnchor(IEntityManager entityManager, EntityUid grid, EntityCoordinates origin, out EntityUid anchor)
    {
        var transform = entityManager.System<SharedTransformSystem>();
        var originPos = transform.ToMapCoordinates(origin).Position;

        if (TryFindNearest(entityManager, transform, grid, originPos, PrimaryAnchorPrototypes, out anchor))
            return true;

        return TryFindNearest(entityManager, transform, grid, originPos, FallbackAnchorPrototypes, out anchor);
    }

    private static bool TryFindNearest(
        IEntityManager entityManager,
        SharedTransformSystem transform,
        EntityUid grid,
        Vector2 originPos,
        string[] prototypes,
        out EntityUid anchor)
    {
        anchor = default;
        var bestDist = float.MaxValue;
        var found = false;

        var query = entityManager.EntityQueryEnumerator<MetaDataComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var meta, out var xform))
        {
            if (xform.GridUid != grid)
                continue;

            var protoId = meta.EntityPrototype?.ID;
            if (protoId == null)
                continue;

            var matches = false;
            foreach (var candidate in prototypes)
            {
                if (protoId == candidate)
                {
                    matches = true;
                    break;
                }
            }

            if (!matches)
                continue;

            var dist = (transform.GetWorldPosition(xform) - originPos).LengthSquared();
            if (dist >= bestDist)
                continue;

            bestDist = dist;
            anchor = uid;
            found = true;
        }

        return found;
    }
}

/// <summary>
/// Resolves a spoken person name or description to an in-game humanoid entity.
/// </summary>
public sealed class PersonNameResolver
{
    private readonly IEntityManager _entityManager;

    public PersonNameResolver(IEntityManager entityManager)
    {
        _entityManager = entityManager;
    }

    /// <summary>
    /// Tries to find a person matching <paramref name="target"/>.
    /// Matches the entity's display name, an android's first/full name, or a substring.
    /// </summary>
    public bool TryResolvePerson(
        EntityUid origin,
        string? target,
        [NotNullWhen(true)] out EntityUid? person)
    {
        person = null;

        if (string.IsNullOrWhiteSpace(target))
            return false;

        var normalized = target.Trim();

        // First pass: exact name match.
        var query = _entityManager.AllEntityQueryEnumerator<HumanoidAppearanceComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out _))
        {
            if (uid == origin)
                continue;

            if (MatchesExactly(uid, normalized))
            {
                person = uid;
                return true;
            }
        }

        // Second pass: substring match.
        query = _entityManager.AllEntityQueryEnumerator<HumanoidAppearanceComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out _, out _))
        {
            if (uid == origin)
                continue;

            if (MatchesSubString(uid, normalized))
            {
                person = uid;
                return true;
            }
        }

        return false;
    }

    private bool MatchesExactly(EntityUid uid, string target)
    {
        var name = _entityManager.GetComponent<MetaDataComponent>(uid).EntityName;

        if (NameMatcher.EqualsIgnoreCaseAndAccents(name, target))
            return true;

        if (_entityManager.TryGetComponent<AndroidIdentityComponent>(uid, out var identity))
        {
            if (NameMatcher.EqualsIgnoreCaseAndAccents(identity.FullDisplayName, target))
                return true;

            if (NameMatcher.EqualsIgnoreCaseAndAccents(identity.FirstName, target))
                return true;
        }

        return false;
    }

    private bool MatchesSubString(EntityUid uid, string target)
    {
        var name = _entityManager.GetComponent<MetaDataComponent>(uid).EntityName;
        return NameMatcher.ContainsIgnoreCaseAndAccents(name, target);
    }
}

/// <summary>
/// Picks floor tiles for androids to stand on: inside the same room as a center point (the search never
/// crosses a door), never on a door tile, and away from other androids.
/// </summary>
public static class AndroidSpots
{
    private const float MinSpacing = 0.9f;
    private const int MaxVisited = 200;

    /// <summary>
    /// Free tiles reachable from the point, walking through doors the android can open, up to a distance.
    /// </summary>
    public static List<EntityCoordinates> ReachableTiles(AndroidBlackboard blackboard, EntityUid owner, EntityCoordinates center, float maxDistance, int maxVisited)
    {
        var result = new List<EntityCoordinates>();
        if (FindStartPoly(blackboard, center) is not { } start ||
            !blackboard.EntityManager.TryGetComponent<MapGridComponent>(start.GraphUid, out var grid))
        {
            return result;
        }

        var mapSys = blackboard.EntityManager.System<SharedMapSystem>();
        var access = blackboard.EntityManager.System<AccessReaderSystem>();
        var tags = access.FindAccessTags(owner);
        var visited = new HashSet<PathPoly> { start };
        var queue = new Queue<PathPoly>();
        queue.Enqueue(start);

        while (queue.TryDequeue(out var poly) && visited.Count < maxVisited)
        {
            if (poly.Data.IsFreeSpace)
                result.Add(poly.Coordinates);

            foreach (var next in poly.Neighbors)
            {
                if ((next.Box.Center - start.Box.Center).Length() > maxDistance ||
                    !next.Data.IsFreeSpace && !IsPassableDoor(next, grid, mapSys, blackboard, access, tags) ||
                    !visited.Add(next))
                {
                    continue;
                }

                queue.Enqueue(next);
            }
        }

        return result;
    }

    private static bool IsPassableDoor(
        PathPoly poly,
        MapGridComponent grid,
        SharedMapSystem mapSys,
        AndroidBlackboard blackboard,
        AccessReaderSystem access,
        ICollection<ProtoId<AccessLevelPrototype>> tags)
    {
        if ((poly.Data.Flags & PathfindingBreadcrumbFlag.Door) == 0)
            return false;

        foreach (var ent in mapSys.GetLocalAnchoredEntities(poly.GraphUid, grid, poly.Box))
        {
            if (!blackboard.EntityManager.TryGetComponent<DoorComponent>(ent, out var door))
                continue;

            return door.State is DoorState.Open or DoorState.Opening ||
                   (door.BumpOpen || door.ClickOpen) &&
                   !blackboard.EntityManager.System<SharedDoorSystem>().IsBolted(ent) &&
                   (!blackboard.EntityManager.TryGetComponent<AccessReaderComponent>(ent, out var reader) ||
                    access.IsAllowed(tags, Array.Empty<StationRecordKey>(), ent, reader));
        }

        return false;
    }

    /// <summary>
    /// The walkable tile nearest to a point, e.g. next to a beacon standing on a counter or a table.
    /// </summary>
    public static EntityCoordinates? NearestFreeTile(AndroidBlackboard blackboard, EntityCoordinates center)
    {
        return FindStartPoly(blackboard, center)?.Coordinates;
    }

    public static EntityCoordinates? PickFreeTile(AndroidBlackboard blackboard, EntityUid owner, EntityCoordinates center, float maxDistance)
    {
        if (FindStartPoly(blackboard, center) is not { } start ||
            !blackboard.EntityManager.TryGetComponent<MapGridComponent>(start.GraphUid, out var grid))
        {
            return null;
        }

        var mapSys = blackboard.EntityManager.System<SharedMapSystem>();
        var occupied = OccupiedPositions(blackboard, owner);
        var candidates = new List<EntityCoordinates>();
        var visited = new HashSet<PathPoly> { start };
        var queue = new Queue<PathPoly>();
        queue.Enqueue(start);

        while (queue.TryDequeue(out var poly) && visited.Count < MaxVisited)
        {
            var coords = poly.Coordinates;
            var world = blackboard.Transform.ToMapCoordinates(coords).Position;
            if (!occupied.Any(p => (p - world).Length() < MinSpacing))
                candidates.Add(coords);

            foreach (var next in poly.Neighbors)
            {
                if (!next.Data.IsFreeSpace ||
                    (next.Box.Center - start.Box.Center).Length() > maxDistance ||
                    HasDoor(next, grid, mapSys, blackboard) ||
                    !visited.Add(next))
                {
                    continue;
                }

                queue.Enqueue(next);
            }
        }

        return candidates.Count == 0 ? null : blackboard.Random.Pick(candidates);
    }

    /// <summary>
    /// Returns true if a door is anchored on the tile under these coordinates.
    /// </summary>
    public static bool IsOnDoor(AndroidBlackboard blackboard, EntityCoordinates coords)
    {
        return blackboard.Pathfinding.GetPoly(coords) is { } poly &&
               blackboard.EntityManager.TryGetComponent<MapGridComponent>(poly.GraphUid, out var grid) &&
               HasDoor(poly, grid, blackboard.EntityManager.System<SharedMapSystem>(), blackboard);
    }

    private static PathPoly? FindStartPoly(AndroidBlackboard blackboard, EntityCoordinates center)
    {
        for (var radius = 0; radius <= 2; radius++)
        for (var x = -radius; x <= radius; x++)
        for (var y = -radius; y <= radius; y++)
        {
            if (Math.Max(Math.Abs(x), Math.Abs(y)) != radius)
                continue;

            if (blackboard.Pathfinding.GetPoly(center.Offset(new Vector2(x, y))) is { Data.IsFreeSpace: true } poly)
                return poly;
        }

        return null;
    }

    private static bool HasDoor(PathPoly poly, MapGridComponent grid, SharedMapSystem mapSys, AndroidBlackboard blackboard)
    {
        foreach (var ent in mapSys.GetLocalAnchoredEntities(poly.GraphUid, grid, poly.Box))
        {
            if (blackboard.EntityManager.HasComponent<DoorComponent>(ent))
                return true;
        }

        return false;
    }

    private static List<Vector2> OccupiedPositions(AndroidBlackboard blackboard, EntityUid owner)
    {
        var positions = new List<Vector2>();
        var query = blackboard.EntityManager.EntityQueryEnumerator<AndroidComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var android, out var xform))
        {
            if (uid == owner)
                continue;

            positions.Add(blackboard.Transform.GetWorldPosition(xform));
            foreach (var spot in new[] { android.BreakSpot, android.TendSpot, blackboard.CoordinatesOf(android.BreakChair) })
            {
                if (spot is { } taken)
                    positions.Add(blackboard.Transform.ToMapCoordinates(taken).Position);
            }
        }

        return positions;
    }
}

