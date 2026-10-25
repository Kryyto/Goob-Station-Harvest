// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Numerics;
using Content.Goobstation.Common.Footprints;
using Content.Goobstation.Shared.FloorCleaner;
using Content.Server._Harvest.AndroidNpc.BehaviorTree.Actions;
using Content.Server._Harvest.AndroidNpc.BehaviorTree.Nodes;
using Content.Server._Harvest.AndroidNpc.Components;
using Content.Server._Harvest.AndroidNpc.Systems;
using Content.Server._Harvest.AndroidNpc.Utilities;
using Content.Shared.Fluids;
using Content.Shared.Fluids.Components;
using Content.Shared.Light.Components;
using Content.Shared.Power.Components;
using Content.Shared.Power.EntitySystems;
using Content.Shared.PowerCell;
using Content.Shared.PowerCell.Components;
using Robust.Server.GameObjects;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Random;

namespace Content.Server._Harvest.AndroidNpc.BehaviorTree.Trees;

/// <summary>
/// Work branches of the janitor android, from tool maintenance down to patrolling.
/// </summary>
public static class JanitorTree
{
    private const string WringMopBranch = "WringMop";
    private const string BucketBranch = "Bucket";
    private const string TrashBranch = "Trash";
    private const string ResupplyBranch = "Resupply";
    private const string CleanadeBranch = "Cleanade";
    private const string CleanadeFleeBranch = "CleanadeFlee";
    private const string FlashlightRechargeBranch = "FlashlightRecharge";
    private const string FlashlightToggleBranch = "FlashlightToggle";
    private const string WorkBranch = "Work";

    private const int PuddleClusterThreshold = 12;
    private const float PuddleClusterRadius = 8f;
    private const float DarkThreshold = 0.5f;
    private const float BrightThreshold = 0.8f;

    /// <summary>
    /// Full janitor tree, as built by <see cref="AndroidTrees"/>.
    /// </summary>
    public static IBehaviorNode Build() => AndroidTrees.Build(AndroidJob.Janitor);

    public static IEnumerable<IBehaviorNode> Branches()
    {
        yield return AndroidTrees.TakeBreak();

        // Rinse the spent mop in the carried bucket; needs no travelling, so it comes first.
        yield return new SequenceNode(WringMopBranch)
        {
            InventoryCondition(WringMopBranch, (inv, owner, inventory) =>
                inv.MopNeedsService(owner, inventory) &&
                inv.TryGetBucket(owner, inventory, out var bucket) &&
                inv.CanBucketRefillMop(bucket)),
            new WringMopAction()
        };

        yield return Service(BucketBranch, AndroidServiceKind.Bucket, (inv, owner, inventory) => inv.BucketNeedsService(owner, inventory), new ServiceBucketAction());
        yield return Service(TrashBranch, AndroidServiceKind.Trash, (inv, owner, inventory) => inv.TrashBagNeedsEmptying(owner, inventory), new DumpTrashAction());

        yield return new SequenceNode(ResupplyBranch)
        {
            InventoryCondition(ResupplyBranch, (inv, owner, inventory) => inv.NeedsResupply(owner, inventory)),
            new MoveToAction(GetResupplyCoordinates, 0.5f, OnResupplyMoveFailed) { RemembersUnreachable = true },
            new RestockAtAction()
        };

        // Keep away from a primed cleanade while the foam dissipates.
        yield return new SequenceNode(CleanadeFleeBranch)
        {
            new ConditionNode(ShouldFleeFromCleanade),
            new MoveToAction((owner, bb) => ShouldFleeFromCleanade(owner, bb) ? Inventory(owner, bb)?.CleanadeFleeTarget : null, 0.5f)
        };

        // Hunt nearby mice, then drag the body to the kitchen for the chef.
        yield return new SequenceNode("Pest")
        {
            new SelectPestTargetAction(),
            new MoveToAction((owner, bb) => bb.CoordinatesOf(Inventory(owner, bb)?.PestTarget), 1.0f) { RemembersUnreachable = true },
            new AttackPestAction(),
            new StartPullPestAction(),
            new SelectorNode
            {
                new SequenceNode
                {
                    new SelectKitchenDisposalTargetAction(),
                    new MoveToAction((owner, bb) => bb.CoordinatesOf(Inventory(owner, bb)?.PestDisposalTarget), 1.0f),
                    new StopPullPestAction()
                },
                new StopPullPestAction()
            }
        };

        yield return new SequenceNode("RepairLight")
        {
            new SelectBrokenLightAction(),
            new MoveToTargetAction(1.0f),
            new ConditionNode(TargetStillDirty),
            new EquipItemAction((_, _) => "LightReplacer"),
            new UseItemAction(GetCurrentToolTarget)
        };

        // Drop a cleanade on large puddle clusters so the janitor can move on quickly.
        yield return new SequenceNode(CleanadeBranch)
        {
            new ConditionNode(HasPuddleCluster),
            new EquipItemAction((_, _) => "CleanerGrenade"),
            new MoveToAction(GetPuddleClusterCenter, 0.5f) { RemembersUnreachable = true },
            new UseCleanadeAction()
        };

        yield return new SequenceNode("CleanTarget")
        {
            new ConditionNode(HasDirtyTarget),
            new SelectDirtyTargetAction(),
            new MoveToTargetAction(1.0f),
            new ConditionNode(TargetStillDirty),
            new EquipItemAction(SelectTool),
            new UseItemAction(GetCurrentToolTarget),
            new SelectorNode
            {
                new SequenceNode
                {
                    new ConditionNode((owner, bb) => Inventory(owner, bb)?.LastTargetWasLiquid == true),
                    new PlaceSignAction()
                },
                new ConditionNode((_, _) => true)
            }
        };

        yield return new SequenceNode("CleanTile")
        {
            new ConditionNode(HasDirtyTile),
            new SelectDirtyTileAction(),
            new MoveToAction((owner, bb) => Memory(owner, bb)?.CurrentDirtyTile, 0.5f, OnDirtyTileMoveFailed) { RemembersUnreachable = true },
            new ConditionNode(DirtyTileStillDirty),
            new EquipItemAction(SelectScrubTool),
            new UseItemOnTileAction()
        };

        yield return new SequenceNode("RetrieveSign")
        {
            new ConditionNode((owner, bb) => Inventory(owner, bb)?.PlacedSigns.Any(s => !bb.EntityManager.Deleted(s)) == true),
            new RetrieveSignAction()
        };

        yield return new SequenceNode(FlashlightRechargeBranch)
        {
            new ConditionNode(FlashlightNeedsCharging),
            new EquipItemAction((_, _) => "FlashlightLantern"),
            new MoveToAction(FindFlashlightChargerCoordinates, 0.5f) { RemembersUnreachable = true },
            new RechargeFlashlightAction()
        };

        // After all work, so lighting never interrupts a cleaning task.
        yield return new SequenceNode(FlashlightToggleBranch)
        {
            new ConditionNode(ShouldUseFlashlight),
            new EquipItemAction((_, _) => "FlashlightLantern"),
            new UseFlashlightAction()
        };

        yield return new SequenceNode("Patrol")
        {
            new PatrolAction()
        };
    }

    private static AndroidInventoryComponent? Inventory(EntityUid owner, AndroidBlackboard bb)
    {
        return bb.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory) ? inventory : null;
    }

    private static AndroidMemoryComponent? Memory(EntityUid owner, AndroidBlackboard bb)
    {
        return bb.EntityManager.TryGetComponent<AndroidMemoryComponent>(owner, out var memory) ? memory : null;
    }

    /// <summary>
    /// A condition on the android's inventory that is skipped while its branch is on a failure cooldown.
    /// </summary>
    private static ConditionNode InventoryCondition(
        string branch,
        Func<AndroidInventorySystem, EntityUid, AndroidInventoryComponent, bool> check)
    {
        return new ConditionNode((owner, bb) =>
            !bb.IsBranchOnCooldown(branch) &&
            Inventory(owner, bb) is { } inventory &&
            check(bb.System<AndroidInventorySystem>(), owner, inventory));
    }

    /// <summary>
    /// Takes a tool to a sink or disposal unit when it needs servicing.
    /// </summary>
    private static IBehaviorNode Service(
        string branch,
        AndroidServiceKind kind,
        Func<AndroidInventorySystem, EntityUid, AndroidInventoryComponent, bool> needsService,
        IBehaviorNode action)
    {
        return new SequenceNode(branch)
        {
            InventoryCondition(branch, needsService),
            new SelectServiceStationAction(kind),
            new MoveToAction((owner, bb) => bb.CoordinatesOf(Inventory(owner, bb)?.ServiceTarget), 0.5f, OnServiceMoveFailed) { RemembersUnreachable = true },
            action
        };
    }

    private static bool OwnsPrototype(AndroidBlackboard bb, EntityUid owner, AndroidInventoryComponent inventory, string prototype)
    {
        return bb.System<AndroidInventorySystem>().FindItemByPrototype(owner, inventory, prototype).HasValue;
    }

    private static bool CanScrub(AndroidBlackboard bb, AndroidInventoryComponent inventory)
    {
        return inventory.OwnedItems.Any(e =>
            !bb.EntityManager.Deleted(e) &&
            (bb.EntityManager.HasComponent<FloorCleanerComponent>(e) || bb.EntityManager.HasComponent<SprayComponent>(e)));
    }

    private static bool HasDirtyTarget(EntityUid owner, AndroidBlackboard bb)
    {
        if (bb.IsBranchOnCooldown(WorkBranch))
            return false;

        // Broken lights have their own branch.
        if (GetCurrentToolTarget(owner, bb) is { } current && !bb.EntityManager.HasComponent<PoweredLightComponent>(current))
            return true;

        if (Inventory(owner, bb) is not { } inventory ||
            Memory(owner, bb) is not { KnownDirtyTargets.Count: > 0 } memory ||
            !bb.EntityManager.TryGetComponent<AndroidComponent>(owner, out var android))
        {
            return false;
        }

        var inv = bb.System<AndroidInventorySystem>();
        var canMop = inv.TryGetMop(owner, inventory, out var mop) && inv.CanMopClean(mop);
        var canBagTrash = !inv.TrashBagNeedsEmptying(owner, inventory);
        var canLightReplace = inv.FindAndRegisterOwnedTool(owner, inventory, "LightReplacer").HasValue;
        var canScrub = CanScrub(bb, inventory);
        var threshold = SelectDirtyTargetAction.GetBlacklistThreshold(bb);

        foreach (var target in memory.KnownDirtyTargets)
        {
            if (bb.EntityManager.Deleted(target) ||
                bb.EntityManager.HasComponent<PoweredLightComponent>(target) ||
                !SelectDirtyTargetAction.IsValidFloorTarget(target, bb) ||
                !SelectDirtyTargetAction.IsStillDirtyTarget(target, bb) ||
                !SelectDirtyTargetAction.HasWorkingToolFor(target, bb, canMop, canBagTrash, canLightReplace, canScrub) ||
                bb.Blacklist.IsBlacklisted(android, target, threshold) ||
                bb.Claims.IsClaimedByOther(target, owner))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static EntityCoordinates? GetResupplyCoordinates(EntityUid owner, AndroidBlackboard bb)
    {
        if (bb.CoordinatesOf(owner) is not { } origin || bb.System<AndroidInventorySystem>().GetJobFor(owner) is not { } job)
            return null;

        return new NavMapBeaconLocator(bb.EntityManager).TryFindResupplyBeacon(origin, job, out var coords) ? coords : null;
    }

    private static bool TargetStillDirty(EntityUid owner, AndroidBlackboard bb)
    {
        if (GetCurrentToolTarget(owner, bb) is not { } target)
            return false;

        if (SelectDirtyTargetAction.IsValidFloorTarget(target, bb) && SelectDirtyTargetAction.IsStillDirtyTarget(target, bb))
            return true;

        // No longer valid: forget it so it is not reselected.
        if (Inventory(owner, bb) is { } inventory)
            inventory.CurrentToolTarget = null;

        return false;
    }

    private static bool HasDirtyTile(EntityUid owner, AndroidBlackboard bb)
    {
        if (bb.IsBranchOnCooldown(WorkBranch) || Memory(owner, bb) is not { } memory)
            return false;

        if (memory.CurrentDirtyTile.HasValue)
            return true;

        if (memory.KnownDirtyTiles.Count == 0 || Inventory(owner, bb) is not { } inventory || !CanScrub(bb, inventory))
            return false;

        var scanHelper = bb.System<AndroidScanSystem>().ScanHelper;
        memory.KnownDirtyTiles.RemoveAll(t => !t.IsValid(bb.EntityManager) || !scanHelper.IsTileDirty(t));
        return memory.KnownDirtyTiles.Count > 0;
    }

    private static void OnDirtyTileMoveFailed(EntityUid owner, AndroidBlackboard bb)
    {
        if (Memory(owner, bb) is not { CurrentDirtyTile: { } tile } memory)
            return;

        memory.KnownDirtyTiles.Remove(tile);
        memory.CurrentDirtyTile = null;
    }

    private static bool DirtyTileStillDirty(EntityUid owner, AndroidBlackboard bb)
    {
        if (Memory(owner, bb) is not { CurrentDirtyTile: { } tile } memory)
            return false;

        if (tile.IsValid(bb.EntityManager) && bb.System<AndroidScanSystem>().ScanHelper.IsTileDirty(tile))
            return true;

        memory.CurrentDirtyTile = null;
        memory.KnownDirtyTiles.Remove(tile);
        return false;
    }

    private static string? SelectTool(EntityUid owner, AndroidBlackboard bb)
    {
        if (GetCurrentToolTarget(owner, bb) is not { } target)
            return null;

        if (bb.EntityManager.HasComponent<PuddleComponent>(target))
            return SelectCleaningTool(owner, bb);

        if (bb.EntityManager.HasComponent<FootprintComponent>(target))
            return SelectScrubTool(owner, bb);

        return bb.EntityManager.HasComponent<PoweredLightComponent>(target) ? "LightReplacer" : "TrashBag";
    }

    /// <summary>
    /// Picks a tool for puddles: the mop by default, sometimes the spray bottle so the whole belt gets used.
    /// Soap is a scrubbing tool and does not trigger puddle cleaning, so it is never picked here.
    /// </summary>
    private static string SelectCleaningTool(EntityUid owner, AndroidBlackboard bb)
    {
        if (Inventory(owner, bb) is not { } inventory ||
            !OwnsPrototype(bb, owner, inventory, "SprayBottleSpaceCleaner"))
        {
            return "MopItem";
        }

        if (!OwnsPrototype(bb, owner, inventory, "MopItem") || bb.Random.Prob(0.4f))
            return "SprayBottleSpaceCleaner";

        return bb.Random.Pick(new[] { "MopItem", "SprayBottleSpaceCleaner" });
    }

    /// <summary>
    /// Picks a tool for footprints and floor grime among spray, soap and mop.
    /// </summary>
    private static string SelectScrubTool(EntityUid owner, AndroidBlackboard bb)
    {
        if (Inventory(owner, bb) is not { } inventory)
            return "MopItem";

        var options = new List<string>();

        if (OwnsPrototype(bb, owner, inventory, "SprayBottleSpaceCleaner"))
            options.Add("SprayBottleSpaceCleaner");

        var soap = inventory.OwnedItems.FirstOrDefault(e => bb.EntityManager.HasComponent<FloorCleanerComponent>(e));
        if (soap != default && bb.EntityManager.GetComponent<MetaDataComponent>(soap).EntityPrototype is { } soapProto)
            options.Add(soapProto.ID);

        if (OwnsPrototype(bb, owner, inventory, "MopItem"))
            options.Add("MopItem");

        return options.Count == 0 ? "MopItem" : bb.Random.Pick(options);
    }

    private static EntityUid? GetCurrentToolTarget(EntityUid owner, AndroidBlackboard bb)
    {
        return Inventory(owner, bb)?.CurrentToolTarget is { } target && !bb.EntityManager.Deleted(target) ? target : null;
    }

    /// <summary>
    /// Records the failure to reach a sink or disposal unit so another one is picked, and cools the branch down.
    /// </summary>
    private static void OnServiceMoveFailed(EntityUid owner, AndroidBlackboard bb)
    {
        if (Inventory(owner, bb) is not { ServiceTarget: { } station } inventory)
            return;

        if (bb.EntityManager.TryGetComponent<AndroidComponent>(owner, out var android))
        {
            var config = bb.Config;
            bb.Blacklist.RecordFailure(android, station, config.BlacklistThreshold, TimeSpan.FromSeconds(config.BlacklistTtlSeconds), bb.Timing.CurTime);
        }

        bb.SetBranchCooldown(inventory.ServiceKind == AndroidServiceKind.Bucket ? BucketBranch : TrashBranch);
        inventory.ServiceTarget = null;
        inventory.ServiceKind = AndroidServiceKind.None;
    }

    private static void OnResupplyMoveFailed(EntityUid owner, AndroidBlackboard bb)
    {
        if (Inventory(owner, bb) is not { } inventory)
            return;

        bb.SetBranchCooldown(ResupplyBranch);
        inventory.ServiceTarget = null;
        inventory.ServiceKind = AndroidServiceKind.None;
    }

    /// <summary>
    /// World positions of the known puddles within the cluster radius of the android.
    /// </summary>
    private static List<Vector2> NearbyPuddles(EntityUid owner, AndroidBlackboard bb)
    {
        var result = new List<Vector2>();

        if (Memory(owner, bb) is not { } memory || !bb.EntityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform))
            return result;

        var origin = bb.Transform.GetWorldPosition(ownerXform);

        foreach (var target in memory.KnownDirtyTargets)
        {
            if (!bb.EntityManager.HasComponent<PuddleComponent>(target) ||
                !bb.EntityManager.TryGetComponent<TransformComponent>(target, out var targetXform))
            {
                continue;
            }

            var position = bb.Transform.GetWorldPosition(targetXform);
            if ((position - origin).Length() <= PuddleClusterRadius)
                result.Add(position);
        }

        return result;
    }

    private static bool HasPuddleCluster(EntityUid owner, AndroidBlackboard bb)
    {
        if (bb.IsBranchOnCooldown(CleanadeBranch) || Inventory(owner, bb) is not { } inventory)
            return false;

        // Belt items may be filled after the loadout was registered, so rescan for the cleanade.
        return bb.System<AndroidInventorySystem>().FindAndRegisterOwnedTool(owner, inventory, "CleanerGrenade").HasValue &&
               NearbyPuddles(owner, bb).Count >= PuddleClusterThreshold;
    }

    /// <summary>
    /// Tile center in the middle of the nearby puddle cluster.
    /// </summary>
    private static EntityCoordinates? GetPuddleClusterCenter(EntityUid owner, AndroidBlackboard bb)
    {
        if (!bb.EntityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform))
            return null;

        var puddles = NearbyPuddles(owner, bb);
        var center = puddles.Count > 0
            ? puddles.Aggregate(Vector2.Zero, (sum, p) => sum + p) / puddles.Count
            : bb.Transform.GetWorldPosition(ownerXform);

        var gridUid = ownerXform.GridUid ?? ownerXform.ParentUid;
        if (!bb.EntityManager.TryGetComponent<MapGridComponent>(gridUid, out var grid))
            return ownerXform.Coordinates;

        var mapSys = bb.System<SharedMapSystem>();
        var tile = mapSys.WorldToTile(gridUid, grid, center);
        return new EntityCoordinates(gridUid, mapSys.TileCenterToVector(gridUid, grid, tile));
    }

    private static bool ShouldFleeFromCleanade(EntityUid owner, AndroidBlackboard bb)
    {
        if (bb.IsBranchOnCooldown(CleanadeFleeBranch) ||
            Inventory(owner, bb) is not { CleanadeFleeUntil: { } until, CleanadeFleeTarget: not null } inventory)
        {
            return false;
        }

        if (bb.Timing.CurTime < until)
            return true;

        inventory.CleanadeFleeTarget = null;
        inventory.CleanadeFleeUntil = null;
        return false;
    }

    /// <summary>
    /// The android's flashlight, registering a belt flashlight filled after spawn.
    /// </summary>
    private static EntityUid? FindFlashlight(EntityUid owner, AndroidBlackboard bb, AndroidInventoryComponent inventory)
    {
        if (bb.System<AndroidInventorySystem>().FindAndRegisterOwnedTool(owner, inventory, "FlashlightLantern") is { } lantern)
            return lantern;

        foreach (var item in inventory.OwnedItems)
        {
            if (!bb.EntityManager.Deleted(item) &&
                (bb.EntityManager.HasComponent<HandheldLightComponent>(item) || bb.EntityManager.HasComponent<UnpoweredFlashlightComponent>(item)))
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>
    /// Current and maximum battery charge of a power-cell flashlight, or null if it has no battery.
    /// </summary>
    private static (float Charge, float Max)? FlashlightCharge(EntityUid flashlight, AndroidBlackboard bb)
    {
        if (!bb.EntityManager.HasComponent<PowerCellSlotComponent>(flashlight) ||
            !bb.System<PowerCellSystem>().TryGetBatteryFromSlotOrEntity(flashlight, out var battery))
        {
            return null;
        }

        return (bb.System<SharedBatterySystem>().GetCharge(battery.Value.AsNullable()), battery.Value.Comp.MaxCharge);
    }

    private static bool FlashlightNeedsCharging(EntityUid owner, AndroidBlackboard bb)
    {
        if (bb.IsBranchOnCooldown(FlashlightRechargeBranch) ||
            ShouldFleeFromCleanade(owner, bb) ||
            Inventory(owner, bb) is not { } inventory ||
            FindFlashlight(owner, bb, inventory) is not { } flashlight)
        {
            return false;
        }

        return FlashlightCharge(flashlight, bb) is { Max: > 0, Charge: <= 0 };
    }

    /// <summary>
    /// The flashlight should be toggled: on in a dark area, off in a bright one.
    /// </summary>
    private static bool ShouldUseFlashlight(EntityUid owner, AndroidBlackboard bb)
    {
        if (bb.IsBranchOnCooldown(FlashlightToggleBranch) ||
            bb.EntityManager.TryGetComponent<AndroidComponent>(owner, out var android) && android.Status == AndroidStatus.OnBreak ||
            Inventory(owner, bb) is not { } inventory ||
            !bb.EntityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform) ||
            FindFlashlight(owner, bb, inventory) is not { } flashlight)
        {
            return false;
        }

        // A battery flashlight with a dead cell is left to the recharge branch.
        if (bb.EntityManager.HasComponent<HandheldLightComponent>(flashlight) &&
            !bb.EntityManager.HasComponent<UnpoweredFlashlightComponent>(flashlight) &&
            FlashlightCharge(flashlight, bb) is not { Charge: > 0 })
        {
            return false;
        }

        var isOn = bb.EntityManager.TryGetComponent<PointLightComponent>(flashlight, out var light) && light.Enabled;
        var ambient = ComputeAmbientIllumination(owner, ownerXform, flashlight, bb);

        return !isOn && ambient <= DarkThreshold || isOn && ambient >= BrightThreshold;
    }

    /// <summary>
    /// Sums the contribution of nearby active point lights, ignoring the flashlight and anything the android carries.
    /// </summary>
    private static float ComputeAmbientIllumination(EntityUid owner, TransformComponent ownerXform, EntityUid flashlight, AndroidBlackboard bb)
    {
        var ownerPos = bb.Transform.GetWorldPosition(ownerXform);
        var total = 0f;

        foreach (var (ent, light) in bb.System<EntityLookupSystem>().GetEntitiesInRange<PointLightComponent>(ownerXform.Coordinates, 6f))
        {
            if (ent == flashlight || !light.Enabled || light.Energy <= 0f || light.Radius <= 0f ||
                !bb.EntityManager.TryGetComponent<TransformComponent>(ent, out var xform) ||
                xform.ParentUid == owner)
            {
                continue;
            }

            var dist = (bb.Transform.GetWorldPosition(xform) - ownerPos).Length();
            if (dist < light.Radius)
                total += light.Energy * (1f - dist / light.Radius);
        }

        return total;
    }

    /// <summary>
    /// Nearest power cell charger, cached on the inventory for the recharge action.
    /// </summary>
    private static EntityCoordinates? FindFlashlightChargerCoordinates(EntityUid owner, AndroidBlackboard bb)
    {
        if (Inventory(owner, bb) is not { } inventory)
            return null;

        if (bb.CoordinatesOf(inventory.FlashlightCharger) is { } cached)
            return cached;

        if (!bb.EntityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform))
            return null;

        var ownerPos = bb.Transform.GetWorldPosition(ownerXform);
        EntityUid? nearest = null;
        var nearestDist = float.MaxValue;

        foreach (var (ent, _) in bb.System<EntityLookupSystem>().GetEntitiesInRange<ChargerComponent>(ownerXform.Coordinates, 32f))
        {
            if (!bb.EntityManager.TryGetComponent<TransformComponent>(ent, out var xform))
                continue;

            var dist = (bb.Transform.GetWorldPosition(xform) - ownerPos).LengthSquared();
            if (dist >= nearestDist)
                continue;

            nearestDist = dist;
            nearest = ent;
        }

        inventory.FlashlightCharger = nearest;
        return bb.CoordinatesOf(nearest);
    }
}
