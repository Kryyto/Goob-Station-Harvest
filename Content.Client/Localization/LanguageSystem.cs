// SPDX-FileCopyrightText: 2026 Eazeon <72609752+eazeon@users.noreply.github.com>
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using Content.Goobstation.Common.CCVar;
using Robust.Shared.Configuration;
using Robust.Shared.Localization;

namespace Content.Client.Localization;

public sealed class LanguageSystem : EntitySystem
{
    [Dependency] private readonly IConfigurationManager _cfg = default!;
    [Dependency] private readonly ILocalizationManager _loc = default!;

    public override void Initialize()
    {
        base.Initialize();
        _cfg.OnValueChanged(GoobCVars.UILanguage, OnLanguageChanged, invokeImmediately: true);
    }

    private void OnLanguageChanged(string language)
    {
        try
        {
            _loc.SetCulture(new CultureInfo(language));
        }
        catch (CultureNotFoundException)
        {
            _loc.SetCulture(new CultureInfo("en-US"));
        }
    }
}
