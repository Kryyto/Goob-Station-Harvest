// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server._Harvest.AndroidNpc.Components;
using Content.Shared.Ghost;

namespace Content.Server.Ghost;

public sealed partial class GhostSystem
{
    /// <summary>
    /// Androids are listed with the players, so admins can warp to one and follow what it does.
    /// Not a <c>WarpPoint</c>: ninja objectives and teleporters would pick androids as locations.
    /// </summary>
    public IEnumerable<GhostWarp> GetAndroidWarps()
    {
        var query = EntityQueryEnumerator<AndroidComponent, AndroidIdentityComponent>();
        while (query.MoveNext(out var uid, out _, out var identity))
        {
            if (!_mobState.IsAlive(uid) && !_mobState.IsCritical(uid))
                continue;

            var name = Name(uid);
            yield return new GhostWarp(GetNetEntity(uid),
                string.IsNullOrEmpty(identity.JobTitle) ? name : $"{name} ({identity.JobTitle})",
                false);
        }
    }
}
