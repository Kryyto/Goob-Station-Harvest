// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameObjects;
using Robust.Shared.ViewVariables;

namespace Content.Server._Harvest.AndroidNpc.Components;

/// <summary>
/// Conversation state of an android: reply rate limiting, pending backend request and the last exchange.
/// </summary>
[RegisterComponent]
public sealed partial class AndroidConversationComponent : Component
{
    /// <summary>
    /// Earliest time the android may reply to free conversation again.
    /// </summary>
    [ViewVariables] public TimeSpan NextReplyAt { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Identifier of the backend request in flight, if any.
    /// </summary>
    [ViewVariables] public int? PendingRequest { get; set; }

    [ViewVariables] public string? LastIntent { get; set; }

    [ViewVariables] public int LastTone { get; set; }

    [ViewVariables] public string? LastLanguage { get; set; }

    /// <summary>
    /// Where the last intent came from: phrase_rule, keyword_rule, model or fallback.
    /// </summary>
    [ViewVariables] public string? LastSource { get; set; }

    [ViewVariables] public string? LastReply { get; set; }

    [ViewVariables] public AndroidConversationEffect LastEffect { get; set; } = AndroidConversationEffect.None;
}
