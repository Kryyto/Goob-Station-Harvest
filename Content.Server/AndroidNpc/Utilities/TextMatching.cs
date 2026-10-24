// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Content.Server.AndroidNpc.Components;
using Content.Server.AndroidNpc.Data;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;

namespace Content.Server.AndroidNpc.Utilities;

/// <summary>
/// Helper for accent-insensitive, case-insensitive string matching.
/// </summary>
public static class NameMatcher
{
    /// <summary>
    /// Removes common diacritics from a string.
    /// </summary>
    public static string RemoveAccents(string input)
    {
        var normalized = input.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(input.Length);

        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Checks whether <paramref name="haystack"/> contains <paramref name="needle"/>
    /// ignoring case and accents.
    /// </summary>
    public static bool ContainsIgnoreCaseAndAccents(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle))
            return false;

        var a = RemoveAccents(haystack).ToLowerInvariant();
        var b = RemoveAccents(needle).ToLowerInvariant();

        return a.Contains(b);
    }

    /// <summary>
    /// Checks whether <paramref name="a"/> equals <paramref name="b"/>
    /// ignoring case and accents.
    /// </summary>
    public static bool EqualsIgnoreCaseAndAccents(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return false;

        return string.Equals(
            RemoveAccents(a).ToLowerInvariant(),
            RemoveAccents(b).ToLowerInvariant(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Checks whether <paramref name="message"/> starts with <paramref name="name"/>
    /// ignoring case and accents.
    /// </summary>
    public static bool StartsWithIgnoreCaseAndAccents(string message, string name)
    {
        if (string.IsNullOrEmpty(message) || string.IsNullOrEmpty(name))
            return false;

        var a = RemoveAccents(message).ToLowerInvariant();
        var b = RemoveAccents(name).ToLowerInvariant();

        return a.StartsWith(b) ||
               (a.Length > b.Length &&
                a.StartsWith(b + " "));
    }
}

/// <summary>
/// Shared addressing checks for android chat and orders.
/// </summary>
public static class AndroidAddressing
{
    /// <summary>
    /// Returns true if the message is directed at this android by name, full display name,
    /// or one of the job's address keywords.
    /// </summary>
    public static bool IsAddressedTo(
        IEntityManager entityManager,
        IPrototypeManager prototypeManager,
        EntityUid android,
        string message)
    {
        return GetAddress(entityManager, prototypeManager, android, message) != AndroidAddress.None;
    }

    /// <summary>
    /// How the message addresses this android: by its name (anywhere, as a whole word), as part of a group
    /// through an address keyword such as "androids", or not at all.
    /// </summary>
    public static AndroidAddress GetAddress(
        IEntityManager entityManager,
        IPrototypeManager prototypeManager,
        EntityUid android,
        string message)
    {
        if (string.IsNullOrWhiteSpace(message) ||
            !entityManager.TryGetComponent<AndroidIdentityComponent>(android, out var identity))
        {
            return AndroidAddress.None;
        }

        var folded = AndroidIntentClassifier.Fold(message);

        if (identity.FirstName.Length > 0 &&
            AndroidIntentClassifier.IsMatch($@"\b{Regex.Escape(AndroidIntentClassifier.Fold(identity.FirstName))}\b", folded) ||
            NameMatcher.ContainsIgnoreCaseAndAccents(message, identity.FullDisplayName))
        {
            return AndroidAddress.Name;
        }

        if (!entityManager.TryGetComponent<AndroidComponent>(android, out var androidComp) ||
            prototypeManager.AndroidJobData(androidComp.Job) is not { } proto)
        {
            return AndroidAddress.None;
        }

        foreach (var keyword in proto.AddressKeywords)
        {
            if (NameMatcher.ContainsIgnoreCaseAndAccents(message, keyword))
                return AndroidAddress.Group;
        }

        return AndroidAddress.None;
    }

    private static readonly char[] AddressSeparators = { ' ', ',', ':', ';', '!', '.', '-', '\t' };

    /// <summary>
    /// Removes the android's name or address keyword from the start and end of the message,
    /// so "Gerard, au taf !" is classified as "au taf !".
    /// </summary>
    public static string StripAddress(
        IEntityManager entityManager,
        IPrototypeManager prototypeManager,
        EntityUid android,
        string message)
    {
        var names = new List<string>();

        if (entityManager.TryGetComponent<AndroidIdentityComponent>(android, out var identity))
        {
            names.Add(identity.FullDisplayName);
            names.Add(identity.FirstName);
        }

        if (entityManager.TryGetComponent<AndroidComponent>(android, out var androidComp) &&
            prototypeManager.AndroidJobData(androidComp.Job) is { } proto)
        {
            names.AddRange(proto.AddressKeywords);
        }

        names.RemoveAll(string.IsNullOrWhiteSpace);
        names.Sort((a, b) => b.Length.CompareTo(a.Length));

        var result = message.Trim();
        bool stripped;

        do
        {
            stripped = false;
            result = result.TrimEnd(AddressSeparators);
            var folded = NameMatcher.RemoveAccents(result).ToLowerInvariant();

            foreach (var name in names)
            {
                var foldedName = NameMatcher.RemoveAccents(name).ToLowerInvariant();

                if (folded.Length == result.Length &&
                    folded.StartsWith(foldedName, StringComparison.Ordinal) &&
                    (folded.Length == foldedName.Length || Array.IndexOf(AddressSeparators, folded[foldedName.Length]) >= 0))
                {
                    result = result[foldedName.Length..].TrimStart(AddressSeparators);
                    stripped = true;
                    break;
                }

                if (folded.Length == result.Length &&
                    folded.Length > foldedName.Length &&
                    folded.EndsWith(foldedName, StringComparison.Ordinal) &&
                    Array.IndexOf(AddressSeparators, folded[^(foldedName.Length + 1)]) >= 0)
                {
                    result = result[..^foldedName.Length].TrimEnd(AddressSeparators);
                    stripped = true;
                    break;
                }
            }
        } while (stripped && result.Length > 0);

        return result.Length > 0 ? result : message.Trim();
    }
}

/// <summary>
/// Matches spoken messages against the order phrase definitions. Case, accents, hyphens and curly apostrophes
/// are ignored, so "Suis moi" matches "suis-moi".
/// </summary>
public sealed class OrderPhraseMatcher
{
    private readonly List<(AndroidOrderType Type, string Phrase)> _phrases = new();

    /// <summary>
    /// Creates a matcher backed by the provided phrase prototype.
    /// </summary>
    public OrderPhraseMatcher(AndroidOrderPhrasesPrototype phrases)
    {
        foreach (var (type, list) in new[]
                 {
                     (AndroidOrderType.FollowMe, phrases.FollowMe),
                     (AndroidOrderType.ReturnToWork, phrases.ReturnToWork),
                     (AndroidOrderType.GoToDepartment, phrases.GoToDepartment),
                     (AndroidOrderType.GoToPerson, phrases.GoToPerson),
                     (AndroidOrderType.TakeBreak, phrases.TakeBreak),
                     (AndroidOrderType.StatusReport, phrases.StatusReport),
                     (AndroidOrderType.RepeatOrder, phrases.RepeatOrder),
                     (AndroidOrderType.Capabilities, phrases.Capabilities),
                     (AndroidOrderType.Menu, phrases.Menu),
                     (AndroidOrderType.HealMe, phrases.HealMe),
                     (AndroidOrderType.Injured, phrases.Injured),
                 })
        {
            foreach (var phrase in list)
                _phrases.Add((type, Normalize(phrase)));
        }
    }

    /// <summary>
    /// Lowercase, without accents, hyphens as spaces and straight apostrophes; keeps the length of the text.
    /// </summary>
    private static string Normalize(string text)
    {
        return NameMatcher.RemoveAccents(text).ToLowerInvariant().Replace('-', ' ').Replace('’', '\'');
    }

    /// <summary>
    /// Tries to identify an order in the message.
    /// </summary>
    /// <param name="message">The spoken message.</param>
    /// <param name="orderType">The recognized order type, if any.</param>
    /// <param name="target">Optional target extracted from the phrase.</param>
    /// <returns>True if an order was matched.</returns>
    public bool TryMatch(string message, out AndroidOrderType orderType, out string? target)
    {
        orderType = AndroidOrderType.ReturnToWork;
        target = null;
        string? best = null;
        var normalized = Normalize(message);

        // The longest matching phrase wins, so "va en pause" is a break and not "go to: pause".
        foreach (var (type, phrase) in _phrases)
        {
            if (best != null && phrase.Length <= best.Length || !normalized.Contains(phrase, StringComparison.Ordinal))
                continue;

            best = phrase;
            orderType = type;
        }

        if (best == null)
            return false;

        target = ExtractTarget(message, normalized, best);
        return true;
    }

    /// <summary>
    /// Extracts the text following a matched phrase, if the phrase ends with whitespace.
    /// </summary>
    private static string? ExtractTarget(string message, string normalized, string phrase)
    {
        if (!phrase.EndsWith(' '))
            return null;

        var index = normalized.IndexOf(phrase, StringComparison.Ordinal);
        if (index < 0)
            return null;

        // Decomposed accents in the input change its length; the normalized text is used then.
        var source = normalized.Length == message.Length ? message : normalized;
        var start = index + phrase.Length;
        return start >= source.Length ? null : source[start..].Trim();
    }
}

public enum AndroidAddress
{
    None,
    Name,
    Group
}
