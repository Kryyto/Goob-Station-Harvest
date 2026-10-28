using System.Text.Json;
using Content.Shared._Harvest.Arcade.AstroMiner;
using Content.Shared.GameTicking;
using Robust.Shared.ContentPack;
using Robust.Shared.Utility;

namespace Content.Server._Harvest.Arcade.AstroMiner;

/// <summary>
/// AstroMiner high scores, separate for each game mode: one list for the current round
/// and one kept between rounds in the server's data folder.
/// </summary>
public sealed class AstroMinerLeaderboardSystem : EntitySystem
{
    [Dependency] private readonly IResourceManager _resources = default!;

    public const int MaxEntries = 10;

    private readonly Dictionary<AstroMinerMode, Board> _boards = new();
    private ISawmill _sawmill = default!;

    public override void Initialize()
    {
        base.Initialize();
        _sawmill = Logger.GetSawmill("astrominer");
        SubscribeLocalEvent<RoundRestartCleanupEvent>(_ =>
        {
            foreach (var board in _boards.Values)
                board.Round.Clear();
        });

        foreach (var mode in Enum.GetValues<AstroMinerMode>())
        {
            var board = new Board(SavePath(mode));
            _boards[mode] = board;
            Load(board);
        }
    }

    /// <summary>
    /// The standard mode keeps the file it had before the endless mode existed.
    /// </summary>
    public static ResPath SavePath(AstroMinerMode mode)
    {
        return mode == AstroMinerMode.Standard
            ? new ResPath("/astrominer_highscores.json")
            : new ResPath($"/astrominer_highscores_{mode.ToString().ToLowerInvariant()}.json");
    }

    public List<AstroMinerScoreEntry> Round(AstroMinerMode mode) => _boards[mode].Round;

    public List<AstroMinerScoreEntry> AllTime(AstroMinerMode mode) => _boards[mode].AllTime;

    /// <summary>
    /// Adds a score to both lists of its mode.
    /// </summary>
    /// <returns>The 1-based placement in each list, null when the score did not make it.</returns>
    public (int? Round, int? AllTime) Register(AstroMinerMode mode, string name, int score, int level)
    {
        if (score <= 0)
            return (null, null);

        var board = _boards[mode];
        var entry = new AstroMinerScoreEntry { Name = name, Score = score, Level = level };
        var round = Insert(board.Round, entry);
        var allTime = Insert(board.AllTime, entry);
        if (allTime != null)
            Save(board);

        return (round, allTime);
    }

    private static int? Insert(List<AstroMinerScoreEntry> list, AstroMinerScoreEntry entry)
    {
        var index = list.FindIndex(e => e.Score < entry.Score);
        if (index < 0)
            index = list.Count;
        if (index >= MaxEntries)
            return null;

        list.Insert(index, entry);
        if (list.Count > MaxEntries)
            list.RemoveAt(list.Count - 1);
        return index + 1;
    }

    private void Load(Board board)
    {
        board.AllTime.Clear();
        try
        {
            if (!_resources.UserData.Exists(board.Path))
                return;

            using var reader = _resources.UserData.OpenText(board.Path);
            var entries = JsonSerializer.Deserialize<List<AstroMinerScoreEntry>>(reader.ReadToEnd());
            if (entries == null)
                return;

            entries.Sort((a, b) => b.Score.CompareTo(a.Score));
            board.AllTime.AddRange(entries.GetRange(0, Math.Min(MaxEntries, entries.Count)));
        }
        catch (Exception e)
        {
            _sawmill.Error($"Could not read {board.Path}: {e}");
        }
    }

    private void Save(Board board)
    {
        try
        {
            using var writer = _resources.UserData.OpenWriteText(board.Path);
            writer.Write(JsonSerializer.Serialize(board.AllTime));
        }
        catch (Exception e)
        {
            _sawmill.Error($"Could not write {board.Path}: {e}");
        }
    }

    private sealed class Board(ResPath path)
    {
        public readonly ResPath Path = path;
        public readonly List<AstroMinerScoreEntry> Round = new();
        public readonly List<AstroMinerScoreEntry> AllTime = new();
    }
}
