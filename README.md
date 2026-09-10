# Upstairs

Every level gets another floor. Same rooms the level already uses, same doors, same loot spots, stacked on top of the ground floor with a lift between them. Optional basement too.

**Everyone installs it.** The host runs the floors; the new rooms are placed through the same calls the game uses for its own, so even someone without the mod sees them. The elevator is the part that needs the mod on your machine: the car, the doors, the buttons, and the sound.

## The elevator

The game's own elevator car, the one from the Museum, with a light inside and two buttons on the wall by the doors.  Walk up and the doors slide open. Step in and grab the up or down button: the doors shut and the car rides the shaft to the next floor, you along with it, and the doors open again up there. Each floor has a call button on a post beside the dock, so you can bring the car to you.

The buttons are the game's own pressable buttons, the kind the shop's reroll sits on, so the grabber sticks to them and the press is the same press. Call opens the doors when the car is already there. Press up twice and it goes two floors. A press the car can't honour, up on the top floor say, gets a refusal. The roof hatch is a small door: grab it and swing it open or shut.

Open the map and the wheel steps through the floors, with a tick and the floor's name written on the screen. It opens on your floor and follows you between floors. Each floor is drawn on its own; nothing from the floor above or below shows through. The map's route to the extraction point, or to the truck once you're done, leads to the nearest elevator when the destination is on another floor.

Everyone in the lobby installs the mod. Someone without it stands where the car docks and the host tumbles them up, the way the game pulls a player out of the void, but they see no car and cannot ride it.

Where the elevator stands:

- Museum: the real thing. The level's own elevator room stands on every floor with its shafts lined up, the stuck car is gone, its doors become landing doors, and the elevator rides the actual shaft. Each of those rooms has a sub floor under it, and it is a side stop: down from that floor takes you there, up brings you back, and it is skipped on the way past.
- Manor: at the top of the long stairs.
- Arctic: the top of the stair corridor.
- Wizard: up in the upstairs classroom.
- Anything else (modded levels included): a plain room. The car is the tell.

The game does not always roll those rooms, and when it does not you get the plain room fallback.

## What changes, what does not

- Rooms on the new floor come from the level's normal, passage and dead end lists. Nothing custom, nothing that looks out of place.
- The ground floor is held to `GroundRooms` rooms (8 by default, a third level's size). Past that, the rooms a deep level would sprawl across go on the other floors instead, extraction rooms included, so a late level grows up rather than a long way out from the truck.
- On a level with two or more extraction points, one of them stands on a new floor. The count is the game's own, the map's route walks you to the elevator for it, and the haul loop is otherwise untouched. `ExtractionOnNewFloors` turns it off.
- The ground floor keeps its normal loot. The new floors get `ExtraLoot` percent of that on top (30 by default), placed the way the game places its own. The quota grows with them the way it does with any valuable.
- Monsters follow. The game moves a monster that has lost you to a fresh spot some way off, and those spots include the floor you rode to, so a ride buys time, not safety. The elevator is loud: leaving and arriving draws anything close to the shaft, and a monster that had you in its sights when the doors shut turns up on your new floor a little while later, out of sight and looking. Monsters also ride it on their own now and then: an idle one near a dock calls the car, stands at the doors as they close, and steps out on another floor, four times in five one with someone on it. More floors, more rides. Every dock carries the shaft's sound, so a car being called, moving, and landing can be heard by the doors on any floor. The Shadow Child skips the elevator and simply appears. Turn `EnemiesOnNewFloors` off to keep them downstairs, `MonsterPatrolInterval` to 0 to stop the rides.
- The gap between floors is measured per level from the tallest and deepest rooms in play, so a catwalk never pokes through the floor above. If you somehow end up outside every room and off the navmesh, on a roof between floors say, the lift catches you after a few seconds and puts you back on the ground pad. FreeFly flyers are left alone.

## Settings

`BepInEx/config/Vippy.Upstairs.cfg`, or in-game if you run REPOConfig.

| Setting | Default | What it does |
|---|---|---|
| Enabled | `true` | The whole thing |
| UpperFloor | `true` | Build the floor above |
| Basement | `false` | Build a floor below as well. Crouch on the lift to reach it |
| GroundRooms | `8` | Most rooms on the ground floor; the rest of a deep level goes on the other floors, 0 for the game's size |
| FloorSize | `60` | Rooms on each new floor as a percent of the ground floor's rooms |
| ExtraLoot | `30` | Valuables on the new floors, percent of what the ground floor gets |
| EnemiesOnNewFloors | `true` | Monsters turn up on the new floors too |
| ExtractionOnNewFloors | `true` | One extraction point on a new floor when the level has two or more |
| MonsterPatrolInterval | `90` | Seconds between monsters riding the elevator on their own with one extra floor, halved with two, 0 for never |
| AvoidRooms | `Bottomless,Outside` | Rooms with these words in their name never get picked for a new floor |
| LiftCooldown | `4` | Seconds before the same player can ride again |
| VoidRescue | `true` | Anyone stuck outside every room for a few seconds gets brought back to the ground elevator |
| LogLevel | `Off` | Debug logs the plan, the pads and every ride |

## Known rough edges

- The map's player dot only shows on the floor you are on; scroll to another floor and it goes until you scroll back.
- Outside the Museum the shaft is a plain dark box built around the path, since the level has nothing between the floors. The car and buttons are the game's own Museum assets, so they look the same on every map.
- Levels from other mods work as long as they use the game's normal room lists.

## Vippy

I make R.E.P.O. mods and I read every bug report. Test builds land in [Vippy's Discord](https://discord.gg/kKqhck2NrP) before Thunderstore. The rest of my mods are [here](https://thunderstore.io/c/repo/p/Vippy/).

Everything I make is free. If you want to help: [Ko-fi](https://ko-fi.com/vippydev), or code `vippy` for 25% off a [BisectHosting](https://bisecthosting.com/vippy) server.
