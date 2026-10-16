// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.AndroidNpc.Components;
using Content.Server.NPC.Systems;
using Content.Shared.Buckle;
using Content.Shared.Buckle.Components;
using Content.Server.Nutrition.EntitySystems;
using Content.Shared.Inventory;
using Content.Shared.Nutrition.Components;
using Content.Shared.Smoking;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Server.AndroidNpc.Systems;

/// <summary>
/// Lights and equips a cigarette for androids that are on a smoke break.
/// </summary>
public sealed class AndroidBreakSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly SmokingSystem _smoking = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedBuckleSystem _buckle = default!;
    [Dependency] private readonly NPCSteeringSystem _steering = default!;
    [Dependency] private readonly ClaimedTargetsSystem _claims = default!;

    /// <summary>
    /// Ends the android's break, wherever it is in it: frees and leaves its chair, puts out the cigarette
    /// and resets the break state. The next break timer is left to the caller.
    /// </summary>
    public void EndBreak(EntityUid uid)
    {
        if (!TryComp<AndroidComponent>(uid, out var android))
            return;

        if (android.BreakChair is { } chair)
            _claims.ReleaseClaim(chair);

        if (TryComp<BuckleComponent>(uid, out var buckle) && buckle.Buckled)
            _buckle.TryUnbuckle(uid, uid);

        StopSmoking(uid);
        _steering.Unregister(uid);

        android.BreakState = AndroidBreakState.None;
        android.BreakUntil = TimeSpan.Zero;
        android.BreakChair = null;
        android.BreakAtVenue = false;
        android.BreakSeatAttempts = 0;
        android.BreakSpot = null;

        if (android.Status == AndroidStatus.OnBreak)
            android.Status = AndroidStatus.Working;
    }

    /// <summary>
    /// Tries to put a lit cigarette in the android's mask slot. If the android already has one,
    /// just keeps it lit. Returns true if a cigarette is ready to smoke.
    /// </summary>
    public bool TrySmoke(EntityUid owner)
    {
        if (EntityManager.Deleted(owner))
            return false;

        if (!EntityManager.TryGetComponent<AndroidMemoryComponent>(owner, out var memory))
            return false;

        // Already have one equipped?
        if (_inventory.TryGetSlotEntity(owner, "mask", out var maskItem) &&
            EntityManager.TryGetComponent<SmokableComponent>(maskItem, out var existing))
        {
            if (existing.State == SmokableState.Lit)
                return true;

            if (existing.State == SmokableState.Burnt)
            {
                _inventory.TryUnequip(owner, "mask", true, true, predicted: false);
                EntityManager.QueueDeleteEntity(maskItem.Value);
                memory.SmokingCigarette = null;
            }
            else
            {
                _smoking.SetSmokableState(maskItem.Value, SmokableState.Lit, existing);
                memory.SmokingCigarette = maskItem;
                return true;
            }
        }

        var cigarette = FindCigarette(owner) ?? SpawnCigarette(owner);
        if (cigarette is not { } item)
            return false;

        // Make sure the cigarette is free of its pack / container.
        _container.TryRemoveFromContainer(item, force: true);

        // If it ended up somewhere other than the android, drop it next to them.
        if (EntityManager.TryGetComponent<TransformComponent>(item, out var itemXform) &&
            itemXform.ParentUid != owner)
        {
            _transform.DropNextTo(item, owner);
        }

        _smoking.SetSmokableState(item, SmokableState.Lit);

        if (!_inventory.TryEquip(owner, owner, item, "mask", true, true, false))
        {
            EntityManager.QueueDeleteEntity(item);
            return false;
        }

        memory.SmokingCigarette = item;
        return true;
    }

    /// <summary>
    /// Removes and deletes any cigarette the android was smoking during a break.
    /// </summary>
    public void StopSmoking(EntityUid owner)
    {
        if (EntityManager.TryGetComponent<AndroidMemoryComponent>(owner, out var memory))
            memory.SmokingCigarette = null;

        if (_inventory.TryGetSlotEntity(owner, "mask", out var maskItem) &&
            EntityManager.HasComponent<SmokableComponent>(maskItem))
        {
            _inventory.TryUnequip(owner, "mask", true, true, predicted: false);
            EntityManager.QueueDeleteEntity(maskItem.Value);
        }
    }

    private EntityUid? FindCigarette(EntityUid owner)
    {
        var query = EntityQueryEnumerator<SmokableComponent, TransformComponent>();

        while (query.MoveNext(out var cig, out var smokable, out var xform))
        {
            if (smokable.State == SmokableState.Burnt)
                continue;

            if (xform.ParentUid == owner)
                return cig;

            // Walk up the parent chain to see if the cigarette is in a pack inside the android.
            var parent = xform.ParentUid;
            while (parent.IsValid())
            {
                if (parent == owner)
                    return cig;

                if (!EntityManager.TryGetComponent<TransformComponent>(parent, out var parentXform))
                    break;

                parent = parentXform.ParentUid;
            }
        }

        return null;
    }

    private EntityUid? SpawnCigarette(EntityUid owner)
    {
        if (!EntityManager.TryGetComponent<TransformComponent>(owner, out var xform))
            return null;

        if (!_prototypeManager.TryIndex("Cigarette", out EntityPrototype? _))
            return null;

        return EntityManager.SpawnEntity("Cigarette", xform.Coordinates);
    }
}
