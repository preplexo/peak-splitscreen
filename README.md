# PEAK Split Screen

A mod for [PEAK](https://store.steampowered.com/app/3527290/PEAK/) that lets two people play on one PC, in one window, with the screen split in two.

- **Player 1** plays with keyboard and mouse.
- **Player 2** plays with a gamepad.

This is an unofficial, personal-use mod. It is not affiliated with or endorsed by the makers of PEAK. You need your own copy of the game on Steam.

## Install

1. Close PEAK if it is running.
2. Download this folder (green **Code** button > **Download ZIP**, then unzip it).
3. Double-click **`install.bat`**.

The installer finds your PEAK folder, installs the [BepInEx 5](https://github.com/BepInEx/BepInEx) mod loader if you do not have it yet (downloaded from its official GitHub release), and copies the mod in. Running it again is safe and is also how you update.

If the installer cannot find the game, it asks for the folder. In Steam: right-click PEAK > Manage > Browse local files, and copy that path.

Windows may warn about running a downloaded `.bat` file. Choose **More info > Run anyway**, or read the file first: it is short and plain text.

## Play

1. Plug in a gamepad before starting the game.
2. Start PEAK from Steam.
3. On the title menu, pick **Play Split-Screen** (the new sign with two hikers, under Play Offline).
4. Player 2 appears next to player 1 and the screen splits. Player 2 uses the game's normal gamepad controls.

To play alone, pick **Play Offline** as usual: that is always a normal one-player game. The game remembers your last pick, so continuing a saved offline run uses the same number of players.

Split screen only works in offline games. Online games are left untouched.

### Player 2's passport

Player 2 gets their own passport in the airport lobby, a few seconds after arriving. Use it like any held item to open it on player 2's half and change their look:

| Gamepad | Does |
| --- | --- |
| D-pad / left stick | Move between options |
| A | Pick the highlighted option |
| LB / RB | Previous / next tab |
| LT / RT | Previous / next page |
| B or Start | Close |

Player 2's look is saved separately from player 1's and comes back the next time you play.

## Settings

After the first launch with the mod, a settings file appears at:

```
<PEAK folder>\BepInEx\config\local.peak.splitscreen.cfg
```

Open it in Notepad. The options under `[General]`:

| Option | Default | What it does |
| --- | --- | --- |
| `Enabled` | `true` | Turn the mod on or off without uninstalling. |
| `SideBySide` | `true` | `true` splits left / right. `false` splits top / bottom. |
| `Player2Hud` | `true` | Show player 2's own stamina bar, inventory and prompts on their half. |
| `Player2Sound` | `true` | Both players hear what is around them. `false` goes back to hearing everything from player 1's position. |
| `TwoPlayers` | `true` | Your last title-menu pick. Play Split-Screen sets it to `true`, Play Offline to `false`. No need to edit it by hand. |

`[Player2]` has one option, `Cosmetics`: player 2's saved look, written by their passport. Clear it to get a random look again.

Leave the `[Debug]` options set to `false`. They exist for testing the mod.

## What works

- Both players move, climb, pick up, hold, use and eat items independently.
- Each half has its own camera and its own HUD.
- The emote wheel and backpack wheel work with player 2's stick.
- Injuries, passing out and dying apply to the right player.
- A player who has passed out or died spectates the other one.
- Player 2 has their own passport and their own saved look.
- Each half shows the weather where that player is: wind and storm effects appear only for the player standing in them.
- Both players hear the sounds around them: each sound plays as loud as it is for the nearer player, and comes from the side it is on for whoever is hearing it. There is one shared soundtrack.

## Known limits

- Tested in the airport lobby and for the first few minutes of a run on the beach. The rest of the mountain (fog, wind, lava, campfires, traps, the end of the run) has had little testing.
- Music and background ambience follow where player 1 is. Sound effects near player 2 have no pitch shift from movement.
- Per-player weather has only been tested in a wind storm. The rising fog and the fog-clearing fields use the same code path but have not been seen in a test run yet.
- A few weather details still follow player 1: the sky darkening during a storm, lava heat haze, and the clear bubble the fog leaves around a player.
- Player 2's passport offers what player 1's account has unlocked. Locked hats, sashes and other options stay locked for both players.
- A game update can break the mod until it is rebuilt.

## Uninstall

Double-click **`uninstall.bat`**. It removes the mod and its settings file. BepInEx is left in place because other mods may use it; the uninstaller tells you which files to delete if you want it gone too.

## Build from source

You only need this if you want to change the mod.

Requirements: the [.NET SDK](https://dotnet.microsoft.com/download) (8 or newer), PEAK installed, and BepInEx 5 already in the game folder (run `install.bat` once).

```
cd src
dotnet build -c Release
```

The build reads the game's own DLLs from the game folder and copies the finished `PeakSplitScreen.dll` into `BepInEx\plugins\PeakSplitScreen`. If PEAK is not in the default Steam location, pass the folder:

```
dotnet build -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\PEAK"
```

To refresh the installer's copy, copy `src\bin\Release\PeakSplitScreen.dll` to `dist\`.

### Source layout

| File | Purpose |
| --- | --- |
| `Plugin.cs` | Entry point and settings. |
| `MenuOption.cs` | Adds the Play Split-Screen sign to the title menu. |
| `SplitManager.cs` | Spawns player 2, owns the second camera and the gamepad. |
| `SplitContext.cs` | Swaps the game's "local player" state so its code can run for player 2. |
| `Patches.cs` | Harmony patches that route game code to the right player. |
| `SplitExtras.cs` | Smaller fixes around items and interaction. |
| `P2Hud.cs` | Player 2's HUD, name tags and per-player screen effects. |
| `P2Passport.cs` | Player 2's passport, gamepad navigation and saved look. |
| `P2Audio.cs` | Mixes positional sound for both players through the one listener. |
| `P2Fog.cs` | Shows fog, wind and storm visuals per player. |
| `Hazards.cs` | Makes hazards affect player 2. |
| `DebugTools.cs` | Scripted test pad used by the `[Debug]` options. |

No game code or assets are included in this repository.
