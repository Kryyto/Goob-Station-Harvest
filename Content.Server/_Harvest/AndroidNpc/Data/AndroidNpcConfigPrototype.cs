// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Radio;
using Robust.Shared.Prototypes;

namespace Content.Server._Harvest.AndroidNpc.Data;

/// <summary>
/// Global configuration prototype for the android NPC system.
/// </summary>
[Prototype("androidNpc")]
public sealed partial class AndroidNpcConfigPrototype : IPrototype
{
    /// <inheritdoc />
    [IdDataField] public string ID { get; private set; } = default!;

    /// <summary>
    /// Whether the android NPC system is enabled.
    /// </summary>
    [DataField("enabled")] public bool Enabled { get; private set; } = true;

    /// <summary>
    /// Language of the androids' ambient lines (chatter, announcements), and of replies when the speaker's
    /// language cannot be told. Replies otherwise follow the language the crew member spoke.
    /// </summary>
    [DataField("language")] public string Language { get; private set; } = "en";

    /// <summary>
    /// Drinks an android may order at the bar on its break.
    /// </summary>
    [DataField("customerDrinks")] public List<EntProtoId> CustomerDrinks { get; private set; } = new();

    /// <summary>
    /// Food an android may order at the kitchen on its break.
    /// </summary>
    [DataField("customerFood")] public List<EntProtoId> CustomerFood { get; private set; } = new();

    /// <summary>
    /// Maximum number of androids that may be active at once.
    /// </summary>
    [DataField("maxAndroids")] public int MaxAndroids { get; private set; } = 5;

    /// <summary>
    /// Prefix used when building the android's full display name.
    /// </summary>
    [DataField("namePrefix")] public string NamePrefix { get; private set; } = "Androïde";

    /// <summary>
    /// Default first name used when no name pool is available.
    /// </summary>
    [DataField("defaultFirstName")] public string DefaultFirstName { get; private set; } = "Unité";

    /// <summary>
    /// Name used when the speaker's entity name cannot be resolved.
    /// </summary>
    [DataField("unknownSpeakerName")] public string UnknownSpeakerName { get; private set; } = "someone";

    /// <summary>
    /// Delay after round start before androids may spawn, in seconds.
    /// </summary>
    [DataField("spawnDelaySeconds")] public float SpawnDelaySeconds { get; private set; } = 30f;

    /// <summary>
    /// Radius for dirty tile scans, in tiles.
    /// </summary>
    [DataField("scanRadius")] public int ScanRadius { get; private set; } = 8;

    /// <summary>
    /// Ticks between tile scans.
    /// </summary>
    /// <summary>
    /// Seconds between two behavior tree evaluations of one android. Movement is steered every tick by the
    /// engine; decisions do not need to be.
    /// </summary>
    [DataField("treeTickIntervalSeconds")] public float TreeTickIntervalSeconds { get; private set; } = 0.1f;

    [DataField("scanIntervalTicks")] public int ScanIntervalTicks { get; private set; } = 60;

    /// <summary>
    /// How long a failed target stays blacklisted, in seconds.
    /// </summary>
    [DataField("blacklistTtlSeconds")] public float BlacklistTtlSeconds { get; private set; } = 30f;

    /// <summary>
    /// Number of consecutive failures before an entity is blacklisted.
    /// </summary>
    [DataField("blacklistThreshold")] public int BlacklistThreshold { get; private set; } = 3;

    /// <summary>
    /// Order lifetime, in seconds.
    /// </summary>
    [DataField("orderExpirySeconds")] public float OrderExpirySeconds { get; private set; } = 300f;

    /// <summary>
    /// Distance at which a follow-me order considers the issuer lost.
    /// </summary>
    [DataField("followLostDistance")] public int FollowLostDistance { get; private set; } = 15;

    /// <summary>
    /// Time the issuer must stay far away before the follow order fails.
    /// </summary>
    [DataField("followLostTimeoutSeconds")] public float FollowLostTimeoutSeconds { get; private set; } = 30f;

    /// <summary>
    /// Time between micro-breaks, in seconds.
    /// </summary>
    [DataField("breakIntervalSeconds")] public float BreakIntervalSeconds { get; private set; } = 120f;

    /// <summary>
    /// Duration of a micro-break, in seconds.
    /// </summary>
    [DataField("breakDurationSeconds")] public float BreakDurationSeconds { get; private set; } = 60f;

    /// <summary>
    /// Fraction of the required loadout that may be missing before the android goes to resupply.
    /// A loadout item missing entirely always triggers a resupply.
    /// </summary>
    [DataField("resupplyThreshold")] public float ResupplyThreshold { get; private set; } = 0.2f;

    /// <summary>
    /// How far an android can hear spoken commands, in tiles.
    /// </summary>
    [DataField("hearingRange")] public int HearingRange { get; private set; } = 5;

    /// <summary>
    /// Time a target claim remains valid, in seconds.
    /// </summary>
    [DataField("claimTtlSeconds")] public float ClaimTtlSeconds { get; private set; } = 60f;

    /// <summary>
    /// Seconds a behavior branch stays on cooldown after it fails, so the android does not retry
    /// the same failing work every tick.
    /// </summary>
    [DataField("branchFailureCooldownSeconds")] public float BranchFailureCooldownSeconds { get; private set; } = 5f;

    /// <summary>
    /// Seconds to keep retrying a path request before giving up on a NoPath result.
    /// </summary>
    [DataField("noPathRetryWindowSeconds")] public float NoPathRetryWindowSeconds { get; private set; } = 2.0f;

    /// <summary>
    /// Whether androids are spawned automatically to fill free job slots during low-population rounds.
    /// </summary>
    [DataField("autoSpawnEnabled")] public bool AutoSpawnEnabled { get; private set; } = true;

    /// <summary>
    /// Androids are only spawned while at most this many players are in the round.
    /// </summary>
    [DataField("lowPopThreshold")] public int LowPopThreshold { get; private set; } = 15;

    /// <summary>
    /// Delay before a destroyed or despawned android of a job can be replaced, in seconds.
    /// </summary>
    [DataField("replacementDelaySeconds")] public float ReplacementDelaySeconds { get; private set; } = 120f;

    /// <summary>
    /// How long a destroyed android's body stays before being removed, in seconds.
    /// </summary>
    [DataField("deadBodyDespawnSeconds")] public float DeadBodyDespawnSeconds { get; private set; } = 60f;

    /// <summary>
    /// Maximum time an android spends walking to cryostorage before being removed anyway, in seconds.
    /// </summary>
    [DataField("despawnTimeoutSeconds")] public float DespawnTimeoutSeconds { get; private set; } = 90f;

    /// <summary>
    /// How often the lifecycle system checks whether androids must be spawned, in seconds.
    /// </summary>
    [DataField("lifecycleCheckSeconds")] public float LifecycleCheckSeconds { get; private set; } = 10f;

    /// <summary>
    /// Minimum seconds between dialogue attempts per android.
    /// </summary>
    [DataField("dialogueRateLimitSeconds")] public float DialogueRateLimitSeconds { get; private set; } = 10f;

    /// <summary>
    /// Base URL of the THX-01 typed-decision server (https://huggingface.co/doofz/THX-01), e.g. http://localhost:8095.
    /// Empty disables it and only the local rules are used.
    /// </summary>
    [DataField("conversationEndpoint")] public string ConversationEndpoint { get; private set; } = string.Empty;

    /// <summary>
    /// Timeout for conversation API requests, in seconds. Slower answers fall back to local rules.
    /// </summary>
    [DataField("conversationTimeoutSeconds")] public float ConversationTimeoutSeconds { get; private set; } = 4f;

    /// <summary>
    /// Environment variable holding the optional bearer key of the THX-01 server. Never put the key in YAML.
    /// </summary>
    [DataField("conversationApiKeyEnvVar")] public string ConversationApiKeyEnvVar { get; private set; } = "THX01_API_KEY";

    /// <summary>
    /// Minimum model confidence for an intent the local rules did not recognize; below it the fallback intent is used.
    /// </summary>
    [DataField("conversationMinConfidence")] public float ConversationMinConfidence { get; private set; } = 0.3f;

    /// <summary>
    /// Minimum model confidence for a model-only intent to trigger its in-game effect (follow, clean, give, evacuate).
    /// Below it the android only answers.
    /// </summary>
    [DataField("conversationEffectMinConfidence")] public float ConversationEffectMinConfidence { get; private set; } = 0.5f;

    /// <summary>
    /// Probability of the threatening tone level needed to treat a message as a threat and alert security.
    /// </summary>
    [DataField("conversationThreatThreshold")] public float ConversationThreatThreshold { get; private set; } = 0.65f;

    /// <summary>
    /// Probability of the impatient tone level needed to answer with the short, direct variants.
    /// </summary>
    [DataField("conversationImpatientThreshold")] public float ConversationImpatientThreshold { get; private set; } = 0.45f;

    /// <summary>
    /// How long an android evacuates after an emergency is reported to it, in seconds.
    /// </summary>
    [DataField("emergencyFleeSeconds")] public float EmergencyFleeSeconds { get; private set; } = 30f;

    /// <summary>
    /// Damage one attacker must deal before the android fights back, until the attacker is in critical condition.
    /// </summary>
    [DataField("retaliationDamage")] public float RetaliationDamage { get; private set; } = 50f;

    /// <summary>
    /// The android stops fighting after losing sight of its attacker for this long, in seconds.
    /// </summary>
    [DataField("retaliationGiveUpSeconds")] public float RetaliationGiveUpSeconds { get; private set; } = 30f;

    /// <summary>
    /// Radio channel used to report threats against the android.
    /// </summary>
    [DataField("securityRadioChannel")] public ProtoId<RadioChannelPrototype> SecurityRadioChannel { get; private set; } = "Security";

    /// <summary>
    /// Radio channel used to relay emergencies reported to the android.
    /// </summary>
    [DataField("emergencyRadioChannel")] public ProtoId<RadioChannelPrototype> EmergencyRadioChannel { get; private set; } = "Common";

    /// <summary>
    /// Distance at which a supply is handed directly to the requester instead of being dropped.
    /// </summary>
    [DataField("handOverRange")] public float HandOverRange { get; private set; } = 1.5f;
}
