// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Access.Components;
using Content.Shared.Access.Systems;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.StationRecords;

namespace Content.Server.NPC.Pathfinding;

// Harvest: paths requested with PathFlags.Access only go through the doors the entity can actually open.
public sealed partial class PathfindingSystem
{
    [Dependency] private readonly AccessReaderSystem _accessReader = default!;
    [Dependency] private readonly SharedAirlockSystem _airlock = default!;
    [Dependency] private readonly SharedDoorSystem _doors = default!;

    /// <summary>
    /// Closed doors on the entity's map that it cannot open: no access, bolted, unpowered, or not interactive
    /// at all (blast doors). Computed on the main thread before the request runs in parallel.
    /// </summary>
    private HashSet<(EntityUid Grid, Vector2i Tile)> GetBlockedDoors(EntityUid entity)
    {
        var blocked = new HashSet<(EntityUid, Vector2i)>();
        if (!_xformQuery.TryGetComponent(entity, out var entityXform))
            return blocked;

        var access = _accessReader.FindAccessTags(entity);
        var noKeys = Array.Empty<StationRecordKey>();
        var query = EntityQueryEnumerator<DoorComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var door, out var xform))
        {
            if (door.State is DoorState.Open or DoorState.Opening ||
                !xform.Anchored ||
                xform.GridUid is not { } grid ||
                xform.MapID != entityXform.MapID)
            {
                continue;
            }

            var openable = (door.BumpOpen || door.ClickOpen) &&
                           !_doors.IsBolted(uid) &&
                           (!TryComp<AirlockComponent>(uid, out var airlock) || _airlock.CanChangeState(uid, airlock)) &&
                           (!_accessQuery.TryGetComponent(uid, out var reader) || _accessReader.IsAllowed(access, noKeys, uid, reader));

            if (!openable)
                blocked.Add((grid, xform.LocalPosition.Floored()));
        }

        return blocked;
    }

    /// <summary>
    /// Androids (access requests) cross the whole station: 512 nodes only covers a few rooms. The search is
    /// still split over ticks by <see cref="PathTime"/>, so a longer search does not cost more per tick.
    /// </summary>
    private const int AccessNodeLimit = 8192;

    private static int NodeLimitFor(PathRequest request)
    {
        return (request.Flags & PathFlags.Access) != 0x0 ? AccessNodeLimit : NodeLimit;
    }

    private static bool IsBlockedDoor(PathRequest request, PathPoly poly)
    {
        return request.BlockedDoors is { Count: > 0 } blocked &&
               blocked.Contains((poly.GraphUid, poly.Box.Center.Floored()));
    }
}
