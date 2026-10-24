// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Server.AndroidNpc.BehaviorTree.Nodes;
using Content.Server.AndroidNpc.Components;
using Content.Server.AndroidNpc.Utilities;
using Content.Shared.CombatMode;
using Content.Shared.Damage;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Weapons.Melee;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Systems;
using Content.Shared.Wieldable;
using Content.Shared.Wieldable.Components;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server.AndroidNpc.Systems;

/// <summary>
/// Androids fight back: once one attacker (not the atmosphere or a fire) has dealt <c>retaliationDamage</c>, the
/// android attacks it with its job's weapon (mop, kitchen knife, the bar's shotgun) until it is in critical
/// condition, then goes back to work. It gives up if it loses sight of the attacker for a while.
/// </summary>
public sealed class AndroidCombatSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedMeleeWeaponSystem _melee = default!;
    [Dependency] private readonly SharedGunSystem _gun = default!;
    [Dependency] private readonly SharedCombatModeSystem _combatMode = default!;
    [Dependency] private readonly SharedWieldableSystem _wieldable = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly AndroidInventorySystem _inventory = default!;
    [Dependency] private readonly AndroidBreakSystem _breaks = default!;
    [Dependency] private readonly AndroidDialogueSystem _dialogue = default!;

    private const float MeleeRange = 1.2f;
    private const float RangedRange = 6f;

    /// <summary>
    /// Beyond this distance the attacker counts as out of sight.
    /// </summary>
    private const float SightRange = 15f;

    private static readonly TimeSpan ReloadTime = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Counts the damage each attacker dealt; past the threshold the android turns on it.
    /// Only living attackers count, so fire, cold, vacuum and falling objects never start a fight.
    /// </summary>
    public void OnHurt(EntityUid uid, AndroidComponent android, DamageChangedEvent args)
    {
        if (!args.DamageIncreased || args.DamageDelta is not { } delta ||
            args.Origin is not { } attacker || attacker == uid || !HasComp<MobStateComponent>(attacker))
        {
            return;
        }

        var total = android.DamageTaken.GetValueOrDefault(attacker) + delta.GetTotal().Float();
        android.DamageTaken[attacker] = total;

        if (android.FightTarget == null && total >= _prototypeManager.AndroidConfig().RetaliationDamage && _mobState.IsAlive(attacker))
            StartFight(uid, android, attacker);
    }

    public void StartFight(EntityUid uid, AndroidComponent android, EntityUid target)
    {
        if (android.BreakState != AndroidBreakState.None || android.Status == AndroidStatus.OnBreak)
            _breaks.EndBreak(uid);

        android.FightTarget = target;
        android.FightTargetSeenAt = _timing.CurTime;
        android.Status = AndroidStatus.Fighting;
        _dialogue.SpeakRandom(uid, _prototypeManager.AndroidFeedback().Retaliate);
    }

    public void EndFight(EntityUid uid, AndroidComponent android)
    {
        if (android.FightTarget is { } target)
            android.DamageTaken.Remove(target);

        android.FightTarget = null;
        if (android.Status == AndroidStatus.Fighting)
            android.Status = AndroidStatus.Working;

        if (TryComp<CombatModeComponent>(uid, out var combat))
            _combatMode.SetInCombatMode(uid, false, combat);

        if (_inventory.GetEquippedItem(uid) is { } held && TryComp<WieldableComponent>(held, out var wieldable) && wieldable.Wielded)
            _wieldable.TryUnwield(held, wieldable, uid, force: true);
    }

    public EntityCoordinates? TargetCoordinates(EntityUid uid)
    {
        return TryComp<AndroidComponent>(uid, out var android) && android.FightTarget is { } target && Exists(target)
            ? Transform(target).Coordinates
            : null;
    }

    /// <summary>
    /// How close to get: a shooter stops at a distance once it sees its target, a melee fighter walks up to it.
    /// </summary>
    public float ApproachRange(EntityUid uid)
    {
        return TryComp<AndroidComponent>(uid, out var android) && android.FightTarget is { } target &&
               _inventory.GetEquippedItem(uid) is { } held && HasComp<GunComponent>(held) &&
               _interaction.InRangeUnobstructed(uid, target, RangedRange)
            ? RangedRange - 1f
            : MeleeRange;
    }

    /// <summary>
    /// Attacks the target when in reach. Running while attacking, Failure when the android must get closer
    /// (or the fight is over).
    /// </summary>
    public BehaviorStatus Attack(EntityUid uid)
    {
        if (!TryComp<AndroidComponent>(uid, out var android) || android.FightTarget is not { } target)
            return BehaviorStatus.Failure;

        var now = _timing.CurTime;
        var config = _prototypeManager.AndroidConfig();
        if (!Exists(target) || !_mobState.IsAlive(target) ||
            Transform(target).MapID != Transform(uid).MapID ||
            now - android.FightTargetSeenAt > TimeSpan.FromSeconds(config.RetaliationGiveUpSeconds))
        {
            EndFight(uid, android);
            return BehaviorStatus.Failure;
        }

        var distance = (_transform.GetWorldPosition(target) - _transform.GetWorldPosition(uid)).Length();
        if (distance <= SightRange && _interaction.InRangeUnobstructed(uid, target, SightRange))
            android.FightTargetSeenAt = now;

        var weapon = EnsureWeapon(uid, android);
        if (TryComp<CombatModeComponent>(uid, out var combat) && !combat.IsInCombatMode)
            _combatMode.SetInCombatMode(uid, true, combat);

        if (weapon is { } gunUid && TryComp<GunComponent>(gunUid, out var gun))
        {
            if (distance > RangedRange || !_interaction.InRangeUnobstructed(uid, target, RangedRange))
                return BehaviorStatus.Failure;

            // Reloading is simulated: the empty gun is refilled after a short pause.
            if (_gun.GetAmmoCount(gunUid) == 0 && TryComp<BallisticAmmoProviderComponent>(gunUid, out var ammo))
            {
                if (android.ReloadAt is not { } reloadAt)
                {
                    android.ReloadAt = now + ReloadTime;
                    return BehaviorStatus.Running;
                }

                if (now < reloadAt)
                    return BehaviorStatus.Running;

                _gun.RefillBallisticAmmo((gunUid, ammo));
                android.ReloadAt = null;
            }

            _gun.AttemptShoot(uid, (gunUid, gun), Transform(target).Coordinates, target);
            return BehaviorStatus.Running;
        }

        if (distance > MeleeRange + 0.3f)
            return BehaviorStatus.Failure;

        var meleeUid = weapon is { } held && HasComp<MeleeWeaponComponent>(held) ? held : uid;
        if (TryComp<MeleeWeaponComponent>(meleeUid, out var melee))
            _melee.AttemptLightAttack(uid, meleeUid, melee, target);

        return BehaviorStatus.Running;
    }

    /// <summary>
    /// Takes the job's weapon in hand, taking it out of the bag or spawning it the first time, and wields it
    /// when it needs both hands.
    /// </summary>
    private EntityUid? EnsureWeapon(EntityUid uid, AndroidComponent android)
    {
        if (_prototypeManager.AndroidJobData(android.Job)?.RetaliationWeapon is not { } proto ||
            !TryComp<AndroidInventoryComponent>(uid, out var inventory))
        {
            return null;
        }

        var weapon = inventory.OwnedItems.FirstOrDefault(i => !Deleted(i) && MetaData(i).EntityPrototype?.ID == proto.Id);
        if (!weapon.IsValid())
        {
            weapon = Spawn(proto, Transform(uid).Coordinates);
            inventory.OwnedItems.Add(weapon);
        }

        if (_inventory.GetEquippedItem(uid) != weapon && !_inventory.Equip(uid, inventory, weapon))
            return null;

        if (TryComp<WieldableComponent>(weapon, out var wieldable) && !wieldable.Wielded)
        {
            _inventory.FreeOtherHands(uid, weapon);
            _wieldable.TryWield(weapon, wieldable, uid, showMessage: false);
        }

        return weapon;
    }
}
