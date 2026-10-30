# Elevators

Every level gets an upstairs and a basement, built out of that level's own rooms, and a real elevator between them: the Museum's car, buttons you grab, a shaft you can hear from any floor, and monsters that use it too.

**Everyone installs it.** The host runs the floors, and the elevator, its buttons and its sounds need the mod on your machine. Someone in the lobby without it still gets moved between floors, but they never see the car.

> **A note from Vippy**
>
> After way too long of a break, I'm finally back and working on these again :D Every one of my mods just got a full pass: bugs fixed, reports read, a few things I'd always meant to do. New builds and fixes land in [Vippy's Discord](https://discord.gg/kKqhck2NrP) before they hit Thunderstore, so come hang out. Thanks for sticking around.

## Why

Late levels in R.E.P.O. sprawl. By level ten you're walking a long way out from the truck through rooms that all look the same, and the map is a scroll. Elevators holds the ground floor to a sane size and sends the rest of the building up and down instead. Same rooms, same loot spots, same doors, stacked. You spend the level moving through a building rather than across one.

And then there's the elevator. I wanted the real thing, not a teleport: doors that close on you, a car that hums and thuds, a call button you hear pressed on a floor you're not on.

## The elevator

It's the game's own elevator car, the one stuck in the Museum, given its job back. Press the call button and the doors open, or the car comes down the shaft to you. Step in, grab UP or DOWN, and the doors shut and the car carries you, you and whatever you dragged in with you. It lands with a thud and a shudder, waits a beat, and opens. Press the same button twice and it rides straight through to the floor after.

- The buttons are the shop's pressable button, grab area and all, on the Museum's own wall plate. A press the car can't honor, UP on the top floor say, gets a refusal.
- An OPEN button inside, a call button at every floor, and a roof hatch you grab and swing.
- Doors open by button and on arrival, close on their own, and hold for anyone (or anything) standing in them.
- Every floor's doorway carries the shaft's sound. You hear the car called, moving, and landing through the doors on any floor.
- Between floors the shaft is the Museum's own shaft walls and pipes with a few service lights. Fall out of the car and there's a real shaft to fall down.

On the Museum it rides the actual shaft, elevator room on every floor, sub floors as side stops. On Manor it's at the top of the long stairs, on McJannek Station the stair corridor, on Swiftbroom the upstairs classroom. Anywhere else, modded levels included, it takes a plain room.

## The monsters

This is the part I spent the longest on, because an elevator that's a safe room is a broken game.

- The car floor is on the navmesh wherever the car stands. A monster chasing you walks in after you, and one inside when the doors shut steps out with you at the other end.
- Sitting in the car with the doors shut doesn't help either. A monster that reaches the doors presses the call button like anyone would, and they open.
- A monster that had you in its sights when the doors closed turns up on your new floor a little later, out of sight and hunting. The Shadow Child does it faster and never bothers with the car.
- Now and then an idle monster walks to a call button, presses it, waits for the doors, and rides to another floor, usually one with someone on it. The more floors, the more often. You hear every bit of that through the doors.
- The Huntsman hears the shaft from anywhere on his floor.
- The elevator is loud. Leaving and landing draw whatever is nearby to the shaft.

All of it configurable, all of it off with one switch if you want the floors without the company.

## The map

Open the map and the wheel steps through the floors, each drawn on its own with its name on the screen. It opens on your floor and follows you between floors. The route to the extraction point, or to the truck once you're done, walks you to the nearest elevator when the goal is on another floor, and every elevator doorway is marked on the map in its own color.

## What changes, what doesn't

- Rooms on the new floors come from the level's own normal, passage and dead end lists.
- The ground floor is held to eight rooms by default. Past that, the rooms a deep level would sprawl across go on the other floors instead, extraction rooms included, so a late level grows up rather than out.
- With two or more extraction points, one stands on a new floor. The count stays the game's own.
- The ground floor keeps its normal loot; the new floors get a modest share on top (30 percent by default). The quota grows with it like any other valuable. This is a bigger building, not a jackpot.
- The gap between floors is measured per level from the tallest and deepest rooms in play, so nothing pokes through. If you somehow end up outside every room, on a roof between floors say, you get put back at the ground elevator after a few seconds.

## Settings

`BepInEx/config/Vippy.Elevators.cfg`, or in-game if you run [REPOConfig](https://thunderstore.io/c/repo/p/REPOModding/REPOConfig/).

| Setting | Default | What it does |
|---|---|---|
| Enabled | `true` | The whole thing |
| UpperFloor | `true` | Build the floor above |
| Basement | `true` | Build a floor below as well |
| GroundRooms | `8` | Most rooms on the ground floor; the rest of a deep level goes on the other floors, 0 for the game's size |
| FloorSize | `60` | Rooms on each new floor as a percent of the ground floor's rooms |
| ExtraLoot | `30` | Valuables on the new floors, percent of what the ground floor gets |
| EnemiesOnNewFloors | `true` | Monsters turn up on the new floors, follow you, and ride the elevator |
| ExtractionOnNewFloors | `true` | One extraction point on a new floor when the level has two or more |
| MonsterPatrolInterval | `90` | Seconds between monsters riding the elevator on their own with one extra floor, halved with two, 0 for never |
| AvoidRooms | `Bottomless,Outside` | Rooms with these words in their name never get picked for a new floor |
| LiftCooldown | `4` | Seconds before the same player can ride again |
| VoidRescue | `true` | Anyone stuck outside every room for a few seconds gets brought back to the ground elevator |
| LogLevel | `Off` | Debug logs the plan, the docks and every ride |

## Known rough edges

- The map's player dot only shows on the floor you are on; scroll to another floor and it goes until you scroll back.
- Levels from other mods work as long as they use the game's normal room lists.
- It's a lot of new ground. If something looks wrong, the Discord is the fastest way to reach me, and a log helps.

## Compatibility

Needs [REPOLib](https://thunderstore.io/c/repo/p/Zehs/REPOLib/) for the networked buttons; it installs with the mod. Works alongside my other mods. Loot and value mods stack on top of the extra loot here, so if a level feels rich, that's where to look first.

## Installation

Install with Gale or r2modman and REPOLib comes along. By hand: drop `Elevators.dll` into `BepInEx/plugins/` next to REPOLib. Everyone in the lobby needs it.

## Come hang out

I'm Vippy. I make R.E.P.O. mods and I read every bug report.

| | |
|---|---|
| **[Vippy's Discord](https://discord.gg/kKqhck2NrP)** | Bug reports, test builds before Thunderstore, and a say in what comes next. Come say hi. |
| **[R.E.P.O. Modding Server](https://discord.gg/9fDzZ9sk95)** | The whole modding scene, not just me. |
| **[More of my mods](https://thunderstore.io/c/repo/p/Vippy/)** | Everything else I've made for R.E.P.O. |
| **[Source on GitHub](https://github.com/VirtualPixel/Elevators)** | The code, if you want to poke at it or send a fix. |

## Keep the mods coming

Everything I make is free and stays free. Two ways to help if you feel like it, neither one expected:

- **[Ko-fi](https://ko-fi.com/vippydev)**: buy me a coffee and your name goes on the supporters list in my Discord. Every coffee buys another evening on the next update.
- **[BisectHosting](https://bisecthosting.com/vippy)**: hosting a server for Minecraft or anything else your crew plays? Code `vippy` takes 25% off, and I get a cut at no cost to you. It's where my own servers live.

[![25% off BisectHosting servers with code vippy](https://www.bisecthosting.com/partners/custom-banners/71eecea6-f5bb-437d-ac56-f6fee4266193.png)](https://bisecthosting.com/vippy)
