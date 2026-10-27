// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Numerics;
using Content.Client.Administration.Managers;
using Content.Client.Construction;
using Content.Client.ContextMenu.UI;
using Content.Client.Examine;
using Content.Client.Interaction;
using Content.Client.Stunnable;
using Content.Client.Tabletop;
using Content.Client.UserInterface.Systems.Actions;
using Content.Shared._Harvest.AdminSquad;
using Content.Shared.Administration;
using Content.Shared.Input;
using Content.Shared.Interaction;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Client.Player;
using Robust.Client.UserInterface;
using Robust.Shared.Input;
using Robust.Shared.Input.Binding;
using Robust.Shared.Map;
using Robust.Shared.Player;
using Robust.Shared.Timing;

namespace Content.Client._Harvest.AdminSquad;

/// <summary>
/// Admin RTS tool: box-select mobs with the left button (shift adds), right-click to move them there,
/// ctrl + right-click to attack-move, right-click on a mob or a structure (door, wall...) to attack it.
/// Hold, release and speaking through the units are in <see cref="AdminSquadWindow"/>.
/// The server only keeps the living mobs that no player controls.
/// </summary>
public sealed class AdminSquadSystem : EntitySystem
{
    [Dependency] private readonly IClientAdminManager _admin = default!;
    [Dependency] private readonly IEyeManager _eye = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IInputManager _input = default!;
    [Dependency] private readonly IOverlayManager _overlays = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly MobStateSystem _mobState = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    /// <summary>
    /// A drag shorter than this, in pixels, is a click.
    /// </summary>
    private const float ClickThreshold = 6f;

    /// <summary>
    /// How far from a click a mob can be picked.
    /// </summary>
    private const float ClickPickRange = 0.75f;

    private static readonly TimeSpan MarkerDuration = TimeSpan.FromSeconds(4);

    public bool Active { get; private set; }

    public readonly HashSet<EntityUid> Selected = new();

    /// <summary>
    /// Where each unit was last sent, shown for a few seconds.
    /// </summary>
    public readonly Dictionary<EntityUid, (MapCoordinates Destination, AdminSquadOrder Order, TimeSpan Until)> Destinations = new();

    /// <summary>
    /// Screen position where the current box selection started.
    /// </summary>
    public Vector2? DragStart { get; private set; }

    private bool _additive;
    private AdminSquadOverlay? _overlay;
    private AdminSquadWindow? _window;

    public event Action? SelectionChanged;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeNetworkEvent<AdminSquadOrderResultEvent>(OnOrderResult);

        CommandBinds.Builder
            .BindBefore(EngineKeyFunctions.Use,
                new PointerStateInputCmdHandler((_, _, _) => StartDrag(false), (_, _, _) => EndDrag(), true),
                typeof(SharedInteractionSystem), typeof(DragDropSystem), typeof(ActionUIController),
                typeof(ConstructionSystem), typeof(TabletopSystem))
            .BindBefore(ContentKeyFunctions.ExamineEntity,
                new PointerStateInputCmdHandler((_, _, _) => StartDrag(true), (_, _, _) => EndDrag(), true),
                typeof(ExamineSystem))
            .BindBefore(EngineKeyFunctions.UseSecondary,
                new PointerInputCmdHandler((_, coords, uid) => OrderAt(AdminSquadOrder.Move, coords, uid), true),
                typeof(EntityMenuUIController), typeof(SharedInteractionSystem), typeof(TabletopSystem), typeof(StunSystem))
            .Bind(ContentKeyFunctions.MovePulledObject,
                new PointerInputCmdHandler((_, coords, uid) => OrderAt(AdminSquadOrder.AttackMove, coords, uid), true))
            .Register<AdminSquadSystem>();
    }

    public override void Shutdown()
    {
        base.Shutdown();
        SetActive(false);
        CommandBinds.Unregister<AdminSquadSystem>();
    }

    public void Toggle()
    {
        SetActive(!Active);
    }

    public void SetActive(bool active)
    {
        if (active && !_admin.HasFlag(AdminFlags.Fun))
            return;

        if (Active == active)
            return;

        Active = active;
        DragStart = null;
        Selected.Clear();
        Destinations.Clear();

        if (active)
        {
            _overlay = new AdminSquadOverlay(this, _transform, _eye, _input);
            _overlays.AddOverlay(_overlay);

            _window = new AdminSquadWindow(this);
            _window.OnClose += () => SetActive(false);
            _window.OpenCentered();
        }
        else
        {
            if (_overlay != null)
                _overlays.RemoveOverlay(_overlay);
            _overlay = null;

            var window = _window;
            _window = null;
            window?.Close();
        }

        SelectionChanged?.Invoke();
    }

    private bool StartDrag(bool additive)
    {
        if (!Active)
            return false;

        DragStart = _input.MouseScreenPosition.Position;
        _additive = additive;
        return true;
    }

    private bool EndDrag()
    {
        if (!Active)
            return false;

        if (DragStart is not { } start)
            return true;

        DragStart = null;
        var end = _input.MouseScreenPosition.Position;

        if (!_additive)
            Selected.Clear();

        if ((end - start).Length() < ClickThreshold)
            SelectAtPoint(end);
        else
            SelectInScreenRect(UIBox2.FromDimensions(Vector2.Min(start, end), Vector2.Abs(end - start)));

        SelectionChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Selects every commandable mob shown in the main viewport.
    /// </summary>
    public void SelectAllOnScreen()
    {
        if (!Active || _eye.MainViewport is not Control viewport)
            return;

        SelectInScreenRect(UIBox2.FromDimensions(viewport.GlobalPixelPosition, viewport.PixelSize));
        SelectionChanged?.Invoke();
    }

    public void ClearSelection()
    {
        Selected.Clear();
        SelectionChanged?.Invoke();
    }

    private void SelectAtPoint(Vector2 screenPos)
    {
        var map = _eye.PixelToMap(screenPos);
        if (map.MapId == MapId.Nullspace)
            return;

        var box = Box2.CenteredAround(map.Position, new Vector2(ClickPickRange * 2));
        var mobs = new HashSet<Entity<MobStateComponent>>();
        _lookup.GetEntitiesIntersecting(map.MapId, box, mobs);

        EntityUid? best = null;
        var bestDistance = ClickPickRange;
        foreach (var mob in mobs)
        {
            if (!CanSelect(mob))
                continue;

            var distance = (_transform.GetWorldPosition(mob) - map.Position).Length();
            if (distance > bestDistance)
                continue;

            best = mob;
            bestDistance = distance;
        }

        if (best == null)
            return;

        // Shift-clicking a selected unit deselects it.
        if (_additive && Selected.Contains(best.Value))
            Selected.Remove(best.Value);
        else
            Selected.Add(best.Value);
    }

    private void SelectInScreenRect(UIBox2 rect)
    {
        // The eye can be rotated: look up the world box around the four corners, then keep what is inside on screen.
        var corners = new[]
        {
            _eye.PixelToMap(rect.TopLeft), _eye.PixelToMap(rect.TopRight),
            _eye.PixelToMap(rect.BottomLeft), _eye.PixelToMap(rect.BottomRight),
        };

        var mapId = corners[0].MapId;
        if (mapId == MapId.Nullspace)
            return;

        var min = corners.Select(c => c.Position).Aggregate(Vector2.Min);
        var max = corners.Select(c => c.Position).Aggregate(Vector2.Max);

        var mobs = new HashSet<Entity<MobStateComponent>>();
        _lookup.GetEntitiesIntersecting(mapId, new Box2(min, max), mobs);

        foreach (var mob in mobs)
        {
            if (!CanSelect(mob))
                continue;

            var screen = _eye.MapToScreen(_transform.GetMapCoordinates(mob)).Position;
            if (rect.Contains(screen))
                Selected.Add(mob);
        }
    }

    private bool CanSelect(EntityUid uid)
    {
        return uid != _player.LocalEntity && !_mobState.IsDead(uid);
    }

    private bool OrderAt(AdminSquadOrder order, EntityCoordinates coords, EntityUid clicked)
    {
        if (!Active)
            return false;

        if (Selected.Count > 0 && IsAttackable(clicked))
            SendOrder(AdminSquadOrder.Attack, coords, clicked);
        else if (Selected.Count > 0 && coords.IsValid(EntityManager))
            SendOrder(order, coords);

        // Never open the context menu while the tool is on.
        return true;
    }

    /// <summary>
    /// A click on another living mob or on a structure is an attack order; the server checks it can be destroyed.
    /// </summary>
    private bool IsAttackable(EntityUid uid)
    {
        if (!uid.IsValid() || !Exists(uid) || Selected.Contains(uid) || uid == _player.LocalEntity)
            return false;

        if (HasComp<MobStateComponent>(uid))
            return !_mobState.IsDead(uid);

        return Transform(uid).Anchored;
    }

    /// <summary>
    /// Sends an order for the current selection. Hold and release ignore <paramref name="target"/>,
    /// an attack order needs <paramref name="targetEntity"/>.
    /// </summary>
    public void SendOrder(AdminSquadOrder order, EntityCoordinates target, EntityUid? targetEntity = null)
    {
        PruneSelection();
        if (Selected.Count == 0)
            return;

        RaiseNetworkEvent(new AdminSquadOrderRequestEvent(
            Selected.Select(uid => GetNetEntity(uid)).ToList(),
            order,
            GetNetCoordinates(target),
            targetEntity != null ? GetNetEntity(targetEntity.Value) : null));
    }

    /// <summary>
    /// Makes every selected unit say, whisper or emote <paramref name="message"/>.
    /// </summary>
    public void Speak(string message, AdminSquadSpeech speech)
    {
        PruneSelection();
        if (Selected.Count == 0 || string.IsNullOrWhiteSpace(message))
            return;

        RaiseNetworkEvent(new AdminSquadSpeakRequestEvent(
            Selected.Select(uid => GetNetEntity(uid)).ToList(),
            message,
            speech));
    }

    private void OnOrderResult(AdminSquadOrderResultEvent msg)
    {
        if (!Active)
            return;

        var until = _timing.RealTime + MarkerDuration;
        for (var i = 0; i < msg.Accepted.Count; i++)
        {
            var uid = GetEntity(msg.Accepted[i]);
            if (msg.Order == AdminSquadOrder.Release)
            {
                Destinations.Remove(uid);
                continue;
            }

            var destination = GetCoordinates(msg.Destinations[i]);
            if (destination.IsValid(EntityManager))
                Destinations[uid] = (_transform.ToMapCoordinates(destination), msg.Order, until);
        }

        // Keep only the units the server accepted: the others are dead or played.
        if (msg.Order != AdminSquadOrder.Release)
        {
            var accepted = msg.Accepted.Select(net => GetEntity(net)).ToHashSet();
            Selected.IntersectWith(accepted);
        }

        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Drops deleted and dead units.
    /// </summary>
    public void PruneSelection()
    {
        if (Selected.RemoveWhere(uid => !Exists(uid) || _mobState.IsDead(uid)) > 0)
            SelectionChanged?.Invoke();
    }

    public override void FrameUpdate(float frameTime)
    {
        base.FrameUpdate(frameTime);

        if (!Active)
            return;

        PruneSelection();

        var now = _timing.RealTime;
        foreach (var (uid, (_, _, until)) in Destinations.ToArray())
        {
            if (until < now || !Exists(uid))
                Destinations.Remove(uid);
        }
    }
}
