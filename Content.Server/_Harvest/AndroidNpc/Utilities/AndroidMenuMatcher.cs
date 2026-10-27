// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using System.Text.RegularExpressions;
using Content.Server._Harvest.AndroidNpc.Data;
using Robust.Shared.Prototypes;

namespace Content.Server._Harvest.AndroidNpc.Utilities;

/// <summary>
/// Finds the menu item a customer asks for. A whole name or a pattern always counts; with fuzzy matching, a name
/// also matches with a small typo per word ("mohito", "margharita") or with only its main words ("des frites").
/// The most specific match wins: "gin fizz" beats "gin", and earlier (hand-written) entries win ties.
/// </summary>
public sealed class AndroidMenuMatcher
{
    /// <summary>
    /// Words that do not identify an item: articles, and containers ("un verre de bidule" is not a glass of wine).
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> StopWords = new()
    {
        ["en"] = new() { "a", "an", "the", "of", "and", "with", "in", "on", "to", "some", "glass", "cup", "bowl", "bottle", "can", "slice", "piece", "plate" },
        ["fr"] = new() { "a", "au", "aux", "d", "de", "des", "du", "en", "et", "l", "la", "le", "les", "un", "une", "avec", "sur", "verre", "tasse", "bol", "bouteille", "canette", "tranche", "part", "assiette" },
    };

    private const float ExactBonus = 4f;
    private const float PatternBonus = 3f;
    private const float CompleteBonus = 3f;
    private const float TypoPenalty = 1.5f;

    private readonly List<Entry> _entries = new();

    private sealed record Entry(AndroidMenuItem Item, List<string[]> Names, List<Regex> Patterns);

    public AndroidMenuMatcher(IEnumerable<AndroidMenuItem> menu, IPrototypeManager protos)
    {
        foreach (var item in menu)
        {
            // Spoken names replace the entity name: filled glasses are all named "metamorphic glass".
            var names = item.Names.Values.ToList();
            if (names.Count == 0 && protos.TryIndex(item.Item, out var proto))
                names.Add(proto.Name);

            _entries.Add(new Entry(item,
                names.Select(n => Tokenize(AndroidIntentClassifier.Fold(n))).Where(t => t.Length > 0).Distinct(TokensComparer.Instance).ToList(),
                item.Patterns.Select(p => new Regex(p, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))).ToList()));
        }
    }

    public IEnumerable<AndroidMenuItem> Items => _entries.Select(e => e.Item);

    /// <summary>
    /// The best menu item named in the text, or null. Typos and partial names count only when
    /// <paramref name="fuzzy"/> is set, i.e. once the message is known to be an order.
    /// </summary>
    public AndroidMenuItem? Match(string text, string language, bool fuzzy)
    {
        var folded = AndroidIntentClassifier.Fold(text);
        var words = Tokenize(folded);
        var stop = StopWords.GetValueOrDefault(language) ?? StopWords["en"];

        AndroidMenuItem? best = null;
        var bestScore = 0f;
        var bestMissing = int.MaxValue;

        foreach (var entry in _entries)
        {
            foreach (var name in entry.Names)
            {
                var (score, missing) = ScoreName(name, words, stop, fuzzy);
                Consider(entry.Item, score, missing);
            }

            foreach (var pattern in entry.Patterns)
            {
                try
                {
                    if (pattern.Match(folded) is { Success: true } m)
                        Consider(entry.Item, m.Length + PatternBonus, 0);
                }
                catch (RegexMatchTimeoutException)
                {
                }
            }
        }

        return best;

        void Consider(AndroidMenuItem item, float score, int missing)
        {
            if (score <= 0f || score < bestScore || score == bestScore && missing >= bestMissing)
                return;

            best = item;
            bestScore = score;
            bestMissing = missing;
        }
    }

    private static (float Score, int Missing) ScoreName(string[] name, string[] words, HashSet<string> stop, bool fuzzy)
    {
        var main = name.Where(t => !stop.Contains(t)).ToArray();
        if (main.Length == 0)
            return (0f, 0);

        var length = main.Sum(t => t.Length);
        if (ContainsSequence(words, name))
            return (string.Join(' ', name).Length + ExactBonus, 0);

        if (!fuzzy)
            return (0f, 0);

        var matched = 0;
        var typos = 0;
        var anchored = false;

        foreach (var token in main)
        {
            var distance = words.Select(w => Distance(token, w)).DefaultIfEmpty(int.MaxValue).Min();
            if (distance > AllowedTypos(token))
                continue;

            matched += token.Length;
            typos += distance;
            anchored |= token.Length >= 4;
        }

        // At least half of the name, including a real word: "frites" finds "frites de l'espace", "jus" alone finds nothing.
        if (!anchored || matched * 2 < length)
            return (0f, 0);

        var complete = matched == length ? CompleteBonus : 0f;
        return (matched + complete - typos * TypoPenalty, length - matched);
    }

    private static int AllowedTypos(string token) => token.Length >= 8 ? 2 : token.Length >= 5 ? 1 : 0;

    /// <summary>
    /// Edit distance between a name word and a spoken word; a plural ending is free.
    /// </summary>
    private static int Distance(string token, string word)
    {
        if (word == token || word.Length == token.Length + 1 && word.StartsWith(token, StringComparison.Ordinal) && word[^1] is 's' or 'x')
            return 0;

        if (Math.Abs(word.Length - token.Length) > 2)
            return int.MaxValue;

        var previous = new int[word.Length + 1];
        var current = new int[word.Length + 1];
        for (var j = 0; j <= word.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= token.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= word.Length; j++)
            {
                var cost = token[i - 1] == word[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[word.Length];
    }

    private static bool ContainsSequence(string[] words, string[] name)
    {
        for (var i = 0; i + name.Length <= words.Length; i++)
        {
            var j = 0;
            while (j < name.Length && Distance(name[j], words[i + j]) == 0)
                j++;

            if (j == name.Length)
                return true;
        }

        return false;
    }

    private static string[] Tokenize(string folded)
    {
        return Regex.Split(folded, @"[^\p{L}\p{N}]+").Where(t => t.Length > 0).ToArray();
    }

    private sealed class TokensComparer : IEqualityComparer<string[]>
    {
        public static readonly TokensComparer Instance = new();

        public bool Equals(string[]? x, string[]? y) => x != null && y != null && x.SequenceEqual(y);

        public int GetHashCode(string[] obj) => string.Join(' ', obj).GetHashCode();
    }
}
