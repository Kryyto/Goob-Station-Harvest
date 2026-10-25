// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Damage.Systems;
using Content.Shared.Damage.Components;
using System.Linq;
using Content.Server.AndroidNpc.BehaviorTree.Nodes;
using Content.Server.AndroidNpc.Components;
using Content.Server.AndroidNpc.Utilities;
using Content.Shared.Body.Components;
using Content.Shared.Body.Systems;
using Content.Shared.Bed.Components;
using Content.Shared.Buckle;
using Content.Shared.Buckle.Components;
using Content.Shared.Chemistry.EntitySystems;
using Content.Shared._Shitmed.Medical.Surgery.Wounds.Components;
using Content.Shared.Damage;
using Content.Shared.Humanoid;
using Content.Shared.Medical.Healing;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Movement.Pulling.Components;
using Content.Shared.Movement.Pulling.Systems;
using Content.Shared.Stacks;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server.AndroidNpc.Systems;

/// <summary>
/// Paramedic androids: treat injured crew on the spot with the kits they carry, give epinephrine to critical
/// patients and carry critical patients and bodies to medbay. Care is simulated: after a short delay the kit's
/// healing is applied and one use of it is spent.
/// </summary>
public sealed class AndroidMedicSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly DamageableSystem _damageable = default!;
    [Dependency] private readonly SharedBodySystem _body = default!;
    [Dependency] private readonly SharedBuckleSystem _buckle = default!;
    [Dependency] private readonly SharedStackSystem _stack = default!;
    [Dependency] private readonly SharedSolutionContainerSystem _solutions = default!;
    [Dependency] private readonly PullingSystem _pulling = default!;
    [Dependency] private readonly ClaimedTargetsSystem _claims = default!;
    [Dependency] private readonly AndroidInventorySystem _inventory = default!;
    [Dependency] private readonly AndroidDialogueSystem _dialogue = default!;
    [Dependency] private readonly AndroidBreakSystem _breaks = default!;
    [Dependency] private readonly AndroidOrderSystem _orders = default!;

    private const float ScanRange = 12f;
    /// <summary>
    /// Reach for care and pulling; a patient lying on the floor keeps the android a little further away.
    /// </summary>
    private const float CareRange = 2f;

    /// <summary>
    /// A patient this close to the medbay beacon has arrived and is left to the doctors.
    /// </summary>
    private const float MedbayRadius = 2.5f;

    /// <summary>
    /// Patients this close to the medbay or morgue beacon are already there and are not dragged around.
    /// </summary>
    private const float MedbayArea = 5f;

    private const string MorgueBeacon = "station-beacon-morgue";
    private static readonly TimeSpan PlacesRefresh = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Living patients with less damage than this are left alone, unless they asked for care.
    /// </summary>
    private const float InjuredDamage = 20f;

    private const float EpinephrineUnits = 10f;
    private const string Epinephrine = "Epinephrine";
    private const string Medipen = "EmergencyMedipen";

    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TreatTime = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan InjectTime = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan EpinephrineCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CarryAnnounceCooldown = TimeSpan.FromMinutes(1);

    /// <summary>
    /// What the android injects for damage its kits cannot heal, at most once per cooldown per patient.
    /// </summary>
    private static readonly (string Group, string Reagent)[] Medicines = { ("Toxin", "Dylovene"), ("Airloss", "Dexalin") };

    private const float MedicineUnits = 10f;
    private const float MedicineDamage = 5f;
    private static readonly TimeSpan MedicineCooldown = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Beds this close to the medbay beacon are medbay's.
    /// </summary>
    private const float BedSearchRange = 15f;

    private enum Need
    {
        None,
        Treat,
        Carry,
        Inject
    }

    /// <summary>
    /// Answers a call for help: "heal me" makes the caller the next patient, a reported casualty sends the
    /// android to the place named (or to the speaker). Returns what the android says.
    /// </summary>
    public string Call(EntityUid medic, EntityUid speaker, bool healMe, string? place, string language)
    {
        var feedback = _prototypeManager.AndroidFeedback(language);
        if (!TryComp<AndroidMedicComponent>(medic, out var comp))
            return feedback.NotAMedic;

        if (TryComp<AndroidComponent>(medic, out var android) && (android.BreakState != AndroidBreakState.None || android.Status == AndroidStatus.OnBreak))
        {
            _breaks.EndBreak(medic);
            android.NextBreakAt = _timing.CurTime + TimeSpan.FromSeconds(_prototypeManager.AndroidConfig().BreakIntervalSeconds);
        }

        if (healMe)
        {
            if (!TryComp<DamageableComponent>(speaker, out var damage) || damage.TotalDamage <= 0)
                return feedback.NotHurt;

            if (!TryComp<MobStateComponent>(speaker, out _) || !CanTreat(medic, comp, speaker, damage))
                return feedback.CannotTreat;

            _orders.ClearOrder(medic);
            comp.Caller = speaker;
            comp.NextScanAt = TimeSpan.Zero;
            return feedback.AckHealMe;
        }

        // The place is named after its beacon ("Bar", "Infirmerie"), not repeated as said.
        if (place == null ||
            !new NavMapBeaconLocator(EntityManager).TryFindPlace(Transform(medic).Coordinates, place, _dialogue.Classifier, out var name, out var coords))
        {
            _orders.IssueOrder(medic, speaker, AndroidOrderType.RespondToCall, null, Transform(speaker).Coordinates, language: language);
            return feedback.AckInjured;
        }

        _orders.IssueOrder(medic, speaker, AndroidOrderType.RespondToCall, name, coords, language: language);
        return AndroidData.Format(feedback.AckInjuredAt, ("target", name));
    }

    /// <summary>
    /// The patient to look after: the current one while it still needs care, else the most urgent one around
    /// (critical, then bodies and critical patients to carry, then the injured), the caller first.
    /// </summary>
    public EntityUid? SelectPatient(EntityUid medic)
    {
        if (!TryComp<AndroidMedicComponent>(medic, out var comp))
            return null;

        // A caller comes before a patient who only needs patching up.
        if (comp.Patient is { } current && NeedOf(medic, comp, current) is var currentNeed && currentNeed != Need.None &&
            !(currentNeed == Need.Treat && comp.Caller is { } waiting && waiting != current && NeedOf(medic, comp, waiting) != Need.None))
        {
            return current;
        }

        // Done with a patient: look for the next one right away instead of wandering off on patrol.
        if (comp.Patient != null)
            comp.NextScanAt = TimeSpan.Zero;

        DropPatient(medic, comp);

        var now = _timing.CurTime;
        if (now < comp.NextScanAt)
            return null;

        comp.NextScanAt = now + ScanInterval;

        if (comp.Caller is { } caller && NeedOf(medic, comp, caller) == Need.None)
            comp.Caller = null;

        var origin = _transform.GetWorldPosition(medic);
        EntityUid? best = null;
        var bestScore = float.MinValue;

        // Nearby crew, plus anyone critical or dead anywhere on the station, as the crew monitor would show.
        var candidates = _lookup.GetEntitiesInRange<MobStateComponent>(Transform(medic).Coordinates, ScanRange).Select(e => e.Owner).ToHashSet();
        var grid = Transform(medic).GridUid;
        var downed = EntityQueryEnumerator<MobStateComponent, HumanoidAppearanceComponent, TransformComponent>();
        while (downed.MoveNext(out var uid, out var mob, out _, out var xform))
        {
            if (mob.CurrentState != MobState.Alive && xform.GridUid == grid)
                candidates.Add(uid);
        }

        // Whoever called for care, by voice or on the radio, wherever they are.
        if (comp.Caller is { } calling)
            candidates.Add(calling);

        foreach (var patient in candidates)
        {
            var need = NeedOf(medic, comp, patient);
            if (need == Need.None || patient != comp.Caller && !HasComp<HumanoidAppearanceComponent>(patient) || _claims.IsClaimedByOther(patient, medic))
                continue;

            var score = (patient == comp.Caller ? 100f : 0f) + (int) need * 10f - (_transform.GetWorldPosition(patient) - origin).Length() * 0.1f;
            if (score <= bestScore)
                continue;

            best = patient;
            bestScore = score;
        }

        if (best is not { } chosen || !_claims.TryClaim(medic, chosen, TimeSpan.FromSeconds(_prototypeManager.AndroidConfig().ClaimTtlSeconds)))
            return null;

        comp.Patient = chosen;
        return chosen;
    }

    public EntityCoordinates? PatientCoordinates(EntityUid medic)
    {
        return TryComp<AndroidMedicComponent>(medic, out var comp) && comp.Patient is { } patient && Exists(patient)
            ? Transform(patient).Coordinates
            : null;
    }

    public bool MustCarry(EntityUid medic)
    {
        return TryComp<AndroidMedicComponent>(medic, out var comp) && comp.Patient is { } patient &&
               NeedOf(medic, comp, patient) == Need.Carry;
    }

    /// <summary>
    /// Treats the patient once in reach. Running while caring, Success when the patient needs nothing more,
    /// Failure while the android must walk up to it.
    /// </summary>
    public BehaviorStatus Care(EntityUid medic)
    {
        if (!TryComp<AndroidMedicComponent>(medic, out var comp) || comp.Patient is not { } patient)
            return BehaviorStatus.Failure;

        var need = NeedOf(medic, comp, patient);
        if (need is Need.None or Need.Carry)
            return BehaviorStatus.Success;

        if ((_transform.GetWorldPosition(patient) - _transform.GetWorldPosition(medic)).Length() > CareRange)
        {
            comp.CareUntil = null;
            return BehaviorStatus.Failure;
        }

        var now = _timing.CurTime;
        if (comp.CareUntil == null)
        {
            comp.CareUntil = now + (need == Need.Inject ? InjectTime : TreatTime);
            Announce(medic, comp, patient, need);
            return BehaviorStatus.Running;
        }

        if (now < comp.CareUntil)
            return BehaviorStatus.Running;

        comp.CareUntil = null;
        if (need == Need.Inject)
            Inject(medic, comp, patient);
        else
            Treat(medic, comp, patient);

        if (NeedOf(medic, comp, patient) == Need.None && _dialogue.LinesFor(medic) is { } lines)
            _dialogue.SpeakRandom(medic, lines.Treated, ("patient", Name(patient)));

        return BehaviorStatus.Running;
    }

    /// <summary>
    /// Takes hold of the patient to drag it to medbay. Running until the pull starts.
    /// </summary>
    public BehaviorStatus StartCarry(EntityUid medic)
    {
        if (!TryComp<AndroidMedicComponent>(medic, out var comp) || comp.Patient is not { } patient ||
            !TryComp<PullableComponent>(patient, out var pullable))
        {
            return BehaviorStatus.Failure;
        }

        if (pullable.Puller == medic)
            return BehaviorStatus.Success;

        if ((_transform.GetWorldPosition(patient) - _transform.GetWorldPosition(medic)).Length() > CareRange)
            return BehaviorStatus.Failure;

        _inventory.TryFreeHand(medic);
        if (!_pulling.TryStartPull(medic, patient, pullableComp: pullable, force: true))
            return BehaviorStatus.Running;

        // Said once per patient, not every time a patient drifting in and out of crit is picked up again.
        var now = _timing.CurTime;
        if (comp.CarryAnnounced is not { } last || last.Patient != patient || now - last.At > CarryAnnounceCooldown)
        {
            comp.CarryAnnounced = (patient, now);
            if (_dialogue.LinesFor(medic) is { } lines)
                _dialogue.SpeakRandom(medic, lines.Carrying, ("patient", Name(patient)));
        }

        return BehaviorStatus.Success;
    }

    /// <summary>
    /// Where to carry patients: the medbay beacon, or the medical one. Looked up again every so often.
    /// </summary>
    public EntityCoordinates? MedbayCoordinates(EntityUid medic)
    {
        RefreshPlaces(medic);
        return CompOrNull<AndroidMedicComponent>(medic)?.Medbay;
    }

    private void RefreshPlaces(EntityUid medic)
    {
        if (!TryComp<AndroidMedicComponent>(medic, out var comp) || _timing.CurTime < comp.PlacesRefreshAt ||
            !TryComp<AndroidComponent>(medic, out var android) || _prototypeManager.AndroidJobData(android.Job) is not { } job)
        {
            return;
        }

        comp.PlacesRefreshAt = _timing.CurTime + PlacesRefresh;
        var locator = new NavMapBeaconLocator(EntityManager);
        var origin = Transform(medic).Coordinates;

        comp.Medbay = job.WorkBeacon != null && locator.TryFindBeacon(origin, job.WorkBeacon, out var coords) ||
                      job.ResupplyBeacon != null && locator.TryFindBeacon(origin, job.ResupplyBeacon, out coords)
            ? coords
            : null;
        comp.Morgue = locator.TryFindBeacon(origin, MorgueBeacon, out var morgue) ? morgue : null;
    }

    /// <summary>
    /// Where to carry the patient: a free stasis bed at medbay, else a medical bed, else the medbay beacon.
    /// </summary>
    public EntityCoordinates? CarryCoordinates(EntityUid medic)
    {
        return TryComp<AndroidMedicComponent>(medic, out var comp) && FreeBed(medic, comp) is { } bed
            ? Transform(bed).Coordinates
            : MedbayCoordinates(medic);
    }

    /// <summary>
    /// Puts the patient on the bed it was carried to, or lets go of it at medbay, where the doctors take over.
    /// </summary>
    public void FinishCarry(EntityUid medic)
    {
        if (!TryComp<AndroidMedicComponent>(medic, out var comp) || comp.Patient is not { } patient)
            return;

        if (TryComp<PullableComponent>(patient, out var pullable) && pullable.Puller == medic)
            _pulling.TryStopPull(patient, pullable, medic, ignoreGrab: true);

        if (FreeBed(medic, comp) is { } bed)
            _buckle.TryBuckle(patient, medic, bed, popup: false);

        if (comp.Bed is { } claimed)
            _claims.ReleaseClaim(claimed);

        comp.Bed = null;
        comp.Delivered.Add(patient);
    }

    /// <summary>
    /// The medbay bed claimed for the current patient, or a newly claimed free one: stasis beds first.
    /// </summary>
    private EntityUid? FreeBed(EntityUid medic, AndroidMedicComponent comp)
    {
        if (comp.Bed is { } bed && IsFreeBed(medic, comp, bed))
            return bed;

        comp.Bed = null;
        if (MedbayCoordinates(medic) is not { } medbay)
            return null;

        EntityUid? best = null;
        var bestScore = float.MaxValue;
        foreach (var (strap, _) in _lookup.GetEntitiesInRange<StrapComponent>(medbay, BedSearchRange, LookupFlags.Static))
        {
            if (!HasComp<StasisBedComponent>(strap) && !HasComp<HealOnBuckleComponent>(strap) || !IsFreeBed(medic, comp, strap))
                continue;

            var score = (HasComp<StasisBedComponent>(strap) ? 0f : 100f) + (_transform.GetWorldPosition(strap) - _transform.ToMapCoordinates(medbay).Position).Length();
            if (score >= bestScore)
                continue;

            best = strap;
            bestScore = score;
        }

        if (best is { } chosen && _claims.TryClaim(medic, chosen, TimeSpan.FromMinutes(2)))
            comp.Bed = chosen;

        return comp.Bed;
    }

    private bool IsFreeBed(EntityUid medic, AndroidMedicComponent comp, EntityUid bed)
    {
        return !Deleted(bed) &&
               TryComp<StrapComponent>(bed, out var strap) && strap.Enabled &&
               strap.BuckledEntities.All(b => b == comp.Patient) &&
               !_claims.IsClaimedByOther(bed, medic);
    }

    public void DropPatient(EntityUid medic, AndroidMedicComponent comp)
    {
        if (comp.Patient is { } patient)
        {
            _claims.ReleaseClaim(patient);
            if (TryComp<PullableComponent>(patient, out var pullable) && pullable.Puller == medic)
                _pulling.TryStopPull(patient, pullable, medic, ignoreGrab: true);
        }

        if (comp.Bed is { } bed)
            _claims.ReleaseClaim(bed);

        comp.Patient = null;
        comp.Bed = null;
        comp.CareUntil = null;
    }

    private Need NeedOf(EntityUid medic, AndroidMedicComponent comp, EntityUid patient)
    {
        if (patient == medic || Deleted(patient) ||
            !TryComp<MobStateComponent>(patient, out var mob) ||
            !TryComp<DamageableComponent>(patient, out var damage) ||
            _containers.IsEntityInContainer(patient) ||
            TryComp<PullableComponent>(patient, out var pullable) && pullable.Puller is { } puller && puller != medic)
        {
            return Need.None;
        }

        // A badly hurt patient being dragged to medbay is not dropped because it briefly comes out of crit.
        if (pullable?.Puller == medic && mob.CurrentState == MobState.Alive && damage.TotalDamage >= InjuredDamage &&
            !Settled(medic, comp, patient))
        {
            return Need.Carry;
        }

        if (mob.CurrentState == MobState.Critical &&
            (!comp.InjectedAt.TryGetValue(patient, out var injected) || _timing.CurTime - injected > EpinephrineCooldown))
        {
            return Need.Inject;
        }

        // Critical patients and bodies need doctors (oxygen, surgery, defibrillator): bring them onto a medbay bed.
        // A critical patient on its bed keeps being looked after; a body is left to the doctors.
        if (mob.CurrentState is MobState.Critical or MobState.Dead)
        {
            if (!Settled(medic, comp, patient))
                return Need.Carry;

            return mob.CurrentState == MobState.Critical && CanTreat(medic, comp, patient, damage) ? Need.Treat : Need.None;
        }

        var hurt = damage.TotalDamage >= InjuredDamage || patient == comp.Caller && damage.TotalDamage > 0;
        return hurt && CanTreat(medic, comp, patient, damage) ? Need.Treat : Need.None;
    }

    /// <summary>
    /// Whether the patient is already where it should be: lying on a bed, at the morgue, or, when no medbay bed
    /// is free, at medbay (close to the beacon while being carried, anywhere in it otherwise).
    /// </summary>
    private bool Settled(EntityUid medic, AndroidMedicComponent comp, EntityUid patient)
    {
        if (TryComp<BuckleComponent>(patient, out var buckle) && buckle.Buckled)
            return true;

        // Without a medbay beacon there is nowhere to carry patients to.
        if (MedbayCoordinates(medic) is not { } medbay)
            return true;

        var coords = Transform(patient).Coordinates;
        if (comp.Morgue is { } morgue && _transform.InRange(morgue, coords, MedbayArea))
            return true;

        if (comp.Patient == patient ? FreeBed(medic, comp) != null : AnyFreeBed(medic, comp, medbay))
            return false;

        var radius = comp.Patient == patient ? MedbayRadius : MedbayArea;
        return comp.Delivered.Contains(patient) || _transform.InRange(medbay, coords, radius);
    }

    private bool AnyFreeBed(EntityUid medic, AndroidMedicComponent comp, EntityCoordinates medbay)
    {
        foreach (var (strap, _) in _lookup.GetEntitiesInRange<StrapComponent>(medbay, BedSearchRange, LookupFlags.Static))
        {
            if ((HasComp<StasisBedComponent>(strap) || HasComp<HealOnBuckleComponent>(strap)) && IsFreeBed(medic, comp, strap))
                return true;
        }

        return false;
    }

    private bool CanTreat(EntityUid medic, AndroidMedicComponent comp, EntityUid patient, DamageableComponent damage)
    {
        return BestKit(medic, damage) != null || NeededMedicine(comp, patient, damage) != null;
    }

    /// <summary>
    /// A medicine for damage the kits do not heal (poison, lack of air) that the patient has not had recently.
    /// </summary>
    private string? NeededMedicine(AndroidMedicComponent comp, EntityUid patient, DamageableComponent damage)
    {
        foreach (var (group, reagent) in Medicines)
        {
            if (damage.DamagePerGroup.GetValueOrDefault(group).Float() > MedicineDamage &&
                (!comp.MedicatedAt.TryGetValue((patient, reagent), out var at) || _timing.CurTime - at > MedicineCooldown))
            {
                return reagent;
            }
        }

        return null;
    }

    /// <summary>
    /// The carried kit that heals the most of the patient's damage.
    /// </summary>
    private EntityUid? BestKit(EntityUid medic, DamageableComponent patient)
    {
        if (!TryComp<AndroidInventoryComponent>(medic, out var inventory))
            return null;

        EntityUid? best = null;
        var bestHeal = 0f;

        foreach (var item in inventory.OwnedItems)
        {
            if (Deleted(item) || !TryComp<HealingComponent>(item, out var healing))
                continue;

            var heal = healing.Damage.DamageDict
                .Where(d => d.Value < 0)
                .Sum(d => MathF.Min(-d.Value.Float(), patient.Damage.DamageDict.GetValueOrDefault(d.Key).Float()));

            if (heal <= bestHeal)
                continue;

            best = item;
            bestHeal = heal;
        }

        return best;
    }

    private void Treat(EntityUid medic, AndroidMedicComponent comp, EntityUid patient)
    {
        if (!TryComp<DamageableComponent>(patient, out var damage))
            return;

        if (BestKit(medic, damage) is not { } kit)
        {
            if (NeededMedicine(comp, patient, damage) is { } reagent &&
                _solutions.TryGetInjectableSolution(patient, out var bloodstream, out _))
            {
                _solutions.TryAddReagent(bloodstream.Value, reagent, MedicineUnits);
                comp.MedicatedAt[(patient, reagent)] = _timing.CurTime;
            }

            return;
        }

        // Bodies keep their damage on each limb: the kit heals limb after limb until it is used up, as a player's would.
        var healing = Comp<HealingComponent>(kit).Damage * _damageable.UniversalTopicalsHealModifier;
        if (TryComp<BodyComponent>(patient, out var body))
        {
            foreach (var (part, _) in _body.GetBodyChildren(patient, body))
            {
                if (healing.GetTotal() >= 0)
                    break;

                if (HasComp<WoundableComponent>(part) &&
                    _damageable.TryChangeDamage(part, healing, out var healed, true, origin: medic, ignoreBlockers: true, canMiss: false))
                {
                    healing += -healed;
                }
            }
        }
        else
        {
            _damageable.TryChangeDamage(patient, healing, true, origin: medic, canMiss: false);
        }

        if (TryComp<StackComponent>(kit, out var stack))
            _stack.ReduceCount((kit, stack), 1);
        else
            QueueDel(kit);
    }

    private void Inject(EntityUid medic, AndroidMedicComponent comp, EntityUid patient)
    {
        comp.InjectedAt[patient] = _timing.CurTime;
        if (_solutions.TryGetInjectableSolution(patient, out var bloodstream, out _))
            _solutions.TryAddReagent(bloodstream.Value, Epinephrine, EpinephrineUnits);

        // A carried medipen is used up; the android keeps a reserve so it never runs dry mid-rescue.
        if (TryComp<AndroidInventoryComponent>(medic, out var inventory) &&
            inventory.OwnedItems.FirstOrDefault(i => !Deleted(i) && MetaData(i).EntityPrototype?.ID == Medipen) is { Valid: true } pen)
        {
            inventory.OwnedItems.Remove(pen);
            QueueDel(pen);
        }
    }

    private void Announce(EntityUid medic, AndroidMedicComponent comp, EntityUid patient, Need need)
    {
        if (comp.Announced == (patient, need == Need.Inject) || _dialogue.LinesFor(medic) is not { } lines)
            return;

        comp.Announced = (patient, need == Need.Inject);
        _dialogue.SpeakRandom(medic, need == Need.Inject ? lines.Stabilizing : lines.Treating, ("patient", Name(patient)));
    }
}
