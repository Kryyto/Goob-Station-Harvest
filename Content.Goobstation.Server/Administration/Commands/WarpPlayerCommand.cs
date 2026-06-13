// SPDX-FileCopyrightText: 2026 Harvest Station
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Numerics;
using Content.Server.Administration;
using Content.Shared.Administration;
using Content.Shared.Warps;
using Robust.Server.Player;
using Robust.Shared.Console;
using Robust.Shared.Enums;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.Goobstation.Server.Administration.Commands;

[AdminCommand(AdminFlags.Admin)]
public sealed class WarpPlayerCommand : LocalizedCommands
{
    [Dependency] private readonly IEntityManager _entities = null!;
    [Dependency] private readonly IPlayerManager _players = null!;

    public override string Command => "warpplayer";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2)
        {
            shell.WriteError("Usage: warpplayer <username> <warppoint>");
            return;
        }

        if (!_players.TryGetSessionByUsername(args[0], out var session))
        {
            shell.WriteError($"Could not find player '{args[0]}'.");
            return;
        }

        if (session.Status != SessionStatus.InGame || session.AttachedEntity is not { Valid: true } playerEntity)
        {
            shell.WriteError("Target player is not in-game.");
            return;
        }

        var xformSystem = _entities.System<SharedTransformSystem>();
        var location = args[1];

        var currentMap = _entities.GetComponent<TransformComponent>(playerEntity).MapID;
        var currentGrid = _entities.GetComponent<TransformComponent>(playerEntity).GridUid;

        var points = new List<(EntityCoordinates Coords, bool Follow)>();
        var query = _entities.AllEntityQueryEnumerator<WarpPointComponent, MetaDataComponent, TransformComponent>();
        while (query.MoveNext(out _, out var warp, out var meta, out var xform))
        {
            if ((warp.Location ?? meta.EntityName) == location)
                points.Add((xform.Coordinates, warp.Follow));
        }

        if (points.Count == 0)
        {
            shell.WriteError($"Warp point '{location}' not found.");
            return;
        }

        var best = points
            .OrderBy(p =>
            {
                var grid = xformSystem.GetGrid(p.Coords);
                if (grid == currentGrid)
                    return 0;

                var map = xformSystem.GetMapId(p.Coords);
                return map == currentMap ? 1 : 2;
            })
            .First();

        var mapCoords = xformSystem.ToMapCoordinates(best.Coords);
        xformSystem.SetMapCoordinates(playerEntity, mapCoords);
        _entities.System<SharedTransformSystem>().AttachToGridOrMap(playerEntity);

        if (_entities.TryGetComponent(playerEntity, out PhysicsComponent? physics))
            _entities.System<SharedPhysicsSystem>().SetLinearVelocity(playerEntity, Vector2.Zero, body: physics);

        shell.WriteLine($"Teleported {args[0]} to warp point '{location}'.");
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
        {
            var names = _players.Sessions.OrderBy(s => s.Name).Select(s => s.Name);
            return CompletionResult.FromHintOptions(names, "<username>");
        }

        if (args.Length != 2)
            return CompletionResult.Empty;

        var warpNames = new List<string>();
        var query = _entities.AllEntityQueryEnumerator<WarpPointComponent, MetaDataComponent>();
        while (query.MoveNext(out _, out var warp, out var meta))
        {
            warpNames.Add(warp.Location ?? meta.EntityName);
        }

        warpNames.Sort();
        return CompletionResult.FromHintOptions(warpNames, "<warppoint>");

    }
}
