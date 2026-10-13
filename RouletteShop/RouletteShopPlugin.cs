using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;

namespace RouletteShop;

public class RouletteShopPlugin : DeadworksPluginBase
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
        public RouletteItem[] Reel = [];
        public RouletteItem? Landed;
        public bool LandedCorrupted;
        public bool LandedJackpot;
        public SpinOutcome? Pending;          // given and charged, reel still turning
        public string StripId = "rsStrip0";
        public string ResultId = "rsResult0";
        public int Seq;
        public int Souls = -1;
    }

    /// <summary>The usercmd in_shop flag each player last sent (true while the game's shop screen is open), and how many times it changed.</summary>
    private readonly Dictionary<int, (bool InShop, int Changes)> _inShop = new();

    /// <summary>How long in_shop must stay off before the sheet closes: it flickers off for a few ms with the shop open.</summary>
    private const int ShopLeftDelayMs = 1000;

    /// <summary>Spins on items with a Corrupted version since each player's last Corrupted one (for CorruptedPitySpins).</summary>
    private readonly Dictionary<int, int> _pityMisses = new();

    /// <summary>A spin that has happened: the item is given and the souls taken. Jackpot: the item is from the tier above.</summary>
    private sealed record SpinOutcome(RouletteItem Item, bool Corrupted, bool Jackpot, RouletteItem[] Strip);

    public override void OnLoad(bool isReload)
    {
        _pool = ItemPool.Load();
        Console.WriteLine(isReload ? "Roulette Shop reloaded!" : "Roulette Shop loaded!");
        Console.WriteLine($"[Roulette] Item pool: {_pool.Count} items ({_pool.Summary()}), {_pool.CorruptibleCount} with a Corrupted version");
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
            .On("close", e => Close(e.Caller.Slot));
        UI.ClientResync += OnClientResync;
        Timer.Every(1.Seconds(), RefreshSouls);
    }

    public override void OnUnload()
    {
        UI.ClientResync -= OnClientResync;
        foreach (int slot in _viewers.Keys.ToList())
            Close(slot);
    }

    public override void OnConfigReloaded()
    {
        LogSettings();
        // Show the new settings on open sheets (the wildcard button, the odds line).
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
        Console.WriteLine($"[Roulette] Jackpot {Config.JackpotChancePercent}%, wildcard box {Config.WildcardBox}, sounds {Config.Sounds}");
    }

    public override void OnClientDisconnect(ClientDisconnectedEvent args)
    {
        _viewers.Remove(args.Slot);
        _inShop.Remove(args.Slot);
        _pityMisses.Remove(args.Slot);
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

        var spin = Spin(controller, cat, tier);
        PlayLanding(controller, spin);
        caller.Reply($"Roulette: you got {(spin.Corrupted ? "CORRUPTED " : "")}{spin.Item.Name}{(spin.Jackpot ? " (JACKPOT: one tier up)" : "")}"
                     + $" ({BoxName(cat)}, {price}).");
        if (_viewers.TryGetValue(controller.Slot, out var v) && v.Pending == null)
            Render(controller, v);
    }

    /// <summary>
    /// The whole server-side spin: checks souls, rolls an item the player doesn't own (now and then from the tier above:
    /// the jackpot; maybe Corrupted), gives it, then takes the flat tier price. A null category is the wildcard box.
    /// Throws <see cref="CommandException"/> with a message for the player when it can't.
    /// </summary>
    private SpinOutcome Spin(CCitadelPlayerController controller, ItemCategory? category, int tier, RouletteItem[]? reel = null)
    {
        var pawn = controller.GetHeroPawn() ?? throw new CommandException("You need a hero to spin.");
        int price = ItemPool.TierPrices[tier - 1];
        int souls = pawn.GetCurrency(ECurrencyType.EGold);
        if (souls < price)
            throw new CommandException($"Not enough souls: you have {souls}, a spin costs {price}.");

        // AddItem gives duplicates, which the normal shop never does, so only roll items the player doesn't have.
        var pool = _pool.Get(category, tier);
        var candidates = pool.Where(i => !Owns(pawn, i)).ToArray();
        if (candidates.Length == 0)
            throw new CommandException(category == null ? $"You already own every item at {price}." : $"You already own every {category} item at {price}.");

        // Jackpot: now and then the item comes from the next tier up, for the same price.
        bool jackpot = false;
        if (tier < ItemPool.TierPrices.Length && Random.Shared.NextDouble() * 100 < Config.JackpotChancePercent)
        {
            var up = _pool.Get(category, tier + 1).Where(i => !Owns(pawn, i)).ToArray();
            if (up.Length > 0)
            {
                candidates = up;
                jackpot = true;
            }
        }
        var item = candidates[Random.Shared.Next(candidates.Length)];

        // Only items with a Corrupted version can come back Corrupted (tiers 3 and 4). Pity: after CorruptedPitySpins of
        // those in a row without one, the next one is.
        int slot = controller.Slot;
        int misses = _pityMisses.GetValueOrDefault(slot);
        bool canCorrupt = item.Corruptible && Config.CorruptedEnabled;
        bool corrupted = canCorrupt && (Config.ForceCorrupted || Random.Shared.NextDouble() * 100 < Config.CorruptedChancePercent);
        bool pity = canCorrupt && !corrupted && Config.CorruptedPitySpins > 0 && misses + 1 >= Config.CorruptedPitySpins;
        corrupted |= pity;

        var before = Snapshot(controller, pawn);

        // Give first and charge only if that worked, so a failed spin never costs souls.
        var given = pawn.AddItem(item.ClassName)
                    ?? throw new CommandException($"Couldn't give you {item.Name}. No souls were taken.");

        // The game's own SetUpgradeBits; whether it applies the Corrupted bonuses and drawback is checked in game.
        string corruptedNote = "";
        if (corrupted)
        {
            int bitsBefore = given.UpgradeBits;
            given.UpgradeBits = bitsBefore | CorruptedBit;
            corruptedNote = $" CORRUPTED (upgrade bits {bitsBefore} -> {given.UpgradeBits})";
        }

        // The shop's own spend path (counted as souls spent), not a raw SetCurrency write.
        pawn.ModifyCurrency(ECurrencyType.EGold, -price, ECurrencySource.EItemPurchase);
        if (canCorrupt)
            _pityMisses[slot] = corrupted ? 0 : misses + 1;

        Console.WriteLine($"[Roulette] {controller.PlayerName} spun {BoxName(category)} {price} -> {item.ClassName}{corruptedNote}"
                          + $"{(pity ? " (pity)" : "")}{(jackpot ? " JACKPOT (tier up)" : "")} ({candidates.Length} unowned in pool)");
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

    /// <summary>The Corrupted line on the sheet: the chance, and how many spins until the pity one.</summary>
    private string OddsLine(int slot)
    {
        if (!Config.CorruptedEnabled)
            return "Corrupted spins are off";
        string odds = $"{Config.CorruptedChancePercent:0.##}% Corrupted at 3,200 and 6,400";
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
        => Play(controller, outcome.Corrupted ? Sfx.Corrupted : outcome.Jackpot ? Sfx.Jackpot : Sfx.Appear(outcome.Item.Category));

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
                UI.Panel(RoulettePanel.Id).ReleaseCursor(controller.Recipients);
                Render(controller, v);
            }
            return;
        }

        _viewers[slot] = v = new Viewer { ViaShopKey = viaShopKey };
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

        var owned = pawn == null ? new HashSet<string>() : pool.Where(i => Owns(pawn, i)).Select(i => i.ClassName).ToHashSet();
        var view = new RoulettePanel.View(v.Category, v.Tier, v.Souls, pool, owned, v.Reel, v.ViaShopKey, Config.WildcardBox,
                                          OddsLine(controller.Slot), v.Landed, v.LandedCorrupted, v.LandedJackpot);
        UI.Panel(RoulettePanel.Id).BuildLayout(controller.Recipients, RoulettePanel.Layout(view));
    }

    private static void Settle(Viewer v, SpinOutcome outcome)
    {
        v.Pending = null;
        v.Landed = outcome.Item;
        v.LandedCorrupted = outcome.Corrupted;
        v.LandedJackpot = outcome.Jackpot;
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

    private void PanelSpin(CCitadelPlayerController controller)
    {
        int slot = controller.Slot;
        if (!_viewers.TryGetValue(slot, out var v) || v.Pending != null)
            return;
        var to = controller.Recipients;

        // Start from a clean panel when the last result is still on show (it un-dims the pool and marks the new owned item).
        if (v.Landed != null)
        {
            v.Landed = null;
            Render(controller, v);
        }

        SpinOutcome outcome;
        try
        {
            outcome = Spin(controller, v.Category, v.Tier, v.Reel);
        }
        catch (CommandException ex)
        {
            Play(controller, Sfx.Error);
            ShowResult(controller, v, id => RoulettePanel.Message(id, "Can't spin", ex.Message));
            return;
        }
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

        ShowResult(controller, v, id => RoulettePanel.Result(id, outcome.Item, outcome.Corrupted, outcome.Jackpot));
        PlayLanding(controller, outcome);

        var pawn = controller.GetHeroPawn();
        v.Souls = pawn?.GetCurrency(ECurrencyType.EGold) ?? v.Souls;
        int unowned = pawn == null ? 0 : _pool.Get(v.Category, v.Tier).Count(i => !Owns(pawn, i));
        RoulettePanel.ShowLanding(controller.Recipients, v.Category, outcome.Corrupted, outcome.Jackpot, v.Souls, unowned, OddsLine(slot),
                                  ReadyOrBroke(v));
    }

    /// <summary>Replaces the result card (fresh id: a removed node lingers a frame).</summary>
    private static void ShowResult(CCitadelPlayerController controller, Viewer v, Func<string, UINode> make)
    {
        string id = $"rsResult{++v.Seq}";
        RoulettePanel.ShowResult(controller.Recipients, v.ResultId, make(id));
        v.ResultId = id;
    }

    private static RoulettePanel.SpinState ReadyOrBroke(Viewer v)
        => v.Souls < ItemPool.TierPrices[v.Tier - 1] ? RoulettePanel.SpinState.Broke : RoulettePanel.SpinState.Ready;

    /// <summary>Keeps the open panels' soul count (and the spin button) up to date while players earn souls.</summary>
    private void RefreshSouls()
    {
        foreach (var (slot, v) in _viewers)
        {
            if (Players.FromSlot(slot) is not { } controller || controller.GetHeroPawn() is not { } pawn)
                continue;
            int souls = pawn.GetCurrency(ECurrencyType.EGold);
            if (souls == v.Souls)
                continue;
            v.Souls = souls;
            RoulettePanel.UpdateSouls(controller.Recipients, souls, v.Category, v.Pending == null ? ReadyOrBroke(v) : null);
        }
    }

    // --- The normal shop can't sell anything: spinning is the only way to get items. ---
    // Command names from the server log of a real open / buy / sell (Checkpoint 3). The game's shop screen stays open (the
    // server couldn't close it: blocking open_item_shop didn't, nor did CCitadelUserMsg_ForceShopClosed sent with it),
    // and the roulette sheet sits beside its item page (design #3, the user's choice). Clicking an item there sends
    // buyitem, which is blocked and instead picks that item's category and price on the sheet. The sheet closes with the
    // shop: on exit_item_shop, or when the player's usercmds stop saying in_shop (however the shop closed).

    public override void OnProcessUsercmds(ProcessUsercmdsEvent args)
    {
        bool inShop = args.Usercmds[^1].InShop;
        int slot = args.PlayerSlot;
        if (_inShop.TryGetValue(slot, out var last) && last.InShop == inShop)
            return;
        int changes = last.Changes + 1;
        _inShop[slot] = (inShop, changes);
        if (inShop || !_viewers.ContainsKey(slot))
            return;
        // The Checkpoint 3 log shows in_shop dropping for ~30 ms with the shop still open: close only if it stays off.
        Timer.Once(ShopLeftDelayMs.Milliseconds(), () =>
        {
            if (_inShop.TryGetValue(slot, out var now) && now.Changes == changes && _viewers.TryGetValue(slot, out var v) && v.ViaShopKey)
            {
                Console.WriteLine($"[Roulette] slot {slot} left the shop (in_shop off for {ShopLeftDelayMs} ms): closing the roulette");
                Close(slot);
            }
        });
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
                                     || e.Command.Contains("broker") || e.Command.Contains("trade")))
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

        // Backstop if a sale gets past the command block some other way: the item goes, but no souls come back.
        if (e.Source == ECurrencySource.EItemSale)
        {
            Console.WriteLine($"[Roulette] blocked a {e.Amount}-soul sale refund for {e.Pawn.Controller?.PlayerName}");
            return HookResult.Stop;
        }
        if (e.Source == ECurrencySource.EItemPurchase)
            Console.WriteLine($"[Roulette] souls EItemPurchase {e.Amount} for {e.Pawn.Controller?.PlayerName}");
        return HookResult.Continue;
    }
}
