// SPDX-FileCopyrightText: 2026 Harvest Station
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Goobstation.Server.Objectives;
using Content.Goobstation.Shared.Administration;
using Content.Goobstation.Shared.Objectives;
using Content.Server.Administration.Managers;
using Content.Server.EUI;
using Content.Shared.Administration;
using Content.Shared.Eui;
using Content.Shared.Mind;
using Content.Shared.Objectives.Systems;
using Robust.Server.Player;
using Robust.Shared.Player;

namespace Content.Goobstation.Server.Administration;

public sealed class PlayerObjectivesEui : BaseEui
{
    [Dependency] private readonly IEntityManager _entities = null!;
    [Dependency] private readonly IAdminManager _admins = null!;

    private readonly ICommonSession _targetSession;

    public PlayerObjectivesEui(ICommonSession targetSession)
    {
        IoCManager.InjectDependencies(this);
        _targetSession = targetSession;
    }

    public override EuiStateBase GetNewState()
    {
        var minds = _entities.System<SharedMindSystem>();
        if (!minds.TryGetMind(_targetSession, out var mindId, out var mind))
            return new PlayerObjectivesEuiState(_targetSession.Name, Array.Empty<ObjectiveEntryData>());

        var objectives = _entities.System<SharedObjectivesSystem>();
        var entries = new List<ObjectiveEntryData>();
        for (var i = 0; i < mind.Objectives.Count; i++)
        {
            var objUid = mind.Objectives[i];
            var info = objectives.GetInfo(objUid, mindId, mind);
            var isCustom = _entities.HasComponent<CustomTextObjectiveComponent>(objUid);
            var isComplete = isCustom && _entities.GetComponent<CustomTextObjectiveComponent>(objUid).IsComplete;
            entries.Add(info == null
                ? new ObjectiveEntryData(i, objUid.ToString(), 0, isCustom, isComplete)
                : new ObjectiveEntryData(i, info.Value.Title, (int) (info.Value.Progress * 100f), isCustom, isComplete));
        }

        return new PlayerObjectivesEuiState(_targetSession.Name, entries.ToArray());
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        if (!_admins.HasAdminFlag(Player, AdminFlags.Admin))
            return;

        var minds = _entities.System<SharedMindSystem>();
        if (!minds.TryGetMind(_targetSession, out var mindId, out var mind))
            return;

        var customSystem = _entities.System<CustomTextObjectiveSystem>();

        switch (msg)
        {
            case PlayerObjectivesAddMessage add:
                minds.TryAddObjective(mindId, mind, add.ProtoId);
                StateDirty();
                break;

            case PlayerObjectivesAddCustomMessage custom:
                var objectives = _entities.System<SharedObjectivesSystem>();
                var customObjUid = objectives.TryCreateObjective(mindId, mind, "AdminCustomObjective");
                if (customObjUid != null)
                {
                    customSystem.SetCustomTitle(customObjUid.Value, custom.CustomText);
                    minds.AddObjective(mindId, mind, customObjUid.Value);
                }
                StateDirty();
                break;

            case PlayerObjectivesRemoveMessage remove:
                minds.TryRemoveObjective(mindId, mind, remove.Index);
                StateDirty();
                break;

            case PlayerObjectivesSetCompleteMessage setComplete:
                if (setComplete.Index >= 0 && setComplete.Index < mind.Objectives.Count)
                {
                    var objUid = mind.Objectives[setComplete.Index];
                    customSystem.SetComplete(objUid, setComplete.Complete);
                }
                StateDirty();
                break;
        }
    }
}
