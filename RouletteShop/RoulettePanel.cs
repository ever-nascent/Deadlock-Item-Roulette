using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;

namespace RouletteShop;

/// <summary>
/// The roulette panel, built on the server the way the Deadworks TicTacToe example builds its board: players need only the
/// Deadworks client bootstrap, no addon. Layout and colours follow the approved mockup, mockup.html (1280x760: category
/// tabs, the catalog page with masthead and steps, the reel, the item pool with the price labels on its edge, the sales
/// slip, the SPIN block). Art and fonts are the game's own: every path is in the game's pak01 (pak01_dir.txt) or used by
/// its shop stylesheets, and "Retail Demo" / "Radiance" are font families its stylesheets name.
/// </summary>
public static class RoulettePanel
{
    public const string Id = "roulette";

    // Reel geometry (mockup.html: W = 140, STEP = W + 12, WIN = 27, LEN = 30).
    public const int CardWidth = 140, CardGap = 12, Step = CardWidth + CardGap;
    public const int StripLength = 30, Winner = 27;
    public const int SpinMs = 4000;
    public const string SpinDuration = "4s";

    /// <summary>
    /// Wait before turning a new strip: the transition needs the strip on the client first, and the strip (the biggest
    /// update) can take a few frames of the UI channel's 16 KB/s to arrive.
    /// </summary>
    public const int TurnDelayMs = 500;

    private const string Ink = "#1a1510", Black = "#110d09", Cream = "#ece2ca", Mint = "#9affd6", Red = "#b3261a", Paper = "#eadfc6";
    private const string Display = "Retail Demo", Body = "Radiance";

    private const string SoulIcon = "s2r://panorama/images/hud/icons/icon_soul.vsvg";
    private const string CorruptedIcon = "s2r://panorama/images/hud/icons/icon_corrupted_souls.vsvg";
    private const string CorruptedFrame = "s2r://panorama/images/shop/corrupted_items/item_frame_corrupted_psd.vtex";
    private const string PaperArt = "s2r://panorama/images/shop/catalog/catalog_shop_generic_bg2_psd.vtex";
    private const string TabEdge = "s2r://panorama/images/shop/catalog/catalog_shop_tab_edge_overlay_psd.vtex";
    private const string Burst = "s2r://panorama/images/shop/catalog/backer_star_test_png.vtex";

    /// <summary>Per-category colours (mockup.html [data-cat]).</summary>
    private sealed record Palette(string Tab, string Acc, string Line, string Cap, string Tint, string Box4, string T4Name);

    private static Palette Colors(ItemCategory c) => c switch
    {
        ItemCategory.Weapon => new("#e4b20c", "#cc8932", "#a9731c", "#3a2a14", "#00000000", "#130d07", "#e4c5a1"),
        ItemCategory.Vitality => new("#a5ce3c", "#6dc04b", "#47743f", "#223c1c", "#3c696e40", "#090d08", "#c2e7cd"),
        _ => new("#b866de", "#c878f0", "#684a86", "#352447", "#967dbe40", "#19131f", "#e3d4f2"),
    };

    public static string TabColor(ItemCategory c) => Colors(c).Tab;

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

    /// <summary>What one player's panel shows.</summary>
    public sealed record View(
        ItemCategory Category, int Tier, int Souls, RouletteItem[] Pool, ISet<string> Owned, RouletteItem[] Reel,
        bool ViaShopKey, double CorruptedChancePercent, RouletteItem? Landed, bool LandedCorrupted);

    // ─── whole panel ──────────────────────────────────────────────────────────

    /// <summary>The backdrop behind the panel: opaque while the game's own shop may be open underneath it.</summary>
    public const string BackdropOpaque = "#070b0bff", BackdropClear = "#070b0b8c";

    public static UINode Layout(View v)
    {
        var pal = Colors(v.Category);
        var panel = UI.Container("rsPanel").WithStyles(("width", "1280px"), ("height", "760px"), ("horizontal-align", "center"),
                                                       ("vertical-align", "center"));
        panel.Add(Tabs(v.Category, v.ViaShopKey), Page(v, pal));

        // Full-screen backdrop: opaque while the game's own shop is open underneath (shop key), see-through otherwise.
        // The hover style is there so it takes the mouse (hittest).
        return UI.Container("rsRoot")
            .WithStyles(("width", "100%"), ("height", "100%"), ("background-color", v.ViaShopKey ? BackdropOpaque : BackdropClear),
                        ("transition-property", "background-color"), ("transition-duration", "0.3s"))
            .WithHoverStyle("opacity", "1")
            .Add(panel);
    }

    private static UINode Tabs(ItemCategory selected, bool viaShopKey)
    {
        var nav = UI.Vertical("rsTabs").At(8, 24, 84, 560);
        foreach (var c in Enum.GetValues<ItemCategory>())
        {
            bool on = c == selected;
            nav.Add(UI.Button("rsTab" + c, "").OnClick("cat", Slug(c))
                .WithStyles(("width", "84px"), ("height", on ? "175px" : "73px"), ("margin-bottom", "8px"),
                            ("border-radius", "8px 0px 0px 8px"), ("background-color", Colors(c).Tab))
                .WithHoverStyle("brightness", on ? "1" : "1.12")
                .Add(UI.Image("", TabIcon(c)).WithStyles(("width", "60px"), ("height", "60px"), ("horizontal-align", "center"),
                                                         ("vertical-align", "center"), ("margin-right", "12px")),
                     UI.Container().WithStyles(("width", "100%"), ("height", "100%"), ("background-image", $"url(\"{TabEdge}\")"),
                                               ("background-size", "100% 100%"), ("opacity", "0.6"))));
        }
        // Opened from chat there is no shop to close it, so it gets a button of its own.
        if (!viaShopKey)
            nav.Add(UI.Button("rsClose", "").OnClick("close")
                .WithStyles(("width", "84px"), ("height", "44px"), ("margin-top", "12px"), ("background-color", Black),
                            ("border-radius", "8px 0px 0px 8px"))
                .WithHoverStyle("background-color", "#2a2420")
                .Add(UI.Label("", "CLOSE").Font(Display, 15, Cream, 1).WithStyles(("horizontal-align", "center"), ("vertical-align", "center"))));
        return nav;
    }

    private static UINode Page(View v, Palette pal)
    {
        var page = UI.Container("rsPage").At(80, 6, 1192, 748).WithStyles(
            ("background-image", $"url(\"{PaperArt}\")"), ("background-size", "100% 165%"), ("background-color", "#cdbd98"));
        page.Add(
            UI.Container().At(0, 0, 1192, 748).WithStyle("background-color", pal.Tint),
            UI.Container().At(0, 0, 12, 748).WithStyle("background-color", pal.Tab),
            Mast(pal),
            Steps(),
            Reel(v, pal),
            PoolBox(v, pal),
            Prices(v, pal),
            Slip(v),
            SpinButton(v, pal));
        return page;
    }

    private static UINode Mast(Palette pal) => UI.Container().At(28, 14, 404, 72).WithStyle("background-color", Black).Add(
        UI.Container().At(5, 5, 394, 62).WithStyle("border", $"2px solid {pal.Acc}"),
        UI.Label("", "ROULETTE SHOP").At(0, 10, 404, 41).Font(Display, 41, Cream, 2)
            .WithStyles(("text-align", "center"), ("line-height", "41px"), ("text-shadow", $"2px 2px 0px {pal.Acc}")),
        UI.Label("", "ITEMS OF CHANCE · BOUGHT, NEVER SOLD").At(0, 51, 404, 13).Font(Display, 12, pal.Acc, 3)
            .WithStyles(("text-align", "center"), ("line-height", "13px")));

    private static UINode Steps()
    {
        var steps = UI.Vertical().At(456, 14, 340, 72);
        string[] lines = ["PICK A DEPARTMENT", "PICK A PRICE", "SPIN — KEEP WHAT LANDS"];
        for (int k = 0; k < lines.Length; k++)
            steps.Add(UI.Horizontal().WithStyle("height", "24px").Add(
                UI.Label("", (k + 1).ToString()).Font(Display, 13, Cream).WithStyles(("width", "20px"), ("height", "20px"),
                    ("border-radius", "50%"), ("background-color", Black), ("text-align", "center"), ("vertical-align", "center"),
                    ("line-height", "21px")),
                UI.Label("", lines[k]).Font(Display, 15, Ink, 1).WithStyles(("margin-left", "9px"), ("vertical-align", "center"))));
        return steps;
    }

    // ─── price labels (black tape, mint number, plain to ornate by tier) ──────

    private static UINode Tape(string id, int tier, string text, double size, string color, Palette pal)
    {
        var tape = UI.Container(id).WithStyles(("height", "40px"), ("min-width", "94px"), ("padding", "0px 13px 0px 13px"),
                                               ("background-color", Black), ("transform", "rotateZ(-5deg)"));
        if (tier == 2)
            tape.WithStyles(("border-radius", "5px"), ("border", "1px solid #7d776c"));
        else if (tier == 3)
            tape.WithStyles(("border-radius", "7px"), ("border", "2px solid #cfc7b6bf"));
        else if (tier == 4)
            tape.WithStyles(("border-radius", "4px"), ("border-bottom", $"3px solid {pal.Acc}"));
        return tape.Add(UI.Label("", text).Font(Display, size, color).WithStyles(("horizontal-align", "center"),
            ("vertical-align", "center"), ("text-shadow", "0px 1px 3px #000000b3"), ("text-transform", "none")));
    }

    private static double TapeSize(int tier) => tier switch { 1 => 24, 4 => 28, _ => 26 };

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

    // ─── the reel ─────────────────────────────────────────────────────────────

    private static UINode Reel(View v, Palette pal)
    {
        var reel = UI.Container("rsReel").At(28, 98, 792, 288).WithStyles(("border", $"3px solid {pal.Line}"),
            ("background-color", "#fff4dc38"), ("overflow", "clip"));

        // Behind the cards: rings (spirit: rays) and the bullseye under the winning card.
        if (v.Category == ItemCategory.Spirit)
        {
            for (int k = 0; k < 15; k++)
                reel.Add(UI.Container().At(393, 141 - 25, 520, 50).WithStyles(("background-color", pal.Line + "4d"),
                    ("transform-origin", "0% 50%"), ("transform", $"rotateZ({4 + 24 * k}deg)")));
        }
        else
        {
            for (int k = 5; k >= 2; k--)
            {
                double d = 2 * (72 * k);
                reel.Add(UI.Container().At(393 - d / 2, 141 - d / 2, d, d).WithStyles(("border-radius", "50%"),
                    ("border", $"2px solid {pal.Line}8c")));
            }
        }
        reel.Add(
            UI.Container().At(393 - 126, 141 - 126, 252, 252).WithStyles(("border-radius", "50%"), ("background-color", pal.Acc + "b8")),
            UI.Container().At(4, 4, 778, 274).WithStyle("border", $"1px solid {pal.Line}"),
            UI.Label("", $"{Slug(v.Category)} · {Souls(ItemPool.TierPrices[v.Tier - 1])} DRAW").At(17, 9, 360, 16).Font(Display, 13, pal.Cap, 2),
            UI.Label("", "MIDDLE CARD WINS").At(406, 9, 360, 16).Font(Display, 13, pal.Cap, 2).WithStyle("text-align", "right"),
            UI.Container("rsWindow").At(19, 28, 748, 227).WithStyle("overflow", "clip")
                .Add(Strip("rsStrip0", v.Reel, landed: v.Landed != null)),
            UI.Container().At(393 - 79, 141 - 119, 158, 238).WithStyles(("border", $"4px solid {Black}"), ("border-radius", "11px")),
            // The pointers: squares turned 45 degrees on the reel's top and bottom edges, half clipped into triangles.
            UI.Container().At(393 - 16, -16, 32, 32).WithStyles(("background-color", Black), ("transform", "rotateZ(45deg)")),
            UI.Container().At(393 - 16, 282 - 16, 32, 32).WithStyles(("background-color", Black), ("transform", "rotateZ(45deg)")));
        return reel;
    }

    public static UINode Strip(string id, IReadOnlyList<RouletteItem> cards, bool landed = false)
    {
        var strip = UI.Horizontal(id).WithStyles(("width", $"{cards.Count * Step}px"), ("height", "227px"),
                                                 ("transform", "translateX(0px)"));
        for (int k = 0; k < cards.Count; k++)
        {
            var card = Card($"{id}c{k}", cards[k], CardWidth).WithStyles(("margin-right", $"{CardGap}px"), ("margin-top", "4px"));
            if (landed && k == 2)
                card.WithStyles(("transform", "scale3d(1.05, 1.05, 1)"), ("box-shadow", "0px 6px 16px #0000008c"));
            strip.Add(card);
        }
        return strip;
    }

    // ─── the pool: one wide tier box with the four price labels on its top edge ───

    public static string CellId(RouletteItem i) => "rsCell_" + i.ClassName;

    /// <summary>One or two rows, whichever gives the bigger card in the 756x260 grid area (mockup.html).</summary>
    private static (int Rows, int Cols, double W) GridFit(int n)
    {
        double Fit(int rows)
        {
            int cols = (n + rows - 1) / rows;
            return Math.Floor(Math.Min((756.0 - (cols - 1) * 4) / cols, (260.0 - (rows - 1) * 4) / rows * 82 / 128));
        }
        int r = Fit(1) >= Fit(2) ? 1 : 2;
        return (r, (n + r - 1) / r, Fit(r));
    }

    private static UINode PoolBox(View v, Palette pal)
    {
        int t = v.Tier, unowned = v.Pool.Count(i => !v.Owned.Contains(i.ClassName));
        var box = UI.Container("rsPool").At(28, 426, 792, 312);
        var frame = UI.Container().At(0, 0, 792, 312);
        switch (t)
        {
            case 1: frame.WithStyles(("border", $"2px solid {pal.Line}"), ("background-color", "#fff4dc33")); break;
            case 2:
                frame.WithStyles(("border", $"3px solid {pal.Line}"), ("background-color", "#fff4dc3d"))
                     .Add(UI.Container().At(4, 4, 778, 298).WithStyle("border", $"1px solid {pal.Line}"));
                break;
            case 3:
                frame.WithStyles(("border", $"2px solid {pal.Line}"), ("background-color", "#fff4dc14"))
                     .Add(UI.Container().At(2, 2, 784, 304).WithStyle("border", $"2px solid {pal.Line}"));
                break;
            default:
                frame.WithStyles(("border", $"2px solid {pal.Box4}"), ("background-color", pal.Box4))
                     .Add(UI.Container().At(4, 4, 780, 300).WithStyle("border", $"1px solid {Cream}99"));
                break;
        }
        box.Add(frame);

        var (rows, cols, w) = GridFit(v.Pool.Length);
        if (cols * (w + 4) <= 610) // the emblem needs ~170px of free box
            box.Add(UI.Image("", TabIcon(v.Category)).At(792 - 22 - 150, 95, 150, 150).WithStyles(
                ("wash-color", t == 4 ? pal.Acc : pal.Cap), ("opacity", t == 4 ? "0.3" : "0.17")));

        string capColor = t == 4 ? Cream : pal.Cap;
        // Ends 8px in from the box edge (the mockup's 20px leaves the Tier 4 count no room: there it ran off the box).
        var caption = UI.Horizontal().At(440, 11, 344, 24).Add(
            UI.Label("", $"TIER {t}").Font(Display, 21, capColor).WithStyle("vertical-align", "bottom"));
        if (t == 4)
            caption.Add(UI.Label("", "EXPERTS ONLY").Font(Display, 15, pal.Acc).WithStyles(("margin-left", "8px"), ("vertical-align", "bottom")));
        caption.Add(UI.Label("", unowned > 0 ? $"{v.Pool.Length} ITEMS · 1 IN {unowned} EACH" : $"{v.Pool.Length} ITEMS · ALL OWNED")
            .Font(Body, 13, capColor, t == 4 ? 1 : 2).WithStyles(("width", "fill-parent-flow(1.0)"), ("text-align", "right"), ("vertical-align", "bottom"),
                                                    ("margin-left", "8px"), ("text-overflow", "shrink"), ("white-space", "nowrap")));
        box.Add(caption);

        double h = Math.Round(w * 128.0 / 82);
        double top = 40 + (260 - (rows * h + (rows - 1) * 4)) / 2;
        var grid = UI.Vertical("rsGrid").At(18, top, 756, rows * h + (rows - 1) * 4 + 8);
        for (int row = 0; row < rows; row++)
        {
            var line = UI.Horizontal().WithStyle("margin-bottom", "4px");
            foreach (var item in v.Pool.Skip(row * cols).Take(cols))
            {
                bool owned = v.Owned.Contains(item.ClassName);
                var cell = UI.Container(CellId(item)).WithStyles(("margin-right", "4px"), ("opacity", owned ? "0.4" : "1"))
                    .Add(Card("", item, w));
                if (owned)
                    cell.Add(UI.Label("", "OWNED").Font(Display, Math.Max(10, w * 0.14), Cream, 1).WithStyles(("horizontal-align", "center"),
                        ("vertical-align", "center"), ("padding", "3px 6px 2px 6px"), ("background-color", Black)));
                line.Add(cell);
            }
            grid.Add(line);
        }
        box.Add(grid);
        return box;
    }

    /// <summary>The four price labels on the pool box's top edge (page coordinates: Panorama clips a child outside its box).</summary>
    private static UINode Prices(View v, Palette pal)
    {
        int t = v.Tier;
        var prices = UI.Horizontal("rsPrices").At(28 + 10, 426 - 21, 520, 56);
        for (int k = 1; k <= ItemPool.TierPrices.Length; k++)
        {
            bool on = k == t;
            prices.Add(UI.Button("rsPrice" + k, "").OnClick("tier", k.ToString())
                .WithStyles(("margin-right", "12px"), ("margin-top", on ? "0px" : "4px"),
                            ("transform", on ? "translateY(-4px) scale3d(1.14, 1.14, 1)" : "scale3d(0.9, 0.9, 1)"))
                .WithHoverStyle("brightness", on ? "1" : "1.15")
                .Add(Tape("", k, ItemPool.TierPrices[k - 1].ToString(), TapeSize(k), on ? Mint : "#64a58c", pal)));
        }
        return prices;
    }

    /// <summary>Rings the cell a spin landed on and tags it YOURS.</summary>
    private static void MarkLanded(UIUpdate update, RouletteItem landed, bool corrupted)
    {
        string cell = CellId(landed);
        update.SetStyle(cell, "border", $"3px solid {(corrupted ? "#ff6a55" : Mint)}")
              .SetStyle(cell, "transform", "scale3d(1.06, 1.06, 1) rotateZ(-2deg)")
              .SetStyle(cell, "box-shadow", "0px 8px 14px #00000099")
              .SetStyle(cell, "z-index", "9");
    }

    private static UINode YoursBadge() => UI.Label("rsYours", "YOURS").Font(Display, 12, Mint, 1).WithStyles(
        ("horizontal-align", "center"), ("margin-top", "2px"), ("padding", "3px 6px 2px 6px"), ("background-color", Black),
        ("transform", "rotateZ(-5deg)"));

    // ─── the sales slip ───────────────────────────────────────────────────────

    private static UINode Slip(View v)
    {
        var paper = UI.Vertical("rsSlip").At(844, 14, 324, 606).WithStyles(("background-color", Paper), ("padding", "20px 18px 22px 18px"),
            ("transform", "rotateZ(1.1deg)"), ("box-shadow", "0px 5px 8px #00000080"));

        paper.Add(
            UI.Horizontal().WithStyle("width", "100%").Add(
                UI.Label("", "SALES SLIP").Font(Display, 12, Ink, 2.4),
                UI.Label("", "ALL SALES FINAL").Font(Display, 12, Ink, 2.4).WithStyles(("width", "fill-parent-flow(1.0)"), ("text-align", "right"))),
            UI.Container().WithStyles(("width", "100%"), ("height", "4px"), ("margin-top", "7px"), ("border-top", $"1px solid {Ink}"),
                                      ("border-bottom", $"1px solid {Ink}")),
            UI.Horizontal().WithStyles(("width", "100%"), ("height", "64px")).Add(
                UI.Label("", "YOUR SOULS").Font(Display, 14, Ink, 2.2).WithStyle("vertical-align", "center"),
                UI.Horizontal().WithStyles(("horizontal-align", "right"), ("vertical-align", "center"), ("height", "40px"),
                                           ("padding", "0px 14px 0px 12px"), ("background-color", Black), ("transform", "rotateZ(-5deg)")).Add(
                    UI.Image("", SoulIcon).WithStyles(("width", "14px"), ("height", "26px"), ("vertical-align", "center"), ("margin-right", "6px"),
                                                      ("wash-color", Mint)),
                    UI.Label("rsSouls", Souls(v.Souls)).Font(Display, 27, Mint).WithStyles(("vertical-align", "center"),
                        ("text-shadow", "0px 1px 3px #000000b3")))),
            UI.Container().WithStyles(("width", "100%"), ("height", "2px"), ("background-color", "#1a15108c")),
            UI.Container("rsResultBox").WithStyles(("width", "100%"), ("height", "fill-parent-flow(1.0)"))
                .Add(v.Landed is { } landed ? Result("rsResult0", landed, v.LandedCorrupted) : Placeholder("rsResult0")),
            UI.Container().WithStyles(("width", "100%"), ("height", "2px"), ("background-color", "#1a15108c")),
            UI.Label("", $"Selling is disabled while Roulette Shop is on\n{v.CorruptedChancePercent:0.##}% of 3,200 and 6,400 spins come back Corrupted"
                         + (v.ViaShopKey ? "\nESC or your shop key closes this" : ""))
                .WithStyles(("width", "100%"), ("margin-top", "9px"), ("text-align", "center"), ("font-family", Body), ("font-weight", "semi-bold"),
                            ("font-size", "12.5px"), ("line-height", "18px"), ("color", Ink)));
        return paper;
    }

    private static UIContainer GotBox(string burstColor, double burstOpacity, UINode inner) => UI.Container()
        .WithStyles(("width", "232px"), ("height", "228px"), ("horizontal-align", "center"), ("margin-top", "2px")).Add(
            UI.Image("", Burst).WithStyles(("width", "232px"), ("height", "228px"), ("wash-color", burstColor), ("opacity", $"{burstOpacity:0.##}")),
            inner);

    public static UINode Placeholder(string id) => UI.Vertical(id).WithStyles(("width", "100%"), ("vertical-align", "center")).Add(
        UI.Label("", "NO SALE YET").Font(Display, 14, Ink, 4.5).WithStyle("horizontal-align", "center"),
        GotBox(Ink, 0.13, UI.Label("", "?").Font(Display, 64, Ink).WithStyles(("width", "112px"), ("height", "175px"),
            ("horizontal-align", "center"), ("vertical-align", "center"), ("border", $"3px solid {Ink}"), ("border-radius", "8px"),
            ("background-color", Paper), ("text-align", "center"), ("padding-top", "48px"), ("transform", "rotateZ(-4deg)"))),
        UI.Label("", "PICK A DEPARTMENT\nAND A PRICE").Font(Display, 28, Ink).WithStyles(("horizontal-align", "center"), ("text-align", "center"),
            ("line-height", "27px"), ("margin-top", "4px")),
        UI.Label("", "THEN SPIN").Font(Body, 14, Ink, 2).WithStyles(("horizontal-align", "center"), ("margin-top", "6px")));

    public static UINode Message(string id, string title, string body) => UI.Vertical(id).WithStyles(("width", "100%"), ("vertical-align", "center")).Add(
        UI.Label("", title).Font(Display, 28, Ink).WithStyles(("horizontal-align", "center"), ("text-align", "center")),
        UI.Label("", body).WithStyles(("width", "100%"), ("margin-top", "10px"), ("text-align", "center"), ("font-family", Body),
            ("font-weight", "semi-bold"), ("font-size", "15px"), ("color", Ink)));

    public static UINode Result(string id, RouletteItem item, bool corrupted)
    {
        var pal = Colors(item.Category);
        var result = UI.Vertical(id).WithStyles(("width", "100%"), ("vertical-align", "center")).Add(
            UI.Label("", "YOU GOT").Font(Display, 14, Ink, 4.5).WithStyle("horizontal-align", "center"),
            GotBox(corrupted ? Red : pal.Acc, 1, Card("", item, 122, corrupted).WithStyles(("horizontal-align", "center"), ("vertical-align", "center"),
                ("transform", "rotateZ(-4deg)"), ("box-shadow", "0px 5px 12px #00000080"))),
            UI.Label("", item.Name).Font(Display, 28, Ink).WithStyles(("width", "100%"), ("text-align", "center"), ("margin-top", "4px"),
                                                                     ("text-overflow", "shrink"), ("white-space", "nowrap")),
            UI.Label("", $"{Slug(item.Category)} · {Souls(ItemPool.TierPrices[item.Tier - 1])} SOULS").Font(Body, 14, pal.Cap, 2)
                .WithStyles(("horizontal-align", "center"), ("margin-top", "6px")));
        if (corrupted)
            result.Add(UI.Horizontal().WithStyles(("horizontal-align", "center"), ("margin-top", "9px"), ("padding", "5px 12px 3px 9px"),
                                                  ("border", $"3px solid {Red}"), ("transform", "rotateZ(-3deg)")).Add(
                UI.Image("", CorruptedIcon).WithStyles(("width", "22px"), ("height", "22px"), ("margin-right", "8px"), ("wash-color", Red),
                                                       ("vertical-align", "center")),
                UI.Label("", "CORRUPTED").Font(Display, 20, Red, 3).WithStyle("vertical-align", "center")));
        return result;
    }

    // ─── SPIN: a black printing block, the price in the label's mint ──────────

    public enum SpinState { Ready, Busy, Broke }

    private static UINode SpinButton(View v, Palette pal)
    {
        var state = v.Souls < ItemPool.TierPrices[v.Tier - 1] ? SpinState.Broke : SpinState.Ready;
        return UI.Button("rsSpin", "").OnClick("spin").At(844, 640, 324, 92)
            .WithStyles(("background-color", Black), ("box-shadow", "0px 6px 0px #0000008c"))
            .WithStyles(SpinStyle(state, v.Category))
            .Add(UI.Container("rsSpinFrame").At(5, 5, 314, 82).WithStyle("border", $"2px solid {FrameColor(state, v.Category)}"),
                 UI.Horizontal().WithStyles(("horizontal-align", "center"), ("vertical-align", "center")).Add(
                     UI.Label("rsSpinText", SpinText(state)).Font(Display, 46, Cream, 3).WithStyle("vertical-align", "center")
                         .WithStyles(SpinTextStyle(state)),
                     UI.Horizontal("rsSpinCost").WithStyles(("margin-left", "18px"), ("vertical-align", "center"), ("transform", "rotateZ(-5deg)"),
                                                            ("visibility", state == SpinState.Ready ? "visible" : "collapse")).Add(
                         UI.Image("", SoulIcon).WithStyles(("width", "17px"), ("height", "32px"), ("vertical-align", "center"),
                                                           ("margin-right", "6px"), ("wash-color", Mint)),
                         UI.Label("", Souls(ItemPool.TierPrices[v.Tier - 1])).Font(Display, 36, Mint).WithStyles(
                             ("vertical-align", "center"), ("text-shadow", "0px 1px 3px #000000b3")))));
    }

    private static string FrameColor(SpinState s, ItemCategory c) => s switch
    {
        SpinState.Ready => Colors(c).Acc,
        SpinState.Busy => "#8a7d69",
        _ => "#e2705c",
    };

    private static (string, string)[] SpinStyle(SpinState s, ItemCategory c) => s switch
    {
        SpinState.Ready => [("background-color", Black)],
        _ => [("background-color", "#3b332a")],
    };

    private static string SpinText(SpinState s) => s switch
    {
        SpinState.Ready => "SPIN",
        SpinState.Busy => "SPINNING...",
        _ => "NOT ENOUGH SOULS",
    };

    private static (string, string)[] SpinTextStyle(SpinState s) => s switch
    {
        SpinState.Ready => [("font-size", "46px"), ("color", Cream)],
        SpinState.Busy => [("font-size", "30px"), ("color", Cream)],
        _ => [("font-size", "27px"), ("color", "#ffb4a4")],
    };

    // ─── updates after the first build (each one a few ops, not a rebuild) ────

    private static UIPanel Panel => UI.Panel(Id);

    /// <summary>Swaps in a spin strip (a fresh id: a removed node lingers a frame) and greys the spin button.</summary>
    public static void StartSpin(RecipientFilter to, string oldStripId, string stripId, RouletteItem[] strip, ItemCategory category)
    {
        Panel.RemoveChild(to, oldStripId);
        Panel.AppendChild(to, "rsWindow", Strip(stripId, strip));
        SetSpinButton(Panel.Build(), category, SpinState.Busy).SendTo(to);
    }

    /// <summary>Turns the reel until <see cref="Winner"/> sits under the pointer. Needs the strip to exist on the client first.</summary>
    public static void TurnReel(RecipientFilter to, string stripId)
        => Panel.Animate(to, stripId, "transform", $"translateX({-(Winner - 2) * Step}px)", SpinDuration, "ease-out");

    /// <summary>Replaces the result on the slip.</summary>
    public static void ShowResult(RecipientFilter to, string oldResultId, UINode result)
    {
        Panel.RemoveChild(to, oldResultId);
        Panel.AppendChild(to, "rsResultBox", result);
    }

    /// <summary>After the reel stops: lifts the winning reel card, rings the item in the pool, updates souls and the button.</summary>
    public static void ShowLanding(RecipientFilter to, string stripId, RouletteItem landed, bool corrupted, int souls, SpinState state)
    {
        var update = Panel.Build();
        MarkLanded(update, landed, corrupted);
        update.SetStyle($"{stripId}c{Winner}", "transform", "scale3d(1.05, 1.05, 1)")
              .SetStyle($"{stripId}c{Winner}", "box-shadow", "0px 6px 16px #0000008c")
              .Set("rsSouls", Souls(souls));
        SetSpinButton(update, landed.Category, state).SendTo(to);
        Panel.AppendChild(to, CellId(landed), YoursBadge());
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
              .SetStyle("rsSpinFrame", "border", $"2px solid {FrameColor(state, category)}");
        foreach (var (name, value) in SpinStyle(state, category))
            update.SetStyle("rsSpin", name, value);
        foreach (var (name, value) in SpinTextStyle(state))
            update.SetStyle("rsSpinText", name, value);
        return update;
    }
}
