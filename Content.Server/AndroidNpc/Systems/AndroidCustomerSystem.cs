// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server.AndroidNpc.Components;
using Content.Server.AndroidNpc.Utilities;
using Content.Shared.AndroidNpc;
using Content.Shared.Buckle.Components;
using Content.Shared.DoAfter;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Item;
using Content.Shared.Nutrition;
using Content.Shared.Nutrition.Components;
using Content.Shared.Nutrition.EntitySystems;
using Content.Shared.Placeable;
using Content.Shared.Stacks;
using Content.Shared.Tag;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.AndroidNpc.Systems;

/// <summary>
/// Drives the bar/kitchen "customer" mini-game: when an android sits down for its break at the
/// bar or kitchen, it asks for a random drink or food, waits a few minutes (shown as a normal
/// DoAfter progress bar above its head) for the order to show up nearby, eats/drinks it entirely
/// once it does, and either tips the crew who served it or leaves grumpy if nobody does in time.
/// </summary>
public sealed class AndroidCustomerSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _proto = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedStackSystem _stack = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly IngestionSystem _ingestion = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly AndroidDialogueSystem _dialogue = default!;
    [Dependency] private readonly TagSystem _tag = default!;
    [Dependency] private readonly OpenableSystem _openable = default!;
    [Dependency] private readonly AndroidServiceSystem _service = default!;

    private static readonly TimeSpan MinWait = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(1);

    // How far the android looks around itself for its order once seated.
    private const float ScanRadius = 2f;

    // How far the android will look for a table or counter to leave its tip on.
    private const float TableSearchRadius = 8f;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<AndroidCustomerOrderComponent, AndroidCustomerWaitDoAfterEvent>(OnWaitFinished);
        SubscribeLocalEvent<AndroidCustomerOrderComponent, ComponentShutdown>(OnOrderShutdown);
    }

    /// <summary>
    /// Places a random order for the given venue if the android does not already have one active.
    /// Announces the order out loud and starts the waiting progress bar.
    /// </summary>
    public void StartOrder(EntityUid android, AndroidBreakVenue venue)
    {
        if (Deleted(android) || HasComp<AndroidCustomerOrderComponent>(android))
            return;

        var customer = _proto.AndroidFeedback().Customer;
        var config = _proto.AndroidConfig();
        var pool = venue == AndroidBreakVenue.Bar ? config.CustomerDrinks : config.CustomerFood;
        if (pool.Count == 0)
            return;

        var choice = _random.Pick(pool);

        if (!_proto.TryIndex(choice, out var choiceProto))
            return;

        var order = AddComp<AndroidCustomerOrderComponent>(android);
        order.RequestedItem = choice;
        order.Venue = venue;
        order.NextScanAt = _timing.CurTime;

        var waitSeconds = _random.NextFloat((float) MinWait.TotalSeconds, (float) MaxWait.TotalSeconds);
        var doAfterArgs = new DoAfterArgs(EntityManager, android, TimeSpan.FromSeconds(waitSeconds), new AndroidCustomerWaitDoAfterEvent(), android)
        {
            BreakOnMove = false,
            BreakOnDamage = true,
            NeedHand = false,
            RequireCanInteract = false,
            Hidden = false,
            BlockDuplicate = false,
            CancelDuplicate = false,
        };

        if (_doAfter.TryStartDoAfter(doAfterArgs, out var id))
            order.WaitDoAfter = id;

        SpeakFormatted(android, venue == AndroidBreakVenue.Bar ? customer.BarOrder : customer.KitchenOrder, ItemName(venue, order.RequestedItem));
    }

    /// <summary>
    /// Returns true while the android is still waiting on a food/drink order.
    /// </summary>
    public bool HasActiveOrder(EntityUid android)
    {
        return HasComp<AndroidCustomerOrderComponent>(android);
    }

    /// <summary>
    /// Makes the android restate its current order out loud, e.g. because the first request was
    /// missed. Returns false (and says nothing) if there is no active order.
    /// </summary>
    public bool TryRestateOrder(EntityUid android)
    {
        if (!TryComp<AndroidCustomerOrderComponent>(android, out var order) || order.Served)
            return false;

        if (!_proto.TryIndex(order.RequestedItem, out var proto))
            return false;

        var customer = _proto.AndroidFeedback().Customer;
        SpeakFormatted(android, order.Venue == AndroidBreakVenue.Bar ? customer.BarReminder : customer.KitchenReminder, ItemName(order.Venue, order.RequestedItem));
        return true;
    }

    /// <summary>
    /// Cancels an android's active order without tipping or complaint, e.g. because the android
    /// had to abandon its break for something more important.
    /// </summary>
    /// <summary>
    /// What the customer calls its order: the venue's menu name ("bière"), not the entity name ("verre métamorphe").
    /// </summary>
    private string ItemName(AndroidBreakVenue venue, EntProtoId item)
    {
        var job = venue == AndroidBreakVenue.Bar ? AndroidJob.Bartender : AndroidJob.Cook;
        return _service.MenuName(job, item, _proto.AndroidConfig().Language);
    }

    public void CancelOrder(EntityUid android)
    {
        if (!TryComp<AndroidCustomerOrderComponent>(android, out var order))
            return;

        if (order.WaitDoAfter is { } waitId && _doAfter.IsRunning(waitId))
            _doAfter.Cancel(waitId);
        RemComp<AndroidCustomerOrderComponent>(android);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<AndroidCustomerOrderComponent>();

        while (query.MoveNext(out var android, out var order))
        {
            if (order.Served)
                continue;

            if (order.ConsumingItem is { } item)
            {
                // Already found and started eating/drinking something - keep going until it's
                // been consumed entirely (or otherwise disappears), then tip and finish up.
                if (Deleted(item) ||
                    !TryComp<EdibleComponent>(item, out var edible) ||
                    _ingestion.IsEmpty((item, edible)))
                {
                    FinishOrder(android, order);
                }

                continue;
            }

            if (now < order.NextScanAt)
                continue;

            order.NextScanAt = now + ScanInterval;
            TryFindAndConsumeOrder(android, order);
        }
    }

    /// <summary>
    /// Looks for a matching drink/food within <see cref="ScanRadius"/> of the android and, if
    /// found, starts eating/drinking it. Items already held by someone else (in a hand, bag, etc.)
    /// are not picked up on - the item needs to be left out where the android can see it.
    /// </summary>
    private void TryFindAndConsumeOrder(EntityUid android, AndroidCustomerOrderComponent order)
    {
        if (!TryComp<TransformComponent>(android, out var xform))
            return;

        var wantsDrink = order.Venue == AndroidBreakVenue.Bar;
        var androidPos = _transform.GetWorldPosition(xform);

        EntityUid? found = null;
        var bestDist = ScanRadius * ScanRadius;

        // What the bartender or cook put down for it comes first.
        if (order.ServedItem is { } served && !Deleted(served) && !order.Refused.Contains(served) &&
            _transform.InRange(Transform(served).Coordinates, xform.Coordinates, ScanRadius))
        {
            found = served;
            bestDist = -1f;
        }

        // Uncontained only - items still held in someone's hand or stashed in a bag/box don't
        // count as "left out" for the android to notice and take.
        foreach (var ent in _lookup.GetEntitiesInRange(xform.Coordinates, ScanRadius, LookupFlags.Uncontained))
        {
            if (ent == android || order.Refused.Contains(ent))
                continue;

            if (!TryComp<EdibleComponent>(ent, out var edible))
                continue;

            if (wantsDrink && edible.Edible != IngestionSystem.Drink)
                continue;

            if (!wantsDrink && edible.Edible != IngestionSystem.Food)
                continue;

            if (_ingestion.IsEmpty((ent, edible)))
                continue;

            if (!TryComp<TransformComponent>(ent, out var itemXform))
                continue;

            var dist = (_transform.GetWorldPosition(itemXform) - androidPos).LengthSquared();
            if (dist > bestDist)
                continue;

            bestDist = dist;
            found = ent;
        }

        if (found is not { } item)
            return;

        // Best-effort: hold it while eating, but self-ingestion doesn't strictly require a hand.
        _hands.TryPickupAnyHand(android, item);
        _openable.TryOpen(item, user: android);

        // Something it cannot eat (a sealed packet at its feet...) is left alone from now on.
        if (!_ingestion.TryIngest(android, android, item))
        {
            _hands.TryDrop(android, item);
            order.Refused.Add(item);
            return;
        }

        order.ConsumingItem = item;
        order.ConsumingItemIsExactMatch = MetaData(item).EntityPrototype?.ID == order.RequestedItem.Id;

        // The order has arrived - stop the waiting progress bar now rather than leaving it
        // running (and thus risking a grumpy timeout) while the android eats/drinks it.
        if (order.WaitDoAfter is { } waitId && _doAfter.IsRunning(waitId))
            _doAfter.Cancel(waitId);
    }

    private void FinishOrder(EntityUid android, AndroidCustomerOrderComponent order)
    {
        order.Served = true;
        if (order.WaitDoAfter is { } waitId && _doAfter.IsRunning(waitId))
            _doAfter.Cancel(waitId);

        var tip = order.ConsumingItemIsExactMatch ? _random.Next(20, 51) : _random.Next(5, 26);

        var cash = Spawn("SpaceCash", FindSurfaceNear(android));
        _stack.SetCount(cash, tip);

        SpeakFormatted(android, _proto.AndroidFeedback().Customer.Thanks, tip.ToString());

        RemComp<AndroidCustomerOrderComponent>(android);
    }

    /// <summary>
    /// Finds the nearest table or counter where the tip can be placed, so it does not end up on
    /// the floor. If no surface is in range, falls back to the android's current coordinates.
    /// </summary>
    public EntityCoordinates FindSurfaceNear(EntityUid android)
    {
        var androidXform = Transform(android);
        var androidPos = _transform.GetWorldPosition(androidXform);

        EntityUid? bestTable = null;
        var bestDist = float.MaxValue;

        foreach (var ent in _lookup.GetEntitiesInRange(androidXform.Coordinates, TableSearchRadius))
        {
            if (!TryComp<PlaceableSurfaceComponent>(ent, out var surface) || !surface.IsPlaceable)
                continue;

            // Only count anchored furniture/counters, not items with surfaces (e.g. crates) or beds.
            if (HasComp<ItemComponent>(ent))
                continue;

            if (TryComp<StrapComponent>(ent, out _))
                continue;

            if (!TryComp<TransformComponent>(ent, out var tableXform))
                continue;

            if (tableXform.MapUid != androidXform.MapUid)
                continue;

            var dist = (_transform.GetWorldPosition(tableXform) - androidPos).LengthSquared();
            if (dist >= bestDist)
                continue;

            bestDist = dist;
            bestTable = ent;
        }

        if (bestTable is { } table)
        {
            var surface = Comp<PlaceableSurfaceComponent>(table);
            var tableXform = Transform(table);
            var dropCoords = surface.PlaceCentered
                ? tableXform.Coordinates.Offset(surface.PositionOffset)
                : tableXform.Coordinates;

            return dropCoords;
        }

        return androidXform.Coordinates;
    }

    private void OnWaitFinished(Entity<AndroidCustomerOrderComponent> ent, ref AndroidCustomerWaitDoAfterEvent args)
    {
        if (args.Cancelled)
            return;

        // Timed out without being served: leave grumpy.
        if (_dialogue.Pick(_proto.AndroidFeedback().Customer.Grumpy) is { } grumpy)
            _dialogue.Speak(ent, grumpy);

        if (TryComp<AndroidComponent>(ent, out var android))
            android.BreakUntil = _timing.CurTime;

        RemComp<AndroidCustomerOrderComponent>(ent);
    }

    private void OnOrderShutdown(Entity<AndroidCustomerOrderComponent> ent, ref ComponentShutdown args)
    {
        if (ent.Comp.WaitDoAfter is { } waitId && _doAfter.IsRunning(waitId))
            _doAfter.Cancel(waitId);
    }

    private void SpeakFormatted(EntityUid android, List<string> lines, string value)
    {
        if (_dialogue.Pick(lines) is { } line)
            _dialogue.Speak(android, string.Format(line, value));
    }
}
