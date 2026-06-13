// SPDX-FileCopyrightText: 2026 Harvest Station
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using Content.Goobstation.Shared.Objectives;
using Content.Server.GameTicking;
using Content.Server.Objectives;
using Content.Shared.Mind;
using Content.Shared.Objectives.Systems;
using Robust.Server.Player;

namespace Content.Goobstation.Server.Objectives;

/// <summary>
/// Shows admin-assigned custom objectives in the round-end summary.
/// </summary>
public sealed class CustomObjectiveRoundEndSystem : EntitySystem
{
    [Dependency] private readonly ObjectivesSystem _objectivesSystem = null!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundEndTextAppendEvent>(OnRoundEndText);
    }

    private void OnRoundEndText(RoundEndTextAppendEvent ev)
    {
        var summaries = new List<string>();

        var mindQuery = EntityQueryEnumerator<MindComponent>();
        while (mindQuery.MoveNext(out var mindId, out var mind))
        {
            if (!HasCustomObjectives(mind))
                continue;

            var name = _objectivesSystem.GetTitle((mindId, mind));
            var result = new StringBuilder();
            result.AppendLine(Loc.GetString("objectives-with-objectives",
                ("custody", string.Empty),
                ("title", name),
                ("agent", Loc.GetString("objective-issuer-admin-plain"))));

            var objectiveSystem = EntityManager.System<SharedObjectivesSystem>();
            foreach (var objective in mind.Objectives)
            {
                if (!HasComp<CustomTextObjectiveComponent>(objective))
                    continue;

                var info = objectiveSystem.GetInfo(objective, mindId, mind);
                if (info == null)
                    continue;

                var progress = info.Value.Progress;
                result.Append("- ");
                switch (progress)
                {
                    case > 0.99f:
                        result.AppendLine(Loc.GetString("objectives-objective-success",
                            ("objective", info.Value.Title),
                            ("progress", progress)));
                        break;
                    case >= 0.5f:
                        result.AppendLine(Loc.GetString("objectives-objective-partial-success",
                            ("objective", info.Value.Title),
                            ("progress", progress)));
                        break;
                    case > 0f:
                        result.AppendLine(Loc.GetString("objectives-objective-partial-failure",
                            ("objective", info.Value.Title),
                            ("progress", progress)));
                        break;
                    default:
                        result.AppendLine(Loc.GetString("objectives-objective-fail",
                            ("objective", info.Value.Title),
                            ("progress", progress)));
                        break;
                }
            }

            summaries.Add(result.ToString());
        }

        if (summaries.Count == 0)
            return;

        var block = new StringBuilder();
        block.AppendLine(Loc.GetString("objectives-round-end-result",
            ("count", summaries.Count),
            ("agent", Loc.GetString("objective-issuer-admin-plain"))));
        block.AppendLine();
        foreach (var s in summaries)
        {
            block.AppendLine(s);
        }

        ev.AddLine(block.ToString());
    }

    private bool HasCustomObjectives(MindComponent mind)
    {
        foreach (var obj in mind.Objectives)
        {
            if (HasComp<CustomTextObjectiveComponent>(obj))
                return true;
        }
        return false;
    }
}
