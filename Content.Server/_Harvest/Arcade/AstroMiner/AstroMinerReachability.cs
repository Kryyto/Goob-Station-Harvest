namespace Content.Server._Harvest.Arcade.AstroMiner;

/// <summary>
/// Estimates what the miner can reach in a cave map (legend in <see cref="Content.Shared._Harvest.Arcade.AstroMiner.AstroMinerLevelPrototype"/>).
/// </summary>
/// <remarks>
/// The miner walks through dirt, empty cells and crystals. Hull, rock walls, foundries, blobs and carps block it.
/// A boulder blocks it too, unless it can be pushed sideways into an empty cell, or the miner can reach the cell
/// right below it and dig it out so the boulder falls away. Falls are not simulated: a boulder that drops into a
/// tunnel, or a carp guarding a passage, can still make a cave harder than this says.
/// </remarks>
public static class AstroMinerReachability
{
    private static readonly (int X, int Y)[] Directions = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    public readonly record struct Result(bool ExitReachable, int Crystals);

    public static Result Analyze(IReadOnlyList<string> map)
    {
        var height = map.Count;
        var width = 0;
        foreach (var row in map)
            width = Math.Max(width, row.Length);

        char At(int x, int y) => y >= 0 && y < height && x >= 0 && x < map[y].Length ? map[y][x] : '#';

        (int X, int Y)? start = null;
        for (var y = 0; y < height && start == null; y++)
        {
            var x = map[y].IndexOf('P');
            if (x >= 0)
                start = (x, y);
        }

        if (start == null)
            return new Result(false, 0);

        var reached = new bool[width, height];
        var cleared = new bool[width, height];

        // Every boulder dug out from below opens more of the cave, so search again until nothing changes.
        while (true)
        {
            Array.Clear(reached);
            var queue = new Queue<(int X, int Y)>();
            queue.Enqueue(start.Value);
            reached[start.Value.X, start.Value.Y] = true;

            while (queue.TryDequeue(out var cell))
            {
                foreach (var (dx, dy) in Directions)
                {
                    var nx = cell.X + dx;
                    var ny = cell.Y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height || reached[nx, ny])
                        continue;

                    var passable = At(nx, ny) switch
                    {
                        '.' or ' ' or '*' or 'E' or 'P' => true,
                        'o' => cleared[nx, ny] || (dy == 0 && IsEmpty(nx + dx, ny)),
                        _ => false,
                    };

                    if (!passable)
                        continue;

                    reached[nx, ny] = true;
                    queue.Enqueue((nx, ny));
                }
            }

            var changed = false;
            for (var y = 0; y < height - 1; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    if (At(x, y) == 'o' && !cleared[x, y] && reached[x, y + 1])
                    {
                        cleared[x, y] = true;
                        changed = true;
                    }
                }
            }

            if (!changed)
                break;
        }

        var exit = false;
        var crystals = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (!reached[x, y])
                    continue;
                switch (At(x, y))
                {
                    case 'E':
                        exit = true;
                        break;
                    case '*':
                        crystals++;
                        break;
                }
            }
        }

        return new Result(exit, crystals);

        // A boulder can only be pushed into a cell that is empty from the start; dug dirt would also do,
        // but only from the other side, which this does not track.
        bool IsEmpty(int x, int y) => At(x, y) == ' ';
    }
}
