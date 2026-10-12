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
}
