// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Prototypes;

namespace Content.Server.AndroidNpc.Data;

/// <summary>
/// Prototype containing the pool of available android first names.
/// </summary>
[Prototype]
public sealed partial class AndroidNamePoolPrototype : IPrototype
{
    /// <inheritdoc />
    [IdDataField] public string ID { get; private set; } = default!;

    /// <summary>
    /// Available first names. One is selected at spawn without replacement within a round.
    /// </summary>
    [DataField("names")] public List<string> Names { get; private set; } = new();
}
