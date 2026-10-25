// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Content.IntegrationTests.Pair;
using Content.Server.AndroidNpc.Components;
using Content.Server.AndroidNpc.Services;
using Content.Server.AndroidNpc.Systems;
using Content.Server.AndroidNpc.Utilities;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Speech;
using NUnit.Framework;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.IntegrationTests.Tests.AndroidNpc;

/// <summary>
/// Android lifecycle (leaving for cryostorage), cook/bartender service, admin tooling and order reporting.
/// </summary>
[TestFixture]
[TestOf(typeof(AndroidLifecycleSystem))]
[TestOf(typeof(AndroidServiceSystem))]
public sealed class AndroidLifecycleAndServiceTests
{
    private static async Task<(TestPair Pair, TestMapData Map)> Setup()
    {
        var pair = await AndroidTestPool.GetServerClient();
        var map = await pair.CreateTestMap();

        await pair.Server.WaitAssertion(() =>
        {
            AndroidNpcLogicTests.EnsureSafeAtmosphere(pair.Server, map);
            pair.Server.System<AndroidConversationSystem>().SetBackend(new Thx01ConversationBackend("", 4f, null));
        });

        return (pair, map);
    }

    private static void Name(IEntityManager sEnt, EntityUid android, string firstName)
    {
        var identity = sEnt.GetComponent<AndroidIdentityComponent>(android);
        identity.FirstName = firstName;
        identity.FullDisplayName = $"Androïde {firstName}";
        sEnt.System<MetaDataSystem>().SetEntityName(android, identity.FullDisplayName);
    }

    [Test]
    public async Task Bartender_PreparesAndServesPlayerOrder()
    {
        var (pair, map) = await Setup();
        var server = pair.Server;
        var sEnt = server.EntMan;
        EntityUid bartender = default;
        EntityUid customer = default;

        await server.WaitAssertion(() =>
        {
            bartender = sEnt.SpawnEntity("MobAndroidBartender", map.GridCoords);
            customer = sEnt.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.5f, 0)));
            Name(sEnt, bartender, "Rene");
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            sEnt.EventBus.RaiseLocalEvent(bartender, new ListenEvent("Rene, un mojito s'il vous plaît", customer));

            var conversation = sEnt.GetComponent<AndroidConversationComponent>(bartender);
            Assert.That(conversation.LastIntent, Is.EqualTo("drink_order"));
            Assert.That(conversation.LastEffect, Is.EqualTo(AndroidConversationEffect.PrepareOrder));

            var service = sEnt.GetComponent<AndroidServiceComponent>(bartender);
            var queued = service.Current ?? service.Pending.Single();
            Assert.That(queued.Item.Id, Is.EqualTo("DrinkMojito"));
            Assert.That(queued.Customer, Is.EqualTo(customer));
        });

        await server.WaitRunTicks(600);

        await server.WaitAssertion(() =>
        {
            var service = sEnt.GetComponent<AndroidServiceComponent>(bartender);
            Assert.That(service.Current, Is.Null, "The order should be finished");
            Assert.That(service.Pending, Is.Empty);

            var xform = sEnt.System<SharedTransformSystem>();
            var customerPos = xform.GetMapCoordinates(customer);
            var served = false;
            var query = sEnt.AllEntityQueryEnumerator<MetaDataComponent>();
            while (query.MoveNext(out var uid, out var meta))
            {
                if (meta.EntityPrototype?.ID == "DrinkMojito" &&
                    (xform.GetMapCoordinates(uid).Position - customerPos.Position).Length() <= 2f)
                {
                    served = true;
                }
            }
            Assert.That(served, Is.True, "A mojito should be put down next to the customer");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Bartender_ServesWaitingAndroidCustomer()
    {
        var (pair, map) = await Setup();
        var server = pair.Server;
        var sEnt = server.EntMan;
        EntityUid bartender = default;
        EntityUid customer = default;

        await server.WaitAssertion(() =>
        {
            bartender = sEnt.SpawnEntity("MobAndroidBartender", map.GridCoords);
            // A cook stays put without a kitchen, unlike a janitor who would patrol away while waiting.
            customer = sEnt.SpawnEntity("MobAndroidCook", map.GridCoords.Offset(new Vector2(0.5f, 0)));
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            server.System<AndroidCustomerSystem>().StartOrder(customer, AndroidBreakVenue.Bar);
            Assert.That(sEnt.HasComponent<AndroidCustomerOrderComponent>(customer), Is.True);
        });

        await server.WaitRunTicks(700);

        await server.WaitAssertion(() =>
        {
            var served = !sEnt.TryGetComponent<AndroidCustomerOrderComponent>(customer, out var order) || order.ConsumingItem != null;
            var service = sEnt.TryGetComponent<AndroidServiceComponent>(bartender, out var s) ? $"current={s.Current?.ItemName} pending={s.Pending.Count}" : "no service comp";
            var xform = sEnt.System<SharedTransformSystem>();
            var requested = order?.RequestedItem.Id;
            var items = new List<string>();
            var q = sEnt.AllEntityQueryEnumerator<MetaDataComponent>();
            while (q.MoveNext(out var uid, out var meta))
            {
                if (meta.EntityPrototype?.ID == requested)
                    items.Add($"{uid}@{xform.GetMapCoordinates(uid).Position} parent={sEnt.GetComponent<TransformComponent>(uid).ParentUid}");
            }
            Assert.That(served, Is.True, $"The bartender should have served the waiting android customer. {service}; requested={requested}; " +
                $"customer@{xform.GetMapCoordinates(customer).Position} bartender@{xform.GetMapCoordinates(bartender).Position} status={sEnt.GetComponent<AndroidComponent>(bartender).Status}; items: {string.Join("; ", items)}");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LeavingAndroid_DropsItsOrderAndIsRemovedAtDeadline()
    {
        var (pair, map) = await Setup();
        var server = pair.Server;
        var sEnt = server.EntMan;
        var timing = server.ResolveDependency<IGameTiming>();
        EntityUid android = default;

        await server.WaitAssertion(() =>
        {
            android = sEnt.SpawnEntity("MobAndroidJanitor", map.GridCoords);
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var orders = server.System<AndroidOrderSystem>();
            orders.IssueOrder(android, android, AndroidOrderType.TakeBreak, null);

            var comp = sEnt.GetComponent<AndroidComponent>(android);
            server.System<AndroidLifecycleSystem>().StartLeaving((android, comp));

            Assert.That(comp.Status, Is.EqualTo(AndroidStatus.Leaving));
            Assert.That(sEnt.HasComponent<AndroidOrderComponent>(android), Is.False);
            Assert.That(orders.GetStatusReport(android), Is.EqualTo(new[] { server.ResolveDependency<IPrototypeManager>().AndroidFeedback().StatusReportLeaving }));

            comp.LeaveDeadline = timing.CurTime;
        });

        await server.WaitRunTicks(3);

        await server.WaitAssertion(() =>
        {
            Assert.That(sEnt.Deleted(android), Is.True, "A leaving android past its deadline is removed");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task TreeTrace_RecordsEvaluatedBranches()
    {
        var (pair, map) = await Setup();
        var server = pair.Server;
        var sEnt = server.EntMan;
        EntityUid android = default;

        await server.WaitAssertion(() =>
        {
            android = sEnt.SpawnEntity("MobAndroidJanitor", map.GridCoords);
            sEnt.GetComponent<AndroidComponent>(android).Trace = new List<string>();
        });

        // The tree runs about every 3 ticks.
        await server.WaitRunTicks(10);

        await server.WaitAssertion(() =>
        {
            var trace = sEnt.GetComponent<AndroidComponent>(android).Trace!;
            Assert.That(trace, Has.Some.StartsWith("Janitor: "));
            Assert.That(trace, Has.Some.Contains("Flee: Failure"));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ForceOrderCommand_BypassesAuthority()
    {
        var (pair, map) = await Setup();
        var server = pair.Server;
        var sEnt = server.EntMan;
        EntityUid android = default;

        await server.WaitAssertion(() =>
        {
            android = sEnt.SpawnEntity("MobAndroidJanitor", map.GridCoords);
            Name(sEnt, android, "Zebulon");
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            server.ResolveDependency<IConsoleHost>().ExecuteCommand(null, "androidforceorder Zebulon GoToDepartment medical");

            var order = sEnt.GetComponent<AndroidOrderComponent>(android);
            Assert.That(order.OrderType, Is.EqualTo(AndroidOrderType.GoToDepartment));
            Assert.That(order.Target, Is.EqualTo("medical"));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Report_IsSpokenOnlyWithinHearingRange()
    {
        var (pair, map) = await Setup();
        var server = pair.Server;
        var sEnt = server.EntMan;

        await server.WaitAssertion(() =>
        {
            var android = sEnt.SpawnEntity("MobAndroidJanitor", map.GridCoords);
            var near = sEnt.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(2, 0)));
            var far = sEnt.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(40, 0)));
            var dialogue = server.System<AndroidDialogueSystem>();

            Assert.That(dialogue.InEarshot(android, near), Is.True);
            Assert.That(dialogue.InEarshot(android, far), Is.False, "A far issuer gets the report over the radio");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Resupply_WaitsForThresholdUnlessAnItemIsMissingEntirely()
    {
        var (pair, map) = await Setup();
        var server = pair.Server;
        var sEnt = server.EntMan;
        EntityUid android = default;

        await server.WaitAssertion(() =>
        {
            android = sEnt.SpawnEntity("MobAndroidJanitor", map.GridCoords);
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var inventory = sEnt.GetComponent<AndroidInventoryComponent>(android);
            var invSys = server.System<AndroidInventorySystem>();
            Assert.That(invSys.NeedsResupply(android, inventory), Is.False);

            var sign = invSys.FindItemByPrototype(android, inventory, "WetFloorSign")!.Value;
            inventory.OwnedItems.Remove(sign);
            sEnt.DeleteEntity(sign);
            Assert.That(invSys.NeedsResupply(android, inventory), Is.False, "One sign out of four is below the threshold");

            var bucket = invSys.FindItemByPrototype(android, inventory, "Bucket")!.Value;
            inventory.OwnedItems.Remove(bucket);
            sEnt.DeleteEntity(bucket);
            Assert.That(invSys.NeedsResupply(android, inventory), Is.True, "The only bucket is missing");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Bartender_UnknownDrink_PreparesSomethingElseAndSaysSo()
    {
        var (pair, map) = await Setup();
        var server = pair.Server;
        var sEnt = server.EntMan;
        EntityUid bartender = default;
        EntityUid customer = default;

        await server.WaitAssertion(() =>
        {
            bartender = sEnt.SpawnEntity("MobAndroidBartender", map.GridCoords);
            customer = sEnt.SpawnEntity("MobHuman", map.GridCoords.Offset(new Vector2(0.5f, 0)));
            Name(sEnt, bartender, "Zephyrin");
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            sEnt.EventBus.RaiseLocalEvent(bartender, new ListenEvent("Zephyrin, un verre de bidule s'il te plaît", customer));

            var job = server.ResolveDependency<IPrototypeManager>().AndroidJobData(AndroidJob.Bartender)!;
            var service = sEnt.GetComponent<AndroidServiceComponent>(bartender);
            var request = service.Current ?? service.Pending.Single();
            Assert.That(request.Substituted, Is.True);
            Assert.That(job.FallbackMenu, Does.Contain(request.Item));

            var expected = job.LinesFor("fr")!.UnknownOrder.Select(l => AndroidData.Format(l, ("item", request.ItemName)));
            Assert.That(expected, Does.Contain(sEnt.GetComponent<AndroidConversationComponent>(bartender).LastReply));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task ServiceAndroids_TakeBreaksAwayFromTheirOwnVenue()
    {
        var (pair, map) = await Setup();
        var server = pair.Server;
        var sEnt = server.EntMan;
        EntityUid bartender = default;
        EntityUid cook = default;

        await server.WaitAssertion(() =>
        {
            bartender = sEnt.SpawnEntity("MobAndroidBartender", map.GridCoords);
            cook = sEnt.SpawnEntity("MobAndroidCook", map.GridCoords);
            sEnt.GetComponent<AndroidComponent>(bartender).NextBreakAt = server.Timing.CurTime;
            sEnt.GetComponent<AndroidComponent>(cook).NextBreakAt = server.Timing.CurTime;
        });

        await server.WaitRunTicks(15);

        await server.WaitAssertion(() =>
        {
            Assert.That(sEnt.GetComponent<AndroidComponent>(bartender).BreakVenue, Is.EqualTo(AndroidBreakVenue.Kitchen));
            Assert.That(sEnt.GetComponent<AndroidComponent>(cook).BreakVenue, Is.EqualTo(AndroidBreakVenue.Bar));
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Janitor_FinishesOffACriticalMouse()
    {
        var (pair, map) = await Setup();
        var server = pair.Server;
        var sEnt = server.EntMan;
        EntityUid mouse = default;

        await server.WaitAssertion(() =>
        {
            sEnt.SpawnEntity("MobAndroidJanitor", map.GridCoords);
            mouse = sEnt.SpawnEntity("MobMouse", map.GridCoords.Offset(new Vector2(0.6f, 0)));
            server.System<MobStateSystem>().ChangeMobState(mouse, MobState.Critical);
        });

        await server.WaitRunTicks(600);

        await server.WaitAssertion(() =>
        {
            Assert.That(sEnt.Deleted(mouse) || sEnt.GetComponent<MobStateComponent>(mouse).CurrentState == MobState.Dead, Is.True,
                "The janitor keeps hitting a critical mouse until it dies instead of trying to drag it alive");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task Hurt_AndroidFleesThenGoesBackToWork()
    {
        var (pair, map) = await Setup();
        var server = pair.Server;
        var sEnt = server.EntMan;
        EntityUid android = default;

        await server.WaitAssertion(() =>
        {
            android = sEnt.SpawnEntity("MobAndroidJanitor", map.GridCoords);
        });

        await server.WaitRunTicks(2);

        await server.WaitAssertion(() =>
        {
            var blunt = server.ResolveDependency<IPrototypeManager>().Index<Content.Shared.Damage.Prototypes.DamageTypePrototype>("Blunt");
            server.System<Content.Shared.Damage.Systems.DamageableSystem>().TryChangeDamage(android, new Content.Shared.Damage.DamageSpecifier(blunt, 10));
        });

        await server.WaitRunTicks(5);
        await server.WaitAssertion(() =>
            Assert.That(sEnt.GetComponent<AndroidComponent>(android).Status, Is.EqualTo(AndroidStatus.Fleeing), "A real hit makes the android flee"));

        // About 10 seconds later the scare is over, even though the damage is still there.
        await server.WaitRunTicks(300);
        await server.WaitAssertion(() =>
            Assert.That(sEnt.GetComponent<AndroidComponent>(android).Status, Is.Not.EqualTo(AndroidStatus.Fleeing), "Old damage alone does not keep it fleeing"));

        await pair.CleanReturnAsync();
    }
}
