// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Server._Harvest.AndroidNpc.Components;
using Content.Server._Harvest.AndroidNpc.Data;
using Robust.Shared.Prototypes;

namespace Content.Server._Harvest.AndroidNpc.Utilities;

/// <summary>
/// Single access point for the android data prototypes. Global prototypes use the "Default" id;
/// per-job prototypes are matched on their <see cref="AndroidJob"/>. Missing global data falls back to
/// the code defaults so callers never have to null-check.
/// </summary>
public static class AndroidData
{
    private const string DefaultId = "Default";

#pragma warning disable RA0039 // Code defaults used only when the YAML prototype is missing.
    private static readonly AndroidNpcConfigPrototype DefaultConfig = new();
    private static readonly AndroidOrderFeedbackPrototype DefaultFeedback = new();
    private static readonly AndroidOrderPhrasesPrototype DefaultPhrases = new();
#pragma warning restore RA0039

    public static AndroidNpcConfigPrototype AndroidConfig(this IPrototypeManager protos)
    {
        return protos.TryIndex<AndroidNpcConfigPrototype>(DefaultId, out var proto) ? proto : DefaultConfig;
    }

    /// <summary>
    /// Order feedback lines in the given language, defaulting to the server language, then English.
    /// </summary>
    public static AndroidOrderFeedbackPrototype AndroidFeedback(this IPrototypeManager protos, string? language = null)
    {
        language ??= protos.AndroidConfig().Language;

        if (protos.TryIndex<AndroidOrderFeedbackPrototype>(language, out var proto) ||
            protos.TryIndex("en", out proto))
        {
            return proto;
        }

        return DefaultFeedback;
    }

    public static AndroidOrderPhrasesPrototype AndroidPhrases(this IPrototypeManager protos)
    {
        return protos.TryIndex<AndroidOrderPhrasesPrototype>(DefaultId, out var proto) ? proto : DefaultPhrases;
    }

    public static AndroidJobPrototype? AndroidJobData(this IPrototypeManager protos, AndroidJob job)
    {
        foreach (var proto in protos.EnumeratePrototypes<AndroidJobPrototype>())
        {
            if (proto.Job == job)
                return proto;
        }

        return null;
    }

    /// <summary>
    /// The job's whole menu: its hand-written entries (jobs.yml) first, then the generated ones (menus.yml).
    /// </summary>
    public static List<AndroidMenuItem> AndroidMenu(this IPrototypeManager protos, AndroidJob job)
    {
        var menu = new List<AndroidMenuItem>(protos.AndroidJobData(job)?.Menu ?? new());
        if (protos.TryIndex<AndroidMenuPrototype>(job.ToString(), out var generated))
            menu.AddRange(generated.Items.Where(i => menu.All(m => m.Item != i.Item)));

        return menu;
    }

    public static AndroidConversationPrototype? AndroidConversation(this IPrototypeManager protos, AndroidJob job)
    {
        foreach (var proto in protos.EnumeratePrototypes<AndroidConversationPrototype>())
        {
            if (proto.Job == job)
                return proto;
        }

        return null;
    }

    /// <summary>
    /// Replaces {key} placeholders in a template.
    /// </summary>
    public static string Format(string template, params (string Key, string Value)[] values)
    {
        foreach (var (key, value) in values)
            template = template.Replace($"{{{key}}}", value, StringComparison.Ordinal);

        return template;
    }
}
