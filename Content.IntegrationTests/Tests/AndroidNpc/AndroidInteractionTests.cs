// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AndroidNpc.Components;
using Content.Server.AndroidNpc.Systems;
using Content.Server.AndroidNpc.Utilities;
using Content.Server.NPC.Pathfinding;
using Content.Server.Power.Components;
using Content.Shared.Damage;
using Content.Shared.Damage.Prototypes;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.NPC;
using Content.Shared.Slippery;
using Content.Shared.Speech;
using Content.Shared.Verbs;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Localization;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Robust.UnitTesting;
using static Content.IntegrationTests.Tests.AndroidNpc.AndroidLifecycleAndServiceTests;

namespace Content.IntegrationTests.Tests.AndroidNpc;

/// <summary>
/// How crew members interact with androids: menus, questions, the context menu, breaks interrupted by orders,
/// fighting back, and doors the androids cannot open.
/// </summary>
[TestFixture]
[TestOf(typeof(AndroidServiceSystem))]
[TestOf(typeof(AndroidCombatSystem))]
public sealed class AndroidInteractionTests
{
    private static readonly object[] MenuRequests =
    {
        new object[] { AndroidJob.Bartender, "un mohito s'il te plait", "fr", "DrinkMojito" },
        new object[] { AndroidJob.Bartender, "deux bieres", "fr", "DrinkBeerglass" },
        new object[] { AndroidJob.Bartender, "un gin fizz", "fr", "DrinkGinFizzGlass" },
        new object[] { AndroidJob.Bartender, "un gin", "fr", "DrinkGinGlass" },
        new object[] { AndroidJob.Bartender, "a gin and tonic please", "en", "DrinkGinTonicglass" },
        new object[] { AndroidJob.Bartender, "une margharita", "fr", "DrinkMargaritaGlass" },
        new object[] { AndroidJob.Bartender, "un jus d'orange", "fr", "DrinkOrangeJuice" },
        new object[] { AndroidJob.Bartender, "un thé", "fr", "DrinkTeaGlass" },
        new object[] { AndroidJob.Bartender, "I'd like the beer", "en", "DrinkBeerglass" },
        new object[] { AndroidJob.Bartender, "un russe blanc", "fr", "DrinkWhiteRussianGlass" },
        new object[] { AndroidJob.Bartender, "a white russian", "en", "DrinkWhiteRussianGlass" },
        new object[] { AndroidJob.Cook, "une pizza", "fr", "FoodPizzaMargherita" },
        new object[] { AndroidJob.Cook, "une pizza aux champignons", "fr", "FoodPizzaMushroom" },
        new object[] { AndroidJob.Cook, "des frites", "fr", "FoodMealFries" },
        new object[] { AndroidJob.Cook, "a cheeseburger", "en", "FoodBurgerCheese" },
        new object[] { AndroidJob.Cook, "une soupe aux pois", "fr", "FoodSoupPea" },
        new object[] { AndroidJob.Cook, "a mushroom pizza", "en", "FoodPizzaMushroom" },
    };

    [Test]
    [TestCaseSource(nameof(MenuRequests))]
    public async Task Menu_FindsTheMostSpecificItemDespiteTypos(AndroidJob job, string request, string language, string expected)
    {
        var pair = await PoolManager.GetServerClient();
        var service = pair.Server.System<AndroidServiceSystem>();

        await pair.Server.WaitAssertion(() =>
            Assert.That(service.Menu(job).Match(request, language, fuzzy: true)?.Item.Id, Is.EqualTo(expected), request));

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Menu_IsLargeAndSmallTalkIsNotAnOrder()
    {
        var pair = await PoolManager.GetServerClient();
        var service = pair.Server.System<AndroidServiceSystem>();

        await pair.Server.WaitAssertion(() =>
        {
            Assert.That(service.Menu(AndroidJob.Bartender).Items.Count(), Is.GreaterThan(100), "Every standard drink is on the bar menu");
            Assert.That(service.Menu(AndroidJob.Cook).Items.Count(), Is.GreaterThan(100), "Every standard dish is on the kitchen menu");
            Assert.That(service.Menu(AndroidJob.Janitor).Items, Is.Empty);
            Assert.That(service.Menu(AndroidJob.Bartender).Match("un verre de bidule", "fr", fuzzy: true), Is.Null,
                "A container alone names no drink");

            foreach (var text in new[] { "salut, ça va ?", "bonjour", "merci beaucoup", "comment tu t'appelles ?", "how are you doing" })
            {
                Assert.That(service.MentionsMenuItem(AndroidJob.Bartender, text, "fr"), Is.False, text);
                Assert.That(service.MentionsMenuItem(AndroidJob.Cook, text, "fr"), Is.False, text);
            }
        });

        await pair.CleanReturnAsync();
    }

    private static readonly object[] OrderWordings =
    {
        new object[] { "Gérard, suis moi", AndroidOrderType.FollowMe, null! },
        new object[] { "Gerard, Suis-Moi s'il te plaît", AndroidOrderType.FollowMe, null! },
        new object[] { "Gérard, peux-tu me suivre ?", AndroidOrderType.FollowMe, null! },
        new object[] { "Gérard, arrête de me suivre", AndroidOrderType.ReturnToWork, null! },
        new object[] { "Gerard, stop following me", AndroidOrderType.ReturnToWork, null! },
        new object[] { "Gérard, retourne à ton poste", AndroidOrderType.ReturnToWork, null! },
        new object[] { "Gérard, va te reposer", AndroidOrderType.TakeBreak, null! },
        new object[] { "Gérard, va en pause", AndroidOrderType.TakeBreak, null! },
        new object[] { "Gérard, va à la cuisine", AndroidOrderType.GoToDepartment, "cuisine" },
        new object[] { "Gérard, va voir Bob", AndroidOrderType.GoToPerson, "Bob" },
        new object[] { "Gérard, tu en es où ?", AndroidOrderType.StatusReport, null! },
        new object[] { "Gérard, tu fais quoi ?", AndroidOrderType.StatusReport, null! },
        new object[] { "Gérard, tu peux faire quoi ?", AndroidOrderType.Capabilities, null! },
        new object[] { "Gerard, what can you do?", AndroidOrderType.Capabilities, null! },
        new object[] { "Gérard, tu peux faire quoi à manger ?", AndroidOrderType.Menu, null! },
        new object[] { "Gérard, qu’est-ce que tu sers ?", AndroidOrderType.Menu, null! },
        new object[] { "Gerard, what's on the menu?", AndroidOrderType.Menu, null! },
    };

    [Test]
    [TestCaseSource(nameof(OrderWordings))]
    public async Task OrderPhrases_UnderstandCommonWordings(string message, AndroidOrderType expected, string? target)
    {
        var pair = await PoolManager.GetServerClient();

        await pair.Server.WaitAssertion(() =>
        {
            var matcher = new OrderPhraseMatcher(pair.Server.ProtoMan.AndroidPhrases());
            Assert.That(matcher.TryMatch(message, out var type, out var found), Is.True, message);
            Assert.That(type, Is.EqualTo(expected), message);
            if (target != null)
                Assert.That(found, Is.EqualTo(target).IgnoreCase, message);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Androids_IgnoreWhatOtherAndroidsSay()
    {
        var (pair, map) = await Setup();
        var server = pair.Server;
        var sEnt = server.EntMan;
        EntityUid customer = default, bartender = default;

        await server.WaitAssertion(() =>
        {
            customer = sEnt.SpawnEntity("MobAndroidCook", map.GridCoords);
            bartender = sEnt.SpawnEntity("MobAndroidBartender", map.GridCoords.Offset(new Vector2(0.8f, 0)));
            Name(sEnt, customer, "Zephyrin");
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            // The bartender serving its android customer by name.
            sEnt.EventBus.RaiseLocalEvent(customer, new ListenEvent("Voici votre bière, Zephyrin. Santé !", bartender));
            sEnt.EventBus.RaiseLocalEvent(customer, new ListenEvent("Zephyrin, suis-moi", bartender));
            Assert.That(sEnt.TryGetComponent<AndroidConversationComponent>(customer, out var conversation) && conversation.LastIntent != null, Is.False);
            Assert.That(sEnt.HasComponent<AndroidOrderComponent>(customer), Is.False);
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ContextMenu_AsksAWaitingCustomerWhatItOrdered()
    {
        var (pair, map) = await Setup();
        var server = pair.Server;
        var sEnt = server.EntMan;
        var loc = server.ResolveDependency<ILocalizationManager>();
        EntityUid player = default, customer = default;

        await server.WaitAssertion(() =>
        {
            player = sEnt.SpawnEntity("MobHuman", map.GridCoords);
            customer = sEnt.SpawnEntity("MobAndroidCook", map.GridCoords.Offset(new Vector2(0.8f, 0)));
            Name(sEnt, customer, "Zephyrin");
        });
        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var verbs = server.System<SharedVerbSystem>();
            var ask = loc.GetString("android-verb-repeat-order");
            Assert.That(verbs.GetLocalVerbs(customer, player, typeof(Verb)).Select(v => v.Text), Does.Not.Contain(ask), "Nothing to ask before it orders");

            server.System<AndroidCustomerSystem>().StartOrder(customer, AndroidBreakVenue.Bar);
            ClearLines(sEnt, customer);
            Assert.That(verbs.GetLocalVerbs(customer, player, typeof(Verb)).Select(v => v.Text), Does.Contain(ask));

            sEnt.EventBus.RaiseLocalEvent(customer, new ListenEvent("Zephyrin, tu veux quoi ?", player));
        });
        await server.WaitRunTicks(60);

        await server.WaitAssertion(() =>
        {
            var order = sEnt.GetComponent<AndroidCustomerOrderComponent>(customer);
            var name = server.System<AndroidServiceSystem>().MenuName(AndroidJob.Bartender, order.RequestedItem, "fr");
            Assert.That(sEnt.GetComponent<AndroidComponent>(customer).RecentLines.Any(l => l.Contains(name)), Is.True,
                $"It restates its order by its menu name ({name})");
        });

        await pair.CleanReturnAsync();
    }

    private static void ClearLines(IEntityManager sEnt, EntityUid android)
    {
        var comp = sEnt.GetComponent<AndroidComponent>(android);
        comp.RecentLines.Clear();
        comp.LastSaid = null;
    }

    [Test]
    public async Task Androids_DoNotSlip()
    {
        var pair = await PoolManager.GetServerClient();
        var factory = pair.Server.ResolveDependency<IComponentFactory>();

        await pair.Server.WaitAssertion(() =>
        {
            foreach (var job in Enum.GetValues<AndroidJob>())
            {
                var proto = pair.Server.ProtoMan.AndroidJobData(job)!.EntityPrototype;
                Assert.That(pair.Server.ProtoMan.Index(proto).TryGetComponent<NoSlipComponent>(out _, factory), Is.True, $"{proto} walks over banana peels");
            }
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Places_AreFoundByTheirBeaconName()
    {
        var (pair, map) = await Setup();
        var server = pair.Server;
        var sEnt = server.EntMan;
        var loc = server.ResolveDependency<ILocalizationManager>();

        await server.WaitAssertion(() =>
        {
            var beacon = sEnt.SpawnEntity("DefaultStationBeaconCryonics", map.GridCoords);
            var label = loc.GetString("station-beacon-cryonics");
            var classifier = server.System<AndroidDialogueSystem>().Classifier;
            var locator = new NavMapBeaconLocator(sEnt);

            Assert.That(classifier.FindLocation($"il y a un blessé en {label}"), Is.Null, "Not one of the built-in aliases");
            Assert.That(locator.TryFindPlace(map.GridCoords, $"il y a un blessé en {label.ToLowerInvariant()} !", classifier, out var name, out var coords), Is.True);
            Assert.That(name, Is.EqualTo(label));
            Assert.That(coords, Is.EqualTo(sEnt.GetComponent<TransformComponent>(beacon).Coordinates));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Pathfinding_AndroidsWalkAroundTablesRatherThanClimbThem()
    {
        var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var sEnt = server.EntMan;
        var map = await pair.CreateTestMap();
        var mapSys = sEnt.System<SharedMapSystem>();
        Task<PathResultEvent> path = default!;

        await server.WaitAssertion(() =>
        {
            var tile = new Tile(map.Tile.Tile.TypeId);
            mapSys.SetTiles(map.Grid, Enumerable.Range(0, 7).SelectMany(x => Enumerable.Range(-3, 7).Select(y => (new Vector2i(x, y), tile))).ToList());
        });
        await server.WaitRunTicks(25);

        // A row of tables across the middle, with a way around at both ends.
        await server.WaitAssertion(() =>
        {
            for (var y = -2; y <= 2; y++)
                sEnt.SpawnEntity("Table", new EntityCoordinates(map.Grid.Owner, 3.5f, y + 0.5f));
        });
        await server.WaitRunTicks(30);

        await server.WaitAssertion(() =>
        {
            var janitor = sEnt.SpawnEntity("MobAndroidJanitor", new EntityCoordinates(map.Grid.Owner, 1.5f, 0.5f));
            path = server.System<PathfindingSystem>().GetPath(janitor,
                new EntityCoordinates(map.Grid.Owner, 1.5f, 0.5f),
                new EntityCoordinates(map.Grid.Owner, 5.5f, 0.5f),
                0.5f, CancellationToken.None, PathFlags.Interact | PathFlags.Access | PathFlags.Climbing);
        });
        await server.WaitRunTicks(10);

        await server.WaitAssertion(() =>
        {
#pragma warning disable RA0004 // The task is completed.
            var result = path.Result;
#pragma warning restore RA0004
            Assert.That(result.Result, Is.EqualTo(PathResult.Path));
            Assert.That(result.Path.Any(p => (p.Data.Flags & PathfindingBreadcrumbFlag.Climb) != 0), Is.False,
                "The detour is taken instead of jumping on a table");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Questions_WhatCanYouDoAndWhatCanYouServe()
    {
        var (pair, map) = await Setup();
        var server = pair.Server;
        var sEnt = server.EntMan;
        var protos = server.ResolveDependency<IPrototypeManager>();
        EntityUid janitor = default, bartender = default, speaker = default;

        await server.WaitAssertion(() =>
        {
            speaker = sEnt.SpawnEntity("MobHuman", map.GridCoords);
            janitor = sEnt.SpawnEntity("MobAndroidJanitor", map.GridCoords.Offset(new Vector2(0.5f, 0)));
            bartender = sEnt.SpawnEntity("MobAndroidBartender", map.GridCoords.Offset(new Vector2(-0.5f, 0)));
            Name(sEnt, janitor, "Zephyrin");
            Name(sEnt, bartender, "Zebulon");
        });

        await server.WaitRunTicks(2);
        await server.WaitAssertion(() =>
        {
            sEnt.EventBus.RaiseLocalEvent(janitor, new ListenEvent("Zephyrin, tu peux faire quoi ?", speaker));
            sEnt.EventBus.RaiseLocalEvent(bartender, new ListenEvent("Zebulon, qu'est-ce que tu sers ?", speaker));
        });
        await server.WaitRunTicks(90);

        await server.WaitAssertion(() =>
        {
            var fr = protos.AndroidFeedback("fr");
            var janitorLines = protos.AndroidJobData(AndroidJob.Janitor)!.LinesFor("fr")!;
            var said = sEnt.GetComponent<AndroidComponent>(janitor).RecentLines;
            Assert.That(said, Does.Contain(janitorLines.Capabilities.Single()), "The janitor says what its job is");
            Assert.That(said, Does.Contain(fr.CapabilitiesOrders), "and which orders it takes");

            var menu = sEnt.GetComponent<AndroidComponent>(bartender).LastSaid;
            Assert.That(menu, Does.StartWith("J'ai "), "The bartender answers with its menu, in French");
            Assert.That(menu, Does.Contain("mojito"), "Hand-picked favourites come first");
            Assert.That(sEnt.HasComponent<AndroidOrderComponent>(janitor) || sEnt.HasComponent<AndroidOrderComponent>(bartender), Is.False,
                "Questions are not orders");

            sEnt.EventBus.RaiseLocalEvent(janitor, new ListenEvent("Zephyrin, what can you serve?", speaker));
        });
        await server.WaitRunTicks(60);

        await server.WaitAssertion(() =>
            Assert.That(sEnt.GetComponent<AndroidComponent>(janitor).LastSaid, Is.EqualTo(protos.AndroidFeedback("en").MenuNone)));

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ContextMenu_OrderIsSpokenByThePlayerAndObeyed()
    {
        var (pair, map) = await Setup();
        var server = pair.Server;
        var sEnt = server.EntMan;
        var loc = server.ResolveDependency<ILocalizationManager>();
        EntityUid janitor = default, player = default;

        await server.WaitAssertion(() =>
        {
            player = sEnt.SpawnEntity("MobHuman", map.GridCoords);
            janitor = sEnt.SpawnEntity("MobAndroidJanitor", map.GridCoords.Offset(new Vector2(1f, 0)));
            Name(sEnt, janitor, "Zephyrin");
        });

        await server.WaitRunTicks(2);
        await server.WaitAssertion(() =>
        {
            var verbs = server.System<SharedVerbSystem>().GetLocalVerbs(janitor, player, typeof(Verb));
            var category = loc.GetString("android-verb-category");
            var orders = verbs.Where(v => v.Category?.Text == category).ToList();

            Assert.That(orders.Select(v => v.Text), Does.Contain(loc.GetString("android-verb-follow-me")));
            Assert.That(orders.Select(v => v.Text), Does.Not.Contain(loc.GetString("android-verb-menu")), "A janitor has no menu to ask about");

            orders.Single(v => v.Text == loc.GetString("android-verb-follow-me")).Act!.Invoke();
        });
        await server.WaitRunTicks(60);

        await server.WaitAssertion(() =>
        {
            Assert.That(sEnt.TryGetComponent<AndroidOrderComponent>(janitor, out var order), Is.True, "The android heard the player and obeys");
            Assert.That(order!.OrderType, Is.EqualTo(AndroidOrderType.FollowMe));
            Assert.That(order.Issuer, Is.EqualTo(player));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Break_IsInterruptedByAnOrderAndItsTimerRestarts()
    {
        var (pair, map) = await Setup();
        var server = pair.Server;
        var sEnt = server.EntMan;
        var timing = server.ResolveDependency<IGameTiming>();
        EntityUid janitor = default, speaker = default;

        await server.WaitAssertion(() =>
        {
            speaker = sEnt.SpawnEntity("MobHuman", map.GridCoords);
            janitor = sEnt.SpawnEntity("MobAndroidJanitor", map.GridCoords.Offset(new Vector2(0.5f, 0)));
            Name(sEnt, janitor, "Zephyrin");
        });

        await server.WaitRunTicks(2);
        await server.WaitAssertion(() => sEnt.GetComponent<AndroidComponent>(janitor).NextBreakAt = timing.CurTime);
        await server.WaitRunTicks(30);

        await server.WaitAssertion(() =>
        {
            var android = sEnt.GetComponent<AndroidComponent>(janitor);
            Assert.That(android.BreakState, Is.Not.EqualTo(AndroidBreakState.None), "The android went on its break");

            sEnt.EventBus.RaiseLocalEvent(janitor, new ListenEvent("Zephyrin, suis-moi", speaker));

            var interval = TimeSpan.FromSeconds(server.ResolveDependency<IPrototypeManager>().AndroidConfig().BreakIntervalSeconds);
            Assert.That(android.BreakState, Is.EqualTo(AndroidBreakState.None), "The order ends the break at once");
            Assert.That(android.Status, Is.Not.EqualTo(AndroidStatus.OnBreak));
            Assert.That(android.NextBreakAt, Is.GreaterThanOrEqualTo(timing.CurTime + interval * 0.9), "The break cycle starts over");
            Assert.That(sEnt.GetComponent<AndroidOrderComponent>(janitor).OrderType, Is.EqualTo(AndroidOrderType.FollowMe));
        });

        await server.WaitRunTicks(10);
        await server.WaitAssertion(() =>
            Assert.That(sEnt.GetComponent<AndroidComponent>(janitor).BreakState, Is.EqualTo(AndroidBreakState.None), "It does not go back to its break"));

        await pair.CleanReturnAsync();
    }

    private static void Hurt(IIntegrationInstance server, EntityUid target, string type, float amount, EntityUid? origin)
    {
        var damageType = server.ProtoMan.Index<DamageTypePrototype>(type);
        server.System<DamageableSystem>().TryChangeDamage(target, new DamageSpecifier(damageType, amount), true, origin: origin, canMiss: false);
    }

    [Test]
    public async Task Retaliation_OnlyAgainstAnAttackerPastTheThreshold()
    {
        var (pair, map) = await Setup();
        var server = pair.Server;
        var sEnt = server.EntMan;
        EntityUid burnt = default, beaten = default, attacker = default;

        await server.WaitAssertion(() =>
        {
            attacker = sEnt.SpawnEntity("MobHuman", map.GridCoords);
            burnt = sEnt.SpawnEntity("MobAndroidJanitor", map.GridCoords.Offset(new Vector2(0.8f, 0)));
            beaten = sEnt.SpawnEntity("MobAndroidJanitor", map.GridCoords.Offset(new Vector2(-0.8f, 0)));
        });

        await server.WaitRunTicks(2);
        await server.WaitAssertion(() =>
        {
            Hurt(server, burnt, "Heat", 60, null);
            Assert.That(sEnt.GetComponent<AndroidComponent>(burnt).FightTarget, Is.Null, "Fire is nobody to fight");

            Hurt(server, beaten, "Blunt", 30, attacker);
            Assert.That(sEnt.GetComponent<AndroidComponent>(beaten).FightTarget, Is.Null, "A few hits only scare it");

            Hurt(server, beaten, "Blunt", 25, attacker);
            var android = sEnt.GetComponent<AndroidComponent>(beaten);
            Assert.That(android.FightTarget, Is.EqualTo(attacker), "Past 50 damage from one attacker it fights back");
            Assert.That(android.Status, Is.EqualTo(AndroidStatus.Fighting));
        });

        await server.WaitRunTicks(10);
        await server.WaitAssertion(() =>
        {
            Assert.That(sEnt.GetComponent<AndroidComponent>(beaten).Status, Is.EqualTo(AndroidStatus.Fighting), "It does not flee while fighting");
            Hurt(server, attacker, "Blunt", 250, null);
            Assert.That(sEnt.GetComponent<MobStateComponent>(attacker).CurrentState, Is.Not.EqualTo(MobState.Alive));
        });

        await server.WaitRunTicks(10);
        await server.WaitAssertion(() =>
        {
            var android = sEnt.GetComponent<AndroidComponent>(beaten);
            Assert.That(android.FightTarget, Is.Null, "It stops once the attacker is down");
            Assert.That(android.Status, Is.Not.EqualTo(AndroidStatus.Fighting));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    [TestCase("MobAndroidJanitor", "MopItem", 0.8f)]
    [TestCase("MobAndroidCook", "KitchenKnife", 0.8f)]
    [TestCase("MobAndroidBartender", "WeaponShotgunDoubleBarreledRubber", 3f)]
    public async Task Retaliation_FightsBackWithTheJobWeapon(string androidProto, string weapon, float distance)
    {
        var (pair, map) = await Setup();
        var server = pair.Server;
        var sEnt = server.EntMan;
        EntityUid android = default, attacker = default;

        await server.WaitAssertion(() =>
        {
            attacker = sEnt.SpawnEntity("MobHuman", map.GridCoords);
            android = sEnt.SpawnEntity(androidProto, map.GridCoords.Offset(new Vector2(distance, 0)));
        });

        await server.WaitRunTicks(2);
        await server.WaitAssertion(() => Hurt(server, android, "Blunt", 55, attacker));
        await server.WaitRunTicks(150);

        await server.WaitAssertion(() =>
        {
            var held = sEnt.System<AndroidInventorySystem>().GetEquippedItem(android);
            Assert.That(held, Is.Not.Null, "The android took its weapon");
            Assert.That(sEnt.GetComponent<MetaDataComponent>(held!.Value).EntityPrototype?.ID, Is.EqualTo(weapon));
            Assert.That(sEnt.GetComponent<DamageableComponent>(attacker).TotalDamage.Float(), Is.GreaterThan(0f), "The attacker got hit back");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// A corridor of five tiles with an airlock in the middle and nothing around: the only way through is the door.
    /// </summary>
    private static async Task<EntityUid> BuildDoorCorridor(IIntegrationInstance server, TestMapData map, string airlock, bool bolted = false)
    {
        var sEnt = server.EntMan;
        var mapSys = sEnt.System<SharedMapSystem>();
        EntityUid door = default;

        await server.WaitAssertion(() =>
        {
            AndroidNpcLogicTests.EnsureSafeAtmosphere(server, map);
            var tile = new Tile(map.Tile.Tile.TypeId);
            mapSys.SetTiles(map.Grid, Enumerable.Range(0, 5).Select(x => (new Vector2i(x, 0), tile)).ToList());
        });
        await server.WaitRunTicks(25);

        await server.WaitAssertion(() =>
        {
            door = sEnt.SpawnEntity(airlock, new EntityCoordinates(map.Grid.Owner, 2.5f, 0.5f));
            sEnt.GetComponent<ApcPowerReceiverComponent>(door).NeedsPower = false;
        });
        await server.WaitRunTicks(5);

        if (bolted)
            await server.WaitAssertion(() => sEnt.System<SharedDoorSystem>().SetBoltsDown((door, sEnt.GetComponent<DoorBoltComponent>(door)), true));

        await server.WaitRunTicks(30);
        return door;
    }

    [Test]
    [TestCase("AirlockJanitorLocked", false, true)]
    [TestCase("AirlockSecurityLocked", false, false)]
    [TestCase("AirlockJanitorLocked", true, false)]
    public async Task Pathfinding_OnlyGoesThroughDoorsTheAndroidCanOpen(string airlock, bool bolted, bool expectPath)
    {
        var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var sEnt = server.EntMan;
        var map = await pair.CreateTestMap();
        await BuildDoorCorridor(server, map, airlock, bolted);

        EntityUid janitor = default;
        Task<PathResultEvent> path = default!;

        await server.WaitAssertion(() =>
        {
            janitor = sEnt.SpawnEntity("MobAndroidJanitor", new EntityCoordinates(map.Grid.Owner, 0.5f, 0.5f));
            path = server.System<PathfindingSystem>().GetPath(janitor,
                new EntityCoordinates(map.Grid.Owner, 0.5f, 0.5f),
                new EntityCoordinates(map.Grid.Owner, 4.5f, 0.5f),
                0.5f, CancellationToken.None, PathFlags.Interact | PathFlags.Access);
        });
        await server.WaitRunTicks(10);

        await server.WaitAssertion(() =>
        {
            Assert.That(path.IsCompleted, Is.True);
#pragma warning disable RA0004 // The task is completed.
            Assert.That(path.Result.Result == PathResult.Path, Is.EqualTo(expectPath),
                expectPath ? "A janitor goes through its own airlock" : "No path through a door it cannot open");
#pragma warning restore RA0004
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Janitor_NeverBumpsADoorItCannotOpen()
    {
        var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var sEnt = server.EntMan;
        var map = await pair.CreateTestMap();
        var door = await BuildDoorCorridor(server, map, "AirlockSecurityLocked");
        EntityUid janitor = default;

        await server.WaitAssertion(() =>
        {
            janitor = sEnt.SpawnEntity("MobAndroidJanitor", new EntityCoordinates(map.Grid.Owner, 0.5f, 0.5f));
            sEnt.System<AndroidOrderSystem>().IssueOrder(janitor, janitor, AndroidOrderType.CleanArea, null,
                new EntityCoordinates(map.Grid.Owner, 4.5f, 0.5f));
        });

        var states = new HashSet<DoorState>();
        for (var i = 0; i < 30; i++)
        {
            await server.WaitRunTicks(5);
            await server.WaitAssertion(() => states.Add(sEnt.GetComponent<DoorComponent>(door).State));
        }

        await server.WaitAssertion(() =>
        {
            var android = sEnt.GetComponent<AndroidComponent>(janitor);
            Assert.That(states, Is.EquivalentTo(new[] { DoorState.Closed }),
                $"The android never tried the security airlock; status {android.Status}, at {sEnt.GetComponent<TransformComponent>(janitor).LocalPosition}, said: {string.Join(" | ", android.RecentLines)}");
            Assert.That(sEnt.GetComponent<TransformComponent>(janitor).LocalPosition.X, Is.LessThan(2f), "It stayed on its side");
        });

        await pair.CleanReturnAsync();
    }
}
