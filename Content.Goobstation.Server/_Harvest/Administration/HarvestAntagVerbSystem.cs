// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Goobstation.Server.Administration.Systems;
using Content.Goobstation.Server.Vampires;
using Content.Server.Antag;
using Content.Shared._EinsteinEngines.Silicon.Components;
using Content.Shared.Database;
using Content.Shared.Verbs;
using Robust.Shared.Utility;

namespace Content.Goobstation.Server._Harvest.Administration;

/// <summary>
/// Admin antag verbs for Harvest antagonists, next to the Goob ones.
/// </summary>
public sealed partial class HarvestAntagVerbSystem : EntitySystem
{
    [Dependency] private AntagSelectionSystem _antag = default!;
    [Dependency] private GoobAdminVerbSystem _goobVerbs = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<GetVerbsEvent<Verb>>(OnGetVerbs);
    }

    private void OnGetVerbs(GetVerbsEvent<Verb> args)
    {
        if (!_goobVerbs.AntagVerbAllowed(args, out var targetPlayer)
            || HasComp<SiliconComponent>(args.Target))
            return;

        // Vampire
        args.Verbs.Add(new Verb
        {
            Text = Loc.GetString("admin-verb-text-make-vampire"),
            Category = VerbCategory.Antag,
            Icon = new SpriteSpecifier.Rsi(new ResPath("/Textures/_Goobstation/Vampires/actions.rsi"), "vampire_glare"),
            Act = () =>
            {
                if (!HasComp<SiliconComponent>(args.Target))
                    _antag.ForceMakeAntag<VampireRuleComponent>(targetPlayer, "VampireRule");
            },
            Impact = LogImpact.High,
            Message = Loc.GetString("admin-verb-make-vampire"),
        });
    }
}
