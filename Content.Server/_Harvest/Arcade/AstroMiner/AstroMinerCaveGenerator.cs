using System.Linq;

namespace Content.Server._Harvest.Arcade.AstroMiner;

/// <summary>
/// A cave ready to be played: its map (legend in <see cref="Content.Shared._Harvest.Arcade.AstroMiner.AstroMinerLevelPrototype"/>) and its rules.
/// </summary>
public sealed record AstroMinerCave(List<string> Map, int Oxygen, int CrystalsNeeded, int CrystalValue, int BonusCrystalValue);

/// <summary>
/// Builds the caves of the endless mode. Deeper caves have more boulders and structures, more crystals to find
/// and less oxygen. The same depth and seed always give the same cave, so a retry after a death replays it.
/// </summary>
public static class AstroMinerCaveGenerator
{
    public const int Width = 40;
    public const int Height = 22;

    private const int Attempts = 10;
    private const int PlacementTries = 15;

    private enum Structure
    {
        /// <summary>A long pocket with carps, boulders held above it.</summary>
        CarpChamber,
        /// <summary>A 2x2 pocket with two carps.</summary>
        CarpCell,
        /// <summary>A small pocket with a crystal between one or two carps.</summary>
        Vault,
        /// <summary>Stacked halls two cells high, each with a crystal and a carp, entered from a dirt side.</summary>
        Floors,
        /// <summary>A foundry line with boulders above it and a pocket below for the crystals.</summary>
        Foundry,
        /// <summary>A pocket with a blob, which boulders dropped from above can box in.</summary>
        BlobPit,
    }

    /// <summary>
    /// Structures and the depth they start appearing at.
    /// </summary>
    private static readonly (Structure Kind, int MinDepth)[] Structures =
    [
        (Structure.Foundry, 1),
        (Structure.CarpChamber, 2),
        (Structure.CarpCell, 2),
        (Structure.Vault, 3),
        (Structure.BlobPit, 3),
        (Structure.Floors, 4),
    ];

    public static AstroMinerCave Generate(int depth, int seed)
    {
        depth = Math.Max(1, depth);
        var random = new Random(seed);
        var needed = Math.Min(14 + 2 * depth, 40);

        List<string> map = default!;
        var result = default(AstroMinerReachability.Result);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            map = ToRows(Build(depth, random));
            result = AstroMinerReachability.Analyze(map);
            if (result.ExitReachable && result.Crystals >= needed + 4)
                break;
        }

        // Walled in every time: fall back to caves without structures, then to one with a tunnel to the exit.
        for (var attempt = 0; !result.ExitReachable && attempt < Attempts; attempt++)
        {
            map = ToRows(Build(1, random));
            result = AstroMinerReachability.Analyze(map);
        }

        if (!result.ExitReachable)
        {
            map = ToRows(Tunnel(Build(1, random)));
            result = AstroMinerReachability.Analyze(map);
        }

        needed = Math.Min(needed, result.Crystals >= 5 ? result.Crystals - 4 : result.Crystals);

        var value = 10 + 2 * Math.Min(depth, 10);
        return new AstroMinerCave(map, Math.Max(90, 164 - 4 * depth), needed, value, value + 10);
    }

    /// <summary>
    /// Cells of the cave and what may go in them while it is built.
    /// </summary>
    private sealed class Layout
    {
        public readonly char[,] Grid = new char[Width, Height];

        /// <summary>No structure here: the miner's corner and the exit's columns.</summary>
        public readonly bool[,] NoStructure = new bool[Width, Height];

        /// <summary>Left as drawn when boulders, crystals and holes are scattered.</summary>
        public readonly bool[,] Fixed = new bool[Width, Height];

        public void Mark(bool[,] mask, int x, int y, int w, int h)
        {
            for (var yy = Math.Max(0, y); yy < y + h && yy < Height; yy++)
            {
                for (var xx = Math.Max(0, x); xx < x + w && xx < Width; xx++)
                    mask[xx, yy] = true;
            }
        }

        /// <summary>
        /// Whether a structure can take the rectangle: only plain dirt, away from the corner and the exit.
        /// </summary>
        public bool IsFree(int x, int y, int w, int h)
        {
            if (x < 1 || y < 1 || x + w > Width - 1 || y + h > Height - 1)
                return false;

            for (var yy = y; yy < y + h; yy++)
            {
                for (var xx = x; xx < x + w; xx++)
                {
                    if (NoStructure[xx, yy] || Fixed[xx, yy] || Grid[xx, yy] != '.')
                        return false;
                }
            }

            return true;
        }

        public void Set(int x, int y, char c) => Grid[x, y] = c;
    }

    private static char[,] Build(int depth, Random random)
    {
        var layout = new Layout();
        var grid = layout.Grid;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
                grid[x, y] = x == 0 || y == 0 || x == Width - 1 || y == Height - 1 ? '#' : '.';
        }

        layout.Mark(layout.NoStructure, 1, 1, 6, 5);
        layout.Mark(layout.NoStructure, Width - 4, 1, 3, Height - 2);

        var doors = new List<(int X, int Y)>();
        if (depth >= 4 && random.NextDouble() < 0.3)
            HullRooms(layout, random, doors);

        var bands = random.Next(0, Math.Min(3, 1 + depth / 3));
        for (var i = 0; i < bands; i++)
            Band(layout, random, random.Next(5, Height - 3));

        var count = Math.Min(1 + depth / 2, 7);
        var blobs = 0;
        var kinds = Structures.Where(s => s.MinDepth <= depth).Select(s => s.Kind).ToList();
        for (var i = 0; i < count; i++)
        {
            var kind = kinds[random.Next(kinds.Count)];
            if (kind == Structure.BlobPit && blobs++ > 0)
                kind = Structure.Foundry;
            TryPlace(layout, random, kind);
        }

        var boulder = Math.Min(0.09 + 0.012 * depth, 0.24);
        var crystal = 0.07;
        var empty = 0.04 + 0.005 * Math.Min(depth, 8);
        for (var y = 1; y < Height - 1; y++)
        {
            for (var x = 1; x < Width - 1; x++)
            {
                if (grid[x, y] != '.' || layout.Fixed[x, y])
                    continue;

                var roll = random.NextDouble();
                if (roll < boulder)
                    grid[x, y] = 'o';
                else if (roll < boulder + crystal)
                    grid[x, y] = '*';
                else if (roll < boulder + crystal + empty)
                    grid[x, y] = ' ';
            }
        }

        // Doorways in the hull stay diggable.
        foreach (var (x, y) in doors)
            grid[x, y] = '.';

        for (var y = 1; y < 4; y++)
        {
            for (var x = 1; x < 5; x++)
                grid[x, y] = '.';
        }

        grid[2, 2] = 'P';
        var exitY = random.Next(Height - 8, Height - 2);
        grid[Width - 2, exitY] = 'E';
        grid[Width - 3, exitY] = '.';
        return grid;
    }

    private static void HullRooms(Layout layout, Random random, List<(int X, int Y)> doors)
    {
        var grid = layout.Grid;
        foreach (var x in new[] { 10, 20, 30 })
        {
            for (var y = 1; y < Height - 1; y++)
                grid[x, y] = '#';
            for (var i = 0; i < 3; i++)
                doors.Add((x, random.Next(2, Height - 2)));
        }

        foreach (var y in new[] { 7, 14 })
        {
            for (var x = 1; x < Width - 1; x++)
                grid[x, y] = '#';
            for (var i = 0; i < 6; i++)
                doors.Add((random.Next(2, Width - 2), y));
        }

        // Keep the corner and the exit reachable whatever the doors.
        for (var y = 1; y < Height - 1; y++)
        {
            for (var x = 1; x < Width - 1; x++)
            {
                if (layout.NoStructure[x, y] && grid[x, y] == '#')
                    grid[x, y] = '.';
            }
        }
    }

    private static void Band(Layout layout, Random random, int y)
    {
        for (var x = 1; x < Width - 1; x++)
        {
            if (!layout.NoStructure[x, y] && layout.Grid[x, y] == '.')
                layout.Grid[x, y] = '=';
        }

        for (var i = 0; i < 4; i++)
        {
            var gap = random.Next(3, Width - 4);
            layout.Grid[gap, y] = layout.Grid[gap + 1, y] = '.';
        }
    }

    private static void TryPlace(Layout layout, Random random, Structure kind)
    {
        // Size of the interior; the footprint adds walls around it and the rows above it.
        var (w, h) = kind switch
        {
            Structure.CarpChamber => (random.Next(5, 9), 3),
            Structure.CarpCell => (2, 2),
            Structure.Vault => (3, 2),
            Structure.Floors => (random.Next(8, 15), 3 * random.Next(2, 5) - 1),
            Structure.Foundry => (random.Next(3, 7), 3),
            _ => (5, 3),
        };

        for (var attempt = 0; attempt < PlacementTries; attempt++)
        {
            var x = random.Next(2, Width - w - 2);
            var y = random.Next(3, Height - h - 2);
            // Walls on both sides and below, two rows above for the boulders and their dirt ceiling.
            var (fx, fy, fw, fh) = kind == Structure.Floors
                ? (x - 1, y - 1, w + 2, h + 2)
                : (x - 1, y - 2, w + 2, h + 3);

            if (!layout.IsFree(fx, fy, fw, fh))
                continue;

            switch (kind)
            {
                case Structure.Floors:
                    Floors(layout, x, y, w, h);
                    break;
                case Structure.Foundry:
                    Foundry(layout, x, y, w);
                    break;
                default:
                    Pocket(layout, random, kind, x, y, w, h);
                    break;
            }

            layout.Mark(layout.Fixed, fx, fy, fw, fh);
            return;
        }
    }

    /// <summary>
    /// An empty pocket walled on its sides and bottom, with boulders held above it by a dirt ceiling:
    /// digging the ceiling drops them in.
    /// </summary>
    private static void Pocket(Layout layout, Random random, Structure kind, int x, int y, int w, int h)
    {
        for (var yy = y; yy < y + h; yy++)
        {
            for (var xx = x; xx < x + w; xx++)
                layout.Set(xx, yy, ' ');
            layout.Set(x - 1, yy, '=');
            layout.Set(x + w, yy, '=');
        }

        for (var xx = x - 1; xx <= x + w; xx++)
            layout.Set(xx, y + h, '=');

        for (var xx = x; xx < x + w; xx++)
        {
            layout.Set(xx, y - 1, '.');
            layout.Set(xx, y - 2, kind == Structure.CarpCell || (xx - x) % 2 == 0 ? 'o' : '.');
        }

        var bottom = y + h - 1;
        switch (kind)
        {
            case Structure.CarpChamber:
                for (var i = 0; 1 + i * 3 < w; i++)
                    layout.Set(x + 1 + i * 3, bottom, 'c');
                break;
            case Structure.CarpCell:
                layout.Set(x, bottom, 'c');
                layout.Set(x + 1, y, 'c');
                break;
            case Structure.Vault:
                layout.Set(x, bottom, 'c');
                layout.Set(x + 1, bottom, '*');
                if (random.NextDouble() < 0.5)
                    layout.Set(x + 2, bottom, 'c');
                break;
            case Structure.BlobPit:
                layout.Set(x + w / 2, bottom, 'b');
                break;
        }
    }

    /// <summary>
    /// Halls two cells high stacked between rock walls. Each is entered through dirt on its left,
    /// next to its crystal, while a carp patrols from the right.
    /// </summary>
    private static void Floors(Layout layout, int x, int y, int w, int h)
    {
        for (var yy = y - 1; yy <= y + h; yy++)
        {
            var wall = (yy - y + 1) % 3 == 0;
            for (var xx = x; xx < x + w; xx++)
                layout.Set(xx, yy, wall ? '=' : ' ');
            layout.Set(x - 1, yy, wall ? '=' : '.');
            layout.Set(x + w, yy, '=');

            if (!wall && (yy - y) % 3 == 1)
            {
                layout.Set(x, yy, '*');
                layout.Set(x + w - 1, yy, 'c');
            }
        }
    }

    /// <summary>
    /// A foundry line under boulders held by dirt, above an empty pocket that opens on dirt to its right.
    /// </summary>
    private static void Foundry(Layout layout, int x, int y, int w)
    {
        for (var xx = x; xx < x + w; xx++)
        {
            layout.Set(xx, y - 2, 'o');
            layout.Set(xx, y - 1, '.');
            layout.Set(xx, y, 'F');
            layout.Set(xx, y + 1, ' ');
            layout.Set(xx, y + 2, ' ');
            layout.Set(xx, y + 3, '=');
        }

        layout.Set(x - 1, y, '=');
        layout.Set(x + w, y, '=');
        for (var yy = y + 1; yy <= y + 2; yy++)
        {
            layout.Set(x - 1, yy, '=');
            layout.Set(x + w, yy, '.');
        }

        layout.Set(x - 1, y + 3, '=');
        layout.Set(x + w, y + 3, '=');
    }

    private static List<string> ToRows(char[,] grid)
    {
        var map = new List<string>(Height);
        for (var y = 0; y < Height; y++)
        {
            var row = new char[Width];
            for (var x = 0; x < Width; x++)
                row[x] = grid[x, y];
            map.Add(new string(row));
        }

        return map;
    }

    /// <summary>
    /// Digs a dirt tunnel from the miner's corner along the top, then down to the exit.
    /// </summary>
    private static char[,] Tunnel(char[,] grid)
    {
        var exitY = 1;
        for (var y = 1; y < Height - 1; y++)
        {
            if (grid[Width - 2, y] == 'E')
                exitY = y;
        }

        for (var x = 3; x <= Width - 3; x++)
            grid[x, 2] = '.';
        for (var y = 2; y <= exitY; y++)
            grid[Width - 3, y] = '.';
        return grid;
    }
}
