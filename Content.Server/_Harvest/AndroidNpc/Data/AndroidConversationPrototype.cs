// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Server._Harvest.AndroidNpc.Components;
using Robust.Shared.Prototypes;

namespace Content.Server._Harvest.AndroidNpc.Data;

/// <summary>
/// Per-job conversation data: scripted replies per intent and tone, the in-game effect of each intent,
/// and the supplies the android can lend.
/// </summary>
[Prototype]
public sealed partial class AndroidConversationPrototype : IPrototype
{
    /// <inheritdoc />
    [IdDataField] public string ID { get; private set; } = default!;

    /// <summary>
    /// Job these replies apply to.
    /// </summary>
    [DataField("job")] public AndroidJob Job { get; private set; } = AndroidJob.Janitor;

    /// <summary>
    /// Hidden context sent to the typed-decision backend with each message (role, duties, setting...).
    /// </summary>
    [DataField("backendState")] public Dictionary<string, string> BackendState { get; private set; } = new();

    /// <summary>
    /// Intent and tone questions asked to the typed-decision backend, per language.
    /// </summary>
    [DataField("backendQuestions")] public List<AndroidBackendQuestions> BackendQuestions { get; private set; } = new();

    /// <summary>
    /// In-game effect triggered by an intent. Intents not listed only produce a reply.
    /// </summary>
    [DataField("effects")] public Dictionary<string, AndroidConversationEffect> Effects { get; private set; } = new();

    /// <summary>
    /// Items the android can hand over on a supply request.
    /// </summary>
    [DataField("supplies")] public List<AndroidSupplyRule> Supplies { get; private set; } = new();

    /// <summary>
    /// Scripted replies; the set of intents defined here is the job's intent set.
    /// </summary>
    [DataField("responses")] public List<AndroidConversationResponse> Responses { get; private set; } = new();
}

/// <summary>
/// Scripted replies for one intent and language, one variant list per tone level.
/// The first variant of each list is the informative one, used for questions.
/// </summary>
[DataDefinition]
public sealed partial class AndroidConversationResponse
{
    [DataField("lang")] public string Lang { get; private set; } = "en";

    [DataField("intent")] public string Intent { get; private set; } = string.Empty;

    [DataField("calm")] public List<string> Calm { get; private set; } = new();

    [DataField("direct")] public List<string> Direct { get; private set; } = new();

    [DataField("threatening")] public List<string> Threatening { get; private set; } = new();

    /// <summary>
    /// Returns the variants for a tone level, clamped to the defined levels.
    /// </summary>
    public List<string> ForTone(int tone)
    {
        return tone switch
        {
            <= 0 => Calm,
            1 => Direct,
            _ => Threatening,
        };
    }
}

/// <summary>
/// Patterns naming a supply and the item prototypes that satisfy it.
/// </summary>
[DataDefinition]
public sealed partial class AndroidSupplyRule
{
    [DataField("patterns")] public List<string> Patterns { get; private set; } = new();

    [DataField("items")] public List<string> Items { get; private set; } = new();
}

/// <summary>
/// Typed questions for one language: an intent choice and a tone score.
/// </summary>
[DataDefinition]
public sealed partial class AndroidBackendQuestions
{
    [DataField("lang")] public string Lang { get; private set; } = "en";

    [DataField("intentInstructions")] public string IntentInstructions { get; private set; } = string.Empty;

    /// <summary>
    /// Intent key to its description.
    /// </summary>
    [DataField("intentCriteria")] public Dictionary<string, string> IntentCriteria { get; private set; } = new();

    [DataField("toneInstructions")] public string ToneInstructions { get; private set; } = string.Empty;

    /// <summary>
    /// Ordered tone levels, from calm to threatening.
    /// </summary>
    [DataField("toneCriteria")] public List<string> ToneCriteria { get; private set; } = new();
}
