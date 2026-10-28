// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Numerics;
using Content.Server.Administration;
using Content.Shared._Harvest.AdminSquad;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.Map;

namespace Content.Server._Harvest.AdminSquad;

/// <summary>
/// <c>squadorder move|attackmove &lt;x&gt; &lt;y&gt; &lt;uid&gt;...</c>, <c>squadorder attack &lt;target&gt; &lt;uid&gt;...</c>
/// or <c>squadorder hold|release &lt;uid&gt;...</c>.
/// Coordinates are world coordinates on the map of the first unit.
/// </summary>
[AdminCommand(AdminFlags.Fun)]
public sealed class AdminSquadCommand : LocalizedEntityCommands
{
    [Dependency] private readonly AdminSquadSystem _squad = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    public override string Command => "squadorder";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2 || !Enum.TryParse<AdminSquadOrder>(args[0], true, out var order))
        {
            shell.WriteError(Loc.GetString("cmd-squadorder-invalid-args"));
            return;
        }

        var needsTarget = order is AdminSquadOrder.Move or AdminSquadOrder.AttackMove;
        var first = needsTarget ? 3 : order == AdminSquadOrder.Attack ? 2 : 1;
        float x = 0, y = 0;
        if (needsTarget && (args.Length < 4
                            || !float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                            || !float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out y)))
        {
            shell.WriteError(Loc.GetString("cmd-squadorder-invalid-args"));
            return;
        }

        EntityUid? victim = null;
        if (order == AdminSquadOrder.Attack)
        {
            if (args.Length < 3 || !NetEntity.TryParse(args[1], out var victimNet) || !EntityManager.TryGetEntity(victimNet, out victim))
            {
                shell.WriteError(Loc.GetString("cmd-squadorder-invalid-args"));
                return;
            }
        }

        var units = new List<EntityUid>();
        for (var i = first; i < args.Length; i++)
        {
            if (!NetEntity.TryParse(args[i], out var net) || !EntityManager.TryGetEntity(net, out var uid))
            {
                shell.WriteError(Loc.GetString("shell-entity-uid-must-be-number"));
                return;
            }

            units.Add(uid.Value);
        }

        var target = EntityCoordinates.Invalid;
        if (needsTarget)
        {
            var mapId = EntityManager.GetComponent<TransformComponent>(units[0]).MapID;
            target = _transform.ToCoordinates(new MapCoordinates(new Vector2(x, y), mapId));
        }

        var results = _squad.Order(units, order, target, victim);
        shell.WriteLine(Loc.GetString("cmd-squadorder-result", ("count", results.Count), ("total", units.Count)));
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length == 1)
            return CompletionResult.FromOptions(Enum.GetNames<AdminSquadOrder>());

        return CompletionResult.FromHint(Loc.GetString("cmd-squadorder-hint"));
    }
}
