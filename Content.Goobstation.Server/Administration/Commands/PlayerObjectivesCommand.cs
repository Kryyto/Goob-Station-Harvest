// SPDX-FileCopyrightText: 2026 Harvest Station
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Server.Administration;
using Content.Server.EUI;
using Content.Shared.Administration;
using Robust.Server.Player;
using Robust.Shared.Console;

namespace Content.Goobstation.Server.Administration.Commands;

[AdminCommand(AdminFlags.Admin)]
public sealed class PlayerObjectivesCommand : LocalizedCommands
{
    [Dependency] private readonly IPlayerManager _players = null!;
    [Dependency] private readonly EuiManager _euis = null!;

    public override string Command => "playerobjectives";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player is not { } admin)
        {
            shell.WriteError("This command can only be run by a player.");
            return;
        }

        if (args.Length != 1)
        {
            shell.WriteError("Usage: playerobjectives <username>");
            return;
        }

        if (!_players.TryGetSessionByUsername(args[0], out var target))
        {
            shell.WriteError($"Could not find player '{args[0]}'.");
            return;
        }

        var eui = new PlayerObjectivesEui(target);
        _euis.OpenEui(eui, admin);
        eui.StateDirty();
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length != 1)
            return CompletionResult.Empty;

        var options = _players.Sessions.OrderBy(c => c.Name).Select(c => c.Name).ToArray();
        return CompletionResult.FromHintOptions(options, "<username>");

    }
}
