namespace RouletteShop;

/// <summary>
/// Deadworks writes the defaults to game\bin\win64\configs\RouletteShopPlugin\RouletteShopPlugin.jsonc on first load;
/// after editing it, run dw_reloadconfig RouletteShopPlugin in the server console. The rates can also be changed mid-game
/// with roulette_rate (roulette.admin), until the next reload.
/// </summary>
public class RouletteConfig
{
    /// <summary>
    /// Whether items can be sold to the game's shop (for half their price, as in the normal game), to make room for
    /// upgrades. Off, selling is blocked (the command and, as a backstop, its refund).
    /// </summary>
    public bool SellingEnabled { get; set; } = true;

    /// <summary>Whether spins can come back Corrupted at all.</summary>
    public bool CorruptedEnabled { get; set; } = true;

    /// <summary>Chance, in percent, that a spin landing on an item with a Corrupted version gives the Corrupted one.</summary>
    public double CorruptedChancePercent { get; set; } = 5.0;

    /// <summary>Debug: every spin landing on an item with a Corrupted version gives the Corrupted one.</summary>
    public bool ForceCorrupted { get; set; }

    /// <summary>
    /// Corrupted pity: after this many spins in a row on items with a Corrupted version (3,200 and 6,400) without one,
    /// the next such spin comes back Corrupted. 0 turns it off.
    /// </summary>
    public int CorruptedPitySpins { get; set; } = 10;

    /// <summary>Chance, in percent, that a spin gives an item from the next tier up for the same price. 0 turns it off.</summary>
    public double JackpotChancePercent { get; set; } = 3.0;

    /// <summary>
    /// Chance, in percent, that a spin gives a Legendary (tier 5) of the chosen category instead, for the same price. If the
    /// game won't give it (they are Street Brawl items), the spin lands a normal item. 0 turns it off.
    /// </summary>
    public double LegendaryChancePercent { get; set; } = 1.0;

    /// <summary>
    /// How much likelier an item in the player's build is (the build picked in the shop, and the quickbuy queue). 1 is no
    /// different from any other item.
    /// </summary>
    public double BuildWeight { get; set; } = 3.0;

    /// <summary>Whether the wildcard box (any category at the chosen price) is offered.</summary>
    public bool WildcardBox { get; set; } = true;

    /// <summary>Whether the roulette plays the game's UI sounds (to the spinning player only).</summary>
    public bool Sounds { get; set; } = true;

    /// <summary>Spinning works only in a shop zone, like buying (the hero's m_bInItemShopZone). The sheet still opens anywhere.</summary>
    public bool SpinOnlyAtShop { get; set; } = true;

    /// <summary>
    /// Backstop for purchases that get past the buy block (quickbuy auto-buy, say): their soul charge is stopped and the item
    /// they gave is taken back.
    /// </summary>
    public bool BlockOtherPurchases { get; set; } = true;

    /// <summary>Everyone sees a chat line when someone lands a Corrupted, Legendary or jackpot item.</summary>
    public bool ShoutOuts { get; set; } = true;

    /// <summary>Kills in a row (without dying) that earn a free spin at the chosen price. 0 turns it off.</summary>
    public int FreeSpinKillStreak { get; set; } = 3;

    /// <summary>Lucky hour: about every this many minutes, Corrupted and Legendary chances are multiplied. 0 turns it off.</summary>
    public double LuckyHourEveryMinutes { get; set; } = 10;

    /// <summary>How long a lucky hour lasts, in minutes.</summary>
    public double LuckyHourMinutes { get; set; } = 2;

    /// <summary>What a lucky hour multiplies the Corrupted and Legendary chances by.</summary>
    public double LuckyHourMultiplier { get; set; } = 2;

    /// <summary>Double or nothing after a spin: the chance, in percent, that the item becomes one from the tier above (else it's gone). 0 turns it off.</summary>
    public double DoubleOrNothingWinPercent { get; set; } = 50;

    /// <summary>A reroll (give back the last spin's item and spin again) costs this percent of the spin price. 0 turns it off.</summary>
    public double RerollCostPercent { get; set; } = 50;

    /// <summary>Death timers are this percent of the game's own (the user: "make it so death timers are half as long"). 100 leaves them alone.</summary>
    public double RespawnTimePercent { get; set; } = 50;
}
