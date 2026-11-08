# Materialklang (Material Sound) – BETA

Sound that knows what a wall is made of. Concrete swallows the highs and lets a dull thump through, a wooden door is a weak barrier, a steel door a strong one, and a gunshot behind you sounds different from one in front of you. Works for **all** sounds in the game, not just gunshots.

> **BETA – early version.** Materialklang has been listened to in the F12 menu and sounds good, but it has **not yet been checked in real raids** (no raid log review, no long play sessions, only one tester). Expect rough edges and values that still need tuning. Feedback is very welcome: please open an issue and, if you can, attach the relevant lines of `BepInEx/LogOutput.log` (they start with `[Diagnostics]` or `[Reverb]`).

## How this mod was made (AI disclosure)

This mod was **programmed by Claude**, an AI assistant made by Anthropic.
**Kaan was the tester**: Kaan has no programming background, played with the mod
on SPT 4.1.6, reported what went wrong, and Claude fixed it. No human programmer
has reviewed the code. It is open source so anyone can check it — if you find a
bug, please open an [issue](https://github.com/Kaanori/spt-materialklang/issues).

## Requirements

- SPT 4.1.6 (EFT 0.16.9). BepInEx ships with SPT.
- Designed as an add-on for [Sound Engine Backport](https://github.com/RedTheFoxx/SPT-SEB) (SEB 1.0.3): recommended, but not required – without it only the weapon directivity and the air absorption of gunshots switch off, everything else keeps working. Optional: SAIN 4.5.1 (bot hearing, off by default). The reverb zones use the Meta XR Audio library that ships with the game.

## Installation

1. Download `Materialklang-0.5.0-beta.zip` from the [Releases](https://github.com/Kaanori/spt-materialklang/releases) page.
2. Extract it **into your SPT game folder** (the folder that contains `EscapeFromTarkov.exe`).
   The ZIP already has the right layout; the mod ends up in `BepInEx/plugins/`.
   On Windows, make sure "Extract All" does not add an extra subfolder.
3. Restart the game.

## Uninstall

Delete the mod's files from `BepInEx/plugins/` (settings: `BepInEx/config/` for client mods) and restart the game.

## What it does, in plain words

Escape from Tarkov (and SPT) already shoots invisible rays between a sound and your ears to decide how muffled it is, but the game treats every wall the same: a thin plywood board and a thick concrete wall differ only in how many meters the ray travelled through them. Materialklang looks at what the ray actually hit (concrete, wood, metal, glass, soil, ...) and works out the muffling from that.

It is an add-on for **Sound Engine Backport (SEB)** and is meant to be played together with it, but most of it also works without.

## Features

| Feature | What you hear |
|---|---|
| **Physics Mode** (on by default) | Real building acoustics: the insulation of a wall follows the *mass law* (heavier and thicker = quieter). Thick concrete is nearly silent, a plank is not. Applies to every sound with an obstacle in the way. |
| **Material table** (sections `2.xx`) | The simpler, hand-tunable alternative to Physics Mode: attenuation per meter and base attenuation for 14 material groups. Used when Physics Mode is off. |
| **Doors and windows** | A closed thin wooden door lets more through than a closed steel door; an intact window absorbs some sound, a broken one none; rows of windows count in proportion to their free area. |
| **Walls between rooms** | Vanilla only lets sound travel between rooms through doors and windows – the wall in between does not count. Materialklang also measures the straight line through the walls and uses whichever path is quieter, so a loud fight next door can come through the wall. |
| **Timbre** | Materials filter highs and lows differently (concrete: dull but with a thump, wood: hollow). Only for sound through walls. |
| **Weapon directivity** | Other people's gunshots are quieter and duller when the muzzle points away from you (less so indoors). Needs SEB. |
| **Air absorption** | Highs fade with distance (about 12 kHz at 19 m, falling with distance, never below 2.5 kHz); humidity setting for damp/foggy air. For gunshots (SEB) the part that is already baked into SEB's distant recordings is subtracted. |
| **Reverb by material** | At raid start the walls of every interior room are measured, and a Meta XR Audio *control zone* per room shifts reverb time and level (a concrete bunker rings longer than a wooden hut). |
| **Bot hearing** (off by default) | With SAIN installed, bots can hear through walls according to the material in between (concrete muffles more than wood). Optional and experimental. |
| **Advanced** | More rays for smoother results, bounce count and energy kept per bounce. |

Every part has its own **Enabled** switch in the F12 menu. Switch off everything and you get vanilla sound back.

## Requirements and compatibility

| | |
|---|---|
| **Tested with** | SPT 4.1.6 (EFT `0.16.9.40743`), Sound Engine Backport 1.0.3, SAIN 4.5.1, BepInEx 5; developed and played on Linux (Proton). |
| **Files** | `Materialklang.dll` -> `BepInEx/plugins/Materialklang/` (GUID `kaan.materialklang`) |
| **Settings** | `BepInEx/config/kaan.materialklang.cfg` (F12 menu, English labels) |
| **License** | MIT |

- **Sound Engine Backport (SEB)** – *recommended, not required* (soft dependency). Materialklang was built and tuned next to SEB 1.0.3. Without SEB, **weapon directivity** and the **air absorption of gunshots** switch off (the log says so); everything else (materials, doors, windows, walls, timbre, air absorption of other sounds, reverb) only touches the base game's own audio system. Running it *without* SEB has not been tested yet.
- **SAIN** – optional, only for **bot hearing**, which is **off** by default. Tested with SAIN 4.5.1; with another version the log shows a warning. Without SAIN that section simply does nothing.
- **Meta XR Audio** – not something you install: it is part of the game (`Meta.XR.Audio.dll`) and is what the game itself uses for room reverb. The reverb zones talk to it directly. If it is unavailable the zones are skipped and the rest keeps working.
- It hooks into internal parts of the game's audio system, so a **game update can break parts of it**. Where a hook target is missing, Materialklang logs a warning and that part stays vanilla.
- Other mods that change volume, room sound or the mixer can fight with it (SEB's author gives the same warning for his own mod). If something sounds odd, switch Materialklang's sections off one by one in F12 to find the culprit.

## Settings overview (F12)

Sections and the most important settings with their defaults. All values have tooltips in the menu.

| Section | Key settings (default) |
|---|---|
| **0. Physics Mode (real values)** | `Enabled` (on); `Max. insulation (dB)` (60); `Loudness factor` (0.2); `Reference frequency (Hz)` (250); `Low-pass at full insulation (Hz)` (800); `Clear-path threshold` (0.02); per material group `Density` (kg/m3) and `Solid up to` (m) |
| **1. General** | `Enabled` (on); `Overall strength` (1.0); `Clear-path threshold` (-1 = game value); `Diagnostic log` (on) |
| **2. 01 ... 14** (one per material group) | `Attenuation per meter`, `Base attenuation` – only used while Physics Mode is off |
| **3. Bot Hearing (SAIN)** | `Mode` (**Off** / QuieterOnly / Replace); `Strength` (1.0); `Exponent` (0.5); floors for gunshot / suppressed shot / footsteps / other sounds; `Calculations per frame` (6) |
| **4. Doors and Windows** | `Enabled` (on); `Door thin wood` (0.35); `Door thick wood` (0.45); `Door thin metal` (0.6); `Door thick metal` (0.8); `Window intact` (0.55) |
| **5. Walls Between Rooms** | `Enabled` (on); `Max. distance (m)` (40); `Surcharge` (0.05); `Shell thickness (m)` (0.15) |
| **6. Timbre** | `Enabled` (on); `Strength` (1.0) – has no effect while Physics Mode is on |
| **7. Weapon Directivity** | `Enabled` (on); `Rear side (dB)` (-4); `Rear low-pass (Hz)` (6000); `Indoor factor` (0.5) |
| **8. Air Absorption** | `Enabled` (on); `Humidity` (1.0); `Shot offset` (1.0) |
| **9. Reverb by Material** | `Enabled` (on); `Strength` (1.0); `Max zones` (400); `Only largest box per room` (on); `Diagnostics` (on) |
| **10. Advanced (rays)** | `Transmission rays x` (1); `Reflection rays x` (1); `Max. bounces` (0 = game value); `Energy kept per bounce` (0 = game value) |

Notes:

- **Physics Mode is on by default**, so the material table (`2.xx`) and Timbre are inactive until you switch it off. Prefer the hand-tunable table? Turn Physics Mode off.
- **Too quiet behind walls?** Raise `Loudness factor` (0.1 = barely any muffling ... 1.0 = true-to-life, i.e. near silence behind concrete). The default 0.2 was chosen by ear with headphones.
- Some settings only take effect after a restart or from the next raid; the tooltip says which.

## Known limits (beta)

- **Not tested in raids yet.** It has been judged in the F12 menu and on a short listening test, not over long sessions, and not on all maps. The values (especially `Loudness factor`, door and window values, reverb strength) are my taste and may not be yours.
- **Performance.** The extra work is small but not zero: the wall measurement between rooms starts at most 8 ray batches per frame, reverb zones are measured once at raid start (a short pause, logged as milliseconds), bot hearing is capped at 6 calculations per frame. If you see frame drops, switch off `5. Walls Between Rooms`, `9. Reverb by Material` or leave `3. Bot Hearing` off first. The diagnostic log writes a few lines every 30 s; switch it off with `Diagnostic log` if you do not need it.
- **Bot hearing is experimental** and off by default. Switching it on changes how far bots hear you, so it changes difficulty.
- **Maps.** The reverb measurement relies on the game's interior rooms (about 400 zones at most per raid); open or unusual rooms may be skipped.
- **No material data for some surfaces.** Anything the game does not tag with a material counts as "Unknown" and behaves like vanilla.
- **Game updates** can break hooks (see above).

## Credits

- **[Sound Engine Backport](https://github.com/RedTheFoxx/SPT-SEB) by RedTheFoxx** – the live sound engine for SPT this is an add-on for. Materialklang only hooks into it at run time (Harmony); no SEB code is included. Thanks for a mod to build on.
- **SAIN** – the bot AI whose hearing check the optional bot mode adjusts.
- **Meta XR Audio** (Meta Platforms) and **Battlestate Games' spatial audio system** – the ray-based occlusion and the room reverb that this mod feeds with materials; the absorption values per material come from the unused material table that ships in the game.
- The physics are textbook acoustics: the *mass law* for wall insulation and *Sabine's formula* for reverb time.
- **BepInEx** and **Harmony** for making all of this possible.

## Changelog

See [CHANGELOG.md](CHANGELOG.md).

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

Bugs and questions: [GitHub issues](https://github.com/Kaanori/spt-materialklang/issues).

## License

MIT — see [LICENSE](LICENSE).
