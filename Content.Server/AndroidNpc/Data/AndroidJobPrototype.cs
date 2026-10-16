// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Server.AndroidNpc.Components;
using Content.Shared.Radio;
using Content.Shared.Roles;
using Robust.Shared.Prototypes;

namespace Content.Server.AndroidNpc.Data;

/// <summary>
/// Everything specific to one android job: spawning, equipment, patrol, voice lines and, for service jobs,
/// the menu it can prepare.
/// </summary>
[Prototype]
public sealed partial class AndroidJobPrototype : IPrototype
{
    /// <inheritdoc />
    [IdDataField] public string ID { get; private set; } = default!;

    [DataField("job")] public AndroidJob Job { get; private set; }

    /// <summary>
    /// Job title shown on the android's ID.
    /// </summary>
    [DataField("jobTitle")] public string JobTitle { get; private set; } = string.Empty;

    /// <summary>
    /// Mob prototype spawned for this job.
    /// </summary>
    [DataField("entityPrototype")] public EntProtoId EntityPrototype { get; private set; } = "MobAndroid";

    /// <summary>
    /// Station job whose free slot the android fills during low-population rounds. A player taking this job
    /// sends the android to cryostorage.
    /// </summary>
    [DataField("stationJob")] public ProtoId<JobPrototype>? StationJob { get; private set; }

    /// <summary>
    /// Whether the lifecycle system spawns this job automatically.
    /// </summary>
    [DataField("autoSpawn")] public bool AutoSpawn { get; private set; } = true;

    /// <summary>
    /// Maximum number of androids of this job per station.
    /// </summary>
    [DataField("maxPerStation")] public int MaxPerStation { get; private set; } = 1;

    /// <summary>
    /// Radio channel used to report to a superior who is out of earshot.
    /// </summary>
    [DataField("radioChannel")] public ProtoId<RadioChannelPrototype> RadioChannel { get; private set; } = "Service";

    /// <summary>
    /// Nav beacon the android works around (service jobs) or resupplies at.
    /// </summary>
    [DataField("workBeacon")] public string? WorkBeacon { get; private set; }

    [DataField("patrolRoute")] public List<string> PatrolRoute { get; private set; } = new();

    [DataField("resupplyBeacon")] public string? ResupplyBeacon { get; private set; }

    [DataField("requiredLoadout")] public List<string> RequiredLoadout { get; private set; } = new();

    [DataField("messTags")] public List<string> MessTags { get; private set; } = new();

    [DataField("trashTags")] public List<string> TrashTags { get; private set; } = new();

    /// <summary>
    /// Access tags that allow a crew member to give orders to this android.
    /// </summary>
    [DataField("orderAccessTags")] public List<string> OrderAccessTags { get; private set; } = new();

    /// <summary>
    /// Words that make a message count as addressed to this android, besides its name.
    /// </summary>
    [DataField("addressKeywords")] public List<string> AddressKeywords { get; private set; } = new();

    /// <summary>
    /// Items a service android can prepare on request, matched against what the customer says.
    /// </summary>
    [DataField("menu")] public List<AndroidMenuItem> Menu { get; private set; } = new();

    /// <summary>
    /// What a service android prepares when it does not recognize the order: one is picked at random.
    /// </summary>
    [DataField("fallbackMenu")] public List<EntProtoId> FallbackMenu { get; private set; } = new();

    /// <summary>
    /// Where the android takes its breaks; null lets it pick the bar or the kitchen. A bartender should not
    /// rest at its own bar, nor a cook in its own kitchen.
    /// </summary>
    [DataField("breakVenue")] public AndroidBreakVenue? BreakVenue { get; private set; }

    /// <summary>
    /// Name of a menu or fallback item in the given language, or the entity name.
    /// </summary>
    public string? MenuName(EntProtoId item, string language)
    {
        return Menu.FirstOrDefault(m => m.Item == item)?.Names.GetValueOrDefault(language);
    }

    /// <summary>
    /// Voice lines per language code.
    /// </summary>
    [DataField("lines")] public Dictionary<string, AndroidJobLines> Lines { get; private set; } = new();

    /// <summary>
    /// Lines in the given language, or English, or any language defined.
    /// </summary>
    public AndroidJobLines? LinesFor(string language)
    {
        return Lines.GetValueOrDefault(language) ?? Lines.GetValueOrDefault("en") ?? Lines.Values.FirstOrDefault();
    }
}

/// <summary>
/// One preparable menu entry.
/// </summary>
[DataDefinition]
public sealed partial class AndroidMenuItem
{
    [DataField("item", required: true)] public EntProtoId Item { get; private set; }

    /// <summary>
    /// Regex patterns matched against the accent-folded request; the entity name always matches too.
    /// </summary>
    [DataField("patterns")] public List<string> Patterns { get; private set; } = new();

    [DataField("prepSeconds")] public float PrepSeconds { get; private set; } = 6f;

    /// <summary>
    /// Spoken name per language, for items whose entity name is not what a customer would say
    /// (metamorphic glasses are all named "metamorphic glass" until filled).
    /// </summary>
    [DataField("names")] public Dictionary<string, string> Names { get; private set; } = new();
}

/// <summary>
/// Voice lines of a job. Lines with {0}, {item} or {customer} placeholders are formatted by the caller.
/// </summary>
[DataDefinition]
public sealed partial class AndroidJobLines
{
    [DataField("mess")] public List<string> Mess { get; private set; } = new();

    [DataField("break")] public List<string> Break { get; private set; } = new();

    [DataField("backToWork")] public List<string> BackToWork { get; private set; } = new();

    [DataField("flee")] public List<string> Flee { get; private set; } = new();

    [DataField("idle")] public List<string> Idle { get; private set; } = new();

    [DataField("attack")] public List<string> Attack { get; private set; } = new();

    /// <summary>
    /// Said when a player takes the android's job and it leaves for cryostorage.
    /// </summary>
    [DataField("despawn")] public List<string> Despawn { get; private set; } = new();

    /// <summary>
    /// Said when a service android starts preparing an order. {item} and {customer} are replaced.
    /// </summary>
    [DataField("preparing")] public List<string> Preparing { get; private set; } = new();

    /// <summary>
    /// Said when a service android hands over an order. {item} and {customer} are replaced.
    /// </summary>
    [DataField("served")] public List<string> Served { get; private set; } = new();

    /// <summary>
    /// Said when the order is not on the menu and something else is prepared instead. {item} is replaced.
    /// </summary>
    [DataField("unknownOrder")] public List<string> UnknownOrder { get; private set; } = new();
}
