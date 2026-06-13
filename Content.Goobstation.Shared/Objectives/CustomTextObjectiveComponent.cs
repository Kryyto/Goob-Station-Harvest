// SPDX-FileCopyrightText: 2026 Harvest Station
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Goobstation.Shared.Objectives;

/// <summary>
/// Marks an objective as having a custom admin-defined title.
/// The title is stored in the entity's MetaData.EntityName.
/// </summary>
[RegisterComponent]
public sealed partial class CustomTextObjectiveComponent : Component
{
    [DataField]
    public bool IsComplete;
}
