using System.Collections.Generic;
using Content.Server._Harvest.Arcade.AstroMiner;
using Content.Shared._Harvest.Arcade.AstroMiner;
using NUnit.Framework;
using Robust.Shared.Maths;

namespace Content.Tests.Server._Harvest.Arcade;

[TestFixture]
public sealed class AstroMinerSimulationTest
{
    private static readonly Vector2i Left = new(-1, 0);
    private static readonly Vector2i Right = new(1, 0);

    private static AstroMinerSimulation Create(int crystalsNeeded, params string[] map)
    {
        return new AstroMinerSimulation(map, 100, crystalsNeeded, 10, 15);
    }

    private static void Ticks(AstroMinerSimulation sim, int count)
    {
        for (var i = 0; i < count; i++)
            sim.Tick();
    }

    [Test]
    public void BoulderFallsIntoEmptySpace()
    {
        var sim = Create(1,
            "#####",
            "#Po*#",
            "#  E#",
            "#####");

        // A resting boulder waits one tick before falling.
        sim.Tick();
        Assert.That(sim[2, 1], Is.EqualTo(AstroMinerTile.Boulder));

        sim.Tick();
        Assert.That(sim[2, 1], Is.EqualTo(AstroMinerTile.Empty));
        Assert.That(sim[2, 2], Is.EqualTo(AstroMinerTile.Boulder));
        Assert.That(sim.GetMoves()[2 * sim.Width + 2], Is.EqualTo(3), "the client slides it down from the cell above");

        Assert.That(sim.Tick().HasFlag(AstroMinerEvents.Landed));
    }

    [Test]
    public void BoulderRestsOnDirtAndRollsOffBoulders()
    {
        var sim = Create(1,
            "######",
            "#P o*#",
            "#  o.#",
            "#  ..#",
            "#E...#",
            "######");

        // The top boulder rolls left off the lower one, then falls.
        Ticks(sim, 3);
        Assert.That(sim[3, 1], Is.EqualTo(AstroMinerTile.Empty));
        Assert.That(sim[2, 3], Is.EqualTo(AstroMinerTile.Boulder));
        // The lower one stays on its dirt.
        Assert.That(sim[3, 2], Is.EqualTo(AstroMinerTile.Boulder));
    }

    [Test]
    public void RestingBoulderDoesNotCrushTheMinerButAFallingOneDoes()
    {
        var sim = Create(1,
            "#####",
            "# o*#",
            "# P #",
            "#.  #",
            "#E  #",
            "#####");

        Ticks(sim, 5);
        Assert.That(sim.Dead, Is.False, "a boulder resting on the miner must not kill it");

        // Stepping aside frees the cell: the boulder falls into it a tick later.
        sim.Input = Left;
        sim.Tick();
        sim.Input = null;
        Assert.That(sim.MinerPosition, Is.EqualTo(new Vector2i(1, 2)));
        Assert.That(sim[2, 1], Is.EqualTo(AstroMinerTile.Boulder));
        sim.Tick();
        Assert.That(sim[2, 2], Is.EqualTo(AstroMinerTile.Boulder));

        // A boulder already falling when it reaches the miner is deadly.
        var sim2 = Create(1,
            "#####",
            "# o #",
            "#   #",
            "# P #",
            "#*.E#",
            "#####");
        Ticks(sim2, 3);
        Assert.That(sim2.Dead, Is.True);
        Assert.That(sim2[2, 3], Is.EqualTo(AstroMinerTile.Explosion));
    }

    [Test]
    public void MinerCanStepOutAndBackUnderABoulder()
    {
        var sim = Create(1,
            "#####",
            "# o*#",
            "# P #",
            "#...#",
            "#E..#",
            "#####");

        for (var i = 0; i < 6; i++)
        {
            sim.Input = i % 2 == 0 ? Left : Right;
            sim.Tick();
        }

        Assert.That(sim.Dead, Is.False);
        Assert.That(sim.MinerPosition, Is.EqualTo(new Vector2i(2, 2)));
        Assert.That(sim[2, 1], Is.EqualTo(AstroMinerTile.Boulder));
    }

    [Test]
    public void DiggingRaisesAnEvent()
    {
        var sim = Create(1,
            "#####",
            "#P.E#",
            "#####");

        sim.Input = Right;
        Assert.That(sim.Tick().HasFlag(AstroMinerEvents.Dug));
    }

    [Test]
    public void CollectingCrystalsOpensTheExitAndTheMinerEscapes()
    {
        var sim = Create(2,
            "#######",
            "#P**.E#",
            "#######");

        sim.Input = Right;
        sim.Tick();
        Assert.That(sim.Crystals, Is.EqualTo(1));
        Assert.That(sim.ExitOpen, Is.False);

        var events = sim.Tick();
        Assert.That(events.HasFlag(AstroMinerEvents.ExitOpened));
        Assert.That(sim[5, 1], Is.EqualTo(AstroMinerTile.ExitOpen));
        Assert.That(sim.Score, Is.EqualTo(20));

        Ticks(sim, 2);
        Assert.That(sim.Escaped, Is.True);
    }

    [Test]
    public void GrabCollectsWithoutMoving()
    {
        var sim = Create(1,
            "#####",
            "#P*E#",
            "#####");

        sim.Input = Right;
        sim.Grab = true;
        sim.Tick();
        Assert.That(sim.MinerPosition, Is.EqualTo(new Vector2i(1, 1)));
        Assert.That(sim.Crystals, Is.EqualTo(1));
        Assert.That(sim[2, 1], Is.EqualTo(AstroMinerTile.Empty));
    }

    [Test]
    public void MinerPushesABoulderAfterAMoment()
    {
        var sim = Create(1,
            "######",
            "#Po *#",
            "#...E#",
            "######");

        sim.Input = Right;
        sim.Tick();
        Assert.That(sim[2, 1], Is.EqualTo(AstroMinerTile.Boulder));
        sim.Tick();
        Assert.That(sim[3, 1], Is.EqualTo(AstroMinerTile.Boulder));
        Assert.That(sim.MinerPosition, Is.EqualTo(new Vector2i(2, 1)));
    }

    [Test]
    public void CarpFollowsWallsAndExplodesNextToTheMiner()
    {
        var sim = Create(1,
            "#######",
            "#c   .#",
            "#.....#",
            "#P*..E#",
            "#######");

        // Heading left into the hull with dirt below, it turns up, then right, then swims right.
        Ticks(sim, 3);
        Assert.That(sim[2, 1], Is.EqualTo(AstroMinerTile.Carp));
        Assert.That(sim[1, 1], Is.EqualTo(AstroMinerTile.Empty));

        var sim2 = Create(1,
            "######",
            "#P c #",
            "#...*#",
            "#...E#",
            "######");
        Ticks(sim2, 3);
        Assert.That(sim2.Dead, Is.True);
    }

    [Test]
    public void BoulderDroppedOnACarpLeavesCrystals()
    {
        var sim = Create(1,
            "#######",
            "#  o  #",
            "#     #",
            "#==c==#",
            "#=====#",
            "#P...E#",
            "#######");

        // The carp is boxed in and turns on the spot; the boulder falls onto it.
        var exploded = false;
        for (var i = 0; i < 4 && !exploded; i++)
            exploded = sim.Tick().HasFlag(AstroMinerEvents.Explosion);

        Assert.That(exploded, Is.True);
        Ticks(sim, AstroMinerSimulation.ExplosionTicks + 1);

        var crystals = 0;
        for (var y = 0; y < sim.Height; y++)
        for (var x = 0; x < sim.Width; x++)
        {
            if (sim[x, y] == AstroMinerTile.Crystal)
                crystals++;
        }

        Assert.That(crystals, Is.GreaterThan(0));
        Assert.That(sim.Dead, Is.False);
    }

    [Test]
    public void RunningOutOfOxygenKillsTheMiner()
    {
        var sim = new AstroMinerSimulation(["####", "#P*E", "####"], 2, 1, 10, 15);
        Ticks(sim, AstroMinerSimulation.TicksPerSecond * 2);
        Assert.That(sim.Dead, Is.True);
        Assert.That(sim.Oxygen, Is.EqualTo(0));
    }

    private static int Count(AstroMinerSimulation sim, AstroMinerTile tile)
    {
        var count = 0;
        for (var y = 0; y < sim.Height; y++)
        for (var x = 0; x < sim.Width; x++)
        {
            if (sim[x, y] == tile)
                count++;
        }

        return count;
    }

    [Test]
    public void FoundryTurnsAFallingBoulderIntoACrystal()
    {
        var sim = Create(1,
            "#####",
            "# o #",
            "#   #",
            "# F #",
            "#   #",
            "#P E#",
            "#####");

        var transformed = false;
        for (var i = 0; i < 5 && !transformed; i++)
            transformed = sim.Tick().HasFlag(AstroMinerEvents.Transformed);

        Assert.That(transformed);
        Assert.That(Count(sim, AstroMinerTile.Boulder), Is.Zero);
        Assert.That(Count(sim, AstroMinerTile.Crystal), Is.EqualTo(1));
        Assert.That(sim[2, 3], Is.EqualTo(AstroMinerTile.Foundry));
    }

    [Test]
    public void FoundryWithNoRoomBelowHoldsTheBoulder()
    {
        var sim = Create(1,
            "#####",
            "# o #",
            "#   #",
            "# F #",
            "#P.E#",
            "#####");

        Ticks(sim, 6);
        Assert.That(sim[2, 2], Is.EqualTo(AstroMinerTile.Boulder));
        Assert.That(Count(sim, AstroMinerTile.Crystal), Is.Zero);
    }

    [Test]
    public void BoxedInBlobTurnsIntoCrystals()
    {
        var sim = Create(1,
            "#####",
            "#=b=#",
            "#===#",
            "#P E#",
            "#####");

        Assert.That(sim.Tick().HasFlag(AstroMinerEvents.Transformed));
        Assert.That(sim[2, 1], Is.EqualTo(AstroMinerTile.Crystal));
    }

    [Test]
    public void BlobSpreadsThroughDirtAndHardensWhenTooBig()
    {
        var rows = new List<string> { new('#', 14) };
        for (var y = 0; y < 11; y++)
            rows.Add(y == 5 ? "#......b.....#" : "#............#");
        rows.Add("#P==========E#");
        rows.Add(new('#', 14));
        var sim = new AstroMinerSimulation(rows, 10_000, 1, 10, 15, seed: 3);

        var grew = false;
        for (var i = 0; i < 5000 && Count(sim, AstroMinerTile.Boulder) == 0; i++)
        {
            sim.Tick();
            grew |= Count(sim, AstroMinerTile.Blob) > 1;
        }

        Assert.That(grew, "the blob never spread");
        Assert.That(Count(sim, AstroMinerTile.Blob), Is.Zero);
        Assert.That(Count(sim, AstroMinerTile.Boulder), Is.GreaterThan(AstroMinerSimulation.BlobMaxSize));
    }

    [Test]
    public void CarpExplodesNextToTheBlob()
    {
        var sim = Create(1,
            "######",
            "#cb==#",
            "#====#",
            "#P..E#",
            "######");

        Assert.That(sim.Tick().HasFlag(AstroMinerEvents.Explosion));
    }
}
