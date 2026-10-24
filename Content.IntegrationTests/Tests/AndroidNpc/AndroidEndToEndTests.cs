// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server.AndroidNpc.Components;
using Content.Server.AndroidNpc.Services;
using Content.Server.AndroidNpc.Systems;
using Content.Server.AndroidNpc.Utilities;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Chat.Systems;
using Content.Server.Doors.Systems;
using Content.Server.NPC.Pathfinding;
using Content.Shared.Atmos;
using Content.Shared.Chat;
using Content.Shared.Doors.Components;
using Content.Shared.NPC;
using Content.Shared.Speech.Components;
using NUnit.Framework;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.IntegrationTests.Tests.AndroidNpc;

/// <summary>
/// End-to-end tests on a full station (Saltern): several androids living a shift, real chat messages,
/// breaks at the same bar, and server tick cost.
/// </summary>
[TestFixture]
public sealed class AndroidEndToEndTests
{
    private sealed record Station(TestPair Pair, EntityUid Grid, List<EntityCoordinates> NearBar, EntityUid Map);

    /// <summary>
    /// Loads Saltern with open doors and breathable air, waits for the navmesh, and lists reachable free
    /// floor tiles around the bar, nearest first.
    /// </summary>
    private static async Task<Station> LoadSaltern()
    {
        var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var sEnt = server.EntMan;
        EntityUid grid = default;
        Entity<MapComponent>? map = null;

        await server.WaitAssertion(() =>
        {
            Assert.That(sEnt.System<MapLoaderSystem>().TryLoadMap(new ResPath("/Maps/_Goobstation/saltern.yml"), out map, out var grids), Is.True);
            sEnt.System<SharedMapSystem>().InitializeMap(map!.Value);
            grid = grids!.MaxBy(g => g.Comp.LocalAABB.Size.LengthSquared()).Owner;

            var doors = sEnt.System<DoorSystem>();
            var query = sEnt.AllEntityQueryEnumerator<DoorComponent, TransformComponent>();
            while (query.MoveNext(out var door, out var comp, out var xform))
            {
                if (xform.GridUid != grid || comp.State is DoorState.Welded or DoorState.Open)
                    continue;

                doors.SetState(door, DoorState.Open, comp);
                doors.OnPartialOpen(door, comp);
                doors.SetNextStateChange(door, null, comp);
            }

            var air = new GasMixture(Atmospherics.CellVolume) { Temperature = Atmospherics.T20C };
            air.AdjustMoles(Gas.Oxygen, 22f);
            air.AdjustMoles(Gas.Nitrogen, 83f);
            sEnt.System<AtmosphereSystem>().SetMapAtmosphere(map.Value.Owner, false, air);

            server.System<AndroidConversationSystem>().SetBackend(new Thx01ConversationBackend("", 4f, null));
        });

        await server.WaitRunTicks(300);

        var nearBar = new List<EntityCoordinates>();
        await server.WaitAssertion(() =>
        {
            Assert.That(new NavMapBeaconLocator(sEnt).TryFindBeacon(new EntityCoordinates(grid, Vector2.Zero), "bar", out var bar), Is.True);

            var pathfinding = sEnt.System<PathfindingSystem>();
            static bool IsFree(PathPoly? p) =>
                p != null && p.Data.IsFreeSpace && (p.Data.Flags & (PathfindingBreadcrumbFlag.Space | PathfindingBreadcrumbFlag.Invalid)) == 0;

            PathPoly? start = null;
            for (var dx = -3; dx <= 3 && start == null; dx++)
            for (var dy = -3; dy <= 3 && start == null; dy++)
            {
                var poly = pathfinding.GetPoly(new EntityCoordinates(grid, bar!.Value.Position + new Vector2(dx, dy)));
                if (IsFree(poly))
                    start = poly;
            }

            Assert.That(start, Is.Not.Null, "No free floor near the bar");

            var visited = new HashSet<PathPoly> { start! };
            var queue = new Queue<PathPoly>();
            queue.Enqueue(start!);
            var tiles = new HashSet<Vector2i>();

            while (queue.Count > 0 && nearBar.Count < 40)
            {
                var current = queue.Dequeue();
                var tile = current.Box.Center.Floored();
                if (tiles.Add(tile))
                    nearBar.Add(new EntityCoordinates(grid, new Vector2(tile.X + 0.5f, tile.Y + 0.5f)));

                foreach (var next in current.Neighbors)
                {
                    if (IsFree(next) && visited.Add(next))
                        queue.Enqueue(next);
                }
            }

            Assert.That(nearBar, Has.Count.GreaterThanOrEqualTo(12), "Not enough free floor around the bar");
        });

        return new Station(pair, grid, nearBar, map!.Value.Owner);
    }

    /// <summary>
    /// Deletes the loaded station before returning the pair; a loaded map is not cleaned up by the pool, and its
    /// androids would leak into later tests.
    /// </summary>
    private static async Task Unload(TestPair pair, EntityUid map)
    {
        await pair.Server.WaitPost(() => pair.Server.EntMan.DeleteEntity(map));
        await pair.CleanReturnAsync();
    }

    private static void Name(IEntityManager sEnt, EntityUid android, string firstName)
    {
        var identity = sEnt.GetComponent<AndroidIdentityComponent>(android);
        identity.FirstName = firstName;
        identity.FullDisplayName = $"Androïde {firstName}";
        sEnt.System<MetaDataSystem>().SetEntityName(android, identity.FullDisplayName);
    }

    private static void ClearLastSaid(IEntityManager sEnt)
    {
        var query = sEnt.AllEntityQueryEnumerator<AndroidComponent>();
        while (query.MoveNext(out _, out var android))
        {
            android.LastSaid = null;
            android.RecentLines.Clear();
        }
    }

    /// <summary>
    /// Every line androids said since <see cref="ClearLastSaid"/>, one entry per android that spoke.
    /// </summary>
    private static List<string> AllSaid(IEntityManager sEnt)
    {
        var said = new List<string>();
        var query = sEnt.AllEntityQueryEnumerator<AndroidComponent>();
        while (query.MoveNext(out _, out var android))
            said.AddRange(android.RecentLines);

        return said;
    }

    /// <summary>
    /// Per-system update time from the engine's Prometheus histogram: total seconds, update count, and number of
    /// updates longer than the largest bucket (about 17 ms).
    /// </summary>
    private static Dictionary<string, (double Seconds, double Count, double Slow)> ReadSystemUsage()
    {
        using var stream = new System.IO.MemoryStream();
        Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream).GetAwaiter().GetResult();
        var text = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        var sums = new Dictionary<string, double>();
        var counts = new Dictionary<string, double>();
        var topBucket = new Dictionary<string, (double Le, double Count)>();
        var regex = new System.Text.RegularExpressions.Regex(
            "^robust_entity_systems_update_usage_(sum|count|bucket)[{]system=\"([^\"]+)\"(?:,le=\"([^\"]+)\")?[}] ([0-9.eE+-]+)$");

        foreach (var line in text.Split('\n'))
        {
            var m = regex.Match(line.Trim());
            if (!m.Success)
                continue;

            var system = m.Groups[2].Value;
            var value = double.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
            switch (m.Groups[1].Value)
            {
                case "sum": sums[system] = value; break;
                case "count": counts[system] = value; break;
                case "bucket" when m.Groups[3].Value != "+Inf":
                    var le = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                    if (!topBucket.TryGetValue(system, out var top) || le > top.Le)
                        topBucket[system] = (le, value);
                    break;
            }
        }

        return sums.ToDictionary(kv => kv.Key, kv =>
        {
            var count = counts.GetValueOrDefault(kv.Key);
            return (kv.Value, count, count - topBucket.GetValueOrDefault(kv.Key).Count);
        });
    }

    private static void Say(IEntityManager sEnt, EntityUid speaker, string message)
    {
        sEnt.System<ChatSystem>().TrySendInGameICMessage(speaker, message, InGameICChatType.Speak, ChatTransmitRange.Normal, ignoreActionBlocker: true);
    }

    [Test]
    public async Task GroupMessage_OneSpokespersonInFrenchWithoutStall()
    {
        var (pair, _, nearBar, map) = await LoadSaltern();
        var server = pair.Server;
        var sEnt = server.EntMan;
        var androids = new List<EntityUid>();
        EntityUid speaker = default;
        var allowed = 0;

        await server.WaitAssertion(() =>
        {
            speaker = sEnt.SpawnEntity("MobHuman", nearBar[0]);
            for (var i = 1; i <= 5; i++)
                androids.Add(sEnt.SpawnEntity("MobAndroidJanitor", nearBar[i]));
        });

        await server.WaitRunTicks(5);

        var watch = Stopwatch.StartNew();
        await server.WaitAssertion(() =>
        {
            ClearLastSaid(sEnt);
            Say(sEnt, speaker, "Androïdes, comment ça va ?");
        });
        var sendMs = watch.Elapsed.TotalMilliseconds;

        var worstChunk = 0.0;
        for (var i = 0; i < 6; i++)
        {
            watch.Restart();
            await server.WaitRunTicks(15);
            worstChunk = Math.Max(worstChunk, watch.Elapsed.TotalMilliseconds / 15);
        }

        await server.WaitAssertion(() =>
        {
            // Count every android on the station: one spawned by the map may be the nearest listener.
            var replies = AllSaid(sEnt);
            TestContext.WriteLine($"Group message handled in {sendMs:F1} ms, worst tick {worstChunk:F1} ms; replies: {string.Join(" | ", replies)}");

            Assert.That(replies, Has.Count.EqualTo(1), "Only the nearest android answers a group message");

            var smallTalk = server.System<AndroidConversationSystem>().GetConversation(AndroidJob.Janitor)!
                .Responses.Single(r => r.Lang == "fr" && r.Intent == "small_talk");
            Assert.That(smallTalk.Calm.Concat(smallTalk.Direct), Does.Contain(replies[0]), "The reply is French small talk");
            Assert.That(sendMs, Is.LessThan(100), "Handling a group message must not stall the server");
        });

        await server.WaitAssertion(() =>
        {
            ClearLastSaid(sEnt);
            Say(sEnt, speaker, "Androïdes, suivez-moi");
            // Every android that heard the group order obeys it.
            var xforms = server.System<SharedTransformSystem>();
            var origin = xforms.GetWorldPosition(speaker);
            allowed = androids.Count(a => sEnt.TryGetComponent<ActiveListenerComponent>(a, out var listener) &&
                                          (xforms.GetWorldPosition(a) - origin).Length() <= listener.Range);
        });

        await server.WaitRunTicks(90);

        await server.WaitAssertion(() =>
        {
            var following = androids.Count(a => sEnt.TryGetComponent<AndroidOrderComponent>(a, out var o) && o.OrderType == AndroidOrderType.FollowMe);
            var acks = AllSaid(sEnt);
            var fr = server.ResolveDependency<IPrototypeManager>().AndroidFeedback("fr");

            Assert.That(following, Is.EqualTo(allowed),
                "Every android that heard the order follows");
            // Other lines may happen meanwhile (a janitor chasing a mouse shouts), but only one android acknowledges.
            Assert.That(acks.Count(a => a == $"{fr.AcknowledgementPrefix} {fr.AckFollowMe}"), Is.EqualTo(1), "One French acknowledgement for the group");
        });

        await Unload(pair, map);
    }

    [Test]
    public async Task NamedOrders_AreAcknowledgedInTheSpeakersLanguage()
    {
        var (pair, _, nearBar, map) = await LoadSaltern();
        var server = pair.Server;
        var sEnt = server.EntMan;
        EntityUid android = default;
        EntityUid speaker = default;
        var protos = server.ResolveDependency<IPrototypeManager>();

        await server.WaitAssertion(() =>
        {
            speaker = sEnt.SpawnEntity("MobHuman", nearBar[0]);
            android = sEnt.SpawnEntity("MobAndroidJanitor", nearBar[1]);
            Name(sEnt, android, "Gerard");
        });

        await server.WaitRunTicks(5);
        await server.WaitAssertion(() => Say(sEnt, speaker, "Gerard, suis-moi"));
        await server.WaitRunTicks(60);

        await server.WaitAssertion(() =>
        {
            var fr = protos.AndroidFeedback("fr");
            Assert.That(sEnt.GetComponent<AndroidComponent>(android).LastSaid, Is.EqualTo($"{fr.AcknowledgementPrefix} {fr.AckFollowMe}"));
            Assert.That(sEnt.GetComponent<AndroidOrderComponent>(android).Language, Is.EqualTo("fr"));
            server.System<AndroidOrderSystem>().ClearOrder(android);
        });

        await server.WaitRunTicks(5);
        await server.WaitAssertion(() => Say(sEnt, speaker, "Gerard, follow me"));
        await server.WaitRunTicks(60);

        await server.WaitAssertion(() =>
        {
            var en = protos.AndroidFeedback("en");
            Assert.That(sEnt.GetComponent<AndroidComponent>(android).LastSaid, Is.EqualTo($"{en.AcknowledgementPrefix} {en.AckFollowMe}"));
        });

        await Unload(pair, map);
    }

    [Test]
    public async Task Bartender_ServesAPlayerAtTheRealBar()
    {
        var (pair, _, nearBar, map) = await LoadSaltern();
        var server = pair.Server;
        var sEnt = server.EntMan;
        EntityUid bartender = default;
        EntityUid customer = default;

        await server.WaitAssertion(() =>
        {
            customer = sEnt.SpawnEntity("MobHuman", nearBar[0]);
            bartender = sEnt.SpawnEntity("MobAndroidBartender", nearBar[3]);
            Name(sEnt, bartender, "Rene");
        });

        await server.WaitRunTicks(5);
        await server.WaitAssertion(() => Say(sEnt, customer, "Rene, une bière s'il vous plaît"));
        await server.WaitRunTicks(900);

        await server.WaitAssertion(() =>
        {
            var xform = sEnt.System<SharedTransformSystem>();
            var customerPos = xform.GetWorldPosition(customer);
            var served = false;
            var query = sEnt.AllEntityQueryEnumerator<MetaDataComponent, TransformComponent>();
            while (query.MoveNext(out var uid, out var meta, out var itemXform))
            {
                if (meta.EntityPrototype?.ID == "DrinkBeerglass" && (xform.GetWorldPosition(itemXform) - customerPos).Length() <= 2.5f)
                    served = true;
            }

            var protos = server.ResolveDependency<IPrototypeManager>();
            var said = sEnt.GetComponent<AndroidComponent>(bartender).LastSaid;
            var item = server.System<AndroidServiceSystem>().MenuName(AndroidJob.Bartender, "DrinkBeerglass", "fr");
            var expected = protos.AndroidJobData(AndroidJob.Bartender)!.LinesFor("fr")!.Served
                .Select(l => AndroidData.Format(l, ("item", item), ("customer", sEnt.GetComponent<MetaDataComponent>(customer).EntityName)));
            Assert.That(served, Is.True, "A beer should be put down next to the customer");
            Assert.That(expected, Does.Contain(said), "The bartender answers in French");
        });

        await Unload(pair, map);
    }

    /// <summary>
    /// Four minutes of game time with four janitors who all go on break at once, a cook and a bartender:
    /// no two androids share a seat or stand on top of each other, janitors keep moving, ticks stay cheap.
    /// Any error logged during the run fails the test.
    /// </summary>
    [Test]
    public async Task LongShift_BreaksDoNotStackAndTicksStayCheap()
    {
        var (pair, _, nearBar, map) = await LoadSaltern();
        var server = pair.Server;
        var sEnt = server.EntMan;
        var xform = sEnt.System<SharedTransformSystem>();
        var janitors = new List<EntityUid>();
        var all = new List<EntityUid>();

        await server.WaitAssertion(() =>
        {
            for (var i = 0; i < 4; i++)
            {
                var janitor = sEnt.SpawnEntity("MobAndroidJanitor", nearBar[8 + i * 3]);
                sEnt.GetComponent<AndroidComponent>(janitor).NextBreakAt = server.Timing.CurTime + TimeSpan.FromSeconds(3);
                janitors.Add(janitor);
            }

            all.AddRange(janitors);
            all.Add(sEnt.SpawnEntity("MobAndroidCook", nearBar[5]));
            all.Add(sEnt.SpawnEntity("MobAndroidBartender", nearBar[6]));

            // Record the behavior tree path so a slow tick is logged with the branch that caused it.
            foreach (var android in all)
                sEnt.GetComponent<AndroidComponent>(android).Trace = new List<string>();
        });

        var start = new Dictionary<EntityUid, Vector2>();
        await server.WaitAssertion(() =>
        {
            foreach (var janitor in janitors)
                start[janitor] = xform.GetWorldPosition(janitor);
        });

        server.ResolveDependency<IEntitySystemManager>().MetricsEnabled = true;
        var usageBefore = ReadSystemUsage();

        const int chunk = 30;
        const int chunks = 240; // 4 minutes at 30 ticks per second.
        var maxTravel = janitors.ToDictionary(j => j, _ => 0f);
        var sharedSeats = new List<string>();
        var stacked = new List<string>();
        var stackedFor = new Dictionary<(int, int), int>();
        var worstTickMs = 0.0;
        var totalMs = 0.0;
        var watch = new Stopwatch();

        for (var c = 0; c < chunks; c++)
        {
            watch.Restart();
            await server.WaitRunTicks(chunk);
            var ms = watch.Elapsed.TotalMilliseconds / chunk;
            totalMs += ms;
            worstTickMs = Math.Max(worstTickMs, ms);
            if (ms > 30)
                TestContext.WriteLine($"Slow second t={c}s: {ms:F1} ms per tick");

            await server.WaitAssertion(() =>
            {
                var live = all.Where(a => !sEnt.Deleted(a)).ToList();
                var seated = live
                    .Select(a => (Uid: a, Comp: sEnt.GetComponent<AndroidComponent>(a)))
                    .Where(a => a.Comp.BreakChair != null && a.Comp.BreakState is AndroidBreakState.Moving or AndroidBreakState.Sitting or AndroidBreakState.Resting)
                    .GroupBy(a => a.Comp.BreakChair)
                    .Where(g => g.Count() > 1);

                foreach (var group in seated)
                    sharedSeats.Add($"t={c}s chair {group.Key} shared by {string.Join(", ", group.Select(g => g.Uid))}");

                string Describe(EntityUid a)
                {
                    var comp = sEnt.GetComponent<AndroidComponent>(a);
                    var seat = comp.BreakChair is { } chair ? sEnt.GetComponent<MetaDataComponent>(chair).EntityPrototype?.ID : "none";
                    var buckled = sEnt.TryGetComponent<Content.Shared.Buckle.Components.BuckleComponent>(a, out var b) && b.Buckled;
                    return $"{a}(seat {seat}, spot {comp.BreakSpot?.Position}, buckled {buckled}, attempts {comp.BreakSeatAttempts})";
                }

                var resting = live.Where(a => sEnt.GetComponent<AndroidComponent>(a) is { Status: AndroidStatus.OnBreak, BreakState: AndroidBreakState.Resting }).ToList();
                for (var i = 0; i < resting.Count; i++)
                for (var j = i + 1; j < resting.Count; j++)
                {
                    var distance = (xform.GetWorldPosition(resting[i]) - xform.GetWorldPosition(resting[j])).Length();
                    var pairKey = (Math.Min(resting[i].Id, resting[j].Id), Math.Max(resting[i].Id, resting[j].Id));
                    if (distance >= 0.45f)
                    {
                        stackedFor.Remove(pairKey);
                        continue;
                    }

                    // Walking past a seated android is fine; staying on top of it is not.
                    stackedFor[pairKey] = stackedFor.GetValueOrDefault(pairKey) + 1;
                    if (stackedFor[pairKey] >= 3)
                        stacked.Add($"t={c}s {Describe(resting[i])} and {Describe(resting[j])} {distance:F2} apart");
                }

                foreach (var janitor in janitors.Where(j => !sEnt.Deleted(j)))
                    maxTravel[janitor] = Math.Max(maxTravel[janitor], (xform.GetWorldPosition(janitor) - start[janitor]).Length());
            });
        }

        TestContext.WriteLine($"Average tick {totalMs / chunks:F2} ms, worst 1 s average {worstTickMs:F2} ms");
        var usageAfter = ReadSystemUsage();
        double AndroidMsPerTick(string system) =>
            (usageAfter.GetValueOrDefault(system).Seconds - usageBefore.GetValueOrDefault(system).Seconds) * 1000 / (chunk * chunks);
        double SlowUpdates(string system) =>
            usageAfter.GetValueOrDefault(system).Slow - usageBefore.GetValueOrDefault(system).Slow;

        foreach (var (system, usage) in ReadSystemUsage()
                     .Select(kv => (kv.Key, (Seconds: kv.Value.Seconds - usageBefore.GetValueOrDefault(kv.Key).Seconds,
                         Slow: kv.Value.Slow - usageBefore.GetValueOrDefault(kv.Key).Slow)))
                     .OrderByDescending(x => x.Item2.Seconds)
                     .Take(12))
        {
            TestContext.WriteLine($"  {system}: {usage.Seconds * 1000:F0} ms total, {usage.Slow:F0} updates over 17 ms");
        }
        TestContext.WriteLine($"Janitor travel: {string.Join(", ", maxTravel.Values.Select(v => v.ToString("F1")))}");

        Assert.Multiple(() =>
        {
            Assert.That(sharedSeats, Is.Empty, "Two androids targeted the same seat");
            Assert.That(stacked, Is.Empty, "Resting androids stood on top of each other");
            Assert.That(maxTravel.Values, Has.All.GreaterThan(3f), "Every janitor should move around during the shift");
            // Wall-clock tick time depends on the machine and on tests running in parallel, so the androids'
            // own systems are judged instead.
            Assert.That(AndroidMsPerTick("AndroidSystem") + AndroidMsPerTick("AndroidScanSystem"), Is.LessThan(3),
                "Android systems cost too much per tick");
            Assert.That(SlowUpdates("AndroidSystem") + SlowUpdates("AndroidScanSystem"), Is.LessThan(chunk * chunks / 100),
                "Android systems had too many updates over 17 ms");
        });

        await Unload(pair, map);
    }

    [Test]
    public async Task Bartender_PicksUpTipsLeftOnTheBar()
    {
        var (pair, grid, nearBar, map) = await LoadSaltern();
        var server = pair.Server;
        var sEnt = server.EntMan;
        EntityUid bartender = default;
        EntityUid cash = default;

        await server.WaitAssertion(() =>
        {
            bartender = sEnt.SpawnEntity("MobAndroidBartender", nearBar[2]);

            Assert.That(new NavMapBeaconLocator(sEnt).TryFindBeacon(new EntityCoordinates(grid, Vector2.Zero), "bar", out var bar), Is.True);
            var xform = sEnt.System<SharedTransformSystem>();
            var counter = sEnt.System<EntityLookupSystem>()
                .GetEntitiesInRange<Content.Shared.Placeable.PlaceableSurfaceComponent>(bar!.Value, 4f)
                .Select(e => e.Owner)
                .FirstOrDefault(e => sEnt.GetComponent<TransformComponent>(e).Anchored);
            Assert.That(counter, Is.Not.EqualTo(default(EntityUid)), "Saltern's bar should have a counter near its beacon");

            cash = sEnt.SpawnEntity("SpaceCash", sEnt.GetComponent<TransformComponent>(counter).Coordinates);
        });

        await server.WaitRunTicks(900);

        await server.WaitAssertion(() =>
        {
            var containers = sEnt.System<Robust.Shared.Containers.SharedContainerSystem>();
            Assert.That(!sEnt.Deleted(cash) && containers.IsEntityOrParentInContainer(cash), Is.True, "The tip should be picked up");
            var parent = sEnt.GetComponent<TransformComponent>(cash).ParentUid;
            var owner = parent;
            while (owner.IsValid() && owner != bartender)
                owner = sEnt.GetComponent<TransformComponent>(owner).ParentUid;
            Assert.That(owner, Is.EqualTo(bartender), "The bartender keeps the tip");
        });

        await Unload(pair, map);
    }
}
