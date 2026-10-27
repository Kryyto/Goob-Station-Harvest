// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Content.Shared._Harvest.AdminSquad;
using Robust.Client.Graphics;
using Robust.Client.Input;
using Robust.Shared.Enums;

namespace Content.Client._Harvest.AdminSquad;

/// <summary>
/// Draws the selection box, a ring under each selected unit and where the last order sent each unit.
/// </summary>
public sealed class AdminSquadOverlay : Overlay
{
    private static readonly Color SelectionColor = Color.LimeGreen;
    private static readonly Color BoxColor = Color.LimeGreen.WithAlpha(0.15f);

    private readonly AdminSquadSystem _squad;
    private readonly SharedTransformSystem _transform;
    private readonly IEyeManager _eye;
    private readonly IInputManager _input;

    public override OverlaySpace Space => OverlaySpace.WorldSpace | OverlaySpace.ScreenSpace;

    public AdminSquadOverlay(AdminSquadSystem squad, SharedTransformSystem transform, IEyeManager eye, IInputManager input)
    {
        _squad = squad;
        _transform = transform;
        _eye = eye;
        _input = input;
        ZIndex = 1000;
    }

    protected override void Draw(in OverlayDrawArgs args)
    {
        if (args.Space == OverlaySpace.ScreenSpace)
            DrawScreen(args.ScreenHandle);
        else
            DrawWorld(args);
    }

    private void DrawScreen(DrawingHandleScreen handle)
    {
        if (_squad.DragStart is not { } start)
            return;

        var end = _input.MouseScreenPosition.Position;
        var box = new UIBox2(Vector2.Min(start, end), Vector2.Max(start, end));
        handle.DrawRect(box, BoxColor);
        handle.DrawRect(box, SelectionColor, false);
    }

    private void DrawWorld(in OverlayDrawArgs args)
    {
        var handle = args.WorldHandle;

        foreach (var (uid, (destination, order, _)) in _squad.Destinations)
        {
            if (destination.MapId != args.MapId)
                continue;

            var color = OrderColor(order);
            if (_squad.Selected.Contains(uid))
            {
                var from = _transform.GetMapCoordinates(uid);
                if (from.MapId == args.MapId)
                    handle.DrawLine(from.Position, destination.Position, color.WithAlpha(0.35f));
            }

            handle.DrawCircle(destination.Position, 0.15f, color);
        }

        foreach (var uid in _squad.Selected)
        {
            var coords = _transform.GetMapCoordinates(uid);
            if (coords.MapId != args.MapId)
                continue;

            handle.DrawCircle(coords.Position, 0.5f, SelectionColor, false);
        }
    }

    private static Color OrderColor(AdminSquadOrder order)
    {
        return order switch
        {
            AdminSquadOrder.AttackMove => Color.Red,
            AdminSquadOrder.Attack => Color.OrangeRed,
            AdminSquadOrder.Hold => Color.Yellow,
            _ => Color.LimeGreen,
        };
    }
}
