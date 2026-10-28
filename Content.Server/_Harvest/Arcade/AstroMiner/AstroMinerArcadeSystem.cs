using System.Linq;
using Content.Server.Advertise.EntitySystems;
using Content.Shared._Harvest.Arcade.AstroMiner;
using Content.Shared.Advertise.Components;
using Content.Shared.Power;
using Content.Shared.UserInterface;
using Robust.Server.GameObjects;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;

namespace Content.Server._Harvest.Arcade.AstroMiner;

/// <summary>
/// Runs the AstroMiner arcade: one player controls the miner, anyone else with the UI open watches.
/// </summary>
public sealed class AstroMinerArcadeSystem : EntitySystem
{
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly UserInterfaceSystem _ui = default!;
    [Dependency] private readonly SpeakOnUIClosedSystem _speakOnUIClosed = default!;
    [Dependency] private readonly AstroMinerLeaderboardSystem _leaderboard = default!;

    private List<AstroMinerLevelPrototype> _levels = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<AstroMinerArcadeComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<AstroMinerArcadeComponent, AfterActivatableUIOpenEvent>(OnAfterUiOpen);
        SubscribeLocalEvent<AstroMinerArcadeComponent, PowerChangedEvent>(OnPowerChanged);
        SubscribeLocalEvent<PrototypesReloadedEventArgs>(OnPrototypesReloaded);

        Subs.BuiEvents<AstroMinerArcadeComponent>(AstroMinerUiKey.Key, subs =>
        {
            subs.Event<BoundUIClosedEvent>(OnUiClosed);
            subs.Event<AstroMinerActionMessage>(OnAction);
        });

        LoadLevels();
    }

    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<AstroMinerLevelPrototype>())
            LoadLevels();
    }

    private void LoadLevels()
    {
        _levels = _prototypes.EnumeratePrototypes<AstroMinerLevelPrototype>()
            .OrderBy(l => l.Order)
            .ThenBy(l => l.ID)
            .ToList();
    }

    public override void Update(float frameTime)
    {
        var query = EntityQueryEnumerator<AstroMinerArcadeComponent>();
        while (query.MoveNext(out var uid, out var arcade))
        {
            if (!arcade.Playing || arcade.Simulation == null)
                continue;

            arcade.Accumulator += frameTime;
            var changed = false;
            var events = AstroMinerEvents.None;
            while (arcade.Accumulator >= AstroMinerSimulation.TickSeconds)
            {
                arcade.Accumulator -= AstroMinerSimulation.TickSeconds;
                events |= Step((uid, arcade));
                changed = true;
            }

            if (changed)
                SendState((uid, arcade), events: events);

            // The cave keeps running while the explosion or the escape is shown.
            if (arcade.TransitionTimer != null)
            {
                arcade.TransitionTimer -= frameTime;
                if (arcade.TransitionTimer <= 0)
                    EndTransition((uid, arcade));
            }
        }
    }

    private AstroMinerEvents Step(Entity<AstroMinerArcadeComponent> ent)
    {
        var arcade = ent.Comp;
        var sim = arcade.Simulation!;

        sim.Input = arcade.TappedDirection ?? (arcade.HeldDirections.Count > 0 ? arcade.HeldDirections[^1] : null);
        sim.Grab = arcade.Grab;
        arcade.TappedDirection = null;

        var events = sim.Tick();

        if (events.HasFlag(AstroMinerEvents.ExitOpened))
            _audio.PlayPvs(arcade.ExitOpenSound, ent);

        if (events.HasFlag(AstroMinerEvents.MinerDied))
        {
            _audio.PlayPvs(arcade.DeathSound, ent);
            arcade.Lives--;
            arcade.TransitionTimer = arcade.TransitionDelay;
        }
        else if (events.HasFlag(AstroMinerEvents.Escaped))
        {
            _audio.PlayPvs(arcade.EscapeSound, ent);
            // Leftover oxygen is worth points.
            arcade.Score += sim.Oxygen * 2;
            Dispense(ent);
            arcade.TransitionTimer = arcade.TransitionDelay;
        }

        return events;
    }

    /// <summary>
    /// Starts the next attempt once the death or escape has been shown long enough.
    /// </summary>
    private void EndTransition(Entity<AstroMinerArcadeComponent> ent)
    {
        var arcade = ent.Comp;
        arcade.TransitionTimer = null;

        var sim = arcade.Simulation!;
        arcade.Score += sim.Score;

        if (sim.Escaped)
            arcade.LevelIndex++;

        if (arcade.Lives <= 0)
        {
            EndGame(ent);
            return;
        }

        StartLevel(ent);
    }

    private void StartLevel(Entity<AstroMinerArcadeComponent> ent)
    {
        var arcade = ent.Comp;
        AstroMinerCave cave;
        if (arcade.Mode == AstroMinerMode.Endless)
        {
            var depth = arcade.LevelIndex + 1;
            cave = AstroMinerCaveGenerator.Generate(depth, unchecked(arcade.EndlessSeed + depth * 104729));
            arcade.LevelName = Loc.GetString("astrominer-level-endless");
            arcade.LevelReward = null;
        }
        else
        {
            if (_levels.Count == 0)
                return;

            var level = _levels[arcade.LevelIndex % _levels.Count];
            var loop = arcade.LevelIndex / _levels.Count;
            var oxygen = (int) MathF.Ceiling(level.Oxygen * MathF.Max(0.4f, 1f - loop * arcade.OxygenLossPerLoop));
            cave = new AstroMinerCave(level.Map, oxygen, level.CrystalsNeeded, level.CrystalValue, level.BonusCrystalValue);
            arcade.LevelName = Loc.GetString(level.Name);
            arcade.LevelReward = level.Reward;
        }

        // Same seed for a retry, so the blob grows the same way again.
        var seed = unchecked(arcade.EndlessSeed + arcade.LevelIndex * 7919);
        arcade.Simulation = new AstroMinerSimulation(cave.Map, cave.Oxygen, cave.CrystalsNeeded, cave.CrystalValue, cave.BonusCrystalValue, seed);
        arcade.Accumulator = 0;
        arcade.TappedDirection = null;
        SendState(ent);
    }

    private void NewGame(Entity<AstroMinerArcadeComponent> ent, AstroMinerMode mode)
    {
        var arcade = ent.Comp;
        arcade.Mode = mode;
        arcade.EndlessSeed = _random.Next();
        arcade.Started = true;
        arcade.Playing = true;
        arcade.GameOver = false;
        arcade.LevelIndex = 0;
        arcade.Score = 0;
        arcade.Lives = arcade.StartingLives;
        arcade.AltMiner = _random.Prob(arcade.AltMinerChance);
        arcade.TransitionTimer = null;
        arcade.RoundPlacement = null;
        arcade.AllTimePlacement = null;

        _audio.PlayPvs(arcade.NewGameSound, ent);
        SendScreen(ent, AstroMinerScreen.Game);
        StartLevel(ent);
    }

    private void EndGame(Entity<AstroMinerArcadeComponent> ent)
    {
        var arcade = ent.Comp;
        arcade.Playing = false;
        arcade.GameOver = true;

        if (arcade.Player is { } player)
        {
            var name = MetaData(player).EntityName;
            (arcade.RoundPlacement, arcade.AllTimePlacement) = _leaderboard.Register(arcade.Mode, name, arcade.Score, arcade.LevelIndex + 1);
        }

        _audio.PlayPvs(arcade.GameOverSound, ent);
        SendHighscores(ent, mode: arcade.Mode);
        SendScreen(ent, AstroMinerScreen.GameOver);
    }

    private void Dispense(Entity<AstroMinerArcadeComponent> ent)
    {
        var arcade = ent.Comp;
        var coords = Transform(ent).Coordinates;
        if (arcade.LevelReward is { } levelReward)
            SpawnAtPosition(levelReward, coords);

        if (arcade.RewardAmount <= 0 || arcade.PossibleRewards.Count == 0)
            return;

        SpawnAtPosition(_random.Pick(arcade.PossibleRewards), coords);
        arcade.RewardAmount--;
    }

    private void OnMapInit(Entity<AstroMinerArcadeComponent> ent, ref MapInitEvent args)
    {
        ent.Comp.RewardAmount = _random.Next(ent.Comp.RewardMinAmount, ent.Comp.RewardMaxAmount + 1);
    }

    private void OnAfterUiOpen(Entity<AstroMinerArcadeComponent> ent, ref AfterActivatableUIOpenEvent args)
    {
        var arcade = ent.Comp;
        if (arcade.Player == null)
            arcade.Player = args.User;
        else if (arcade.Player != args.User && !arcade.Spectators.Contains(args.User))
            arcade.Spectators.Add(args.User);

        SendUserStatus(ent, args.User);
        SendHighscores(ent, args.User);

        var screen = arcade.GameOver ? AstroMinerScreen.GameOver
            : !arcade.Started ? AstroMinerScreen.Title
            : arcade.Playing ? AstroMinerScreen.Game
            : AstroMinerScreen.Pause;
        SendScreen(ent, screen, args.User);
        SendState(ent, args.User);
    }

    private void OnUiClosed(Entity<AstroMinerArcadeComponent> ent, ref BoundUIClosedEvent args)
    {
        var arcade = ent.Comp;
        if (arcade.Player != args.Actor)
        {
            arcade.Spectators.Remove(args.Actor);
            return;
        }

        ClearInput(arcade);
        arcade.Player = null;
        if (arcade.Playing)
        {
            arcade.Playing = false;
            SendScreen(ent, AstroMinerScreen.Pause);
        }

        if (arcade.Spectators.Count == 0)
            return;

        arcade.Player = arcade.Spectators[0];
        arcade.Spectators.RemoveAt(0);
        SendUserStatus(ent, arcade.Player.Value);
    }

    private void OnPowerChanged(Entity<AstroMinerArcadeComponent> ent, ref PowerChangedEvent args)
    {
        if (args.Powered)
            return;

        _ui.CloseUi(ent.Owner, AstroMinerUiKey.Key);
        ent.Comp.Player = null;
        ent.Comp.Spectators.Clear();
        ent.Comp.Playing = false;
        ClearInput(ent.Comp);
    }

    private void OnAction(Entity<AstroMinerArcadeComponent> ent, ref AstroMinerActionMessage msg)
    {
        var arcade = ent.Comp;
        if (msg.Actor != arcade.Player)
            return;

        switch (msg.Action)
        {
            case AstroMinerAction.NewGame:
            case AstroMinerAction.NewEndlessGame:
                if (TryComp<SpeakOnUIClosedComponent>(ent, out var speak))
                    _speakOnUIClosed.TrySetFlag((ent, speak));
                NewGame(ent, msg.Action == AstroMinerAction.NewEndlessGame ? AstroMinerMode.Endless : AstroMinerMode.Standard);
                break;
            case AstroMinerAction.StartUp:
                Press(arcade, new Vector2i(0, -1));
                break;
            case AstroMinerAction.StartDown:
                Press(arcade, new Vector2i(0, 1));
                break;
            case AstroMinerAction.StartLeft:
                Press(arcade, new Vector2i(-1, 0));
                break;
            case AstroMinerAction.StartRight:
                Press(arcade, new Vector2i(1, 0));
                break;
            case AstroMinerAction.EndUp:
                arcade.HeldDirections.Remove(new Vector2i(0, -1));
                break;
            case AstroMinerAction.EndDown:
                arcade.HeldDirections.Remove(new Vector2i(0, 1));
                break;
            case AstroMinerAction.EndLeft:
                arcade.HeldDirections.Remove(new Vector2i(-1, 0));
                break;
            case AstroMinerAction.EndRight:
                arcade.HeldDirections.Remove(new Vector2i(1, 0));
                break;
            case AstroMinerAction.StartGrab:
                arcade.Grab = true;
                break;
            case AstroMinerAction.EndGrab:
                arcade.Grab = false;
                break;
            case AstroMinerAction.Pause:
                if (!arcade.Playing)
                    break;
                arcade.Playing = false;
                ClearInput(arcade);
                SendScreen(ent, AstroMinerScreen.Pause);
                break;
            case AstroMinerAction.Unpause:
                if (!arcade.Started || arcade.GameOver)
                    break;
                arcade.Playing = true;
                SendScreen(ent, AstroMinerScreen.Game);
                break;
            case AstroMinerAction.ShowHighscores:
                if (arcade.Playing)
                {
                    arcade.Playing = false;
                    ClearInput(arcade);
                }
                SendHighscores(ent, msg.Actor);
                SendScreen(ent, AstroMinerScreen.Highscores, msg.Actor);
                break;
        }
    }

    private static void Press(AstroMinerArcadeComponent arcade, Vector2i dir)
    {
        arcade.HeldDirections.Remove(dir);
        arcade.HeldDirections.Add(dir);
        arcade.TappedDirection = dir;
    }

    private static void ClearInput(AstroMinerArcadeComponent arcade)
    {
        arcade.HeldDirections.Clear();
        arcade.TappedDirection = null;
        arcade.Grab = false;
    }

    private void SendState(Entity<AstroMinerArcadeComponent> ent, EntityUid? actor = null, AstroMinerEvents events = AstroMinerEvents.None)
    {
        var arcade = ent.Comp;
        if (arcade.Simulation is not { } sim)
            return;

        var msg = new AstroMinerStateMessage
        {
            Width = sim.Width,
            Height = sim.Height,
            Cells = sim.GetCells(),
            Moves = actor == null ? sim.GetMoves() : new byte[sim.Width * sim.Height],
            Miner = sim.MinerPosition,
            AltMiner = arcade.AltMiner,
            Events = events,
            TickSeconds = AstroMinerSimulation.TickSeconds,
            Score = arcade.Score + sim.Score,
            Lives = arcade.Lives,
            Crystals = sim.Crystals,
            CrystalsNeeded = sim.CrystalsNeeded,
            Oxygen = sim.Oxygen,
            Level = arcade.LevelIndex + 1,
            LevelName = arcade.LevelName,
        };
        Send(ent, msg, actor);
    }

    private void SendScreen(Entity<AstroMinerArcadeComponent> ent, AstroMinerScreen screen, EntityUid? actor = null)
    {
        var arcade = ent.Comp;
        var msg = new AstroMinerScreenMessage(screen, arcade.Started)
        {
            Mode = arcade.Mode,
            FinalScore = arcade.Score,
            RoundPlacement = arcade.RoundPlacement,
            AllTimePlacement = arcade.AllTimePlacement,
        };
        Send(ent, msg, actor);
    }

    /// <summary>
    /// Sends the leaderboards of <paramref name="mode"/>, or of every mode.
    /// </summary>
    private void SendHighscores(Entity<AstroMinerArcadeComponent> ent, EntityUid? actor = null, AstroMinerMode? mode = null)
    {
        foreach (var m in Enum.GetValues<AstroMinerMode>())
        {
            if (mode != null && m != mode)
                continue;
            Send(ent, new AstroMinerHighscoresMessage(m, new(_leaderboard.Round(m)), new(_leaderboard.AllTime(m))), actor);
        }
    }

    private void SendUserStatus(Entity<AstroMinerArcadeComponent> ent, EntityUid actor)
    {
        Send(ent, new AstroMinerUserStatusMessage(ent.Comp.Player == actor), actor);
    }

    private void Send(EntityUid uid, BoundUserInterfaceMessage msg, EntityUid? actor)
    {
        if (actor is { } target)
            _ui.ServerSendUiMessage(uid, AstroMinerUiKey.Key, msg, target);
        else
            _ui.ServerSendUiMessage(uid, AstroMinerUiKey.Key, msg);
    }
}
