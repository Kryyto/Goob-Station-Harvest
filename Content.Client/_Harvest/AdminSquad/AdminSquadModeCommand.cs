// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Console;

namespace Content.Client._Harvest.AdminSquad;

/// <summary>
/// Turns the admin squad tool on or off. Permission: FUN, in clientCommandPerms.yml.
/// </summary>
public sealed class AdminSquadModeCommand : LocalizedEntityCommands
{
    [Dependency] private readonly AdminSquadSystem _squad = default!;

    public override string Command => "squadmode";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        _squad.Toggle();
        shell.WriteLine(Loc.GetString(_squad.Active ? "cmd-squadmode-on" : "cmd-squadmode-off"));
    }
}
