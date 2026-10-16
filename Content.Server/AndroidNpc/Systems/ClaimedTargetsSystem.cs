// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameObjects;
using Robust.Shared.Timing;

namespace Content.Server.AndroidNpc.Systems;

/// <summary>
/// Shared registry of targets currently claimed by an android.
/// Prevents multiple androids from pathing to the same mess.
/// </summary>
public sealed class ClaimedTargetsSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;

    private readonly Dictionary<EntityUid, (EntityUid Owner, TimeSpan Expiry)> _claims = new();

    /// <summary>
    /// Tries to claim a target for the specified android.
    /// </summary>
    public bool TryClaim(EntityUid owner, EntityUid target, TimeSpan duration)
    {
        if (_claims.TryGetValue(target, out var existing) && existing.Owner != owner)
        {
            if (existing.Expiry > _timing.CurTime)
                return false;
        }

        _claims[target] = (owner, _timing.CurTime + duration);
        return true;
    }

    /// <summary>
    /// Releases a target claim.
    /// </summary>
    public void ReleaseClaim(EntityUid target)
    {
        _claims.Remove(target);
    }

    /// <summary>
    /// Checks whether a target is claimed by another android.
    /// </summary>
    public bool IsClaimedByOther(EntityUid target, EntityUid owner)
    {
        if (!_claims.TryGetValue(target, out var existing))
            return false;

        if (existing.Expiry <= _timing.CurTime)
        {
            _claims.Remove(target);
            return false;
        }

        return existing.Owner != owner;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var toRemove = new List<EntityUid>();

        foreach (var (target, claim) in _claims)
        {
            if (claim.Expiry <= now)
                toRemove.Add(target);
        }

        foreach (var target in toRemove)
        {
            _claims.Remove(target);
        }
    }
}
