// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Numerics;
using Content.Server._Harvest.AndroidNpc.BehaviorTree.Nodes;
using Content.Server._Harvest.AndroidNpc.Components;
using Content.Server._Harvest.AndroidNpc.Systems;
using Content.Shared.Fluids.Components;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Storage.EntitySystems;
using Content.Shared.Storage;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.Server._Harvest.AndroidNpc.BehaviorTree.Actions;

/// <summary>
/// Drops a wet floor sign at the android's current location.
/// </summary>
public sealed class PlaceSignAction : IBehaviorNode
{
    /// <inheritdoc />
    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        // PRE-VALIDATION
        if (blackboard.EntityManager.Deleted(owner))
            return BehaviorStatus.Failure;

        if (!blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
            return BehaviorStatus.Failure;

        var sign = inventory.OwnedItems.FirstOrDefault(item =>
        {
            if (blackboard.EntityManager.Deleted(item))
                return false;

            return blackboard.EntityManager.TryGetComponent<MetaDataComponent>(item, out var meta)
                && meta.EntityPrototype?.ID == "WetFloorSign";
        });

        // No sign available or it is not currently reachable. This is not a failure;
        // the puddle has already been cleaned.
        if (!sign.IsValid())
            return BehaviorStatus.Success;

        // Don't spam signs; only place one if there is no other placed sign within 5 tiles.
        if (HasPlacedSignNearby(owner, blackboard, 5f))
            return BehaviorStatus.Success;

        // EXECUTE
        var hands = blackboard.EntityManager.System<SharedHandsSystem>();
        var inventorySystem = blackboard.EntityManager.System<AndroidInventorySystem>();

        if (!inventorySystem.Equip(owner, inventory, sign))
            return BehaviorStatus.Success;

        inventory.EquippedItem = sign;

        if (!hands.TryDrop(owner))
        {
            // Couldn't place the sign, but the puddle is cleaned. Keep the sign in hand
            // and don't treat this as a failure.
            inventory.EquippedItem = null;
            return BehaviorStatus.Success;
        }

        // POST-VALIDATION
        inventory.EquippedItem = null;
        inventory.OwnedItems.Remove(sign);
        inventory.PlacedSigns.Add(sign);
        return BehaviorStatus.Success;
    }

    private static bool HasPlacedSignNearby(EntityUid owner, AndroidBlackboard blackboard, float range)
    {
        if (!blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
            return false;

        if (!blackboard.EntityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform))
            return false;

        var ownerPos = blackboard.EntityManager.System<SharedTransformSystem>().GetWorldPosition(ownerXform);
        var rangeSquared = range * range;

        foreach (var sign in inventory.PlacedSigns)
        {
            if (blackboard.EntityManager.Deleted(sign))
                continue;

            if (!blackboard.EntityManager.TryGetComponent<TransformComponent>(sign, out var signXform))
                continue;

            var signPos = blackboard.EntityManager.System<SharedTransformSystem>().GetWorldPosition(signXform);
            if ((signPos - ownerPos).LengthSquared() <= rangeSquared)
                return true;
        }

        return false;
    }
}

/// <summary>
/// Picks up wet floor signs the android placed once the nearby puddle has evaporated.
/// </summary>
public sealed class RetrieveSignAction : IBehaviorNode
{
    private readonly MoveToAction _moveTo;

    // The tree is shared by every android of the job, so the chosen sign is kept per android.
    private readonly Dictionary<EntityUid, EntityUid> _signs = new();

    public RetrieveSignAction()
    {
        _moveTo = new MoveToAction((owner, blackboard) =>
        {
            if (!_signs.TryGetValue(owner, out var sign) || blackboard.EntityManager.Deleted(sign))
                return null;

            return blackboard.EntityManager.GetComponentOrNull<TransformComponent>(sign)?.Coordinates;
        }, 0.5f);
    }

    /// <inheritdoc />
    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        if (blackboard.EntityManager.Deleted(owner))
            return BehaviorStatus.Failure;

        if (!blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
            return BehaviorStatus.Failure;

        // If we don't have a sign yet or it was deleted, pick a new one.
        if (!_signs.TryGetValue(owner, out var current) || blackboard.EntityManager.Deleted(current))
        {
            if (ChooseSignToRetrieve(owner, inventory, blackboard) is not { } chosen)
            {
                _signs.Remove(owner);
                return BehaviorStatus.Failure;
            }

            current = chosen;
            _signs[owner] = chosen;
        }

        // Move within interaction range of the sign.
        var moveStatus = _moveTo.Tick(owner, blackboard, frameTime);
        if (moveStatus == BehaviorStatus.Running)
            return BehaviorStatus.Running;

        if (moveStatus == BehaviorStatus.Failure)
        {
            inventory.PlacedSigns.Remove(current);
            _signs.Remove(owner);
            return BehaviorStatus.Failure;
        }

        // Pick the sign up and put it back in the satchel.
        if (!TryStoreSign(owner, current, inventory, blackboard))
        {
            inventory.PlacedSigns.Remove(current);
            _signs.Remove(owner);
            return BehaviorStatus.Failure;
        }

        inventory.PlacedSigns.Remove(current);
        inventory.OwnedItems.Add(current);
        _signs.Remove(owner);
        return BehaviorStatus.Success;
    }

    private static EntityUid? ChooseSignToRetrieve(EntityUid owner, AndroidInventoryComponent inventory, AndroidBlackboard blackboard)
    {
        var ownerPos = blackboard.EntityManager.GetComponentOrNull<TransformComponent>(owner)?.MapPosition.Position ?? Vector2.Zero;

        foreach (var sign in inventory.PlacedSigns)
        {
            if (blackboard.EntityManager.Deleted(sign))
                continue;

            var signXform = blackboard.EntityManager.GetComponentOrNull<TransformComponent>(sign);
            if (signXform == null)
                continue;

            var signPos = signXform.MapPosition.Position;
            if ((signPos - ownerPos).Length() > 16f)
                continue;

            if (IsPuddleNearby(signPos, blackboard))
                continue;

            return sign;
        }

        return null;
    }

    private static bool IsPuddleNearby(Vector2 position, AndroidBlackboard blackboard)
    {
        var query = blackboard.EntityManager.EntityQuery<PuddleComponent, TransformComponent>();
        foreach (var (_, xform) in query)
        {
            if ((xform.MapPosition.Position - position).Length() <= 2f)
                return true;
        }

        return false;
    }

    private static bool TryStoreSign(EntityUid owner, EntityUid sign, AndroidInventoryComponent inventory, AndroidBlackboard blackboard)
    {
        var inventorySystem = blackboard.EntityManager.System<InventorySystem>();
        var storageSystem = blackboard.EntityManager.System<SharedStorageSystem>();

        // Try the back slot first.
        if (inventorySystem.TryGetSlotEntity(owner, "back", out var backUid) &&
            blackboard.EntityManager.TryGetComponent<StorageComponent>(backUid, out var storage))
        {
            if (storageSystem.Insert(backUid.Value, sign, out _, storageComp: storage, playSound: false))
                return true;
        }

        // Fall back to a free hand.
        return blackboard.EntityManager.System<SharedHandsSystem>().TryPickupAnyHand(owner, sign);
    }
}
