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

## Physics and sources

What Materialklang calculates, what each part is based on, and where a value is simply game data or chosen by ear. Numbers in square brackets point to the reference list at the end of this section.

### 1. Wall insulation: the mass law (Physics Mode)

A wall stops sound mainly by being heavy: the sound has to shake it, and a heavier wall moves less. The textbook rule for this is the *mass law*. For sound arriving straight on (normal incidence) it is `TL ≈ 20·log10(m·f) − 42 dB`; for sound arriving from all directions, as in a real room (*field incidence*), it is about 5 dB lower [1, 2]. Materialklang uses the field-incidence form:

```
TL = 20 · log10(m · f) − 47 dB        m = density × thickness (kg/m²), f = 250 Hz
TL is clamped to 0 ... 60 dB ("Max. insulation")
```

- **TL** (transmission loss) = how many decibels the wall takes away. The rule rises by 6 dB per doubling of mass or frequency.
- **250 Hz** is used because that is the range you mainly hear through walls (adjustable, 63–1000 Hz).
- **The 60 dB cap** stands for *flanking*: in a real building sound also leaks around a wall (ceilings, floors, pipes), so the insulation you actually get between two rooms stays well below what the wall alone would allow [3]. The value 60 dB itself is an assumption, not a measured value.
- **Hollow objects:** if the game reports a piece thicker than the material's `Solid up to` value (e.g. a car or a container measured from one side to the other), it counts as two shells of that thickness. This is a simplification (a real double wall behaves differently).
- **Not modelled:** the mass law only holds below the *coincidence frequency* of a panel (where the wall starts to bend along with the sound); coincidence dips, resonances and stiffness are ignored [1, 2]. For thick concrete the real value at 250 Hz is therefore usually somewhat lower than the formula says.

Examples with the default densities (before the loudness factor):

| Piece | Surface mass | TL at 250 Hz | What you hear with `Loudness factor` 0.2 |
|---|---|---|---|
| Thin wood, 2 cm | 10 kg/m² | 21 dB | −4 dB |
| Thin metal, 3 mm | 24 kg/m² | 28 dB | −6 dB |
| Thick wood, 10 cm | 60 kg/m² | 37 dB | −7 dB |
| Concrete, 20 cm | 460 kg/m² | 54 dB | −11 dB |
| Concrete, 1 m | 2300 kg/m² | 68 → capped 60 dB | −12 dB |

### 2. Default densities

| # | Group | Density (kg/m³) | `Solid up to` (m) | Basis |
|---|---|---|---|---|
| 01 | Concrete/Stone | 2300 | 1.0 | Plain normal-weight concrete; EN 1991-1-1 gives 24 kN/m³ (about 2400–2450 kg/m³) for normal-weight concrete [4] |
| 02 | Soil/Terrain | 1700 | 3.0 | Typical value for soil (roughly 1500–2000) |
| 03 | Gravel/Snow | 1600 | 3.0 | Typical value for loose gravel; snow is much lighter, so snow is over-insulated |
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

Densities 01, 04, 05, 08 are standard values; the others are typical handbook-range values or deliberate mixed values, not taken from one specific table. The `Solid up to` thicknesses are assumptions about the game's geometry, not literature values.

### 3. From decibels to loudness and muffling

- **Loudness:** `volume factor = 10^(−dB × Loudness factor / 20)` – the standard conversion from a level difference in dB to a sound-pressure (amplitude) factor [2].
- **Loudness factor 0.2 is chosen by ear, not physics.** A real gunshot is far louder than headphones can play, so true-to-life insulation (factor 1.0) would make anything behind concrete silent. 0.2 compresses the real values into the range of game loudness.
- **Muffling (low-pass):** the mass law rises 6 dB per octave, so a wall removes highs more than lows [1, 2]. The mapping `cut-off = 22 kHz × (800 Hz / 22 kHz)^score` (logarithmic, from open to fully insulated) is my own approximation, not a formula from the literature.
- **The lightest path counts:** sound reaches you through the wall, around corners and via reflections at the same time. Parallel paths add up as energy, so the weakest barrier dominates – the same idea as flanking transmission in building acoustics [3]. Materialklang takes the *smallest* of the three occlusion scores instead of the game's average; using the minimum instead of a full energy sum is a simplification.

### 4. Doors and windows

The defaults (`Door thin wood` 0.35, `thick wood` 0.45, `thin metal` 0.6, `thick metal` 0.8, `Window intact` 0.55) are scores on the same scale as Physics Mode (score × 60 dB), i.e. about 21, 27, 36, 48 and 33 dB before the loudness factor. They are **set by hand** for a plausible order (wood < glass < steel), not taken from measurements of particular doors; real doors are often limited by the gaps around them. A row of windows counts in proportion to its open area (open area lets sound through unhindered) – a simple area weighting.

### 5. Walls between rooms and bot hearing

- **Walls between rooms** use the same mass law (section 1) for each wall piece on the straight line, or the material table when Physics Mode is off. Single surfaces without a back face count as a shell of 0.15 m; pieces over 1.5 m count as two shells (assumption about the game's meshes).
- **Bot hearing (SAIN):** the energy that passes the walls is turned into a hearing-range factor with the exponent 0.5 (square root). That follows from spherical spreading: sound pressure falls with 1/distance (−6 dB per doubling of distance), so the distance at which a sound is still heard scales with the amplitude factor, i.e. the square root of the energy factor [2]. The floors per sound type (0.35 / 0.25 / 0.15 / 0.2) are game-balance values, chosen by hand.

### 6. Air absorption

Air swallows high frequencies over distance; the international calculation method is ISO 9613-1 [7]. Materialklang uses a simple low-pass instead of the full per-band ISO calculation:

```
cut-off(d) = 12 kHz × (19 m / d)^(2/3) × Humidity     clamped to 2.5 ... 22 kHz
```

Checked against ISO 9613-1 at 20 °C and 70 % relative humidity (the frequency at which air absorption reaches 3 dB over the distance):

| Distance | Materialklang | ISO 9613-1 (3 dB point) |
|---|---|---|
| 10 m | 18.4 kHz | 16.6 kHz |
| 19 m | 12.0 kHz | 11.7 kHz |
| 50 m | 6.3 kHz | 7.0 kHz |
| 100 m | 4.0 kHz | 4.7 kHz |
| 200 m | 2.5 kHz (floor) | 3.0 kHz |

So the anchor point matches ISO 9613-1 well; the curve falls a little faster than ISO at long range, and the 2.5 kHz floor is a choice. **The `Humidity` slider is a plain factor tuned by ear.** In ISO 9613-1 the effect of humidity depends on temperature (at 20 °C, dry air actually absorbs highs more than humid air), so the slider is not an ISO humidity input. For SEB gunshots, the distance that is already "baked" into SEB's distant recordings is subtracted (`Shot offset`) – that is based on SEB's sound bank data, not on a formula.

### 7. Weapon directivity

The muzzle blast of a firearm is directional: measurements show that its level and waveform depend strongly on the angle between the barrel and the microphone [8]. Materialklang uses

```
w = (1 − cos angle) / 2              0 = muzzle points at you, 1 = points away
level = Rear side (−4 dB) × w^1.3 × indoor factor
low-pass = 22 kHz × (6 kHz / 22 kHz)^(w² × indoor factor)
```

**The shape and all numbers (−4 dB, 6 kHz, exponent 1.3, indoor factor 0.5) are tuned by ear**, not fitted to the measurements in [8]; the reference only supports *that* gunshots are directional.

### 8. Timbre (Physics Mode off)

The low/high weights per material (e.g. concrete: highs ×1.5, lows ×0.6) that tilt the filter are **chosen by ear** to give each material its character. In Physics Mode this part is off, because the mass-law low-pass already carries the tilt.

### 9. Reverb by material

The reverb time of a room depends on its volume and on how much its surfaces absorb. Materialklang uses *Sabine's formula* in SI units [9, 10]:

```
T60 = 0.161 · V / (S · α)          V = room volume (m³), S = surface (m²), α = mean absorption coefficient
```

- The game's baked room data treat every surface as α = 0.1 (found in the game's own data). Materialklang measures the real wall materials of each room with 64 evenly spread rays, averages α per band (125 Hz … 4 kHz) and sends the **difference** to the game's reverb as a Meta XR *control zone*:
  `ΔT60 = 0.161 · V/S · (1/α − 1/0.1)`, clamped to −0.8 … +1.5 s.
- **Reverb level:** in a diffuse sound field the reverberant level is proportional to `10·log10(4 / (S·α))` [2, 10], so the level change is `10·log10(0.1 / α)`, clamped to ±6 dB.
- Sabine's formula overestimates reverb in strongly absorbing ("dead") rooms; Eyring's formula is the usual refinement there [11]. It is not used here: for hard walls (small α, e.g. concrete, metal, tile) – the common case indoors – the two formulas differ only slightly.
- **Absorption coefficients α per material and frequency are game data:** they come from an unused material table that ships with EFT (26 materials). Battlestate Games does not document where they come from; several rows look like the classic values found in room-acoustics tables (concrete, glass, fabric), but they are cited here as game data, not as a measurement source. The clamps are chosen by ear.

### 10. Game values and ray settings

- Ray counts (12 transmission / 24 reflection rays), max. 3 bounces and 70 % energy kept per bounce are the **game's** values; the `Advanced` multipliers only scale them. "After 6 bounces about 12 % energy is left" is simply 0.7⁶ ≈ 0.12.
- The material table in sections `2.xx` (attenuation per meter, base attenuation) is **hand-tuned**, not physics; it is only used while Physics Mode is off.

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

## Credits

- **[Sound Engine Backport](https://github.com/RedTheFoxx/SPT-SEB) by RedTheFoxx** – the live sound engine for SPT this is an add-on for. Materialklang only hooks into it at run time (Harmony); no SEB code is included. Thanks for a mod to build on.
- **SAIN** – the bot AI whose hearing check the optional bot mode adjusts.
- **Meta XR Audio** (Meta Platforms) and **Battlestate Games' spatial audio system** – the ray-based occlusion and the room reverb that this mod feeds with materials; the absorption values per material come from the unused material table that ships in the game.
- The physics are textbook acoustics: the *mass law* for wall insulation and *Sabine's formula* for reverb time – see [Physics and sources](#physics-and-sources) for the references.
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
