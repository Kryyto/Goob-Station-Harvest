// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared.Guidebook;
using Robust.Shared.ContentPack;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client._Harvest.Guidebook;

/// <summary>
/// Points guide entries at their Harvest translation when one exists, so upstream guide files stay untouched.
/// A translation of <c>/ServerInfo/Guidebook/Mobs/Human.xml</c> lives in
/// <c>/ServerInfo/Guidebook/_Harvest/Translations/Mobs/Human.xml</c>.
/// </summary>
public sealed partial class GuidebookTranslationSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototype = default!;
    [Dependency] private IResourceManager _resource = default!;

    private static readonly ResPath GuidebookRoot = new("/ServerInfo/Guidebook");
    private static readonly ResPath TranslationRoot = new("/ServerInfo/Guidebook/_Harvest/Translations");

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);
        ApplyTranslations();
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<GuideEntryPrototype>())
            ApplyTranslations();
    }

    private void ApplyTranslations()
    {
        foreach (var entry in _prototype.EnumeratePrototypes<GuideEntryPrototype>())
        {
            if (!entry.Text.TryRelativeTo(GuidebookRoot, out var relative)
                || entry.Text.TryRelativeTo(TranslationRoot, out _))
                continue;

            var translated = TranslationRoot / relative.Value;
            if (_resource.ContentFileExists(translated))
                entry.Text = translated;
        }
    }
}
