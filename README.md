# Upstairs

Every level gets another floor. Same rooms the level already uses, same doors, same loot spots, stacked on top of the ground floor with a lift between them. Optional basement too.

**Everyone installs it.** The host runs the floors; the new rooms are placed through the same calls the game uses for its own, so even someone without the mod sees them. The elevator is the part that needs the mod on your machine: the car, the doors, the fade, and the HUD line that points you at it.

## The elevator

The game's own elevator car, the one from the Museum, with a light inside and two buttons on the wall by the doors. A line at the bottom of the HUD points at it with the distance. Walk up and the doors slide open. Step in, look at the up or down button and grab it: the doors shut and the car rides the shaft to the next floor, you along with it, and the doors open again up there. Each floor has a call button on a post beside the dock, so you can bring the car to you.

Anyone in the lobby without the mod still gets moved: they stand where the car docks and the host tumbles them up, the way the game pulls a player out of the void. They do not see the car and cannot ride it.

Where the elevator stands:

- Museum: the real thing. The level's own elevator room stands on every floor with its shafts lined up, the stuck car is gone, its doors become landing doors, and the elevator rides the actual shaft. It also stops at that room's sub floor on the way, so the loot down there stays reachable.
- Manor: at the top of the long stairs.
- Arctic: the top of the stair corridor.
- Wizard: up in the upstairs classroom.
- Anything else (modded levels included): a plain room. The HUD line still points at it, and the car is the tell.

The game does not always roll those rooms, and when it does not you get the plain room fallback.

## What changes, what does not

- Rooms on the new floor come from the level's normal, passage and dead end lists. Nothing custom, nothing that looks out of place.
- Extraction points stay on the ground floor. The haul loop is untouched.
- The ground floor keeps its normal loot. The new floors get `ExtraLoot` percent of that on top, placed the way the game places its own. The quota grows with them the way it does with any valuable.
- Monsters spawn and roam on the ground floor only, unless you turn `EnemiesOnNewFloors` on. A ride up is a real escape.
- The gap between floors is measured per level from the tallest and deepest rooms in play, so a catwalk never pokes through the floor above. If you somehow end up outside every room and off the navmesh, on a roof between floors say, the lift catches you after a few seconds and puts you back on the ground pad. FreeFly flyers are left alone.

## Settings

`BepInEx/config/Vippy.Upstairs.cfg`, or in-game if you run REPOConfig.

| Setting | Default | What it does |
|---|---|---|
| Enabled | `true` | The whole thing |
| UpperFloor | `true` | Build the floor above |
| Basement | `false` | Build a floor below as well. Crouch on the lift to reach it |
| FloorSize | `60` | Rooms on each new floor as a percent of the ground floor's rooms |
| ExtraLoot | `50` | Valuables on the new floors, percent of what the ground floor gets |
| EnemiesOnNewFloors | `false` | Let monsters up there |
| AvoidRooms | `Bottomless,Outside` | Rooms with these words in their name never get picked for a new floor |
| LiftCooldown | `4` | Seconds before the same player can ride again |
| ShowLiftHint | `true` | HUD line pointing you at the nearest elevator |
| VoidRescue | `true` | Anyone stuck outside every room for a few seconds gets brought back to the ground elevator |
| LogLevel | `Off` | Debug logs the plan, the pads and every ride |

## Known rough edges

- The map shows the floor you are on. Late joiners may see both floors' room outlines on one layer for that level.
- Outside the Museum the shaft is a plain dark box built around the path, since the level has nothing between the floors. The car and buttons are the game's own Museum assets, so they look the same on every map.
- The Museum elevator car stays stuck where the game left it. The lift pad is in the corridor at the shaft mouth, not inside the car.
- Levels from other mods work as long as they use the game's normal room lists.

## Vippy

I make R.E.P.O. mods and I read every bug report. Test builds land in [Vippy's Discord](https://discord.gg/kKqhck2NrP) before Thunderstore. The rest of my mods are [here](https://thunderstore.io/c/repo/p/Vippy/).

Everything I make is free. If you want to help: [Ko-fi](https://ko-fi.com/vippydev), or code `vippy` for 25% off a [BisectHosting](https://bisecthosting.com/vippy) server.
