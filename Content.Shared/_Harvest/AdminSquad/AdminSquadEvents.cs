// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Map;
using Robust.Shared.Serialization;

namespace Content.Shared._Harvest.AdminSquad;

/// <summary>
/// An order an admin gives to a group of NPCs.
/// </summary>
[Serializable, NetSerializable]
public enum AdminSquadOrder : byte
{
    /// <summary>
    /// Walk to the destination ignoring enemies, then hold it.
    /// </summary>
    Move,

    /// <summary>
    /// Walk to the destination fighting whatever is met on the way, then hold it.
    /// </summary>
    AttackMove,

    /// <summary>
    /// Stay where it stands, fight what comes close and come back.
    /// </summary>
    Hold,

    /// <summary>
    /// Go back to the NPC's own behavior.
    /// </summary>
    Release,

    /// <summary>
    /// Attack an entity (a mob or a structure such as a door or a wall) until it is down, then hold there.
    /// </summary>
    Attack,
}

/// <summary>
/// Sent by the admin squad tool: give <see cref="Order"/> to <see cref="Units"/>.
/// <see cref="Target"/> is only used by <see cref="AdminSquadOrder.Move"/> and <see cref="AdminSquadOrder.AttackMove"/>,
/// <see cref="TargetEntity"/> by <see cref="AdminSquadOrder.Attack"/>.
/// </summary>
[Serializable, NetSerializable]
public sealed class AdminSquadOrderRequestEvent(
    List<NetEntity> units,
    AdminSquadOrder order,
    NetCoordinates target,
    NetEntity? targetEntity = null)
    : EntityEventArgs
{
    public List<NetEntity> Units = units;
    public AdminSquadOrder Order = order;
    public NetCoordinates Target = target;
    public NetEntity? TargetEntity = targetEntity;
}

/// <summary>
/// How the selected units say a line for the admin.
/// </summary>
[Serializable, NetSerializable]
public enum AdminSquadSpeech : byte
{
    Say,
    Whisper,
    Emote,
}

/// <summary>
/// Sent by the admin squad tool: every unit in <see cref="Units"/> says <see cref="Message"/>.
/// </summary>
[Serializable, NetSerializable]
public sealed class AdminSquadSpeakRequestEvent(List<NetEntity> units, string message, AdminSquadSpeech speech)
    : EntityEventArgs
{
    public List<NetEntity> Units = units;
    public string Message = message;
    public AdminSquadSpeech Speech = speech;
}

/// <summary>
/// Answer to <see cref="AdminSquadOrderRequestEvent"/>: the units that took the order (others are player controlled,
/// dead, not mobs or on another map) and the destination each one got, in the same order.
/// </summary>
[Serializable, NetSerializable]
public sealed class AdminSquadOrderResultEvent(AdminSquadOrder order, List<NetEntity> accepted, List<NetCoordinates> destinations)
    : EntityEventArgs
{
    public AdminSquadOrder Order = order;
    public List<NetEntity> Accepted = accepted;
    public List<NetCoordinates> Destinations = destinations;
}
