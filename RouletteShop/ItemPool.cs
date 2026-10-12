using System.Text.Json;

namespace RouletteShop;

public enum ItemCategory { Weapon, Vitality, Spirit }

/// <param name="Corruptible">Has a Corrupted version (corrupted_info in the item data; tiers 3 and 4).</param>
/// <param name="GameIcon">The game's own shop icon, e.g. file://{images}/items/weapon/basic_magazine.psd.</param>
/// <param name="Active">An active item (the shop draws its icon round).</param>
/// <param name="Imbue">Has to be imbued into an ability.</param>
public sealed record RouletteItem(string ClassName, string Name, ItemCategory Category, int Tier, bool Corruptible,
                                  string GameIcon, bool Active, bool Imbue);

/// <summary>
/// The items a spin can land on. Read from the embedded manifest.json, which fetch-deadlock-assets.mjs writes from
/// items-full.json: shopable, not disabled, tier 1-4 (Street Brawl Legendaries are tier 5).
/// </summary>
public sealed class ItemPool
{
    /// <summary>Spin price per tier (index 0 is tier 1). Roulette charges the flat tier price.</summary>
    public static readonly int[] TierPrices = [800, 1600, 3200, 6400];

    private readonly ILookup<(ItemCategory Category, int Tier), RouletteItem> _items;

    private ItemPool(List<RouletteItem> items)
    {
        Count = items.Count;
        CorruptibleCount = items.Count(i => i.Corruptible);
        _items = items.ToLookup(i => (i.Category, i.Tier));
    }

    public int Count { get; }

    public int CorruptibleCount { get; }

    public RouletteItem[] Get(ItemCategory category, int tier) => _items[(category, tier)].ToArray();

    /// <summary>Item counts per category for tiers 1-4, e.g. "weapon 7/16/19/11".</summary>
    public string Summary() => string.Join(", ", Enum.GetValues<ItemCategory>().Select(c =>
        $"{c.ToString().ToLowerInvariant()} {string.Join('/', Enumerable.Range(1, TierPrices.Length).Select(t => _items[(c, t)].Count()))}"));

    public static ItemPool Load()
    {
        using var stream = typeof(ItemPool).Assembly.GetManifestResourceStream("manifest.json")
            ?? throw new InvalidOperationException("manifest.json is not embedded in the plugin");
        using var doc = JsonDocument.Parse(stream);

        var items = new List<RouletteItem>();
        foreach (var e in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            items.Add(new RouletteItem(
                e.GetProperty("class_name").GetString()!,
                e.GetProperty("name").GetString()!,
                Enum.Parse<ItemCategory>(e.GetProperty("slot").GetString()!, ignoreCase: true),
                e.GetProperty("tier").GetInt32(),
                e.GetProperty("corruptible").GetBoolean(),
                e.GetProperty("game_icon").GetString()!,
                e.GetProperty("active").GetBoolean(),
                e.GetProperty("imbue").GetBoolean()));
        }
        return new ItemPool(items);
    }
}
