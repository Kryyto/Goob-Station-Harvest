using Content.Server._Harvest.Arcade.AstroMiner;
using NUnit.Framework;

namespace Content.Tests.Server._Harvest.Arcade;

[TestFixture]
public sealed class AstroMinerReachabilityTest
{
    private static AstroMinerReachability.Result Analyze(params string[] map) => AstroMinerReachability.Analyze(map);

    [Test]
    public void DirtAndCrystalsLeadToTheExit()
    {
        var result = Analyze(
            "######",
            "#P.*E#",
            "######");
        Assert.That(result.ExitReachable);
        Assert.That(result.Crystals, Is.EqualTo(1));
    }

    [Test]
    public void BouldersThatCannotMoveWallTheExitOff()
    {
        // Boulders resting on the hull, with no room to be pushed into.
        var result = Analyze(
            "#######",
            "#P.ooE#",
            "#######");
        Assert.That(result.ExitReachable, Is.False);
    }

    [Test]
    public void ABoulderCanBePushedIntoAnEmptyCell()
    {
        var result = Analyze(
            "#######",
            "#P.o E#",
            "#######");
        Assert.That(result.ExitReachable);
    }

    [Test]
    public void ABoulderCanBeDugOutFromBelow()
    {
        // The column of boulders falls once the dirt under it is dug from the side.
        var result = Analyze(
            "#####",
            "#P#E#",
            "#.#o#",
            "#.#o#",
            "#...#",
            "#####");
        Assert.That(result.ExitReachable);
    }

    [Test]
    public void CarpsAndBlobsBlockThePath()
    {
        Assert.That(Analyze("######", "#P.cE#", "######").ExitReachable, Is.False);
        Assert.That(Analyze("######", "#P.bE#", "######").ExitReachable, Is.False);
        Assert.That(Analyze("######", "#P.FE#", "######").ExitReachable, Is.False);
    }
}
