# Spleak - Local Splitscreen for PEAK

Couch co-op for PEAK: two players, one PC, one window.

- **Player 1** plays with keyboard and mouse, or with a second gamepad.
- **Player 2** plays with a gamepad.

The game runs once and draws two views. There is no second copy of the game to launch, no second Steam account and no Nucleus Co-op setup.

This is an unofficial mod. It is not affiliated with or endorsed by the makers of PEAK.

AI disclosure: the code was written with an AI coding assistant, directed and play-tested by the author.

## How to play

1. Plug in a gamepad before starting the game.
2. Start PEAK from your mod manager.
3. On the title menu, pick **Play Split-Screen** (the new sign under Play Offline).
4. Player 2 appears next to player 1 and the screen splits.

**Two gamepads:** connect a second one and player 1 can play on it. Player 2 keeps the first gamepad; player 1 switches between keyboard/mouse and their gamepad just by using either. If the pads are the wrong way round, swap controllers.

Pick **Play Offline** any time for a normal solo game. Split screen only works in offline games; online lobbies are left untouched.

## Features

- Each player has their own camera, stamina bar, inventory, prompts and name tags.
- Player 2 uses the game's normal gamepad controls, including the emote and backpack wheels.
- Player 2 gets their own passport in the airport. Their look is saved and comes back next time.
- Both players hear the sounds around them. One shared soundtrack.
- Fog, wind, storms and lava haze show on the half of the player who is in them.
- Injuries, passing out and death apply to the right player. A downed player spectates the other.
- Side by side or top and bottom.

### Player 2's passport

| Gamepad | Does |
| --- | --- |
| D-pad / left stick | Move between options |
| A | Pick the highlighted option |
| LB / RB | Previous / next tab |
| LT / RT | Previous / next page |
| B or Start | Close |

## Settings

Open `orchen17.peak.splitscreen.cfg` in your mod manager's config editor (it appears after the first launch).

| Option | Default | What it does |
| --- | --- | --- |
| `Enabled` | `true` | Turn the mod on or off. |
| `SideBySide` | `true` | `true` splits left / right, `false` top / bottom. |
| `Player2Hud` | `true` | Player 2's own HUD on their half. |
| `TwoGamepads` | `true` | Let player 1 play on a second gamepad. |
| `Player2Sound` | `true` | Sound for both players, or only from player 1's position. |

## Good to know

- Two players only.
- Player 2's passport offers what your account has unlocked.
- Music and background ambience follow player 1.
- This is a first release. The early mountain has had the most testing; later biomes and the ending have had less.
- A game update can break the mod until it is updated.

## Bugs

Report problems on [GitHub](https://github.com/preplexo/peak-splitscreen/issues) or the [Nexus Mods page](https://www.nexusmods.com/peak/mods/247) and attach `BepInEx\LogOutput.log` from the run where it went wrong.
