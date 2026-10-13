namespace RouletteShop;

/// <summary>
/// Deadworks writes the defaults to game\bin\win64\configs\RouletteShopPlugin\RouletteShopPlugin.jsonc on first load;
/// after editing it, run dw_reloadconfig RouletteShopPlugin in the server console.
/// </summary>
public class RouletteConfig
{
    /// <summary>Whether spins can come back Corrupted at all.</summary>
    public bool CorruptedEnabled { get; set; } = true;

    /// <summary>Chance, in percent, that a spin landing on an item with a Corrupted version gives the Corrupted one.</summary>
    public double CorruptedChancePercent { get; set; } = 1.0;

    /// <summary>Debug: every spin landing on an item with a Corrupted version gives the Corrupted one.</summary>
    public bool ForceCorrupted { get; set; }

    /// <summary>
    /// Corrupted pity: after this many spins in a row on items with a Corrupted version (3,200 and 6,400) without one,
    /// the next such spin comes back Corrupted. 0 turns it off.
    /// </summary>
    public int CorruptedPitySpins { get; set; } = 10;

    /// <summary>Chance, in percent, that a spin gives an item from the next tier up for the same price. 0 turns it off.</summary>
    public double JackpotChancePercent { get; set; } = 3.0;

    /// <summary>Whether the wildcard box (any category at the chosen price) is offered.</summary>
    public bool WildcardBox { get; set; } = true;

    /// <summary>Whether the roulette plays the game's UI sounds (to the spinning player only).</summary>
    public bool Sounds { get; set; } = true;
}
