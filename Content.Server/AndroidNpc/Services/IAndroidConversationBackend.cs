// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Threading;
using System.Threading.Tasks;

namespace Content.Server.AndroidNpc.Services;

/// <summary>
/// One message to classify, with the job context and typed questions to ask about it.
/// Built on the game thread from prototypes, then sent from a background task.
/// </summary>
/// <param name="Message">What the crew member said, without the android's name.</param>
/// <param name="Language">Detected language code.</param>
/// <param name="State">Hidden job context (role, duties, setting...).</param>
/// <param name="IntentInstructions">Intent question text.</param>
/// <param name="IntentCriteria">Intent keys and their descriptions.</param>
/// <param name="ToneInstructions">Tone question text.</param>
/// <param name="ToneCriteria">Ordered tone levels, calm to threatening.</param>
public sealed record AndroidConversationQuery(
    string Message,
    string Language,
    IReadOnlyDictionary<string, string> State,
    string IntentInstructions,
    IReadOnlyDictionary<string, string> IntentCriteria,
    string ToneInstructions,
    IReadOnlyList<string> ToneCriteria);

/// <summary>
/// Typed answers returned by a conversation backend for one message.
/// </summary>
/// <param name="Intent">Intent choice, e.g. "cleaning_request".</param>
/// <param name="IntentConfidence">Calibrated confidence of the intent choice, 0 to 1.</param>
/// <param name="ToneProbabilities">Probability of each tone level, calm to threatening. Empty if not answered.</param>
public sealed record AndroidConversationPrediction(string? Intent, float IntentConfidence, IReadOnlyList<float> ToneProbabilities);

/// <summary>
/// Remote typed-decision classifier for android conversation. It never controls behavior directly:
/// its answers go through the same intent table as the local rules.
/// </summary>
public interface IAndroidConversationBackend
{
    /// <summary>
    /// Whether the backend is configured and should be queried.
    /// </summary>
    bool Enabled { get; }

    /// <summary>
    /// Classifies a message. Returns null on timeout, error or malformed answer.
    /// </summary>
    Task<AndroidConversationPrediction?> PredictAsync(AndroidConversationQuery query, CancellationToken cancel);
}
