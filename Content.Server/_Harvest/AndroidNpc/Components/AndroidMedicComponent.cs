// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameObjects;
using Robust.Shared.Map;

namespace Content.Server._Harvest.AndroidNpc.Components;

/// <summary>
/// Patient care state of a paramedic android.
/// </summary>
[RegisterComponent]
public sealed partial class AndroidMedicComponent : Component
{
    /// <summary>
    /// The crew member being treated or carried to medbay.
    /// </summary>
    [ViewVariables] public EntityUid? Patient { get; set; }

    /// <summary>
    /// Someone who asked to be treated; looked after before anyone else.
    /// </summary>
    [ViewVariables] public EntityUid? Caller { get; set; }

    /// <summary>
    /// When the current treatment or injection is done.
    /// </summary>
    [ViewVariables] public TimeSpan? CareUntil { get; set; }

    /// <summary>
    /// Patient (and whether for epinephrine) the android last announced it was treating, so it says so once.
    /// </summary>
    [ViewVariables] public (EntityUid Patient, bool Injecting)? Announced { get; set; }

    /// <summary>
    /// Patient the android last announced it was carrying to medbay, and when.
    /// </summary>
    [ViewVariables] public (EntityUid Patient, TimeSpan At)? CarryAnnounced { get; set; }

    [ViewVariables] public TimeSpan NextScanAt { get; set; }

    /// <summary>
    /// Medbay and morgue beacons of the station, refreshed at <see cref="PlacesRefreshAt"/>.
    /// </summary>
    [ViewVariables] public EntityCoordinates? Medbay { get; set; }

    [ViewVariables] public EntityCoordinates? Morgue { get; set; }

    [ViewVariables] public TimeSpan PlacesRefreshAt { get; set; }

    /// <summary>
    /// When each critical patient last got epinephrine.
    /// </summary>
    [ViewVariables] public Dictionary<EntityUid, TimeSpan> InjectedAt { get; } = new();

    /// <summary>
    /// Patients already carried to medbay, left to the doctors.
    /// </summary>
    [ViewVariables] public HashSet<EntityUid> Delivered { get; } = new();

    /// <summary>
    /// Medbay bed claimed for the patient being carried.
    /// </summary>
    [ViewVariables] public EntityUid? Bed { get; set; }

    /// <summary>
    /// When each patient last got each medicine (dylovene, dexalin).
    /// </summary>
    [ViewVariables] public Dictionary<(EntityUid Patient, string Reagent), TimeSpan> MedicatedAt { get; } = new();
}
