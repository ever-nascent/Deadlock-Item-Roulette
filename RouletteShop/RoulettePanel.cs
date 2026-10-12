using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;

namespace RouletteShop;

/// <summary>
/// The roulette panel, built on the server the way the Deadworks TicTacToe example builds its board: players need only the
/// Deadworks client bootstrap, no addon. It is the roulette sheet of mockups/catalog.html (design #3, approved by the user):
/// one sheet that sits beside the game's own shop, which stays open. Masthead, category and price pickers (the server
/// can't see the game's tabs, so clicking an item in the game's shop or these buttons picks the box), a vertical reel,
/// the soul count, the result, the SPIN button and the fine print. Art and fonts are the game's own: every path is in the
/// game's pak01 (pak01_dir.txt) or used by its shop stylesheets, and "Retail Demo" / "Radiance" are font families its
/// stylesheets name.
/// </summary>
public static class RoulettePanel
{
    public const string Id = "roulette";

    // Where the sheet goes. The game's shop (citadel_hud_hero_shop.css) puts its 1200-wide item page on the right and the
    // hero, souls and recent purchases on the left; the page's left edge is about 325 (16:9) to 350 (21:9) left of the
    // screen's middle, 20 from the top, 960 tall. The sheet covers the left part, its right edge 370 left of the middle.
    // Its size is the mockup's sheet (346x748) scaled to the page's height.
    public const int SheetWidth = 444, SheetHeight = 960, SheetTop = 20, SheetRightFromCenter = 370;

    // Reel geometry: five cards on show, the middle one wins (mockup STEP and WIN, at this scale).
    public const int CardWidth = 78, CardHeight = 122, CardGap = 4, Step = CardHeight + CardGap;
    public const int StripLength = 30, Winner = 27;
    private const int WindowHeight = 5 * CardHeight + 4 * CardGap;
    public const int SpinMs = 4000;
    public const string SpinDuration = "4s";

    /// <summary>
    /// Wait before turning a new strip: the transition needs the strip on the client first, and the strip (the biggest
    /// update) can take a few frames of the UI channel's 16 KB/s to arrive.
    /// </summary>
    public const int TurnDelayMs = 500;

    private const string Ink = "#1a1510", Black = "#100d0a", Cream = "#eadfc6", Mint = "#9affd6", Red = "#b3261a", HitCorrupted = "#ff5a40";
    private const string Display = "Retail Demo", Body = "Radiance";

    private const string SoulIcon = "s2r://panorama/images/hud/icons/icon_soul.vsvg";
    private const string CorruptedIcon = "s2r://panorama/images/hud/icons/icon_corrupted_souls.vsvg";
    private const string CorruptedFrame = "s2r://panorama/images/shop/corrupted_items/item_frame_corrupted_psd.vtex";
    private const string SheetPaper = "s2r://panorama/images/shop/catalog/catalog_shop_filter_bg_psd.vtex";
    private const string SlipPaper = "s2r://panorama/images/shop/catalog/cards/shopitem_papertexture01_psd.vtex";

    /// <summary>Per-category colours (mockups/catalog.html [data-cat]); PaperX picks the category's band of the sheet paper.</summary>
    private sealed record Palette(string Tab, string Line, string Acc, string Deep, string T4Name, int PaperX);

    private static Palette Colors(ItemCategory c) => c switch
    {
        ItemCategory.Weapon => new("#e4b20c", "#b9772a", "#d99a3a", "#7c4610", "#e4c5a1", -19),
        ItemCategory.Vitality => new("#a5ce3c", "#4c7a48", "#a9d84a", "#2c5a26", "#c2e7cd", -952),
        _ => new("#b866de", "#66508c", "#c878f0", "#5d3190", "#e3d4f2", -488),
    };

    private static string Slug(ItemCategory c) => c.ToString().ToLowerInvariant();

    private static string CardBacker(RouletteItem i) => $"s2r://panorama/images/shop/catalog/cards/card_backer_{Slug(i.Category)}_t{i.Tier}_psd.vtex";

    private static string TabIcon(ItemCategory c) => $"s2r://panorama/images/shop/catalog/catalog_shop_tab_icon_{Slug(c)}_psd.vtex";

    public static string Souls(int souls) => souls.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    private static string Px(double v) => $"{Math.Round(v)}px";

    /// <summary>Places a node at x, y (from its parent's top left) with a fixed size.</summary>
    private static T At<T>(this T n, double x, double y, double w, double h) where T : UINode
        => n.WithStyles(("margin-left", Px(x)), ("margin-top", Px(y)), ("width", Px(w)), ("height", Px(h)));

    private static T Font<T>(this T n, string family, double size, string color, double spacing = 0) where T : UINode
        => n.WithStyles(("font-family", family), ("font-weight", "bold"), ("font-size", Px(size)), ("color", color),
                        ("letter-spacing", $"{spacing:0.#}px"), ("text-transform", "uppercase"));

    private static T Centered<T>(this T n) where T : UINode => n.WithStyles(("horizontal-align", "center"), ("vertical-align", "center"));

    /// <summary>What one player's panel shows.</summary>
    public sealed record View(
        ItemCategory Category, int Tier, int Souls, RouletteItem[] Pool, ISet<string> Owned, RouletteItem[] Reel,
        bool ViaShopKey, double CorruptedChancePercent, RouletteItem? Landed, bool LandedCorrupted);

    // ─── the sheet ────────────────────────────────────────────────────────────

    // Sheet coordinates: 18 in from the sides; the side column (souls, result, SPIN) right of the reel.
    private const int Side = 136, SideWidth = 290, MachineTop = 239, MachineHeight = WindowHeight + 26;

    public static UINode Layout(View v)
    {
        var pal = Colors(v.Category);
        int unowned = v.Pool.Count(i => !v.Owned.Contains(i.ClassName));
        bool hitCorrupted = v.Landed != null && v.LandedCorrupted;

        // Centred, then pushed left by its right margin (alignment centres the margin box, as the game's own #Shop
        // relies on): the sheet's right edge lands SheetRightFromCenter left of the middle of the screen.
        // The hover style is there so it takes the mouse (hittest): clicks on the sheet don't reach the game.
        return UI.Container("rsSheet")
            .WithStyles(("width", Px(SheetWidth)), ("height", Px(SheetHeight)), ("horizontal-align", "center"), ("vertical-align", "top"),
                        ("margin-top", Px(SheetTop)), ("margin-right", Px(2 * (SheetRightFromCenter + SheetWidth / 2))),
                        ("background-color", "#d9c9a6"), ("background-image", $"url(\"{SheetPaper}\")"),
                        ("background-size", "1452px 2182px"), ("background-position", $"{pal.PaperX}px 0px"),
                        ("background-repeat", "no-repeat"), ("box-shadow", "0px 6px 18px #00000099"))
            .WithHoverStyle("opacity", "1")
            .Add(Mast(pal, closable: !v.ViaShopKey),
                 Categories(v.Category),
                 Prices(v.Tier, pal),
                 Reel(v, hitCorrupted),
                 SoulsRow(v.Souls),
                 UI.Container("rsResultBox").At(Side, 307, SideWidth, 411).WithStyles(("background-color", "#e7dcc3"),
                         ("background-image", $"url(\"{SlipPaper}\")"), ("background-size", "100% 100%"), ("background-img-opacity", "0.5"),
                         ("box-shadow", "2px 3px 0px #0000004d"))
                     .Add(v.Landed is { } landed ? Result("rsResult0", landed, v.LandedCorrupted) : Placeholder("rsResult0")),
                 UI.Label("rsDraw", DrawText(unowned)).At(Side, 730, SideWidth, 20).Font(Body, 15, Ink, 2.5)
                     .WithStyles(("text-align", "center"), ("text-overflow", "shrink"), ("white-space", "nowrap")),
                 SpinButton(v, pal),
                 Footer(v, pal));
    }

    private static string DrawText(int unowned) => unowned > 0 ? $"RANDOM PICK · 1 OF {unowned}" : "YOU OWN THIS WHOLE BOX";

    private static UINode Mast(Palette pal, bool closable)
    {
        var mast = UI.Container("rsMast").At(18, 20, 408, 85).WithStyle("background-color", Black).Add(
            UI.Container().At(5, 5, 398, 75).WithStyle("border", $"2px solid {pal.Acc}"),
            UI.Label("", "ROULETTE SHOP").At(0, 15, 408, 37).Font(Display, 36, Cream, 1)
                .WithStyles(("text-align", "center"), ("line-height", "37px"), ("text-shadow", $"2px 2px 0px {pal.Line}")),
            UI.Label("", "ONE SPIN · ONE ITEM · NO REFUNDS").At(0, 57, 408, 15).Font(Body, 15, pal.Acc, 3)
                .WithStyles(("text-align", "center"), ("line-height", "15px")));
        // Opened from chat there is no shop to close it, so it gets a button of its own.
        if (closable)
            mast.Add(UI.Button("rsClose", "").OnClick("close").At(368, 10, 30, 30)
                .WithStyles(("background-color", pal.Acc), ("border-radius", "4px"))
                .WithHoverStyle("brightness", "1.2")
                .Add(UI.Label("", "X").Font(Display, 20, Ink).Centered()));
        return mast;
    }

    // ─── pickers: the category (the game's tab colours and icons) and the price (black tapes, as on the page) ───

    private static UINode Categories(ItemCategory selected)
    {
        var row = UI.Horizontal("rsTabs").At(18, 117, 408, 46);
        var all = Enum.GetValues<ItemCategory>();
        foreach (var c in all)
        {
            bool on = c == selected;
            row.Add(UI.Button("rsTab" + c, "").OnClick("cat", Slug(c))
                .WithStyles(("width", "130px"), ("height", "46px"), ("margin-right", c == all[^1] ? "0px" : "9px"), ("border-radius", "6px"),
                            ("background-color", Colors(c).Tab), ("opacity", on ? "1" : "0.55"))
                .WithStyles(on ? [("border", $"3px solid {Black}")] : [])
                .WithHoverStyle("opacity", on ? "1" : "0.85")
                .Add(UI.Horizontal().Centered().Add(
                    UI.Image("", TabIcon(c)).WithStyles(("width", "30px"), ("height", "30px"), ("vertical-align", "center")),
                    UI.Label("", Slug(c)).Font(Display, 17, Ink, 1).WithStyles(("margin-left", "6px"), ("vertical-align", "center")))));
        }
        return row;
    }

    private static UINode Prices(int selected, Palette pal)
    {
        var row = UI.Horizontal("rsPrices").At(18, 175, 408, 52);
        for (int k = 1; k <= ItemPool.TierPrices.Length; k++)
        {
            bool on = k == selected;
            row.Add(UI.Button("rsPrice" + k, "").OnClick("tier", k.ToString())
                .WithStyles(("width", "93px"), ("height", "42px"), ("margin-top", "5px"), ("margin-right", k < ItemPool.TierPrices.Length ? "12px" : "0px"),
                            ("transform", on ? "translateY(-3px) scale3d(1.08, 1.08, 1)" : "scale3d(0.94, 0.94, 1)"))
                .WithHoverStyle("brightness", on ? "1" : "1.15")
                .Add(Tape(k, ItemPool.TierPrices[k - 1].ToString(), on ? 28 : 25, on ? Mint : "#64a58c", pal)));
        }
        return row;
    }

    /// <summary>A price label: black tape, mint number, plain to ornate by tier (the shop page's tier labels).</summary>
    private static UINode Tape(int tier, string text, double size, string color, Palette pal)
    {
        var tape = UI.Container().WithStyles(("width", "100%"), ("height", "100%"), ("background-color", Black), ("transform", "rotateZ(-5deg)"));
        tape.WithStyles(tier switch
        {
            1 => [("border-radius", "2px")],
            2 => [("border-radius", "5px"), ("border", "1px solid #7d776c")],
            3 => [("border-radius", "7px"), ("border", "2px solid #cfc7b6bf")],
            _ => [("border-radius", "4px"), ("border-bottom", $"3px solid {pal.Acc}")],
        });
        return tape.Add(UI.Label("", text).Font(Display, size, color).Centered()
            .WithStyles(("text-shadow", "0px 1px 3px #000000b3"), ("text-transform", "none")));
    }

    // ─── item card, after the real shop's (backer by category and tier, icon 64%, name plate 36%) ───

    public static UIContainer Card(string id, RouletteItem i, double w, bool corrupted = false)
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
                    ("color", i.Tier == 4 ? pal.T4Name : Ink))));
        if (i.Imbue)
        {
            double tag = Math.Round(font * 0.615 * 1.25 + 4);
            card.Add(UI.Label("", "IMBUE").At(0, icon - tag, w, tag).WithStyles(("background-color", "#5a3a8a"), ("color", "#d4b8f0"),
                ("font-family", Display), ("font-weight", "bold"), ("font-size", Px(font * 0.615)), ("letter-spacing", "1px"),
                ("text-align", "center"), ("border-radius", "3px")));
        }
        if (corrupted)
            card.Add(UI.Container().At(0, 0, w, h).WithStyles(("background-image", $"url(\"{CorruptedFrame}\")"),
                                                              ("background-size", "100% 100%")));
        return card;
    }

    // ─── the reel: a black tape, five cards on show, the middle one ringed between two pointers ───

    private static UINode Reel(View v, bool hitCorrupted)
    {
        string hit = hitCorrupted ? HitCorrupted : Mint;
        int middle = 13 + 2 * Step, ring = middle - 6;
        return UI.Container("rsReel").At(18, MachineTop, 104, MachineHeight).WithStyles(("background-color", Black), ("border-radius", "3px"),
                ("overflow", "clip"), ("box-shadow", "2px 3px 0px #0000004d"))
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
            strip.Add(Card($"{id}c{k}", cards[k], CardWidth).WithStyle("margin-bottom", Px(CardGap)));
        return strip;
    }

    // ─── souls, the result, SPIN, the fine print ──────────────────────────────

    private static UINode SoulsRow(int souls) => UI.Horizontal().At(Side, MachineTop, SideWidth, 56).Add(
        UI.Horizontal().WithStyles(("vertical-align", "center"), ("height", "46px"), ("margin-left", "4px"), ("padding", "0px 14px 0px 12px"),
                                   ("background-color", Black), ("border-radius", "3px"), ("transform", "rotateZ(-5deg)")).Add(
            UI.Image("", SoulIcon).WithStyles(("width", "17px"), ("height", "33px"), ("vertical-align", "center"), ("margin-right", "6px"),
                                              ("wash-color", Mint)),
            UI.Label("rsSouls", Souls(souls)).Font(Display, 35, Mint).WithStyles(("vertical-align", "center"), ("text-shadow", "0px 1px 3px #000000b3"))),
        UI.Label("", "YOUR\nSOULS").Font(Body, 15, Ink, 2.5).WithStyles(("vertical-align", "center"), ("margin-left", "14px"), ("line-height", "18px")));

    /// <summary>The result's frame: a heading over a ruled line, the body centred below it.</summary>
    private static UINode ResultFrame(string id, string heading, UINode body) => UI.Vertical(id)
        .WithStyles(("width", "100%"), ("height", "100%"), ("padding", "14px 15px 15px 15px")).Add(
            UI.Label("", heading).Font(Body, 17, Ink, 4).WithStyles(("width", "100%"), ("text-align", "center"), ("padding-bottom", "8px"),
                                                                   ("border-bottom", $"2px solid {Ink}")),
            UI.Container().WithStyles(("width", "100%"), ("height", "fill-parent-flow(1.0)")).Add(body));

    private static UIContainer ResultBody() => UI.Vertical().WithStyles(("width", "100%"), ("vertical-align", "center"));

    private static UINode ItemName(string text) => UI.Label("", text).Font(Display, 27, Ink)
        .WithStyles(("width", "100%"), ("text-align", "center"), ("text-transform", "none"), ("text-overflow", "shrink"), ("white-space", "nowrap"));

    public static UINode Placeholder(string id) => ResultFrame(id, "NO SPIN YET", ResultBody().Add(
        UI.Label("", "?").Font(Display, 64, "#1a151066").WithStyles(("width", "112px"), ("height", "175px"), ("horizontal-align", "center"),
            ("margin-bottom", "14px"), ("border", "2px solid #1a151080"), ("border-radius", "8px"), ("text-align", "center"),
            ("padding-top", "50px"), ("transform", "rotateZ(-3deg)")),
        ItemName("Pick a box"),
        UI.Label("", "Click an item in the shop, or pick a category and a price above, then spin.").WithStyles(("width", "100%"),
            ("margin-top", "8px"), ("text-align", "center"), ("font-family", Body), ("font-weight", "semi-bold"), ("font-size", "18px"),
            ("line-height", "23px"), ("color", Ink))));

    public static UINode Message(string id, string title, string body) => ResultFrame(id, title.ToUpperInvariant(), ResultBody().Add(
        UI.Label("", body).WithStyles(("width", "100%"), ("text-align", "center"), ("font-family", Body), ("font-weight", "semi-bold"),
            ("font-size", "18px"), ("line-height", "23px"), ("color", Ink))));

    public static UINode Result(string id, RouletteItem item, bool corrupted)
    {
        var pal = Colors(item.Category);
        var body = ResultBody().Add(
            Card("", item, 112, corrupted).WithStyles(("horizontal-align", "center"), ("margin-bottom", "14px"), ("transform", "rotateZ(-3deg)"),
                                                      ("box-shadow", "0px 6px 14px #00000073")),
            ItemName(item.Name),
            UI.Horizontal().WithStyles(("horizontal-align", "center"), ("margin-top", "11px")).Add(
                UI.Label("", Slug(item.Category)).Font(Display, 19, pal.Deep, 1).WithStyle("vertical-align", "center"),
                UI.Horizontal().WithStyles(("margin-left", "11px"), ("vertical-align", "center"), ("padding", "4px 9px 2px 7px"),
                                           ("background-color", Black), ("border-radius", "3px"), ("transform", "rotateZ(-5deg)")).Add(
                    UI.Image("", SoulIcon).WithStyles(("width", "12px"), ("height", "23px"), ("vertical-align", "center"), ("margin-right", "5px"),
                                                      ("wash-color", Mint)),
                    UI.Label("", Souls(ItemPool.TierPrices[item.Tier - 1])).Font(Display, 23, Mint).WithStyle("vertical-align", "center"))));
        if (corrupted)
            body.Add(UI.Horizontal().WithStyles(("horizontal-align", "center"), ("margin-top", "13px"), ("padding", "6px 11px 4px 11px"),
                                                ("border", $"3px solid {Red}"), ("border-radius", "4px"), ("transform", "rotateZ(-4deg)")).Add(
                UI.Image("", CorruptedIcon).WithStyles(("width", "24px"), ("height", "24px"), ("margin-right", "8px"), ("wash-color", Red),
                                                       ("vertical-align", "center")),
                UI.Label("", "CORRUPTED").Font(Display, 22, Red, 2).WithStyle("vertical-align", "center")));
        return ResultFrame(id, "YOU GOT", body);
    }

    public enum SpinState { Ready, Busy, Broke }

    /// <summary>SPIN: a block in the category's tab colour, the price on a black tape.</summary>
    private static UINode SpinButton(View v, Palette pal)
    {
        var state = v.Souls < ItemPool.TierPrices[v.Tier - 1] ? SpinState.Broke : SpinState.Ready;
        return UI.Button("rsSpin", "").OnClick("spin").At(Side, 762, SideWidth, 123)
            .WithStyles(("border-radius", "5px"), ("box-shadow", "0px 6px 0px #0c0906"))
            .WithStyles(SpinStyle(state, v.Category))
            .Add(UI.Container("rsSpinFrame").At(4, 4, SideWidth - 8, 115).WithStyles(("border", $"2px solid {FrameColor(state)}"), ("border-radius", "3px")),
                 UI.Horizontal().Centered().Add(
                     UI.Label("rsSpinText", SpinText(state)).Font(Display, 46, Ink, 2).WithStyle("vertical-align", "center")
                         .WithStyles(SpinTextStyle(state)),
                     UI.Horizontal("rsSpinCost").WithStyles(("margin-left", "14px"), ("vertical-align", "center"), ("padding", "5px 10px 3px 8px"),
                                                            ("background-color", Black), ("border-radius", "3px"), ("transform", "rotateZ(-5deg)"),
                                                            ("visibility", state == SpinState.Ready ? "visible" : "collapse")).Add(
                         UI.Image("", SoulIcon).WithStyles(("width", "15px"), ("height", "29px"), ("vertical-align", "center"),
                                                           ("margin-right", "5px"), ("wash-color", Mint)),
                         UI.Label("", Souls(ItemPool.TierPrices[v.Tier - 1])).Font(Display, 29, Mint).WithStyles(
                             ("vertical-align", "center"), ("text-shadow", "0px 1px 3px #000000b3")))));
    }

    private static string FrameColor(SpinState s) => s switch
    {
        SpinState.Ready => "#1a1510cc",
        SpinState.Busy => "#1a151080",
        _ => "#7d1a10",
    };

    private static (string, string)[] SpinStyle(SpinState s, ItemCategory c) => s switch
    {
        SpinState.Ready => [("background-color", Colors(c).Tab)],
        SpinState.Busy => [("background-color", "#9d927f")],
        _ => [("background-color", "#b3a78f")],
    };

    private static string SpinText(SpinState s) => s switch
    {
        SpinState.Ready => "SPIN",
        SpinState.Busy => "SPINNING...",
        _ => "NOT ENOUGH SOULS",
    };

    private static (string, string)[] SpinTextStyle(SpinState s) => s switch
    {
        SpinState.Ready => [("font-size", "46px"), ("color", Ink)],
        SpinState.Busy => [("font-size", "28px"), ("color", Ink)],
        _ => [("font-size", "25px"), ("color", "#7d1a10")],
    };

    private static UINode Footer(View v, Palette pal)
    {
        var footer = UI.Vertical().At(18, 901, 408, 46);
        foreach (string line in new[] { "Selling is disabled while Roulette Shop is on",
                                        $"{v.CorruptedChancePercent:0.##}% of 3,200 and 6,400 spins come back Corrupted" })
            footer.Add(UI.Horizontal().WithStyles(("width", "100%"), ("height", "23px")).Add(
                UI.Container().WithStyles(("width", "7px"), ("height", "7px"), ("vertical-align", "center"), ("margin-left", "2px"),
                                          ("background-color", pal.Deep), ("transform", "rotateZ(45deg)")),
                UI.Label("", line).WithStyles(("width", "fill-parent-flow(1.0)"), ("vertical-align", "center"), ("margin-left", "9px"),
                    ("font-family", Body), ("font-weight", "bold"), ("font-size", "15px"), ("color", Ink), ("text-overflow", "shrink"),
                    ("white-space", "nowrap"))));
        return footer;
    }

    // ─── updates after the first build (each one a few ops, not a rebuild) ────

    private static UIPanel Panel => UI.Panel(Id);

    /// <summary>Swaps in a spin strip (a fresh id: a removed node lingers a frame) and greys the spin button.</summary>
    public static void StartSpin(RecipientFilter to, string oldStripId, string stripId, RouletteItem[] strip, ItemCategory category)
    {
        Panel.RemoveChild(to, oldStripId);
        Panel.AppendChild(to, "rsWindow", Strip(stripId, strip));
        SetSpinButton(Panel.Build(), category, SpinState.Busy).SendTo(to);
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

    /// <summary>After the reel stops: the ring turns red for a Corrupted item; souls, the odds and the button update.</summary>
    public static void ShowLanding(RecipientFilter to, RouletteItem landed, bool corrupted, int souls, int unowned, SpinState state)
    {
        var update = Panel.Build().Set("rsSouls", Souls(souls)).Set("rsDraw", DrawText(unowned));
        if (corrupted)
            update.SetStyle("rsRing", "border", $"3px solid {HitCorrupted}")
                  .SetStyle("rsPtrL", "background-color", HitCorrupted)
                  .SetStyle("rsPtrR", "background-color", HitCorrupted);
        SetSpinButton(update, landed.Category, state).SendTo(to);
    }

    /// <summary>The soul count, and the spin button unless a spin is turning.</summary>
    public static void UpdateSouls(RecipientFilter to, int souls, ItemCategory category, SpinState? state)
    {
        var update = Panel.Build().Set("rsSouls", Souls(souls));
        if (state is { } s)
            SetSpinButton(update, category, s);
        update.SendTo(to);
    }

    private static UIUpdate SetSpinButton(UIUpdate update, ItemCategory category, SpinState state)
    {
        update.Set("rsSpinText", SpinText(state))
              .SetStyle("rsSpinCost", "visibility", state == SpinState.Ready ? "visible" : "collapse")
              .SetStyle("rsSpinFrame", "border", $"2px solid {FrameColor(state)}");
        foreach (var (name, value) in SpinStyle(state, category))
            update.SetStyle("rsSpin", name, value);
        foreach (var (name, value) in SpinTextStyle(state))
            update.SetStyle("rsSpinText", name, value);
        return update;
    }
}
