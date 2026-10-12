using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;

namespace RouletteShop;

/// <summary>
/// The roulette panel, built on the server the way the Deadworks TicTacToe example builds its board: players need only the
/// Deadworks launcher, no addon. Layout and colours follow mockups/approved.html (1280x760). Art paths are the game's own,
/// each one used by the game's shop stylesheets (citadel_hud_hero_shop.css, citadel_shop_mod_view.css).
/// </summary>
public static class RoulettePanel
{
    public const string Id = "roulette";

    // Reel geometry: 5 visible cards; a spin strip is StripLength cards and stops with Winner in the middle of the window.
    public const int CardWidth = 94, CardHeight = 147, CardGap = 6, Step = CardWidth + CardGap;
    public const int StripLength = 30, Winner = 27;
    public const int SpinMs = 4000;
    public const string SpinDuration = "4s";

    /// <summary>
    /// Wait before turning a new strip: the transition needs the strip on the client first, and the strip (the biggest
    /// update, ~30 cards) can take a few frames of the UI channel's 16 KB/s to arrive.
    /// </summary>
    public const int TurnDelayMs = 500;

    // Common ink (approved mockup).
    private const string Mint = "#9affd6", Ink = "#1a1510", Cream = "#eadfc6", Black = "#120e0a", Red = "#b3261a";

    private const string SoulIcon = "s2r://panorama/images/hud/icons/icon_soul.vsvg";
    private const string CorruptedIcon = "s2r://panorama/images/hud/icons/icon_corrupted_souls.vsvg";
    private const string CorruptedFrame = "s2r://panorama/images/shop/corrupted_items/item_frame_corrupted_psd.vtex";
    private const string Paper = "s2r://panorama/images/shop/catalog/catalog_shop_generic_bg2_psd.vtex";
    private const string TabEdge = "s2r://panorama/images/shop/catalog/catalog_shop_tab_edge_overlay_psd.vtex";

    private sealed record Palette(string Tab, string Rule, string Cap, string Name, string T4, string T4Name);

    private static Palette Colors(ItemCategory c) => c switch
    {
        ItemCategory.Weapon => new("#e4b20c", "#c4862c", "#49381f", "#d99a3a", "#110c08", "#e4c5a1"),
        ItemCategory.Vitality => new("#a5ce3c", "#557f4c", "#2c4b26", "#a9d84a", "#0a0d09", "#c2e7cd"),
        _ => new("#b866de", "#72559a", "#3f2a57", "#c878f0", "#1a1423", "#e3d4f2"),
    };

    public static string TabColor(ItemCategory c) => Colors(c).Tab;

    private static string Slug(ItemCategory c) => c.ToString().ToLowerInvariant();

    private static string CardBacker(RouletteItem i) => $"s2r://panorama/images/shop/catalog/cards/card_backer_{Slug(i.Category)}_t{i.Tier}_psd.vtex";

    private static string TabIcon(ItemCategory c) => $"s2r://panorama/images/shop/catalog/catalog_shop_tab_icon_{Slug(c)}_psd.vtex";

    public static string Souls(int souls) => souls.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>What one player's panel shows.</summary>
    public sealed record View(
        ItemCategory Category, int Tier, int Souls, RouletteItem[] Pool, ISet<string> Owned, RouletteItem[] Reel,
        bool ViaShopKey, double CorruptedChancePercent, Func<ItemCategory, int, int> PoolSize,
        RouletteItem? Landed, bool LandedCorrupted);

    // ─── whole panel ──────────────────────────────────────────────────────────

    public static UINode Layout(View v)
    {
        var pal = Colors(v.Category);
        int price = ItemPool.TierPrices[v.Tier - 1];

        var panel = UI.Container("rsPanel").WithStyles(
            ("width", "1280px"), ("height", "760px"), ("horizontal-align", "center"), ("vertical-align", "center"),
            ("background-color", "#0b0d11"), ("box-shadow", "0px 0px 40px #000000cc"));
        panel.Add(Tabs(v.Category), Page(v, pal, price));

        // Full-screen backdrop over the game's own shop; the hover style is there so it takes the mouse (hittest).
        return UI.Container("rsRoot")
            .WithStyles(("width", "100%"), ("height", "100%"), ("background-color", "#05070ae6"))
            .WithHoverStyle("background-color", "#05070ae6")
            .Add(panel);
    }

    private static UINode Tabs(ItemCategory selected)
    {
        var tabs = UI.Vertical("rsTabs").WithStyles(("width", "84px"), ("margin-left", "6px"), ("margin-top", "26px"));
        foreach (var c in Enum.GetValues<ItemCategory>())
        {
            bool on = c == selected;
            tabs.Add(UI.Button("rsTab" + c, "").OnClick("cat", Slug(c))
                .WithStyles(("width", "84px"), ("height", on ? "175px" : "73px"), ("margin-bottom", "8px"),
                            ("border-radius", "8px 0px 0px 8px"), ("background-color", Colors(c).Tab))
                .WithHoverStyle("brightness", on ? "1" : "1.12")
                .Add(UI.Container().WithStyles(("width", "100%"), ("height", "100%"), ("background-image", $"url(\"{TabEdge}\")"),
                                               ("background-size", "100% 100%"), ("opacity", "0.6")),
                     UI.Image("", TabIcon(c)).WithStyles(("width", "60px"), ("height", "60px"), ("horizontal-align", "center"),
                                                         ("vertical-align", "center"), ("margin-right", "12px"))));
        }
        return tabs;
    }

    private static UINode Page(View v, Palette pal, int price)
    {
        var page = UI.Container("rsPage").WithStyles(
            ("width", "1194px"), ("height", "744px"), ("margin-left", "78px"), ("margin-top", "8px"),
            ("background-color", "#d8c9a6"), ("background-image", $"url(\"{Paper}\")"), ("background-size", "118% 200%"));

        page.Add(
            UI.Container().WithStyles(("width", "9px"), ("height", "739px"), ("margin-top", "5px"), ("background-color", pal.Tab)),
            Header(v, pal),
            Prices(v, pal),
            Machine(v, pal, price),
            PoolBox(v, pal),
            Footer(v));
        return page;
    }

    private static UINode Header(View v, Palette pal)
    {
        var mast = UI.Vertical().WithStyles(("width", "440px"), ("height", "98px"), ("background-color", Black),
                                            ("border", $"3px solid {pal.Name}"))
            .Add(UI.Label("", "ROULETTE SHOP").WithStyles(("horizontal-align", "center"), ("margin-top", "12px"),
                     ("font-family", "block"), ("font-size", "42px"), ("font-weight", "black"), ("letter-spacing", "2px"),
                     ("color", Cream), ("text-shadow", $"2px 2px 0px {pal.Rule}")),
                 UI.Label("", "FORTUNES BOUGHT · NEVER SOLD").WithStyles(("horizontal-align", "center"), ("margin-top", "4px"),
                     ("font-family", "block"), ("font-size", "12px"), ("letter-spacing", "4px"), ("color", pal.Name)));

        var souls = UI.Vertical().WithStyles(("width", "640px"), ("height", "78px"), ("margin-left", "464px"), ("margin-top", "10px"),
                                             ("background-color", Black), ("transform", "rotateZ(-2deg)"))
            .Add(UI.Label("", "YOUR SOULS").WithStyles(("horizontal-align", "center"), ("margin-top", "10px"),
                     ("font-family", "sans"), ("font-weight", "bold"), ("font-size", "12px"), ("letter-spacing", "3px"), ("color", Cream)),
                 UI.Horizontal().WithStyle("horizontal-align", "center").Add(
                     UI.Image("", SoulIcon).WithStyles(("width", "17px"), ("height", "32px"), ("vertical-align", "center"),
                                                       ("margin-right", "9px"), ("wash-color", Mint)),
                     UI.Label("rsSouls", Souls(v.Souls)).WithStyles(("font-family", "block"), ("font-weight", "bold"),
                         ("font-size", "36px"), ("color", Mint), ("text-shadow", "0px 1px 3px #000000b3"))));

        return UI.Container().WithStyles(("width", "1144px"), ("height", "100px"), ("margin-left", "32px"), ("margin-top", "16px"))
            .Add(mast, souls);
    }

    // ─── tier boxes and price tapes ───────────────────────────────────────────

    private static (string, string)[] TierBox(int tier, Palette pal) => tier switch
    {
        1 => [("border", $"2px solid {pal.Rule}"), ("background-color", "#fff7e23d")],
        2 => [("border", $"3px solid {pal.Rule}"), ("background-color", "#fff7e24d")],
        3 => [("border", $"2px solid {pal.Rule}"), ("background-color", "#fff7e22e")],
        _ => [("border", "1px solid #dccdae8c"), ("background-color", pal.T4)],
    };

    private static string TierCaption(int tier, Palette pal) => tier == 4 ? "#e6d9bd" : pal.Cap;

    private static UINode Tape(int tier, string id = "") => UI.Container(id)
        .WithStyles(("min-width", "78px"), ("height", tier >= 3 ? "40px" : "35px"), ("padding", "2px 12px 0px 12px"),
                    ("background-color", Black), ("transform", "rotateZ(-3deg)"))
        .Add(UI.Label("", ItemPool.TierPrices[tier - 1].ToString()).WithStyles(
            ("horizontal-align", "center"), ("vertical-align", "center"), ("font-family", "block"), ("font-weight", "bold"),
            ("font-size", tier switch { 1 => "24px", 4 => "28px", _ => "26px" }), ("color", Mint),
            ("text-shadow", "0px 1px 3px #000000b3")));

    private static UINode Prices(View v, Palette pal)
    {
        var col = UI.Container("rsPrices").WithStyles(("width", "170px"), ("height", "560px"), ("margin-left", "32px"), ("margin-top", "148px"));
        for (int tier = 1; tier <= ItemPool.TierPrices.Length; tier++)
        {
            bool on = tier == v.Tier;
            var box = UI.Container().WithStyles(TierBox(tier, pal))
                .WithStyles(("width", "146px"), ("height", "108px"), ("opacity", on ? "1" : "0.65"));
            if (on)
                box.WithStyles(("border", $"3px solid {Ink}"));

            var button = UI.Button("rsPrice" + tier, "").OnClick("tier", tier.ToString())
                .WithStyles(("width", "170px"), ("height", "130px"), ("margin-top", $"{(tier - 1) * 144}px"))
                .WithHoverStyle("brightness", on ? "1" : "1.08")
                .Add(UI.Container().WithStyles(("width", "146px"), ("height", "108px"), ("margin-left", "13px"), ("margin-top", "15px"))
                         .Add(box,
                              UI.Label("", $"TIER {tier}").WithStyles(("horizontal-align", "right"), ("margin", "10px 10px 0px 0px"),
                                  ("font-family", "block"), ("font-weight", "bold"), ("font-size", "14px"), ("color", TierCaption(tier, pal))),
                              UI.Label("", $"{v.PoolSize(v.Category, tier)} ITEMS").WithStyles(("vertical-align", "bottom"),
                                  ("margin", "0px 0px 12px 14px"), ("padding", "3px 7px 3px 7px"), ("background-color", Cream),
                                  ("font-family", "sans"), ("font-weight", "bold"), ("font-size", "12px"), ("letter-spacing", "2px"), ("color", Ink))),
                     Tape(tier).WithStyle("transform", on ? "rotateZ(-3deg) scale3d(1.12, 1.12, 1)" : "rotateZ(-3deg)"));
            if (on)
                button.Add(UI.Label("", "▶").WithStyles(("horizontal-align", "right"), ("vertical-align", "center"),
                    ("font-size", "22px"), ("color", Ink), ("margin-top", "8px")));
            col.Add(button);
        }
        return col;
    }

    // ─── item card, after the real shop's ─────────────────────────────────────

    private static string NameSize(string name, double width)
    {
        int longestWord = name.Split(' ').Max(w => w.Length);
        double cqw = name.Length > 22 ? 13.7 : name.Length > 19 || longestWord > 11 ? 15.07
                   : name.Length > 14 || longestWord > 10 ? 16.44 : 17.81;
        return $"{Math.Round(cqw * width / 100)}px";
    }

    public static UINode Card(string id, RouletteItem i, int width, bool corrupted = false)
    {
        int height = (int)Math.Round(width * 128.0 / 82);
        var pal = Colors(i.Category);
        var card = UI.Container(id).WithStyles(("width", $"{width}px"), ("height", $"{height}px"), ("border-radius", "5px"),
                                               ("background-image", $"url(\"{CardBacker(i)}\")"), ("background-size", "cover"));
        card.Add(
            UI.Image("", i.GameIcon).WithStyles(("width", $"{width}px"), ("height", $"{width}px"),
                ("border-radius", i.Active ? "50%" : "5px 5px 0px 0px"),
                ("box-shadow", i.Active ? "0px 1px 3px #00000073" : "none")),
            UI.Label("", i.Name).WithStyles(("width", "100%"), ("height", $"{height - width}px"), ("margin-top", $"{width}px"),
                ("padding", "0px 6px 0px 6px"), ("text-align", "center"), ("vertical-align", "top"), ("font-family", "block"),
                ("font-weight", "bold"), ("font-size", NameSize(i.Name, width)), ("text-overflow", "shrink"),
                ("color", i.Tier == 4 ? pal.T4Name : Ink)));
        if (i.Imbue)
            card.Add(UI.Label("", "IMBUE").WithStyles(("width", "100%"), ("margin-top", $"{width - 14}px"), ("text-align", "center"),
                ("background-color", "#5a3a8a"), ("color", "#d4b8f0"), ("font-family", "block"), ("font-size", "10px"), ("letter-spacing", "1px")));
        if (corrupted)
            card.Add(UI.Container().WithStyles(("width", "100%"), ("height", "100%"), ("background-image", $"url(\"{CorruptedFrame}\")"),
                                               ("background-size", "100% 100%")));
        return card;
    }

    // ─── centre: reel, result, spin button ────────────────────────────────────

    private static UINode Machine(View v, Palette pal, int price)
    {
        var reel = UI.Container("rsReel").WithStyles(("width", "512px"), ("height", "187px"), ("background-color", Black),
                                                     ("border-top", $"4px solid {Cream}"), ("border-bottom", $"4px solid {Cream}"));
        reel.Add(
            UI.Container("rsWindow").WithStyles(("width", "494px"), ("height", $"{CardHeight}px"), ("margin", "16px 9px 0px 9px"),
                                                ("overflow", "clip"))
                .Add(Strip("rsStrip0", v.Reel)),
            UI.Container().WithStyles(("width", "106px"), ("height", "159px"), ("horizontal-align", "center"), ("margin-top", "10px"),
                                      ("border", $"3px solid {pal.Tab}"), ("border-radius", "8px")),
            UI.Label("", "▼").WithStyles(("horizontal-align", "center"), ("margin-top", "-12px"), ("font-size", "28px"), ("color", pal.Tab),
                                         ("text-shadow", $"0px 0px 2px {Ink}")),
            UI.Label("", "▲").WithStyles(("horizontal-align", "center"), ("vertical-align", "bottom"), ("margin-bottom", "-12px"),
                                         ("font-size", "28px"), ("color", pal.Tab), ("text-shadow", $"0px 0px 2px {Ink}")));

        var result = UI.Container("rsResultBox").WithStyles(("width", "512px"), ("height", "237px"), ("margin-top", "205px"))
            .Add(v.Landed is { } landed ? Result("rsResult0", landed, v.LandedCorrupted) : Placeholder("rsResult0"));

        return UI.Container("rsMachine").WithStyles(("width", "512px"), ("height", "560px"), ("margin-left", "202px"), ("margin-top", "142px"))
            .Add(reel, result, SpinButton(v, pal, price));
    }

    public static UINode Strip(string id, IReadOnlyList<RouletteItem> cards)
    {
        var strip = UI.Horizontal(id).WithStyles(("width", $"{cards.Count * Step}px"), ("height", $"{CardHeight}px"),
                                                 ("transform", "translateX(0px)"));
        for (int k = 0; k < cards.Count; k++)
            strip.Add(Card($"{id}c{k}", cards[k], CardWidth).WithStyle("margin-right", $"{CardGap}px"));
        return strip;
    }

    private static UINode ResultFrame(string id, bool corrupted) => UI.Horizontal(id).WithStyles(
        ("width", "512px"), ("height", "237px"), ("padding", "12px 26px 12px 26px"), ("background-color", Cream),
        ("border", $"2px solid {(corrupted ? Red : Ink)}"));

    public static UINode Placeholder(string id) => ResultFrame(id, false).Add(
        UI.Label("", "?").WithStyles(("width", "126px"), ("height", "197px"), ("vertical-align", "center"), ("text-align", "center"),
            ("border", "2px solid #1a151080"), ("border-radius", "5px"), ("transform", "rotateZ(-4deg)"), ("font-family", "block"),
            ("font-size", "64px"), ("padding-top", "60px"), ("color", "#1a151073")),
        ResultText("PICK A CATEGORY AND A PRICE", "Then spin", "One random item from the box on the right", null, Ink));

    public static UINode Message(string id, string title, string body) => ResultFrame(id, false).Add(
        ResultText("ROULETTE", title, body, null, Ink));

    public static UINode Result(string id, RouletteItem item, bool corrupted)
    {
        var pal = Colors(item.Category);
        return ResultFrame(id, corrupted).Add(
            Card("", item, 126, corrupted).WithStyles(("vertical-align", "center"), ("transform", "rotateZ(-4deg)"),
                                                      ("box-shadow", "3px 5px 0px #1a15104d")),
            ResultText("YOU GOT", item.Name, $"{Slug(item.Category).ToUpperInvariant()} · {Souls(ItemPool.TierPrices[item.Tier - 1])} SOULS",
                       corrupted ? Corrupted() : null, pal.Cap));
    }

    private static UINode ResultText(string kicker, string title, string meta, UINode? extra, string capColor)
    {
        var text = UI.Vertical().WithStyles(("width", "320px"), ("vertical-align", "center"), ("margin-left", "26px")).Add(
            UI.Label("", kicker).WithStyles(("font-family", "sans"), ("font-weight", "bold"), ("font-size", "13px"),
                                            ("letter-spacing", "3px"), ("color", capColor)),
            UI.Label("", title).WithStyles(("width", "100%"), ("margin", "8px 0px 10px 0px"), ("font-family", "block"),
                                           ("font-weight", "bold"), ("font-size", "34px"), ("text-overflow", "shrink"), ("color", Ink)),
            UI.Label("", meta).WithStyles(("width", "100%"), ("font-family", "sans"), ("font-weight", "bold"), ("font-size", "14px"),
                                          ("letter-spacing", "1px"), ("text-transform", "uppercase"), ("color", capColor)));
        return extra == null ? text : text.Add(extra);
    }

    private static UINode Corrupted() => UI.Horizontal().WithStyles(("margin-top", "12px"), ("padding", "5px 10px 4px 10px"),
                                                                    ("border", $"2px solid {Red}"), ("transform", "rotateZ(-2deg)"))
        .Add(UI.Image("", CorruptedIcon).WithStyles(("width", "24px"), ("height", "24px"), ("margin-right", "8px"), ("wash-color", Red)),
             UI.Label("", "CORRUPTED").WithStyles(("font-family", "block"), ("font-weight", "bold"), ("font-size", "20px"),
                                                  ("letter-spacing", "2px"), ("color", Red)));

    /// <summary>Spin button looks: ready, spinning, or not enough souls.</summary>
    public enum SpinState { Ready, Busy, Broke }

    private static (string, string)[] SpinStyle(SpinState s, string tab) => s switch
    {
        SpinState.Ready => [("background-color", tab), ("box-shadow", $"4px 5px 0px {Ink}"), ("border", $"2px solid {Ink}")],
        SpinState.Busy => [("background-color", "#a3977f"), ("box-shadow", $"4px 5px 0px {Ink}"), ("border", $"2px solid {Ink}")],
        _ => [("background-color", "#cfc3a8"), ("box-shadow", "none"), ("border", $"2px solid {Red}")],
    };

    private static string SpinText(SpinState s) => s switch
    {
        SpinState.Ready => "SPIN",
        SpinState.Busy => "SPINNING...",
        _ => "NOT ENOUGH SOULS",
    };

    private static (string, string)[] SpinTextStyle(SpinState s) =>
        [("font-size", s == SpinState.Ready ? "44px" : "30px"), ("color", s == SpinState.Broke ? "#96190f" : Ink)];

    private static UINode SpinButton(View v, Palette pal, int price)
    {
        var state = v.Souls < price ? SpinState.Broke : SpinState.Ready;
        return UI.Button("rsSpin", "").OnClick("spin")
            .WithStyles(("width", "507px"), ("height", "84px"), ("margin-top", "460px"))
            .WithStyles(SpinStyle(state, pal.Tab))
            .WithHoverStyle("brightness", "1.07")
            .Add(UI.Horizontal().WithStyles(("horizontal-align", "center"), ("vertical-align", "center")).Add(
                UI.Label("rsSpinText", SpinText(state)).WithStyles(("vertical-align", "center"), ("font-family", "block"),
                    ("font-weight", "black"), ("letter-spacing", "4px")).WithStyles(SpinTextStyle(state)),
                Tape(v.Tier, "rsSpinTape").WithStyles(("vertical-align", "center"), ("margin-left", "18px"),
                                                      ("visibility", state == SpinState.Ready ? "visible" : "collapse"))));
    }

    // ─── right: the pool ──────────────────────────────────────────────────────

    public static string CellId(RouletteItem i) => "rsCell_" + i.ClassName;

    private static UINode PoolBox(View v, Palette pal)
    {
        int unowned = v.Pool.Count(i => !v.Owned.Contains(i.ClassName));
        var box = UI.Container("rsPool").WithStyles(("width", "438px"), ("height", "540px"), ("margin-left", "738px"), ("margin-top", "152px"))
            .WithStyles(TierBox(v.Tier, pal));

        var grid = UI.Vertical("rsGrid").WithStyles(("width", "410px"), ("height", "420px"), ("margin", "46px 14px 0px 14px"),
                                                    ("overflow", "squish scroll"));
        for (int row = 0; row * 5 < v.Pool.Length; row++)
        {
            var line = UI.Horizontal().WithStyle("margin-bottom", "5px");
            foreach (var item in v.Pool.Skip(row * 5).Take(5))
            {
                bool owned = v.Owned.Contains(item.ClassName);
                var cell = UI.Container(CellId(item)).WithStyles(("margin-right", "5px"), ("opacity", owned ? "0.35" : "1"))
                    .Add(Card("", item, 78));
                if (owned)
                    cell.Add(UI.Label("", "OWNED").WithStyles(("horizontal-align", "center"), ("vertical-align", "center"),
                        ("padding", "3px 6px 2px 6px"), ("background-color", Black), ("color", Cream), ("font-family", "block"),
                        ("font-size", "11px"), ("letter-spacing", "1px")));
                line.Add(cell);
            }
            grid.Add(line);
        }

        string caption = v.Tier == 4 ? "TIER 4  EXPERTS ONLY" : $"TIER {v.Tier}";
        return box.Add(
            grid,
            Tape(v.Tier).WithStyles(("margin-left", "-15px"), ("margin-top", "-17px")),
            UI.Label("", caption).WithStyles(("margin", "13px 0px 0px 92px"), ("font-family", "block"), ("font-weight", "bold"),
                                             ("font-size", "19px"), ("color", TierCaption(v.Tier, pal))),
            UI.Label("", $"{Slug(v.Category).ToUpperInvariant()} · {v.Pool.Length} ITEMS").WithStyles(("horizontal-align", "right"),
                ("margin", "15px 16px 0px 0px"), ("font-family", "sans"), ("font-weight", "bold"), ("font-size", "12px"),
                ("letter-spacing", "2px"), ("color", TierCaption(v.Tier, pal))),
            UI.Vertical().WithStyles(("horizontal-align", "right"), ("vertical-align", "bottom"), ("margin", "0px 16px 12px 0px"),
                                     ("transform", "rotateZ(-5deg)"))
                .Add(UI.Label("", unowned > 0 ? $"1 in {unowned}" : "ALL OWNED").WithStyles(("horizontal-align", "right"),
                         ("font-family", "block"), ("font-weight", "bold"), ("font-size", "30px"), ("color", TierCaption(v.Tier, pal))),
                     UI.Label("", "ODDS ON EVERY ITEM YOU DON'T OWN").WithStyles(("horizontal-align", "right"), ("margin-top", "4px"),
                         ("font-family", "sans"), ("font-weight", "bold"), ("font-size", "11px"), ("letter-spacing", "2px"),
                         ("color", TierCaption(v.Tier, pal)))));
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

    /// <summary>Turns the reel until <see cref="Winner"/> sits under the pointer. Needs the strip to exist on the client first.</summary>
    public static void TurnReel(RecipientFilter to, string stripId)
        => Panel.Animate(to, stripId, "transform", $"translateX({-(Winner - 2) * Step}px)", SpinDuration, "ease-out");

    /// <summary>Replaces the result card.</summary>
    public static void ShowResult(RecipientFilter to, string oldResultId, UINode result)
    {
        Panel.RemoveChild(to, oldResultId);
        Panel.AppendChild(to, "rsResultBox", result);
    }

    /// <summary>After the reel stops: marks the item in the pool and the reel, and updates souls and the spin button.</summary>
    public static void ShowLanding(RecipientFilter to, string stripId, RouletteItem[] pool, ISet<string> ownedBefore,
                                   RouletteItem landed, bool corrupted, int souls, SpinState state)
    {
        var update = Panel.Build();
        MarkLanded(update, pool, ownedBefore, landed, corrupted);
        for (int k = Winner - 2; k <= Winner + 2; k++)
            if (k != Winner)
                update.SetStyle($"{stripId}c{k}", "opacity", "0.5");
        update.Set("rsSouls", Souls(souls));
        SetSpinButton(update, landed.Category, state).SendTo(to);
        Panel.AppendChild(to, CellId(landed), YouGotBadge());
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
              .SetStyle("rsSpinTape", "visibility", state == SpinState.Ready ? "visible" : "collapse");
        foreach (var (name, value) in SpinStyle(state, TabColor(category)))
            update.SetStyle("rsSpin", name, value);
        foreach (var (name, value) in SpinTextStyle(state))
            update.SetStyle("rsSpinText", name, value);
        return update;
    }

    /// <summary>Marks the cell a spin landed on (mint outline, red when Corrupted) and dims the rest.</summary>
    private static void MarkLanded(UIUpdate update, RouletteItem[] pool, ISet<string> ownedBefore, RouletteItem landed, bool corrupted)
    {
        foreach (var i in pool)
        {
            if (i == landed)
                update.SetStyle(CellId(i), "border", $"3px solid {(corrupted ? "#ff6a55" : Mint)}")
                      .SetStyle(CellId(i), "transform", "scale3d(1.1, 1.1, 1)")
                      .SetStyle(CellId(i), "z-index", "5");
            else if (!ownedBefore.Contains(i.ClassName))
                update.SetStyle(CellId(i), "opacity", "0.5");
        }
    }

    private static UINode YouGotBadge() => UI.Label("rsYouGot", "YOU GOT").WithStyles(("margin", "-10px 0px 0px -10px"),
        ("padding", "4px 7px 3px 7px"), ("background-color", Black), ("color", Mint), ("font-family", "block"), ("font-weight", "bold"),
        ("font-size", "12px"), ("letter-spacing", "1px"), ("transform", "rotateZ(-9deg)"));

    private static UINode Footer(View v)
    {
        UINode Note(string text) => UI.Label("", text).WithStyles(("padding", "5px 9px 4px 9px"), ("background-color", Cream),
            ("font-family", "sans"), ("font-weight", "bold"), ("font-size", "13px"), ("color", Ink), ("margin-right", "14px"));

        var footer = UI.Horizontal("rsFooter").WithStyles(("width", "1144px"), ("height", "30px"), ("margin-left", "32px"), ("margin-top", "706px"))
            .Add(Note("✱ Selling is disabled while Roulette Shop is on"),
                 Note($"✱ {v.CorruptedChancePercent:0.##}% of 3,200 and 6,400 spins come back Corrupted"));

        if (v.ViaShopKey)
            footer.Add(Note("Press ESC or your shop key to close"));
        else
            footer.Add(UI.Button("rsClose", "CLOSE  ✕").OnClick("close")
                .WithStyles(("horizontal-align", "right"), ("padding", "5px 12px 4px 12px"), ("background-color", Black))
                .WithHoverStyle("background-color", "#2a2420")
                .WithTextStyles(("font-family", "block"), ("font-weight", "bold"), ("font-size", "13px"), ("letter-spacing", "1px"), ("color", Cream)));
        return footer;
    }
}
