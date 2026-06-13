// SPDX-FileCopyrightText: 2026 Harvest Station
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Server.Administration;
using Content.Server.Mind;
using Content.Shared.Administration;
using Robust.Server.Player;
using Robust.Shared.Console;

namespace Content.Goobstation.Server.Administration.Commands;

[AdminCommand(AdminFlags.Fun)]
public sealed class ControlMobAsCommand : IConsoleCommand
{
    [Dependency] private readonly IEntityManager _entities = null!;
    [Dependency] private readonly IPlayerManager _players = null!;

    public string Command => "controlmobas";
    public string Description => "Force a player to control a target entity.";
    public string Help => "controlmobas <controller_username> <target_netentity_id>";

    public void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length != 2)
        {
            shell.WriteLine(Loc.GetString("shell-wrong-arguments-number"));
            return;
        }

        var username = args[0];
        if (!_players.TryGetSessionByUsername(username, out var session))
        {
            shell.WriteError($"No player found with username '{username}'.");
            return;
        }

        if (!int.TryParse(args[1], out var targetId))
        {
            shell.WriteLine(Loc.GetString("shell-argument-must-be-number"));
            return;
        }

        var targetNet = new NetEntity(targetId);
        if (!_entities.TryGetEntity(targetNet, out var target))
        {
            shell.WriteLine(Loc.GetString("shell-invalid-entity-id"));
            return;
        }

        _entities.System<MindSystem>().ControlMob(session.UserId, target.Value);
    }

    public CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length switch
        {
            1 => CompletionResult.FromOptions(_players.Sessions.Select(s => s.Name)),
            2 => CompletionResult.FromOptions(CompletionHelper.NetEntities(args[1], entManager: _entities)),
            _ => CompletionResult.Empty,
        };
    }
}
