using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;

namespace RouletteShop;

/// <summary>
/// The roulette panel, built on the server the way the Deadworks TicTacToe example builds its board: players need only the
/// Deadworks client bootstrap, no addon. It is the roulette sheet of mockups/catalog.html (design #3, approved by the user):
/// one sheet that sits beside the game's own shop, which stays open. Masthead, category and price pickers (the server
/// can't see the game's tabs, so clicking an item in the game's shop or these buttons picks the box; a fourth button is
/// the wildcard box, any category), a vertical reel,
/// the soul count, the result, the SPIN button and the fine print. Art and fonts are the game's own: every path is in the
/// game's pak01 (pak01_dir.txt) or used by its shop stylesheets, and "Retail Demo" / "Radiance" are font families its
/// stylesheets name. Textures as the shop uses them: its price stickers (pricetag_tierN, citadel_shop_mods_filtered.css),
/// its printed category labels and brush-stroke headers, paper wear on every card (citadel_shop_mod_view.css), worn tape,
/// slate, and the speckled ink of its text (opacity-mask text_speckle_mask_main).
/// </summary>
public static class RoulettePanel
{
    public const string Id = "roulette";

    // Where the sheet goes. The game's shop (citadel_hud_hero_shop.css) puts its 1200-wide item page on the right and the
    // hero, souls and recent purchases on the left; the page's left edge is about 325 (16:9) to 350 (21:9) left of the
    // screen's middle, 20 from the top, 960 tall. The sheet sits at the screen's right edge (the user: "move the roulette
    // to the right where theres an empty space", so it doesn't cover the build/quickbuy list on the left). On 21:9 the
    // gap right of the page is about 460 wide (their screenshot); on 16:9 there is none, and the sheet covers the
    // page's right end. Its size is the mockup's sheet (346x748) scaled to the page's height.
    public const int SheetWidth = 444, SheetHeight = 960, SheetTop = 20, SheetRight = 8;

    // Reel geometry: five cards on show, the middle one wins (mockup STEP and WIN, at this scale). The spin is about 2 s
    // in all (the user's choice): TurnDelayMs for the strip to arrive, then SpinMs of turning.
    public const int CardWidth = 78, CardHeight = 122, CardGap = 4, Step = CardHeight + CardGap;
    public const int StripLength = 20, Winner = 17;
    private const int WindowHeight = 5 * CardHeight + 4 * CardGap;
    public const int SpinMs = 1500;
    public const string SpinDuration = "1.5s";

    /// <summary>
    /// Wait before turning a new strip: the transition needs the strip on the client first, and the strip (the biggest
    /// update) can take a few frames of the UI channel's 16 KB/s to arrive.
    /// </summary>
    public const int TurnDelayMs = 500;

    private const string Ink = "#1a1510", Black = "#100d0a", Cream = "#eadfc6", Mint = "#9affd6", Red = "#b3261a", HitCorrupted = "#ff5a40";
    private const string Gold = "#9a6a00", HitJackpot = "#ffd23f";
    private const string Amber = "#c2600a", HitLegendary = "#ff9b2f", LegendaryTag = "#c27a12";
    private const string Display = "Retail Demo", Body = "Radiance";

    private const string Img = "s2r://panorama/images/";
    private const string CorruptedIcon = Img + "hud/icons/icon_corrupted_souls.vsvg";
    private const string CorruptedFrame = Img + "shop/corrupted_items/item_frame_corrupted_psd.vtex";
    private const string SheetPaper = Img + "shop/catalog/catalog_shop_filter_bg_psd.vtex";
    private const string SlipPaper = Img + "shop/catalog/cards/shopitem_papertexture01_psd.vtex";
    private const string PrintOverlay = Img + "shop/print_screen_overlay_psd.vtex";
    private const string Slate = Img + "main_menu/slate_texture_png.vtex";
    private const string Speckle = Img + "main_menu/text_speckle_mask_main_psd.vtex";
    private const string WornTape = Img + "shop/catalog/filters/filter_nav_backer_psd.vtex";
    private const string CategoryLabels = Img + "shop/catalog/filters/shop_filtered_tree_header_full_psd.vtex";
    private const string PriceGlyph = Img + "shop/catalog/price_currency_psd.vtex";
    private const string UpgradeSticker = Img + "shop/catalog/sticker_discount_psd.vtex";
    private const string HoloDisc = Img + "shop/catalog/pricetag_legendary_psd.vtex";
    private const string BlankCard = Img + "shop/card_backer_psd.vtex";

    /// <summary>The shop's price sticker for a tier (each one has its price printed on it).</summary>
    private static string PriceTag(int tier) => $"{Img}shop/catalog/pricetag_tier{tier}_psd.vtex";

    /// <summary>One of the shop's four paper-wear overlays for its item cards.</summary>
    private static string PaperWear(int n) => $"{Img}shop/catalog/cards/shopitem_paperwear0{1 + n % 4}_psd.vtex";

    /// <summary>The shop's brush-stroke header for a category (its tooltip headers); the wildcard box gets the Corrupted one.</summary>
    private static string Brush(ItemCategory? c) => $"{Img}shop/catalog/catalog_tooltip_header_{(c == null ? "weapon_corrupted" : Slug(c))}_psd.vtex";

    /// <summary>
    /// Per-category colours (mockups/catalog.html [data-cat]); PaperX picks the category's band of the sheet paper. A null
    /// category is the wildcard box: paper colours, on the weapon band.
    /// </summary>
    private sealed record Palette(string Tab, string Line, string Acc, string Deep, string T4Name, int PaperX);

    private static Palette Colors(ItemCategory? c) => c switch
    {
        ItemCategory.Weapon => new("#e4b20c", "#b9772a", "#d99a3a", "#7c4610", "#e4c5a1", -19),
        ItemCategory.Vitality => new("#a5ce3c", "#4c7a48", "#a9d84a", "#2c5a26", "#c2e7cd", -952),
        ItemCategory.Spirit => new("#b866de", "#66508c", "#c878f0", "#5d3190", "#e3d4f2", -488),
        _ => new("#e2d3b0", "#6b5a40", "#c9a96a", "#4a3b26", "#e4c5a1", -19),
    };

    private static string Slug(ItemCategory? c) => c?.ToString().ToLowerInvariant() ?? "any";

    /// <summary>The shop's card backer for the item's category and tier; a Legendary (tier 5 has no backer) takes tier 4's.</summary>
    private static string CardBacker(RouletteItem i) =>
        $"s2r://panorama/images/shop/catalog/cards/card_backer_{Slug(i.Category)}_t{Math.Min(i.Tier, 4)}_psd.vtex";

    public static string Souls(int souls) => souls.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    private static string Px(double v) => $"{Math.Round(v)}px";

    /// <summary>Places a node at x, y (from its parent's top left) with a fixed size.</summary>
    private static T At<T>(this T n, double x, double y, double w, double h) where T : UINode
        => n.WithStyles(("margin-left", Px(x)), ("margin-top", Px(y)), ("width", Px(w)), ("height", Px(h)));

    private static T Font<T>(this T n, string family, double size, string color, double spacing = 0) where T : UINode
        => n.WithStyles(("font-family", family), ("font-weight", "bold"), ("font-size", Px(size)), ("color", color),
                        ("letter-spacing", $"{spacing:0.#}px"), ("text-transform", "uppercase"));

    private static T Centered<T>(this T n) where T : UINode => n.WithStyles(("horizontal-align", "center"), ("vertical-align", "center"));

    /// <summary>The game's speckled, worn-ink print on text and stamps (its own opacity-mask).</summary>
    private static T Worn<T>(this T n) where T : UINode => n.WithStyle("opacity-mask", $"url(\"{Speckle}\") 1");

    /// <summary>A layer filling its parent with an image, stretched.</summary>
    private static UIContainer Layer(double w, double h, string image, double opacity = 1, string id = "") => UI.Container(id).At(0, 0, w, h).WithStyles(
        ("background-image", $"url(\"{image}\")"), ("background-size", "100% 100%"), ("opacity", $"{opacity:0.##}"));

    /// <summary>
    /// What one player's panel shows. Category null is the wildcard box; Wildcard says whether it is offered. DrawLine is
    /// the line under the result (the odds, the build), RatesLine and OddsLine the fine print.
    /// </summary>
    public sealed record View(
        ItemCategory? Category, int Tier, int Souls, RouletteItem[] Reel, bool ViaShopKey, bool Wildcard,
        string DrawLine, string RatesLine, string OddsLine, RouletteItem? Landed, bool LandedCorrupted, bool LandedJackpot,
        SpinState Button, int FreeSpins, bool Lucky, Landing? LandedAs = null, string StreakLine = "");

    /// <summary>
    /// What can be done with the item a spin landed: Keep it (not given yet; until then it can be bet, double or nothing, or
    /// rerolled for RerollCost souls), then imbue it (ImbueSlots: which of abilities 1-4 take it; ImbuedSlot once done).
    /// Doubled: it came from a double or nothing.
    /// </summary>
    public sealed record Landing(bool Double, int RerollCost, bool[]? ImbueSlots, int? ImbuedSlot, bool Doubled = false, bool Keep = false);

    // ─── the sheet ────────────────────────────────────────────────────────────

    // Sheet coordinates: 18 in from the sides; the side column (souls, result, SPIN) right of the reel.
    private const int Side = 136, SideWidth = 290, MachineTop = 239, MachineHeight = WindowHeight + 26;

    public static UINode Layout(View v)
    {
        var pal = Colors(v.Category);
        string hit = v.Landed is { } l ? HitColor(v.LandedCorrupted, v.LandedJackpot, l.Legendary) : Mint;

        // SheetRight from the screen's right edge.
        // The hover style is there so it takes the mouse (hittest): clicks on the sheet don't reach the game.
        return UI.Container("rsSheet")
            .WithStyles(("width", Px(SheetWidth)), ("height", Px(SheetHeight)), ("horizontal-align", "right"), ("vertical-align", "top"),
                        ("margin-top", Px(SheetTop)), ("margin-right", Px(SheetRight)),
                        ("background-color", "#d9c9a6"), ("background-image", $"url(\"{SheetPaper}\")"),
                        ("background-size", "1452px 2182px"), ("background-position", $"{pal.PaperX}px 0px"),
                        ("background-repeat", "no-repeat"), ("box-shadow", "0px 6px 18px #00000099"))
            .WithHoverStyle("opacity", "1")
            .Add(Layer(SheetWidth, SheetHeight, PrintOverlay, 0.55),   // first, so it sits under everything (and their clicks)
                 Mast(v.Category, closable: !v.ViaShopKey, v.Lucky),
                 Categories(v.Category, v.Wildcard),
                 Prices(v.Tier),
                 Reel(v, hit),
                 SoulsRow(v.Souls),
                 // The result slip: shop card paper with the cards' wear on its edges, pinned slightly askew.
                 UI.Container("rsResultBox").At(Side, 307, SideWidth, 411).WithStyles(("background-color", "#dccdac"),
                         ("box-shadow", "2px 4px 6px #00000066"), ("transform", "rotateZ(-0.6deg)"))
                     .Add(Layer(SideWidth, 411, SlipPaper, 0.45),
                          Layer(SideWidth, 411, PaperWear(1)),
                          v.Landed is { } landed ? Result("rsResult0", landed, v.LandedCorrupted, v.LandedJackpot, v.LandedAs) : Placeholder("rsResult0")),
                 UI.Label("rsDraw", v.DrawLine).At(Side, 730, SideWidth, 20).Font(Body, 15, Ink, 2.5).Worn()
                     .WithStyles(("text-align", "center"), ("text-overflow", "shrink"), ("white-space", "nowrap")),
                 SpinButton(v),
                 Footer(v, pal));
    }

    /// <summary>The ring and pointers round the winning card: red for Corrupted, gold for a jackpot, amber for a Legendary.</summary>
    private static string HitColor(bool corrupted, bool jackpot, bool legendary)
        => corrupted ? HitCorrupted : legendary ? HitLegendary : jackpot ? HitJackpot : Mint;

    /// <summary>The masthead: slate, the category's brush stroke across it, the name in worn cream ink.</summary>
    public static string MastLine(bool lucky) => lucky ? "LUCKY HOUR · BETTER RARE ODDS" : "ONE SPIN · ONE ITEM · NO REFUNDS";

    private static UINode Mast(ItemCategory? category, bool closable, bool lucky)
    {
        var mast = UI.Container("rsMast").At(18, 20, 408, 85).WithStyles(("background-color", Black), ("background-image", $"url(\"{Slate}\")"),
                ("background-size", "cover"), ("box-shadow", "2px 3px 0px #00000059")).Add(
            Layer(408, 85, Brush(category), 0.95),
            UI.Label("", "ROULETTE SHOP").At(0, 13, 408, 39).Font(Display, 38, Cream, 1).Worn()
                .WithStyles(("text-align", "center"), ("line-height", "39px"), ("text-shadow", "2px 3px 0px #000000cc")),
            UI.Label("rsMastLine", MastLine(lucky)).At(0, 58, 408, 15).Font(Body, 14, lucky ? HitJackpot : Cream, 3).Worn()
                .WithStyles(("text-align", "center"), ("line-height", "15px"), ("opacity", lucky ? "1" : "0.85"), ("text-overflow", "shrink"),
                            ("white-space", "nowrap")));
        // Opened from chat there is no shop to close it, so it gets a button of its own.
        if (closable)
            mast.Add(UI.Button("rsClose", "").OnClick("close").At(370, 8, 30, 30)
                .WithStyles(("background-color", Cream), ("border-radius", "3px"), ("transform", "rotateZ(4deg)"))
                .WithHoverStyle("brightness", "1.2")
                .Add(UI.Label("", "X").Font(Display, 20, Ink).Centered().Worn()));
        return mast;
    }

    // ─── pickers: the shop's printed category labels, and its price stickers ───

    /// <summary>
    /// Where each category's printed label ("WEAPON  Stock up!", "Spirit  Big deal!", "Vitality  Feel good!") sits in the
    /// shop's label strip (shop_filtered_tree_header_full, 2160x183): its left edge; each is about 700x173.
    /// </summary>
    private static int LabelX(ItemCategory c) => c switch { ItemCategory.Weapon => 14, ItemCategory.Spirit => 736, _ => 1450 };

    private const double LabelWidth = 700, LabelHeight = 173, StripWidth = 2160, StripHeight = 183, LabelTop = 5;

    private static UINode Categories(ItemCategory? selected, bool wildcard)
    {
        var row = UI.Horizontal("rsTabs").At(18, 117, 408, 46);
        var all = Enum.GetValues<ItemCategory>();
        const int gap = 6, any = 54;
        double width = wildcard ? (408 - any - gap * 3) / 3.0 : (408 - gap * 2) / 3.0;
        double height = Math.Round(width * LabelHeight / LabelWidth), scale = width / LabelWidth;
        foreach (var c in all)
        {
            bool on = c == selected;
            // The label, cropped out of the strip with background-size and -position.
            row.Add(UI.Button("rsTab" + c, "").OnClick("cat", Slug(c))
                .WithStyles(("width", Px(width)), ("height", Px(height)), ("vertical-align", "center"), ("margin-right", Px(gap)),
                            ("background-image", $"url(\"{CategoryLabels}\")"), ("background-repeat", "no-repeat"),
                            ("background-size", $"{StripWidth * scale:0.#}px {StripHeight * scale:0.#}px"),
                            ("background-position", $"{-LabelX(c) * scale:0.#}px {-LabelTop * scale:0.#}px"),
                            ("saturation", on ? "1" : "0.2"), ("opacity", on ? "1" : "0.75"),
                            ("transform", on ? "scale3d(1.1, 1.1, 1) rotateZ(-2deg)" : "none"))
                .WithStyles(on ? [("box-shadow", "0px 3px 5px #00000080")] : [])
                .WithHoverStyle("saturation", "0.8"));
        }
        if (wildcard)
        {
            bool on = selected == null;
            row.Add(UI.Button("rsTabAny", "").OnClick("cat", "any")
                .WithStyles(("width", Px(any)), ("height", Px(height)), ("vertical-align", "center"), ("background-color", "#e9dcc3"),
                            ("border", $"2px solid {Ink}"), ("opacity", on ? "1" : "0.75"),
                            ("transform", on ? "scale3d(1.1, 1.1, 1) rotateZ(2deg)" : "rotateZ(2deg)"))
                .WithStyles(on ? [("box-shadow", "0px 3px 5px #00000080")] : [])
                .WithHoverStyle("opacity", "0.95")
                .Add(UI.Label("", "ANY").Font(Display, 16, Ink, 1).Centered().Worn()));
        }
        return row;
    }

    /// <summary>The tilt the shop gives each tier's price sticker (citadel_shop_mods_filtered.css .CostSticker).</summary>
    private static string StickerTilt(int tier) => tier switch { 1 => "-2deg", 2 => "5deg", 3 => "-3deg", _ => "0deg" };

    /// <summary>The four price buttons: the shop's own price stickers, the chosen one full colour and lifted.</summary>
    private static UINode Prices(int selected)
    {
        var row = UI.Horizontal("rsPrices").At(18, 173, 408, 64);
        for (int k = 1; k <= ItemPool.TierPrices.Length; k++)
        {
            bool on = k == selected;
            row.Add(UI.Button("rsPrice" + k, "").OnClick("tier", k.ToString())
                .WithStyles(("width", "93px"), ("height", "60px"), ("margin-top", "2px"), ("margin-right", k < ItemPool.TierPrices.Length ? "12px" : "0px"),
                            ("transform", on ? $"scale3d(1.12, 1.12, 1) rotateZ({StickerTilt(k)})" : $"scale3d(0.9, 0.9, 1) rotateZ({StickerTilt(k)})"),
                            ("saturation", on ? "1" : "0.15"), ("brightness", on ? "1" : "0.85"))
                .WithHoverStyle("saturation", "0.8")
                .Add(UI.Image("", PriceTag(k)).WithStyles(("width", "100%"), ("height", "100%"),
                    ("img-shadow", on ? "0px 3px 5px #000000aa" : "0px 1px 2px #00000066"))));
        }
        return row;
    }

    // ─── item card, after the real shop's (backer by category and tier, icon 64%, name plate 36%) ───

    /// <summary>An item card like the shop's: backer, icon, name plate, and one of its paper-wear overlays (<paramref name="wear"/>).</summary>
    public static UIContainer Card(string id, RouletteItem i, double w, bool corrupted = false, int wear = 0)
    {
        double h = Math.Round(w * 128.0 / 82), icon = Math.Round(h * 0.640625), font = 13.0 * w / 73;
        double name = font * (i.Name.Length <= 14 ? 1 : i.Name.Length <= 19 ? 0.923 : i.Name.Length <= 22 ? 0.846 : 0.769);
        double radius = Math.Round(font * 0.385);
        var pal = Colors(i.Category);

        var card = UI.Container(id).WithStyles(("width", Px(w)), ("height", Px(h)), ("border-radius", Px(radius)),
            ("background-image", $"url(\"{CardBacker(i)}\")"), ("background-size", "100% 100%"));
        double pad = i.Active ? Math.Round(w * 0.03) : 0;
        card.Add(
            UI.Image("", i.GameIcon).At(pad, pad, w - 2 * pad, icon - 2 * pad).WithStyles(
                ("border-radius", i.Active ? "50%" : $"{Px(radius)} {Px(radius)} 0px 0px"),
                ("box-shadow", i.Active ? "0px 1px 3px #00000073" : "none")),
            UI.Container().At(0, icon, w, h - icon).Add(
                UI.Label("", i.Name).WithStyles(("width", Px(w * 0.88)), ("horizontal-align", "center"), ("vertical-align", "center"),
                    ("text-align", "center"), ("font-family", Display), ("font-weight", "bold"), ("font-size", Px(name)),
                    ("line-height", Px(name * 0.92)), ("letter-spacing", "0px"), ("text-overflow", "shrink"),
                    ("color", i.Tier >= 4 ? pal.T4Name : Ink))));
        if (i.Legendary)
        {
            // Tier 5 has no card art of its own (the shop only hides the tier label): a gold frame and a tag.
            double tag = Math.Round(font * 0.615 * 1.25 + 4);
            card.Add(UI.Container().At(0, 0, w, h).WithStyles(("border", $"{Px(Math.Max(2, w / 30))} solid {HitLegendary}"),
                                                              ("border-radius", Px(radius))),
                     UI.Label("", "LEGENDARY").At(0, 0, w, tag).WithStyles(("background-color", LegendaryTag), ("color", "#fff3d6"),
                         ("font-family", Display), ("font-weight", "bold"), ("font-size", Px(font * 0.615)), ("letter-spacing", "1px"),
                         ("text-align", "center"), ("border-radius", $"{Px(radius)} {Px(radius)} 0px 0px")));
        }
        if (i.Imbue)
        {
            double tag = Math.Round(font * 0.615 * 1.25 + 4);
            card.Add(UI.Label("", "IMBUE").At(0, icon - tag, w, tag).WithStyles(("background-color", "#5a3a8a"), ("color", "#d4b8f0"),
                ("font-family", Display), ("font-weight", "bold"), ("font-size", Px(font * 0.615)), ("letter-spacing", "1px"),
                ("text-align", "center"), ("border-radius", "3px")));
        }
        card.Add(Layer(w, h, PaperWear(wear)));
        if (corrupted)
            card.Add(Layer(w, h, CorruptedFrame));
        return card;
    }

    // ─── the reel: a black tape, five cards on show, the middle one ringed between two pointers ───

    private static UINode Reel(View v, string hit)
    {
        int middle = 13 + 2 * Step, ring = middle - 6;
        return UI.Container("rsReel").At(18, MachineTop, 104, MachineHeight).WithStyles(("background-color", Black), ("border-radius", "3px"),
                ("background-image", $"url(\"{Slate}\")"), ("background-size", "cover"), ("overflow", "clip"), ("box-shadow", "2px 3px 0px #0000004d"))
            .Add(UI.Container().At(0, 0, 104, MachineHeight).WithStyles(("border", "2px solid #eadfc629"), ("border-radius", "3px")),
                 UI.Container("rsWindow").At(13, 13, CardWidth, WindowHeight).WithStyle("overflow", "clip")
                     .Add(Strip("rsStrip0", v.Reel)),
                 // Shade over the cards that don't win (outside the window, so a new strip goes in under it).
                 UI.Container().At(13, 13, CardWidth, middle - 15).WithStyle("background-color", "#100d0a73"),
                 UI.Container().At(13, middle + CardHeight + 2, CardWidth, WindowHeight - (middle - 13 + CardHeight + 2)).WithStyle("background-color", "#100d0a73"),
                 UI.Container("rsRing").At(4, ring, 96, CardHeight + 12).WithStyles(("border", $"3px solid {hit}"), ("border-radius", "8px")),
                 // The pointers: squares turned 45 degrees on the tape's edges, half clipped into triangles.
                 UI.Container("rsPtrL").At(-8, ring + (CardHeight + 12) / 2 - 8, 16, 16).WithStyles(("background-color", hit), ("transform", "rotateZ(45deg)")),
                 UI.Container("rsPtrR").At(96, ring + (CardHeight + 12) / 2 - 8, 16, 16).WithStyles(("background-color", hit), ("transform", "rotateZ(45deg)")));
    }

    public static UINode Strip(string id, IReadOnlyList<RouletteItem> cards)
    {
        var strip = UI.Vertical(id).WithStyles(("width", Px(CardWidth)), ("height", Px(cards.Count * Step)), ("transform", "translateY(0px)"));
        for (int k = 0; k < cards.Count; k++)
            strip.Add(Card($"{id}c{k}", cards[k], CardWidth, wear: k).WithStyle("margin-bottom", Px(CardGap)));
        return strip;
    }

    // ─── souls, the result, SPIN, the fine print ──────────────────────────────

    /// <summary>A strip of the shop's worn dark tape (its filter bar backer), tilted.</summary>
    private static UIContainer Tape(double height, string tilt) => UI.Horizontal().WithStyles(("vertical-align", "center"), ("height", Px(height)),
        ("background-image", $"url(\"{WornTape}\")"), ("background-size", "100% 100%"), ("transform", $"rotateZ({tilt})"));

    /// <summary>The shop's price glyph (§, mint) next to a mint number.</summary>
    private static UINode Price(string id, string text, double size) => UI.Horizontal().WithStyle("vertical-align", "center").Add(
        UI.Image("", PriceGlyph).WithStyles(("width", Px(size * 0.5)), ("height", Px(size * 0.92)), ("vertical-align", "center"), ("margin-right", "4px")),
        UI.Label(id, text).Font(Display, size, Mint).Worn().WithStyles(("vertical-align", "center"), ("text-shadow", "0px 1px 3px #000000b3")));

    private static UINode SoulsRow(int souls) => UI.Horizontal().At(Side, MachineTop, SideWidth, 56).Add(
        Tape(48, "-3deg").WithStyles(("margin-left", "2px"), ("padding", "0px 16px 0px 14px")).Add(Price("rsSouls", Souls(souls), 34)),
        UI.Label("", "YOUR\nSOULS").Font(Body, 15, Ink, 2.5).Worn().WithStyles(("vertical-align", "center"), ("margin-left", "12px"), ("line-height", "18px")));

    /// <summary>The result's frame: a heading over a ruled line, the body centred below it.</summary>
    private static UINode ResultFrame(string id, string heading, UINode body) => UI.Vertical(id)
        .WithStyles(("width", "100%"), ("height", "100%"), ("padding", "14px 15px 15px 15px")).Add(
            UI.Label("", heading).Font(Body, 17, Ink, 4).Worn().WithStyles(("width", "100%"), ("text-align", "center"), ("padding-bottom", "8px"),
                                                                           ("border-bottom", $"2px solid {Ink}")),
            UI.Container().WithStyles(("width", "100%"), ("height", "fill-parent-flow(1.0)")).Add(body));

    private static UIContainer ResultBody() => UI.Vertical().WithStyles(("width", "100%"), ("vertical-align", "center"));

    private static UINode ItemName(string text) => UI.Label("", text).Font(Display, 27, Ink).Worn()
        .WithStyles(("width", "100%"), ("text-align", "center"), ("text-transform", "none"), ("text-overflow", "shrink"), ("white-space", "nowrap"));

    public static UINode Placeholder(string id) => ResultFrame(id, "NO SPIN YET", ResultBody().Add(
        UI.Container().WithStyles(("width", "112px"), ("height", "175px"), ("horizontal-align", "center"), ("margin-bottom", "14px"),
                ("background-image", $"url(\"{BlankCard}\")"), ("background-size", "100% 100%"), ("border-radius", "6px"),
                ("transform", "rotateZ(-3deg)"), ("box-shadow", "0px 4px 10px #00000059"))
            .Add(Layer(112, 175, PaperWear(2)),
                 UI.Label("", "?").Font(Display, 64, Cream).Centered().Worn().WithStyle("text-shadow", "2px 2px 0px #000000aa")),
        ItemName("Pick a box"),
        UI.Label("", "Click an item in the shop, or pick a category and a price above, then spin.").WithStyles(("width", "100%"),
            ("margin-top", "8px"), ("text-align", "center"), ("font-family", Body), ("font-weight", "semi-bold"), ("font-size", "18px"),
            ("line-height", "23px"), ("color", Ink))));

    public static UINode Message(string id, string title, string body) => ResultFrame(id, title.ToUpperInvariant(), ResultBody().Add(
        UI.Label("", body).WithStyles(("width", "100%"), ("text-align", "center"), ("font-family", Body), ("font-weight", "semi-bold"),
            ("font-size", "18px"), ("line-height", "23px"), ("color", Ink))));

    public static UINode Result(string id, RouletteItem item, bool corrupted, bool jackpot = false, Landing? landing = null)
    {
        var pal = Colors(item.Category);
        // The price on worn tape: the item's shop price, or (tier 5 has no price here) its tier.
        var tape = Tape(36, "-4deg").WithStyles(("margin-left", "11px"), ("padding", "0px 12px 0px 10px"));
        if (item.Legendary)
            tape.Add(UI.Label("", "TIER 5").Font(Display, 21, HitLegendary, 1).Worn().WithStyle("vertical-align", "center"));
        else
            tape.Add(Price("", Souls(ItemPool.TierPrices[item.Tier - 1]), 23));

        // The card (smaller when there are buttons under it), on the shop's holographic Legendary sticker for a
        // Legendary, with its UPGRADE ribbon for a jackpot, and the rubber stamps across its foot.
        bool actions = landing is { } l && (l.Keep || l.Double || l.RerollCost > 0 || l.ImbueSlots != null || l.ImbuedSlot != null);
        double cw = landing?.Keep == true ? 80 : actions ? 100 : 112, ch = Math.Round(cw * 128 / 82);
        var card = Card("", item, cw, corrupted, wear: 3).WithStyles(("horizontal-align", "center"), ("vertical-align", "top"),
                                                                    ("margin-top", "4px"), ("transform", "rotateZ(-3deg)"),
                                                                    ("box-shadow", "0px 6px 14px #00000073"));
        if (jackpot)
            card.Add(UI.Image("", UpgradeSticker).At(cw - 50, 0, 50, 48));
        var holder = UI.Container().WithStyles(("width", "240px"), ("height", Px(ch + 14)), ("horizontal-align", "center"));
        if (item.Legendary)
            holder.Add(UI.Image("", HoloDisc).WithStyles(("width", Px(ch + 8)), ("height", Px(ch + 8)), ("horizontal-align", "center"),
                                                         ("vertical-align", "center"), ("opacity", "0.9")));
        holder.Add(card);
        var stamps = new List<UIContainer>();
        if (item.Legendary)
            stamps.Add(Stamp(Amber, 18, "LEGENDARY", null));
        if (corrupted)
            stamps.Add(Stamp(Red, 15, "CORRUPTED", CorruptedIcon));
        if (jackpot)
            stamps.Add(Stamp(Gold, 15, "JACKPOT", null));
        if (landing?.Doubled == true)
            stamps.Add(Stamp(Gold, 15, "DOUBLED", null));
        if (stamps.Count > 0)
        {
            var row = UI.Horizontal().WithStyles(("horizontal-align", "center"), ("vertical-align", "bottom"));
            for (int k = 0; k < stamps.Count; k++)
                row.Add(stamps[k].WithStyle("margin-left", k == 0 ? "0px" : "6px"));
            holder.Add(row);
        }

        var body = ResultBody().Add(
            holder,
            ItemName(item.Name).WithStyle("margin-top", "8px"),
            UI.Horizontal().WithStyles(("horizontal-align", "center"), ("margin-top", "12px")).Add(
                UI.Label("", Slug(item.Category)).Font(Display, 19, pal.Deep, 1).Worn().WithStyle("vertical-align", "center"),
                tape));
        if (landing is { } a)
        {
            // KEEP; for an imbue item, picking its ability keeps it (and imbues it), as buying one in the shop does.
            if (a.Keep)
                body.Add(a.ImbueSlots is { } keys
                    ? ImbueRow("KEEP INTO ABILITY", keys)
                    : ActionButton("rsKeep", "keep", null, "KEEP IT", 256).WithStyles(("horizontal-align", "center"), ("margin-top", "16px")));
            if (a.Double || a.RerollCost > 0)
            {
                var bets = UI.Horizontal().WithStyles(("horizontal-align", "center"), ("margin-top", "14px"));
                if (a.Double)
                    bets.Add(ActionButton("rsDouble", "double", null, "DOUBLE OR NOTHING", 136));
                if (a.RerollCost > 0)
                    bets.Add(ActionButton("rsReroll", "reroll", null, $"REROLL {Souls(a.RerollCost)}", 112).WithStyle("margin-left", a.Double ? "8px" : "0px"));
                body.Add(bets);
            }
            if (a.ImbuedSlot is { } done)
                body.Add(UI.Label("", $"IMBUED INTO ABILITY {done}").Font(Body, 14, Ink, 2).Worn()
                    .WithStyles(("horizontal-align", "center"), ("margin-top", "14px")));
            else if (!a.Keep && a.ImbueSlots is { } slots)
                body.Add(ImbueRow("IMBUE INTO", slots));
        }
        return ResultFrame(id, landing?.Keep == true ? "YOU ROLLED" : "YOU GOT", body);
    }

    /// <summary>A label and the four ability keys (greyed out for an ability that won't take the item).</summary>
    private static UINode ImbueRow(string label, bool[] slots)
    {
        var row = UI.Horizontal().WithStyles(("horizontal-align", "center"), ("margin-top", "16px")).Add(
            UI.Label("", label).Font(Body, 13, Ink, 2).Worn().WithStyles(("vertical-align", "center"), ("margin-right", "8px")));
        for (int k = 0; k < slots.Length; k++)
            row.Add(ImbueKey(k + 1, slots[k]));
        return row;
    }

    // ─── the reveal: the landed item, big, in the middle of the screen for a few seconds ───

    public const string RevealId = "rouletteReveal";
    public const int RevealMs = 3000;

    /// <summary>
    /// The item a spin landed, big, in the middle of the screen (the user: "when you get the item, it displays it to you.
    /// that way people know what they get"). No hover style, so it doesn't take the mouse.
    /// </summary>
    public static UINode Reveal(string heading, RouletteItem item, bool corrupted, bool jackpot, bool doubled)
    {
        const int w = 420, h = 570, cw = 200;
        var card = Card("", item, cw, corrupted, wear: 3).WithStyles(("horizontal-align", "center"), ("margin-top", "116px"),
                                                                    ("transform", "rotateZ(-2deg)"), ("box-shadow", "0px 8px 20px #000000aa"));
        if (jackpot)
            card.Add(UI.Image("", UpgradeSticker).At(cw - 90, 0, 90, 86));

        var tags = UI.Horizontal().WithStyles(("horizontal-align", "center"), ("vertical-align", "bottom"), ("margin-bottom", "24px"))
            .Add(UI.Label("", item.Legendary ? "TIER 5" : $"{Slug(item.Category)} · {Souls(ItemPool.TierPrices[item.Tier - 1])}")
                .Font(Display, 22, Colors(item.Category).Tab, 2).WithStyle("vertical-align", "center"));
        foreach (var (on, color, text) in new[] { (item.Legendary, Amber, "LEGENDARY"), (corrupted, Red, "CORRUPTED"), (jackpot, Gold, "JACKPOT"), (doubled, Gold, "DOUBLED") })
            if (on)
                tags.Add(Stamp(color, 15, text, null).WithStyle("margin-left", "10px"));

        return UI.Container("rvRoot")
            .WithStyles(("width", Px(w)), ("height", Px(h)), ("horizontal-align", "center"), ("vertical-align", "center"), ("margin-bottom", "80px"),
                        ("background-color", Black), ("background-image", $"url(\"{Slate}\")"), ("background-size", "cover"),
                        ("border-radius", "6px"), ("box-shadow", "0px 10px 30px #000000cc"))
            .Add(Layer(w, 92, Brush(item.Category)),
                 UI.Label("", heading).At(0, 18, w, 56).Font(Display, 44, Cream, 3).Worn()
                     .WithStyles(("text-align", "center"), ("text-shadow", "2px 3px 0px #000000cc")),
                 card,
                 UI.Label("", item.Name).At(16, 446, w - 32, 40).Font(Display, 34, Cream).WithStyles(("text-align", "center"), ("text-transform", "none"),
                     ("text-overflow", "shrink"), ("white-space", "nowrap"), ("text-shadow", "2px 2px 0px #000000cc")),
                 tags);
    }

    /// <summary>A small button on the slip: worn tape, cream worn type.</summary>
    private static UINode ActionButton(string id, string evt, string? arg, string text, double width)
    {
        var button = UI.Button(id, "").WithStyles(("width", Px(width)), ("height", "32px"), ("background-image", $"url(\"{WornTape}\")"),
                                                  ("background-size", "100% 100%"), ("transform", "rotateZ(-1deg)"))
            .WithHoverStyle("brightness", "1.25");
        if (arg == null)
            button.OnClick(evt);
        else
            button.OnClick(evt, arg);
        return button.Add(UI.Label("", text).Font(Display, 13, Cream, 1).Centered().Worn()
            .WithStyles(("text-overflow", "shrink"), ("white-space", "nowrap")));
    }

    /// <summary>One of the four ability keys for imbuing: a paper key, greyed out for an ability that won't take the item.</summary>
    private static UINode ImbueKey(int n, bool takes)
    {
        var face = UI.Label("", n.ToString()).Font(Display, 17, Ink).Centered();
        (string, string)[] look = [("width", "30px"), ("height", "30px"), ("margin-left", "4px"), ("vertical-align", "center"),
                                   ("background-color", Cream), ("border", $"2px solid {Ink}"), ("border-radius", "3px")];
        return takes
            ? UI.Button($"rsImbue{n}", "").OnClick("imbue", n.ToString()).WithStyles(look).WithHoverStyle("brightness", "1.15").Add(face)
            : UI.Container($"rsImbue{n}").WithStyles(look).WithStyle("opacity", "0.3").Add(face);
    }

    /// <summary>A rubber stamp in worn ink.</summary>
    private static UIContainer Stamp(string color, double size, string text, string? icon)
    {
        var stamp = UI.Horizontal().WithStyles(("padding", "5px 9px 3px 9px"), ("border", $"3px solid {color}"), ("border-radius", "4px"),
                                               ("background-color", "#e7dcc3e6"), ("transform", "rotateZ(-6deg)")).Worn();
        if (icon != null)
            stamp.Add(UI.Image("", icon).WithStyles(("width", Px(size + 2)), ("height", Px(size + 2)), ("margin-right", "8px"), ("wash-color", color),
                                                    ("vertical-align", "center")));
        return stamp.Add(UI.Label("", text).Font(Display, size, color, 2).WithStyle("vertical-align", "center"));
    }

    /// <summary>The SPIN button: Away = not in a shop zone (when spinning needs one), Free = a free spin is waiting.</summary>
    public enum SpinState { Ready, Busy, Broke, Away, Free, Full }

    /// <summary>SPIN: slate with the category's brush stroke, the word in worn cream, and the tier's own price sticker.</summary>
    private static UINode SpinButton(View v)
    {
        var state = v.Button;
        return UI.Button("rsSpin", "").OnClick("spin").At(Side, 762, SideWidth, 123)
            .WithStyles(("background-color", Black), ("background-image", $"url(\"{Slate}\")"), ("background-size", "cover"),
                        ("border-radius", "4px"), ("box-shadow", "0px 6px 0px #0c0906"))
            .WithHoverStyle("brightness", "1.12")
            .Add(Layer(SideWidth, 123, Brush(v.Category), id: "rsSpinBrush").WithStyles(SpinBrushStyle(state)),
                 UI.Container("rsSpinFrame").At(5, 5, SideWidth - 10, 113).WithStyles(("border", $"2px solid {FrameColor(state)}"), ("border-radius", "3px")),
                 UI.Horizontal().Centered().Add(
                     UI.Label("rsSpinText", SpinText(state, v.FreeSpins)).Font(Display, 50, Cream, 2).Worn()
                         .WithStyles(("vertical-align", "center"), ("text-shadow", "2px 3px 0px #000000cc"))
                         .WithStyles(SpinTextStyle(state)),
                     UI.Image("rsSpinCost", PriceTag(v.Tier)).WithStyles(("width", "104px"), ("height", "64px"), ("margin-left", "10px"),
                         ("vertical-align", "center"), ("transform", $"rotateZ({StickerTilt(v.Tier)})"), ("img-shadow", "0px 3px 5px #000000aa"),
                         ("visibility", state == SpinState.Ready ? "visible" : "collapse"))),
                 // Free-spin progress (the user: "how are you supposed to see your progress on a free spin?").
                 UI.Label("rsStreak", v.StreakLine).At(0, 98, SideWidth, 18).Font(Body, 13, Mint, 2)
                     .WithStyles(("text-align", "center"), ("text-shadow", "1px 1px 0px #000000cc")));
    }

    /// <summary>The free-spin progress line on the SPIN button.</summary>
    public static void UpdateStreak(RecipientFilter to, string streakLine) => Panel.Build().Set("rsStreak", streakLine).SendTo(to);

    private static string FrameColor(SpinState s) => s switch
    {
        SpinState.Ready => "#eadfc64d",
        SpinState.Free => "#9affd6b3",
        SpinState.Away => "#eadfc626",
        SpinState.Busy => "#eadfc626",
        _ => "#ff8a7a80",
    };

    /// <summary>The brush stroke: full colour when ready, drained while turning or when the player can't pay.</summary>
    private static (string, string)[] SpinBrushStyle(SpinState s) => s switch
    {
        SpinState.Ready or SpinState.Free => [("saturation", "1"), ("opacity", "1")],
        SpinState.Busy => [("saturation", "0.3"), ("opacity", "0.6")],
        _ => [("saturation", "0"), ("opacity", "0.35")],
    };

    private static string SpinText(SpinState s, int free) => s switch
    {
        SpinState.Ready => "SPIN",
        SpinState.Busy => "SPINNING...",
        SpinState.Away => "GO TO A SHOP",
        SpinState.Free => free > 1 ? $"FREE SPIN X{free}" : "FREE SPIN",
        SpinState.Full => "SLOTS FULL",
        _ => "NOT ENOUGH SOULS",
    };

    private static (string, string)[] SpinTextStyle(SpinState s) => s switch
    {
        SpinState.Ready => [("font-size", "50px"), ("color", Cream)],
        SpinState.Busy => [("font-size", "30px"), ("color", Cream)],
        SpinState.Away => [("font-size", "30px"), ("color", Cream)],
        SpinState.Free => [("font-size", "38px"), ("color", Mint)],
        _ => [("font-size", "25px"), ("color", "#ff8a7a")],
    };

    private static UINode Footer(View v, Palette pal)
    {
        var footer = UI.Vertical().At(18, 901, 408, 46);
        foreach (var (id, line) in new[] { ("rsRates", v.RatesLine), ("rsOdds", v.OddsLine) })
            footer.Add(UI.Horizontal().WithStyles(("width", "100%"), ("height", "23px")).Add(
                UI.Container().WithStyles(("width", "7px"), ("height", "7px"), ("vertical-align", "center"), ("margin-left", "2px"),
                                          ("background-color", pal.Deep), ("transform", "rotateZ(45deg)")),
                UI.Label(id, line).Worn().WithStyles(("width", "fill-parent-flow(1.0)"), ("vertical-align", "center"), ("margin-left", "9px"),
                    ("font-family", Body), ("font-weight", "bold"), ("font-size", "15px"), ("color", Ink), ("text-overflow", "shrink"),
                    ("white-space", "nowrap"))));
        return footer;
    }

    // ─── updates after the first build (each one a few ops, not a rebuild) ────

    private static UIPanel Panel => UI.Panel(Id);

    /// <summary>Swaps in a spin strip (a fresh id: a removed node lingers a frame) and greys the spin button.</summary>
    public static void StartSpin(RecipientFilter to, string oldStripId, string stripId, RouletteItem[] strip, ItemCategory? category)
    {
        Panel.RemoveChild(to, oldStripId);
        Panel.AppendChild(to, "rsWindow", Strip(stripId, strip));
        SetSpinButton(Panel.Build(), category, SpinState.Busy, 0).SendTo(to);
    }

    /// <summary>Turns the reel until <see cref="Winner"/> sits between the pointers. Needs the strip to exist on the client first.</summary>
    public static void TurnReel(RecipientFilter to, string stripId)
        => Panel.Animate(to, stripId, "transform", $"translateY({-(Winner - 2) * Step}px)", SpinDuration, "ease-out");

    /// <summary>Replaces the result.</summary>
    public static void ShowResult(RecipientFilter to, string oldResultId, UINode result)
    {
        Panel.RemoveChild(to, oldResultId);
        Panel.AppendChild(to, "rsResultBox", result);
    }

    /// <summary>
    /// After the reel stops: the ring turns red for a Corrupted item (gold for a jackpot, amber for a Legendary); souls, the
    /// odds lines and the button (in the box's colour) update.
    /// </summary>
    public static void ShowLanding(RecipientFilter to, ItemCategory? box, RouletteItem landed, bool corrupted, bool jackpot, int souls,
                                   string drawLine, string oddsLine, SpinState state, int freeSpins)
    {
        var update = Panel.Build().Set("rsSouls", Souls(souls)).Set("rsDraw", drawLine).Set("rsOdds", oddsLine);
        if (corrupted || jackpot || landed.Legendary)
        {
            string hit = HitColor(corrupted, jackpot, landed.Legendary);
            update.SetStyle("rsRing", "border", $"3px solid {hit}")
                  .SetStyle("rsPtrL", "background-color", hit)
                  .SetStyle("rsPtrR", "background-color", hit);
        }
        SetSpinButton(update, box, state, freeSpins).SendTo(to);
    }

    /// <summary>The soul count, and the spin button unless a spin is turning.</summary>
    public static void UpdateSouls(RecipientFilter to, int souls, ItemCategory? category, SpinState? state, int freeSpins)
    {
        var update = Panel.Build().Set("rsSouls", Souls(souls));
        if (state is { } s)
            SetSpinButton(update, category, s, freeSpins);
        update.SendTo(to);
    }

    /// <summary>The draw line and the odds lines (after a bet, or when a lucky hour starts or ends).</summary>
    public static void UpdateLines(RecipientFilter to, string drawLine, string ratesLine, string oddsLine, bool lucky)
        => Panel.Build().Set("rsDraw", drawLine).Set("rsRates", ratesLine).Set("rsOdds", oddsLine).Set("rsMastLine", MastLine(lucky))
            .SetStyle("rsMastLine", "color", lucky ? HitJackpot : Cream).SendTo(to);

    private static UIUpdate SetSpinButton(UIUpdate update, ItemCategory? category, SpinState state, int freeSpins)
    {
        update.Set("rsSpinText", SpinText(state, freeSpins))
              .SetStyle("rsSpinCost", "visibility", state == SpinState.Ready ? "visible" : "collapse")
              .SetStyle("rsSpinFrame", "border", $"2px solid {FrameColor(state)}");
        foreach (var (name, value) in SpinBrushStyle(state))
            update.SetStyle("rsSpinBrush", name, value);
        foreach (var (name, value) in SpinTextStyle(state))
            update.SetStyle("rsSpinText", name, value);
        return update;
    }
}
