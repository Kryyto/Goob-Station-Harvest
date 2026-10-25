// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Server._Harvest.AndroidNpc.BehaviorTree.Nodes;
using Content.Server._Harvest.AndroidNpc.Components;
using Content.Server._Harvest.AndroidNpc.Data;
using Content.Server._Harvest.AndroidNpc.Utilities;
using Content.Shared.Cargo.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Nutrition.Components;
using Content.Shared.Nutrition.EntitySystems;
using Content.Shared.Placeable;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server._Harvest.AndroidNpc.Systems;

/// <summary>
/// Cook and bartender androids: takes orders (from players through conversation, and from android customers
/// waiting at the venue), prepares them for the menu item's preparation time and puts them down next to the
/// customer. Preparation is simulated: the android does not run the real cooking or mixing mechanics.
/// </summary>
public sealed class AndroidServiceSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly AndroidDialogueSystem _dialogue = default!;
    [Dependency] private readonly AndroidCustomerSystem _customer = default!;
    [Dependency] private readonly ClaimedTargetsSystem _claims = default!;
    [Dependency] private readonly OpenableSystem _openable = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly AndroidInventorySystem _inventory = default!;

    private static readonly TimeSpan CustomerScanInterval = TimeSpan.FromSeconds(2);
    private const float CustomerScanRange = 20f;
    private const float MaxCustomerDistance = 25f;
    private const float HandOverDistance = 1.5f;
    private const float DefaultPrepSeconds = 6f;
    private const float TipSearchRange = 5f;

    private readonly Dictionary<AndroidJob, AndroidMenuMatcher> _menus = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(_ => _menus.Clear());
    }

    /// <summary>
    /// The job's whole menu, hand-written entries first, built once.
    /// </summary>
    public AndroidMenuMatcher Menu(AndroidJob job)
    {
        if (!_menus.TryGetValue(job, out var menu))
            _menus[job] = menu = new AndroidMenuMatcher(_prototypeManager.AndroidMenu(job), _prototypeManager);

        return menu;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;
        var query = EntityQueryEnumerator<AndroidServiceComponent, AndroidComponent>();

        while (query.MoveNext(out var uid, out var service, out var android))
        {
            if (now < service.NextCustomerScan || android.Status is AndroidStatus.Fleeing or AndroidStatus.Leaving)
                continue;

            service.NextCustomerScan = now + CustomerScanInterval;
            QueueWaitingCustomers(uid, android, service);
        }
    }

    /// <summary>
    /// Queues an order for a player: the first menu entry the request mentions. When nothing on the menu is
    /// recognized, something from the fallback menu is prepared instead and <paramref name="substitution"/> is the
    /// line explaining it, to say instead of the usual reply. Returns false if the android has no menu.
    /// </summary>
    public bool TryQueueRequest(EntityUid worker, EntityUid customer, string request, string language, out string? substitution)
    {
        substitution = null;

        if (!TryComp<AndroidComponent>(worker, out var android) || Menu(android.Job) is not { } menu || !menu.Items.Any())
            return false;

        if (menu.Match(request, language, fuzzy: true) is { } item)
            return Enqueue(worker, customer, item.Item, item.PrepSeconds, language);

        var job = _prototypeManager.AndroidJobData(android.Job);
        var fallback = job is { FallbackMenu.Count: > 0 } ? _random.Pick(job.FallbackMenu) : _random.Pick(menu.Items.ToList()).Item;
        var prep = menu.Items.FirstOrDefault(m => m.Item == fallback)?.PrepSeconds ?? DefaultPrepSeconds;

        if (!Enqueue(worker, customer, fallback, prep, language, substituted: true))
            return false;

        if (job?.LinesFor(language) is { } lines && _dialogue.Pick(lines.UnknownOrder) is { } line)
            substitution = AndroidData.Format(line, ("item", MenuName(android.Job, fallback, language)));

        return true;
    }

    /// <summary>
    /// What the android calls an item when speaking: the menu name in that language, or the entity name.
    /// </summary>
    public string MenuName(AndroidJob job, EntProtoId item, string language)
    {
        return Menu(job).Items.FirstOrDefault(m => m.Item == item)?.Names.GetValueOrDefault(language) ??
               (_prototypeManager.TryIndex(item, out var proto) ? proto.Name : item.Id);
    }

    /// <summary>
    /// Returns true if the text names an item on the job's menu, without allowing typos.
    /// </summary>
    public bool MentionsMenuItem(AndroidJob job, string text, string language)
    {
        return Menu(job).Match(text, language, fuzzy: false) != null;
    }

    /// <summary>
    /// A few menu names to answer "what can you serve": the hand-written favourites first, then random picks.
    /// </summary>
    public List<string> MenuSample(AndroidJob job, string language, int count)
    {
        var featured = _prototypeManager.AndroidJobData(job)?.Menu.Select(m => m.Item).ToHashSet() ?? new();
        var items = Menu(job).Items.ToList();
        var others = items.Where(i => !featured.Contains(i.Item)).ToList();
        _random.Shuffle(others);

        return items.Where(i => featured.Contains(i.Item)).Take(count / 2)
            .Concat(others)
            .Take(count)
            .Select(i => MenuName(job, i.Item, language))
            .ToList();
    }

    private bool Enqueue(EntityUid worker, EntityUid customer, EntProtoId item, float prepSeconds, string language, bool substituted = false)
    {
        if (!_prototypeManager.HasIndex(item) || !TryComp<AndroidComponent>(worker, out var android))
            return false;

        var name = MenuName(android.Job, item, language);
        var service = EnsureComp<AndroidServiceComponent>(worker);
        service.Pending.Enqueue(new AndroidServiceRequest(customer, Name(customer), item, name, prepSeconds, language, substituted));
        return true;
    }

    /// <summary>
    /// Android customers on break at this worker's venue get served automatically.
    /// </summary>
    private void QueueWaitingCustomers(EntityUid worker, AndroidComponent android, AndroidServiceComponent service)
    {
        var venue = android.Job switch
        {
            AndroidJob.Bartender => AndroidBreakVenue.Bar,
            AndroidJob.Cook => AndroidBreakVenue.Kitchen,
            _ => (AndroidBreakVenue?) null,
        };

        if (venue == null || !TryComp<TransformComponent>(worker, out var workerXform))
            return;

        var menu = Menu(android.Job).Items;
        var query = EntityQueryEnumerator<AndroidCustomerOrderComponent, TransformComponent>();

        while (query.MoveNext(out var customer, out var order, out var xform))
        {
            if (customer == worker || order.Served || order.Delivered || order.ConsumingItem != null || order.Venue != venue ||
                xform.MapID != workerXform.MapID ||
                (_transform.GetWorldPosition(xform) - _transform.GetWorldPosition(workerXform)).Length() > CustomerScanRange ||
                !_claims.TryClaim(worker, customer, TimeSpan.FromSeconds(_prototypeManager.AndroidConfig().ClaimTtlSeconds)))
            {
                continue;
            }

            if (service.Current?.Customer == customer || service.Pending.Any(r => r.Customer == customer))
                continue;

            Enqueue(worker, customer, order.RequestedItem, menu.FirstOrDefault(m => m.Item == order.RequestedItem)?.PrepSeconds ?? DefaultPrepSeconds,
                _prototypeManager.AndroidConfig().Language);
        }
    }

    /// <summary>
    /// Selects the order to work on and returns where to walk to serve it, or null if there is nothing to serve.
    /// The item is put down on the nearest counter or table next to the customer.
    /// </summary>
    public EntityCoordinates? GetServeCoordinates(EntityUid worker)
    {
        if (!TryComp<AndroidServiceComponent>(worker, out var service))
            return null;

        while (service.Current is { } current && !IsValidCustomer(worker, current.Customer))
            Drop(service);

        while (service.Current == null && service.Pending.TryDequeue(out var next))
        {
            if (!IsValidCustomer(worker, next.Customer))
                continue;

            service.Current = next;
            service.ReadyAt = null;
            service.DropCoordinates = FindDropCoordinates(next.Customer);
        }

        // Walk to the counter or table next to the customer, reached from the venue's side, rather than out
        // to the customer; only without such a surface does the order go to the customer's feet.
        return service.Current is { } request && TryComp<TransformComponent>(request.Customer, out var customerXform)
            ? service.DropCoordinates ?? customerXform.Coordinates
            : null;
    }

    /// <summary>
    /// Prepares the current order next to the customer, then puts it down.
    /// </summary>
    public BehaviorStatus TickPreparation(EntityUid worker)
    {
        if (!TryComp<AndroidServiceComponent>(worker, out var service) ||
            service.Current is not { } request ||
            service.DropCoordinates is not { } drop)
        {
            return BehaviorStatus.Failure;
        }

        var lines = _dialogue.LinesFor(worker, request.Language);
        var values = new[] { ("item", request.ItemName), ("customer", request.CustomerName) };

        if (service.ReadyAt == null)
        {
            service.ReadyAt = _timing.CurTime + TimeSpan.FromSeconds(request.PrepSeconds);
            if (lines != null && !request.Substituted)
                _dialogue.SpeakRandom(worker, lines.Preparing, values);

            return BehaviorStatus.Running;
        }

        if (_timing.CurTime < service.ReadyAt)
            return BehaviorStatus.Running;

        // Cans and bottles come open so the customer can drink right away.
        var served = Spawn(request.Item, drop);
        if (TryComp<OpenableComponent>(served, out var openable))
            _openable.SetOpen(served, true, openable);

        if (TryComp<AndroidCustomerOrderComponent>(request.Customer, out var order))
        {
            order.Delivered = true;
            order.ServedItem = served;
        }

        if (lines != null)
            _dialogue.SpeakRandom(worker, lines.Served, values);

        Drop(service);
        return BehaviorStatus.Success;
    }

    private void Drop(AndroidServiceComponent service)
    {
        if (service.Current is { } request)
            _claims.ReleaseClaim(request.Customer);

        service.Current = null;
        service.ReadyAt = null;
        service.DropCoordinates = null;
    }

    private bool IsValidCustomer(EntityUid worker, EntityUid customer)
    {
        if (Deleted(customer) ||
            TryComp<MobStateComponent>(customer, out var mob) && mob.CurrentState == MobState.Dead ||
            !TryComp<TransformComponent>(customer, out var customerXform) ||
            !TryComp<TransformComponent>(worker, out var workerXform) ||
            customerXform.MapID != workerXform.MapID)
        {
            return false;
        }

        return (_transform.GetWorldPosition(customerXform) - _transform.GetWorldPosition(workerXform)).Length() <= MaxCustomerDistance;
    }

    /// <summary>
    /// The nearest table or counter next to the customer, or the customer's own tile when none is within reach,
    /// so an android customer still sees its order.
    /// </summary>
    private EntityCoordinates FindDropCoordinates(EntityUid customer)
    {
        var customerCoords = Transform(customer).Coordinates;
        var surface = _customer.FindSurfaceNear(customer);

        return _transform.InRange(surface, customerCoords, HandOverDistance) ? surface : customerCoords;
    }

    /// <summary>
    /// Money left on a table or counter near the venue, for the android to pick up between orders.
    /// </summary>
    public EntityCoordinates? GetTipCoordinates(EntityUid worker, EntityCoordinates? workSpot)
    {
        if (!TryComp<AndroidServiceComponent>(worker, out var service) || workSpot is not { } spot)
            return null;

        if (service.TipTarget is { } current && IsCollectableTip(worker, current, spot))
            return Transform(current).Coordinates;

        service.TipTarget = null;

        foreach (var (cash, _) in _lookup.GetEntitiesInRange<CashComponent>(spot, TipSearchRange, LookupFlags.Uncontained))
        {
            if (!IsCollectableTip(worker, cash, spot) || !_claims.TryClaim(worker, cash, TimeSpan.FromSeconds(30)))
                continue;

            service.TipTarget = cash;
            return Transform(cash).Coordinates;
        }

        return null;
    }

    /// <summary>
    /// Picks up the targeted tip and puts it away.
    /// </summary>
    public BehaviorStatus TryCollectTip(EntityUid worker)
    {
        if (!TryComp<AndroidServiceComponent>(worker, out var service) || service.TipTarget is not { } cash || Deleted(cash))
            return BehaviorStatus.Failure;

        service.TipTarget = null;
        _claims.ReleaseClaim(cash);
        return _inventory.TryPocket(worker, cash) ? BehaviorStatus.Success : BehaviorStatus.Failure;
    }

    /// <summary>
    /// Loose money lying on a table near the venue, not claimed by another android.
    /// </summary>
    private bool IsCollectableTip(EntityUid worker, EntityUid cash, EntityCoordinates spot)
    {
        if (Deleted(cash) ||
            !TryComp<TransformComponent>(cash, out var xform) ||
            _containers.IsEntityInContainer(cash) ||
            _claims.IsClaimedByOther(cash, worker) ||
            !_transform.InRange(xform.Coordinates, spot, TipSearchRange))
        {
            return false;
        }

        // Only money put down on furniture: cash dropped on the floor may well belong to someone.
        foreach (var ent in _lookup.GetEntitiesIntersecting(cash, LookupFlags.Static))
        {
            if (HasComp<PlaceableSurfaceComponent>(ent))
                return true;
        }

        return false;
    }
}
