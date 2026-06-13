// SPDX-FileCopyrightText: 2026 Harvest Station
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Robust.Shared.Serialization;
using Content.Shared.Eui;

namespace Content.Goobstation.Shared.Administration;

[Serializable, NetSerializable]
public sealed class PlayerObjectivesEuiState(string username, ObjectiveEntryData[] objectives) : EuiStateBase
{
    public readonly string Username = username;
    public readonly ObjectiveEntryData[] Objectives = objectives;
}

[Serializable, NetSerializable]
public sealed class ObjectiveEntryData(
    int index,
    string title,
    int progressPercent,
    bool isCustom = false,
    bool isComplete = false)
{
    public readonly int Index = index;
    public readonly string Title = title;
    public readonly int ProgressPercent = progressPercent;
    public readonly bool IsCustom = isCustom;
    public readonly bool IsComplete = isComplete;
}

[Serializable, NetSerializable]
public sealed class PlayerObjectivesAddMessage(string protoId) : EuiMessageBase
{
    public readonly string ProtoId = protoId;
}

[Serializable, NetSerializable]
public sealed class PlayerObjectivesRemoveMessage(int index) : EuiMessageBase
{
    public readonly int Index = index;
}

[Serializable, NetSerializable]
public sealed class PlayerObjectivesAddCustomMessage(string customText) : EuiMessageBase
{
    public readonly string CustomText = customText;
}

[Serializable, NetSerializable]
public sealed class PlayerObjectivesSetCompleteMessage(int index, bool complete) : EuiMessageBase
{
    public readonly int Index = index;
    public readonly bool Complete = complete;
}
