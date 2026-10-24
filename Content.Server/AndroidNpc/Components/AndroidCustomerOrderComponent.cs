// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.DoAfter;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.ViewVariables;

namespace Content.Server.AndroidNpc.Components;

/// <summary>
/// Tracks an android's active food/drink order while it waits at the bar or kitchen.
/// Added by <see cref="Systems.AndroidCustomerSystem"/> when the android sits down and starts
/// waiting, and removed once the order is served or the android gives up and leaves grumpy.
/// </summary>
[RegisterComponent]
public sealed partial class AndroidCustomerOrderComponent : Component
{
    /// <summary>
    /// Prototype of the specific drink or food the android asked for.
    /// </summary>
    [ViewVariables]
    public EntProtoId RequestedItem;

    /// <summary>
    /// Where the order was placed (bar or kitchen). Determines which category of item
    /// (drink vs. food) will satisfy the order.
    /// </summary>
    [ViewVariables]
    public AndroidBreakVenue Venue;

    /// <summary>
    /// The DoAfter driving the waiting progress bar above the android's head.
    /// </summary>
    [ViewVariables]
    public DoAfterId? WaitDoAfter;

    /// <summary>
    /// Set once the order has been fulfilled, to guard against being served twice.
    /// </summary>
    [ViewVariables]
    public bool Served;

    /// <summary>
    /// A service android already put the order down for this customer; it is not served again.
    /// </summary>
    [ViewVariables]
    public bool Delivered;

    /// <summary>
    /// The item a service android put down for this customer; taken before anything else lying around.
    /// </summary>
    [ViewVariables]
    public EntityUid? ServedItem;

    /// <summary>
    /// Items the android failed to eat or drink (sealed packets, wrong containers); never tried again.
    /// </summary>
    [ViewVariables]
    public HashSet<EntityUid> Refused = new();

    /// <summary>
    /// Item the android is currently eating/drinking, if it has found a matching order nearby.
    /// Cleared (and the order finished) once the item is empty or deleted.
    /// </summary>
    [ViewVariables]
    public EntityUid? ConsumingItem;

    /// <summary>
    /// Whether <see cref="ConsumingItem"/> is an exact match for <see cref="RequestedItem"/>,
    /// cached at pickup time since the item may no longer exist once it's finished being eaten.
    /// </summary>
    [ViewVariables]
    public bool ConsumingItemIsExactMatch;

    /// <summary>
    /// Earliest time at which the android is allowed to scan its surroundings again for its order.
    /// </summary>
    [ViewVariables]
    public TimeSpan NextScanAt;
}
