// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Prototypes;

namespace Content.Server.AndroidNpc.Data;

/// <summary>
/// Deterministic, job-independent rules used to classify what a crew member says to an android:
/// language hints, exact phrases, ordered keyword patterns, tone patterns and spoken location aliases.
/// Ported from the Android-generic-responses project.
/// </summary>
[Prototype]
public sealed partial class AndroidIntentRulesPrototype : IPrototype
{
    /// <inheritdoc />
    [IdDataField] public string ID { get; private set; } = default!;

    /// <summary>
    /// Language used when no hint matches more than another.
    /// </summary>
    [DataField("defaultLanguage")] public string DefaultLanguage { get; private set; } = "en";

    /// <summary>
    /// Intent used when no rule matches and no backend answer is available.
    /// </summary>
    [DataField("fallbackIntent")] public string FallbackIntent { get; private set; } = "unrelated";

    /// <summary>
    /// Supported languages with detection hints and localized alert lines.
    /// </summary>
    [DataField("languages")] public List<AndroidConversationLanguage> Languages { get; private set; } = new();

    /// <summary>
    /// Whole-message phrases that force an intent.
    /// </summary>
    [DataField("phrases")] public List<AndroidIntentPhraseRule> Phrases { get; private set; } = new();

    /// <summary>
    /// Ordered keyword patterns; the first matching intent available to the job wins.
    /// </summary>
    [DataField("keywords")] public List<AndroidIntentKeywordRule> Keywords { get; private set; } = new();

    /// <summary>
    /// Tone patterns, checked from the highest level down.
    /// </summary>
    [DataField("tone")] public List<AndroidToneRule> Tone { get; private set; } = new();

    /// <summary>
    /// Spoken location names mapped to nav beacon queries.
    /// </summary>
    [DataField("locations")] public List<AndroidLocationAlias> Locations { get; private set; } = new();
}

/// <summary>
/// A supported conversation language.
/// </summary>
[DataDefinition]
public sealed partial class AndroidConversationLanguage
{
    /// <summary>
    /// Language code, e.g. "en" or "fr".
    /// </summary>
    [DataField("id")] public string Id { get; private set; } = "en";

    /// <summary>
    /// Patterns matched against the lowercased message; each match counts towards this language.
    /// </summary>
    [DataField("hints")] public List<string> Hints { get; private set; } = new();

    /// <summary>
    /// Radio line sent to security when the android is threatened. {speaker} and {location} are replaced.
    /// </summary>
    [DataField("threat")] public string Threat { get; private set; } = string.Empty;

    /// <summary>
    /// Radio line sent when an emergency is reported to the android. {speaker} and {location} are replaced.
    /// </summary>
    [DataField("emergency")] public string Emergency { get; private set; } = string.Empty;

    /// <summary>
    /// Location text used when no beacon is close enough.
    /// </summary>
    [DataField("noLocation")] public string NoLocation { get; private set; } = string.Empty;

    /// <summary>
    /// Reply used when a requested supply is not carried.
    /// </summary>
    [DataField("noSupply")] public string NoSupply { get; private set; } = string.Empty;
}

/// <summary>
/// Exact whole-message phrases for one intent and language.
/// </summary>
[DataDefinition]
public sealed partial class AndroidIntentPhraseRule
{
    [DataField("lang")] public string Lang { get; private set; } = "en";

    [DataField("intent")] public string Intent { get; private set; } = string.Empty;

    [DataField("phrases")] public List<string> Phrases { get; private set; } = new();
}

/// <summary>
/// Regex patterns for one intent and language, matched against accent-folded lowercase text.
/// </summary>
[DataDefinition]
public sealed partial class AndroidIntentKeywordRule
{
    [DataField("lang")] public string Lang { get; private set; } = "en";

    [DataField("intent")] public string Intent { get; private set; } = string.Empty;

    [DataField("patterns")] public List<string> Patterns { get; private set; } = new();
}

/// <summary>
/// Regex patterns that raise the tone level of a message (0 calm, 1 impatient, 2 threatening).
/// </summary>
[DataDefinition]
public sealed partial class AndroidToneRule
{
    [DataField("lang")] public string Lang { get; private set; } = "en";

    [DataField("level")] public int Level { get; private set; } = 1;

    [DataField("patterns")] public List<string> Patterns { get; private set; } = new();
}

/// <summary>
/// Spoken names for a location, resolved through <see cref="Utilities.NavMapBeaconLocator"/>.
/// </summary>
[DataDefinition]
public sealed partial class AndroidLocationAlias
{
    [DataField("target")] public string Target { get; private set; } = string.Empty;

    [DataField("aliases")] public List<string> Aliases { get; private set; } = new();
}
