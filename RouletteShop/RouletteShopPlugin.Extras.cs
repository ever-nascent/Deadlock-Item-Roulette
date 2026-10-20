using DeadworksManaged.Api;

namespace RouletteShop;

/// <summary>
/// The extras the user picked after the textured sheet: spinning only at a shop, a backstop for purchases that get past the
/// buy block, imbuing, chat shout-outs, free spins from kill streaks, lucky hours, double or nothing, and rerolls.
/// </summary>
public partial class RouletteShopPlugin
{
    // ─── spinning only at a shop, like buying ─────────────────────────────────

    /// <summary>Whether the hero stands in a shop zone (set by the game's item shop triggers, where buying works).</summary>
    private static readonly SchemaAccessor<bool> InItemShopZone = new("CCitadelPlayerPawn"u8, "m_bInItemShopZone"u8);

    /// <summary>
    /// A shop zone, or the base (its regeneration zone). The user, with friends: "when they try to use the roulette in base,
    /// it doesnt seem to work". The log: a friend at base had m_bInItemShopZone false (refused 4 times), while the host
    /// spun at base; so the item shop trigger covers only part of the base.
    /// </summary>
    private bool AtShop(CCitadelPlayerPawn pawn) => !Config.SpinOnlyAtShop || InItemShopZone.Get(pawn.Handle) || pawn.InRegenerationZone;

    // ─── the backstop for purchases that get past the buy block (quickbuy auto-buy, say) ───

    /// <summary>True while the roulette itself gives, takes back or charges for an item.</summary>
    private bool _granting;

    private readonly Dictionary<int, DateTime> _blockedPurchase = new();
    private readonly Dictionary<int, List<(string Name, DateTime At)>> _strays = new();

    /// <summary>
    /// Items that left a player outside the roulette (ability_removed): an upgrade bought by quickbuy uses up the item it
    /// upgrades, which is given back when the upgrade is taken back.
    /// </summary>
    private readonly Dictionary<int, List<(string Name, DateTime At)>> _consumed = new();

    /// <summary>Items each player sold (sellitem): never given back as an upgrade's used-up part.</summary>
    private readonly Dictionary<int, List<(string Name, DateTime At)>> _sold = new();

    /// <summary>Whether the player has been told in chat about the purchase blocked around now.</summary>
    private readonly HashSet<int> _toldBlocked = new();

    /// <summary>Items the roulette gave, in case the game reports them (ability_added) after the grant returned.</summary>
    private readonly Dictionary<int, List<(string Name, DateTime At)>> _granted = new();

    private static readonly TimeSpan StrayWindow = TimeSpan.FromSeconds(2);

    private T Granting<T>(Func<T> act)
    {
        _granting = true;
        try { return act(); }
        finally { _granting = false; }
    }

    private void Granting(Action act) => Granting(() => { act(); return 0; });

    private static List<(string Name, DateTime At)> Recent(Dictionary<int, List<(string Name, DateTime At)>> lists, int slot)
    {
        if (!lists.TryGetValue(slot, out var list))
            lists[slot] = list = [];
        list.RemoveAll(e => DateTime.UtcNow - e.At > StrayWindow);
        return list;
    }

    private void NoteGranted(int slot, string name) => Recent(_granted, slot).Add((name, DateTime.UtcNow));

    /// <summary>An item arrived. If a purchase was stopped around now, it's that purchase's item: take it back.</summary>
    [GameEventHandler("ability_added")]
    public HookResult OnAbilityAdded(AbilityAddedEvent e)
    {
        string? name = e.Ability?.SubclassVData?.Name;
        if (_granting || !Config.BlockOtherPurchases || name == null || !name.StartsWith("upgrade_") || e.Userid?.Controller is not { } controller)
            return HookResult.Continue;
        int slot = controller.Slot;
        if (Recent(_granted, slot).Any(g => g.Name == name))
            return HookResult.Continue;
        Recent(_strays, slot).Add((name, DateTime.UtcNow));
        TakeBackStrays(slot);
        return HookResult.Continue;
    }

    /// <summary>The hero's quickbuy auto-purchase switch (networked): on, the game buys queued items as souls come in.</summary>
    private static readonly SchemaAccessor<bool> QuickbuyAutoPurchase = new("CCitadelPlayerPawn"u8, "m_bQuickbuyAutoPurchase"u8);

    /// <summary>The item auto-purchase is waiting to buy (networked CUtlStringToken; 0 for none).</summary>
    private static readonly SchemaAccessor<uint> QuickbuyAutoPurchaseRequest = new("CCitadelPlayerPawn"u8, "m_unQuickbuyAutoPurchaseRequest"u8);

    /// <summary>
    /// Turns quickbuy auto-purchase back off. The user: "i'd press the quickbuy button and it'd suddenly start buying items
    /// when i had the souls". Switched off here, before it buys; the soul-charge backstop still catches a purchase made
    /// in between. The quickbuy queue itself stays (build-aware spins read it).
    /// </summary>
    private void StopAutoPurchase()
    {
        if (!Config.BlockOtherPurchases)
            return;
        // Not Players.GetAll: it lists no one on this game build (see PlayerAt).
        for (int slot = 0; slot < Players.MaxSlot; slot++)
        {
            if (!Players.IsConnected(slot) || PlayerAt(slot) is not { } controller
                || controller.GetHeroPawn() is not { } pawn || !QuickbuyAutoPurchase.Get(pawn.Handle))
                continue;
            uint request = QuickbuyAutoPurchaseRequest.Get(pawn.Handle);
            QuickbuyAutoPurchase.Set(pawn.Handle, false);
            QuickbuyAutoPurchaseRequest.Set(pawn.Handle, 0);
            Console.WriteLine($"[Roulette] turned off quickbuy auto-purchase for {controller.PlayerName} "
                              + $"(it was waiting to buy {_pool.Find(request)?.ClassName ?? $"id {request}"})");
            Chat.PrintToChat(controller, "Roulette: quickbuy can't buy items here. Spin instead.");
        }
    }

    /// <summary>An item left the player. If a purchase was stopped around now, it was used up by that purchase: give it back.</summary>
    [GameEventHandler("ability_removed")]
    public HookResult OnAbilityRemoved(AbilityRemovedEvent e)
    {
        string? name = e.Ability?.SubclassVData?.Name;
        if (_granting || !Config.BlockOtherPurchases || name == null || !name.StartsWith("upgrade_") || e.Userid?.Controller is not { } controller)
            return HookResult.Continue;
        int slot = controller.Slot;
        if (Recent(_sold, slot).Any(s => s.Name == name))
            return HookResult.Continue;
        Recent(_consumed, slot).Add((name, DateTime.UtcNow));
        TakeBackStrays(slot);
        return HookResult.Continue;
    }

    private void NoteSold(int slot, string name) => Recent(_sold, slot).Add((name, DateTime.UtcNow));

    /// <summary>A soul charge for an item that didn't come from a spin was stopped (OnModifyCurrency).</summary>
    private void PurchaseBlocked(int slot)
    {
        _blockedPurchase[slot] = DateTime.UtcNow;
        _toldBlocked.Remove(slot);
        TakeBackStrays(slot);
    }

    /// <summary>
    /// Within StrayWindow of a stopped purchase: takes back the items it gave and gives back the items it used up. Runs on
    /// each event, as the game's order of charge, removal and addition isn't known (and may differ for quickbuy).
    /// </summary>
    private void TakeBackStrays(int slot)
    {
        if (!_blockedPurchase.TryGetValue(slot, out var at) || DateTime.UtcNow - at > StrayWindow)
            return;
        var strays = Recent(_strays, slot);
        var consumed = Recent(_consumed, slot);
        if (strays.Count + consumed.Count == 0 || PlayerAt(slot)?.GetHeroPawn() is not { } pawn)
            return;
        foreach (var (name, _) in strays)
        {
            bool removed = Granting(() => pawn.RemoveItem(name));
            Console.WriteLine($"[Roulette] took back {name} from slot {slot}: it was bought outside the roulette ({(removed ? "removed" : "couldn't remove it")})");
        }
        strays.Clear();
        var giveBack = consumed.Select(c => c.Name).ToList();
        consumed.Clear();
        // Next tick, not from inside the game's own removal.
        if (giveBack.Count > 0)
            Timer.NextTick(() =>
            {
                if (PlayerAt(slot)?.GetHeroPawn() is not { } p)
                    return;
                foreach (string name in giveBack)
                {
                    NoteGranted(slot, name);
                    bool given = Granting(() => p.AddItem(name)) != null;
                    Console.WriteLine($"[Roulette] gave {name} back to slot {slot}: a blocked purchase used it up ({(given ? "given" : "couldn't give it")})");
                }
            });
        if (_toldBlocked.Add(slot))
            Chat.PrintToChat(slot, "Roulette: buying from the shop is off, quickbuy too. Spin instead.");
    }

    // ─── imbuing ──────────────────────────────────────────────────────────────

    /// <summary>The imbue item each player's last spin gave, until it is imbued (for the sheet's keys and roulette_imbue).</summary>
    private readonly Dictionary<int, string> _lastImbue = new();

    [Command("roulette_imbue", Description = "Imbue the item your last spin gave into one of your abilities: roulette_imbue <1-4>")]
    public void CmdImbue(Caller caller, int ability)
    {
        var controller = caller.Player ?? throw new CommandException("Imbue from in game.");
        caller.Reply("Roulette: " + Imbue(controller, ability));
    }

    /// <summary>Imbues the last spin's imbue item into ability <paramref name="n"/> (1-4). Throws with a message when it can't.</summary>
    private string Imbue(CCitadelPlayerController controller, int n)
    {
        int slot = controller.Slot;
        if (n is < 1 or > 4)
            throw new CommandException("Pick ability 1, 2, 3 or 4.");
        if (!_lastImbue.TryGetValue(slot, out var name) || controller.GetHeroPawn() is not { } pawn)
            throw new CommandException("Your last spin didn't give an item to imbue.");
        var result = pawn.ImbueItem(name, (EAbilitySlot)(n - 1));
        if (result != ImbueResult.Success)
            throw new CommandException($"Ability {n} won't take it ({result}).");
        _lastImbue.Remove(slot);
        Console.WriteLine($"[Roulette] {controller.PlayerName} imbued {name} into ability {n}");
        Play(controller, Sfx.Click(null));
        if (_viewers.TryGetValue(slot, out var v) && v.Landed?.ClassName == name && v.Pending == null)
        {
            v.ImbuedSlot = n;
            ShowLandedResult(controller, v);
        }
        return $"imbued into ability {n}.";
    }

    // ─── chat shout-outs ──────────────────────────────────────────────────────

    private void ShoutOut(CCitadelPlayerController controller, RouletteItem item, bool corrupted, bool jackpot)
    {
        if (!Config.ShoutOuts)
            return;
        string? what = item.Legendary ? $"the LEGENDARY {item.Name}"
            : corrupted && jackpot ? $"a CORRUPTED {item.Name} on a JACKPOT"
            : corrupted ? $"a CORRUPTED {item.Name}"
            : jackpot ? $"a JACKPOT: {item.Name}, a {RoulettePanel.Souls(ItemPool.TierPrices[item.Tier - 1])} item"
            : null;
        if (what != null)
            Chat.PrintToChatAll($"Roulette: {controller.PlayerName} landed {what}!");
    }

    // ─── free spins from kill streaks ─────────────────────────────────────────

    private readonly Dictionary<int, int> _streaks = new(), _freeSpins = new();

    [GameEventHandler("player_death")]
    public HookResult OnPlayerDeath(PlayerDeathEvent e)
    {
        int victim = e.UseridController?.Slot ?? -1;
        if (Config.FreeSpinKillStreak > 0 && e.AttackerController is { } killer && killer.Slot != victim)
        {
            int streak = _streaks[killer.Slot] = _streaks.GetValueOrDefault(killer.Slot) + 1;
            if (streak % Config.FreeSpinKillStreak == 0)
            {
                _freeSpins[killer.Slot] = _freeSpins.GetValueOrDefault(killer.Slot) + 1;
                Console.WriteLine($"[Roulette] {killer.PlayerName}: {streak} kills in a row, a free spin ({_freeSpins[killer.Slot]} waiting)");
                Chat.PrintToChat(killer.Slot, $"Roulette: {streak} kills in a row. Your next spin is free!");
                if (Config.Sounds)
                    killer.PlaySound(Sfx.Jackpot);
            }
        }
        if (victim >= 0)
            _streaks[victim] = 0;
        return HookResult.Continue;
    }

    // ─── lucky hours ──────────────────────────────────────────────────────────

    private DateTime _nextLucky = DateTime.MaxValue;
    private DateTime? _luckyUntil;

    private bool Lucky => _luckyUntil is { } until && DateTime.UtcNow < until;

    private double LuckyFactor => Lucky ? Math.Max(1, Config.LuckyHourMultiplier) : 1;

    private double CorruptedChance => Math.Min(100, Config.CorruptedChancePercent * LuckyFactor);

    private double LegendaryChance => Math.Min(100, Config.LegendaryChancePercent * LuckyFactor);

    /// <summary>The next lucky hour: about LuckyHourEveryMinutes from now (70% to 130% of it).</summary>
    private void ScheduleLucky() => _nextLucky = Config.LuckyHourEveryMinutes > 0
        ? DateTime.UtcNow.AddMinutes(Config.LuckyHourEveryMinutes * (0.7 + 0.6 * Random.Shared.NextDouble()))
        : DateTime.MaxValue;

    private void LuckyTick()
    {
        if (_luckyUntil is { } until)
        {
            if (DateTime.UtcNow >= until)
                EndLucky();
        }
        else if (DateTime.UtcNow >= _nextLucky)
            StartLucky(Config.LuckyHourMinutes);
    }

    private void StartLucky(double minutes)
    {
        _luckyUntil = DateTime.UtcNow.AddMinutes(minutes);
        Console.WriteLine($"[Roulette] lucky hour for {minutes:0.#} minutes");
        Chat.PrintToChatAll($"Roulette: LUCKY HOUR! Corrupted and Legendary chances are {Config.LuckyHourMultiplier:0.#}x for {minutes:0.#} minutes.");
        RefreshLines();
    }

    private void EndLucky()
    {
        _luckyUntil = null;
        ScheduleLucky();
        Console.WriteLine("[Roulette] lucky hour over");
        Chat.PrintToChatAll("Roulette: the lucky hour is over.");
        RefreshLines();
    }

    /// <summary>The odds lines (and the masthead's lucky hour line) on every open sheet.</summary>
    private void RefreshLines()
    {
        foreach (var (slot, v) in _viewers)
            if (PlayerAt(slot) is { } controller)
                RoulettePanel.UpdateLines(controller.Recipients, DrawLine(controller, v.Category, v.Tier), RatesLine(), OddsLine(slot), Lucky);
    }

    // ─── after a spin: double or nothing, reroll ──────────────────────────────

    /// <summary>The items a double or nothing can turn <paramref name="item"/> into: unowned, same category, the tier above.</summary>
    private RouletteItem[] DoubleTargets(CCitadelPlayerPawn pawn, RouletteItem item)
        => item.Legendary || item.Tier >= ItemPool.TierPrices.Length ? [] : _pool.Get(item.Category, item.Tier + 1).Where(i => !Owns(pawn, i)).ToArray();

    private int RerollFee(int tier) => Config.RerollCostPercent > 0
        ? (int)Math.Ceiling(ItemPool.TierPrices[tier - 1] * Config.RerollCostPercent / 100)
        : 0;

    /// <summary>What the sheet offers for the item the last spin gave (null once it's gone).</summary>
    private RoulettePanel.Landing? Landing(CCitadelPlayerController controller, Viewer v)
    {
        if (v.Landed is not { } item || controller.GetHeroPawn() is not { } pawn || !Owns(pawn, item))
            return null;
        bool bet = Config.DoubleOrNothingWinPercent > 0 && DoubleTargets(pawn, item).Length > 0;
        bool[]? slots = item.Imbue && v.ImbuedSlot == null && _lastImbue.GetValueOrDefault(controller.Slot) == item.ClassName
            ? Enumerable.Range(0, 4).Select(k => pawn.AbilityComponent.GetAbilityBySlot((EAbilitySlot)k)?.CanBeImbuedBy(item.ClassName) == true).ToArray()
            : null;
        return new(bet, RerollFee(v.Tier), slots, v.ImbuedSlot, v.LandedDoubled);
    }

    /// <summary>Shows the item the last spin gave again, with what can be done with it now.</summary>
    private void ShowLandedResult(CCitadelPlayerController controller, Viewer v)
    {
        if (v.Landed is { } item)
            ShowResult(controller, v, id => RoulettePanel.Result(id, item, v.LandedCorrupted, v.LandedJackpot, Landing(controller, v)));
    }

    private void OnDouble(CCitadelPlayerController controller)
    {
        int slot = controller.Slot;
        if (!_viewers.TryGetValue(slot, out var v) || v.Pending != null || v.Landed is not { } item || controller.GetHeroPawn() is not { } pawn
            || Config.DoubleOrNothingWinPercent <= 0 || !Owns(pawn, item) || DoubleTargets(pawn, item) is not { Length: > 0 } targets)
            return;
        bool win = Random.Shared.NextDouble() * 100 < Config.DoubleOrNothingWinPercent;
        var next = win ? Pick(targets, BuildIds(slot)) : null;
        Granting(() => pawn.RemoveItem(item.ClassName));
        if (next != null)
        {
            if (Granting(() => pawn.AddItem(next.ClassName)) == null)
            {
                Granting(() => pawn.AddItem(item.ClassName));
                Console.WriteLine($"[Roulette] double or nothing: couldn't give {next.ClassName}, gave {item.ClassName} back");
                return;
            }
            NoteGranted(slot, next.ClassName);
        }
        Console.WriteLine($"[Roulette] {controller.PlayerName} bet {item.ClassName} double or nothing: {(next != null ? $"won {next.ClassName}" : "lost it")}");

        if (next != null)
        {
            v.Landed = next;
            v.LandedCorrupted = v.LandedJackpot = false;
            v.LandedDoubled = true;
            v.ImbuedSlot = null;
            if (next.Imbue)
                _lastImbue[slot] = next.ClassName;
            Play(controller, Sfx.Jackpot);
            ShowLandedResult(controller, v);
            if (Config.ShoutOuts)
                Chat.PrintToChatAll($"Roulette: {controller.PlayerName} went double or nothing and won {next.Name}!");
        }
        else
        {
            v.Landed = null;
            Play(controller, Sfx.Error);
            ShowResult(controller, v, id => RoulettePanel.Message(id, "Nothing", $"Double or nothing: your {item.Name} is gone."));
        }
        RoulettePanel.UpdateLines(controller.Recipients, DrawLine(controller, v.Category, v.Tier), RatesLine(), OddsLine(slot), Lucky);
    }

    private void OnReroll(CCitadelPlayerController controller)
    {
        if (!_viewers.TryGetValue(controller.Slot, out var v) || v.Pending != null || v.Landed is not { } old
            || controller.GetHeroPawn() is not { } pawn || !Owns(pawn, old))
            return;
        int fee = RerollFee(v.Tier);
        if (fee <= 0)
            return;
        // Checked here too, so a reroll that can't happen leaves the result (and its buttons) alone.
        string? why = !AtShop(pawn) ? "Reroll at a shop." : pawn.GetCurrency(ECurrencyType.EGold) < fee ? $"A reroll costs {fee} souls." : null;
        if (why != null)
        {
            Play(controller, Sfx.Error);
            Chat.PrintToChat(controller, "Roulette: " + why);
            return;
        }
        PanelSpin(controller, reroll: old, fee: fee);
    }
}
