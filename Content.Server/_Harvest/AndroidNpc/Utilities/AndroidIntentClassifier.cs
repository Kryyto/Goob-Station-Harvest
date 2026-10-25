// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Text.RegularExpressions;
using Content.Server._Harvest.AndroidNpc.Data;

namespace Content.Server._Harvest.AndroidNpc.Utilities;

/// <summary>
/// Result of a deterministic intent rule match.
/// </summary>
/// <param name="Intent">Matched intent.</param>
/// <param name="Source">"phrase_rule" or "keyword_rule".</param>
public readonly record struct AndroidIntentMatch(string Intent, string Source);

/// <summary>
/// Deterministic classifier for what a crew member says to an android: language, intent and tone.
/// Mirrors the phrase and keyword overrides of the Android-generic-responses backend.
/// </summary>
public sealed class AndroidIntentClassifier
{
    public const string PhraseSource = "phrase_rule";
    public const string KeywordSource = "keyword_rule";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    private readonly AndroidIntentRulesPrototype _rules;
    private readonly Dictionary<string, Regex?> _regexCache = new();
    private readonly Dictionary<string, Dictionary<string, HashSet<string>>> _phrases = new();
    private readonly List<(string Target, Regex Pattern)> _locations = new();

    /// <summary>
    /// Creates a classifier from the given rules prototype.
    /// </summary>
    public AndroidIntentClassifier(AndroidIntentRulesPrototype rules)
    {
        _rules = rules;

        foreach (var rule in rules.Phrases)
        {
            if (!_phrases.TryGetValue(rule.Lang, out var byIntent))
                _phrases[rule.Lang] = byIntent = new();

            if (!byIntent.TryGetValue(rule.Intent, out var set))
                byIntent[rule.Intent] = set = new();

            foreach (var phrase in rule.Phrases)
                set.Add(NormalizePhrase(phrase));
        }

        foreach (var location in rules.Locations)
        {
            foreach (var alias in location.Aliases)
                _locations.Add((location.Target, new Regex($@"\b{Regex.Escape(Fold(alias))}\b", RegexOptions.CultureInvariant, RegexTimeout)));
        }
    }

    /// <summary>
    /// Lowercase, accent-stripped text with typographic apostrophes normalized and whitespace collapsed.
    /// </summary>
    public static string Fold(string text)
    {
        var folded = NameMatcher.RemoveAccents(text).ToLowerInvariant().Replace('’', '\'');
        return string.Join(' ', folded.Split((char[]?) null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Folds the text and trims leading and trailing punctuation for whole-message phrase comparison.
    /// </summary>
    public static string NormalizePhrase(string text)
    {
        return Fold(text).Trim().Trim(PunctuationChars).Trim();
    }

    private static readonly char[] PunctuationChars = ".,;:!?¡¿'\"()[]{}-_…*~".ToCharArray();

    /// <summary>
    /// Picks the language whose hints match the message the most; ties go to the default language.
    /// </summary>
    public string DetectLanguage(string message, string? fallback = null)
    {
        var lower = message.ToLowerInvariant().Replace('’', '\'');
        var best = fallback ?? _rules.DefaultLanguage;
        var bestScore = 0;

        foreach (var language in _rules.Languages)
        {
            var score = language.Hints.Sum(hint => CountMatches(hint, lower));

            if (score > bestScore)
            {
                best = language.Id;
                bestScore = score;
            }
            else if (score == bestScore && score > 0 && language.Id == (fallback ?? _rules.DefaultLanguage))
            {
                best = language.Id;
            }
        }

        return best;
    }

    /// <summary>
    /// Returns the localized lines for a language, or the default language's lines.
    /// </summary>
    public AndroidConversationLanguage? GetLanguage(string lang)
    {
        return _rules.Languages.FirstOrDefault(l => l.Id == lang) ??
               _rules.Languages.FirstOrDefault(l => l.Id == _rules.DefaultLanguage) ??
               _rules.Languages.FirstOrDefault();
    }

    /// <summary>
    /// Intent used when nothing else classifies the message.
    /// </summary>
    public string FallbackIntent => _rules.FallbackIntent;

    /// <summary>
    /// Tries the exact phrase rules, then the ordered keyword rules, restricted to the job's intents.
    /// </summary>
    public AndroidIntentMatch? Classify(string message, string lang, IReadOnlySet<string> intents)
    {
        var normalized = NormalizePhrase(message);

        if (_phrases.TryGetValue(lang, out var byIntent))
        {
            foreach (var (intent, phrases) in byIntent)
            {
                if (intents.Contains(intent) && phrases.Contains(normalized))
                    return new AndroidIntentMatch(intent, PhraseSource);
            }
        }

        var folded = Fold(message);

        foreach (var rule in _rules.Keywords)
        {
            if (rule.Lang != lang || !intents.Contains(rule.Intent))
                continue;

            if (MatchesAny(rule.Patterns, folded))
                return new AndroidIntentMatch(rule.Intent, KeywordSource);
        }

        return null;
    }

    /// <summary>
    /// Returns the tone level: 0 calm, 1 impatient or rude, 2 threatening.
    /// Tone patterns of every language are checked, since insults and threats are often mixed.
    /// </summary>
    public int DetectTone(string message)
    {
        var folded = Fold(message);
        var level = 0;

        foreach (var rule in _rules.Tone)
        {
            if (rule.Level > level && MatchesAny(rule.Patterns, folded))
                level = rule.Level;
        }

        if (level == 0 && IsShouting(message))
            level = 1;

        return Math.Clamp(level, 0, 2);
    }

    /// <summary>
    /// Returns the nav beacon query for the first location named in the message.
    /// </summary>
    public string? FindLocation(string message)
    {
        var folded = Fold(message);

        foreach (var (target, pattern) in _locations)
        {
            if (pattern.IsMatch(folded))
                return target;
        }

        return null;
    }

    /// <summary>
    /// Matches a pattern against already folded text, treating invalid or too slow patterns as no match.
    /// </summary>
    public static bool IsMatch(string pattern, string folded)
    {
        try
        {
            return Regex.IsMatch(folded, pattern, RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (Exception e) when (e is ArgumentException or RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true if any of the regex patterns matches the already folded text.
    /// </summary>
    public bool MatchesAny(IEnumerable<string> patterns, string folded)
    {
        foreach (var pattern in patterns)
        {
            if (GetRegex(pattern) is { } regex && SafeIsMatch(regex, folded))
                return true;
        }

        return false;
    }

    private int CountMatches(string pattern, string text)
    {
        if (GetRegex(pattern) is not { } regex)
            return 0;

        try
        {
            return regex.Matches(text).Count;
        }
        catch (RegexMatchTimeoutException)
        {
            return 0;
        }
    }

    private static bool SafeIsMatch(Regex regex, string text)
    {
        try
        {
            return regex.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private Regex? GetRegex(string pattern)
    {
        if (_regexCache.TryGetValue(pattern, out var cached))
            return cached;

        Regex? regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException)
        {
            regex = null;
        }

        _regexCache[pattern] = regex;
        return regex;
    }

    private static bool IsShouting(string message)
    {
        var letters = message.Where(char.IsLetter).ToList();
        return letters.Count >= 6 && letters.All(char.IsUpper);
    }
}
