// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Content.Server._Harvest.AndroidNpc.Services;

/// <summary>
/// Calls a THX-01 typed-decision server (https://huggingface.co/doofz/THX-01) through
/// <c>POST {endpoint}/v1/decide</c>, asking an intent choice and a tone score in one forward pass.
/// </summary>
public sealed class Thx01ConversationBackend : IAndroidConversationBackend
{
    public const string IntentQuestion = "intent";
    public const string ToneQuestion = "tone";

    private const int MaxMessageLength = 2000;

    private readonly HttpClient? _client;
    private readonly string _endpoint;

    /// <summary>
    /// Creates a backend client. An empty endpoint disables it.
    /// </summary>
    /// <param name="endpoint">Base URL, e.g. http://localhost:8095.</param>
    /// <param name="timeoutSeconds">Request timeout; slower answers fall back to local rules.</param>
    /// <param name="apiKey">Optional bearer key, matching THX01_API_KEY on the server.</param>
    public Thx01ConversationBackend(string endpoint, float timeoutSeconds, string? apiKey)
    {
        _endpoint = endpoint.Trim().TrimEnd('/');

        if (_endpoint.Length == 0)
            return;

        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(0.5f, timeoutSeconds)) };

        if (!string.IsNullOrWhiteSpace(apiKey))
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
    }

    /// <inheritdoc />
    public bool Enabled => _client != null;

    /// <inheritdoc />
    public async Task<AndroidConversationPrediction?> PredictAsync(AndroidConversationQuery query, CancellationToken cancel)
    {
        if (_client == null || query.IntentCriteria.Count == 0)
            return null;

        var body = BuildRequest(query);
        if (body == null)
            return null;

        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _client.PostAsync($"{_endpoint}/v1/decide", content, cancel).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
            return TryParse(json, out var prediction) ? prediction : null;
        }
        catch (Exception e) when (e is TaskCanceledException or OperationCanceledException or HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the <c>/v1/decide</c> body: the job context plus the message as state, and the intent/tone questions.
    /// </summary>
    public static string? BuildRequest(AndroidConversationQuery query)
    {
        var message = query.Message.Trim();
        if (message.Length == 0)
            return null;

        if (message.Length > MaxMessageLength)
            message = message[..MaxMessageLength];

        var state = new Dictionary<string, string>(query.State)
        {
            ["message"] = message,
            ["language"] = query.Language,
        };

        var questions = new Dictionary<string, object>
        {
            [IntentQuestion] = new Dictionary<string, object>
            {
                ["type"] = "choice",
                ["instructions"] = query.IntentInstructions,
                ["criteria"] = query.IntentCriteria,
            },
        };

        if (query.ToneCriteria.Count > 0)
        {
            questions[ToneQuestion] = new Dictionary<string, object>
            {
                ["type"] = "score",
                ["instructions"] = query.ToneInstructions,
                ["criteria"] = query.ToneCriteria,
            };
        }

        return JsonSerializer.Serialize(new Dictionary<string, object> { ["state"] = state, ["questions"] = questions });
    }

    /// <summary>
    /// Parses a <c>/v1/decide</c> response:
    /// <c>{"answers": {"intent": {"choice", "confidence"}, "tone": {"probabilities": {"0": p0, "1": p1, ...}}}}</c>.
    /// </summary>
    public static bool TryParse(string json, out AndroidConversationPrediction? prediction)
    {
        prediction = null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("answers", out var answers) ||
                answers.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            string? intent = null;
            var confidence = 0f;

            if (answers.TryGetProperty(IntentQuestion, out var intentElement) && intentElement.ValueKind == JsonValueKind.Object)
            {
                if (intentElement.TryGetProperty("choice", out var choice) && choice.ValueKind == JsonValueKind.String)
                    intent = choice.GetString();

                if (intentElement.TryGetProperty("confidence", out var conf) && conf.ValueKind == JsonValueKind.Number)
                    confidence = Math.Clamp(conf.GetSingle(), 0f, 1f);
            }

            var tone = new List<float>();

            if (answers.TryGetProperty(ToneQuestion, out var toneElement) &&
                toneElement.ValueKind == JsonValueKind.Object &&
                toneElement.TryGetProperty("probabilities", out var probabilities) &&
                probabilities.ValueKind == JsonValueKind.Object)
            {
                var levels = new SortedDictionary<int, float>();
                foreach (var level in probabilities.EnumerateObject())
                {
                    if (int.TryParse(level.Name, out var index) && level.Value.ValueKind == JsonValueKind.Number)
                        levels[index] = level.Value.GetSingle();
                }

                if (levels.Count > 0 && levels.Keys.SequenceEqual(Enumerable.Range(0, levels.Count)))
                    tone.AddRange(levels.Values);
            }

            if (intent == null && tone.Count == 0)
                return false;

            prediction = new AndroidConversationPrediction(intent, confidence, tone);
            return true;
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Maps tone probabilities to a level conservatively: threatening only when clearly likely, since a false
    /// threat alerts security.
    /// </summary>
    public static int ToneLevel(IReadOnlyList<float> probabilities, float threatThreshold, float impatientThreshold)
    {
        if (probabilities.Count >= 3 && probabilities[2] >= threatThreshold)
            return 2;

        if (probabilities.Count >= 2 && probabilities[1] >= impatientThreshold)
            return 1;

        return 0;
    }
}
