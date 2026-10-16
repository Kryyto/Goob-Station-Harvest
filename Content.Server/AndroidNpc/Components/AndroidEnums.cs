// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Content.Server.AndroidNpc.Components;

public enum AndroidJob
{
    Janitor,
    Cook,
    Bartender
}

public enum AndroidStatus
{
    Working,
    Fleeing,
    FollowingOrder,
    OnBreak,
    Idle,

    /// <summary>
    /// A player took the android's job; it is walking to cryostorage to despawn.
    /// </summary>
    Leaving
}

/// <summary>
/// In-game consequence of a classified conversation intent.
/// </summary>
public enum AndroidConversationEffect
{
    None,
    FollowMe,
    ReturnToWork,
    CleanArea,
    GiveSupply,
    Emergency,

    /// <summary>
    /// Queue a food or drink order with a service android.
    /// </summary>
    PrepareOrder
}
