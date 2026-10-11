using DeadworksManaged.Api;

namespace RouletteShop;

public class RouletteShopPlugin : DeadworksPluginBase
{
    public override string Name => "Roulette Shop";

    private ItemPool _pool = null!;

    public override void OnLoad(bool isReload)
    {
        _pool = ItemPool.Load();
        Console.WriteLine(isReload ? "Roulette Shop reloaded!" : "Roulette Shop loaded!");
        Console.WriteLine($"[Roulette] Item pool: {_pool.Count} items ({_pool.Summary()})");
    }

    [Command("hello", Description = "Say hello")]
    public void CmdHello(Caller caller)
    {
        caller.Reply($"Hello, {caller.Name}!");
    }

    [Command("roulette", Description = "Spin for a random item: roulette <weapon|vitality|spirit> <800|1600|3200|6400>")]
    public void CmdRoulette(Caller caller, ItemCategory category, int price)
    {
        int tier = Array.IndexOf(ItemPool.TierPrices, price) + 1;
        if (tier == 0)
            throw new CommandException("Price must be 800, 1600, 3200 or 6400.");

        var controller = caller.Player ?? throw new CommandException("Spin from in game.");
        var pawn = controller.GetHeroPawn() ?? throw new CommandException("You need a hero to spin.");

        int souls = pawn.GetCurrency(ECurrencyType.EGold);
        if (souls < price)
            throw new CommandException($"Not enough souls: you have {souls}, a spin costs {price}.");

        // AddItem gives duplicates, which the normal shop never does, so only roll items the player doesn't have.
        var items = _pool.Get(category, tier)
            .Where(i => pawn.AbilityComponent.FindAbilityByName(i.ClassName) == null)
            .ToArray();
        if (items.Length == 0)
            throw new CommandException($"You already own every {category} item at {price}.");
        var item = items[Random.Shared.Next(items.Length)];

        var before = Snapshot(controller, pawn);

        // Give first and charge only if that worked, so a failed spin never costs souls.
        if (pawn.AddItem(item.ClassName) == null)
            throw new CommandException($"Couldn't give you {item.Name}. No souls were taken.");

        // The shop's own spend path (counted as souls spent), not a raw SetCurrency write.
        pawn.ModifyCurrency(ECurrencyType.EGold, -price, ECurrencySource.EItemPurchase);
        var after = Snapshot(controller, pawn);

        caller.Reply($"Roulette: you got {item.Name} ({category}, {price}).");
        Report(caller, $"{caller.Name} spun {category} {price} -> {item.ClassName} ({items.Length} unowned in pool)");
        Report(caller, $"before: {before} | after: {after}");

        // Net worth may only update on a later tick, so look again once things have settled.
        Timer.Once(1.Seconds(), () =>
        {
            if (caller.Player is { } c && c.GetHeroPawn() is { } p)
                Report(caller, $"1s later: {Snapshot(c, p)}");
        });
    }

    // --- The normal shop is removed: it can't be opened, bought from or sold to. Spinning is the only way to get items. ---
    // Command names from the server log of a real open / buy / sell (Checkpoint 3).

    private const string SpinHint = "Spin instead: !roulette <weapon|vitality|spirit> <800|1600|3200|6400>";

    private readonly Dictionary<int, bool> _inShop = new();

    public override HookResult OnClientConCommand(ClientConCommandEvent e)
    {
        // Temporary (Checkpoint 3): log every command, to spot any other way into the shop.
        Console.WriteLine($"[Roulette] client command from {e.Controller?.PlayerName}: '{e.Command}' args [{string.Join(", ", e.Args)}]");

        var message = e.Command switch
        {
            "open_item_shop" => "The shop is closed. " + SpinHint,
            "buyitem" => "Buying is disabled. " + SpinHint,
            "sellitem" => "Selling is disabled while the roulette is on.",
            _ => null,
        };
        if (message == null)
            return HookResult.Continue;

        Console.WriteLine($"[Roulette] blocked '{e.Command}' from {e.Controller?.PlayerName}");
        if (e.Controller is { } controller)
        {
            Chat.PrintToChat(controller, message);
            if (e.Command == "open_item_shop")
                CloseShop(controller.Slot);
        }
        return HookResult.Stop;
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

    // In case the shop screen opens anyway, close it as soon as the player's input says it's open.
    public override void OnProcessUsercmds(ProcessUsercmdsEvent e)
    {
        foreach (var cmd in e.Usercmds)
        {
            if (_inShop.GetValueOrDefault(e.PlayerSlot) == cmd.InShop)
                continue;
            _inShop[e.PlayerSlot] = cmd.InShop;
            Console.WriteLine($"[Roulette] slot {e.PlayerSlot} in_shop -> {cmd.InShop}");
            if (cmd.InShop)
                CloseShop(e.PlayerSlot);
        }
    }

    private static void CloseShop(int slot)
        => NetMessages.Send(new CCitadelUserMsg_ForceShopClosed(), RecipientFilter.Single(slot));

    private static string Snapshot(CCitadelPlayerController controller, CCitadelPlayerPawn pawn)
        => $"souls {pawn.GetCurrency(ECurrencyType.EGold)}, net worth {controller.PlayerDataGlobal.GoldNetWorth}, level {pawn.Level}";

    /// <summary>Checkpoint 2 evidence: to the player's chat and the server console.</summary>
    private static void Report(Caller caller, string message)
    {
        caller.Reply(message);
        Console.WriteLine($"[Roulette] {message}");
    }
}
