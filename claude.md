\# Deadlock Roulette Shop (Deadworks mod)



\## Goal

Turn the Curiosity Shop into a roulette machine, for a server the user hosts.

\- Player picks a \*\*category\*\* (Weapon / Vitality / Spirit) and a \*\*price\*\* (800 / 1,600 / 3,200 / 6,400 souls), then spins.

\- The server rolls a random item of that category and price, deducts the souls, and gives the item.

\- 1% of spins come back \*\*corrupted\*\* (Deadlock's Corrupted item variant).

\- Extras the user picked after the design #3 build (each a config switch in `RouletteConfig.cs`): a spin of about 2 s; the game's own UI sounds; a \*\*jackpot\*\* (`JackpotChancePercent`, default 3%: the item comes from the next tier up for the same price); \*\*Corrupted pity\*\* (`CorruptedPitySpins`, default 10: after that many spins on Corrupted-capable items without one, the next one is Corrupted; counted per player, reset on disconnect); a \*\*wildcard box\*\* (`WildcardBox`: any category at the chosen price, `!roulette any 1600`).

\- \*\*Selling is disabled\*\* while the mod is on (otherwise spins can be sold back for profit).

\- \*\*The normal shop is removed.\*\* The user, after Checkpoint 2: "i want the shop to be re-designed by the roulette ui altogether, the shop should not exist anymore". Changed later (the server can't close the game's shop screen): "can we use the 3rd shop design instead? it just adds something to the existing shop area which means we dont have to get rid of it or add a black screen". The game's shop stays on screen, but nothing can be bought from it or sold to it; the roulette sheet sits beside it.

\- The \*\*list of items\*\* in the current category and price is the game's own shop page, next to the sheet. The sheet shows the reel and the item you land on (the server can't highlight items on the game's page).



\## How the user wants to work

\- Verifiable checkpoint at each step: build it, run it, show the evidence it works, then move on.

\- Direct, specific corrections are normal; validate each change instead of assuming it worked.

\- Don't guess APIs. If something below is marked UNVERIFIED, test it before building on it.



\## Architecture

Two parts:

1\. \*\*Deadworks plugin (C#, .NET)\*\*: item pool, roll, soul check and deduction, giving items, corrupted roll, sell blocking, and the server side of the UI.

2\. \*\*Panorama addon (VPK)\*\*: the roulette panel layout (XML/CSS/JS). Players need the Deadworks launcher and the addon mounted. The server decides the result; the client only animates it.
   \*\*Changed (full build):\*\* the panel is built on the server with Deadworks' UI nodes (`RouletteShop/RoulettePanel.cs`), the way the Deadworks TicTacToe example builds its board ("players need nothing installed"): no addon VPK to compile, pack, host or download. Players still need the Deadworks launcher (https://deadworks.net, "Download Client"), which installs the client bootstrap that renders server-built panels. The shop key opens the roulette panel over the game's shop screen, and closing the shop (`exit\_item\_shop`) closes it; `!roulette` with no arguments opens it from chat (with a CLOSE button). Players without the launcher are told to spin from chat.



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
   Leads from the Deadworks source: `OnClientConCommand` can block a player's console command by name (the example plugins block `selecthero`); `OnModifyCurrency` reports `EItemSale` refunds and can stop them; `OnProcessUsercmds` exposes the usercmd `in\_shop` flag; the server can send `CCitadelUserMsg\_ForceShopClosed`. Logged in game (Checkpoint 3): opening the shop sends `open\_item\_shop` and flips `in\_shop` to true; buying sends `buyitem <class\_name>` (then `EItemPurchase` -cost); selling sends `sellitem <class\_name>` (then `EItemSale` +refund, spendOnly). Closing it sends `exit\_item\_shop`. The plugin blocks the three commands and stops any `EItemSale` refund as a backstop; buying is confirmed blocked in game. The server can't remove the shop screen: blocking `open\_item\_shop` doesn't stop the client opening it, and `CCitadelUserMsg\_ForceShopClosed` (sent, no error) doesn't close it. The client still plays the buy sound on click. A second try (ForceShopClosed 250 ms after letting `open\_item\_shop` through) was never confirmed either way, and left the roulette up on its own when the shop closed within its 1.5 s window (the user: "sometimes the ui for the roulette shop will not go away"); it was dropped for design #3, which keeps the game's shop open. Now: `open\_item\_shop` goes through and opens the sheet; the sheet closes on `exit\_item\_shop`, or when the player's usercmds keep `in\_shop` off for 1 s (it flickers off for ~30 ms with the shop open, per the Checkpoint 3 log; logged as `left the shop (in\_shop off for 1000 ms)`); `buyitem <class\_name>` with the sheet open picks that item's category and price on the sheet (still blocked). Lead for the addon: Deadworks' `client-bootstrap` replaces game layouts (`hud\_hideout.xml`, `citadel\_db\_page\_play.xml`), its scripts find game HUD panels with `FindChildTraverse`, and run client commands with `$.DispatchEvent("CitadelConCommand", ...)`, so the roulette addon may be able to hide the game's shop panel and open the roulette panel instead (unverified).

3\. Can a plugin grant a \*\*Corrupted\*\* item? Check the Deadworks abilities database (deadworks.net/db/abilities) and the item data for corrupted class names. The game's Broker trades high-tier items for Corrupted versions, each with a random drawback per match.
   Findings (Checkpoint 4): only tier 3 and 4 items have a Corrupted version (`corrupted\_info` in the item data: 90 of the 156 pool items, every tier 3 and 4 one), so 800 and 1,600 spins can never be Corrupted. The game marks a Corrupted item with `ABILITY\_UPGRADE\_BIT\_CORRUPTED = 128` in `AbilityUpgradeBits\_t` (the item's `m\_nUpgradeInfo`; schema from https://s2v.app/SchemaExplorer, build of Oct 09 2026); the drawback is the modifier `CCitadel\_Modifier\_CorruptedPenalty`. The plugin gives the item with `AddItem`, then sets the bit through `ability.UpgradeBits`, which calls the game's own `SetUpgradeBits`. UNVERIFIED until tested in game: does that apply the Corrupted bonuses and drawback? Related server convars (cheat): `citadel\_item\_purchases\_force\_corrupted`, `citadel\_corrupted\_penalty\_seed`, `citadel\_corrupted\_items\_force\_variance` (variance is off in sandbox/hideout).

4\. Which game image path serves item icons to a Panorama panel, so the mod can use the game's own art without bundling copies. (Hero portraits come from game data, e.g. `Heroes.X.GetHeroData()?.IconImageSmall`.)
   \*\*Answered:\*\* each item's `m\_strShopIconLarge` in the game's `scripts/abilities.vdata` (SteamDatabase/GameTracking-Deadlock), e.g. `file://{images}/items/weapon/basic\_magazine.psd`. It equals the deadlock-api `shop\_image` path with `.psd` for all 156 pool items, so the asset script writes it to the manifest as `game\_icon`. Shop art (card backers, tab icons, soul icon, corrupted frame) uses the `s2r://panorama/images/...vtex` paths the game's own shop stylesheets use.

7\. The roulette panel (Checkpoint 5). Seen in game: it opens on the shop key once the client bootstrap is installed (the user added the bootstrap VPK through Deadlock Mod Manager), but the game's shop still opens with it, and a fully opaque backdrop hid the hero too (rejected by the user). Now (design #3) it is one sheet beside the game's shop, no backdrop. Still to see in game: does the sheet land left of the shop's item page (placed from `citadel\_hud\_hero\_shop.css`: centred with a 1184px right margin, so its right edge is 370 left of the middle); do clicks on the sheet reach it over the shop; does clicking a shop item send `buyitem` even when the player can't afford it or owns it; does the reel slide (CSS transition on `transform`); do the game fonts (Retail Demo, Radiance) and art look like the mockup. Verified here: the server's UI frames go through Deadworks' real UI channel, and Deadworks' real client script (`dw\_bootstrap.js`, v0.5.6 and newest) builds the expected panel from them in a Node run with a stand-in for Panorama (50 checks), and clicks come back as `dw\_ui roulette|...` events.

8\. The Broker (Corrupted item shop) is not blocked yet: its trade command is unknown. The plugin logs shop-like commands, so a Broker trade in game will show its name.

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

The approved design is \*\*design #3, `mockups/catalog.html`\*\*: the game's own shop stays, and a roulette sheet sits beside its item page. (Earlier the user approved the root `mockup.html`, the "sales slip" design, then switched to #3 so the shop needn't be hidden; `mockups/approved.html` is the very first draft.)

The game's shop (`citadel\_hud\_hero\_shop.css`) puts its 1200-wide item page, with the tabs, on the right, and the hero model, souls and recent purchases on the left. The user chose to put the sheet on the left, over the hero area, so it fits every screen shape (on 16:9 there's no room right of the page). The sheet (`RouletteShop/RoulettePanel.cs`, 444x960, the mockup's sheet scaled to the page's height), top to bottom:

\- Masthead "ROULETTE SHOP / ONE SPIN · ONE ITEM · NO REFUNDS" (an X button when opened from chat, where no shop closes it).
\- Category and price pickers. The mockup picks with the game's tabs and price boxes, but the server can't see those, so: clicking an item in the game's shop picks its box, and these buttons do too (the user's choice: "Click item + sheet buttons").
\- A vertical reel of 5 game item cards, the middle one ringed between two pointers (red when it lands Corrupted, gold for a jackpot). On a spin a 20-card strip slides 1.5 s (after 0.5 s for it to reach the client) and stops on the server's pick.
\- Sounds (`Sounds` in the config), the game's own from `soundevents/ui.vsndevts`, played to the spinning player with `controller.PlaySound`: `UI.Notify.ItemPurchase` on SPIN, `UI.ItemDraft.RevolverSpin` while the reel turns, `UI.ItemDraft.Appear.<Weapon|Vitality|Spirit>` on landing (`.Rare` for a jackpot, `.Legendary` for Corrupted), `UI.Settings.Error` when a spin can't happen, `UI.Shop.Mod.Activate[.<category>]` on the sheet's buttons. Unverified in game: that a server-sent sound plays while the shop is open.
\- YOUR SOULS, the result ("YOU GOT", the card, name, category and price, a red CORRUPTED stamp), "RANDOM PICK · 1 OF N" (N = items not owned), the SPIN button in the category's colour with the price ("NOT ENOUGH SOULS" when the player can't afford it), and the fine print (selling disabled; 1% of 3,200 and 6,400 spins come back Corrupted).



\## Checkpoints (do them in order; show evidence for each)

0\. Run the asset script; confirm the item count and a few icons exist.

1\. Scaffold a plugin from the Deadworks setup docs; build it; load it on a local server; a `/hello` command replies. \*\*Done\*\* (plugin in `RouletteShop/`).

2\. Item pool generated from `items-full.json`; `!roulette weapon 800` picks a random 800-soul weapon item, checks and deducts souls, and calls `AddItem`; print balance before and after, and confirm net worth/level did not drop (UNVERIFIED #1). \*\*Done.\*\*

3\. Sell blocking (UNVERIFIED #2): find a real hook or implement the polling fallback; prove a roulette item cannot be sold for souls. With the normal shop removed, also block buying from it. Buying confirmed blocked in game; selling to confirm.

4\. Corrupted (UNVERIFIED #3): implement the 1% roll behind a config flag, with a debug setting that forces it so it can be tested. Implemented; to confirm in game.

5\. Panorama addon: a minimal panel that opens and sends a click back to the server; then the full layout; item pool list; spin animation with the server-chosen result. Built as a server-side panel (see Architecture); to confirm in game (UNVERIFIED #7).

6\. Icons: compile into the VPK; handle `AddonStatusChanged` for players without the addon (fall back to a plain panel or chat command). Done differently: the game's own icon paths need no compiling (UNVERIFIED #4), and players without the launcher (`UI.HasClientBootstrap`) get the chat command.

7\. Test on the user's server with two players.



\## Rules

\- Build only what the current checkpoint needs. Don't skip ahead.

\- Keep roll logic and soul accounting server-side; never trust the client for the result.

\- If an UNVERIFIED item turns out to be impossible, say so and propose the closest alternative instead of working around it silently.

