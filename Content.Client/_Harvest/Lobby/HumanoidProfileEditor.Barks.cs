// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Goobstation.Common.Barks;

namespace Content.Client.Lobby.UI;

public sealed partial class HumanoidProfileEditor
{
    /// <summary>
    /// Bark names are plain strings in the prototypes: use the optional bark-name-ID translation,
    /// without the missing-key warnings Loc.GetString would log for every bark.
    /// </summary>
    private static string BarkName(BarkPrototype bark)
    {
        return Loc.TryGetString($"bark-name-{bark.ID}", out var name) ? name : bark.Name;
    }
}
