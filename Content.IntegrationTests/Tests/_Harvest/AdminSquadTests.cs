// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.IntegrationTests.Tests._Harvest.AndroidNpc;
using Content.IntegrationTests.Pair;
using Content.Server._Harvest.AdminSquad;
using Content.Server._Harvest.AndroidNpc.Components;
using Content.Server.Atmos.Components;
using Content.Server.NPC.HTN;
using Content.Shared._Harvest.AdminSquad;
using Content.Shared.Gravity;
using Content.Shared.Mobs.Systems;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;
using Robust.UnitTesting;

namespace Content.IntegrationTests.Tests._Harvest;

[TestFixture]
public sealed class AdminSquadTests
{
    private const string Spider = "MobGiantSpider";

    /// <summary>
    /// Floor from <paramref name="min"/> to <paramref name="max"/> with breathable air and gravity.
    /// </summary>
    private static void BuildRoom(IIntegrationInstance server, TestMapData testMap, Vector2i min, Vector2i max)
    {
        var entMan = server.EntMan;
        var grid = testMap.Grid.Owner;

        AndroidNpcLogicTests.EnsureSafeAtmosphere(server, testMap);
        entMan.RemoveComponent<GridAtmosphereComponent>(grid);

        var gravity = entMan.EnsureComponent<GravityComponent>(grid);
        gravity.Enabled = true;
        gravity.Inherent = true;
        entMan.Dirty(grid, gravity);

        var tile = new Tile(testMap.Tile.Tile.TypeId);
        var tiles = new List<(Vector2i, Tile)>();
        for (var x = min.X; x <= max.X; x++)
        {
            for (var y = min.Y; y <= max.Y; y++)
                tiles.Add((new Vector2i(x, y), tile));
        }

        entMan.System<SharedMapSystem>().SetTiles(testMap.Grid, tiles);
    }

    [Test]
    public async Task MoveOrder_SpreadsSquadAndRelease_RestoresBehavior()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var transform = entMan.System<SharedTransformSystem>();
        var squad = entMan.System<AdminSquadSystem>();
        var grid = testMap.Grid.Owner;

        // A 10x4 room.
        await server.WaitAssertion(() => BuildRoom(server, testMap, new Vector2i(-1, -1), new Vector2i(8, 2)));

        // Let the pathfinding navmesh build before spawning the NPCs.
        await server.WaitRunTicks(25);

        var spiders = new List<EntityUid>();
        var destination = EntityCoordinates.Invalid;
        List<(EntityUid Unit, EntityCoordinates Destination)> results = null!;

        await server.WaitAssertion(() =>
        {
            spiders.Add(entMan.SpawnAtPosition(Spider, new EntityCoordinates(grid, 0.5f, 0.5f)));
            spiders.Add(entMan.SpawnAtPosition(Spider, new EntityCoordinates(grid, 0.5f, 1.5f)));

            destination = new EntityCoordinates(grid, 7.5f, 0.5f);
            results = squad.Order(spiders, AdminSquadOrder.Move, destination);

            Assert.That(results.Select(r => r.Unit), Is.EquivalentTo(spiders), "Both spiders should take the order");
            Assert.That(results[0].Destination, Is.Not.EqualTo(results[1].Destination),
                "Each spider should get its own tile");

            foreach (var spider in spiders)
            {
                var htn = entMan.GetComponent<HTNComponent>(spider);
                Assert.That(htn.RootTask.Task.Id, Is.EqualTo("AdminSquadCompound"));
            }
        });

        // About 10 seconds: plenty to walk 7 tiles.
        await server.WaitRunTicks(300);

        await server.WaitAssertion(() =>
        {
            foreach (var (unit, spot) in results)
            {
                var position = transform.GetMapCoordinates(unit).Position;
                var target = transform.ToMapCoordinates(spot).Position;
                Assert.That((position - target).Length(), Is.LessThan(1.2f),
                    $"{entMan.ToPrettyString(unit)} at {position} did not reach {target}");

                var ordered = entMan.GetComponent<AdminSquadOrderedComponent>(unit);
                Assert.That(ordered.Order, Is.EqualTo(AdminSquadOrder.Hold), "An arrived move order turns into holding");
            }

            var released = squad.Order(spiders, AdminSquadOrder.Release, EntityCoordinates.Invalid);
            Assert.That(released, Has.Count.EqualTo(2));

            foreach (var spider in spiders)
            {
                Assert.That(entMan.HasComponent<AdminSquadOrderedComponent>(spider), Is.False);
                Assert.That(entMan.GetComponent<HTNComponent>(spider).RootTask.Task.Id,
                    Is.Not.EqualTo("AdminSquadCompound"), "Release should restore the spider's own root task");
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task FormationSpots_AreDistinctFreeTiles()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var mapSys = entMan.System<SharedMapSystem>();
        var transform = entMan.System<SharedTransformSystem>();
        var squad = entMan.System<AdminSquadSystem>();

        await server.WaitAssertion(() =>
        {
            var tile = new Tile(testMap.Tile.Tile.TypeId);
            var tiles = new List<(Vector2i, Tile)>();
            for (var x = -2; x <= 2; x++)
            {
                for (var y = -2; y <= 2; y++)
                    tiles.Add((new Vector2i(x, y), tile));
            }

            mapSys.SetTiles(testMap.Grid, tiles);

            // A wall right next to the target must be skipped.
            entMan.SpawnAtPosition("WallSolid", new EntityCoordinates(testMap.Grid.Owner, 1.5f, 0.5f));
        });

        await server.WaitRunTicks(1);

        await server.WaitAssertion(() =>
        {
            var target = transform.ToMapCoordinates(new EntityCoordinates(testMap.Grid.Owner, 0.5f, 0.5f));
            var spots = squad.FormationSpots(target, 6);

            Assert.That(spots, Has.Count.EqualTo(6));
            Assert.That(spots.Distinct().Count(), Is.EqualTo(6), "Six units on open floor get six tiles");
            Assert.That(spots[0].Position, Is.EqualTo(new Vector2(0.5f, 0.5f)), "The first unit gets the target tile");
            Assert.That(spots.Any(s => s.Position == new Vector2(1.5f, 0.5f)), Is.False, "The wall tile is not a spot");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LockedDoorInTheWay_IsSmashed()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var squad = entMan.System<AdminSquadSystem>();
        var grid = testMap.Grid.Owner;
        var obstacles = new List<EntityUid>();

        // Two rooms split by a wall with a locked command airlock in the middle.
        await server.WaitAssertion(() =>
        {
            BuildRoom(server, testMap, new Vector2i(-3, -1), new Vector2i(5, 1));
            obstacles.Add(entMan.SpawnAtPosition("AirlockCommandLocked", new EntityCoordinates(grid, 1.5f, 0.5f)));
            obstacles.Add(entMan.SpawnAtPosition("WallSolid", new EntityCoordinates(grid, 1.5f, -0.5f)));
            obstacles.Add(entMan.SpawnAtPosition("WallSolid", new EntityCoordinates(grid, 1.5f, 1.5f)));
        });

        await server.WaitRunTicks(25);

        EntityUid spider = default;
        await server.WaitAssertion(() =>
        {
            spider = entMan.SpawnAtPosition(Spider, new EntityCoordinates(grid, -1.5f, 0.5f));
            var results = squad.Order(new[] { spider }, AdminSquadOrder.Move, new EntityCoordinates(grid, 4.5f, 0.5f));
            Assert.That(results, Has.Count.EqualTo(1));
        });

        // Ten hits at most for the airlock, then the walk.
        await server.WaitRunTicks(900);

        await server.WaitAssertion(() =>
        {
            var position = entMan.System<SharedTransformSystem>().GetMapCoordinates(spider).Position;
            var hit = !entMan.EntityExists(obstacles[0]) && position.X > 2f;
            var ordered = entMan.GetComponent<AdminSquadOrderedComponent>(spider);
            var htn = entMan.GetComponent<HTNComponent>(spider);
            entMan.TryGetComponent<Content.Server.NPC.Components.NPCSteeringComponent>(spider, out var steering);
            Assert.That(hit, Is.True,
                $"The spider should break the locked airlock down and walk through. " +
                $"pos={entMan.System<SharedTransformSystem>().GetMapCoordinates(spider).Position} order={ordered.Order} " +
                $"attack={ordered.AttackTarget} obstacle={ordered.AttackingObstacle} " +
                $"target={(ordered.AttackTarget is { } at ? entMan.ToPrettyString(at) : "none")} " +
                $"obstacles={string.Join(",", obstacles.Select(o => entMan.EntityExists(o) ? entMan.GetComponent<Content.Shared.Damage.Components.DamageableComponent>(o).TotalDamage.ToString() : "gone"))} " +
                $"plan={string.Join(",", htn.Plan?.Tasks.Select(x => x.Operator.GetType().Name) ?? Enumerable.Empty<string>())} steering={steering?.Status}");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task MobWithoutAi_GetsOrdersAndLosesThemOnRelease()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var transform = entMan.System<SharedTransformSystem>();
        var squad = entMan.System<AdminSquadSystem>();
        var grid = testMap.Grid.Owner;

        await server.WaitAssertion(() => BuildRoom(server, testMap, new Vector2i(-1, -1), new Vector2i(6, 2)));
        await server.WaitRunTicks(25);

        EntityUid human = default;
        var destination = new EntityCoordinates(grid, 5.5f, 0.5f);
        await server.WaitAssertion(() =>
        {
            human = entMan.SpawnAtPosition("MobHuman", new EntityCoordinates(grid, 0.5f, 0.5f));
            Assert.That(entMan.HasComponent<HTNComponent>(human), Is.False, "Test needs a mob without AI");

            Assert.That(squad.Order(new[] { human }, AdminSquadOrder.Move, destination), Has.Count.EqualTo(1));
            Assert.That(entMan.HasComponent<HTNComponent>(human), Is.True);
        });

        await server.WaitRunTicks(300);

        await server.WaitAssertion(() =>
        {
            var distance = (transform.GetMapCoordinates(human).Position - transform.ToMapCoordinates(destination).Position).Length();
            Assert.That(distance, Is.LessThan(1.2f), "The human should walk to its destination");

            squad.Release(human);
            Assert.That(entMan.HasComponent<HTNComponent>(human), Is.False, "The HTN added for the order is removed");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Android_MovesAttacksAndSpeaks()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var server = pair.Server;
        var testMap = await pair.CreateTestMap();
        var entMan = server.EntMan;
        var transform = entMan.System<SharedTransformSystem>();
        var squad = entMan.System<AdminSquadSystem>();
        var mobState = entMan.System<MobStateSystem>();
        var grid = testMap.Grid.Owner;

        await server.WaitAssertion(() => BuildRoom(server, testMap, new Vector2i(-1, -1), new Vector2i(6, 2)));
        await server.WaitRunTicks(25);

        EntityUid android = default;
        var destination = new EntityCoordinates(grid, 5.5f, 0.5f);
        await server.WaitAssertion(() =>
        {
            android = entMan.SpawnAtPosition("MobAndroidCook", new EntityCoordinates(grid, 0.5f, 0.5f));
            Assert.That(squad.Order(new[] { android }, AdminSquadOrder.Move, destination), Has.Count.EqualTo(1));
            squad.Speak(android, "Escouade en position", AdminSquadSpeech.Say);
        });

        await server.WaitRunTicks(300);

        EntityUid mouse = default;
        await server.WaitAssertion(() =>
        {
            var distance = (transform.GetMapCoordinates(android).Position - transform.ToMapCoordinates(destination).Position).Length();
            Assert.That(distance, Is.LessThan(1.2f), "The android should walk to its destination");
            Assert.That(entMan.GetComponent<AndroidComponent>(android).RecentLines, Does.Contain("Escouade en position"));

            mouse = entMan.SpawnAtPosition("MobMouse", new EntityCoordinates(grid, 3.5f, 1.5f));
            Assert.That(squad.Order(new[] { android }, AdminSquadOrder.Attack, EntityCoordinates.Invalid, mouse),
                Has.Count.EqualTo(1));
        });

        await server.WaitRunTicks(450);

        await server.WaitAssertion(() =>
        {
            Assert.That(!entMan.EntityExists(mouse) || !mobState.IsAlive(mouse), Is.True, "The android should beat the mouse down");
        });

        await pair.CleanReturnAsync();
    }
}
