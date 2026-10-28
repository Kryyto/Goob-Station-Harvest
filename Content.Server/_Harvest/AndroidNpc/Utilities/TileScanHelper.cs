// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Server.Decals;
using Content.Shared.Chemistry.Components.SolutionManager;
using Content.Shared.Decals;
using Content.Shared.Fluids;
using Content.Shared.Fluids.Components;
using Content.Shared.Interaction;
using Content.Shared.Item;
using Content.Shared.Light.Components;
using Content.Shared.Light.EntitySystems;
using Content.Shared.Mobs.Components;
using Content.Shared.Shuttles.Components;
using Content.Shared.Tag;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Content.Goobstation.Common.Footprints;
using System.Numerics;

namespace Content.Server._Harvest.AndroidNpc.Utilities;

/// <summary>
/// Helper for finding dirty tiles and debris around an android.
/// </summary>
public sealed class TileScanHelper
{
    private readonly IEntityManager _entityManager;
    private readonly EntityLookupSystem _lookup;
    private readonly SharedTransformSystem _transform;
    private readonly SharedMapSystem _map;
    private readonly SharedPuddleSystem _puddle;
    private readonly SharedInteractionSystem _interaction;
    private readonly SharedPoweredLightSystem _poweredLight;
    private readonly DecalSystem _decal;

    public TileScanHelper(IEntityManager entityManager, EntityLookupSystem lookup, SharedTransformSystem transform, SharedPuddleSystem puddle, SharedInteractionSystem interaction, DecalSystem? decal = null)
    {
        _entityManager = entityManager;
        _lookup = lookup;
        _transform = transform;
        _map = entityManager.System<SharedMapSystem>();
        _puddle = puddle;
        _interaction = interaction;
        _poweredLight = entityManager.System<SharedPoweredLightSystem>();
        _decal = decal ?? entityManager.System<DecalSystem>();
    }

    /// <summary>
    /// Finds puddles and tagged trash within the specified range.
    /// </summary>
    /// <param name="owner">The android entity.</param>
    /// <param name="range">Range in world units.</param>
    /// <param name="trashTags">Tags to consider as trash.</param>
    /// <returns>Sorted list of dirty target entities, nearest first.</returns>
    public List<EntityUid> FindDirtyTargets(EntityUid owner, float range, IReadOnlyList<string> trashTags)
    {
        var result = new List<EntityUid>();

        if (!_entityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform))
            return result;

        var ownerGrid = ownerXform.GridUid ?? ownerXform.ParentUid;
        var worldPos = _transform.GetWorldPosition(ownerXform);

        // Puddles.
        var puddles = _lookup.GetEntitiesInRange<PuddleComponent>(ownerXform.Coordinates, range);

        foreach (var puddle in puddles)
        {
            if (_entityManager.Deleted(puddle.Owner) || puddle.Owner == owner)
                continue;

            if (!_entityManager.TryGetComponent<TransformComponent>(puddle.Owner, out var puddleXform))
                continue;

            if (!IsOnFloorGrid(puddleXform, ownerGrid))
                continue;

            if (IsNearActiveDrain(puddleXform))
                continue;

            // Puddles that are already just water will evaporate on their own; ignore them.
            if (puddle.Comp.Solution is { } soln)
            {
                var solution = soln.Comp.Solution;
                var absorbentReagents = _puddle.GetAbsorbentReagents(solution);
                var waterVolume = solution.GetTotalPrototypeQuantity(absorbentReagents);
                if (waterVolume >= solution.Volume)
                    continue;
            }

            // Line of sight is the expensive check, so it comes last.
            if (!CanSee(owner, puddleXform, range))
                continue;

            result.Add(puddle.Owner);
        }

        // Footprints and other washable floor grime.
        var footprints = _lookup.GetEntitiesInRange<FootprintComponent>(ownerXform.Coordinates, range);
        foreach (var footprint in footprints)
        {
            if (_entityManager.Deleted(footprint.Owner) || footprint.Owner == owner)
                continue;

            if (!_entityManager.TryGetComponent<TransformComponent>(footprint.Owner, out var footprintXform))
                continue;

            if (!IsOnFloorGrid(footprintXform, ownerGrid))
                continue;

            if (!CanSee(owner, footprintXform, range))
                continue;

            if (!result.Contains(footprint.Owner))
                result.Add(footprint.Owner);
        }

        // Tagged trash.
        if (trashTags.Count > 0)
        {
            var tagSystem = _entityManager.System<TagSystem>();
            var nearby = _lookup.GetEntitiesInRange<ItemComponent>(ownerXform.Coordinates, range);

            foreach (var item in nearby)
            {
                if (_entityManager.Deleted(item.Owner) || item.Owner == owner)
                    continue;

                if (!_entityManager.TryGetComponent<TagComponent>(item.Owner, out var tagComp))
                    continue;

                if (!trashTags.Any(tag => tagSystem.HasTag(tagComp, tag)))
                    continue;

                if (!_entityManager.TryGetComponent<TransformComponent>(item.Owner, out var itemXform))
                    continue;

                if (!IsOnFloorGrid(itemXform, ownerGrid))
                    continue;

                // Unbroken bowls and similar reusable reagent containers are tagged as trash
                // by the kitchen recipes, but they should not be thrown away.
                if (IsReusableDish(item.Owner) || result.Contains(item.Owner))
                    continue;

                if (!CanSee(owner, itemXform, range))
                    continue;

                result.Add(item.Owner);
            }
        }

        return result
            .OrderBy(e => _entityManager.TryGetComponent<TransformComponent>(e, out var xform)
                ? (_transform.GetWorldPosition(xform) - worldPos).LengthSquared()
                : float.MaxValue)
            .ToList();
    }

    /// <summary>
    /// Finds floor tiles with cleanable decals within the specified range.
    /// </summary>
    public List<EntityCoordinates> FindDirtyTiles(EntityUid owner, float range)
    {
        var result = new List<EntityCoordinates>();

        if (!_entityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform))
            return result;

        if (ownerXform.GridUid is not { Valid: true } gridUid)
            return result;

        if (!_entityManager.TryGetComponent<MapGridComponent>(gridUid, out var gridComp))
            return result;

        var ownerPos = _transform.GetWorldPosition(ownerXform);
        var ownerLocal = _map.WorldToLocal(gridUid, gridComp, ownerPos);

        var bounds = new Box2(
            new Vector2(ownerLocal.X - range, ownerLocal.Y - range),
            new Vector2(ownerLocal.X + range, ownerLocal.Y + range));

        var seenTiles = new HashSet<Vector2i>();
        var decals = _decal.GetDecalsIntersecting(gridUid, bounds);

        foreach (var (_, decal) in decals)
        {
            if (!decal.Cleanable)
                continue;

            var tile = new Vector2i(
                (int)Math.Floor(decal.Coordinates.X / gridComp.TileSize),
                (int)Math.Floor(decal.Coordinates.Y / gridComp.TileSize));
            if (!seenTiles.Add(tile))
                continue;

            var tileLocal = new Vector2(tile.X + 0.5f, tile.Y + 0.5f) * gridComp.TileSize;
            result.Add(new EntityCoordinates(gridUid, tileLocal));
        }

        // Tiles are in grid-local coordinates, like the owner position used for the bounds.
        return result.OrderBy(c => (c.Position - ownerLocal).LengthSquared()).ToList();
    }

    /// <summary>
    /// Returns true if the given tile still has a cleanable decal on it.
    /// </summary>
    public bool IsTileDirty(EntityCoordinates tile, float radius = 0.75f)
    {
        if (!tile.IsValid(_entityManager))
            return false;

        if (tile.EntityId is not { Valid: true } gridUid)
            return false;

        if (!_entityManager.TryGetComponent<MapGridComponent>(gridUid, out var gridComp))
            return false;

        var tileIndex = _map.LocalToTile(gridUid, gridComp, tile);
        var tileLocal = new Vector2(tileIndex.X, tileIndex.Y) * gridComp.TileSize;
        var tileSize = gridComp.TileSize;

        var bounds = new Box2(
            new Vector2(tileLocal.X - tileSize * 0.25f, tileLocal.Y - tileSize * 0.25f),
            new Vector2(tileLocal.X + tileSize * 1.25f, tileLocal.Y + tileSize * 1.25f));

        var decals = _decal.GetDecalsIntersecting(gridUid, bounds);

        foreach (var (_, decal) in decals)
        {
            if (!decal.Cleanable)
                continue;

            var decalTile = new Vector2i(
                (int)Math.Floor(decal.Coordinates.X / tileSize),
                (int)Math.Floor(decal.Coordinates.Y / tileSize));

            if ((tileIndex - decalTile).Length <= radius)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true if the entity is a free-floating floor item (or puddle) on the same grid as the
    /// android and not held by or contained in another entity.
    /// </summary>
    private bool IsOnFloorGrid(TransformComponent xform, EntityUid? ownerGrid)
    {
        // Puddles and floor trash must be direct children of the grid. GridUid can be temporarily
        // uninitialised in integration tests (or after a grid split/merge), so fall back to the parent.
        var targetGrid = xform.GridUid ?? xform.ParentUid;

        if (!targetGrid.IsValid() || xform.ParentUid != targetGrid)
            return false;

        if (!_entityManager.HasComponent<MapGridComponent>(xform.ParentUid))
            return false;

        // Only consider targets on the android's own grid; pathfinding cannot cross grids.
        if (ownerGrid is { Valid: true } && xform.ParentUid != ownerGrid)
            return false;

        // Never consider mobs or items held by mobs as floor garbage.
        if (_entityManager.HasComponent<MobStateComponent>(xform.Owner))
            return false;

        return true;
    }

    /// <summary>
    /// Returns true for unbroken reagent containers (such as empty bowls) that are tagged as trash
    /// but are still usable and should not be thrown away.
    /// </summary>
    private bool IsReusableDish(EntityUid item)
    {
        if (!_entityManager.HasComponent<SolutionContainerManagerComponent>(item))
            return false;

        // Broken/crushed trash usually has <see cref="SpaceGarbageComponent"/>; keep that.
        return !_entityManager.HasComponent<SpaceGarbageComponent>(item);
    }

    /// <summary>
    /// Returns true if the android has a clear line of sight to the target.
    /// The target itself is ignored so an item or puddle sitting on the floor does not block itself.
    /// </summary>
    private bool CanSee(EntityUid owner, TransformComponent targetXform, float range)
    {
        return _interaction.InRangeUnobstructed(
            owner,
            targetXform.Coordinates,
            range,
            predicate: e => e == targetXform.Owner);
    }

    /// <summary>
    /// Returns true if the android has a clear line of sight to an entity target.
    /// Wall-mounted targets are handled using the interaction system's wallmount logic.
    /// </summary>
    private bool CanSee(EntityUid owner, EntityUid target, TransformComponent targetXform, float range)
    {
        return _interaction.InRangeUnobstructed(
            new Entity<TransformComponent?>(owner, null),
            new Entity<TransformComponent?>(target, targetXform),
            range,
            popup: false);
    }

    /// <summary>
    /// Finds powered light fixtures with missing, broken or burned bulbs that a light replacer can fix.
    /// </summary>
    public List<EntityUid> FindBrokenLights(EntityUid owner, float range)
    {
        var result = new List<EntityUid>();

        if (!_entityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform))
            return result;

        var ownerGrid = ownerXform.GridUid ?? ownerXform.ParentUid;
        var lights = _lookup.GetEntitiesInRange<PoweredLightComponent>(ownerXform.Coordinates, range);

        foreach (var light in lights)
        {
            if (_entityManager.Deleted(light.Owner))
                continue;

            if (!_entityManager.TryGetComponent<TransformComponent>(light.Owner, out var lightXform))
                continue;

            // Ignore lights on other grids or that the android cannot actually see (e.g. through walls).
            if (!IsOnFloorGrid(lightXform, ownerGrid))
                continue;

            if (!CanSee(owner, light.Owner, lightXform, range))
                continue;

            var bulbUid = _poweredLight.GetBulb(light.Owner, light.Comp);
            if (bulbUid == null)
            {
                result.Add(light.Owner);
                continue;
            }

            if (_entityManager.TryGetComponent<LightBulbComponent>(bulbUid.Value, out var bulb) &&
                bulb.State != LightBulbState.Normal)
            {
                result.Add(light.Owner);
            }
        }

        return result;
    }

    /// <summary>
    /// Active floor drains slowly absorb nearby puddles. Don’t waste effort mopping inside their
    /// effective range.
    /// </summary>
    private bool IsNearActiveDrain(TransformComponent puddleXform)
    {
        const float drainAvoidanceRange = 2.5f;

        var puddlePos = _transform.GetWorldPosition(puddleXform);

        var query = _entityManager.EntityQueryEnumerator<DrainComponent, TransformComponent>();
        while (query.MoveNext(out var _, out var drain, out var xform))
        {
            if (!drain.AutoDrain)
                continue;

            if (!xform.Anchored)
                continue;

            if (xform.GridUid != puddleXform.GridUid)
                continue;

            var drainPos = _transform.GetWorldPosition(xform);
            if ((drainPos - puddlePos).Length() <= drainAvoidanceRange)
                return true;
        }

        return false;
    }
}
