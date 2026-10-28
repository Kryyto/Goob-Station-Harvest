// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._Harvest.AdminSquad;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Map;

namespace Content.Client._Harvest.AdminSquad;

/// <summary>
/// Shown while the squad tool is on: selection count, controls reminder, the orders without a destination and a line
/// the selected units say for the admin. Closing it turns the tool off.
/// </summary>
public sealed class AdminSquadWindow : DefaultWindow
{
    private readonly AdminSquadSystem _squad;
    private readonly Label _count;
    private readonly LineEdit _message;
    private readonly OptionButton _speech;

    public AdminSquadWindow(AdminSquadSystem squad)
    {
        _squad = squad;
        Title = Loc.GetString("admin-squad-window-title");
        MinWidth = 380;

        _count = new Label();

        var help = new Label
        {
            Text = Loc.GetString("admin-squad-window-help"),
            FontColorOverride = Color.Gray,
        };

        var buttons = new GridContainer { Columns = 2 };
        buttons.AddChild(MakeButton("admin-squad-window-hold",
            () => _squad.SendOrder(AdminSquadOrder.Hold, EntityCoordinates.Invalid)));
        buttons.AddChild(MakeButton("admin-squad-window-release",
            () => _squad.SendOrder(AdminSquadOrder.Release, EntityCoordinates.Invalid)));
        buttons.AddChild(MakeButton("admin-squad-window-select-screen", _squad.SelectAllOnScreen));
        buttons.AddChild(MakeButton("admin-squad-window-clear", _squad.ClearSelection));
        foreach (var child in buttons.Children)
            child.HorizontalExpand = true;

        _speech = new OptionButton();
        _speech.AddItem(Loc.GetString("admin-squad-window-say"), (int) AdminSquadSpeech.Say);
        _speech.AddItem(Loc.GetString("admin-squad-window-whisper"), (int) AdminSquadSpeech.Whisper);
        _speech.AddItem(Loc.GetString("admin-squad-window-emote"), (int) AdminSquadSpeech.Emote);
        _speech.OnItemSelected += args => _speech.SelectId(args.Id);

        _message = new LineEdit
        {
            PlaceHolder = Loc.GetString("admin-squad-window-message"),
            HorizontalExpand = true,
        };
        _message.OnTextEntered += _ => SendMessage();

        var speak = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Horizontal,
            SeparationOverride = 4,
        };
        speak.AddChild(_speech);
        speak.AddChild(_message);
        speak.AddChild(MakeButton("admin-squad-window-send", SendMessage));

        var box = new BoxContainer
        {
            Orientation = BoxContainer.LayoutOrientation.Vertical,
            SeparationOverride = 6,
        };
        box.AddChild(_count);
        box.AddChild(help);
        box.AddChild(buttons);
        box.AddChild(speak);
        Contents.AddChild(box);

        _squad.SelectionChanged += UpdateCount;
        OnClose += () => _squad.SelectionChanged -= UpdateCount;
        UpdateCount();
    }

    private static Button MakeButton(string text, Action onPressed)
    {
        var button = new Button
        {
            Text = Loc.GetString(text),
        };
        button.OnPressed += _ => onPressed();
        return button;
    }

    private void SendMessage()
    {
        _squad.Speak(_message.Text, (AdminSquadSpeech) _speech.SelectedId);
        _message.Clear();
    }

    private void UpdateCount()
    {
        _count.Text = Loc.GetString("admin-squad-window-selected", ("count", _squad.Selected.Count));
    }
}
