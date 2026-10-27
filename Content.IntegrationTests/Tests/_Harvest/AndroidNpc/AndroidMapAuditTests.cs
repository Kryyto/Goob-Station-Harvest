// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Content.Server._Harvest.AndroidNpc.BehaviorTree;
using Content.Server._Harvest.AndroidNpc.BehaviorTree.Actions;
using Content.Server._Harvest.AndroidNpc.Components;
using Content.Server._Harvest.AndroidNpc.Data;
using Content.Server._Harvest.AndroidNpc.Services;
using Content.Server._Harvest.AndroidNpc.Utilities;
using Content.Server.Maps;
using Content.Shared.Buckle.Components;
using Content.Shared.Climbing.Components;
using Content.Shared.Maps;
using Content.Shared.Pinpointer;
using Content.Shared.Placeable;
using Content.Shared.Roles;
using Content.Shared.Station.Components;
using Content.Shared.Tag;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests._Harvest.AndroidNpc;

/// <summary>
/// Every map of the rotation can host the androids: their workplaces are found, and their breaks land somewhere
/// sensible (a stool at the bar, a chair at a dining table in front of the kitchen), never in maintenance.
/// </summary>
[TestFixture]
public sealed class AndroidMapAuditTests
{
    /// <summary>
    /// The maps of <c>DefaultMapPool</c>; <see cref="PoolIsAudited"/> keeps the list in sync.
    /// </summary>
    private static readonly string[] PoolMaps =
    {
        "Atlas", "Amber", "Bagel", "Box", "Cluster", "Core", "Fland", "Marathon", "Meta", "Oasis", "Omega", "Origin",
        "Saltern", "Packed", "FlandHighPop", "OasisHighPop", "Kettle", "Leonid", "Delta", "Chloris", "Cog",
        "Serpentcrest", "Shoukou", "Dove",
    };

    /// <summary>
    /// Maps whose own data breaks any test that loads them, androids or not.
    /// </summary>
    private static readonly Dictionary<string, string> BrokenMaps = new()
    {
        ["Box"] = "the map file has an invalid DeviceNetwork entity reference (entity_deserializer error on load)",
        ["Dove"] = "deleting the map trips a DeviceListSystem debug assert (inconsistent device list in the port)",
    };

    private static readonly MethodInfo FindBreakSeat = typeof(TakeBreakAction)
        .GetMethod("FindBreakSeat", BindingFlags.NonPublic | BindingFlags.Static)!;

    [Test]
    public async Task PoolIsAudited()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var pool = pair.Server.ProtoMan.Index<GameMapPoolPrototype>("DefaultMapPool");
        Assert.That(PoolMaps, Is.EquivalentTo(pool.Maps.Select(m => m.Id)), "Update PoolMaps when the map rotation changes");
        await pair.CleanReturnAsync();
    }

    [Test]
    [TestCaseSource(nameof(PoolMaps))]
    public async Task AndroidsFitTheMap(string mapId)
    {
        if (BrokenMaps.TryGetValue(mapId, out var broken))
            Assert.Ignore($"{mapId}: {broken}");

        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var sEnt = server.EntMan;
        var protoMan = server.ProtoMan;
        var xforms = sEnt.System<SharedTransformSystem>();
        var gameMap = protoMan.Index<GameMapPrototype>(mapId);
        EntityUid map = default, grid = default;
        var androids = new Dictionary<AndroidJobPrototype, EntityUid>();
        var report = new List<string>();
        var problems = new List<string>();

        await server.WaitAssertion(() =>
        {
            Assert.That(sEnt.System<MapLoaderSystem>().TryLoadMap(gameMap.MapPath, out var loaded, out var grids), Is.True, $"Load {gameMap.MapPath}");
            sEnt.System<SharedMapSystem>().InitializeMap(loaded!.Value);
            map = loaded.Value.Owner;
            grid = grids!.MaxBy(g => g.Comp.LocalAABB.Size.LengthSquared()).Owner;

            var stationJobs = gameMap.Stations.Values
                .SelectMany(s => s.StationComponentOverrides.Values)
                .Select(c => c.Component).OfType<Content.Server.Station.Components.StationJobsComponent>()
                .SelectMany(j => j.SetupAvailableJobs.Keys)
                .ToHashSet();

            var locator = new NavMapBeaconLocator(sEnt);
            var origin = new EntityCoordinates(grid, Vector2.Zero);
            foreach (var job in protoMan.EnumeratePrototypes<AndroidJobPrototype>())
            {
                if (job.StationJob is { } stationJob && !stationJobs.Contains(stationJob))
                {
                    report.Add($"{job.ID}: no {stationJob} slot on this map, no android");
                    continue;
                }

                // As the android does: its work beacon, else its resupply one (the paramedic's medical beacon).
                EntityUid? place = null;
                EntityCoordinates? coords = null;
                var found = job.WorkBeacon != null && locator.TryFindBeacon(origin, job.WorkBeacon, out place, out coords) ||
                            job.ResupplyBeacon != null && locator.TryFindBeacon(origin, job.ResupplyBeacon, out place, out coords);
                if (!found || place == null || coords == null)
                {
                    problems.Add($"{job.ID}: workplace '{job.WorkBeacon ?? job.ResupplyBeacon}' not found");
                    continue;
                }

                report.Add($"{job.ID}: works at {Describe(sEnt, place.Value)}");
                androids[job] = sEnt.SpawnEntity(job.EntityPrototype, coords.Value);
            }
        });

        await server.WaitRunTicks(5);

        await server.WaitAssertion(() =>
        {
            var tags = sEnt.System<TagSystem>();
            foreach (var (job, android) in androids)
            {
                var venues = job.BreakVenue is { } fixedVenue ? new[] { fixedVenue } : new[] { AndroidBreakVenue.Bar, AndroidBreakVenue.Kitchen };
                foreach (var venue in venues)
                {
                    var blackboard = AndroidBlackboard.Create(sEnt, android, new BlacklistManager());
                    var (seat, atVenue) = ((EntityUid?, bool)) FindBreakSeat.Invoke(null, new object[] { android, blackboard, venue })!;
                    if (seat is not { } spot)
                    {
                        report.Add($"{job.ID} break at {venue}: none (goes to the other venue)");
                        if (job.BreakVenue != null)
                            problems.Add($"{job.ID}: nowhere to take its break at {venue}");
                        continue;
                    }

                    var kind = sEnt.HasComponent<StrapComponent>(spot)
                        ? tags.HasTag(spot, "BarStool") ? "bar stool" : HasTable(sEnt, spot) ? "chair at a table" : "chair"
                        : "standing by";
                    report.Add($"{job.ID} break at {venue}: {kind} {Describe(sEnt, spot)} (at venue: {atVenue})");

                    if (Describe(sEnt, spot).Contains("maint", System.StringComparison.OrdinalIgnoreCase))
                        problems.Add($"{job.ID}: break at {venue} in maintenance ({Describe(sEnt, spot)})");
                    if (venue == AndroidBreakVenue.Kitchen && kind == "chair")
                        problems.Add($"{job.ID}: kitchen break on a chair without a table ({Describe(sEnt, spot)})");
                }
            }
        });

        TestContext.WriteLine($"{mapId}:\n  " + string.Join("\n  ", report));
        await server.WaitPost(() => sEnt.DeleteEntity(map));
        await pair.CleanReturnAsync();
        Assert.That(problems, Is.Empty, string.Join("\n", problems));
    }

    private static bool HasTable(IEntityManager sEnt, EntityUid seat)
    {
        return sEnt.System<EntityLookupSystem>().GetEntitiesInRange(seat, 1.2f, LookupFlags.Static)
            .Any(e => sEnt.HasComponent<PlaceableSurfaceComponent>(e) && sEnt.HasComponent<ClimbableComponent>(e));
    }

    /// <summary>
    /// The entity, where it is and the nearest station beacon: enough to tell the room.
    /// </summary>
    private static string Describe(IEntityManager sEnt, EntityUid uid)
    {
        var xforms = sEnt.System<SharedTransformSystem>();
        var pos = xforms.GetWorldPosition(uid);
        string? nearest = null;
        var best = float.MaxValue;
        var query = sEnt.AllEntityQueryEnumerator<NavMapBeaconComponent, TransformComponent>();
        while (query.MoveNext(out _, out var beacon, out var xform))
        {
            var dist = (xforms.GetWorldPosition(xform) - pos).Length();
            if (dist >= best)
                continue;

            best = dist;
            nearest = beacon.Text ?? (beacon.DefaultText is { } loc ? Loc.GetString(loc) : null);
        }

        return $"{sEnt.GetComponent<MetaDataComponent>(uid).EntityPrototype?.ID} near '{nearest}' ({best:0} m)";
    }
}
