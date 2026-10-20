using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;

namespace RouletteShop;

public partial class RouletteShopPlugin : DeadworksPluginBase
{
    public override string Name => "Roulette Shop";

    /// <summary>ABILITY_UPGRADE_BIT_CORRUPTED in the game's AbilityUpgradeBits_t (schema, build of Oct 09 2026).</summary>
    private const int CorruptedBit = 128;

    private string SpinUsage => $"!roulette <weapon|vitality|spirit{(Config.WildcardBox ? "|any" : "")}> <800|1600|3200|6400>";

    /// <summary>The game's own UI sounds (soundevents/ui.vsndevts), played to the spinning player only.</summary>
    private static class Sfx
    {
        public const string Purchase = "UI.Notify.ItemPurchase";
        public const string Reel = "UI.ItemDraft.RevolverSpin";
        public const string Corrupted = "UI.ItemDraft.Appear.Legendary";
        public const string Legendary = "UI.ItemDraft.Appear.Legendary";
        public const string Jackpot = "UI.ItemDraft.Appear.Rare";
        public const string Error = "UI.Settings.Error";

        public static string Appear(ItemCategory c) => $"UI.ItemDraft.Appear.{c}";

        public static string Click(ItemCategory? c) => c is { } cat ? $"UI.Shop.Mod.Activate.{cat}" : "UI.Shop.Mod.Activate";
    }

    [PluginConfig]
    public RouletteConfig Config { get; set; } = new();

    private ItemPool _pool = null!;

    /// <summary>Players with the roulette panel open, by slot.</summary>
    private readonly Dictionary<int, Viewer> _viewers = new();

    private sealed class Viewer
    {
        public ItemCategory? Category = ItemCategory.Weapon;   // null: the wildcard box (any category)
        public int Tier = 1;
        public bool ViaShopKey;
        public long ShopKeyAt;                // when ViaShopKey was set (Environment.TickCount64)
        public long PawnShopOpenAt;           // when the hero's m_bHasShopOpen last read true (Environment.TickCount64)
        public string? ShopSignalsLogged;     // the shop signals last logged, so only changes are logged
        public RouletteItem[] Reel = [];
        public RouletteItem? Landed;
        public bool LandedCorrupted;
        public bool LandedJackpot;
        public bool LandedDoubled;            // the landed item came from a double or nothing
        public bool Kept;                     // the landed item has been given (KEEP, or kept on the way out)
        public int Paid;                      // what the landed item's spin cost, paid back if the game won't give it
        public int? ImbuedSlot;               // the ability the landed item was imbued into
        public SpinOutcome? Pending;          // given and charged, reel still turning
        public string StripId = "rsStrip0";
        public string ResultId = "rsResult0";
        public int Seq;
        public int Souls = -1;
        public RoulettePanel.SpinState? Button;   // what the SPIN button last showed
        public int FreeShown;
    }

    /// <summary>When each player's usercmds last said in_shop (true while the game's shop screen is open), as Environment.TickCount64.</summary>
    private readonly Dictionary<int, long> _lastInShop = new();

    /// <summary>When each player's usercmds last said using_free_cursor (the mouse is a cursor, not aiming), as Environment.TickCount64.</summary>
    private readonly Dictionary<int, long> _lastFreeCursor = new();

    /// <summary>The hero's own flag for the shop screen being open (schema; not yet seen in a log).</summary>
    private static readonly SchemaAccessor<bool> HasShopOpen = new("CCitadelPlayerPawn"u8, "m_bHasShopOpen"u8);

    /// <summary>The item each player last asked to sell (sellitem), for the half-price refund in OnModifyCurrency.</summary>
    private readonly Dictionary<int, (string ClassName, DateTime At)> _selling = new();

    /// <summary>True while the roulette pays a sale's refund itself.</summary>
    private bool _refunding;

    /// <summary>How long a shop signal must stay off before the sheet closes: in_shop flickers off for a few ms with the shop open.</summary>
    private const int ShopLeftDelayMs = 1000;

    /// <summary>Spins on items with a Corrupted version since each player's last Corrupted one (for CorruptedPitySpins).</summary>
    private readonly Dictionary<int, int> _pityMisses = new();

    /// <summary>The item ids of the build each player last picked in the shop (CCitadelClientMsg_HeroBuild).</summary>
    private readonly Dictionary<int, HashSet<uint>> _builds = new();

    private IHandle? _buildHook;

    /// <summary>A spin that has happened: the item is given and the souls taken. Jackpot: the item is from the tier above.</summary>
    private sealed record SpinOutcome(RouletteItem Item, bool Corrupted, bool Jackpot, RouletteItem[] Strip, int Paid);

    /// <summary>The server window's writer, put back on unload.</summary>
    private TextWriter? _serverOut;

    /// <summary>
    /// Copies everything written to the console into roulette_server.log beside the server, with times: Deadworks writes
    /// the log only to the server window (no file, and con_logfile is hidden), and the log has to be read after each test.
    /// </summary>
    private sealed class FileCopy(TextWriter server, string path) : TextWriter
    {
        private readonly StreamWriter _file = new(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        public override System.Text.Encoding Encoding => server.Encoding;
        public override void Write(char value) => server.Write(value);
        public override void Write(string? value) { server.Write(value); _file.Write(value); }
        public override void WriteLine(string? value) { server.WriteLine(value); _file.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {value}"); }
        protected override void Dispose(bool disposing) { if (disposing) _file.Dispose(); }
    }

    public override void OnLoad(bool isReload)
    {
        _serverOut = Console.Out;
        string logPath = Path.Combine(AppContext.BaseDirectory, "roulette_server.log");
        Console.SetOut(new FileCopy(_serverOut, logPath));
        Console.WriteLine($"[Roulette] copying the log to {logPath}");
        _pool = ItemPool.Load();
        Console.WriteLine(isReload ? "Roulette Shop reloaded!" : "Roulette Shop loaded!");
        Console.WriteLine($"[Roulette] Item pool: {_pool.Count} items ({_pool.Summary()}), {_pool.CorruptibleCount} with a Corrupted version, "
                          + $"{_pool.LegendaryCount} Legendaries");
        LogSettings();

        UI.Panel(RoulettePanel.Id)
            .On("cat", e =>
            {
                if (TryParseBox(e.ArgAt(0), out var c) && _viewers.TryGetValue(e.Caller.Slot, out var v))
                    Select(e.Caller, c, v.Tier, Sfx.Click(c));
            })
            .On("tier", e =>
            {
                if (int.TryParse(e.ArgAt(0), out var t) && t >= 1 && t <= ItemPool.TierPrices.Length && _viewers.TryGetValue(e.Caller.Slot, out var v))
                    Select(e.Caller, v.Category, t, Sfx.Click(v.Category));
            })
            .On("spin", e => PanelSpin(e.Caller))
            .On("double", e => OnDouble(e.Caller))
            .On("reroll", e => OnReroll(e.Caller))
            .On("imbue", e =>
            {
                // An imbue item not kept yet: picking its ability keeps it (the user: "when you get imbueable things, it
                // needs to do the imbue action too").
                if (_viewers.TryGetValue(e.Caller.Slot, out var v) && v.Pending == null && v.Landed?.Imbue == true && !v.Kept)
                {
                    KeepLanded(e.Caller, v, autoImbue: false);
                    Play(e.Caller, Sfx.Purchase);
                    if (v.Landed == null)
                        return;
                }
                try { Imbue(e.Caller, int.TryParse(e.ArgAt(0), out var n) ? n : 0); }
                catch (CommandException ex) { Chat.PrintToChat(e.Caller, "Roulette: " + ex.Message); }
            })
            .On("keep", e => OnKeep(e.Caller))
            .On("close", e => Close(e.Caller.Slot));
        UI.ClientResync += OnClientResync;
        Timer.Every(1.Seconds(), RefreshSouls);
        Timer.Every(250.Milliseconds(), CloseLeftShops);
        Timer.Every(100.Milliseconds(), StopAutoPurchase);
        ScheduleLucky();
        Timer.Every(5.Seconds(), LuckyTick);
        _buildHook = NetMessages.HookIncoming<CCitadelClientMsg_HeroBuild>(OnHeroBuild);
        try { _purchaseHook = NetMessages.HookOutgoing<CCitadelUserMessage_ItemPurchaseNotification>(OnPurchaseNotification); }
        catch (InvalidOperationException ex) { Console.WriteLine($"[Roulette] can't watch purchase notifications: {ex.Message}"); }
    }

    private IHandle? _purchaseHook;

    /// <summary>
    /// Logs the game's own purchase notice (it says whether quickbuy made the purchase), to show in the log which purchases
    /// the backstop in OnModifyCurrency missed.
    /// </summary>
    private HookResult OnPurchaseNotification(OutgoingMessageContext<CCitadelUserMessage_ItemPurchaseNotification> ctx)
    {
        var m = ctx.Message;
        string item = _pool.Find(m.AbilityId)?.ClassName ?? $"id {m.AbilityId}";
        Console.WriteLine($"[Roulette] purchase notice: userid {m.Userid}, {item}{(m.Sell ? ", sell" : "")}{(m.Quickbuy ? ", quickbuy" : "")}"
                          + $"{(_granting ? " (the roulette's own)" : "")}");
        return HookResult.Continue;
    }

    public override void OnUnload()
    {
        UI.ClientResync -= OnClientResync;
        _buildHook?.Cancel();
        _purchaseHook?.Cancel();
        foreach (int slot in _viewers.Keys.ToList())
            Close(slot);
        // Their timers go with the plugin.
        foreach (int slot in _revealSeq.Keys)
            if (PlayerAt(slot) is { } c)
                UI.Panel(RoulettePanel.RevealId).DestroyLayout(c.Recipients);
        if (_serverOut != null)
            Console.SetOut(_serverOut);
    }

    public override void OnConfigReloaded()
    {
        LogSettings();
        if (_luckyUntil == null)
            ScheduleLucky();
        RefreshSheets();
    }

    /// <summary>Shows new settings on open sheets (the wildcard button, the odds lines).</summary>
    private void RefreshSheets()
    {
        foreach (var (slot, v) in _viewers)
        {
            if (v.Pending != null || PlayerAt(slot) is not { } controller)
                continue;
            if (v.Category == null && !Config.WildcardBox)
                v.Category = ItemCategory.Weapon;
            Render(controller, v);
        }
    }

    private void LogSettings()
    {
        Console.WriteLine($"[Roulette] Corrupted: enabled {Config.CorruptedEnabled}, chance {Config.CorruptedChancePercent}%, force {Config.ForceCorrupted}, "
                          + $"pity after {Config.CorruptedPitySpins} spins");
        Console.WriteLine($"[Roulette] Legendary {Config.LegendaryChancePercent}%, jackpot {Config.JackpotChancePercent}%, build items {Config.BuildWeight}x, "
                          + $"wildcard box {Config.WildcardBox}, sounds {Config.Sounds}");
        Console.WriteLine($"[Roulette] Spin only at a shop {Config.SpinOnlyAtShop}, block other purchases {Config.BlockOtherPurchases}, "
                          + $"shout-outs {Config.ShoutOuts}, free spin every {Config.FreeSpinKillStreak} kills, lucky hour every "
                          + $"{Config.LuckyHourEveryMinutes} min for {Config.LuckyHourMinutes} min ({Config.LuckyHourMultiplier}x), "
                          + $"double or nothing {Config.DoubleOrNothingWinPercent}%, reroll {Config.RerollCostPercent}% of the price");
    }

    public override void OnClientDisconnect(ClientDisconnectedEvent args)
    {
        _viewers.Remove(args.Slot);
        _lookupLogged.Remove(args.Slot);
        _revealSeq.Remove(args.Slot);
        _lastInShop.Remove(args.Slot);
        _lastFreeCursor.Remove(args.Slot);
        _selling.Remove(args.Slot);
        _pityMisses.Remove(args.Slot);
        _builds.Remove(args.Slot);
        _quickbuy.Remove(args.Slot);
        _streaks.Remove(args.Slot);
        _freeSpins.Remove(args.Slot);
        _lastImbue.Remove(args.Slot);
        _strays.Remove(args.Slot);
        _granted.Remove(args.Slot);
        _blockedPurchase.Remove(args.Slot);
        _consumed.Remove(args.Slot);
        _sold.Remove(args.Slot);
        _toldBlocked.Remove(args.Slot);
    }

    [Command("hello", Description = "Say hello")]
    public void CmdHello(Caller caller)
    {
        caller.Reply($"Hello, {caller.Name}!");
    }

    // ─── spinning ─────────────────────────────────────────────────────────────

    [Command("roulette", Description = "Open the Roulette Shop, or spin straight away: roulette [weapon|vitality|spirit|any] [800|1600|3200|6400]")]
    public void CmdRoulette(Caller caller, string? category = null, int? price = null)
    {
        // Parsed here rather than bound as an optional enum, whose usage line would say "[category]" instead of the names.
        ItemCategory? cat = null;
        if (category != null && !TryParseBox(category, out cat))
            throw new CommandException(Config.WildcardBox ? "Category must be weapon, vitality, spirit or any." : "Category must be weapon, vitality or spirit.");

        int tier = 0;
        if (price is { } p)
        {
            tier = Array.IndexOf(ItemPool.TierPrices, p) + 1;
            if (tier == 0)
                throw new CommandException("Price must be 800, 1600, 3200 or 6400.");
        }

        var controller = caller.Player ?? throw new CommandException("Spin from in game.");
        if (category == null && price == null)
        {
            if (_viewers.ContainsKey(controller.Slot))
                Close(controller.Slot);
            else
                Open(controller, viaShopKey: false);
            return;
        }
        if (category == null || price == null)
            throw new CommandException("Usage: " + SpinUsage);

        bool free = _freeSpins.GetValueOrDefault(controller.Slot) > 0;
        var spin = Spin(controller, cat, tier, charge: free ? 0 : null);
        if (free)
            _freeSpins[controller.Slot]--;
        if (!Give(controller, spin.Item, spin.Corrupted, spin.Paid))
            return;
        PlayLanding(controller, spin);
        caller.Reply($"Roulette: you got {(spin.Corrupted ? "CORRUPTED " : "")}{(spin.Item.Legendary ? "LEGENDARY " : "")}{spin.Item.Name}"
                     + $"{(spin.Jackpot ? " (JACKPOT: one tier up)" : "")} ({BoxName(cat)}, {(free ? "free spin" : price.ToString())})."
                     + (spin.Item.Imbue ? " Imbue it with !roulette_imbue <1-4>." : ""));
        ShoutOut(controller, spin.Item, spin.Corrupted, spin.Jackpot);
        if (_viewers.TryGetValue(controller.Slot, out var v) && v.Pending == null)
            Render(controller, v);
    }

    [Command("roulette_rate", Description = "Show or change the roulette's odds until the config is reloaded: "
                                            + "roulette_rate [corrupted|legendary|jackpot|pity|build|lucky] [value]", Permission = "roulette.admin")]
    public void CmdRate(Caller caller, string? name = null, double? value = null)
    {
        if (name == null)
        {
            caller.Reply("Roulette: " + RatesSummary());
            return;
        }
        if (value is not { } x)
            throw new CommandException("Usage: roulette_rate <corrupted|legendary|jackpot|pity|build|lucky> <value>");
        static double Percent(double x) => x is >= 0 and <= 100 ? x : throw new CommandException("A chance is 0 to 100 (percent).");
        switch (name.ToLowerInvariant())
        {
            case "corrupted": Config.CorruptedChancePercent = Percent(x); break;
            case "legendary": Config.LegendaryChancePercent = Percent(x); break;
            case "jackpot": Config.JackpotChancePercent = Percent(x); break;
            case "pity":
                Config.CorruptedPitySpins = x >= 0 && x == Math.Floor(x) ? (int)x : throw new CommandException("Pity is a whole number of spins (0 turns it off).");
                break;
            case "build":
                Config.BuildWeight = x >= 1 ? x : throw new CommandException("The build weight is 1 (no difference) or more.");
                break;
            case "lucky":
                // A lucky hour now, for x minutes (0 ends one).
                if (x > 0)
                    StartLucky(x);
                else if (_luckyUntil != null)
                    EndLucky();
                Console.WriteLine($"[Roulette] {caller.Name} set lucky to {x}");
                return;
            default:
                throw new CommandException("Rates: corrupted, legendary, jackpot, pity, build, lucky (minutes).");
        }
        Console.WriteLine($"[Roulette] {caller.Name} set {name} to {x}");
        Chat.PrintToChatAll("Roulette: " + RatesSummary());
        RefreshSheets();
    }

    private string RatesSummary()
        => $"Corrupted {Config.CorruptedChancePercent:0.##}% (pity {Config.CorruptedPitySpins}), Legendary {Config.LegendaryChancePercent:0.##}%, "
           + $"jackpot {Config.JackpotChancePercent:0.##}%, build items {Config.BuildWeight:0.##}x"
           + (Lucky ? $", LUCKY HOUR ({Config.LuckyHourMultiplier:0.#}x Corrupted and Legendary)" : "");

    /// <summary>
    /// The whole server-side spin: checks souls, rolls an item the player doesn't own (items in their build likelier; now
    /// and then a Legendary, or one from the tier above: the jackpot; maybe Corrupted), gives it, then takes the flat tier
    /// price (or <paramref name="charge"/>: 0 for a free spin, the fee for a reroll, which never lands <paramref name="exclude"/>).
    /// A null category is the wildcard box. Throws <see cref="CommandException"/> with a message for the player when it can't.
    /// </summary>
    private SpinOutcome Spin(CCitadelPlayerController controller, ItemCategory? category, int tier, RouletteItem[]? reel = null,
                             int? charge = null, RouletteItem? exclude = null)
    {
        var pawn = controller.GetHeroPawn() ?? throw new CommandException("You need a hero to spin.");
        if (!AtShop(pawn))
            throw new CommandException("Spin at a shop: walk into a shop zone first, like buying.");
        int price = charge ?? ItemPool.TierPrices[tier - 1];
        int souls = pawn.GetCurrency(ECurrencyType.EGold);
        if (souls < price)
            throw new CommandException($"Not enough souls: you have {souls}, {(exclude != null ? "a reroll" : "a spin")} costs {price}.");

        // AddItem gives duplicates, which the normal shop never does, so only roll items the player doesn't have.
        var pool = _pool.Get(category, tier);
        var candidates = pool.Where(i => !Owns(pawn, i) && i != exclude).ToArray();
        if (candidates.Length == 0)
            throw new CommandException(category == null ? $"You already own every item at {price}." : $"You already own every {category} item at {price}.");

        var before = Snapshot(controller, pawn);
        var build = BuildIds(controller.Slot);

        // Legendary: now and then a tier 5 Legendary of the box's category, for the same price. The game marks them Street
        // Brawl only and may refuse them when kept: then Give pays the spin back.
        RouletteItem? item = null;
        if (Random.Shared.NextDouble() * 100 < LegendaryChance
            && _pool.Legendaries(category).Where(i => !Owns(pawn, i) && i != exclude).ToArray() is { Length: > 0 } legends)
            item = Pick(legends, build);

        // Jackpot: now and then the item comes from the next tier up, for the same price.
        bool jackpot = false;
        if (item == null && tier < ItemPool.TierPrices.Length && Random.Shared.NextDouble() * 100 < Config.JackpotChancePercent)
        {
            var up = _pool.Get(category, tier + 1).Where(i => !Owns(pawn, i) && i != exclude).ToArray();
            if (up.Length > 0)
            {
                candidates = up;
                jackpot = true;
            }
        }
        item ??= Pick(candidates, build);

        // Only items with a Corrupted version can come back Corrupted (tiers 3 and 4). Pity: after CorruptedPitySpins of
        // those in a row without one, the next one is.
        int slot = controller.Slot;
        int misses = _pityMisses.GetValueOrDefault(slot);
        bool canCorrupt = item.Corruptible && Config.CorruptedEnabled;
        bool corrupted = canCorrupt && (Config.ForceCorrupted || Random.Shared.NextDouble() * 100 < CorruptedChance);
        bool pity = canCorrupt && !corrupted && Config.CorruptedPitySpins > 0 && misses + 1 >= Config.CorruptedPitySpins;
        corrupted |= pity;

        // Charged now; the item is given by Give (KEEP on the sheet, or straight away from chat), which pays back if the game refuses it.
        // The shop's own spend path (counted as souls spent), not a raw SetCurrency write.
        if (price > 0)
            Granting(() => pawn.ModifyCurrency(ECurrencyType.EGold, -price, ECurrencySource.EItemPurchase));
        if (canCorrupt)
            _pityMisses[slot] = corrupted ? 0 : misses + 1;

        Console.WriteLine($"[Roulette] {controller.PlayerName} {(exclude != null ? $"rerolled {exclude.ClassName}," : "spun")} {BoxName(category)} {ItemPool.TierPrices[tier - 1]}"
                          + $"{(charge == 0 ? " (free spin)" : charge != null ? $" (paid {charge})" : "")} -> {item.ClassName}{(corrupted ? " CORRUPTED" : "")}"
                          + $"{(pity ? " (pity)" : "")}{(jackpot ? " JACKPOT (tier up)" : "")}{(item.Legendary ? " LEGENDARY" : "")}"
                          + $"{(build.Contains(item.Id) ? " (in their build)" : "")} ({candidates.Length} unowned in pool)");
        Console.WriteLine($"[Roulette] before: {before} | after: {Snapshot(controller, pawn)}");
        Timer.Once(1.Seconds(), () =>
        {
            if (PlayerAt(slot) is { } c && c.GetHeroPawn() is { } p)
                Console.WriteLine($"[Roulette] 1s later: {Snapshot(c, p)}");
        });

        return new SpinOutcome(item, corrupted, jackpot, BuildStrip(reel ?? [], pool, item), price);
    }

    /// <summary>
    /// Gives a spin's item, Corrupted if it rolled that. If the game won't (a Legendary outside Street Brawl, say), pays
    /// back <paramref name="paid"/> and returns false.
    /// </summary>
    private bool Give(CCitadelPlayerController controller, RouletteItem item, bool corrupted, int paid)
    {
        int slot = controller.Slot;
        if (controller.GetHeroPawn() is not { } pawn)
            return false;
        var given = Granting(() => pawn.AddItem(item.ClassName));
        if (given == null)
        {
            if (paid > 0)
                Granting(() => pawn.ModifyCurrency(ECurrencyType.EGold, paid, ECurrencySource.EItemSale));
            Console.WriteLine($"[Roulette] the game wouldn't give {controller.PlayerName} {item.ClassName}: paid back {paid}");
            Chat.PrintToChat(controller, $"Roulette: the game wouldn't give you {item.Name}, so your {paid} souls are back.");
            return false;
        }
        NoteGranted(slot, item.ClassName);

        // The game's own SetUpgradeBits; whether it applies the Corrupted bonuses and drawback is checked in game.
        string corruptedNote = "";
        if (corrupted)
        {
            int bitsBefore = given.UpgradeBits;
            given.UpgradeBits = bitsBefore | CorruptedBit;
            corruptedNote = $" CORRUPTED (upgrade bits {bitsBefore} -> {given.UpgradeBits})";
        }
        if (item.Imbue)
            _lastImbue[slot] = item.ClassName;
        Console.WriteLine($"[Roulette] gave {controller.PlayerName} {item.ClassName}{corruptedNote}");
        return true;
    }

    /// <summary>
    /// A box's category by name ("weapon"), or null for "any" (the wildcard box, when it's on). Unlike Enum.TryParse,
    /// numbers don't count.
    /// </summary>
    private bool TryParseBox(string text, out ItemCategory? category)
    {
        category = Enum.GetValues<ItemCategory>().Cast<ItemCategory?>()
            .FirstOrDefault(c => c.ToString()!.Equals(text, StringComparison.OrdinalIgnoreCase));
        return category != null || (Config.WildcardBox && text.Equals("any", StringComparison.OrdinalIgnoreCase));
    }

    private static string BoxName(ItemCategory? category) => category?.ToString() ?? "Any";

    /// <summary>A random item, an item in the player's build BuildWeight times as likely as any other.</summary>
    private RouletteItem Pick(RouletteItem[] candidates, ISet<uint> build)
    {
        double weight = Math.Max(1, Config.BuildWeight);
        double roll = Random.Shared.NextDouble() * candidates.Sum(i => build.Contains(i.Id) ? weight : 1);
        foreach (var i in candidates)
            if ((roll -= build.Contains(i.Id) ? weight : 1) < 0)
                return i;
        return candidates[^1];
    }

    /// <summary>The player's build: the one they picked in the shop, and what they put in their quickbuy queue.</summary>
    private HashSet<uint> BuildIds(int slot)
        => [.. _builds.GetValueOrDefault(slot) ?? [], .. _quickbuy.GetValueOrDefault(slot) ?? []];

    /// <summary>The items each player's client asked to queue for quickbuy (the blocked quickbuy command's ids).</summary>
    private readonly Dictionary<int, HashSet<uint>> _quickbuy = new();

    /// <summary>
    /// The client's quickbuy command. Logged in game: the client sends "quickbuy clear" then "quickbuy add &lt;item id&gt; 1
    /// false" per queued item, and the game bought the first one ~100 ms later (EItemPurchase, notice "quickbuy"); the
    /// backstop's take-back then failed ("couldn't remove it"). Blocked, the server's queue stays empty, so quickbuy has
    /// nothing to buy; the ids are kept for build-aware spins.
    /// </summary>
    private HookResult OnQuickbuy(ClientConCommandEvent e)
    {
        if (e.Controller is not { } controller)
            return HookResult.Continue;
        int slot = controller.Slot;
        switch (e.Args.ElementAtOrDefault(1))
        {
            case "clear":
                _quickbuy.Remove(slot);
                break;
            case "add" when int.TryParse(e.Args.ElementAtOrDefault(2), out int id):
                if (!_quickbuy.TryGetValue(slot, out var ids))
                    _quickbuy[slot] = ids = [];
                ids.Add((uint)id);
                break;
        }
        return Config.BlockOtherPurchases ? HookResult.Stop : HookResult.Continue;
    }

    /// <summary>The build the player picked in the shop: the game sends it to the server (and so to us) as they pick it.</summary>
    private HookResult OnHeroBuild(IncomingMessageContext<CCitadelClientMsg_HeroBuild> ctx)
    {
        int slot = ctx.SenderSlot;
        var build = ctx.Message.HeroBuild;
        var ids = build?.Details?.ModCategories.SelectMany(c => c.Mods).Select(m => m.AbilityId).ToHashSet() ?? [];
        _builds[slot] = ids;
        Console.WriteLine($"[Roulette] slot {slot} picked the build '{build?.Name}': {ids.Count(id => _pool.Find(id) != null)} roulette items");
        if (_viewers.TryGetValue(slot, out var v) && v.Pending == null && PlayerAt(slot) is { } controller)
            Render(controller, v);
        return HookResult.Continue;
    }

    /// <summary>The line under the result: how many items the box holds for this player, and how many are from their build.</summary>
    private string DrawLine(CCitadelPlayerController controller, ItemCategory? category, int tier)
    {
        var pawn = controller.GetHeroPawn();
        var unowned = _pool.Get(category, tier).Where(i => pawn == null || !Owns(pawn, i)).ToArray();
        if (unowned.Length == 0)
            return "YOU OWN THIS WHOLE BOX";
        int inBuild = pawn == null || Config.BuildWeight <= 1 ? 0 : unowned.Count(i => BuildIds(controller.Slot).Contains(i.Id));
        return inBuild > 0 ? $"1 OF {unowned.Length} · {inBuild} FROM YOUR BUILD ({Config.BuildWeight:0.#}X)" : $"RANDOM PICK · 1 OF {unowned.Length}";
    }

    /// <summary>The first line of fine print: selling, and the Legendary and jackpot chances.</summary>
    private string RatesLine()
    {
        var parts = new List<string>();
        if (!Config.SellingEnabled)
            parts.Add("No selling");
        if (LegendaryChance > 0)
            parts.Add($"{LegendaryChance:0.##}% Legendary");
        if (Config.JackpotChancePercent > 0)
            parts.Add($"{Config.JackpotChancePercent:0.##}% jackpot (tier up)");
        return string.Join(" · ", parts);
    }

    /// <summary>The Corrupted line on the sheet: the chance, and how many spins until the pity one.</summary>
    private string OddsLine(int slot)
    {
        if (!Config.CorruptedEnabled)
            return "Corrupted spins are off";
        string odds = $"{CorruptedChance:0.##}% Corrupted at 3,200 and 6,400";
        if (Config.CorruptedPitySpins <= 0)
            return odds;
        int left = Math.Max(1, Config.CorruptedPitySpins - _pityMisses.GetValueOrDefault(slot));
        return left == 1 ? $"{odds} · guaranteed next spin" : $"{odds} · guaranteed within {left} spins";
    }

    private void Play(CCitadelPlayerController controller, string sound)
    {
        if (Config.Sounds)
            controller.PlaySound(sound);
    }

    private void PlayLanding(CCitadelPlayerController controller, SpinOutcome outcome)
        => Play(controller, outcome.Corrupted ? Sfx.Corrupted : outcome.Item.Legendary ? Sfx.Legendary
                            : outcome.Jackpot ? Sfx.Jackpot : Sfx.Appear(outcome.Item.Category));

    private static bool Owns(CCitadelPlayerPawn pawn, RouletteItem item)
        => pawn.AbilityComponent.FindAbilityByName(item.ClassName) != null;

    private static string Snapshot(CCitadelPlayerController controller, CCitadelPlayerPawn pawn)
        => $"souls {pawn.GetCurrency(ECurrencyType.EGold)}, net worth {controller.PlayerDataGlobal.GoldNetWorth}, level {pawn.Level}";

    /// <summary>
    /// A reel strip for the animation: it starts with the five cards on show now (so nothing jumps), is filler after that
    /// (never the same card twice in a row), and stops with the winner in the middle of five different cards.
    /// </summary>
    private static RouletteItem[] BuildStrip(RouletteItem[] showing, RouletteItem[] pool, RouletteItem winner)
    {
        var strip = new List<RouletteItem>(showing.Length == 5 ? showing : Distinct(pool, 5, []));
        while (strip.Count < RoulettePanel.StripLength)
            strip.Add(Other(pool, strip[^1]));
        var around = Distinct(pool, 4, [winner]);
        int w = RoulettePanel.Winner;
        strip[w - 2] = around[0];
        strip[w - 1] = around[1];
        strip[w] = winner;
        strip[w + 1] = around[2];
        strip[w + 2] = around[3];
        return [.. strip];
    }

    private static RouletteItem Other(RouletteItem[] pool, RouletteItem not)
    {
        RouletteItem pick;
        do pick = pool[Random.Shared.Next(pool.Length)]; while (pick == not && pool.Length > 1);
        return pick;
    }

    /// <summary><paramref name="count"/> different items, none of them in <paramref name="not"/> (pools have 7 or more).</summary>
    private static RouletteItem[] Distinct(RouletteItem[] pool, int count, RouletteItem[] not)
        => pool.Except(not).OrderBy(_ => Random.Shared.Next()).Take(count).ToArray();

    // ─── the panel ────────────────────────────────────────────────────────────

    private void Open(CCitadelPlayerController controller, bool viaShopKey)
    {
        int slot = controller.Slot;
        if (!UI.HasClientBootstrap(slot))
        {
            Chat.PrintToChat(controller, "The Roulette Shop screen needs the Deadworks launcher. Spin from chat instead: " + SpinUsage);
            return;
        }
        if (_viewers.TryGetValue(slot, out var v))
        {
            if (viaShopKey && !v.ViaShopKey)
            {
                // Now the shop key closes it too; the cursor is the shop's.
                v.ViaShopKey = true;
                v.ShopKeyAt = Environment.TickCount64;
                UI.Panel(RoulettePanel.Id).ReleaseCursor(controller.Recipients);
                Render(controller, v);
            }
            return;
        }

        _viewers[slot] = v = new Viewer { ViaShopKey = viaShopKey, ShopKeyAt = Environment.TickCount64 };
        Render(controller, v);
        // From the shop key the game's own shop screen (underneath) has already freed the cursor.
        if (!viaShopKey)
            UI.Panel(RoulettePanel.Id).RequestCursor(controller.Recipients);
        Console.WriteLine($"[Roulette] opened the panel for {controller.PlayerName} ({(viaShopKey ? "shop key" : "chat")})");
    }

    /// <summary>Slots whose lookup has been logged (once each).</summary>
    private readonly HashSet<int> _lookupLogged = new();

    /// <summary>
    /// The player in <paramref name="slot"/> (a controller's Slot, as commands and panel events give it), by entity index:
    /// Slot is EntityIndex - 1 (Deadworks v0.5.6, CBasePlayerController). Not Players.GetAll or Players.FromSlot: both
    /// skip slots at or past GlobalVars.MaxClients, and GetAll also skips slots not marked fully connected. The user's
    /// log: every exit_item_shop gave "no connected player has that slot" for slot 0, though the event's own controller
    /// was slot 0, so the destroy was never sent and the sheet stayed. Logs once per slot why GetAll missed.
    /// </summary>
    private CCitadelPlayerController? PlayerAt(int slot)
    {
        var found = CBaseEntity.FromIndex<CCitadelPlayerController>(slot + 1);
        if (_lookupLogged.Add(slot))
            Console.WriteLine($"[Roulette] slot {slot} is {found?.PlayerName ?? "no one"} by entity index; Players.GetAll has "
                              + $"{Players.GetAll().Count()}, FromSlot {(Players.FromSlot(slot) == null ? "no one" : "found")}, "
                              + $"MaxClients {GlobalVars.MaxClients}, IsConnected {Players.IsConnected(slot)}");
        return found;
    }

    /// <summary>
    /// When the sheet's destroy is sent again after a close. The user's log: exit_item_shop came 430 ms after
    /// open_item_shop, Close ran, and the sheet stayed for good. The Deadworks client reads its messages from closed-caption
    /// slots in slot order, not send order, so a destroy can be read before the last piece of a build still arriving and
    /// find nothing to destroy. A destroy with no sheet does nothing; the gaps are over the client's 500 ms window for
    /// dropping a repeated caption.
    /// </summary>
    private static readonly int[] DestroyResendMs = [600, 2000, 4000];

    private void Close(int slot)
    {
        if (!_viewers.Remove(slot, out var v))
            return;
        // DestroyLayout also gives back a cursor the panel asked for.
        if (PlayerAt(slot) is not { } controller)
        {
            Console.WriteLine($"[Roulette] closing the panel for slot {slot}, but no connected player has that slot: nothing sent");
            return;
        }
        KeepLanded(controller, v);
        UI.Panel(RoulettePanel.Id).DestroyLayout(controller.Recipients);
        Console.WriteLine($"[Roulette] closed the panel for {controller.PlayerName}");
        foreach (int ms in DestroyResendMs)
            Timer.Once(ms.Milliseconds(), () =>
            {
                // Not if the sheet was opened again since.
                if (!_viewers.ContainsKey(slot) && PlayerAt(slot) is { } c)
                    UI.Panel(RoulettePanel.Id).DestroyLayout(c.Recipients);
            });
    }

    private void OnClientResync(int slot)
    {
        if (!_viewers.TryGetValue(slot, out var v) || PlayerAt(slot) is not { } controller)
            return;
        Render(controller, v);
        if (!v.ViaShopKey)
            UI.Panel(RoulettePanel.Id).RequestCursor(controller.Recipients);
    }

    /// <summary>Sends the whole panel. A spin still turning is shown as landed: its item is already given.</summary>
    private void Render(CCitadelPlayerController controller, Viewer v)
    {
        if (v.Pending is { } pending)
            Settle(v, pending);

        var pawn = controller.GetHeroPawn();
        var pool = _pool.Get(v.Category, v.Tier);
        if (v.Reel.Length != 5 || v.Reel.Any(i => !pool.Contains(i)))
            v.Reel = Distinct(pool, 5, []);
        v.Souls = pawn?.GetCurrency(ECurrencyType.EGold) ?? 0;
        v.StripId = "rsStrip0";
        v.ResultId = "rsResult0";
        v.Seq = 0;

        v.Button = ButtonState(controller, v);
        v.FreeShown = _freeSpins.GetValueOrDefault(controller.Slot);
        var view = new RoulettePanel.View(v.Category, v.Tier, v.Souls, v.Reel, v.ViaShopKey, Config.WildcardBox,
                                          DrawLine(controller, v.Category, v.Tier), RatesLine(), OddsLine(controller.Slot),
                                          v.Landed, v.LandedCorrupted, v.LandedJackpot, v.Button.Value, v.FreeShown, Lucky,
                                          Landing(controller, v), StreakLine(controller.Slot));
        UI.Panel(RoulettePanel.Id).BuildLayout(controller.Recipients, RoulettePanel.Layout(view));
    }

    private static void Settle(Viewer v, SpinOutcome outcome)
    {
        v.Pending = null;
        v.Landed = outcome.Item;
        v.LandedCorrupted = outcome.Corrupted;
        v.LandedJackpot = outcome.Jackpot;
        v.LandedDoubled = false;
        v.ImbuedSlot = null;
        v.Kept = false;
        v.Paid = outcome.Paid;   // ponytail: a reroll's item pays back only the reroll fee, not the first spin
        v.Reel = outcome.Strip[(RoulettePanel.Winner - 2)..(RoulettePanel.Winner + 3)];
    }

    /// <summary>
    /// Gives the landed item if it hasn't been yet: on KEEP, and before anything that would drop it (closing the sheet,
    /// another spin, another box), as it's paid for (the user's choice: auto-keep). A spin still turning lands first.
    /// </summary>
    private void KeepLanded(CCitadelPlayerController controller, Viewer v, bool autoImbue = true)
    {
        bool turning = v.Pending != null;
        if (v.Pending is { } pending)
            Settle(v, pending);
        if (v.Landed is not { } item || v.Kept)
            return;
        v.Kept = true;
        if (!Give(controller, item, v.LandedCorrupted, v.Paid))
        {
            v.Landed = null;
            return;
        }
        // Kept without picking an ability: into the first one that takes it, as an imbue item does nothing un-imbued.
        if (autoImbue && item.Imbue && controller.GetHeroPawn() is { } pawn
            && Enumerable.Range(1, 4).FirstOrDefault(n => pawn.AbilityComponent.GetAbilityBySlot((EAbilitySlot)(n - 1))?.CanBeImbuedBy(item.ClassName) == true) is > 0 and var first)
        {
            try { Chat.PrintToChat(controller, $"Roulette: {item.Name} " + Imbue(controller, first)); }
            catch (CommandException ex) { Chat.PrintToChat(controller, "Roulette: " + ex.Message); }
        }
        if (turning)
        {
            // Closed before the reel stopped: still show what they got.
            ShowReveal(controller, "YOU GOT", item, v.LandedCorrupted, v.LandedJackpot, doubled: false);
            ShoutOut(controller, item, v.LandedCorrupted, v.LandedJackpot);
        }
    }

    /// <summary>Each player's latest reveal, so an older one's timer doesn't take down a newer one.</summary>
    private readonly Dictionary<int, int> _revealSeq = new();

    /// <summary>Shows the landed item big in the middle of the screen for RevealMs (destroy sent again, as for the sheet).</summary>
    private void ShowReveal(CCitadelPlayerController controller, string heading, RouletteItem item, bool corrupted, bool jackpot, bool doubled)
    {
        int slot = controller.Slot, seq = _revealSeq[slot] = _revealSeq.GetValueOrDefault(slot) + 1;
        UI.Panel(RoulettePanel.RevealId).BuildLayout(controller.Recipients, RoulettePanel.Reveal(heading, item, corrupted, jackpot, doubled));
        foreach (int ms in new[] { RoulettePanel.RevealMs, RoulettePanel.RevealMs + 600, RoulettePanel.RevealMs + 2000 })
            Timer.Once(ms.Milliseconds(), () =>
            {
                if (_revealSeq.GetValueOrDefault(slot) == seq && PlayerAt(slot) is { } c)
                    UI.Panel(RoulettePanel.RevealId).DestroyLayout(c.Recipients);
            });
    }

    /// <summary>KEEP on the sheet: the landed item is given.</summary>
    private void OnKeep(CCitadelPlayerController controller)
    {
        if (!_viewers.TryGetValue(controller.Slot, out var v) || v.Pending != null || v.Landed == null || v.Kept)
            return;
        var item = v.Landed;
        KeepLanded(controller, v);
        if (v.Landed != null)
        {
            Play(controller, Sfx.Purchase);
            ShowLandedResult(controller, v);
        }
        else
        {
            Play(controller, Sfx.Error);
            ShowResult(controller, v, id => RoulettePanel.Message(id, "Not given", $"The game wouldn't give you {item.Name}. Your souls are back."));
        }
        RoulettePanel.UpdateLines(controller.Recipients, DrawLine(controller, v.Category, v.Tier), RatesLine(), OddsLine(controller.Slot), Lucky);
    }

    /// <summary>Picks a box (category, null for the wildcard box, and tier) on the sheet.</summary>
    private void Select(CCitadelPlayerController controller, ItemCategory? category, int tier, string? sound = null)
    {
        if (!_viewers.TryGetValue(controller.Slot, out var v) || v.Pending != null)
            return;
        if (sound != null)
            Play(controller, sound);
        KeepLanded(controller, v);
        v.Category = category;
        v.Tier = tier;
        v.Landed = null;
        v.Reel = [];
        Render(controller, v);
    }

    /// <summary>
    /// A spin from the sheet: a free one if a kill streak earned it, or a <paramref name="reroll"/> of the last item for
    /// <paramref name="fee"/> (the item goes back once the new one is given).
    /// </summary>
    private void PanelSpin(CCitadelPlayerController controller, RouletteItem? reroll = null, int fee = 0)
    {
        int slot = controller.Slot;
        if (!_viewers.TryGetValue(slot, out var v) || v.Pending != null)
            return;
        var to = controller.Recipients;
        bool free = reroll == null && _freeSpins.GetValueOrDefault(slot) > 0;

        // A new spin keeps the last item (a reroll drops it instead, once the new spin is paid for), then starts from a clean
        // panel (it un-dims the pool and marks the new owned item).
        if (reroll == null)
        {
            KeepLanded(controller, v);
            if (v.Landed != null)
            {
                v.Landed = null;
                Render(controller, v);
            }
        }

        SpinOutcome outcome;
        try
        {
            outcome = Spin(controller, v.Category, v.Tier, v.Reel, reroll != null ? fee : free ? 0 : null, reroll);
        }
        catch (CommandException ex)
        {
            var hero = controller.GetHeroPawn();
            Console.WriteLine($"[Roulette] {controller.PlayerName} can't spin: {ex.Message} "
                              + $"({(hero == null ? "no hero" : $"m_bInItemShopZone {InItemShopZone.Get(hero.Handle)}, m_bInRegenerationZone {hero.InRegenerationZone}")})");
            Play(controller, Sfx.Error);
            ShowResult(controller, v, id => RoulettePanel.Message(id, "Can't spin", ex.Message));
            return;
        }
        if (free)
            _freeSpins[slot]--;
        v.Pending = outcome;
        Play(controller, Sfx.Purchase);

        // Swap in the spin strip, then turn it once it exists on the client (a transition needs a node to move).
        string stripId = $"rsStrip{++v.Seq}";
        RoulettePanel.StartSpin(to, v.StripId, stripId, outcome.Strip, v.Category);
        v.StripId = stripId;

        Timer.Once(RoulettePanel.TurnDelayMs.Milliseconds(), () =>
        {
            if (_viewers.TryGetValue(slot, out var now) && now.StripId == stripId && PlayerAt(slot) is { } c)
            {
                RoulettePanel.TurnReel(c.Recipients, stripId);
                Play(c, Sfx.Reel);
            }
        });
        Timer.Once((RoulettePanel.TurnDelayMs + RoulettePanel.SpinMs + 150).Milliseconds(), () => Land(slot, stripId));
    }

    private void Land(int slot, string stripId)
    {
        if (!_viewers.TryGetValue(slot, out var v) || v.StripId != stripId || v.Pending is not { } outcome
            || PlayerAt(slot) is not { } controller)
            return;
        Settle(v, outcome);

        ShowLandedResult(controller, v);
        ShowReveal(controller, "YOU ROLLED", outcome.Item, outcome.Corrupted, outcome.Jackpot, doubled: false);
        PlayLanding(controller, outcome);
        ShoutOut(controller, outcome.Item, outcome.Corrupted, outcome.Jackpot);

        v.Souls = controller.GetHeroPawn()?.GetCurrency(ECurrencyType.EGold) ?? v.Souls;
        v.Button = ButtonState(controller, v);
        v.FreeShown = _freeSpins.GetValueOrDefault(slot);
        RoulettePanel.ShowLanding(controller.Recipients, v.Category, outcome.Item, outcome.Corrupted, outcome.Jackpot, v.Souls,
                                  DrawLine(controller, v.Category, v.Tier), OddsLine(slot), v.Button.Value, v.FreeShown);
    }

    /// <summary>Replaces the result card (fresh id: a removed node lingers a frame).</summary>
    private static void ShowResult(CCitadelPlayerController controller, Viewer v, Func<string, UINode> make)
    {
        string id = $"rsResult{++v.Seq}";
        RoulettePanel.ShowResult(controller.Recipients, v.ResultId, make(id));
        v.ResultId = id;
    }

    /// <summary>What the SPIN button says: turning, away from a shop, a free spin waiting, short of souls, or ready.</summary>
    private RoulettePanel.SpinState ButtonState(CCitadelPlayerController controller, Viewer v)
    {
        if (v.Pending != null)
            return RoulettePanel.SpinState.Busy;
        if (controller.GetHeroPawn() is not { } pawn)
            return RoulettePanel.SpinState.Broke;
        if (!AtShop(pawn))
            return RoulettePanel.SpinState.Away;
        if (_freeSpins.GetValueOrDefault(controller.Slot) > 0)
            return RoulettePanel.SpinState.Free;
        return pawn.GetCurrency(ECurrencyType.EGold) < ItemPool.TierPrices[v.Tier - 1] ? RoulettePanel.SpinState.Broke : RoulettePanel.SpinState.Ready;
    }

    /// <summary>Keeps the open panels' soul count and SPIN button up to date (souls earned, a shop zone entered, a free spin earned).</summary>
    private void RefreshSouls()
    {
        foreach (var (slot, v) in _viewers)
        {
            if (PlayerAt(slot) is not { } controller || controller.GetHeroPawn() is not { } pawn)
                continue;
            int souls = pawn.GetCurrency(ECurrencyType.EGold), free = _freeSpins.GetValueOrDefault(slot);
            var state = ButtonState(controller, v);
            if (souls == v.Souls && state == v.Button && free == v.FreeShown)
                continue;
            v.Souls = souls;
            v.Button = state;
            v.FreeShown = free;
            RoulettePanel.UpdateSouls(controller.Recipients, souls, v.Category, v.Pending == null ? state : null, free);
        }
    }

    // --- The normal shop can't sell anything: spinning is the only way to get items. Selling to it works (SellingEnabled). ---
    // Command names from the server log of a real open / buy / sell (Checkpoint 3). The game's shop screen stays open (the
    // server couldn't close it: blocking open_item_shop didn't, nor did CCitadelUserMsg_ForceShopClosed sent with it),
    // and the roulette sheet sits beside its item page (design #3, the user's choice). Clicking an item there sends
    // buyitem, which is blocked and instead picks that item's category and price on the sheet. The sheet closes with the
    // shop: on exit_item_shop, or when one of the shop signals below goes off (however the shop closed).

    public override void OnProcessUsercmds(ProcessUsercmdsEvent args)
    {
        long now = Environment.TickCount64;
        if (args.Usercmds.Any(c => c.InShop))
            _lastInShop[args.PlayerSlot] = now;
        if (args.Usercmds.Any(c => c.UsingFreeCursor))
            _lastFreeCursor[args.PlayerSlot] = now;
    }

    /// <summary>
    /// Closes the sheet of anyone whose shop is no longer open, however it closed. The user, after the in_shop-only build:
    /// "the roulette shop is not going away when i close my in-game shop". Three signals, none of them confirmed in a log
    /// as going off when the shop closes: the usercmd's in_shop, the hero's m_bHasShopOpen, and the usercmd's
    /// using_free_cursor (the shop frees the cursor; a shop-key sheet doesn't ask for one, so a closed shop means aiming
    /// again). A signal counts only once it has been seen on since the shop key, so one the game never sets can't close
    /// the sheet; the sheet closes when any counted one stays off for ShopLeftDelayMs. Changes are logged, to tell which
    /// signal works.
    /// </summary>
    private void CloseLeftShops()
    {
        long now = Environment.TickCount64;
        foreach (var (slot, v) in _viewers.ToList())
        {
            if (!v.ViaShopKey)
                continue;
            if (PlayerAt(slot)?.GetHeroPawn() is { } pawn && HasShopOpen.Get(pawn.Handle))
                v.PawnShopOpenAt = now;

            (string Name, long At)[] signals =
            [
                ("in_shop", _lastInShop.GetValueOrDefault(slot)),
                ("m_bHasShopOpen", v.PawnShopOpenAt),
                ("using_free_cursor", _lastFreeCursor.GetValueOrDefault(slot)),
            ];
            string State(long at) => at < v.ShopKeyAt - ShopLeftDelayMs ? "unseen" : now - at < ShopLeftDelayMs ? "on" : "off";

            string line = string.Join(", ", signals.Select(s => $"{s.Name} {State(s.At)}"));
            if (line != v.ShopSignalsLogged)
            {
                v.ShopSignalsLogged = line;
                Console.WriteLine($"[Roulette] slot {slot} shop signals: {line}");
            }

            foreach (var (name, at) in signals)
            {
                if (State(at) != "off")
                    continue;
                Console.WriteLine($"[Roulette] slot {slot} left the shop ({name} off for {now - at} ms): closing the roulette");
                Close(slot);
                break;
            }
        }
    }

    /// <summary>A click on an item in the game's shop: picks that item's box on the sheet. False if it isn't for the sheet.</summary>
    private bool PickFromShop(CCitadelPlayerController controller, string? className)
    {
        if (className == null || !_viewers.TryGetValue(controller.Slot, out var v) || _pool.Find(className) is not { } item)
            return false;
        if (v.Pending == null && (item.Category != v.Category || item.Tier != v.Tier))
            Select(controller, item.Category, item.Tier);   // the game's shop plays its own click
        return true;
    }

    public override HookResult OnClientConCommand(ClientConCommandEvent e)
    {
        // Temporary: log shop-like commands, to spot any other way into a shop (the Broker's, say).
        if (e.Command != "dw_ui" && (e.Command.Contains("shop") || e.Command.Contains("item") || e.Command.Contains("corrupt")
                                     || e.Command.Contains("broker") || e.Command.Contains("trade") || e.Command.Contains("buy")))
            Console.WriteLine($"[Roulette] client command from {e.Controller?.PlayerName}: '{e.Command}' args [{string.Join(", ", e.Args)}]");

        var controller = e.Controller;
        switch (e.Command)
        {
            case "quickbuy":
                return OnQuickbuy(e);
            case "open_item_shop":
                // The game's shop opens as normal; the roulette sheet opens beside it.
                if (controller != null)
                    Open(controller, viaShopKey: true);
                return HookResult.Continue;
            case "exit_item_shop":
                if (controller != null && _viewers.TryGetValue(controller.Slot, out var v) && v.ViaShopKey)
                    Close(controller.Slot);
                return HookResult.Continue;
            case "buyitem":
                // Args holds the command too: [buyitem, upgrade_...] (server log, Checkpoint 3).
                if (controller != null && PickFromShop(controller, e.Args.ElementAtOrDefault(1)))
                    Console.WriteLine($"[Roulette] blocked '{e.Command}' from {controller.PlayerName}: picked its box on the sheet");
                else
                    Block(e, "Buying is disabled. Spin instead: " + SpinUsage);
                return HookResult.Stop;
            case "sellitem":
                if (Config.SellingEnabled)
                {
                    if (controller != null && e.Args.ElementAtOrDefault(1) is { } sold)
                    {
                        _selling[controller.Slot] = (sold, DateTime.UtcNow);
                        NoteSold(controller.Slot, sold);
                    }
                    return HookResult.Continue;
                }
                Block(e, "Selling is disabled while the roulette is on.");
                return HookResult.Stop;
            default:
                return HookResult.Continue;
        }
    }

    private static void Block(ClientConCommandEvent e, string message)
    {
        Console.WriteLine($"[Roulette] blocked '{e.Command}' from {e.Controller?.PlayerName}");
        if (e.Controller is { } controller)
            Chat.PrintToChat(controller, message);
    }

    public override HookResult OnModifyCurrency(ModifyCurrencyEvent e)
    {
        // The roulette's own charges and paybacks (_granting) go through.
        if (e.CurrencyType != ECurrencyType.EGold || _granting)
            return HookResult.Continue;

        if (e.Source == ECurrencySource.EItemSale)
        {
            if (Config.SellingEnabled)
                return HalfPriceSale(e);
            // Backstop if a sale gets past the command block some other way: the item goes, but no souls come back.
            Console.WriteLine($"[Roulette] blocked a {e.Amount}-soul sale refund for {e.Pawn.Controller?.PlayerName}");
            return HookResult.Stop;
        }
        // EItemUpgrade too: the user, after the EItemPurchase-only backstop: "quick buy bypasses it". Quickbuy mostly buys
        // build items that upgrade one the player has (unverified which source the game charges those under).
        if (e.Source is ECurrencySource.EItemPurchase or ECurrencySource.EItemUpgrade)
        {
            // A purchase that got past the buy block (quickbuy, say): no souls, its item is taken back, and any item it
            // used up is given back.
            Console.WriteLine($"[Roulette] souls {e.Source} {e.Amount} for {e.Pawn.Controller?.PlayerName}, not from a spin");
            if (Config.BlockOtherPurchases && e.Pawn.Controller is { } buyer)
            {
                PurchaseBlocked(buyer.Slot);
                return HookResult.Stop;
            }
        }
        return HookResult.Continue;
    }

    /// <summary>
    /// What selling an item gives back: half its shop price, as in the normal game. A Legendary (9,999 in the item data, a
    /// Street Brawl placeholder) counts as the top shop tier, so a lucky spin can't be sold for more than the best one.
    /// </summary>
    private static int SellRefund(RouletteItem item)
        => (item.Legendary ? ItemPool.TierPrices[^1] : ItemPool.TierPrices[item.Tier - 1]) / 2;

    /// <summary>
    /// A sale's refund: the game's own if it is already half the item's price, else stopped and half paid instead (the
    /// roulette gives items without a purchase, so the game's own refund isn't known to be half).
    /// </summary>
    private HookResult HalfPriceSale(ModifyCurrencyEvent e)
    {
        if (_refunding || e.Pawn.Controller is not { } seller || !_selling.Remove(seller.Slot, out var sold)
            || DateTime.UtcNow - sold.At > TimeSpan.FromSeconds(2) || _pool.Find(sold.ClassName) is not { } item)
            return HookResult.Continue;
        int refund = SellRefund(item);
        if (e.Amount == refund)
            return HookResult.Continue;
        Console.WriteLine($"[Roulette] {seller.PlayerName} sold {item.ClassName}: refund {e.Amount} -> {refund} (half its price)");
        int slot = seller.Slot;
        bool silent = e.Silent, forceGain = e.ForceGain, spendOnly = e.SpendOnly;
        // Next tick, not from inside the game's own currency change.
        Timer.NextTick(() =>
        {
            if (PlayerAt(slot)?.GetHeroPawn() is not { } pawn)
                return;
            _refunding = true;
            try { pawn.ModifyCurrency(ECurrencyType.EGold, refund, ECurrencySource.EItemSale, silent, forceGain, spendOnly); }
            finally { _refunding = false; }
        });
        return HookResult.Stop;
    }
}
