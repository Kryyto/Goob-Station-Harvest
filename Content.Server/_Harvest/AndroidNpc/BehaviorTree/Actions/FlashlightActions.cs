// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server._Harvest.AndroidNpc.BehaviorTree.Nodes;
using Content.Server._Harvest.AndroidNpc.Components;
using Content.Server._Harvest.AndroidNpc.Systems;
using Content.Server.Light.EntitySystems;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Light.Components;
using Content.Shared.Light.EntitySystems;
using Content.Shared.Power.Components;
using Content.Shared.Power.EntitySystems;
using Content.Shared.PowerCell.Components;
using Content.Shared.PowerCell;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;

namespace Content.Server._Harvest.AndroidNpc.BehaviorTree.Actions;

/// <summary>
/// Toggles the equipped flashlight on or off depending on the ambient light level.
/// </summary>
public sealed class UseFlashlightAction : IBehaviorNode
{
    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        var sawmill = blackboard.Log;

        if (!blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
            return BehaviorStatus.Failure;

        var equipped = inventory.EquippedItem;
        if (!equipped.HasValue || blackboard.EntityManager.Deleted(equipped.Value))
            return BehaviorStatus.Failure;

        var turnedOn = false;

        if (blackboard.EntityManager.TryGetComponent<UnpoweredFlashlightComponent>(equipped.Value, out var unpowered))
        {
            var unpoweredSystem = blackboard.EntityManager.System<UnpoweredFlashlightSystem>();
            unpoweredSystem.TryToggleLight((equipped.Value, unpowered), owner);
            turnedOn = unpowered.LightOn;
        }
        else if (blackboard.EntityManager.TryGetComponent<HandheldLightComponent>(equipped.Value, out var handheld))
        {
            var handheldSystem = blackboard.EntityManager.System<HandheldLightSystem>();
            if (handheld.Activated)
            {
                // Already on; turn it off.
                handheldSystem.TurnOff((equipped.Value, handheld));
                turnedOn = false;
            }
            else
            {
                // TurnOn also enables the PointLight and validates the battery.
                turnedOn = handheldSystem.TurnOn(owner, (equipped.Value, handheld));
                if (!turnedOn)
                {
                    // Could not turn the light on; restore the work tool and let the recharge branch handle it.
                    inventory.EquippedItem = inventory.PreviousEquippedItem;
                    return BehaviorStatus.Failure;
                }
            }
        }
        else
        {
            return BehaviorStatus.Failure;
        }

        // Keep the flashlight in a clothing slot so the android's hands remain free for tools.
        if (turnedOn)
        {
            var invSys = blackboard.EntityManager.System<InventorySystem>();
            foreach (var slot in new[] { "pocket1", "pocket2", "belt", "back" })
            {
                if (invSys.TryEquip(owner, equipped.Value, slot, silent: true))
                    break;
            }
        }

        // The flashlight is a temporary side tool; restore the previously equipped work item
        // in the blackboard so the next work cycle knows what it was doing.
        inventory.EquippedItem = inventory.PreviousEquippedItem;
        sawmill.Debug($"[UseFlashlight] toggled; restored equipped to {inventory.EquippedItem?.Id ?? 0}");
        return BehaviorStatus.Success;
    }
}

/// <summary>
/// Recharges the android's flashlight by moving its power cell to a nearby cell charger,
/// waiting until it is full, and putting it back into the flashlight.
/// </summary>
public sealed class RechargeFlashlightAction : IBehaviorNode
{
    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        var sawmill = blackboard.Log;

        if (!blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
            return BehaviorStatus.Failure;

        var charger = inventory.FlashlightCharger;
        if (!charger.HasValue || blackboard.EntityManager.Deleted(charger.Value))
        {
            Fail(inventory, blackboard);
            return BehaviorStatus.Failure;
        }

        if (!blackboard.EntityManager.TryGetComponent<TransformComponent>(charger.Value, out var chargerXform) ||
            !blackboard.EntityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform))
        {
            Fail(inventory, blackboard);
            return BehaviorStatus.Failure;
        }

        if ((blackboard.Transform.GetWorldPosition(chargerXform) - blackboard.Transform.GetWorldPosition(ownerXform)).Length() > 1.5f)
            return BehaviorStatus.Running;

        var flashlight = FindFlashlight(owner, inventory, blackboard.EntityManager);
        if (!flashlight.HasValue)
        {
            Fail(inventory, blackboard);
            return BehaviorStatus.Failure;
        }

        var hands = blackboard.EntityManager.System<SharedHandsSystem>();
        var itemSlots = blackboard.EntityManager.System<ItemSlotsSystem>();
        var powerCell = blackboard.EntityManager.System<PowerCellSystem>();
        var inventorySystem = blackboard.EntityManager.System<AndroidInventorySystem>();

        switch (inventory.FlashlightChargeStage)
        {
            case FlashlightChargeStage.None:
                // Ensure the flashlight is held so we can work with its cell.
                if (!hands.IsHolding(owner, flashlight.Value))
                {
                    if (!inventorySystem.Equip(owner, inventory, flashlight.Value))
                    {
                        sawmill.Warning($"[RechargeFlashlight] could not equip flashlight {flashlight.Value}");
                        Fail(inventory, blackboard);
                        return BehaviorStatus.Failure;
                    }
                }

                if (!blackboard.EntityManager.TryGetComponent<PowerCellSlotComponent>(flashlight.Value, out var slotComp))
                {
                    Fail(inventory, blackboard);
                    return BehaviorStatus.Failure;
                }

                // Eject the cell without requiring a free hand. It may land on the floor; the next stage picks it up.
                if (!itemSlots.TryEject(flashlight.Value, slotComp.CellSlotId, null, out var cell))
                {
                    sawmill.Warning($"[RechargeFlashlight] failed to eject cell from {flashlight.Value}");
                    Fail(inventory, blackboard);
                    return BehaviorStatus.Failure;
                }

                inventory.FlashlightCell = cell;
                inventory.FlashlightChargeStage = FlashlightChargeStage.CellInHand;
                sawmill.Debug($"[RechargeFlashlight] ejected cell {cell.Value} from {flashlight.Value}");
                return BehaviorStatus.Running;

            case FlashlightChargeStage.CellInHand:
                var cellInHand = inventory.FlashlightCell;
                if (!cellInHand.HasValue || blackboard.EntityManager.Deleted(cellInHand.Value))
                {
                    Fail(inventory, blackboard);
                    return BehaviorStatus.Failure;
                }

                // Make sure the cell is actually being held; if it fell to the floor, pick it up.
                if (!hands.IsHolding(owner, cellInHand.Value))
                {
                    if (!hands.TryPickupAnyHand(owner, cellInHand.Value))
                    {
                        sawmill.Warning($"[RechargeFlashlight] could not pick up cell {cellInHand.Value}");
                        Fail(inventory, blackboard);
                        return BehaviorStatus.Failure;
                    }
                }

                var chargerComp = blackboard.EntityManager.GetComponent<Content.Shared.Power.Components.ChargerComponent>(charger.Value);
                if (!itemSlots.TryInsert(charger.Value, chargerComp.SlotId, cellInHand.Value, owner))
                {
                    sawmill.Warning($"[RechargeFlashlight] could not insert cell into charger {charger.Value}");
                    Fail(inventory, blackboard);
                    return BehaviorStatus.Failure;
                }

                inventory.FlashlightChargeStage = FlashlightChargeStage.CellInCharger;
                sawmill.Debug($"[RechargeFlashlight] inserted cell into charger {charger.Value}");
                return BehaviorStatus.Running;

            case FlashlightChargeStage.CellInCharger:
                var chargerComp2 = blackboard.EntityManager.GetComponent<Content.Shared.Power.Components.ChargerComponent>(charger.Value);
                if (!itemSlots.TryGetSlot(charger.Value, chargerComp2.SlotId, out var slot) || slot.Item is not { } chargingCell)
                {
                    sawmill.Warning($"[RechargeFlashlight] cell disappeared from charger {charger.Value}");
                    Fail(inventory, blackboard);
                    return BehaviorStatus.Failure;
                }

                if (!blackboard.EntityManager.TryGetComponent<BatteryComponent>(chargingCell, out var battery))
                {
                    Fail(inventory, blackboard);
                    return BehaviorStatus.Failure;
                }

                var batterySystem = blackboard.EntityManager.System<SharedBatterySystem>();
                var charge = batterySystem.GetCharge((chargingCell, battery));
                if (charge < battery.MaxCharge)
                    return BehaviorStatus.Running;

                if (!itemSlots.TryEject(charger.Value, chargerComp2.SlotId, null, out var removedCell))
                {
                    sawmill.Warning($"[RechargeFlashlight] could not eject charged cell from {charger.Value}");
                    Fail(inventory, blackboard);
                    return BehaviorStatus.Failure;
                }

                inventory.FlashlightCell = removedCell;
                inventory.FlashlightChargeStage = FlashlightChargeStage.CellRemoved;
                sawmill.Debug($"[RechargeFlashlight] removed charged cell {removedCell.Value} from charger");
                return BehaviorStatus.Running;

            case FlashlightChargeStage.CellRemoved:
                var cellToInsert = inventory.FlashlightCell;
                if (!cellToInsert.HasValue || blackboard.EntityManager.Deleted(cellToInsert.Value))
                {
                    Fail(inventory, blackboard);
                    return BehaviorStatus.Failure;
                }

                if (!hands.IsHolding(owner, cellToInsert.Value))
                {
                    if (!hands.TryPickupAnyHand(owner, cellToInsert.Value))
                    {
                        sawmill.Warning($"[RechargeFlashlight] lost charged cell {cellToInsert.Value}");
                        Fail(inventory, blackboard);
                        return BehaviorStatus.Failure;
                    }
                }

                if (!blackboard.EntityManager.TryGetComponent<PowerCellSlotComponent>(flashlight.Value, out var flashlightSlot))
                {
                    Fail(inventory, blackboard);
                    return BehaviorStatus.Failure;
                }

                if (!itemSlots.TryInsert(flashlight.Value, flashlightSlot.CellSlotId, cellToInsert.Value, owner))
                {
                    sawmill.Warning($"[RechargeFlashlight] could not reinsert cell into flashlight {flashlight.Value}");
                    Fail(inventory, blackboard);
                    return BehaviorStatus.Failure;
                }

                sawmill.Debug($"[RechargeFlashlight] reinserted cell into flashlight {flashlight.Value}");
                Reset(inventory);
                return BehaviorStatus.Success;

            default:
                Fail(inventory, blackboard);
                return BehaviorStatus.Failure;
        }
    }

    private static EntityUid? FindFlashlight(EntityUid owner, AndroidInventoryComponent inventory, IEntityManager entMan)
    {
        foreach (var item in inventory.OwnedItems)
        {
            if (entMan.Deleted(item))
                continue;

            if (entMan.HasComponent<HandheldLightComponent>(item) || entMan.HasComponent<UnpoweredFlashlightComponent>(item))
                return item;
        }

        return null;
    }

    private static void Reset(AndroidInventoryComponent inventory)
    {
        inventory.FlashlightCharger = null;
        inventory.FlashlightCell = null;
        inventory.FlashlightChargeStage = FlashlightChargeStage.None;
    }

    private static void Fail(AndroidInventoryComponent inventory, AndroidBlackboard blackboard)
    {
        Reset(inventory);
        blackboard.SetBranchCooldown("FlashlightRecharge");
    }
}
