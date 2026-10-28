using System.Linq;
using Content.Server._Harvest.Arcade.AstroMiner;
using NUnit.Framework;

namespace Content.Tests.Server._Harvest.Arcade;

[TestFixture]
public sealed class AstroMinerCaveGeneratorTest
{
    [Test]
    public void CavesArePlayableAtEveryDepth()
    {
        for (var depth = 1; depth <= 30; depth++)
        {
            for (var seed = 0; seed < 20; seed++)
            {
                var cave = AstroMinerCaveGenerator.Generate(depth, seed * 7919 + depth);
                var map = cave.Map;
                var name = $"depth {depth}, seed {seed}";

                Assert.That(map, Has.Count.EqualTo(AstroMinerCaveGenerator.Height), name);
                Assert.That(map.All(r => r.Length == AstroMinerCaveGenerator.Width), name);
                Assert.That(map[0].All(c => c == '#') && map[^1].All(c => c == '#')
                            && map.All(r => r[0] == '#' && r[^1] == '#'), $"{name}: the hull must enclose the cave");

                var text = string.Concat(map);
                Assert.That(text.Count(c => c == 'P'), Is.EqualTo(1), name);
                Assert.That(text.Count(c => c == 'E'), Is.EqualTo(1), name);
                var reach = AstroMinerReachability.Analyze(map);
                Assert.That(reach.ExitReachable, $"{name}: the exit cannot be reached");
                Assert.That(reach.Crystals, Is.GreaterThanOrEqualTo(cave.CrystalsNeeded), $"{name}: not enough crystals can be reached");
                Assert.That(cave.CrystalsNeeded, Is.Positive, name);
                Assert.That(cave.Oxygen, Is.GreaterThanOrEqualTo(90), name);

                var sim = new AstroMinerSimulation(map, cave.Oxygen, cave.CrystalsNeeded, cave.CrystalValue, cave.BonusCrystalValue);
                for (var i = 0; i < 30; i++)
                    sim.Tick();
            }
        }
    }

    [Test]
    public void SameSeedGivesTheSameCave()
    {
        var first = AstroMinerCaveGenerator.Generate(5, 42);
        var again = AstroMinerCaveGenerator.Generate(5, 42);
        var other = AstroMinerCaveGenerator.Generate(5, 43);
        Assert.That(again.Map, Is.EqualTo(first.Map));
        Assert.That(other.Map, Is.Not.EqualTo(first.Map));
    }

    [Test]
    public void ColumnsNextToTheExitAreNotPlainDirt()
    {
        for (var seed = 0; seed < 5; seed++)
        {
            var map = AstroMinerCaveGenerator.Generate(3, seed).Map;
            var right = string.Concat(map.Skip(1).SkipLast(1).Select(r => r.Substring(AstroMinerCaveGenerator.Width - 4, 3)));
            Assert.That(right.Any(c => c is 'o' or '*' or ' '), $"seed {seed}: the columns next to the exit are only dirt");
        }
    }

    [Test]
    public void DeepCavesHaveStructures()
    {
        var text = string.Concat(Enumerable.Range(0, 20).SelectMany(seed => AstroMinerCaveGenerator.Generate(8, seed).Map));
        foreach (var block in new[] { 'c', 'F', 'b' })
            Assert.That(text, Does.Contain(block.ToString()), $"no '{block}' in 20 caves at depth 8");
    }

    [Test]
    public void DeeperCavesAreHarder()
    {
        var shallow = AstroMinerCaveGenerator.Generate(1, 1);
        var deep = AstroMinerCaveGenerator.Generate(12, 1);
        Assert.That(deep.Oxygen, Is.LessThan(shallow.Oxygen));
        Assert.That(deep.CrystalValue, Is.GreaterThan(shallow.CrystalValue));
    }
}
