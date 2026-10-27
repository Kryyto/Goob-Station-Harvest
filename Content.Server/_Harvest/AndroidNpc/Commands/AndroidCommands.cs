// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Server.Administration;
using Content.Server._Harvest.AndroidNpc.Components;
using Content.Server._Harvest.AndroidNpc.Systems;
using Content.Server._Harvest.AndroidNpc.Utilities;
using Content.Server.NPC.Components;
using Content.Shared.Administration;
using Robust.Shared.Console;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Server._Harvest.AndroidNpc.Commands;

/// <summary>
/// Shared target resolution: an android is designated by entity id or first name.
/// </summary>
public abstract class AndroidCommandBase : LocalizedEntityCommands
{
    protected bool TryResolveAndroid(IConsoleShell shell, string arg, out Entity<AndroidComponent> android)
    {
        android = default;

        var query = EntityManager.AllEntityQueryEnumerator<AndroidComponent, AndroidIdentityComponent>();
        while (query.MoveNext(out var uid, out var comp, out var identity))
        {
            if (uid.ToString() == arg || NameMatcher.EqualsIgnoreCaseAndAccents(identity.FirstName, arg))
            {
                android = (uid, comp);
                return true;
            }
        }

        shell.WriteError($"No android '{arg}'. Use an entity id or a first name.");
        return false;
    }

    protected string Describe(EntityUid uid, AndroidComponent android)
    {
        var order = EntityManager.TryGetComponent<AndroidOrderComponent>(uid, out var o) ? $", order {o.OrderType}" : string.Empty;
        return $"{uid} {EntityManager.GetComponent<MetaDataComponent>(uid).EntityName} [{android.Job}] {android.Status}{order}";
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        if (args.Length != 1)
            return CompletionResult.Empty;

        var names = new List<string>();
        var query = EntityManager.AllEntityQueryEnumerator<AndroidIdentityComponent>();
        while (query.MoveNext(out _, out var identity))
            names.Add(identity.FirstName);

        return CompletionResult.FromHintOptions(names, "android");
    }
}

[AdminCommand(AdminFlags.Debug)]
public sealed class AndroidStatusCommand : AndroidCommandBase
{
    public override string Command => "androidstatus";
    public override string Description => "Lists all androids, or prints the details of one.";
    public override string Help => "Usage: androidstatus [entityId|firstName]";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length == 0)
        {
            var query = EntityManager.AllEntityQueryEnumerator<AndroidComponent>();
            var count = 0;
            while (query.MoveNext(out var uid, out var android))
            {
                shell.WriteLine(Describe(uid, android));
                count++;
            }

            shell.WriteLine($"{count} android(s).");
            return;
        }

        if (!TryResolveAndroid(shell, args[0], out var target))
            return;

        var (ent, comp) = target;
        shell.WriteLine(Describe(ent, comp));

        foreach (var line in EntityManager.System<AndroidOrderSystem>().GetStatusReport(ent))
            shell.WriteLine($"  Says: {line}");

        foreach (var line in comp.RecentLines)
            shell.WriteLine($"  Said: {line}");

        if (EntityManager.TryGetComponent<AndroidOrderComponent>(ent, out var order))
            shell.WriteLine($"  Order: {order.OrderType} from {order.IssuerName}, target {order.Target ?? "-"}, expires {order.ExpiresAt}");

        if (EntityManager.TryGetComponent<AndroidInventoryComponent>(ent, out var inventory))
        {
            shell.WriteLine($"  Equipped: {Name(inventory.EquippedItem)}, tool target: {Name(inventory.CurrentToolTarget)}");
            shell.WriteLine($"  Service target: {Name(inventory.ServiceTarget)} [{inventory.ServiceKind}], pest: {Name(inventory.PestTarget)}");
            shell.WriteLine($"  Owned: {string.Join(", ", inventory.OwnedItems.Where(EntityManager.EntityExists).Select(e => Name(e)))}");
        }

        if (EntityManager.TryGetComponent<AndroidServiceComponent>(ent, out var service))
            shell.WriteLine($"  Serving: {service.Current?.ItemName ?? "-"} for {service.Current?.CustomerName ?? "-"}, {service.Pending.Count} pending");

        if (EntityManager.TryGetComponent<AndroidMemoryComponent>(ent, out var memory))
            shell.WriteLine($"  Known dirty targets: {memory.KnownDirtyTargets.Count}, tiles: {memory.KnownDirtyTiles.Count}");

        if (EntityManager.TryGetComponent<NPCSteeringComponent>(ent, out var steering))
            shell.WriteLine($"  Steering: {steering.Status} to {steering.Coordinates} (range {steering.Range:F2})");
    }

    private string Name(EntityUid? uid)
    {
        return uid is { } e && EntityManager.TryGetComponent<MetaDataComponent>(e, out var meta) ? $"{meta.EntityName} ({e})" : "-";
    }
}

[AdminCommand(AdminFlags.Debug)]
public sealed class AndroidTreeCommand : AndroidCommandBase
{
    public override string Command => "androidtree";
    public override string Description => "Traces an android's behavior tree: which branches ran and with what result on the last tick.";
    public override string Help => "Usage: androidtree <entityId|firstName> [off]";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length == 0)
        {
            shell.WriteError(Help);
            return;
        }

        if (!TryResolveAndroid(shell, args[0], out var android))
            return;

        if (args.Length > 1 && args[1] == "off")
        {
            android.Comp.Trace = null;
            shell.WriteLine("Tracing disabled.");
            return;
        }

        if (android.Comp.Trace == null)
        {
            android.Comp.Trace = new List<string>();
            shell.WriteLine("Tracing enabled; run the command again to see the last tick.");
            return;
        }

        foreach (var line in android.Comp.Trace)
            shell.WriteLine(line);
    }
}

[AdminCommand(AdminFlags.Admin)]
public sealed class AndroidForceOrderCommand : AndroidCommandBase
{
    public override string Command => "androidforceorder";
    public override string Description => "Gives an order to an android, bypassing authority checks. FollowMe and CleanArea use your position.";
    public override string Help => $"Usage: androidforceorder <entityId|firstName> <{string.Join("|", Orders)}> [target]";

    private static readonly AndroidOrderType[] Orders =
    {
        AndroidOrderType.FollowMe, AndroidOrderType.ReturnToWork, AndroidOrderType.GoToDepartment,
        AndroidOrderType.GoToPerson, AndroidOrderType.TakeBreak, AndroidOrderType.CleanArea,
    };

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length < 2 || !Enum.TryParse<AndroidOrderType>(args[1], true, out var type) || !Orders.Contains(type))
        {
            shell.WriteError(Help);
            return;
        }

        if (!TryResolveAndroid(shell, args[0], out var android))
            return;

        var player = shell.Player?.AttachedEntity;
        if (type is AndroidOrderType.FollowMe or AndroidOrderType.CleanArea && player == null)
        {
            shell.WriteError($"{type} needs a player position.");
            return;
        }

        var target = args.Length > 2 ? string.Join(' ', args.Skip(2)) : null;
        if (type is AndroidOrderType.GoToDepartment or AndroidOrderType.GoToPerson && target == null)
        {
            shell.WriteError($"{type} needs a target.");
            return;
        }

        var issuer = player ?? android.Owner;
        var issuerName = player == null ? IoCManager.Resolve<IPrototypeManager>().AndroidFeedback().ForcedOrderIssuer : null;
        var coords = type == AndroidOrderType.CleanArea ? EntityManager.GetComponent<TransformComponent>(issuer).Coordinates : (Robust.Shared.Map.EntityCoordinates?) null;

        EntityManager.System<AndroidOrderSystem>().IssueOrder(android, issuer, type, target, coords, issuerName);
        shell.WriteLine($"Ordered {Describe(android, android.Comp)}.");
    }
}

[AdminCommand(AdminFlags.Admin)]
public sealed class SpawnAndroidCommand : AndroidCommandBase
{
    public override string Command => "spawnandroid";
    public override string Description => "Spawns an android at your position.";
    public override string Help => "Usage: spawnandroid [Janitor|Cook|Bartender] [firstName]";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (shell.Player?.AttachedEntity is not { } player)
        {
            shell.WriteError("You must be a player to use this command.");
            return;
        }

        var jobName = args.Length > 0 ? args[0] : nameof(AndroidJob.Janitor);
        if (!Enum.TryParse<AndroidJob>(jobName, true, out var job) ||
            IoCManager.Resolve<IPrototypeManager>().AndroidJobData(job) is not { } jobData)
        {
            shell.WriteError($"Unknown android job '{jobName}'.");
            return;
        }

        var spawned = EntityManager.SpawnEntity(jobData.EntityPrototype, EntityManager.GetComponent<TransformComponent>(player).Coordinates);

        if (args.Length > 1 && EntityManager.TryGetComponent<AndroidIdentityComponent>(spawned, out var identity))
        {
            identity.FirstName = args[1];
            identity.FullDisplayName = $"{IoCManager.Resolve<IPrototypeManager>().AndroidConfig().NamePrefix} {args[1]}";
            EntityManager.System<MetaDataSystem>().SetEntityName(spawned, identity.FullDisplayName);
        }

        shell.WriteLine($"Spawned {spawned} as {job}.");
    }

    public override CompletionResult GetCompletion(IConsoleShell shell, string[] args)
    {
        return args.Length == 1
            ? CompletionResult.FromHintOptions(Enum.GetNames<AndroidJob>(), "job")
            : CompletionResult.Empty;
    }
}

[AdminCommand(AdminFlags.Admin)]
public sealed class DespawnAndroidCommand : AndroidCommandBase
{
    public override string Command => "despawnandroid";
    public override string Description => "Deletes an android, or with 'leave' sends it to cryostorage first.";
    public override string Help => "Usage: despawnandroid <entityId|firstName> [leave]";

    public override void Execute(IConsoleShell shell, string argStr, string[] args)
    {
        if (args.Length == 0)
        {
            shell.WriteError(Help);
            return;
        }

        if (!TryResolveAndroid(shell, args[0], out var android))
            return;

        if (args.Length > 1 && args[1] == "leave")
        {
            EntityManager.System<AndroidLifecycleSystem>().StartLeaving(android);
            shell.WriteLine($"{android.Owner} is leaving for cryostorage.");
            return;
        }

        EntityManager.DeleteEntity(android);
        shell.WriteLine($"Despawned {android.Owner}.");
    }
}
