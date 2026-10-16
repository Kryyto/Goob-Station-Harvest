// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Server.AndroidNpc.Components;
using Content.Server.AndroidNpc.Data;
using Content.Server.AndroidNpc.Utilities;
using Content.Server.Decals;
using Content.Shared.Fluids;
using Content.Shared.Interaction;
using Robust.Shared.GameObjects;
using Robust.Shared.Log;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server.AndroidNpc.Systems;

/// <summary>
/// Periodically scans the area around androids for puddles and trash.
/// </summary>
public sealed class AndroidScanSystem : EntitySystem
{
    [Dependency] private readonly IEntityManager _entityManager = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedPuddleSystem _puddle = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly ILogManager _logManager = default!;

    private ISawmill _sawmill = default!;
    private TileScanHelper _scanHelper = default!;

    /// <summary>
    /// Shared scan helper used by behavior tree nodes that need to re-check tile dirtiness.
    /// </summary>
    public TileScanHelper ScanHelper => _scanHelper;

    public override void Initialize()
    {
        base.Initialize();
        _scanHelper = new TileScanHelper(_entityManager, _lookup, _transform, _puddle, _interaction, EntityManager.System<DecalSystem>());
        _sawmill = _logManager.GetSawmill("AndroidNpc");
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<AndroidComponent, AndroidMemoryComponent, AndroidIdentityComponent>();
        var config = _prototypeManager.AndroidConfig();
        var scanned = 0;

        while (query.MoveNext(out var uid, out var android, out var memory, out _))
        {
            // Only jobs that clean need to know about messes; cooks and bartenders skip the scan entirely.
            var job = _prototypeManager.AndroidJobData(android.Job);
            if (job == null || job.TrashTags.Count == 0 && job.MessTags.Count == 0)
                continue;

            var interval = _timing.TickPeriod * config.ScanIntervalTicks;

            if (memory.LastScanTime > TimeSpan.Zero && _timing.CurTime - memory.LastScanTime < interval)
                continue;

            memory.LastScanTime = _timing.CurTime;

            var radius = config.ScanRadius;
            var found = _scanHelper.FindDirtyTargets(uid, radius, job.TrashTags);
            var brokenLights = _scanHelper.FindBrokenLights(uid, radius);
            var dirtyTiles = _scanHelper.FindDirtyTiles(uid, radius);

            memory.KnownDirtyTargets.Clear();
            memory.KnownDirtyTargets.AddRange(found);
            memory.KnownDirtyTargets.AddRange(brokenLights);

            memory.KnownDirtyTiles.Clear();
            memory.KnownDirtyTiles.AddRange(dirtyTiles);

            scanned++;
            _sawmill.Debug($"[Scan] {uid}: found {found.Count} dirty targets, {dirtyTiles.Count} dirty tiles");

            // One scan per tick: androids due at the same time are spread over the next ticks.
            break;
        }

        if (scanned > 0)
            _sawmill.Debug($"AndroidScanSystem.Update: scanned {scanned} androids");
    }
}
