// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameObjects;

namespace Content.Server._Harvest.AndroidNpc.Components;

/// <summary>
/// Identity information for an android NPC, separate from behavior state.
/// </summary>
[RegisterComponent]
public sealed partial class AndroidIdentityComponent : Component
{
    /// <summary>
    /// First name used to address the android.
    /// </summary>
    [DataField("firstName")] public string FirstName { get; set; } = string.Empty;

    /// <summary>
    /// Generated last name or suffix for the android.
    /// </summary>
    [DataField("lastName")] public string LastName { get; set; } = string.Empty;

    /// <summary>
    /// Full display name shown in the game, e.g. "Androïde Gérard".
    /// </summary>
    [DataField("fullDisplayName")] public string FullDisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Job title used in speech and on the ID card.
    /// </summary>
    [DataField("jobTitle")] public string JobTitle { get; set; } = string.Empty;
}
