// SPDX-FileCopyrightText: 2026 Harvest Station
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Client.Eui;
using Content.Goobstation.Shared.Administration;
using Content.Shared.Eui;
using JetBrains.Annotations;

namespace Content.Goobstation.Client.Administration.PlayerObjectives;

[UsedImplicitly]
public sealed class PlayerObjectivesEui : BaseEui
{
    private PlayerObjectivesWindow? _window;

    public override void Opened()
    {
        _window = new PlayerObjectivesWindow();
        _window.OnAddObjective += protoId => SendMessage(new PlayerObjectivesAddMessage(protoId));
        _window.OnAddCustomObjective += text => SendMessage(new PlayerObjectivesAddCustomMessage(text));
        _window.OnRemoveObjective += index => SendMessage(new PlayerObjectivesRemoveMessage(index));
        _window.OnSetObjectiveComplete += (index, complete) => SendMessage(new PlayerObjectivesSetCompleteMessage(index, complete));
        _window.OnClose += () => SendMessage(new CloseEuiMessage());
        _window.OpenCentered();
    }

    public override void Closed()
    {
        _window?.Close();
    }

    public override void HandleState(EuiStateBase state)
    {
        if (state is not PlayerObjectivesEuiState s)
            return;

        _window?.UpdateState(s);
    }
}
