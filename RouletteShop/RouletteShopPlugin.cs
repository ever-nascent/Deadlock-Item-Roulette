using System.Runtime.InteropServices;
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

    /// <summary>The pawn's quickbuy queue: a CNetworkUtlVectorBase of CUtlStringToken (item ids).</summary>
    private static readonly SchemaAccessor<byte> QuickbuyQueue = new("CCitadelPlayerPawn"u8, "m_vecQuickbuyQueue"u8);

    /// <summary>A spin that has happened: the item is given and the souls taken. Jackpot: the item is from the tier above.</summary>
    private sealed record SpinOutcome(RouletteItem Item, bool Corrupted, bool Jackpot, RouletteItem[] Strip);

    public override void OnLoad(bool isReload)
    {
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
                try { Imbue(e.Caller, int.TryParse(e.ArgAt(0), out var n) ? n : 0); }
                catch (CommandException ex) { Chat.PrintToChat(e.Caller, "Roulette: " + ex.Message); }
            })
            .On("close", e => Close(e.Caller.Slot));
        UI.ClientResync += OnClientResync;
        Timer.Every(1.Seconds(), RefreshSouls);
        Timer.Every(250.Milliseconds(), CloseLeftShops);
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
            if (v.Pending != null || Players.FromSlot(slot) is not { } controller)
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
        _lastInShop.Remove(args.Slot);
        _lastFreeCursor.Remove(args.Slot);
        _selling.Remove(args.Slot);
        _pityMisses.Remove(args.Slot);
        _builds.Remove(args.Slot);
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
        PlayLanding(controller, spin);
        if (spin.Item.Imbue)
            _lastImbue[controller.Slot] = spin.Item.ClassName;
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
        var build = BuildIds(controller.Slot, pawn);

        // Legendary: now and then a tier 5 Legendary of the box's category, for the same price. The game marks them Street
        // Brawl only and may refuse them: then the spin carries on with a normal item.
        RouletteItem? item = null;
        CCitadelBaseAbility? given = null;
        if (Random.Shared.NextDouble() * 100 < LegendaryChance
            && _pool.Legendaries(category).Where(i => !Owns(pawn, i) && i != exclude).ToArray() is { Length: > 0 } legends)
        {
            var legend = Pick(legends, build);
            given = Granting(() => pawn.AddItem(legend.ClassName));
            if (given != null)
                item = legend;
            else
                Console.WriteLine($"[Roulette] the game wouldn't give the Legendary {legend.ClassName} (Street Brawl only?): rolling a normal item");
        }

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

        // Give first and charge only if that worked, so a failed spin never costs souls.
        given ??= Granting(() => pawn.AddItem(item.ClassName))
                  ?? throw new CommandException($"Couldn't give you {item.Name}. No souls were taken.");
        NoteGranted(slot, item.ClassName);

        // The game's own SetUpgradeBits; whether it applies the Corrupted bonuses and drawback is checked in game.
        string corruptedNote = "";
        if (corrupted)
        {
            int bitsBefore = given.UpgradeBits;
            given.UpgradeBits = bitsBefore | CorruptedBit;
            corruptedNote = $" CORRUPTED (upgrade bits {bitsBefore} -> {given.UpgradeBits})";
        }

        // The shop's own spend path (counted as souls spent), not a raw SetCurrency write.
        if (price > 0)
            Granting(() => pawn.ModifyCurrency(ECurrencyType.EGold, -price, ECurrencySource.EItemPurchase));
        if (canCorrupt)
            _pityMisses[slot] = corrupted ? 0 : misses + 1;

        Console.WriteLine($"[Roulette] {controller.PlayerName} {(exclude != null ? $"rerolled {exclude.ClassName}," : "spun")} {BoxName(category)} {ItemPool.TierPrices[tier - 1]}"
                          + $"{(charge == 0 ? " (free spin)" : charge != null ? $" (paid {charge})" : "")} -> {item.ClassName}{corruptedNote}"
                          + $"{(pity ? " (pity)" : "")}{(jackpot ? " JACKPOT (tier up)" : "")}{(item.Legendary ? " LEGENDARY" : "")}"
                          + $"{(build.Contains(item.Id) ? " (in their build)" : "")} ({candidates.Length} unowned in pool)");
        Console.WriteLine($"[Roulette] before: {before} | after: {Snapshot(controller, pawn)}");
        Timer.Once(1.Seconds(), () =>
        {
            if (Players.FromSlot(slot) is { } c && c.GetHeroPawn() is { } p)
                Console.WriteLine($"[Roulette] 1s later: {Snapshot(c, p)}");
        });

        return new SpinOutcome(item, corrupted, jackpot, BuildStrip(reel ?? [], pool, item));
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

    /// <summary>The player's build: the one they picked in the shop, and whatever is in their quickbuy queue.</summary>
    private HashSet<uint> BuildIds(int slot, CCitadelPlayerPawn pawn)
    {
        var ids = new HashSet<uint>(_builds.GetValueOrDefault(slot) ?? []);
        // CUtlVector layout: the count at +0, the element pointer at +8.
        nint vector = QuickbuyQueue.GetAddress(pawn.Handle);
        int count = Marshal.ReadInt32(vector);
        nint data = Marshal.ReadIntPtr(vector, 8);
        if (count is > 0 and <= 256 && data != 0)
            for (int k = 0; k < count; k++)
                ids.Add((uint)Marshal.ReadInt32(data, 4 * k));
        return ids;
    }

    /// <summary>The build the player picked in the shop: the game sends it to the server (and so to us) as they pick it.</summary>
    private HookResult OnHeroBuild(IncomingMessageContext<CCitadelClientMsg_HeroBuild> ctx)
    {
        int slot = ctx.SenderSlot;
        var build = ctx.Message.HeroBuild;
        var ids = build?.Details?.ModCategories.SelectMany(c => c.Mods).Select(m => m.AbilityId).ToHashSet() ?? [];
        _builds[slot] = ids;
        Console.WriteLine($"[Roulette] slot {slot} picked the build '{build?.Name}': {ids.Count(id => _pool.Find(id) != null)} roulette items");
        if (_viewers.TryGetValue(slot, out var v) && v.Pending == null && Players.FromSlot(slot) is { } controller)
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
        int inBuild = pawn == null || Config.BuildWeight <= 1 ? 0 : unowned.Count(i => BuildIds(controller.Slot, pawn).Contains(i.Id));
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

    private void Close(int slot)
    {
        if (!_viewers.Remove(slot))
            return;
        // DestroyLayout also gives back a cursor the panel asked for.
        if (Players.FromSlot(slot) is { } controller)
            UI.Panel(RoulettePanel.Id).DestroyLayout(controller.Recipients);
    }

    private void OnClientResync(int slot)
    {
        if (!_viewers.TryGetValue(slot, out var v) || Players.FromSlot(slot) is not { } controller)
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
                                          Landing(controller, v));
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
        v.Reel = outcome.Strip[(RoulettePanel.Winner - 2)..(RoulettePanel.Winner + 3)];
    }

    /// <summary>Picks a box (category, null for the wildcard box, and tier) on the sheet.</summary>
    private void Select(CCitadelPlayerController controller, ItemCategory? category, int tier, string? sound = null)
    {
        if (!_viewers.TryGetValue(controller.Slot, out var v) || v.Pending != null)
            return;
        if (sound != null)
            Play(controller, sound);
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

        // Start from a clean panel when the last result is still on show (it un-dims the pool and marks the new owned item).
        if (v.Landed != null)
        {
            v.Landed = null;
            Render(controller, v);
        }

        SpinOutcome outcome;
        try
        {
            outcome = Spin(controller, v.Category, v.Tier, v.Reel, reroll != null ? fee : free ? 0 : null, reroll);
        }
        catch (CommandException ex)
        {
            Play(controller, Sfx.Error);
            ShowResult(controller, v, id => RoulettePanel.Message(id, "Can't spin", ex.Message));
            return;
        }
        if (free)
            _freeSpins[slot]--;
        if (reroll != null && controller.GetHeroPawn() is { } pawn)
            Granting(() => pawn.RemoveItem(reroll.ClassName));
        v.Pending = outcome;
        Play(controller, Sfx.Purchase);

        // Swap in the spin strip, then turn it once it exists on the client (a transition needs a node to move).
        string stripId = $"rsStrip{++v.Seq}";
        RoulettePanel.StartSpin(to, v.StripId, stripId, outcome.Strip, v.Category);
        v.StripId = stripId;

        Timer.Once(RoulettePanel.TurnDelayMs.Milliseconds(), () =>
        {
            if (_viewers.TryGetValue(slot, out var now) && now.StripId == stripId && Players.FromSlot(slot) is { } c)
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
            || Players.FromSlot(slot) is not { } controller)
            return;
        Settle(v, outcome);
        if (outcome.Item.Imbue)
            _lastImbue[slot] = outcome.Item.ClassName;

        ShowLandedResult(controller, v);
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
            if (Players.FromSlot(slot) is not { } controller || controller.GetHeroPawn() is not { } pawn)
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
            if (Players.FromSlot(slot)?.GetHeroPawn() is { } pawn && HasShopOpen.Get(pawn.Handle))
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
        if (e.CurrencyType != ECurrencyType.EGold)
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
            if (_granting)
                return HookResult.Continue;
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
            if (Players.FromSlot(slot)?.GetHeroPawn() is not { } pawn)
                return;
            _refunding = true;
            try { pawn.ModifyCurrency(ECurrencyType.EGold, refund, ECurrencySource.EItemSale, silent, forceGain, spendOnly); }
            finally { _refunding = false; }
        });
        return HookResult.Stop;
    }
}
