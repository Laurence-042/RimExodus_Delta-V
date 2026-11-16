# Delta-V performance guide

[简体中文](性能指南.md)

In an ordinary colony view with no ship hulls on screen and no space battle, Delta-V's measured work was about **0.02 ms per frame and 0.03–0.10 ms per game tick**. Spaceflight also adds flight control and hull drawing. Heavy combat between two capital ships at 6× speed caused both choppy frames and slower game time.

| What I'm doing | Delta-V mean per frame | Delta-V mean per game tick | Observed game performance |
| --- | ---: | ---: | --- |
| **Everyday colony play**, 1×, no visible hulls or active space projectiles | 0.02 ms | 0.03–0.10 ms | About 54–60 FPS and 56–60 TPS |
| **Non-combat flight**, 1×, one ship under thrust and no active projectiles | 0.95–1.65 ms | 0.41–0.66 ms | About 60 FPS and 60 TPS |
| **Heavy combat between two capital ships**, 6× requested, up to 152 active projectiles | 9.09–16.18 ms | 3.57–4.54 ms | 13–14 FPS and about 117–127 TPS |

## What you may notice in play

- **Colony play.** The example above had no visible ship hulls and no space combat. Ships already active in space can still add simulation work while you are viewing the colony.
- **Flying, docking and looking at ships.** The number of external hulls in view affects frame time. In the two-ship test, hull drawing still took **3.41 ms per frame** while paused, with the game running at **60 FPS**. Showing a hull for the first time or approaching a site whose interior map needs to generate can cause a brief hitch.
- **Large battles.** The longest frame in the heaviest sample was **141.8 ms**. Projectiles, flight control, damaged hulls and follow-up work from damage can all add load at once. Try lowering the speed to 1× or zooming in to keep fewer hulls on screen. The first reduces the requested simulation work per second. The second mainly reduces drawing work.

## How to read these figures

FPS measures how often the display updates. TPS measures how many simulation ticks the game actually completes per second. The first two numeric columns report main-thread work measured by Delta-V. The game performance column includes the base game, dependencies and other mods. Frame and tick costs are averaged over their respective counts.

The colony data comes from steady ground play in RimWorld **1.6.4871 rev591** and the first window after entering the colony in **1.7.5011 rev24**. The flight data comes from the October 28, 2026, 1.6 development build, with one ship under thrust and other objects still present. The combat data covers two roughly five-second windows from the November 16, 2026, 1.7 development build. AI diagnostics were on and the machine used an **Intel Core i7-12700H and RTX 3060 Laptop GPU**. Comparable samples for ordinary combat at 1× are still pending.

To measure your own save, turn on **Mod Settings → Delta-V → Logging → Performance analysis** and keep the camera and speed fixed for at least three five-second windows. If you report a hitch, include your game and mod versions, hardware, speed, and the `[DeltaV:Performance]` lines around it from `Player.log`. You can leave performance logging off during regular play.
