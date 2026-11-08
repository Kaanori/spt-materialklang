# True Acoustics – Echo & Matter (BETA)

*Formerly **Materialklang** (Material Sound). Same mod, same settings – only the name changed: it does much more than material sounds now.*

Sound that knows what a wall is made of. Concrete swallows the highs and lets a dull thump through, a wooden door is a weak barrier, a steel door a strong one, and a gunshot behind you sounds different from one in front of you. Works for **all** sounds in the game, not just gunshots.

**🎧 Hear the difference yourself:** [True Acoustics Demo](https://claude.ai/artifact/7BXLomAnuBnL6ofqfq41RC) – vanilla vs. True Acoustics side by side, plus a sound sandbox where you build walls, doors and windows (or load a real map) and listen live, with or without ray tracing. Headphones recommended.

> **BETA – early version.** True Acoustics has been listened to in the F12 menu and sounds good, but it has **not yet been checked in real raids** (no raid log review, no long play sessions, only one tester). Expect rough edges and values that still need tuning. Feedback is very welcome: please open an issue and, if you can, attach the relevant lines of `BepInEx/LogOutput.log` (they start with `[Diagnostics]` or `[Reverb]`).

## How this mod was made (AI disclosure)

This mod was **programmed by Claude**, an AI assistant made by Anthropic.
**Kaan was the tester**: Kaan has no programming background, played with the mod
on SPT 4.1.6, reported what went wrong, and Claude fixed it. No human programmer
has reviewed the code. It is open source so anyone can check it — if you find a
bug, please open an [issue](https://github.com/Kaanori/spt-true-acoustics/issues).

## Requirements

- SPT 4.1.6 (EFT 0.16.9). BepInEx ships with SPT.
- Designed as an add-on for [Sound Engine Backport](https://github.com/RedTheFoxx/SPT-SEB) (SEB 1.0.3): recommended, but not required – without it only the weapon directivity and the air absorption of gunshots switch off, everything else keeps working. Optional: SAIN 4.5.1 (bot hearing, off by default). The reverb zones use the Meta XR Audio library that ships with the game.

## Installation

1. Download `Materialklang-0.8.4-beta.zip` from the [Releases](https://github.com/Kaanori/spt-true-acoustics/releases) page.
2. Extract it **into your SPT game folder** (the folder that contains `EscapeFromTarkov.exe`).
   The ZIP already has the right layout; the mod ends up in `BepInEx/plugins/`.
   On Windows, make sure "Extract All" does not add an extra subfolder.
3. Restart the game.

## Uninstall

Delete the mod's files from `BepInEx/plugins/` (settings: `BepInEx/config/` for client mods) and restart the game.

## What it does, in plain words

Escape from Tarkov (and SPT) already shoots invisible rays between a sound and your ears to decide how muffled it is, but the game treats every wall the same: a thin plywood board and a thick concrete wall differ only in how many meters the ray travelled through them. True Acoustics looks at what the ray actually hit (concrete, wood, metal, glass, soil, ...) and works out the muffling from that.

It is an add-on for **Sound Engine Backport (SEB)** and is meant to be played together with it, but most of it also works without.

## Features

| Feature | What you hear |
|---|---|
| **Physics Mode** (on by default) | Real building acoustics: the insulation of a wall follows the *mass law* (heavier and thicker = quieter). Thick concrete is nearly silent, a plank is not. Applies to every sound with an obstacle in the way. |
| **Material table** (sections `2.xx`) | The simpler, hand-tunable alternative to Physics Mode: attenuation per meter and base attenuation for 15 material groups. Used when Physics Mode is off. |
| **Doors and windows** | A closed thin wooden door lets more through than a closed steel door; an intact window absorbs some sound, a broken one none; for rows of windows the game itself accounts for how much of the row is broken. |
| **Walls between rooms** | Vanilla only lets sound travel between rooms through doors and windows – the wall in between does not count. True Acoustics also measures the straight line through the walls and uses whichever path is quieter, so a loud fight next door can come through the wall. |
| **Timbre** | Materials filter highs and lows differently (concrete: dull but with a thump, wood: hollow). Only for sound through walls. |
| **Weapon directivity** | Other people's gunshots are quieter and duller when the muzzle points away from you (less so indoors). Needs SEB. |
| **Air absorption** | Highs fade with distance (about 12 kHz at 19 m, falling with distance, never below 2.5 kHz). Since 0.7.0 the curve is calculated with ISO 9613-1 from the raid's temperature and pressure and an estimated humidity; an `Air clarity factor` makes the air duller or clearer. For gunshots (SEB) the part that is already baked into SEB's distant recordings is subtracted. |
| **Forest** | Sound that travels through dense forest loses level and highs, following the foliage table of ISO 9613-2. The trees are counted from the map's real trees at raid start (Woods, Shoreline, Lighthouse, Customs, Reserve ...); no effect in cities and indoors. |
| **Distance** (off by default, needs SEB) | Distant gunshots carry further (deliberately less realistic): flatter volume curve beyond 25 m, audible up to 900 m outdoors, no dip where the game switches to the far recording, suppressed shots no longer go silent beyond about 100 m, flatter explosion curve. Shots up to 25 m sound exactly as before. |
| **Reverb by material** | At raid start the walls of every interior room are measured, and a Meta XR Audio *control zone* per room shifts reverb time and level (a concrete bunker rings longer than a wooden hut). |
| **Bot hearing** (off by default) | With SAIN installed, bots can hear through walls according to the material in between (concrete muffles more than wood). Optional and experimental. |
| **Advanced** | More rays for smoother results, bounce count and energy kept per bounce. |
| **Sound log and live tuning** (off by default) | A developer/tuning tool: one log line per sound near you (path, obstacles with material, thickness and dB, volume, low-pass, reverb) and a JSON file that changes settings while the game runs. See [Sound log and live tuning](#sound-log-and-live-tuning). |

Every part has its own **Enabled** switch in the F12 menu. Switch off everything and you get vanilla sound back.

## Requirements and compatibility

| | |
|---|---|
| **Tested with** | SPT 4.1.6 (EFT `0.16.9.40743`), Sound Engine Backport 1.0.3, SAIN 4.5.1, BepInEx 5; developed and played on Linux (Proton). |
| **Files** | `Materialklang.dll` -> `BepInEx/plugins/Materialklang/` (GUID `kaan.materialklang`) |
| **Settings** | `BepInEx/config/kaan.materialklang.cfg` (F12 menu, English labels) |
| **License** | MIT |

- **Sound Engine Backport (SEB)** – *recommended, not required* (soft dependency). True Acoustics was built and tuned next to SEB 1.0.3. With an SEB version other than 1.0.x the SEB parts stay off (log warning), and if SEB changes something True Acoustics relies on, those parts switch themselves off for the session (log error) while shots keep playing normally. Without SEB, **weapon directivity** and the **air absorption of gunshots** switch off (the log says so); everything else (materials, doors, windows, walls, timbre, air absorption of other sounds, reverb) only touches the base game's own audio system. Running it *without* SEB has not been tested yet.
- **SAIN** – optional, only for **bot hearing**, which is **off** by default. Tested with SAIN 4.5.1; with another version the log shows a warning. Without SAIN that section simply does nothing.
- **Meta XR Audio** – not something you install: it is part of the game (`Meta.XR.Audio.dll`) and is what the game itself uses for room reverb. The reverb zones talk to it directly. If it is unavailable the zones are skipped and the rest keeps working.
- It hooks into internal parts of the game's audio system, so a **game update can break parts of it**. Where a hook target is missing, True Acoustics logs a warning and that part stays vanilla.
- Other mods that change volume, room sound or the mixer can fight with it (SEB's author gives the same warning for his own mod). If something sounds odd, switch True Acoustics's sections off one by one in F12 to find the culprit.

## Settings overview (F12)

Sections and the most important settings with their defaults. All values have tooltips in the menu.

| Section | Key settings (default) |
|---|---|
| **0. Physics Mode (real values)** | `Enabled` (on); `Max. insulation (dB)` (60); `Loudness factor` (0.2); `Reference frequency (Hz)` (250); `Low-pass at full insulation (Hz)` (800); `Clear-path threshold` (0.02); `Coincidence correction` (on); `Coincidence loss factor` (0.1); per material group `Density` (kg/m3) and `Solid up to` (m) |
| **1. General** | `Enabled` (on); `Overall strength` (1.0); `Clear-path threshold` (-1 = game value); `Diagnostic log` (on); `Steady occlusion` (on – keeps the occlusion steady while the game recalculates a path and lets filters glide; off = 0.8.0 behaviour) |
| **2. 01 ... 15** (one per material group) | `Attenuation per meter`, `Base attenuation` – only used while Physics Mode is off |
| **3. Bot Hearing (SAIN)** | `Mode` (**Off** / QuieterOnly / Replace); `Strength` (1.0); `Exponent` (0.5); floors for gunshot / suppressed shot / footsteps / other sounds; `Calculations per frame` (6) |
| **4. Doors and Windows** | `Enabled` (on); `Door thin wood` (0.35); `Door thick wood` (0.45); `Door thin metal` (0.6); `Door thick metal` (0.8); `Window intact` (0.55) |
| **5. Walls Between Rooms** | `Enabled` (on); `Max. distance (m)` (40); `Surcharge` (0.05); `Shell thickness (m)` (0.15 – single surfaces of solid groups; thin groups use their own thickness) |
| **6. Timbre** | `Enabled` (on); `Strength` (1.0) – has no effect while Physics Mode is on |
| **7. Weapon Directivity** | `Enabled` (on); `Rear side (dB)` (-4); `Rear low-pass (Hz)` (6000); `Indoor factor` (0.5) |
| **8. Air Absorption** | `Enabled` (on); `Air clarity factor` (1.0; called `Humidity` before 0.6.0); `Shot offset` (1.0); `Use raid weather (ISO 9613-1)` (on); `Base humidity (%)` (60); `Show weather in log` (on) |
| **9. Reverb by Material** | `Enabled` (on); `Strength` (1.0); `Max zones` (400); `Only largest box per room` (on); `Min. absorption` (0.05); `Diagnostics` (on) |
| **10. Advanced (rays)** | `Transmission rays x` (1); `Reflection rays x` (1); `Max. bounces` (0 = game value); `Energy kept per bounce` (0 = game value) |
| **11. Sound log & live tuning** | `Sound log` (**off**); `Log radius (m)` (60); `Max. lines per source per second` (4); `Max. file size (MB)` (50); `Keep last raids` (10); `Mark odd sound key` (unbound); `Live tuning` (**off**) |
| **12. Forest** | `Enabled` (on); `Strength` (1.0); `Density threshold (trees per 100 m2)` (3); `Show forest in sound log` (on) |
| **13. Distance (SEB)** | `Enabled` (**off**); `Far clip compensation (dB)` (4); `Max. distance outdoors (m)` (900; SEB 600); `Suppressed: keep mid clip` (on); `Explosions: SPT curve` (on) – distant shots louder, shots up to 25 m unchanged; needs SEB 1.0.x |
| **14. Sound Paths (physics)** | Physics Mode only. `Diffraction and reflection in dB` (on); `Edge diffraction: wedge angle (deg)` (90 = house corner, 0 = thin edge); `Octave bands for the low-pass` (on); `Band low-pass strength` (1.0); `Energy sum of all paths` (on); `Reflections for every occluded sound` (on); `Reflections for every occluded sound: max. distance (m)` (50); `Reflection: extra loss per bounce (dB)` (3); `Receiving room` (on); `Receiving room: diffuse share` (0.5); `Receiving room: max. correction (dB)` (6); `Receiving room: max. wall area (m2)` (20); `Flanking transmission` (on) – each part off = the behaviour before it |

Notes:

- **Physics Mode is on by default**, so the material table (`2.xx`) and Timbre are inactive until you switch it off. Prefer the hand-tunable table? Turn Physics Mode off.
- **Too quiet behind walls?** Raise `Loudness factor` (0.1 = barely any muffling ... 1.0 = true-to-life, i.e. near silence behind concrete). The default 0.2 was chosen by ear with headphones.
- Almost every setting now applies **live** (while you drag the slider in F12 or when the live-tuning file changes). Only these need a game restart: `3. Bot Hearing (SAIN) / Mode` and the four `10. Advanced (rays)` settings (the game builds its ray buffers at raid start).

## Sound log and live tuning

Two developer tools for tuning and bug reports, both **off by default** and both in F12 section `11. Sound log & live tuning`. With them off they cost one boolean check.

### Sound log

Switch on `Sound log`, play, and each processed sound within `Log radius (m)` (60) of you becomes one line in `BepInEx/Materialklang-Logs/<date_time>-<map>.log` (a new file per raid; the last `Keep last raids` (10) files are kept; a file stops at `Max. file size (MB)` (50), only marks and `[Live]` lines are still written). At most `Max. lines per source per second` (4) per sound source, so footsteps do not flood the file. Writing happens on a background thread; the game thread only fills a recycled record (no allocation per sound, checked offline: 0 bytes, about 1 µs).

A line looks like this (one line, wrapped here):

```
14:32:05.123 t=412.35 f=24510 | Gunshot(Gunshots) clip=ak74_shot_03 src=SuperSource/BetterSource_12 owner=Bot_Pmc_1 @Bots
 | d=23.4 m | path=through walls [T=0.31 D=0.55 R=0.62 effect=0.62]
 | hits: ConcreteStone 0.28 m 41.2 dB x7 "Wall_A12"; WoodThick 0.04 m 8.1 dB x2 (free rays 3/12)
 | vol=0.870 (-1.2 dB) lp=4210 Hz hp=0 Hz | mode=physics (insulation 37.2 dB x loudness 0.20) timbre dH=-3.2 dL=1.1 dB air=5800 Hz
 | SEB directivity w=0.62 -2.1 dB rear-lp=8900 Hz, air=5800 Hz, final lp=5300 Hz
 | rooms src=Hall_A ear=Kitchen | reverb src[Hall_A +0.31 s, -1.5 dB] ear[Kitchen -0.20 s, +2.4 dB]
```

- `14:32:05.123` real clock, `t` game time (s), `f` frame. Then the kind (own guess from the clip name, game group in brackets), clip, source object and owner.
- `d` distance to you. `path` is the path with the lowest score among transmission (**T**, through the obstacle), diffraction (**D**, around it) and reflection (**R**); scores are 0 = free … 1 = blocked. `direct` = nothing in the way, `via doors/windows (rooms)` = the room-portal route, `through wall between rooms` = the wall measurement beat the portal route (`route=` and `wall=` scores are shown then, with `wall pieces:`).
- `hits`: the pieces the transmission rays went through, strongest first: material group, estimated thickness, insulation of this piece in dB, how many rays (`xN`) and the collider name. In sliders mode the dB are the equivalent `attenuation × 4.34`. `free rays a/b` = rays that did not hit anything.
- `vol` is the final volume factor (and its dB), `lp`/`hp` the final low/high-pass. `mode` says whether Physics Mode or the sliders applied; in Physics Mode the raw insulation and the loudness factor are shown.
- `SEB …` appears for Sound Engine Backport gunshots (directivity and air absorption values); `reverb` shows the control zone (Reverb by Material) around the source and around you, as change of reverb time and level at 1 kHz.
- Settings in effect are written at the top of each file, and every live change is written as a `[Live]` line, so a log is self-explanatory.

**Mark odd sound:** bind `Mark odd sound key` (unbound by default) and press it when something sounds wrong; a `=== MARK n === 14:32:09.480 t=… listener=(x, y, z)` line goes into the log so the moment is easy to find. A short ding confirms the mark (an error sound means the sound log is off).

### Live tuning

Switch on `Live tuning`. The plugin creates `BepInEx/config/kaan.materialklang.live.json` and checks it once per second. It holds overrides with the exact F12 names:

```json
{
  "0. Physics Mode (real values)/Loudness factor": 0.3,
  "5. Walls Between Rooms/Enabled": false
}
```

- On a change, only values that changed since the last read are applied (so your own F12 changes are not overwritten every second). `"reload": true` applies everything again (the plugin removes it afterwards). Keys starting with `_` are comments.
- Each applied value is logged (`[Live] 0. Physics Mode (real values)/Loudness factor: 0.2 -> 0.3`, also into the sound log). BepInEx writes the new value to the cfg file. Values outside the allowed range are limited and the log says so. Unknown names and wrong types are reported once and ignored.
- After every change (from F12 or from the file) door/window values and wall measurements are rebuilt and the game recalculates all sound occlusion, so you hear the change within a moment.
- At game start every value in the file is applied once (that is also the only time the restart-only settings below can be set this way).
- **Need a restart (ignored while running, with a log warning):** `3. Bot Hearing (SAIN)/Mode`, `10. Advanced (rays)/Transmission rays x`, `Reflection rays x`, `Max. bounces`, `Energy kept per bounce`. **Everything else is live.**
- Helper for scripts: `materialklang-live.sh "<Section/Key>" <value>` edits the file atomically (see the script header; `--list`, `--remove`, `--clear`, `--reload`).

## Known limits (beta)

- **Not tested in raids yet.** It has been judged in the F12 menu and on a short listening test, not over long sessions, and not on all maps. The values (especially `Loudness factor`, door and window values, reverb strength) are my taste and may not be yours.
- **Performance.** The extra work is small but not zero: the wall measurement between rooms starts at most 8 ray batches per frame, reverb zones are measured once at raid start (a short pause, logged as milliseconds), bot hearing is capped at 6 calculations per frame. If you see frame drops, switch off `5. Walls Between Rooms`, `9. Reverb by Material` or leave `3. Bot Hearing` off first. The diagnostic log writes a few lines every 30 s; switch it off with `Diagnostic log` if you do not need it.
- **Bot hearing is experimental** and off by default. Switching it on changes how far bots hear you, so it changes difficulty.
- **Forest.** Based on the standard's table, which assumes foliage dense enough to block the view; pine trunks are bare at ear height, so real pine forest may attenuate less than the table (lower `Strength` if forests sound too dull). Only the horizontal line between source and listener is checked, in a 10 m grid, up to 400 m. The scan at raid start calls Unity's `FindObjectsOfTypeAll` once (a short pause of tens of milliseconds, logged), the counting is spread over several frames. Not yet checked in a real raid.
- **Maps.** The reverb measurement relies on the game's interior rooms (about 400 zones at most per raid); open or unusual rooms may be skipped.
- **No material data for some surfaces.** Anything the game does not tag with a material counts as "Unknown" and behaves like vanilla.
- **Sounds that get a new occlusion container** (for example some environment and one-shot sounds) start before the game's asynchronous ray calculation has a result, so their first moment can be less muffled than the rest; since 0.8.1 the correction glides instead of jumping. Bot footsteps, gear and voices use a container per bot and are not affected.
- **Volume steps at indoor/outdoor changes.** When you or a bot step between indoors and outdoors, the game switches its indoor/outdoor factor at once (e.g. a door path score of 0.34 becomes 0.68), so the volume of that sound can change by about 10 dB within one update. This is the game's behaviour (in Physics Mode more audible than in vanilla); True Acoustics does not smooth it yet.
- **Game updates** can break hooks (see above).

## Physics and sources

What True Acoustics calculates, what each part is based on, and where a value is simply game data or chosen by ear. Numbers in square brackets point to the reference list at the end of this section.

### 1. Wall insulation: the mass law (Physics Mode)

A wall stops sound mainly by being heavy: the sound has to shake it, and a heavier wall moves less. The textbook rule for this is the *mass law*. For sound arriving straight on (normal incidence) it is `TL ≈ 20·log10(m·f) − 42 dB`; for sound arriving from all directions, as in a real room (*field incidence*), it is about 5 dB lower [1, 2]. True Acoustics uses the field-incidence form:

```
TL = 20 · log10(m · f) − 47 dB        m = density × thickness (kg/m²), f = 250 Hz
TL is clamped to 0 ... 60 dB ("Max. insulation")
```

- **TL** (transmission loss) = how many decibels the wall takes away. The rule rises by 6 dB per doubling of mass or frequency.
- **250 Hz** is used because that is the range you mainly hear through walls (adjustable, 63–1000 Hz).
- **The 60 dB cap** stands for *flanking*: in a real building sound also leaks around a wall (ceilings, floors, pipes), so the insulation you actually get between two rooms stays well below what the wall alone would allow [3]. The value 60 dB itself is an assumption, not a measured value.
- **Hollow objects:** if the game reports a piece thicker than the material's `Solid up to` value (e.g. a car or a container measured from one side to the other), it counts as two shells of that thickness. This is a simplification (a real double wall behaves differently).
- **Coincidence:** the plain mass law only holds below the *coincidence frequency* `fc` of a panel (where the wall starts to bend along with the sound). Above it, real walls insulate less than the mass law says [1, 2]. Since 0.6.0 this is corrected for **thick, heavy groups** (concrete/stone, thick metal, thick wood); see "Coincidence correction" below. Resonances and stiffness of whole building elements are still not modelled.

Examples with the default densities (before the loudness factor):

| Piece | Surface mass | TL at 250 Hz | What you hear with `Loudness factor` 0.2 |
|---|---|---|---|
| Thin wood, 2 cm | 10 kg/m² | 21 dB | −4 dB |
| Thin metal, 3 mm | 24 kg/m² | 28 dB | −6 dB |
| Thick wood, 10 cm | 60 kg/m² | 37 → 27 dB (coincidence correction) | −5 dB |
| Concrete, 10 cm | 230 kg/m² | 48 → 37 dB (coincidence correction) | −7 dB |
| Concrete, 20 cm | 460 kg/m² | 54 → 46 dB (coincidence correction) | −9 dB |
| Concrete, 1 m | 2300 kg/m² | 68 → 67, capped 60 dB | −12 dB |
| Snow, 1 m | 300 kg/m² | 51 dB | −10 dB |

The coincidence correction (0.6.0) is on by default; with `Coincidence correction` off the TL column is the plain mass law (the first number in each arrow).

#### Coincidence correction (thick heavy walls)

Above its coincidence frequency a plate transmits more sound than the mass law predicts. For a homogeneous plate (thickness `h`, density `ρ`, Young's modulus `E`, Poisson ratio `ν`) the textbook relation is [1, 2]

```
fc = c0² / (2π·h) · sqrt( 12·ρ·(1 − ν²) / E )       c0 = 343 m/s     (concrete: fc·h ≈ 19 Hz·m, steel: ≈ 12 Hz·m)
```

The correction has the three-region shape of **Sharp's prediction method** [12] (also used in [13]) and is applied per wall piece, at the reference frequency `f` (250 Hz):

```
f ≤ fc/2   : TL = mass law                                           (nothing changes)
fc/2 < f < fc : straight line (dB over log f) from  mass law(fc/2)  to  TL(fc)
f ≥ fc     : TL = mass law + 10·log10( 2·η·f / (π·fc) )               (η = coincidence loss factor, default 0.1)
TL(fc)     = mass law(fc) + 10·log10(2·η/π)
the result never exceeds the plain mass law
```

- **Thin pieces are not touched.** Only concrete/stone (`E` 25 GPa, `ν` 0.2), thick metal (200 GPa, 0.3) and thick wood (9 GPa, 0.3) have a plate model; thin wood, thin metal, glass, door leaves, soil, gravel, snow, soft materials, bodies and "unknown" keep the pure mass law. Within the modelled groups a piece is unchanged whenever `fc ≥ 2·f`, i.e. at 250 Hz: concrete thinner than about 4 cm, thick wood thinner than about 3 cm, steel thinner than about 2.5 cm (the default steel limit is 1 cm, so steel is never affected by default). Very thick walls (about 1 m of concrete) end up close to the mass law again, because far above `fc` the Sharp slope catches up.
- **Typical effect at 250 Hz:** 5 cm concrete −4 dB, 10 cm −11 dB, 20 cm −8 dB, 30 cm −6 dB, 50 cm −4 dB, 1 m −1 dB (before the 60 dB cap and before the loudness factor).
- **What is verified and what is not.** The bibliographic data of Sharp's paper [12] is checked; I could not retrieve the paper itself, so the construction and the constants follow the secondary descriptions in [13] and in textbook form [1, 2] (the constant is −48 dB in some of them and −47 dB in others; True Acoustics keeps its −47 everywhere). **The loss factor 0.1 is an effective total loss factor (internal damping + edge losses + radiation) fitted so that 100–200 mm concrete shows a dip of roughly 8–11 dB at 250 Hz, as typical measured curves do. It is not a literature value** – raise `Coincidence loss factor` for shallower dips, or switch the correction off.
- The correction is not a replacement for flanking limits: the 60 dB cap stays.

### 2. Default densities

| # | Group | Density (kg/m³) | `Solid up to` (m) | Basis |
|---|---|---|---|---|
| 01 | Concrete/Stone | 2300 | 1.0 | Plain normal-weight concrete; EN 1991-1-1 gives 24 kN/m³ (about 2400–2450 kg/m³) for normal-weight concrete [4] |
| 02 | Soil/Terrain | 1700 | 3.0 | Typical value for soil (roughly 1500–2000) |
| 03 | Gravel/Pebbles | 1600 | 3.0 | Typical value for loose gravel |
| 04 | Metal thick | 7850 | 0.01 | Structural steel, EN 1993-1-1 [5] (EN 1991-1-1: 77–78.5 kN/m³ [4]) |
| 05 | Metal thin | 7850 | 0.003 | Steel, as above |
| 06 | Wood thick | 600 | 0.3 | Typical softwood (roughly 400–700) |
| 07 | Wood thin | 500 | 0.05 | Typical softwood / plywood |
| 08 | Glass | 2500 | 0.02 | Soda-lime glass, EN 572-1 [6] |
| 09 | Soft (cardboard, fabric, plastic, rubber) | 500 | 0.1 | Mixed value for very different materials, chosen by ear |
| 10 | Grate/Fence/Grass | 0 | 0 | Treated as open (no insulation) |
| 11 | Body/Armor | 1000 | 0.5 | About the density of water / body tissue |
| 12 | Water | 1000 | 3.0 | Water |
| 13 | Unknown | 2000 | 0.5 | Assumption (something heavy in between) |
| 14 | Door leaf | 600 | 0.06 | Like thick wood; assumption |
| 15 | Snow | 300 | 3.0 | Own group since 0.6.0 (before: part of gravel at 1600 kg/m³, far too heavy). Fresh snow is roughly 100–300 kg/m³, settled/packed snow 300–500 kg/m³; 300 is the middle of that range [14]. The game's `Snow` material is mapped here, `Gravel` and `Pebbles` stay in group 03. |

Densities 01, 04, 05, 08 are standard values (snow: handbook range [14]); the others are typical handbook-range values or deliberate mixed values, not taken from one specific table. The `Solid up to` thicknesses are assumptions about the game's geometry, not literature values.

### 3. From decibels to loudness and muffling

- **Loudness:** `volume factor = 10^(−dB × Loudness factor / 20)` – the standard conversion from a level difference in dB to a sound-pressure (amplitude) factor [2].
- **Loudness factor 0.2 is chosen by ear, not physics.** A real gunshot is far louder than headphones can play, so true-to-life insulation (factor 1.0) would make anything behind concrete silent. 0.2 compresses the real values into the range of game loudness.
- **Muffling (low-pass):** the mass law rises 6 dB per octave, so a wall removes highs more than lows [1, 2]. The mapping `cut-off = 22 kHz × (800 Hz / 22 kHz)^score` (logarithmic, from open to fully insulated) is my own approximation, not a formula from the literature.
- **Since 0.8.4 (section 14): all paths in decibels, added as energy.** Edge diffraction uses the UTD implementation from Valve's Steam Audio (Apache 2.0), ported to C# (see `THIRD_PARTY_NOTICES.md`): Uniform Theory of Diffraction (Kouyoumjian & Pathak 1974) at the edge point the game finds, with the real distances source–edge–listener; the game knows no faces, so the wedge angle is a setting (90° house corner). Reflections: image source over the game's shortest reflection path, `−20 lg(L/d) + Σ 10 lg(1 − α)` with α of the hit surfaces (same material table as section 9) and an extra loss per bounce (assumption: the game's last leg to you is a visibility test, not a mirror path). Wall insulation per band 250 Hz / 1 kHz / 4 kHz (mass law + coincidence at each frequency), the low-pass is a 2-pole fit to that curve (the game has exactly one low-pass and one high-pass per sound; a 12 dB/octave filter cannot follow the 6 dB/octave mass law at both points, the fit splits the error). Through-wall paths get the receiving room (EN ISO 12354-1: power through the wall into your room, `4P/A` diffuse share weighted, limited ±6 dB) and flanking paths over floor, ceiling and side walls (EN ISO 12354-1 4.4, rigid cross junction values of Annex E). The paths are added as energy (`10 lg Σ 10^(L/10)`, never above 0 dB); free transmission rays and the edge path describe the same sound at an obstacle's edge, so the larger of the two counts. The section below describes the behaviour with section 14 switched off.
- **The lightest path counts:** sound reaches you through the wall, around corners and via reflections at the same time. Parallel paths add up as energy, so the weakest barrier dominates – the same idea as flanking transmission in building acoustics [3]. True Acoustics takes the *smallest* of the three occlusion scores instead of the game's average; using the minimum instead of a full energy sum is a simplification. Known limit: in Physics Mode the transmission score is a dB scale (score = dB / 60), while the diffraction and reflection scores are the game's own heuristics on a 0..1 scale – the minimum compares numbers of different origin. There is no calibration curve for them (no measurements to fit one to).
- **One value per sound:** the game keeps one occlusion value per occluder, but multiplies it with an indoor/outdoor factor that depends on the sound group (character sounds 2.0, weapon sounds 1.5 in the logs). Between full updates the game re-applies the stored volume of every sound but recalculated the low-pass from the value of whichever sound was calculated *last* – the footsteps of a bot got the weapon's value every few frames (in Physics Mode one octave of low-pass back and forth). Since 0.8.3 every sound keeps its own value, so its low-pass and its volume always come from the same calculation.

### 4. Doors and windows

The defaults (`Door thin wood` 0.35, `thick wood` 0.45, `thin metal` 0.6, `thick metal` 0.8, `Window intact` 0.55) are scores on the same scale as Physics Mode (score × 60 dB), i.e. about 21, 27, 36, 48 and 33 dB before the loudness factor. They are **set by hand** for a plausible order (wood < glass < steel), not taken from measurements of particular doors; real doors are often limited by the gaps around them. For a row of windows with broken panes the game itself already uses the size and cost of the broken part for the path; since 0.8.3 True Acoustics no longer adds the remaining glass a second time (the opening dominates anyway, it is a parallel path).

### 5. Walls between rooms and bot hearing

- **Walls between rooms** use the same mass law (section 1) for each wall piece on the straight line, or the material table when Physics Mode is off. Five lines are measured (one centre line, four offset by 0.25 m), each with a ray in both directions (at most 8 hits per ray).
  - **Single surfaces** without a back face (one-sided meshes) get a typical thickness of their group: metal sheet 2 mm, thick metal 10 mm, glass 6 mm, soft (posters, plastic, cardboard) 5 mm, thin wood 12 mm, thick wood and door leaves 40 mm. Solid groups (concrete/stone, soil, gravel, water, snow, unknown) count as one shell of `Shell thickness` (0.15 m) – there the surface is usually one side of a slab or wall (e.g. a road above an underground car park). These thicknesses are assumptions about typical building parts, not measurements. Up to 0.8.2 every single surface counted as 0.15 m, so a poster weighed like 15 cm of wall (about 40 dB). Pieces over 1.5 m count as two shells (assumption about the game's meshes).
  - **Full rays:** a ray with 8 hits has not seen the rest of the line, but the ray from the other end may have. A line is only unknown if both rays are full and their seen ranges leave a gap; then it counts as blocked only if what was seen already reaches the 60 dB cap, otherwise it is left out. Up to 0.8.2 any full ray made the line "blocked" – in a Sandbox log 201 of 230 lines, so the wall never won against the door path.
  - **Offset lines** that start inside geometry (for example under the pavement the source stands on, or behind the wall you lean against) are left out when the centre line has no surface near that end; the remaining lines share their weight. If no line says anything, the game's door path applies.
- **Bot hearing (SAIN)** uses the same single-surface thicknesses (its `Default thickness` is the shell for solid groups).
- **Bot hearing (SAIN) – range:** the energy that passes the walls is turned into a hearing-range factor with the exponent 0.5 (square root). That follows from spherical spreading: sound pressure falls with 1/distance (−6 dB per doubling of distance), so the distance at which a sound is still heard scales with the amplitude factor, i.e. the square root of the energy factor [2]. The floors per sound type (0.35 / 0.25 / 0.15 / 0.2) are game-balance values, chosen by hand.

### 6. Air absorption

Air swallows high frequencies over distance; the international calculation method is ISO 9613-1 [7]. Since 0.7.0 True Acoustics uses it directly (setting `Use raid weather (ISO 9613-1)`, on by default).

**Formula.** The absorption coefficient for pure tones is (ISO 9613-1:1993, eq. 3-5, with the molar water-vapour concentration from annex B)

```
alpha(f) = 8.686 f^2 { 1.84e-11 (pa/pr)^-1 (T/T0)^0.5
           + (T/T0)^-2.5 [ 0.01275 e^(-2239.1/T) / (frO + f^2/frO)
                         + 0.1068  e^(-3352.0/T) / (frN + f^2/frN) ] }          [dB/m]
```

with the relaxation frequencies of oxygen and nitrogen `frO`, `frN` (which depend on pressure `pa`, temperature `T` and the water-vapour concentration `h` that follows from relative humidity). `T0 = 293.15 K`, `pr = 101.325 kPa`.

**From absorption to a low-pass.** The game filters with a cut-off frequency, not with a per-frequency attenuation. True Acoustics takes the frequency at which the air absorbs exactly **3 dB over the distance** (`alpha(fc) × d = 3 dB`; found by bisection) as the cut-off, multiplies it with the `Air clarity factor`, and clamps it to 2.5 ... 22 kHz. The 3 dB point is a choice, not part of the standard. A table "cut-off versus distance" (96 steps, 1 m ... 3 km) is calculated whenever the weather or a setting changes; each sound costs only a table lookup.

**Weather inputs.** Read from the game at raid start and every 30 s:

| Value | Source in the game | Used as |
|---|---|---|
| Temperature (°C) | `WeatherController.WeatherCurve.Temperature` | `T` |
| Air pressure | `AtmospherePressure` of the server's weather nodes (SPT: mmHg, about 760-780; interpolated over the raid time). It is not part of the weather curve, so the plugin reads it when the nodes are set. | `pa` = value × 0.133322 kPa (values below 200 count as kPa, from 900 as hPa; nothing found = 101.325 kPa) |
| Rain 0..1, fog density (0.004 clear ... 0.075 heavy), cloudiness (-1 clear ... 1 overcast) | `WeatherCurve.Rain`, `.Fog`, `.Cloudiness` | only for the humidity estimate |

**Humidity is an estimate** – the game has no air humidity. The rule (highest value wins):

- rain above 0.02: 95 % + 5 % × rain (95-100 %)
- fog density from 0.012: 80 % at 0.015 (faint fog) rising linearly to 100 % at 0.075 (heavy fog)
- otherwise the setting `Base humidity (%)` (default 60), plus up to 10 points with cloud cover (0 at cloudiness -0.5, +10 at +0.5)

Missing values are skipped (no weather at all = 20 °C, 101.325 kPa, base humidity). These thresholds and the cloud bonus are my own assumptions, not from a standard. The switch `Show weather in log` writes the values and the resulting cut-offs to `BepInEx/LogOutput.log` (`[Weather] ...`), to the header of the sound log, and again when T, p, humidity, rain or fog change noticeably.

**Checks** (offline test `Materialklang-KlangTest`, section 11). Absorption in dB/km against the ISO 9613-1 table (101.325 kPa), 10 °C and 70 % relative humidity: 125 Hz 0.41, 250 Hz 1.04, 500 Hz 1.92, 1 kHz 3.66, 2 kHz 9.70, 4 kHz 33.1, 8 kHz 118 (table: 0.41 / 1.04 / 1.93 / 3.66 / 9.66 / 32.8 / 117). At 20 °C: 8 kHz 77.6 dB/km at 70 % (about 76 in the table), **217 at 20 %**, **59 at 100 %**; 1 kHz 4.98, 4 kHz 23.1 at 70 %.

The 3 dB cut-off at 20 °C and 70 % relative humidity, compared with the fixed curve of versions up to 0.6.0 (`12 kHz × (19 m / d)^(2/3)`):

| Distance | Old curve | ISO 9613-1 (3 dB point) |
|---|---|---|
| 10 m | 18.4 kHz | 16.6 kHz |
| 19 m | 12.0 kHz | 11.7 kHz |
| 50 m | 6.3 kHz | 7.0 kHz |
| 100 m | 4.0 kHz | 4.7 kHz |
| 200 m | 2.5 kHz (floor) | 3.0 kHz |

So at the reference weather the new calculation is close to the old behaviour (within about 20 %). Examples with the new calculation (3 dB point before the clarity factor; the 2.5 kHz floor still applies afterwards):

| Distance | Sunny, 20 °C, 60 % | Rain, 10 °C, 97 % | Cold and dry, 0 °C, 40 % | Hot and dry, 30 °C, 20 % |
|---|---|---|---|---|
| 50 m | 6.5 kHz | 6.5 kHz | 3.3 kHz | 4.5 kHz |
| 100 m | 4.4 kHz | 4.5 kHz | 2.5 kHz (2.0) | 3.1 kHz |
| 300 m | 2.5 kHz (2.1) | 2.5 kHz (2.4) | 2.5 kHz (1.1) | 2.5 kHz (1.5) |

Cold air absorbs more than warm air at the same humidity, humid air less than dry air – in the first two columns the two effects almost cancel at the 3 dB point, so ordinary sunny and rainy raids sound much the same; cold dry weather is clearly duller. (Real air absorption also depends on frequency in a way a single cut-off cannot show; this is the simplification of the low-pass.) The `Air clarity factor` is a plain factor on the cut-off, tuned by ear – it is not a humidity input (the old name `Humidity` suggested "damp air = duller", which is the wrong way round: dry air absorbs highs *more*). Switching `Use raid weather` off brings back the fixed curve of 0.6.0. For SEB gunshots, the distance that is already "baked" into SEB's distant recordings is subtracted (`Shot offset`) – that is based on SEB's sound bank data, not on a formula.

### 7. Weapon directivity

The muzzle blast of a firearm is directional: measurements show that its level and waveform depend strongly on the angle between the barrel and the microphone [8]. True Acoustics uses

```
w = (1 − cos angle) / 2              0 = muzzle points at you, 1 = points away
level = Rear side (−4 dB) × w^1.3 × indoor factor
low-pass = 22 kHz × (6 kHz / 22 kHz)^(w² × indoor factor)
```

**The shape and all numbers (−4 dB, 6 kHz, exponent 1.3, indoor factor 0.5) are tuned by ear**, not fitted to the measurements in [8]; the reference only supports *that* gunshots are directional.

### 8. Timbre (Physics Mode off)

The low/high weights per material (e.g. concrete: highs ×1.5, lows ×0.6) that tilt the filter are **chosen by ear** to give each material its character. In Physics Mode this part is off, because the mass-law low-pass already carries the tilt.

### 9. Reverb by material

The reverb time of a room depends on its volume and on how much its surfaces absorb. True Acoustics uses *Sabine's formula* in SI units [9, 10]:

```
T60 = 0.161 · V / (S · α + 4·m·V)  V = room volume (m³), S = surface (m²), α = mean absorption coefficient, m = air attenuation (1/m)
```

- The game's baked room data treat every surface as α = 0.1 (found in the game's own data). True Acoustics measures the real wall materials of each room with 64 evenly spread rays, averages α per band (125 Hz … 4 kHz) and sends the **difference** to the game's reverb as a Meta XR *control zone*:
  `ΔT60 = T60(max(α, α_min)) − T60(0.1)`, clamped to −0.8 … +1.5 s.
- **Minimum absorption** (`Min. absorption`, default 0.05): the rays only see wall surfaces, but openings, furniture, gaps and clutter absorb too. The mean α is therefore never taken below 0.05, which means a room never rings longer than twice the game's baked reverb. This is an assumption, not a measured value. Up to 0.8.2 there was no such floor: bare concrete (α 0.02) gave +4 … +5 s for almost every room, so nearly every concrete room sat at the +1.5 s clamp and rooms of different size sounded the same.
- **Air absorption in the room** (the `4·m·V` term, as in ISO 354 [17]): m from ISO 9613-1 [7] at 20 °C, 50 % humidity and 101.325 kPa (typical room conditions), the same for the baked and the new reverb time. It matters in large halls and at high frequencies (m ≈ 0.007 /m at 4 kHz, 0.001 /m at 1 kHz).
- **Reverb level:** in a diffuse sound field the reverberant level is proportional to `10·log10(4 / A)` with the equivalent absorption area `A = S·α + 4·m·V` [2, 10], so the level change is `10·log10(A_baked / A_new)` (at most +3 dB with the 0.05 floor), clamped to ±6 dB.
- The `[Reverb]` log line at raid start reports how many rooms still reach the +1.5 s clamp.
- Sabine's formula overestimates reverb in strongly absorbing ("dead") rooms; Eyring's formula is the usual refinement there [11]. It is not used here: for hard walls (small α, e.g. concrete, metal, tile) – the common case indoors – the two formulas differ only slightly.
- **Absorption coefficients α per material and frequency are game data:** they come from an unused material table that ships with EFT (26 materials). Battlestate Games does not document where they come from; several rows look like the classic values found in room-acoustics tables (concrete, glass, fabric), but they are cited here as game data, not as a measurement source. The clamps are chosen by ear.

### 10. Game values and ray settings

- Ray counts (12 transmission / 24 reflection rays), max. 3 bounces and 70 % energy kept per bounce are the **game's** values; the `Advanced` multipliers only scale them. "After 6 bounces about 12 % energy is left" is simply 0.7⁶ ≈ 0.12.
- The material table in sections `2.xx` (attenuation per meter, base attenuation) is **hand-tuned**, not physics; it is only used while Physics Mode is off.

### 11. Forest (foliage attenuation)

**Standard.** ISO 9613-2:1996, annex A (attenuation by foliage) [15]: for a path through foliage of length `d_f` the attenuation is `A_fol = D_f × d_f`, with `D_f` from table A.1 per octave band:

| Octave band (Hz) | 63 | 125 | 250 | 500 | 1k | 2k | 4k | 8k |
|---|---|---|---|---|---|---|---|---|
| 10 m ≤ d_f ≤ 20 m: total dB | 0 | 0 | 1 | 1 | 1 | 1 | 2 | 3 |
| 20 m ≤ d_f ≤ 200 m: dB per metre | 0.02 | 0.03 | 0.04 | 0.05 | 0.06 | 0.08 | 0.09 | 0.12 |

The standard stops at 200 m; longer paths count as 200 m. **Verification status:** I could not open the standard itself (paid). The values above are the same in the CadnaA documentation, which reproduces the table of ISO 9613-2 [16], and match what I remember of the standard; the original text was not checked. The standard's wording for which foliage the table covers (dense foliage that blocks the view) is my reading, not quoted from the standard.

**My additions** (the standard has nothing for these): below 10 m the attenuation rises linearly from 0 to the 10 m value; from 20 m on the larger of the fixed value and `dB/m × d_f` is used (so the curve never falls); between bands the values are interpolated linearly in log frequency, outside 63 Hz ... 8 kHz they stay constant.

**How the trees are found.** I counted the objects in the game's scene files (SPT 4.1.6, EFT 0.16.9.40743). Trees are **ordinary GameObjects with a `LODGroup`**, named `pine`, `pine02_big`, `pine04_big`, `birch`, `tree`, `ash` (trees) and `filbert_big`, `filbert_small`, `fibert_hedge` (bushes). The Unity terrains of the six forest maps (Woods, Shoreline, Lighthouse, Customs, Reserve, Interchange) have **0 tree instances**, so terrain trees are not used. Tree and bush objects found in the terrain scenes (before weighting): Woods about 23,600, Shoreline 20,700, Lighthouse 11,600, Customs 5,600, Reserve 5,800, Interchange 2,400 (plus about 3,100 `*_Billboard` stand-ins, not counted); in the Streets, Ground Zero and indoor-map scenes I scanned I found no tree objects. At raid start the plugin collects the `LODGroup` objects once, picks the trees by name, and counts them in a 2D grid of 10 × 10 m cells (about 140 × 140 cells on Woods, built in a few milliseconds). Weights: trees 1, big bushes 0.5, small bushes 0.25; billboards, stumps, ferns, grass, dry or dead bushes 0.

**Dense foliage.** A cell counts fully as dense foliage from `Density threshold` trees per 100 m² (default 3, about one tree every 6 m); below that it counts in proportion (1.5 trees = half). With a crown diameter of roughly 4 m (my estimate) 3 trees per 100 m² give a mean free path of the line of sight of about 8 m, i.e. the view is blocked within a few tens of metres. This threshold is an assumption, not a standard value. On Woods the median occupied cell holds 1.5 weighted trees and 29 % of the occupied cells reach 3, so with the default a typical stretch of forest counts about half and the dense cores count fully (Shoreline: 16 % of the occupied cells reach 3, Lighthouse 19 %, Customs 11 %, Reserve 9 %, Interchange 8 %).

**Path.** For each sound the straight horizontal line between source and listener is followed through the grid (digital differential analyser, no memory allocation); the metres, weighted by the density of each cell, add up to `d_f` (at most 200 m counted). Only sounds within 400 m, not when source and listener are both indoors. The result is cached per sound source while neither source nor listener moves more than a metre.

**From the table to the game's model.** The game filters with one low-pass and one volume factor, not per band, so:
- **Level:** the 500 Hz band value `A_fol(500 Hz)` × `Strength` (100 m of forest: 5.0 dB) lowers the volume of the sound (only ever quieter).
- **Low-pass:** the rest of the spectral slope. The cut-off is the frequency above 500 Hz at which *air absorption over the whole distance* (ISO 9613-1 of the raid weather, interpolated between octave bands) plus the *foliage attenuation beyond the 500 Hz value* reach 3 dB (the same 3 dB point as the air low-pass), multiplied by the `Air clarity factor`, and combined with the existing air low-pass by taking the lower of the two. Unlike the air low-pass it has no 2.5 kHz floor (its natural lower limit is 500 Hz). The 3 dB point and the choice of 500 Hz are mine, not part of the standard.
- Gunshots from SEB get the low-pass in SEB's own filter chain (the level comes from the game's volume factor as for every sound).

Example (20 °C, 60 %, `Strength` 1, sound source in the distance given, the whole path through dense forest):

| Forest on the path | Level (500 Hz) | 1 / 2 / 4 / 8 kHz (table) | Low-pass, air only | Low-pass, air + forest |
|---|---|---|---|---|
| 50 m (source 50 m away) | -2.5 dB | 3.0 / 4.0 / 4.5 / 6.0 dB | 6.4 kHz | 3.6 kHz |
| 100 m (100 m away) | -5.0 dB | 6.0 / 8.0 / 9.0 / 12.0 dB | 4.4 kHz | 1.6 kHz |
| 200 m (200 m away) | -10.0 dB | 12 / 16 / 18 / 24 dB | 2.8 kHz | 1.0 kHz |
| 100 m (300 m away) | -5.0 dB | 6.0 / 8.0 / 9.0 / 12.0 dB | 2.1 kHz | 1.1 kHz |

**Checks** (offline test `Materialklang-KlangTest`, section 12): table values and the 10 m / 20 m / 200 m limits, monotonic behaviour, 0 m = no effect, interpolation, air bands against the exact ISO 9613-1 formula, cut-off without forest against the air-only cut-off (4366 against 4386 Hz at 100 m), path tracing on a synthetic grid (straight, reversed, diagonal, partly inside, beside the forest, thresholds), classification of object names, cost (about 1.5 µs per 500 m path, 3.5 µs for the low-pass, no memory allocation; the cache makes most updates free).

### References

1. L. L. Beranek, I. L. Vér (eds.): *Noise and Vibration Control Engineering: Principles and Applications.* Wiley, New York 1992; 2nd ed. (I. L. Vér, L. L. Beranek), Wiley 2006.
2. D. A. Bies, C. H. Hansen, C. Q. Howard: *Engineering Noise Control*, 5th ed. CRC Press, Boca Raton 2017.
3. EN ISO 12354-1:2017 – *Building acoustics – Estimation of acoustic performance of buildings from the performance of elements – Part 1: Airborne sound insulation between rooms.*
4. EN 1991-1-1:2002 – *Eurocode 1: Actions on structures – Part 1-1: General actions – Densities, self-weight, imposed loads for buildings*, Annex A.
5. EN 1993-1-1:2005 – *Eurocode 3: Design of steel structures – Part 1-1: General rules and rules for buildings*, 3.2.6 (ρ = 7850 kg/m³).
6. EN 572-1 – *Glass in building – Basic soda lime silicate glass products – Part 1: Definitions and general physical and mechanical properties* (ρ = 2500 kg/m³).
7. ISO 9613-1:1993 – *Acoustics – Attenuation of sound during propagation outdoors – Part 1: Calculation of the absorption of sound by the atmosphere.*
8. S. D. Beck, H. Nakasone, K. W. Marr: "Variations in recorded acoustic gunshot waveforms generated by small firearms." *J. Acoust. Soc. Am.* 129(4), 1748–1759 (2011).
9. W. C. Sabine: *Collected Papers on Acoustics.* Harvard University Press, Cambridge MA 1922.
10. H. Kuttruff: *Room Acoustics*, 6th ed. CRC Press, Boca Raton 2017.
11. C. F. Eyring: "Reverberation time in 'dead' rooms." *J. Acoust. Soc. Am.* 1(2A), 217–241 (1930). doi:10.1121/1.1915175
12. B. H. Sharp: "Prediction methods for the sound transmission of building elements." *Noise Control Engineering* 11(2), 53–63 (1978). (Bibliographic data checked against the INCE table of contents; the article itself was not available to me.)
13. "Acoustic properties of glubam and SPF", arXiv:2212.04173 – applies Sharp's model in three regions around the coincidence frequency, with the same −48 dB mass-law form and `10·log10(2ηf/(π·fc))` term above `fc`.
14. Typical snow densities (fresh snow roughly 100–300 kg/m³, settled/packed snow roughly 300–500 kg/m³): standard figures from snow science and avalanche handbooks; no single table is cited.
15. ISO 9613-2:1996 – *Acoustics – Attenuation of sound during propagation outdoors – Part 2: General method of calculation*, annex A.2 (attenuation by foliage), table A.1. (A 2024 edition exists.)
16. Datakustik, CadnaA documentation, "Built-Up Areas and Foliage" (https://doku.datakustik.com/CadnaA/en/BebaungBewuchs.html) – reproduces the foliage table of ISO 9613-2; used to check the numbers because the standard itself was not available to me.
17. ISO 354:2003 – *Acoustics – Measurement of sound absorption in a reverberation room* (Sabine formula with the air term 4mV).

## Credits

- **[Sound Engine Backport](https://github.com/RedTheFoxx/SPT-SEB) by RedTheFoxx** – the live sound engine for SPT this is an add-on for. True Acoustics only hooks into it at run time (Harmony); no SEB code is included. Thanks for a mod to build on.
- **SAIN** – the bot AI whose hearing check the optional bot mode adjusts.
- **Meta XR Audio** (Meta Platforms) and **Battlestate Games' spatial audio system** – the ray-based occlusion and the room reverb that this mod feeds with materials; the absorption values per material come from the unused material table that ships in the game.
- The physics are textbook acoustics: the *mass law* for wall insulation and *Sabine's formula* for reverb time – see [Physics and sources](#physics-and-sources) for the references.
- **BepInEx** and **Harmony** for making all of this possible.

## Changelog

See [CHANGELOG.md](https://github.com/Kaanori/spt-true-acoustics/blob/main/CHANGELOG.md).

## Building from source

Needs the .NET 10 SDK and an SPT 4.1.6 installation to build against.

```bash
./build.sh             # Linux: build only
./build.sh --install   # build and copy into the game folder
```
```powershell
.\build.ps1            # Windows
.\build.ps1 -Install
```

The game folder is set in `Directory.Build.props` (Windows default `C:/SPT`)
or via the environment variable `SPT_DIR`.

## Support

Bugs and questions: [GitHub issues](https://github.com/Kaanori/spt-true-acoustics/issues).

## License

MIT — see [LICENSE](https://github.com/Kaanori/spt-true-acoustics/blob/main/LICENSE).
