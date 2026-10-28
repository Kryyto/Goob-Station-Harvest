using Robust.Shared.Prototypes;

namespace Content.Shared._Harvest.Arcade.AstroMiner;

/// <summary>
/// One cave of AstroMiner. Levels are played in the order of <see cref="Order"/>.
/// </summary>
/// <remarks>
/// Map legend: <c>#</c> hull, <c>=</c> rock wall, <c>.</c> dirt, space for empty, <c>o</c> boulder,
/// <c>*</c> crystal, <c>P</c> miner, <c>E</c> exit, <c>c</c> carp, <c>F</c> foundry, <c>b</c> blob.
/// </remarks>
[Prototype]
public sealed partial class AstroMinerLevelPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField(required: true)]
    public LocId Name;

    [DataField]
    public int Order;

    /// <summary>
    /// Seconds of oxygen the miner starts with.
    /// </summary>
    [DataField]
    public int Oxygen = 120;

    [DataField]
    public int CrystalsNeeded = 10;

    [DataField]
    public int CrystalValue = 10;

    /// <summary>
    /// Value of each crystal collected once the exit is open.
    /// </summary>
    [DataField]
    public int BonusCrystalValue = 15;

    /// <summary>
    /// Dispensed every time this cave is escaped, on top of the cabinet's prizes.
    /// </summary>
    [DataField]
    public EntProtoId? Reward;

    [DataField(required: true)]
    public List<string> Map = new();
}
