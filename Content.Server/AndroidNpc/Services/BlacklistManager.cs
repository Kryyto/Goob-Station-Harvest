// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.AndroidNpc.Components;
using Robust.Shared.GameObjects;

namespace Content.Server.AndroidNpc.Services;

/// <summary>
/// Tracks targets an android has repeatedly failed to act on.
/// </summary>
public interface IBlacklistManager
{
    /// <summary>
    /// Checks whether a target is currently blacklisted for the android.
    /// </summary>
    bool IsBlacklisted(AndroidComponent android, EntityUid target, int threshold);

    /// <summary>
    /// Records a failure against a target. When failures exceed the threshold within the TTL, the target is blacklisted.
    /// </summary>
    void RecordFailure(AndroidComponent android, EntityUid target, int threshold, TimeSpan ttl, TimeSpan now);

    /// <summary>
    /// Removes expired blacklist entries.
    /// </summary>
    void Cleanup(AndroidComponent android, TimeSpan now);
}

/// <summary>
/// Default implementation of <see cref="IBlacklistManager"/>.
/// </summary>
public sealed class BlacklistManager : IBlacklistManager
{
    /// <inheritdoc />
    public bool IsBlacklisted(AndroidComponent android, EntityUid target, int threshold)
    {
        if (!android.BlacklistedTargets.TryGetValue(target, out var entry))
            return false;

        return entry.FailCount >= threshold;
    }

    /// <inheritdoc />
    public void RecordFailure(AndroidComponent android, EntityUid target, int threshold, TimeSpan ttl, TimeSpan now)
    {
        if (!android.BlacklistedTargets.TryGetValue(target, out var entry))
        {
            entry = (now + ttl, 1);
        }
        else
        {
            entry = (now + ttl, entry.FailCount + 1);
        }

        android.BlacklistedTargets[target] = entry;

        if (entry.FailCount >= threshold)
        {
            // Already blacklisted by count; expiry is updated by RecordFailure caller through Cleanup.
        }
    }

    /// <inheritdoc />
    public void Cleanup(AndroidComponent android, TimeSpan now)
    {
        var toRemove = new List<EntityUid>();

        foreach (var (target, entry) in android.BlacklistedTargets)
        {
            if (entry.Expiry <= now)
                toRemove.Add(target);
        }

        foreach (var target in toRemove)
        {
            android.BlacklistedTargets.Remove(target);
        }
    }
}
