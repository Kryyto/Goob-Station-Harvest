// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Server.AndroidNpc.Components;
using Content.Server.AndroidNpc.Data;
using Content.Server.AndroidNpc.Utilities;
using Content.Server.GameTicking;
using Content.Server.Spawners.Components;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared.Bed.Cryostorage;
using Content.Shared.GameTicking;
using Content.Shared.Mind;
using Content.Shared.Mobs;
using Content.Shared.Roles.Jobs;
using Robust.Server.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Server.AndroidNpc.Systems;

/// <summary>
/// Android lifecycle: map spawners, automatic spawning into free job slots of low-population rounds,
/// leaving for cryostorage when a player takes the job, removal of destroyed androids and delayed replacement.
/// </summary>
public sealed class AndroidLifecycleSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly IPlayerManager _playerManager = default!;
    [Dependency] private readonly GameTicker _ticker = default!;
    [Dependency] private readonly StationSystem _station = default!;
    [Dependency] private readonly StationJobsSystem _stationJobs = default!;
    [Dependency] private readonly SharedMindSystem _mind = default!;
    [Dependency] private readonly SharedJobSystem _jobs = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly AndroidBreakSystem _breaks = default!;
    [Dependency] private readonly AndroidDialogueSystem _dialogue = default!;
    [Dependency] private readonly AndroidCustomerSystem _customer = default!;

    private readonly Dictionary<AndroidJob, TimeSpan> _replacementBlockedUntil = new();
    private readonly Dictionary<EntityUid, TimeSpan> _bodyRemovals = new();
    private TimeSpan _nextCheck = TimeSpan.MaxValue;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<AndroidSpawnerComponent, MapInitEvent>(OnSpawnerMapInit);
        SubscribeLocalEvent<GameRunLevelChangedEvent>(OnRunLevelChanged);
        SubscribeLocalEvent<PlayerSpawnCompleteEvent>(OnPlayerSpawned);
        SubscribeLocalEvent<RulePlayerJobsAssignedEvent>(OnPlayersSpawned);
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        SubscribeLocalEvent<AndroidComponent, MobStateChangedEvent>(OnMobStateChanged);
        SubscribeLocalEvent<AndroidComponent, ComponentShutdown>(OnAndroidShutdown);
    }

    private void OnSpawnerMapInit(EntityUid uid, AndroidSpawnerComponent spawner, MapInitEvent args)
    {
        if (TerminatingOrDeleted(uid) || _prototypeManager.AndroidJobData(spawner.Job) is not { } job)
        {
            Log.Warning($"No android job configured for {spawner.Job} on spawner {ToPrettyString(uid)}");
            return;
        }

        var spawned = Spawn(job.EntityPrototype, Transform(uid).Coordinates);

        if (spawner.PersonalSpace is { } space && TryComp<AndroidComponent>(spawned, out var android))
            android.PersonalSpace = space;

        Log.Info($"Spawned android {ToPrettyString(spawned)} ({spawner.Job}) from spawner {ToPrettyString(uid)}");

        if (spawner.DeleteAfterSpawn)
            QueueDel(uid);
    }

    private void OnRunLevelChanged(GameRunLevelChangedEvent ev)
    {
        _nextCheck = ev.New == GameRunLevel.InRound
            ? _timing.CurTime + TimeSpan.FromSeconds(_prototypeManager.AndroidConfig().SpawnDelaySeconds)
            : TimeSpan.MaxValue;
    }

    /// <summary>
    /// The round-start crew has just spawned: fill the jobs left free right away.
    /// </summary>
    private void OnPlayersSpawned(RulePlayerJobsAssignedEvent ev)
    {
        var config = _prototypeManager.AndroidConfig();
        if (config.Enabled && config.AutoSpawnEnabled)
            FillFreeJobs(config);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        _replacementBlockedUntil.Clear();
        _bodyRemovals.Clear();
        _nextCheck = TimeSpan.MaxValue;
    }

    /// <summary>
    /// A player joined with a job an android fills: the android hands over and leaves.
    /// </summary>
    private void OnPlayerSpawned(PlayerSpawnCompleteEvent ev)
    {
        if (ev.JobId is not { } jobId)
            return;

        foreach (var job in _prototypeManager.EnumeratePrototypes<AndroidJobPrototype>())
        {
            if (job.StationJob != jobId)
                continue;

            var android = AndroidsOf(job.Job)
                .FirstOrDefault(a => a.Comp.Status != AndroidStatus.Leaving && _station.GetOwningStation(a) == ev.Station);

            if (android.Comp != null)
                StartLeaving(android);
        }
    }

    private void OnMobStateChanged(Entity<AndroidComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState != MobState.Dead)
            return;

        var config = _prototypeManager.AndroidConfig();
        _bodyRemovals[ent] = _timing.CurTime + TimeSpan.FromSeconds(config.DeadBodyDespawnSeconds);
        BlockReplacement(ent.Comp.Job);
    }

    private void OnAndroidShutdown(Entity<AndroidComponent> ent, ref ComponentShutdown args)
    {
        _bodyRemovals.Remove(ent);

        // A player taking the job is not a reason to bring an android back; that case is covered by the job check.
        if (ent.Comp.Status != AndroidStatus.Leaving)
            BlockReplacement(ent.Comp.Job);
    }

    private void BlockReplacement(AndroidJob job)
    {
        var until = _timing.CurTime + TimeSpan.FromSeconds(_prototypeManager.AndroidConfig().ReplacementDelaySeconds);
        if (!_replacementBlockedUntil.TryGetValue(job, out var current) || current < until)
            _replacementBlockedUntil[job] = until;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;

        foreach (var (body, removeAt) in _bodyRemovals.ToList())
        {
            if (now < removeAt)
                continue;

            _bodyRemovals.Remove(body);
            if (!TerminatingOrDeleted(body))
                QueueDel(body);
        }

        var leaving = EntityQueryEnumerator<AndroidComponent>();
        while (leaving.MoveNext(out var uid, out var android))
        {
            if (android.Status == AndroidStatus.Leaving && now >= android.LeaveDeadline)
                FinishLeaving(uid);
        }

        if (now < _nextCheck)
            return;

        var config = _prototypeManager.AndroidConfig();
        _nextCheck = now + TimeSpan.FromSeconds(config.LifecycleCheckSeconds);

        if (config.Enabled && config.AutoSpawnEnabled && _ticker.RunLevel == GameRunLevel.InRound)
            FillFreeJobs(config);
    }

    /// <summary>
    /// Spawns androids into the stations' free job slots while the round is low-population.
    /// </summary>
    public void FillFreeJobs(AndroidNpcConfigPrototype config)
    {
        var players = _ticker.PlayerGameStatuses.Values.Count(s => s == PlayerGameStatus.JoinedGame);
        if (players > config.LowPopThreshold)
            return;

        var total = AndroidsOf(null).Count();
        var now = _timing.CurTime;
        var stations = EntityQueryEnumerator<StationJobsComponent>();

        while (stations.MoveNext(out var station, out var stationJobs))
        {
            foreach (var job in _prototypeManager.EnumeratePrototypes<AndroidJobPrototype>())
            {
                if (total >= config.MaxAndroids)
                    return;

                if (!job.AutoSpawn || job.StationJob is not { } stationJob ||
                    !_stationJobs.GetJobs(station, stationJobs).ContainsKey(stationJob) ||
                    _replacementBlockedUntil.TryGetValue(job.Job, out var blocked) && now < blocked ||
                    PlayerHasJob(stationJob) ||
                    AndroidsOf(job.Job).Count(a => _station.GetOwningStation(a) == station) >= job.MaxPerStation ||
                    FindSpawnPoint(station, stationJob) is not { } coords)
                {
                    continue;
                }

                var android = Spawn(job.EntityPrototype, coords);
                total++;
                Log.Info($"Spawned android {ToPrettyString(android)} to fill {stationJob} on {ToPrettyString(station)} ({players} players)");
            }
        }
    }

    /// <summary>
    /// Sends the android to the nearest cryostorage; it is removed when it arrives or when the timeout runs out.
    /// </summary>
    public void StartLeaving(Entity<AndroidComponent> android)
    {
        var config = _prototypeManager.AndroidConfig();

        RemComp<AndroidOrderComponent>(android);
        _customer.CancelOrder(android);
        _breaks.EndBreak(android);

        android.Comp.Status = AndroidStatus.Leaving;
        android.Comp.LeaveDeadline = _timing.CurTime + TimeSpan.FromSeconds(config.DespawnTimeoutSeconds);
        android.Comp.LeaveTarget = FindCryostorage(android);

        if (_dialogue.LinesFor(android) is { } lines)
            _dialogue.SpeakRandom(android, lines.Despawn);

        Log.Info($"Android {ToPrettyString(android)} is leaving for cryostorage");
    }

    /// <summary>
    /// Removes a leaving android.
    /// </summary>
    public void FinishLeaving(EntityUid android)
    {
        if (!TerminatingOrDeleted(android))
            QueueDel(android);
    }

    private IEnumerable<Entity<AndroidComponent>> AndroidsOf(AndroidJob? job)
    {
        var query = EntityQueryEnumerator<AndroidComponent>();
        var result = new List<Entity<AndroidComponent>>();

        while (query.MoveNext(out var uid, out var android))
        {
            if ((job == null || android.Job == job) && !_bodyRemovals.ContainsKey(uid))
                result.Add((uid, android));
        }

        return result;
    }

    private bool PlayerHasJob(string jobId)
    {
        foreach (var session in _playerManager.Sessions)
        {
            if (_mind.TryGetMind(session.UserId, out var mind) && _jobs.MindHasJobWithId(mind.Value.Owner, jobId))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The station's job spawn point for the job, falling back to a late-join spawn point.
    /// </summary>
    private EntityCoordinates? FindSpawnPoint(EntityUid station, string jobId)
    {
        EntityCoordinates? fallback = null;
        var query = EntityQueryEnumerator<SpawnPointComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var point, out var xform))
        {
            if (_station.GetOwningStation(uid, xform) != station)
                continue;

            if (point.SpawnType == SpawnPointType.Job && point.Job == jobId)
                return xform.Coordinates;

            if (point.SpawnType == SpawnPointType.LateJoin)
                fallback ??= xform.Coordinates;
        }

        return fallback;
    }

    private EntityCoordinates? FindCryostorage(EntityUid android)
    {
        if (!TryComp<TransformComponent>(android, out var androidXform))
            return null;

        var origin = _transform.GetWorldPosition(androidXform);
        EntityCoordinates? best = null;
        var bestDist = float.MaxValue;
        var query = EntityQueryEnumerator<CryostorageComponent, TransformComponent>();

        while (query.MoveNext(out _, out _, out var xform))
        {
            if (xform.GridUid != androidXform.GridUid)
                continue;

            var dist = (_transform.GetWorldPosition(xform) - origin).LengthSquared();
            if (dist >= bestDist)
                continue;

            bestDist = dist;
            best = xform.Coordinates;
        }

        return best;
    }
}
