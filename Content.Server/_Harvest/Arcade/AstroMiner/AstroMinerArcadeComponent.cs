using Content.Shared._Harvest.Arcade.AstroMiner;
using Robust.Shared.Audio;
using Robust.Shared.Prototypes;

namespace Content.Server._Harvest.Arcade.AstroMiner;

[RegisterComponent]
public sealed partial class AstroMinerArcadeComponent : Component
{
    [ViewVariables]
    public EntityUid? Player;

    [ViewVariables]
    public readonly List<EntityUid> Spectators = new();

    /// <summary>
    /// The cave being played, null before the first game.
    /// </summary>
    [ViewVariables]
    public AstroMinerSimulation? Simulation;

    [ViewVariables]
    public bool Started;

    [ViewVariables]
    public bool Playing;

    [ViewVariables]
    public bool GameOver;

    [ViewVariables]
    public AstroMinerMode Mode;

    /// <summary>
    /// Index of the cave being played, from 0; the depth minus one in endless mode.
    /// </summary>
    [ViewVariables]
    public int LevelIndex;

    /// <summary>
    /// Rolled on each endless game; cave n is generated from it and n, so a retry replays the same cave.
    /// </summary>
    [ViewVariables]
    public int EndlessSeed;

    [ViewVariables]
    public string LevelName = string.Empty;

    /// <summary>
    /// Dispensed when the current cave is escaped, see <see cref="AstroMinerLevelPrototype.Reward"/>.
    /// </summary>
    [ViewVariables]
    public EntProtoId? LevelReward;

    [ViewVariables]
    public int Lives;

    /// <summary>
    /// Points from finished attempts; the current cave's points are in <see cref="Simulation"/>.
    /// </summary>
    [ViewVariables]
    public int Score;

    [ViewVariables]
    public float Accumulator;

    /// <summary>
    /// Seconds left before the next attempt starts, after the miner died or escaped.
    /// </summary>
    [ViewVariables]
    public float? TransitionTimer;

    /// <summary>
    /// Directions held by the player, most recent last.
    /// </summary>
    [ViewVariables]
    public readonly List<Vector2i> HeldDirections = new();

    /// <summary>
    /// A direction pressed and released between two ticks, so a quick tap still moves the miner.
    /// </summary>
    [ViewVariables]
    public Vector2i? TappedDirection;

    [ViewVariables]
    public bool Grab;

    [ViewVariables]
    public int? RoundPlacement;

    [ViewVariables]
    public int? AllTimePlacement;

    [DataField]
    public int StartingLives = 3;

    /// <summary>
    /// Chance for a game to use the alternative miner sprite.
    /// </summary>
    [DataField]
    public float AltMinerChance = 0.1f;

    /// <summary>
    /// Whether this game uses the alternative miner sprite, rolled on each new game.
    /// </summary>
    [ViewVariables]
    public bool AltMiner;

    [DataField]
    public float TransitionDelay = 2f;

    /// <summary>
    /// Oxygen lost per run through every cave, as a fraction of the cave's oxygen.
    /// </summary>
    [DataField]
    public float OxygenLossPerLoop = 0.15f;

    [DataField]
    public List<EntProtoId> PossibleRewards = new();

    [DataField]
    public int RewardMinAmount;

    [DataField]
    public int RewardMaxAmount;

    /// <summary>
    /// Prizes left in the machine, one is dispensed per cave escaped.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite)]
    public int RewardAmount;

    [DataField]
    public SoundSpecifier NewGameSound = new SoundPathSpecifier("/Audio/Effects/Arcade/newgame.ogg");

    [DataField]
    public SoundSpecifier ExitOpenSound = new SoundPathSpecifier("/Audio/Effects/Arcade/player_charge.ogg");

    [DataField]
    public SoundSpecifier EscapeSound = new SoundPathSpecifier("/Audio/Effects/Arcade/win.ogg");

    [DataField]
    public SoundSpecifier DeathSound = new SoundPathSpecifier("/Audio/Effects/Arcade/player_attack.ogg");

    [DataField]
    public SoundSpecifier GameOverSound = new SoundPathSpecifier("/Audio/Effects/Arcade/gameover.ogg");
}
