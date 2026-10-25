// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Damage.Components;
using Content.Server.AlertLevel;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Station.Systems;
using Content.Server.AndroidNpc.Components;
using Content.Server.AndroidNpc.Utilities;
using Content.Server.AndroidNpc.Data;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Systems;
using Content.Shared.Tag;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server.AndroidNpc.Systems;

/// <summary>
/// Monitors androids for dangerous conditions and updates their status.
/// </summary>
public sealed class AndroidSafetySystem : EntitySystem
{
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly AtmosphereSystem _atmos = default!;
    [Dependency] private readonly AlertLevelSystem _alert = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly TagSystem _tag = default!;
    [Dependency] private readonly AndroidCombatSystem _combat = default!;

    /// <summary>
    /// How long an android keeps fleeing after being hurt.
    /// </summary>
    private static readonly TimeSpan HurtFleeTime = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Total damage above which the android stays away from danger until repaired.
    /// </summary>
    private const float BadlyDamaged = 50f;

    private static readonly ProtoId<TagPrototype> MouseTag = "Mouse";

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<AndroidComponent, DamageChangedEvent>(OnDamaged);
    }

    /// <summary>
    /// Remembers real attacks. Bites from the mice a janitor hunts are part of the job, not a reason to flee.
    /// </summary>
    private void OnDamaged(EntityUid uid, AndroidComponent android, DamageChangedEvent args)
    {
        _combat.OnHurt(uid, android, args);

        if (!args.DamageIncreased || args.DamageDelta is not { } delta || delta.GetTotal() < 5 ||
            args.Origin is { } origin && _tag.HasTag(origin, MouseTag))
        {
            return;
        }

        android.LastHurtAt = _timing.CurTime;
        android.FleeFrom = args.Origin is { } attacker && Exists(attacker) ? Transform(attacker).Coordinates : Transform(uid).Coordinates;
        android.FleeTarget = EntityCoordinates.Invalid;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<AndroidComponent, AndroidIdentityComponent>();
        var config = _prototypeManager.AndroidConfig();

        while (query.MoveNext(out var uid, out var android, out _))
        {
            if (android.Status == AndroidStatus.Leaving)
                continue;

            // A fighting android stands its ground until it goes down itself.
            if (android.FightTarget != null)
            {
                if (_entityManager.TryGetComponent<MobStateComponent>(uid, out var mob) && mob.CurrentState != MobState.Alive)
                    _combat.EndFight(uid, android);
                else
                    continue;
            }

            if (IsInDanger(uid, android, config))
            {
                // A hazard (fire, bad air, alert) is fled from where it was first felt.
                if (android.Status != AndroidStatus.Fleeing)
                    android.FleeFrom ??= Transform(uid).Coordinates;

                android.Status = AndroidStatus.Fleeing;
            }
            else if (android.Status == AndroidStatus.Fleeing)
            {
                android.Status = AndroidStatus.Working;
                android.FleeFrom = null;
                android.FleeTarget = EntityCoordinates.Invalid;
            }
        }
    }

    private bool IsInDanger(EntityUid uid, AndroidComponent android, AndroidNpcConfigPrototype? config)
    {
        if (_timing.CurTime < android.EmergencyUntil)
            return true;

        // Just attacked, or badly damaged. Old scratches alone do not keep an android fleeing forever.
        if (_timing.CurTime - android.LastHurtAt < HurtFleeTime && android.LastHurtAt > TimeSpan.Zero)
            return true;

        if (_entityManager.TryGetComponent<DamageableComponent>(uid, out var damageable) && damageable.TotalDamage.Float() > BadlyDamaged)
            return true;

        if (_entityManager.TryGetComponent<MobThresholdsComponent>(uid, out var thresholds))
        {
            // Could be dead/critical; treat as dangerous.
            if (thresholds.CurrentThresholdState is MobState.Critical or MobState.Dead)
                return true;
        }

        // Fire check.
        if (_entityManager.TryGetComponent<FlammableComponent>(uid, out var flammable) && flammable.OnFire)
            return true;

        // Atmos check.
        var mixture = _atmos.GetContainingMixture(uid);

        if (mixture != null)
        {
            if (mixture.Pressure < 80f || mixture.Temperature > 340f)
                return true;

            if (mixture.GetMoles(Gas.Plasma) > 0.01f || mixture.GetMoles(Gas.Tritium) > 0.01f)
                return true;
        }

        // Alert level check.
        if (_entityManager.TryGetComponent<TransformComponent>(uid, out var xform))
        {
            var station = _station.GetStationInMap(xform.MapID);

            if (station is { } stationId)
            {
                var level = _alert.GetLevel(stationId);

                if (level is "delta" or "red")
                    return true;
            }
        }

        return false;
    }

}
