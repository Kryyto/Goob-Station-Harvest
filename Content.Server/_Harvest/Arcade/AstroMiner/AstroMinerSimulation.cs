using Content.Shared._Harvest.Arcade.AstroMiner;

namespace Content.Server._Harvest.Arcade.AstroMiner;

/// <summary>
/// One attempt at one AstroMiner cave: falling boulders and crystals, carps following the walls,
/// and the miner digging its way to the exit. Has no dependency on entities so it can be unit tested.
/// </summary>
public sealed class AstroMinerSimulation
{
    public const int TicksPerSecond = 6;
    public const float TickSeconds = 1f / TicksPerSecond;

    /// <summary>
    /// Ticks an explosion stays on screen before leaving its result behind.
    /// </summary>
    public const int ExplosionTicks = 4;

    /// <summary>
    /// Ticks the miner must keep pushing a boulder before it moves.
    /// </summary>
    public const int PushTicks = 2;

    /// <summary>
    /// Chance per tick for each blob cell to spread to a neighbor.
    /// </summary>
    public const double BlobGrowChance = 0.03;

    /// <summary>
    /// A blob that grows past this many cells hardens into boulders.
    /// </summary>
    public const int BlobMaxSize = 80;

    private static readonly Vector2i[] Directions = [new(0, -1), new(1, 0), new(0, 1), new(-1, 0)];

    public readonly int Width;
    public readonly int Height;

    private readonly AstroMinerTile[] _cells;
    /// <summary>The object moved down or rolled last tick: it crushes what it lands on.</summary>
    private readonly bool[] _falling;
    /// <summary>A resting object lost its support last tick: it falls next tick unless the cell below is filled again.</summary>
    private readonly bool[] _unstable;
    private readonly bool[] _moved;
    /// <summary>Index in <see cref="Directions"/> a carp is heading to.</summary>
    private readonly byte[] _carpDirection;
    private readonly byte[] _explosionTimer;
    private readonly AstroMinerTile[] _explosionResult;
    /// <summary>1 + index in <see cref="Directions"/> of the move that brought the cell's content there this tick, 0 if none.</summary>
    private readonly byte[] _moves;

    public readonly int CrystalsNeeded;
    public readonly int CrystalValue;
    public readonly int BonusCrystalValue;

    public Vector2i MinerPosition { get; private set; }
    public int Crystals { get; private set; }
    /// <summary>Points earned in this cave.</summary>
    public int Score { get; private set; }
    public int Oxygen { get; private set; }
    public bool ExitOpen { get; private set; }
    public bool Escaped { get; private set; }
    public bool Dead { get; private set; }

    /// <summary>Direction the miner wants to go, null to stand still.</summary>
    public Vector2i? Input;

    /// <summary>Dig or collect next to the miner without moving.</summary>
    public bool Grab;

    private readonly Random _random;
    private int _pushTicks;
    private int _tick;

    /// <param name="seed">Drives the blob's growth, so a cave plays the same for the same moves.</param>
    public AstroMinerSimulation(IReadOnlyList<string> map, int oxygen, int crystalsNeeded, int crystalValue, int bonusCrystalValue, int seed = 0)
    {
        _random = new Random(seed);
        Height = map.Count;
        Width = 0;
        foreach (var row in map)
            Width = Math.Max(Width, row.Length);

        var size = Width * Height;
        _cells = new AstroMinerTile[size];
        _falling = new bool[size];
        _unstable = new bool[size];
        _moved = new bool[size];
        _carpDirection = new byte[size];
        _explosionTimer = new byte[size];
        _explosionResult = new AstroMinerTile[size];
        _moves = new byte[size];

        Oxygen = oxygen;
        CrystalsNeeded = crystalsNeeded;
        CrystalValue = crystalValue;
        BonusCrystalValue = bonusCrystalValue;

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var c = x < map[y].Length ? map[y][x] : '#';
                var tile = c switch
                {
                    ' ' => AstroMinerTile.Empty,
                    '.' => AstroMinerTile.Dirt,
                    '=' => AstroMinerTile.Wall,
                    'o' => AstroMinerTile.Boulder,
                    '*' => AstroMinerTile.Crystal,
                    'E' => AstroMinerTile.ExitClosed,
                    'P' => AstroMinerTile.Miner,
                    'c' => AstroMinerTile.Carp,
                    'F' => AstroMinerTile.Foundry,
                    'b' => AstroMinerTile.Blob,
                    _ => AstroMinerTile.Hull,
                };
                _cells[Index(x, y)] = tile;
                if (tile == AstroMinerTile.Miner)
                    MinerPosition = new Vector2i(x, y);
                else if (tile == AstroMinerTile.Carp)
                    _carpDirection[Index(x, y)] = 3;
            }
        }

        if (CrystalsNeeded <= 0)
            OpenExit();
    }

    public AstroMinerTile this[int x, int y] => InBounds(x, y) ? _cells[Index(x, y)] : AstroMinerTile.Hull;

    public AstroMinerTile this[Vector2i pos] => this[pos.X, pos.Y];

    /// <summary>
    /// Copies the field, one byte per cell, row by row.
    /// </summary>
    public byte[] GetCells()
    {
        var bytes = new byte[_cells.Length];
        for (var i = 0; i < _cells.Length; i++)
            bytes[i] = (byte) _cells[i];
        return bytes;
    }

    /// <summary>
    /// Copies the moves of the last tick, see <see cref="AstroMinerStateMessage.Moves"/>.
    /// </summary>
    public byte[] GetMoves() => (byte[]) _moves.Clone();

    public AstroMinerEvents Tick()
    {
        var events = AstroMinerEvents.None;
        Array.Clear(_moves);
        if (Escaped)
            return events;

        Array.Clear(_moved);

        if (!Dead)
            events |= MoveMiner();

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var i = Index(x, y);
                if (_moved[i])
                    continue;

                switch (_cells[i])
                {
                    case AstroMinerTile.Boulder:
                    case AstroMinerTile.Crystal:
                        events |= UpdateFalling(x, y);
                        break;
                    case AstroMinerTile.Carp:
                        events |= UpdateCarp(x, y);
                        break;
                    case AstroMinerTile.Explosion:
                        if (--_explosionTimer[i] == 0)
                            _cells[i] = _explosionResult[i];
                        break;
                }
            }
        }

        events |= UpdateBlob();

        if (++_tick % TicksPerSecond == 0 && !Dead && Oxygen > 0 && --Oxygen == 0)
            events |= Explode(MinerPosition, AstroMinerTile.Empty);

        return events;
    }

    private AstroMinerEvents MoveMiner()
    {
        if (Input is not { } dir)
        {
            _pushTicks = 0;
            return AstroMinerEvents.None;
        }

        var target = MinerPosition + dir;
        var events = AstroMinerEvents.None;
        switch (this[target])
        {
            case AstroMinerTile.Empty:
                break;
            case AstroMinerTile.Dirt:
                events |= AstroMinerEvents.Dug;
                break;
            case AstroMinerTile.Crystal:
                Crystals++;
                Score += ExitOpen ? BonusCrystalValue : CrystalValue;
                events |= AstroMinerEvents.CrystalCollected;
                if (!ExitOpen && Crystals >= CrystalsNeeded)
                {
                    OpenExit();
                    events |= AstroMinerEvents.ExitOpened;
                }
                break;
            case AstroMinerTile.ExitOpen:
                if (Grab)
                    return events;
                Escaped = true;
                _cells[Index(MinerPosition)] = AstroMinerTile.Empty;
                MinerPosition = target;
                return events | AstroMinerEvents.Escaped;
            case AstroMinerTile.Boulder when dir.Y == 0:
                var beyond = target + dir;
                if (this[beyond] != AstroMinerTile.Empty || _falling[Index(target)])
                    return events;
                if (++_pushTicks < PushTicks)
                    return events;
                _pushTicks = 0;
                var b = Index(beyond);
                _cells[b] = AstroMinerTile.Boulder;
                _falling[b] = _unstable[b] = false;
                _moved[b] = true;
                _moves[b] = DirectionCode(dir);
                break;
            case AstroMinerTile.Carp:
                return events | Explode(MinerPosition, AstroMinerTile.Empty);
            default:
                _pushTicks = 0;
                return events;
        }

        var t = Index(target);
        if (Grab)
        {
            _cells[t] = AstroMinerTile.Empty;
            return events;
        }

        _cells[Index(MinerPosition)] = AstroMinerTile.Empty;
        _cells[t] = AstroMinerTile.Miner;
        _falling[t] = _unstable[t] = false;
        _moved[t] = true;
        _moves[t] = DirectionCode(dir);
        MinerPosition = target;
        return events;
    }

    private AstroMinerEvents UpdateFalling(int x, int y)
    {
        var i = Index(x, y);
        var below = this[x, y + 1];

        if (below == AstroMinerTile.Empty)
        {
            // A resting object waits a tick before falling, so the miner can step back under it.
            if (!_falling[i] && !_unstable[i])
            {
                _unstable[i] = true;
                return AstroMinerEvents.None;
            }

            MoveObject(x, y, x, y + 1);
            return AstroMinerEvents.None;
        }

        _unstable[i] = false;

        if (_falling[i] && below == AstroMinerTile.Miner)
            return Explode(new Vector2i(x, y + 1), AstroMinerTile.Empty);

        if (_falling[i] && below == AstroMinerTile.Carp)
            return Explode(new Vector2i(x, y + 1), AstroMinerTile.Crystal);

        // A falling boulder goes through the foundry and comes out below as a crystal.
        if (_falling[i] && below == AstroMinerTile.Foundry && _cells[i] == AstroMinerTile.Boulder
            && this[x, y + 2] == AstroMinerTile.Empty)
        {
            var o = Index(x, y + 2);
            _cells[i] = AstroMinerTile.Empty;
            _falling[i] = false;
            _cells[o] = AstroMinerTile.Crystal;
            _falling[o] = true;
            _unstable[o] = false;
            _moved[o] = true;
            return AstroMinerEvents.Transformed;
        }

        if (below is AstroMinerTile.Boulder or AstroMinerTile.Crystal or AstroMinerTile.Wall)
        {
            if (this[x - 1, y] == AstroMinerTile.Empty && this[x - 1, y + 1] == AstroMinerTile.Empty)
            {
                MoveObject(x, y, x - 1, y);
                return AstroMinerEvents.None;
            }

            if (this[x + 1, y] == AstroMinerTile.Empty && this[x + 1, y + 1] == AstroMinerTile.Empty)
            {
                MoveObject(x, y, x + 1, y);
                return AstroMinerEvents.None;
            }
        }

        if (!_falling[i])
            return AstroMinerEvents.None;

        _falling[i] = false;
        return AstroMinerEvents.Landed;
    }

    private void MoveObject(int fromX, int fromY, int toX, int toY)
    {
        var from = Index(fromX, fromY);
        var to = Index(toX, toY);
        _cells[to] = _cells[from];
        _cells[from] = AstroMinerTile.Empty;
        _falling[from] = _unstable[from] = false;
        _falling[to] = true;
        _unstable[to] = false;
        _moved[to] = true;
        _moves[to] = DirectionCode(new Vector2i(toX - fromX, toY - fromY));
    }

    /// <summary>
    /// Carps keep a wall on their left: turn left when possible, else go straight, else turn right.
    /// </summary>
    private AstroMinerEvents UpdateCarp(int x, int y)
    {
        var pos = new Vector2i(x, y);
        foreach (var dir in Directions)
        {
            if (this[pos + dir] is AstroMinerTile.Miner or AstroMinerTile.Blob)
                return Explode(pos, AstroMinerTile.Empty);
        }

        var i = Index(x, y);
        var heading = _carpDirection[i];
        var left = (byte) ((heading + 3) % 4);

        if (this[pos + Directions[left]] == AstroMinerTile.Empty)
            heading = left;
        else if (this[pos + Directions[heading]] != AstroMinerTile.Empty)
        {
            _carpDirection[i] = (byte) ((heading + 1) % 4);
            return AstroMinerEvents.None;
        }

        var target = pos + Directions[heading];
        var t = Index(target);
        _cells[t] = AstroMinerTile.Carp;
        _cells[i] = AstroMinerTile.Empty;
        _carpDirection[t] = heading;
        _moved[t] = true;
        _moves[t] = (byte) (heading + 1);
        return AstroMinerEvents.None;
    }

    /// <summary>
    /// The blob spreads at random into empty cells and dirt. Once no cell of it can spread, it all turns into crystals;
    /// past <see cref="BlobMaxSize"/> cells, into boulders.
    /// </summary>
    private AstroMinerEvents UpdateBlob()
    {
        var size = 0;
        var canGrow = false;
        for (var i = 0; i < _cells.Length; i++)
        {
            if (_cells[i] != AstroMinerTile.Blob || _moved[i])
                continue;

            size++;
            var pos = new Vector2i(i % Width, i / Width);
            foreach (var dir in Directions)
            {
                if (this[pos + dir] is AstroMinerTile.Empty or AstroMinerTile.Dirt)
                    canGrow = true;
            }

            if (_random.NextDouble() >= BlobGrowChance)
                continue;

            var target = pos + Directions[_random.Next(Directions.Length)];
            if (this[target] is not (AstroMinerTile.Empty or AstroMinerTile.Dirt))
                continue;

            var t = Index(target);
            _cells[t] = AstroMinerTile.Blob;
            _falling[t] = _unstable[t] = false;
            _moved[t] = true;
            size++;
        }

        if (size == 0 || (canGrow && size <= BlobMaxSize))
            return AstroMinerEvents.None;

        var result = canGrow ? AstroMinerTile.Boulder : AstroMinerTile.Crystal;
        for (var i = 0; i < _cells.Length; i++)
        {
            if (_cells[i] != AstroMinerTile.Blob)
                continue;
            _cells[i] = result;
            _falling[i] = _unstable[i] = false;
        }

        return result == AstroMinerTile.Crystal ? AstroMinerEvents.Transformed : AstroMinerEvents.None;
    }

    /// <summary>
    /// Blows up the 3x3 square around <paramref name="center"/>, leaving <paramref name="result"/> behind.
    /// The hull and the exit resist.
    /// </summary>
    private AstroMinerEvents Explode(Vector2i center, AstroMinerTile result)
    {
        var events = AstroMinerEvents.Explosion;
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var x = center.X + dx;
                var y = center.Y + dy;
                if (!InBounds(x, y))
                    continue;

                var i = Index(x, y);
                switch (_cells[i])
                {
                    case AstroMinerTile.Hull:
                    case AstroMinerTile.ExitClosed:
                    case AstroMinerTile.ExitOpen:
                        continue;
                    case AstroMinerTile.Miner:
                        if (!Dead)
                            events |= AstroMinerEvents.MinerDied;
                        Dead = true;
                        break;
                }

                _cells[i] = AstroMinerTile.Explosion;
                _explosionTimer[i] = ExplosionTicks;
                _explosionResult[i] = result;
                _falling[i] = _unstable[i] = false;
                _moved[i] = true;
                _moves[i] = 0;
            }
        }

        return events;
    }

    private void OpenExit()
    {
        ExitOpen = true;
        for (var i = 0; i < _cells.Length; i++)
        {
            if (_cells[i] == AstroMinerTile.ExitClosed)
                _cells[i] = AstroMinerTile.ExitOpen;
        }
    }

    private static byte DirectionCode(Vector2i dir)
    {
        return (byte) (Array.IndexOf(Directions, dir) + 1);
    }

    private bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    private int Index(int x, int y) => y * Width + x;

    private int Index(Vector2i pos) => Index(pos.X, pos.Y);
}
