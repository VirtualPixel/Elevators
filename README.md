# Upstairs

Every level gets another floor. Same rooms the level already uses, same doors, same loot spots, stacked on top of the ground floor with a lift between them. Optional basement too.

**Host only. Nobody else in the lobby needs it.** The new floor is built through the same calls the game itself uses to place rooms, so every client, modded or not, sees the whole thing.

## The lift

Once the level loads the host gets a popup saying where the lift is. Stand on it and you get tumbled up to the floor above, the same way the game pulls you out of the void. The pad on the new floor takes you back down. With a basement on, crouch on the pad to go down instead.

Where the lift lands:

- Museum: at the doors of the broken elevator.
- Manor: at the top of the long stairs.
- Arctic: the top of the stair corridor.
- Wizard: up in the upstairs classroom.
- Anything else (modded levels included): a plain room, and the popup tells you how many rooms in from the truck it is.

The game does not always roll those rooms, and when it does not you get the plain room fallback. The popup is honest about which one you got.

## What changes, what does not

- Rooms on the new floor come from the level's normal, passage and dead end lists. Nothing custom, nothing that looks out of place.
- Extraction points stay on the ground floor. The haul loop is untouched.
- The game already spreads its valuables over every floor it finds, so the ground floor gets a bit less and the new floor gets the rest. On top of that, `ExtraLoot` adds more upstairs. The quota grows with them the way it does with any valuable.
- Monsters spawn and roam on the ground floor only, unless you turn `EnemiesOnNewFloors` on. A ride up is a real escape.
- The gap between floors is measured per level from the tallest and deepest rooms in play, so a catwalk never pokes through the floor above. If you somehow end up between floors, the lift catches you after a second and puts you back on the ground pad.

## Settings

`BepInEx/config/Vippy.Upstairs.cfg`, or in-game if you run REPOConfig.

| Setting | Default | What it does |
|---|---|---|
| Enabled | `true` | The whole thing |
| UpperFloor | `true` | Build the floor above |
| Basement | `false` | Build a floor below as well. Crouch on the lift to reach it |
| FloorSize | `60` | Rooms on each new floor as a percent of the ground floor's rooms |
| ExtraLoot | `40` | Extra valuables on the new floors, percent of the level's normal count |
| EnemiesOnNewFloors | `false` | Let monsters up there |
| AvoidRooms | `Bottomless,Outside` | Rooms with these words in their name never get picked for a new floor |
| LiftCooldown | `4` | Seconds before the same player can ride again |
| Announce | `true` | The popup on load. Host only, since only the host runs the mod |
| LogLevel | `Off` | Debug logs the plan, the pads and every ride |

## Known rough edges

- The map draws every room's outline on one layer, so once you have explored both floors the outlines overlap. The walls and doors are drawn on the right layer.
- The lift is a spot, not a shaft. Players without the mod see no marker, so tell them where it is. The host's popup has the wording.
- Levels from other mods work as long as they use the game's normal room lists.

## Vippy

I make R.E.P.O. mods and I read every bug report. Test builds land in [Vippy's Discord](https://discord.gg/kKqhck2NrP) before Thunderstore. The rest of my mods are [here](https://thunderstore.io/c/repo/p/Vippy/).

Everything I make is free. If you want to help: [Ko-fi](https://ko-fi.com/vippydev), or code `vippy` for 25% off a [BisectHosting](https://bisecthosting.com/vippy) server.
