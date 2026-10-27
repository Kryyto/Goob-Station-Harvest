// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using Content.Server._Harvest.AndroidNpc.BehaviorTree.Actions;
using Content.Server._Harvest.AndroidNpc.BehaviorTree.Nodes;
using Content.Server._Harvest.AndroidNpc.Components;
using Content.Server._Harvest.AndroidNpc.Data;
using Content.Server._Harvest.AndroidNpc.Systems;
using Content.Shared.Interaction;
using Content.Shared.Timing;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;

namespace Content.Server._Harvest.AndroidNpc.BehaviorTree.Actions;

/// <summary>
/// Wrings the dirty mop out into the android's own bucket, which pushes the absorbed filth into the
/// bucket and draws clean water back into the mop. This needs no travelling because the bucket is
/// carried in the android's bag.
/// </summary>
public sealed class WringMopAction : IBehaviorNode
{
    /// <inheritdoc />
    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        // PRE-VALIDATION
        if (blackboard.EntityManager.Deleted(owner))
            return BehaviorStatus.Failure;

        if (!blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
            return BehaviorStatus.Failure;

        var inventorySystem = blackboard.EntityManager.System<AndroidInventorySystem>();

        // Wait out the mop's use delay; otherwise _absorbent.Mop returns early and we look like we failed.
        if (inventorySystem.TryGetMop(owner, inventory, out var mop) &&
            blackboard.EntityManager.TryGetComponent<UseDelayComponent>(mop.Owner, out var useDelay) &&
            blackboard.EntityManager.System<UseDelaySystem>().IsDelayed((mop.Owner, useDelay)))
        {
            return BehaviorStatus.Running;
        }

        // EXECUTE
        if (!inventorySystem.TryWringMop(owner, inventory))
        {
            blackboard.Log.Debug("[WringMop] could not wring the mop out into the bucket");
            blackboard.SetBranchCooldown("WringMop");
            return BehaviorStatus.Failure;
        }

        blackboard.Log.Debug("[WringMop] rinsed the mop in the bucket");

        // POST-VALIDATION
        // Keep running until the mop actually holds enough water to clean with again.
        return inventorySystem.MopNeedsService(owner, inventory)
            ? BehaviorStatus.Running
            : BehaviorStatus.Success;
    }
}

/// <summary>
/// Picks the sink or disposal unit the android should walk to in order to service a tool, and stores
/// it on the inventory component so the following move and service nodes agree on the destination.
/// </summary>
public sealed class SelectServiceStationAction : IBehaviorNode
{
    private readonly AndroidServiceKind _kind;

    /// <summary>
    /// Creates a selector for the given kind of tool maintenance.
    /// </summary>
    public SelectServiceStationAction(AndroidServiceKind kind)
    {
        _kind = kind;
    }

    /// <inheritdoc />
    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        // PRE-VALIDATION
        if (blackboard.EntityManager.Deleted(owner))
            return BehaviorStatus.Failure;

        if (!blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
            return BehaviorStatus.Failure;

        var inventorySystem = blackboard.EntityManager.System<AndroidInventorySystem>();

        // Keep the station we already picked so the android does not dither between two equidistant sinks,
        // unless the station has been blacklisted because we could not reach it.
        if (inventory.ServiceKind == _kind &&
            inventory.ServiceTarget is { } existing &&
            !blackboard.EntityManager.Deleted(existing) &&
            !IsStationBlacklisted(owner, blackboard, existing))
        {
            return BehaviorStatus.Success;
        }

        // EXECUTE
        var found = _kind switch
        {
            AndroidServiceKind.Bucket => inventorySystem.TryFindBucketStation(owner, inventory, out var bucketStation)
                ? bucketStation
                : (EntityUid?)null,
            AndroidServiceKind.Trash => inventorySystem.TryFindDisposalUnit(owner, out var disposal)
                ? disposal
                : (EntityUid?)null,
            _ => null,
        };

        if (found is not { } station || IsStationBlacklisted(owner, blackboard, station))
        {
            inventory.ServiceTarget = null;
            inventory.ServiceKind = AndroidServiceKind.None;
            var branch = _kind == AndroidServiceKind.Bucket ? "Bucket" : "Trash";
            blackboard.SetBranchCooldown(branch);
            blackboard.Log.Debug($"[SelectServiceStation] no station found for {_kind}");
            return BehaviorStatus.Failure;
        }

        inventory.ServiceTarget = station;
        inventory.ServiceKind = _kind;
        blackboard.Log.Debug($"[SelectServiceStation] heading to {station} for {_kind}");

        return BehaviorStatus.Success;
    }

    private static bool IsStationBlacklisted(EntityUid owner, AndroidBlackboard blackboard, EntityUid station)
    {
        if (!blackboard.EntityManager.TryGetComponent<AndroidComponent>(owner, out var android))
            return false;

        var threshold = blackboard.Config.BlacklistThreshold;
        return blackboard.Blacklist.IsBlacklisted(android, station, threshold);
    }

}

/// <summary>
/// At a sink or drain: tips the bucket's dirty water into the plumbing and refills it with clean
/// water so the mop has something to rinse in again.
/// </summary>
public sealed class ServiceBucketAction : IBehaviorNode
{
    /// <inheritdoc />
    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        var sawmill = blackboard.Log;

        // PRE-VALIDATION
        if (blackboard.EntityManager.Deleted(owner))
            return BehaviorStatus.Failure;

        if (!blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
            return BehaviorStatus.Failure;

        if (inventory.ServiceTarget is not { } station || blackboard.EntityManager.Deleted(station))
        {
            ClearService(inventory);
            return BehaviorStatus.Failure;
        }

        var inventorySystem = blackboard.EntityManager.System<AndroidInventorySystem>();

        var interaction = blackboard.EntityManager.System<SharedInteractionSystem>();
        if (!interaction.InRangeUnobstructed(owner, station, SharedInteractionSystem.InteractionRange))
        {
            sawmill.Debug($"[ServiceBucket] {station} is not in interaction range");
            return BehaviorStatus.Failure;
        }

        // EXECUTE
        if (!inventorySystem.TryServiceBucket(owner, inventory, station))
        {
            // This station cannot help after all; forget it so a different one is chosen next tick.
            sawmill.Debug($"[ServiceBucket] {station} could not service the bucket");
            blackboard.SetBranchCooldown("Bucket");
            ClearService(inventory);
            return BehaviorStatus.Failure;
        }

        sawmill.Debug($"[ServiceBucket] serviced the bucket at {station}");

        // POST-VALIDATION
        if (inventorySystem.BucketNeedsService(owner, inventory))
        {
            // Dumping and refilling can take several interactions, e.g. when the drain buffer is small.
            return BehaviorStatus.Running;
        }

        ClearService(inventory);
        return BehaviorStatus.Success;
    }

    private static void ClearService(AndroidInventoryComponent inventory)
    {
        inventory.ServiceTarget = null;
        inventory.ServiceKind = AndroidServiceKind.None;
    }
}

/// <summary>
/// At a disposal unit: tips the whole trash bag into it so the bag can be filled again.
/// </summary>
public sealed class DumpTrashAction : IBehaviorNode
{
    /// <inheritdoc />
    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        var sawmill = blackboard.Log;

        // PRE-VALIDATION
        if (blackboard.EntityManager.Deleted(owner))
            return BehaviorStatus.Failure;

        if (!blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
            return BehaviorStatus.Failure;

        if (inventory.ServiceTarget is not { } disposal || blackboard.EntityManager.Deleted(disposal))
        {
            ClearService(inventory);
            return BehaviorStatus.Failure;
        }

        var inventorySystem = blackboard.EntityManager.System<AndroidInventorySystem>();

        var interaction = blackboard.EntityManager.System<SharedInteractionSystem>();
        if (!interaction.InRangeUnobstructed(owner, disposal, SharedInteractionSystem.InteractionRange))
        {
            sawmill.Debug($"[DumpTrash] {disposal} is not in interaction range");
            return BehaviorStatus.Failure;
        }

        // EXECUTE
        if (!inventorySystem.TryDumpTrashBag(owner, inventory, disposal))
        {
            // The unit refused the trash (full, unpowered, unanchored); try somewhere else.
            sawmill.Debug($"[DumpTrash] {disposal} did not take the trash");
            blackboard.SetBranchCooldown("Trash");
            ClearService(inventory);
            return BehaviorStatus.Failure;
        }

        sawmill.Debug($"[DumpTrash] emptied the trash bag into {disposal}");

        ClearService(inventory);
        return BehaviorStatus.Success;
    }

    private static void ClearService(AndroidInventoryComponent inventory)
    {
        inventory.ServiceTarget = null;
        inventory.ServiceKind = AndroidServiceKind.None;
    }
}

/// <summary>
/// Restocks the android's required loadout by spawning missing items.
/// This node assumes the android is already at the resupply location.
/// </summary>
public sealed class RestockAtAction : IBehaviorNode
{
    /// <inheritdoc />
    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        // PRE-VALIDATION
        if (blackboard.EntityManager.Deleted(owner))
            return BehaviorStatus.Failure;

        if (!blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
            return BehaviorStatus.Failure;

        var inventorySystem = blackboard.EntityManager.System<AndroidInventorySystem>();

        // EXECUTE
        inventorySystem.ResupplyTools(owner, inventory);
        inventorySystem.RestockLoadout(owner, inventory);

        // POST-VALIDATION
        if (blackboard.EntityManager.TryGetComponent<AndroidComponent>(owner, out var android))
        {
            if (inventorySystem.NeedsResupply(owner, inventory))
                android.SupplyLevel = MathF.Max(android.SupplyLevel, 0.2f);
            else
                android.SupplyLevel = 1f;
        }

        var result = inventorySystem.NeedsResupply(owner, inventory)
            ? BehaviorStatus.Running
            : BehaviorStatus.Success;

        if (result == BehaviorStatus.Success)
        {
            // After resupplying, go explore a different cardinal direction.
            PatrolAction.SetRandomPatrolNudge(owner, blackboard, clearVisited: true);
        }

        blackboard.Log.Debug($"[RestockAt] result={result}");
        return result;
    }
}
