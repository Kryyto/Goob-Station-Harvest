// SPDX-FileCopyrightText: 2026 Harvest Station
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Goobstation.Shared.Objectives;
using Content.Shared.Objectives.Components;

namespace Content.Goobstation.Server.Objectives;

public sealed class CustomTextObjectiveSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<CustomTextObjectiveComponent, ObjectiveGetProgressEvent>(OnGetProgress);
    }

    private void OnGetProgress(EntityUid uid, CustomTextObjectiveComponent comp, ref ObjectiveGetProgressEvent args)
    {
        args.Progress = comp.IsComplete ? 1f : 0f;
    }

    /// <summary>
    /// Sets the custom title on an objective entity.
    /// </summary>
    public void SetCustomTitle(EntityUid uid, string title)
    {
        EntityManager.System<MetaDataSystem>().SetEntityName(uid, title);
    }

    public void SetComplete(EntityUid uid, bool complete)
    {
        if (!EntityManager.TryGetComponent<CustomTextObjectiveComponent>(uid, out var comp))
            return;
        comp.IsComplete = complete;
        Dirty(uid, comp);
    }
}
