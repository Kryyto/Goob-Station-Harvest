// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Administration.Components;

namespace Content.Shared._Shitmed.Medical.Surgery.Wounds.Systems;

public sealed partial class WoundSystem
{
    /// <summary>
    /// Whether an admin override keeps this body from losing limbs.
    /// </summary>
    private bool IsDismemberBlocked(EntityUid body)
    {
        return TryComp<AdminOverrideComponent>(body, out var adminOverride) && !adminOverride.Dismemberable;
    }
}
