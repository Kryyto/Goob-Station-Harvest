using System.Numerics;
using Content.Shared._Harvest.Arcade.AstroMiner;
using Robust.Client.GameObjects;
using Robust.Client.Graphics;
using Robust.Client.UserInterface;
using Robust.Shared.Graphics.RSI;
using Robust.Shared.Timing;
using Robust.Shared.Utility;

namespace Content.Client._Harvest.Arcade.AstroMiner;

/// <summary>
/// Draws the part of the AstroMiner cave around the miner, sliding objects from one cell to the next between ticks.
/// </summary>
public sealed class AstroMinerField : Control
{
    private static readonly ResPath RsiPath = new("/Textures/_Harvest/Arcade/astrominer.rsi");
    private static readonly Vector2[] Directions = [new(0, -1), new(1, 0), new(0, 1), new(-1, 0)];

    /// <summary>
    /// Tiles the view tries to show across and down; the tile size is the largest multiple of the sprite size that fits.
    /// </summary>
    private const float ViewTilesX = 20;
    private const float ViewTilesY = 12;
    private const int SpriteSize = 16;

    /// <summary>
    /// How fast the camera catches up with the miner.
    /// </summary>
    private const float CameraSpeed = 8f;

    private readonly (Texture[] Frames, float Delay)[] _tiles;
    private readonly Texture _empty;
    private readonly (Texture[] Frames, float Delay) _altMiner;

    private int _width;
    private int _height;
    private byte[] _cells = [];
    private byte[] _moves = [];
    private Vector2i _miner;
    private float _tickSeconds = 0.2f;
    private float _sinceUpdate;
    private float _time;
    private Vector2? _camera;
    private bool _useAltMiner;

    public AstroMinerField()
    {
        RectClipContent = true;

        var sprites = IoCManager.Resolve<IEntityManager>().System<SpriteSystem>();
        var tiles = Enum.GetValues<AstroMinerTile>();
        _tiles = new (Texture[], float)[tiles.Length];
        foreach (var tile in tiles)
        {
            _tiles[(int) tile] = LoadState(sprites, StateName(tile));
        }

        _altMiner = LoadState(sprites, "miner_2");

        _empty = _tiles[(int) AstroMinerTile.Empty].Frames[0];
        MinSize = new Vector2(320, 192);
    }

    private static (Texture[] Frames, float Delay) LoadState(SpriteSystem sprites, string name)
    {
        var state = sprites.GetState(new SpriteSpecifier.Rsi(RsiPath, name));
        var frames = state.GetFrames(RsiDirection.South);
        return (frames, frames.Length > 1 ? state.GetDelay(0) : 0f);
    }

    private static string StateName(AstroMinerTile tile)
    {
        return tile switch
        {
            AstroMinerTile.Empty => "empty",
            AstroMinerTile.Dirt => "dirt",
            AstroMinerTile.Wall => "wall",
            AstroMinerTile.Hull => "hull",
            AstroMinerTile.Boulder => "boulder",
            AstroMinerTile.Crystal => "crystal",
            AstroMinerTile.ExitClosed => "exit_closed",
            AstroMinerTile.ExitOpen => "exit_open",
            AstroMinerTile.Miner => "miner",
            AstroMinerTile.Carp => "carp",
            AstroMinerTile.Foundry => "foundry",
            AstroMinerTile.Blob => "blob",
            _ => "explosion",
        };
    }

    public void SetState(AstroMinerStateMessage state)
    {
        // A new cave or a respawn: jump to the miner instead of panning across the cave.
        if (state.Width != _width || state.Height != _height || (state.Miner - _miner).Length > 2)
            _camera = null;

        _width = state.Width;
        _height = state.Height;
        _cells = state.Cells;
        _moves = state.Moves.Length == state.Cells.Length ? state.Moves : new byte[state.Cells.Length];
        _miner = state.Miner;
        _useAltMiner = state.AltMiner;
        _tickSeconds = state.TickSeconds > 0 ? state.TickSeconds : _tickSeconds;
        _sinceUpdate = 0;
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);
        _time += args.DeltaSeconds;
        _sinceUpdate += args.DeltaSeconds;

        if (_width == 0 || TileSize() is not { } size)
            return;

        var target = CameraTarget(size);
        _camera = _camera is { } camera
            ? camera + (target - camera) * MathF.Min(1f, args.DeltaSeconds * CameraSpeed)
            : target;
    }

    private int? TileSize()
    {
        if (PixelWidth <= 0 || PixelHeight <= 0)
            return null;

        var fit = MathF.Min(PixelWidth / ViewTilesX, PixelHeight / ViewTilesY);
        return Math.Max(SpriteSize, (int) (fit / SpriteSize) * SpriteSize);
    }

    /// <summary>
    /// Progress of the current slide, from 0 (objects at their previous cell) to 1.
    /// </summary>
    private float SlideProgress => Math.Clamp(_sinceUpdate / _tickSeconds, 0f, 1f);

    private Vector2 SlideOffset(int index)
    {
        var move = index < _moves.Length ? _moves[index] : 0;
        if (move == 0 || move > Directions.Length)
            return Vector2.Zero;
        return -Directions[move - 1] * (1f - SlideProgress);
    }

    /// <summary>
    /// Top left corner of the view, in tiles, that centers the miner without showing outside the cave.
    /// </summary>
    private Vector2 CameraTarget(int size)
    {
        var view = new Vector2(PixelWidth / (float) size, PixelHeight / (float) size);
        var miner = new Vector2(_miner.X, _miner.Y) + new Vector2(0.5f, 0.5f);
        var minerIndex = _miner.Y * _width + _miner.X;
        if (minerIndex >= 0 && minerIndex < _cells.Length && _cells[minerIndex] == (byte) AstroMinerTile.Miner)
            miner += SlideOffset(minerIndex);

        return new Vector2(Axis(miner.X, view.X, _width), Axis(miner.Y, view.Y, _height));

        static float Axis(float center, float view, int length)
        {
            if (view >= length)
                return (length - view) / 2f;
            return Math.Clamp(center - view / 2f, 0f, length - view);
        }
    }

    protected override void Draw(DrawingHandleScreen handle)
    {
        base.Draw(handle);

        handle.DrawRect(new UIBox2(0, 0, PixelWidth, PixelHeight), Color.Black);
        if (_width == 0 || _height == 0 || _cells.Length < _width * _height || TileSize() is not { } size)
            return;

        var camera = _camera ?? CameraTarget(size);
        var minX = Math.Max(0, (int) MathF.Floor(camera.X) - 1);
        var minY = Math.Max(0, (int) MathF.Floor(camera.Y) - 1);
        var maxX = Math.Min(_width - 1, (int) MathF.Ceiling(camera.X + PixelWidth / (float) size) + 1);
        var maxY = Math.Min(_height - 1, (int) MathF.Ceiling(camera.Y + PixelHeight / (float) size) + 1);

        // Cave floor first, then what stays in place, then what slides over it.
        for (var pass = 0; pass < 3; pass++)
        {
            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    var index = y * _width + x;
                    var tile = _cells[index];
                    if (tile >= _tiles.Length)
                        continue;

                    var offset = SlideOffset(index);
                    var moving = offset != Vector2.Zero;
                    Texture texture;
                    switch (pass)
                    {
                        case 0:
                            texture = _empty;
                            offset = Vector2.Zero;
                            break;
                        case 1 when !moving && tile != (byte) AstroMinerTile.Empty:
                        case 2 when moving:
                            var (frames, delay) = _useAltMiner && tile == (byte) AstroMinerTile.Miner ? _altMiner : _tiles[tile];
                            texture = delay > 0 ? frames[(int) (_time / delay) % frames.Length] : frames[0];
                            break;
                        default:
                            continue;
                    }

                    var position = (new Vector2(x, y) + offset - camera) * size;
                    handle.DrawTextureRect(texture, UIBox2.FromDimensions(Round(position), new Vector2(size, size)));
                }
            }
        }
    }

    private static Vector2 Round(Vector2 v) => new(MathF.Round(v.X), MathF.Round(v.Y));
}
