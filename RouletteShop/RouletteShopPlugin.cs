using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;

namespace RouletteShop;

public class RouletteShopPlugin : DeadworksPluginBase
{
    public override string Name => "Roulette Shop";

    /// <summary>ABILITY_UPGRADE_BIT_CORRUPTED in the game's AbilityUpgradeBits_t (schema, build of Oct 09 2026).</summary>
    private const int CorruptedBit = 128;

    private const string SpinUsage = "!roulette <weapon|vitality|spirit> <800|1600|3200|6400>";

    [PluginConfig]
    public RouletteConfig Config { get; set; } = new();

    private ItemPool _pool = null!;

    /// <summary>Players with the roulette panel open, by slot.</summary>
    private readonly Dictionary<int, Viewer> _viewers = new();

    private sealed class Viewer
    {
        public ItemCategory Category = ItemCategory.Weapon;
        public int Tier = 1;
        public bool ViaShopKey;
        public RouletteItem[] Reel = [];
        public RouletteItem? Landed;
        public bool LandedCorrupted;
        public SpinOutcome? Pending;          // given and charged, reel still turning
        public string StripId = "rsStrip0";
        public string ResultId = "rsResult0";
        public int Seq;
        public int Souls = -1;
        public DateTime? ShopCloseSentAt;     // when ForceShopClosed went out, to recognise the exit_item_shop it causes
    }

    /// <summary>A spin that has happened: the item is given and the souls taken.</summary>
    private sealed record SpinOutcome(RouletteItem Item, bool Corrupted, RouletteItem[] Strip);

    public override void OnLoad(bool isReload)
    {
        _pool = ItemPool.Load();
        Console.WriteLine(isReload ? "Roulette Shop reloaded!" : "Roulette Shop loaded!");
        Console.WriteLine($"[Roulette] Item pool: {_pool.Count} items ({_pool.Summary()}), {_pool.CorruptibleCount} with a Corrupted version");
        LogCorruptedSettings();

        UI.Panel(RoulettePanel.Id)
            .On("cat", e => Select(e.Caller, ParseCategory(e.ArgAt(0)), null))
            .On("tier", e => Select(e.Caller, null, int.TryParse(e.ArgAt(0), out var t) && t >= 1 && t <= ItemPool.TierPrices.Length ? t : null))
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

    public override void OnConfigReloaded() => LogCorruptedSettings();

    private void LogCorruptedSettings()
        => Console.WriteLine($"[Roulette] Corrupted: enabled {Config.CorruptedEnabled}, chance {Config.CorruptedChancePercent}%, force {Config.ForceCorrupted}");

    public override void OnClientDisconnect(ClientDisconnectedEvent args) => _viewers.Remove(args.Slot);

    [Command("hello", Description = "Say hello")]
    public void CmdHello(Caller caller)
    {
        caller.Reply($"Hello, {caller.Name}!");
    }

    // ─── spinning ─────────────────────────────────────────────────────────────

    [Command("roulette", Description = "Open the Roulette Shop, or spin straight away: roulette [weapon|vitality|spirit] [800|1600|3200|6400]")]
    public void CmdRoulette(Caller caller, string? category = null, int? price = null)
    {
        // Parsed here rather than bound as an optional enum, whose usage line would say "[category]" instead of the names.
        ItemCategory? cat = null;
        if (category != null)
            cat = ParseCategory(category) ?? throw new CommandException("Category must be weapon, vitality or spirit.");

        int tier = 0;
        if (price is { } p)
        {
            tier = Array.IndexOf(ItemPool.TierPrices, p) + 1;
            if (tier == 0)
                throw new CommandException("Price must be 800, 1600, 3200 or 6400.");
        }

        var controller = caller.Player ?? throw new CommandException("Spin from in game.");
        if (cat == null && price == null)
        {
            if (_viewers.ContainsKey(controller.Slot))
                Close(controller.Slot);
            else
                Open(controller, viaShopKey: false);
            return;
        }
        if (cat == null || price == null)
            throw new CommandException("Usage: " + SpinUsage);

        var spin = Spin(controller, cat.Value, tier);
        caller.Reply($"Roulette: you got {(spin.Corrupted ? "CORRUPTED " : "")}{spin.Item.Name} ({cat}, {price}).");
        if (_viewers.TryGetValue(controller.Slot, out var v) && v.Pending == null)
            Render(controller, v);
    }

    /// <summary>
    /// The whole server-side spin: checks souls, rolls an item the player doesn't own (and maybe Corrupted), gives it, then
    /// takes the flat tier price. Throws <see cref="CommandException"/> with a message for the player when it can't.
    /// </summary>
    private SpinOutcome Spin(CCitadelPlayerController controller, ItemCategory category, int tier, RouletteItem[]? reel = null)
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
            throw new CommandException($"You already own every {category} item at {price}.");
        var item = candidates[Random.Shared.Next(candidates.Length)];

        // Only items with a Corrupted version can come back Corrupted (tiers 3 and 4).
        bool corrupted = item.Corruptible && Config.CorruptedEnabled
                         && (Config.ForceCorrupted || Random.Shared.NextDouble() * 100 < Config.CorruptedChancePercent);

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

        Console.WriteLine($"[Roulette] {controller.PlayerName} spun {category} {price} -> {item.ClassName}{corruptedNote} ({candidates.Length} unowned in pool)");
        Console.WriteLine($"[Roulette] before: {before} | after: {Snapshot(controller, pawn)}");
        int slot = controller.Slot;
        Timer.Once(1.Seconds(), () =>
        {
            if (Players.FromSlot(slot) is { } c && c.GetHeroPawn() is { } p)
                Console.WriteLine($"[Roulette] 1s later: {Snapshot(c, p)}");
        });

        return new SpinOutcome(item, corrupted, BuildStrip(reel ?? [], pool, item));
    }

    /// <summary>A category by name ("weapon"); unlike Enum.TryParse, numbers don't count.</summary>
    private static ItemCategory? ParseCategory(string text) => Enum.GetValues<ItemCategory>().Cast<ItemCategory?>()
        .FirstOrDefault(c => c.ToString()!.Equals(text, StringComparison.OrdinalIgnoreCase));

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
        var view = new RoulettePanel.View(v.Category, v.Tier, v.Souls, pool, owned, v.Reel, v.ViaShopKey, Config.CorruptedChancePercent,
                                          v.Landed, v.LandedCorrupted);
        UI.Panel(RoulettePanel.Id).BuildLayout(controller.Recipients, RoulettePanel.Layout(view));
    }

    private static void Settle(Viewer v, SpinOutcome outcome)
    {
        v.Pending = null;
        v.Landed = outcome.Item;
        v.LandedCorrupted = outcome.Corrupted;
        v.Reel = outcome.Strip[(RoulettePanel.Winner - 2)..(RoulettePanel.Winner + 3)];
    }

    private void Select(CCitadelPlayerController controller, ItemCategory? category, int? tier)
    {
        if (!_viewers.TryGetValue(controller.Slot, out var v) || v.Pending != null)
            return;
        if (category is { } c)
            v.Category = c;
        if (tier is { } t)
            v.Tier = t;
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
            ShowResult(controller, v, id => RoulettePanel.Message(id, "Can't spin", ex.Message));
            return;
        }
        v.Pending = outcome;

        // Swap in the spin strip, then turn it once it exists on the client (a transition needs a node to move).
        string stripId = $"rsStrip{++v.Seq}";
        RoulettePanel.StartSpin(to, v.StripId, stripId, outcome.Strip, v.Category);
        v.StripId = stripId;

        Timer.Once(RoulettePanel.TurnDelayMs.Milliseconds(), () =>
        {
            if (_viewers.TryGetValue(slot, out var now) && now.StripId == stripId && Players.FromSlot(slot) is { } c)
                RoulettePanel.TurnReel(c.Recipients, stripId);
        });
        Timer.Once((RoulettePanel.TurnDelayMs + RoulettePanel.SpinMs + 300).Milliseconds(), () => Land(slot, stripId));
    }

    private void Land(int slot, string stripId)
    {
        if (!_viewers.TryGetValue(slot, out var v) || v.StripId != stripId || v.Pending is not { } outcome
            || Players.FromSlot(slot) is not { } controller)
            return;
        Settle(v, outcome);

        ShowResult(controller, v, id => RoulettePanel.Result(id, outcome.Item, outcome.Corrupted));

        v.Souls = controller.GetHeroPawn()?.GetCurrency(ECurrencyType.EGold) ?? v.Souls;
        RoulettePanel.ShowLanding(controller.Recipients, stripId, outcome.Item, outcome.Corrupted, v.Souls, ReadyOrBroke(v));
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

    // --- The normal shop is removed: nothing can be bought from or sold to it. Spinning is the only way to get items. ---
    // Command names from the server log of a real open / buy / sell (Checkpoint 3). Blocking open_item_shop doesn't stop
    // the client showing the shop screen, and CCitadelUserMsg_ForceShopClosed sent at that same moment (with
    // open_item_shop blocked) didn't close it (tested in game). So the shop key opens the roulette over it, opaque, and
    // closing the shop (exit_item_shop) closes the roulette. With CloseGameShop the server lets open_item_shop through
    // and sends ForceShopClosed a moment later; an exit_item_shop right after it means the shop did close, and the
    // roulette carries on by itself (see-through backdrop, its own cursor and CLOSE button, the shop key toggles it).

    private const int ShopCloseDelayMs = 250, ShopCloseWindowMs = 1500;

    private void OnShopKey(CCitadelPlayerController controller)
    {
        int slot = controller.Slot;
        if (_viewers.ContainsKey(slot))
        {
            // The shop key toggles: close the roulette (and the shop the client is opening again).
            Close(slot);
            if (Config.CloseGameShop)
                Timer.Once(ShopCloseDelayMs.Milliseconds(), () => SendShopClosed(slot));
            return;
        }
        Open(controller, viaShopKey: true);
        if (Config.CloseGameShop && _viewers.TryGetValue(slot, out var v))
            Timer.Once(ShopCloseDelayMs.Milliseconds(), () =>
            {
                if (!_viewers.TryGetValue(slot, out var now) || now != v || !SendShopClosed(slot))
                    return;
                v.ShopCloseSentAt = DateTime.UtcNow;
                Timer.Once(ShopCloseWindowMs.Milliseconds(), () =>
                {
                    if (v.ShopCloseSentAt != null && _viewers.TryGetValue(slot, out var later) && later == v)
                        Console.WriteLine($"[Roulette] the game's shop did not close on ForceShopClosed (slot {slot})");
                });
            });
    }

    private static bool SendShopClosed(int slot)
    {
        if (Players.FromSlot(slot) is not { } controller)
            return false;
        NetMessages.Send(new CCitadelUserMsg_ForceShopClosed(), controller.Recipients);
        Console.WriteLine($"[Roulette] sent ForceShopClosed to {controller.PlayerName}");
        return true;
    }

    private void OnShopExit(CCitadelPlayerController controller)
    {
        if (!_viewers.TryGetValue(controller.Slot, out var v))
            return;
        if (v.ShopCloseSentAt is { } sent && (DateTime.UtcNow - sent).TotalMilliseconds < ShopCloseWindowMs)
        {
            // Our ForceShopClosed did it: the roulette stays, now by itself over the game.
            Console.WriteLine($"[Roulette] the game's shop closed {(DateTime.UtcNow - sent).TotalMilliseconds:0} ms after ForceShopClosed");
            v.ShopCloseSentAt = null;
            v.ViaShopKey = false;
            Render(controller, v);
            UI.Panel(RoulettePanel.Id).RequestCursor(controller.Recipients);
            return;
        }
        if (v.ViaShopKey)
            Close(controller.Slot);
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
                if (controller != null)
                    OnShopKey(controller);
                // ForceShopClosed only gets its chance with the shop open on the server too; buying and selling stay blocked.
                return Config.CloseGameShop ? HookResult.Continue : HookResult.Stop;
            case "exit_item_shop":
                if (controller != null)
                    OnShopExit(controller);
                return HookResult.Continue;
            case "buyitem":
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
