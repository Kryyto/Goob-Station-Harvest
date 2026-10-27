// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Server._Harvest.AndroidNpc.BehaviorTree.Nodes;
using Content.Server._Harvest.AndroidNpc.Components;
using Content.Server._Harvest.AndroidNpc.Systems;
using Content.Server._Harvest.AndroidNpc.Utilities;
using Content.Shared.CombatMode;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.Tag;
using Content.Shared.Weapons.Melee;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;

namespace Content.Server._Harvest.AndroidNpc.BehaviorTree.Actions;

/// <summary>
/// Selects a nearby pest (e.g., a mouse) for the android to hunt.
/// </summary>
public sealed class SelectPestTargetAction : IBehaviorNode
{
    private const float SearchRadius = 10f;
    private static readonly TimeSpan ChaseTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IgnoreEscapedPest = TimeSpan.FromMinutes(2);
    private static readonly ProtoId<TagPrototype> MouseTag = "Mouse";

    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        if (!blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
            return BehaviorStatus.Failure;

        if (blackboard.IsBranchOnCooldown("Pest") ||
            !blackboard.EntityManager.TryGetComponent<AndroidComponent>(owner, out var android))
        {
            return BehaviorStatus.Failure;
        }

        // The mouse just killed stays the target so its body is dragged to the kitchen.
        if (inventory.PestTarget is { Valid: true } corpse && IsDeadPest(corpse, blackboard))
            return BehaviorStatus.Success;

        // Keep a still-valid target, unless it has been escaping for too long: then forget it for a while.
        if (inventory.PestTarget is { Valid: true } existing && IsValidPest(existing, blackboard))
        {
            // Only a live, fleeing mouse can time out; a downed one is being dragged to the kitchen.
            if (blackboard.Timing.CurTime - inventory.PestTargetSince < ChaseTimeout ||
                blackboard.EntityManager.GetComponent<MobStateComponent>(existing).CurrentState != MobState.Alive)
            {
                return BehaviorStatus.Success;
            }

            blackboard.Blacklist.RecordFailure(android, existing, 1, IgnoreEscapedPest, blackboard.Timing.CurTime);
            blackboard.SetBranchCooldown("Pest");
            inventory.PestTarget = null;
            return BehaviorStatus.Failure;
        }

        inventory.PestTarget = null;

        if (!blackboard.EntityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform))
            return BehaviorStatus.Failure;

        var lookup = blackboard.EntityManager.System<EntityLookupSystem>();
        var tagSystem = blackboard.EntityManager.System<TagSystem>();
        var transform = blackboard.EntityManager.System<SharedTransformSystem>();
        var interaction = blackboard.EntityManager.System<SharedInteractionSystem>();
        var ownerPos = transform.GetWorldPosition(ownerXform);
        var ownerGrid = ownerXform.GridUid ?? ownerXform.ParentUid;

        EntityUid? nearest = null;
        var nearestDist = float.MaxValue;

        foreach (var (ent, _) in lookup.GetEntitiesInRange<MobStateComponent>(ownerXform.Coordinates, SearchRadius))
        {
            if (ent == owner || !IsValidPest(ent, blackboard) || blackboard.Blacklist.IsBlacklisted(android, ent, 1))
                continue;

            if (!blackboard.EntityManager.TryGetComponent<TransformComponent>(ent, out var xform))
                continue;

            // Only consider pests the android can actually see, and only on the same grid.
            if (xform.GridUid is not { Valid: true } entGrid || entGrid != ownerGrid)
                continue;

            if (!interaction.InRangeUnobstructed(
                    new Entity<TransformComponent?>(owner, ownerXform),
                    new Entity<TransformComponent?>(ent, xform),
                    SearchRadius,
                    popup: false))
            {
                continue;
            }

            var dist = (transform.GetWorldPosition(xform) - ownerPos).LengthSquared();
            if (dist >= nearestDist)
                continue;

            nearestDist = dist;
            nearest = ent;
        }

        if (!nearest.HasValue)
        {
            blackboard.SetBranchCooldown("Pest");
            return BehaviorStatus.Failure;
        }

        inventory.PestTarget = nearest;
        inventory.PestTargetSince = blackboard.Timing.CurTime;
        blackboard.Log.Debug($"[Pest] selected target {nearest.Value}");
        return BehaviorStatus.Success;
    }

    private static bool IsDeadPest(EntityUid pest, AndroidBlackboard blackboard)
    {
        return !blackboard.EntityManager.Deleted(pest) &&
               blackboard.EntityManager.System<TagSystem>().HasTag(pest, MouseTag) &&
               blackboard.EntityManager.TryGetComponent<MobStateComponent>(pest, out var mobState) &&
               mobState.CurrentState == MobState.Dead;
    }

    private static bool IsValidPest(EntityUid pest, AndroidBlackboard blackboard)
    {
        if (blackboard.EntityManager.Deleted(pest))
            return false;

        if (!blackboard.EntityManager.System<TagSystem>().HasTag(pest, MouseTag))
            return false;

        if (!blackboard.EntityManager.TryGetComponent<MobStateComponent>(pest, out var mobState))
            return false;

        return mobState.CurrentState is MobState.Alive or MobState.Critical;
    }
}

/// <summary>
/// Attacks the currently selected pest with a mop, or with bare hands if no mop is available.
/// Sprays, rags and other janitorial tools are not used as weapons because they do not harm the pest.
/// Returns Success once the pest is dead, in critical condition, or no longer valid.
/// </summary>
public sealed class AttackPestAction : IBehaviorNode
{
    private const float AttackRange = 1.5f;
    private static readonly ProtoId<TagPrototype> MopTag = "Mop";

    private static readonly TimeSpan AttackShoutInterval = TimeSpan.FromSeconds(3);

    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        if (!blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
        {
            SetCombatMode(owner, blackboard, false);
            return BehaviorStatus.Failure;
        }

        var pest = inventory.PestTarget;
        if (!pest.HasValue || blackboard.EntityManager.Deleted(pest.Value))
        {
            inventory.PestTarget = null;
            SetCombatMode(owner, blackboard, false);
            return BehaviorStatus.Success;
        }

        // Pest is dead or otherwise invalid -> we are done attacking.
        // Keep PestTarget set so the post-attack branch can pull/dispose of the corpse.
        if (IsPestDeadOrInvalid(pest.Value, blackboard))
        {
            blackboard.Log.Debug($"[Pest] target {pest.Value} is dead/invalid; attack finished");
            SetCombatMode(owner, blackboard, false);
            return BehaviorStatus.Success;
        }

        if (!blackboard.EntityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform) ||
            !blackboard.EntityManager.TryGetComponent<TransformComponent>(pest.Value, out var pestXform))
        {
            SetCombatMode(owner, blackboard, false);
            return BehaviorStatus.Failure;
        }

        var transform = blackboard.EntityManager.System<SharedTransformSystem>();
        var dist = (transform.GetWorldPosition(pestXform) - transform.GetWorldPosition(ownerXform)).Length();
        if (dist > AttackRange)
        {
            // Let the preceding move action bring us closer.
            SetCombatMode(owner, blackboard, false);
            return BehaviorStatus.Failure;
        }

        // Make sure combat mode is on so the attack actually connects.
        SetCombatMode(owner, blackboard, true);

        // Shout once per pest/cooldown so the android sounds aggressive without spamming every tick.
        TryPestAttackShout(owner, blackboard, pest.Value);

        // Ensure we are attacking with a mop if one is available; otherwise use bare hands.
        var invSys = blackboard.EntityManager.System<AndroidInventorySystem>();
        var tagSystem = blackboard.EntityManager.System<TagSystem>();

        var held = invSys.GetEquippedItem(owner);
        if (!IsMopWeapon(held, blackboard, tagSystem))
        {
            var mop = FindOwnedMop(owner, blackboard, tagSystem);
            if (mop.HasValue)
                invSys.Equip(owner, inventory, mop.Value);
        }

        // Re-check the active hand after equipping.
        held = invSys.GetEquippedItem(owner);

        var meleeSys = blackboard.EntityManager.System<SharedMeleeWeaponSystem>();
        EntityUid weaponUid;
        MeleeWeaponComponent weapon;

        if (IsMopWeapon(held, blackboard, tagSystem) &&
            held.HasValue &&
            blackboard.EntityManager.TryGetComponent<MeleeWeaponComponent>(held.Value, out var heldMelee))
        {
            weaponUid = held.Value;
            weapon = heldMelee;
        }
        else
        {
            // No usable mop; punch the pest with bare hands.
            if (!blackboard.EntityManager.TryGetComponent<MeleeWeaponComponent>(owner, out var ownerMelee))
            {
                blackboard.Log.Warning($"[Pest] {owner} has no unarmed attack");
                SetCombatMode(owner, blackboard, false);
                return BehaviorStatus.Failure;
            }

            weaponUid = owner;
            weapon = ownerMelee;
        }

        meleeSys.AttemptLightAttack(owner, weaponUid, weapon, pest.Value);
        return BehaviorStatus.Running;
    }

    private static bool IsMopWeapon(EntityUid? item, AndroidBlackboard blackboard, TagSystem tagSystem)
    {
        if (!item.HasValue || !blackboard.EntityManager.EntityExists(item.Value))
            return false;

        if (!tagSystem.HasTag(item.Value, MopTag))
            return false;

        if (!blackboard.EntityManager.TryGetComponent<MeleeWeaponComponent>(item.Value, out var melee))
            return false;

        if (melee.MustBeEquippedToUse)
            return false;

        // Rags and other harmless "mops" have no positive damage.
        return melee.Damage.AnyPositive();
    }

    private static EntityUid? FindOwnedMop(EntityUid owner, AndroidBlackboard blackboard, TagSystem tagSystem)
    {
        if (!blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
            return null;

        foreach (var item in inventory.OwnedItems)
        {
            if (IsMopWeapon(item, blackboard, tagSystem))
                return item;
        }

        return null;
    }

    private static void SetCombatMode(EntityUid owner, AndroidBlackboard blackboard, bool enabled)
    {
        if (!blackboard.EntityManager.TryGetComponent<CombatModeComponent>(owner, out var combat))
            return;

        if (combat.IsInCombatMode == enabled)
            return;

        blackboard.EntityManager.System<SharedCombatModeSystem>().SetInCombatMode(owner, enabled, combat);
    }

    private static bool IsPestDeadOrInvalid(EntityUid pest, AndroidBlackboard blackboard)
    {
        if (blackboard.EntityManager.Deleted(pest))
            return true;

        if (!blackboard.EntityManager.TryGetComponent<MobStateComponent>(pest, out var mobState))
            return true;

        // Keep hitting a downed (critical) mouse until it dies: pulling a living one gets stuck.
        // The post-attack branch then pulls/disposes of the body.
        return mobState.CurrentState == MobState.Dead;
    }

    private static void TryPestAttackShout(EntityUid owner, AndroidBlackboard blackboard, EntityUid pest)
    {
        if (!blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
            return;

        var now = blackboard.Timing.CurTime;

        if (inventory.LastPestAttackShoutTarget == pest &&
            now - inventory.LastPestAttackShoutTime < AttackShoutInterval)
        {
            return;
        }

        inventory.LastPestAttackShoutTarget = pest;
        inventory.LastPestAttackShoutTime = now;

        var dialogue = blackboard.System<AndroidDialogueSystem>();
        if (dialogue.LinesFor(owner) is { } lines)
            dialogue.SpeakRandom(owner, lines.Attack);
    }
}

/// <summary>
/// Starts pulling the dead pest so it can be dragged to the kitchen.
/// </summary>
public sealed class StartPullPestAction : IBehaviorNode
{
    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        if (!blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
            return BehaviorStatus.Failure;

        var pest = inventory.PestTarget;
        if (!pest.HasValue || blackboard.EntityManager.Deleted(pest.Value))
        {
            inventory.PestTarget = null;
            return BehaviorStatus.Success;
        }

        if (!blackboard.EntityManager.TryGetComponent<PullableComponent>(pest.Value, out _))
        {
            // Not pullable; leave the target set so the stop-pull action can convert the
            // corpse into a dirty trash target.
            return BehaviorStatus.Success;
        }

        // Already pulling it?
        if (blackboard.EntityManager.TryGetComponent<PullerComponent>(owner, out var puller) &&
            puller.Pulling == pest.Value)
        {
            return BehaviorStatus.Success;
        }

        if (blackboard.EntityManager.TryGetComponent<PullableComponent>(pest.Value, out var pullable) &&
            pullable.Puller == owner)
        {
            return BehaviorStatus.Success;
        }

        var pulling = blackboard.EntityManager.System<PullingSystem>();

        // Pulling takes a free hand: with the mop and another tool in hand, put one away first.
        blackboard.System<AndroidInventorySystem>().TryFreeHand(owner);

        // Use force so the android can pull a dead/crit pest immediately after attacking it,
        // without being blocked by its own attack cooldown.
        if (pulling.TryStartPull(owner, pest.Value, pullableComp: pullable, force: true))
        {
            blackboard.Log.Debug($"[Pest] started pulling {pest.Value}");
            return BehaviorStatus.Success;
        }

        // Could not pull yet (e.g. no free hand, cooldown, interaction blocked). Wait and retry.
        blackboard.Log.Debug($"[Pest] pull not started for {pest.Value}; retrying");
        return BehaviorStatus.Running;
    }
}

/// <summary>
/// Selects a kitchen appliance to drag a dead pest to. Kitchens don't have a nav beacon, so this
/// looks around a kitchen-specific anchor (dinnerware vendor, ChefDrobe, or a sink) first, so the
/// mouse gets dragged there even when it died far away from the kitchen (e.g. on the other side of
/// a large station); falls back to a plain nearby search if none of those exist either.
/// </summary>
public sealed class SelectKitchenDisposalTargetAction : IBehaviorNode
{
    private static readonly string[] KitchenPrototypes =
    {
        "KitchenSpike",
        "KitchenReagentGrinder",
        "KitchenMicrowave",
        "KitchenElectricGrill",
    };

    private const float SearchRadius = 60f;
    private const float AnchorSearchRadius = 30f;

    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        if (!blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
            return BehaviorStatus.Failure;

        if (inventory.PestDisposalTarget is { Valid: true } existing &&
            !blackboard.EntityManager.Deleted(existing))
        {
            return BehaviorStatus.Success;
        }

        inventory.PestDisposalTarget = null;

        if (!blackboard.EntityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform))
            return BehaviorStatus.Failure;

        var transform = blackboard.EntityManager.System<SharedTransformSystem>();
        var lookup = blackboard.EntityManager.System<EntityLookupSystem>();

        // Prefer searching around the kitchen anchor so this works station-wide, not just within
        // SearchRadius of wherever the mouse happened to die.
        EntityUid? nearest = null;

        if (ownerXform.GridUid is { } grid &&
            KitchenLocator.TryFindKitchenAnchor(blackboard.EntityManager, grid, ownerXform.Coordinates, out var kitchenAnchor))
        {
            var anchorMapPos = transform.GetMapCoordinates(kitchenAnchor);
            nearest = FindNearestKitchenAppliance(blackboard, lookup, transform, anchorMapPos, AnchorSearchRadius);
        }

        // No anchor, or nothing found around it; fall back to a plain radius search from the android.
        nearest ??= FindNearestKitchenAppliance(blackboard, lookup, transform, transform.GetMapCoordinates(ownerXform), SearchRadius);

        if (!nearest.HasValue)
        {
            blackboard.Log.Debug("[Pest] no kitchen appliance found to dump pest at");
            blackboard.SetBranchCooldown("Pest");
            return BehaviorStatus.Failure;
        }

        inventory.PestDisposalTarget = nearest;
        blackboard.Log.Debug($"[Pest] selected kitchen disposal target {nearest.Value}");
        return BehaviorStatus.Success;
    }

    private static EntityUid? FindNearestKitchenAppliance(
        AndroidBlackboard blackboard,
        EntityLookupSystem lookup,
        SharedTransformSystem transform,
        MapCoordinates anchor,
        float radius)
    {
        EntityUid? nearest = null;
        var nearestDist = radius * radius;

        foreach (var ent in lookup.GetEntitiesInRange(anchor, radius))
        {
            if (!blackboard.EntityManager.TryGetComponent<MetaDataComponent>(ent, out var meta) ||
                meta.EntityPrototype?.ID is not { } protoId)
            {
                continue;
            }

            var found = false;
            foreach (var kitchen in KitchenPrototypes)
            {
                if (protoId == kitchen)
                {
                    found = true;
                    break;
                }
            }

            if (!found)
                continue;

            if (!blackboard.EntityManager.TryGetComponent<TransformComponent>(ent, out var xform))
                continue;

            var dist = (transform.GetWorldPosition(xform) - anchor.Position).LengthSquared();
            if (dist >= nearestDist)
                continue;

            nearestDist = dist;
            nearest = ent;
        }

        return nearest;
    }
}

/// <summary>
/// Stops pulling the pest, leaving the body on the current tile.
/// </summary>
public sealed class StopPullPestAction : IBehaviorNode
{
    public BehaviorStatus Tick(EntityUid owner, AndroidBlackboard blackboard, float frameTime)
    {
        if (!blackboard.EntityManager.TryGetComponent<AndroidInventoryComponent>(owner, out var inventory))
            return BehaviorStatus.Failure;

        var pest = inventory.PestTarget;
        if (pest is { Valid: true } &&
            blackboard.EntityManager.TryGetComponent<PullableComponent>(pest.Value, out var pullable))
        {
            var pulling = blackboard.EntityManager.System<PullingSystem>();
            pulling.TryStopPull(pest.Value, pullable, owner, ignoreGrab: true);
        }

        // If the android reached the kitchen disposal target, leave the corpse there for the chef
        // instead of converting it into ordinary trash.
        var atKitchen = false;
        if (inventory.PestDisposalTarget is { Valid: true } kitchen &&
            !blackboard.EntityManager.Deleted(kitchen) &&
            blackboard.EntityManager.TryGetComponent<TransformComponent>(owner, out var ownerXform) &&
            blackboard.EntityManager.TryGetComponent<TransformComponent>(kitchen, out var kitchenXform))
        {
            var ownerPos = blackboard.Transform.GetWorldPosition(ownerXform);
            var kitchenPos = blackboard.Transform.GetWorldPosition(kitchenXform);
            atKitchen = (ownerPos - kitchenPos).Length() <= 2.5f;
        }

        // Convert the dead pest into a dirty target only when the android was not hauling it to
        // the kitchen. This keeps delivered rodent bodies near the kitchen appliances.
        if (!atKitchen &&
            pest is { Valid: true } deadPest &&
            !blackboard.Claims.IsClaimedByOther(deadPest, owner) &&
            SelectDirtyTargetAction.IsValidFloorTarget(deadPest, blackboard))
        {
            blackboard.Claims.TryClaim(owner, deadPest, TimeSpan.FromSeconds(blackboard.Config.ClaimTtlSeconds));
            inventory.CurrentToolTarget = deadPest;
            inventory.ToolUseStarted = false;
            blackboard.Log.Debug($"[Pest] converting dead pest {deadPest} into dirty target for pickup");
        }

        inventory.PestTarget = null;
        inventory.PestDisposalTarget = null;
        return BehaviorStatus.Success;
    }
}
