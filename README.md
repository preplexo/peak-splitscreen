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
3. Start an **offline** game.
4. Player 2 appears next to player 1 and the screen splits. Player 2 uses the game's normal gamepad controls.

Split screen only works in offline games. Online games are left untouched.

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

Leave the `[Debug]` options set to `false`. They exist for testing the mod.

## What works

- Both players move, climb, pick up, hold, use and eat items independently.
- Each half has its own camera and its own HUD.
- The emote wheel and backpack wheel work with player 2's stick.
- Injuries, passing out and dying apply to the right player.
- A player who has passed out or died spectates the other one.

## Known limits

- Tested mostly in the airport lobby. A full run up the mountain (fog, wind, lava, campfires, traps, the end of the run) has had little testing.
- Sounds are heard from player 1's position only.
- Fog visuals follow player 1.
- Player 2's character cosmetics have not been tested.
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
| `SplitManager.cs` | Spawns player 2, owns the second camera and the gamepad. |
| `SplitContext.cs` | Swaps the game's "local player" state so its code can run for player 2. |
| `Patches.cs` | Harmony patches that route game code to the right player. |
| `SplitExtras.cs` | Smaller fixes around items and interaction. |
| `P2Hud.cs` | Player 2's HUD, name tags and per-player screen effects. |
| `Hazards.cs` | Makes hazards affect player 2. |
| `DebugTools.cs` | Scripted test pad used by the `[Debug]` options. |

No game code or assets are included in this repository.
