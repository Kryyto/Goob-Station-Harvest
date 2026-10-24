// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Prototypes;

namespace Content.Server.AndroidNpc.Data;

/// <summary>
/// Phrase variations that trigger each type of android order.
/// </summary>
[Prototype]
public sealed partial class AndroidOrderPhrasesPrototype : IPrototype
{
    /// <inheritdoc />
    [IdDataField] public string ID { get; private set; } = default!;

    /// <summary>
    /// Phrases that make the android follow the issuer.
    /// </summary>
    [DataField("followMe")] public List<string> FollowMe { get; private set; } = new();

    /// <summary>
    /// Phrases that make the android return to work.
    /// </summary>
    [DataField("returnToWork")] public List<string> ReturnToWork { get; private set; } = new();

    /// <summary>
    /// Phrases that make the android go to a department or area.
    /// </summary>
    [DataField("goToDepartment")] public List<string> GoToDepartment { get; private set; } = new();

    /// <summary>
    /// Phrases that make the android go to a specific person.
    /// </summary>
    [DataField("goToPerson")] public List<string> GoToPerson { get; private set; } = new();

    /// <summary>
    /// Phrases that make the android take a break.
    /// </summary>
    [DataField("takeBreak")] public List<string> TakeBreak { get; private set; } = new();

    /// <summary>
    /// Phrases that ask the android to report its current status.
    /// </summary>
    [DataField("statusReport")] public List<string> StatusReport { get; private set; } = new();

    /// <summary>
    /// Phrases that ask the android to restate its current bar/kitchen order.
    /// </summary>
    [DataField("repeatOrder")] public List<string> RepeatOrder { get; private set; } = new();

    /// <summary>
    /// Phrases that ask the android what it can do.
    /// </summary>
    [DataField("capabilities")] public List<string> Capabilities { get; private set; } = new();

    /// <summary>
    /// Phrases that ask a cook or bartender what it can serve.
    /// </summary>
    [DataField("menu")] public List<string> Menu { get; private set; } = new();

    /// <summary>
    /// Phrases asking a paramedic to treat the speaker.
    /// </summary>
    [DataField("healMe")] public List<string> HealMe { get; private set; } = new();

    /// <summary>
    /// Phrases reporting a casualty; a trailing space lets a place follow ("il y a un blessé au bar").
    /// </summary>
    [DataField("injured")] public List<string> Injured { get; private set; } = new();
}
