// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.ViewVariables;

namespace Content.Server.AndroidNpc.Components;

/// <summary>
/// One food or drink order taken by a service android.
/// </summary>
/// <param name="Language">Language the order was placed in; the android answers in it.</param>
/// <param name="Substituted">The order was not understood and something else is prepared; the android already said so.</param>
public sealed record AndroidServiceRequest(EntityUid Customer, string CustomerName, EntProtoId Item, string ItemName, float PrepSeconds, string Language, bool Substituted = false);

/// <summary>
/// Order queue of a cook or bartender android.
/// </summary>
[RegisterComponent]
public sealed partial class AndroidServiceComponent : Component
{
    [ViewVariables] public Queue<AndroidServiceRequest> Pending { get; } = new();

    [ViewVariables] public AndroidServiceRequest? Current { get; set; }

    /// <summary>
    /// Where the current order will be put down, next to the customer.
    /// </summary>
    [ViewVariables] public EntityCoordinates? DropCoordinates { get; set; }

    /// <summary>
    /// When the current order is ready; null until preparation starts.
    /// </summary>
    [ViewVariables] public TimeSpan? ReadyAt { get; set; }

    [ViewVariables] public TimeSpan NextCustomerScan { get; set; }

    /// <summary>
    /// Money left on the venue's tables that the android is walking to pick up.
    /// </summary>
    [ViewVariables] public EntityUid? TipTarget { get; set; }
}
