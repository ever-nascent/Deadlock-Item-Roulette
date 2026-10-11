using DeadworksManaged.Api;

namespace RouletteShop;

public class RouletteShopPlugin : DeadworksPluginBase
{
    public override string Name => "Roulette Shop";

    public override void OnLoad(bool isReload)
    {
        Console.WriteLine(isReload ? "Roulette Shop reloaded!" : "Roulette Shop loaded!");
    }

    [Command("hello", Description = "Say hello")]
    public void CmdHello(Caller caller)
    {
        caller.Reply($"Hello, {caller.Name}!");
    }
}
