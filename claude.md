\# Deadlock Roulette Shop (Deadworks mod)



\## Goal

Turn the Curiosity Shop into a roulette machine, for a server the user hosts.

\- Player picks a \*\*category\*\* (Weapon / Vitality / Spirit) and a \*\*price\*\* (800 / 1,600 / 3,200 / 6,400 souls), then spins.

\- The server rolls a random item of that category and price, deducts the souls, and gives the item.

\- 1% of spins come back \*\*corrupted\*\* (Deadlock's Corrupted item variant).

\- \*\*Selling is disabled\*\* while the mod is on (otherwise spins can be sold back for profit).

\- \*\*The normal shop is removed.\*\* The user, after Checkpoint 2: "i want the shop to be re-designed by the roulette ui altogether, the shop should not exist anymore".

\- The panel shows the \*\*list of items\*\* in the current category and price, and highlights the one you land on.



\## How the user wants to work

\- Verifiable checkpoint at each step: build it, run it, show the evidence it works, then move on.

\- Direct, specific corrections are normal; validate each change instead of assuming it worked.

\- Don't guess APIs. If something below is marked UNVERIFIED, test it before building on it.



\## Architecture

Two parts:

1\. \*\*Deadworks plugin (C#, .NET)\*\*: item pool, roll, soul check and deduction, giving items, corrupted roll, sell blocking, and the server side of the UI.

2\. \*\*Panorama addon (VPK)\*\*: the roulette panel layout (XML/CSS/JS). Players need the Deadworks launcher and the addon mounted. The server decides the result; the client only animates it.



Deadlock's in-game UI is Valve Panorama. Browser web components (such as @deadlock-api/ui-core's `dl-shop-panel`) cannot run in-game. They are only useful for browser mockups and as a visual reference.



\## Verified API facts (from docs.deadworks.net)

\- Plugin: class inherits `DeadworksPluginBase`, has `Name`, `OnLoad(bool isReload)`. Commands: `\[Command("name", Description = "...")] public void Cmd(Caller caller)`. Throw `CommandException` to show an error to the player. Copy the built `.dll` into the server's `plugins` folder; it hot-reloads.

\- Player: `caller.Player?.GetHeroPawn()` returns the pawn (null if no hero). `Players.GetAll()`, `Players.FromSlot(slot)`.

\- Items: `pawn.AddItem("upgrade\_xxx")` (returns null if it can't give the item), `pawn.AddItem(name, enhanced: true)`, `pawn.RemoveItem(name)` (no refund), `pawn.SellItem(name)` (refunds souls). Active items that attach to an ability: `pawn.TryAddItem(name, EAbilitySlot.Signature1, out var item)`.

\- Check/list items: `pawn.AbilityComponent.FindAbilityByName(name)`, `pawn.AbilityComponent.Abilities` (`ability.IsItem`, `ability.AbilityName`).

\- Souls: `pawn.GetCurrency(ECurrencyType.EGold)` and `pawn.SetCurrency(ECurrencyType.EGold, value)`. Net worth is separate: `controller.PlayerDataGlobal.GoldNetWorth`.

\- Events: `\[GameEventHandler("ability\_added")]` fires when a player gets an ability or item (`args.Userid`, `args.Ability?.SubclassVData?.Name`). `player\_used\_ability` also exists. `OnAbilityAttempt` can block ability use.

\- UI (namespace `DeadworksManaged.Api.UI`): `UI.Panel("id").LoadXml(controller.Recipients, "file://{resources}/layout/x.xml")`, `.Set(recipients, "key", value)`, `.On("event", e => ...)` (`e.Caller`, `e.ArgAt(0)`), `.RequestCursor(recipients)` / `.ReleaseCursor(...)` (the player can't move or aim while the cursor is free), `.DestroyLayout(...)`, `.Reload(...)`, `.SetStyle`, `.Animate`, `.AppendChild/RemoveChild`. Handle `UI.AddonStatusChanged` and `UI.ClientResync`.

\- Addon layout: VPK root has `panorama/layout/\*.xml` and `panorama/scripts/\*.js`; the XML must include `<include src="s2r://panorama/scripts/dw\_addon.js" />`; JS uses `DW.registerPanel({ init(panel){ panel.onClick("id", () => panel.send("event")) }, render(panel){ panel.text("id", ...) } })`.

\- Docs to read before building: setup (docs.deadworks.net/getting-started/developers/setup), first-plugin, features/players, abilities, game-events, ui, guides/uploading-content, and the example plugins on github.com/Deadworks-net/deadworks.



\## UNVERIFIED (test each one before relying on it)

1\. Does `SetCurrency(EGold, ...)` change only the spendable balance? Check that net worth and level do not drop after a spin. Does `EGold` cover both secured and unsecured souls?
   \*\*Answered (Checkpoint 2):\*\* don't use `SetCurrency`; it writes `m\_nCurrencies` directly and skips the game's bookkeeping. `pawn.ModifyCurrency(ECurrencyType.EGold, -price, ECurrencySource.EItemPurchase)`, the shop's own spend path, took exactly the price in game with net worth and level unchanged (souls 916 -> 116, net worth 1716, level 4, the same 1s later). Secured vs unsecured is still open.

2\. Is there any hook or override to block selling? None is documented. Look in the Deadworks GitHub repo for sell/purchase events. Fallback: poll each player's `Abilities` and detect a roulette item disappearing.
   Leads from the Deadworks source: `OnClientConCommand` can block a player's console command by name (the example plugins block `selecthero`); `OnModifyCurrency` reports `EItemSale` refunds and can stop them; `OnProcessUsercmds` exposes the usercmd `in\_shop` flag; the server can send `CCitadelUserMsg\_ForceShopClosed`. The plugin logs all three while the normal shop is used, to find the real buy/sell commands.

3\. Can a plugin grant a \*\*Corrupted\*\* item? Check the Deadworks abilities database (deadworks.net/db/abilities) and the item data for corrupted class names. The game's Broker trades high-tier items for Corrupted versions, each with a random drawback per match.

4\. Which game image path serves item icons to a Panorama panel, so the mod can use the game's own art without bundling copies. (Hero portraits come from game data, e.g. `Heroes.X.GetHeroData()?.IconImageSmall`.)

5\. Does `AddItem` refuse when inventory slots are full? What happens with active items (max 4)?
   Known: `AddItem` gives a duplicate of an item the player already owns (Checkpoint 2), so the roll skips owned items.

6\. Does the cost/tier in the item data match what `AddItem` accepts (some items have component discounts when bought normally; roulette items are charged the flat tier price)?



\## Item data and art

\- Run `node fetch-deadlock-assets.mjs deadlock-assets` (Node 18+). It saves `items-full.json` (from `https://api.deadlock-api.com/v1/assets/items?language=english`), the shop item icons, the shop UI art (from `https://assets-bucket.deadlock-api.com/assets-api-res/...`) and a `manifest.json`. It has not been run yet, so check its output; if a file fails, report which.

\- Item fields in the data: `class\_name` (the internal name, starts with `upgrade\_`), `name`, `item\_slot\_type` (weapon / vitality / spirit), `item\_tier` (1-4 for 800 / 1,600 / 3,200 / 6,400), `cost`, `shopable`, `disabled`, `image`, `image\_webp`, `shop\_image`, `shop\_image\_small`, `shop\_image\_webp`.

\- Build the plugin's item pool from this data, not by hand. Exclude Street Brawl "Legendary" items and anything not shopable.

\- In-game images must be compiled into the addon VPK (Source 2 tools / the community guide "Compiling UI Textures").

\- The user has OK'd using these assets in the mod.



\## Panel layout (from the approved mockup)

A 1280x760 screen. Header: "ROULETTE SHOP" and the player's soul balance. Left column: category tabs (Weapon orange `#f0993a`, Vitality green `#7ccb64`, Spirit purple `#b384f0`) and a 2x2 grid of price buttons. Center: a 5-card reel with a pointer (the middle card is the one that lands), a result card ("YOU GOT", item name, category and price, a red CORRUPTED line when it rolls), and a big SPIN button that reads "NOT ENOUGH SOULS" when the player can't afford it. Right column: the scrollable item pool for the current category and price, with the landed item highlighted. Footer notes: selling disabled, 1% corrupted. Dark background `#0d0f12`, amber accent `#e9b44c`. A clickable version lives at https://claude.ai/artifact/HCPzgg4EaPievmvRJBWaLF (private to the user's account).



\## Checkpoints (do them in order; show evidence for each)

0\. Run the asset script; confirm the item count and a few icons exist.

1\. Scaffold a plugin from the Deadworks setup docs; build it; load it on a local server; a `/hello` command replies. \*\*Done\*\* (plugin in `RouletteShop/`).

2\. Item pool generated from `items-full.json`; `!roulette weapon 800` picks a random 800-soul weapon item, checks and deducts souls, and calls `AddItem`; print balance before and after, and confirm net worth/level did not drop (UNVERIFIED #1). \*\*Done.\*\*

3\. Sell blocking (UNVERIFIED #2): find a real hook or implement the polling fallback; prove a roulette item cannot be sold for souls. With the normal shop removed, also block buying from it.

4\. Corrupted (UNVERIFIED #3): implement the 1% roll behind a config flag, with a debug setting that forces it so it can be tested.

5\. Panorama addon: a minimal panel that opens and sends a click back to the server; then the full layout; item pool list; spin animation with the server-chosen result.

6\. Icons: compile into the VPK; handle `AddonStatusChanged` for players without the addon (fall back to a plain panel or chat command).

7\. Test on the user's server with two players.



\## Rules

\- Build only what the current checkpoint needs. Don't skip ahead.

\- Keep roll logic and soul accounting server-side; never trust the client for the result.

\- If an UNVERIFIED item turns out to be impossible, say so and propose the closest alternative instead of working around it silently.

