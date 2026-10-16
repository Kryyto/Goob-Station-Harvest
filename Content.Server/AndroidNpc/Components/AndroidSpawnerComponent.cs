// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameObjects;

namespace Content.Server.AndroidNpc.Components;

/// <summary>
/// Map marker that spawns an android NPC when the map is initialized.
/// </summary>
[RegisterComponent]
public sealed partial class AndroidSpawnerComponent : Component
{
    /// <summary>
    /// Job to assign to the spawned android.
    /// </summary>
    [DataField("job")]
    public AndroidJob Job { get; set; } = AndroidJob.Janitor;

    /// <summary>
    /// If true, the spawner entity is deleted once the android is spawned.
    /// Set to false to keep the spawn point around for admin tools or future logic.
    /// </summary>
    [DataField("deleteAfterSpawn")]
    public bool DeleteAfterSpawn { get; set; } = true;

    /// <summary>
    /// Optional override for the android's personal space. Null uses random 1-3 tiles.
    /// </summary>
    [DataField("personalSpace")]
    public float? PersonalSpace { get; set; }
}
