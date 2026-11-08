# Changelog

## 0.8.4-beta (not released yet – in testing)

- **Reflection path no longer flips to "direct path clear" (game bug).** From the second reflection step on, the game kept reading index 0 of the batch as the direct ray – but there sits some reflection ray, and if that one flew into the open, the whole calculation counted as "direct path clear" (R = 0). Depending on frame timing a bot 55 m away behind buildings jumped several times a second between –1 dB (clear) and –24 to –31 dB (behind walls) – reported with the mark key on Streets, 08.11.2026. Vanilla only moves about 1 dB there, the physics mode made it audible. Now the real result of the direct ray from step 1 is kept and later steps no longer count as a direct ray. Diagnostic line "Reflection direct path …" every 30 s. Part of "Steady occlusion".
- **Map export for the sound sandbox.** F12 > 11: "Export map for sandbox" cuts the level's collision geometry at ear height (settable) and writes walls with their game material and estimated thickness, plus doors and windows, to `BepInEx/Materialklang-Logs/sandbox-<map>-<height>.json`; the Material Sound Demo loads it with "Load map…". Runs over several frames, confirmation sound when done.

## 0.8.3-beta

- `Mark odd sound key` now plays a short confirmation sound (the game's quest "sub-task complete" ding), or the error sound if the sound log is off – no need to check the log to know the mark was set.

Fixes from a code review of 0.8.2, checked against the Sandbox and City sound logs of 08.11.2026. Built, not yet heard in a raid. The plugin reports the numeric version 0.8.3.

**Low-pass jumping by an octave every few frames (bots behind walls)**
- *Cause:* the game keeps **one** occlusion value per occluder, but each sound of a bot (footsteps, gear, breathing, weapon) gets it multiplied by the indoor/outdoor factor of its own sound group (character sounds 2.0, weapon sounds 1.5). On a full update every sound gets its own value. Between full updates the game re-applies the stored *volume* of each sound but recalculates the *low-pass* from the occluder's single value – which still holds the value of the sound calculated last. In the log: door path 0.46 and volume constant, the footsteps' effect alternating 0.92 / 0.69 (= 0.46 × 2.0 / × 1.5) and the low-pass 1043 / 2235 Hz every 2–3 frames (56 jumps of one octave or more in the Sandbox log). Volume and low-pass of a sound also came from different calculations. Vanilla has the same bug with a flatter filter curve.
- *Fix:* the value is remembered per sound when the game calculates it and put back right before the game applies that sound's filter. Low-pass and volume of every sound now come from the same calculation. Part of `Steady occlusion` (on). New diagnostic line: `Per-source occlusion: N of M filter updates would have used the occlusion of another sound`.

**Walls between rooms: the wall (almost) never won**
- *Cause 1:* if one of the two rays of a measuring line had 8 hits, the whole line counted as blocked – 201 of 230 lines in the Sandbox log, "wall won 0x". Now a line is only unknown if *both* rays are full and leave an unseen gap; then it counts as blocked only if what was seen already reaches the 60 dB cap, otherwise it is left out.
- *Cause 2:* every single surface without a back face counted as 0.15 m of wall (posters, car panels, glass about 34–41 dB). Now thin groups get a typical thickness (metal sheet 2 mm, glass 6 mm, posters/plastic 5 mm, thin wood 12 mm, thick wood/doors 40 mm, thick metal 10 mm); solid groups (concrete, soil, …) stay at `Shell thickness`, because there the surface is one side of a slab – the road above the Sandbox car park is real concrete between you and the bot and keeps counting.
- *Cause 3:* the four lines offset by 0.25 m often start inside geometry (under the pavement a bot stands on, behind the wall you lean against) and hit a surface that is not between the sound and you. Such lines are now left out unless the centre line has a surface at the same end; the other lines share their weight. If no line says anything, the game's door path applies.
- Bot hearing (SAIN) uses the same single-surface thicknesses. New counters in the `Walls:` diagnostic line.

**Pooled calculators carried the previous sound's result**
- The game returns occlusion calculators to its pool without resetting them, so a new sound read the score of the previous owner for 1–3 frames (in Physics Mode it was even kept as a "finished" score). The calculator is now reset when a new occluder takes it, exactly as the game does when it switches between obstruction and door path: "clear" until its own first calculation is done.

**Gunshots safe against SEB changes**
- Everything that uses Sound Engine Backport internals (its 11-parameter shot context, private fields) now sits in separate methods whose call is protected. If SEB changes them, the SEB parts switch off for the session with one error line and shots play as SEB intends – before, the hook could fail before its own error handling and the shot stayed silent. SEB versions other than 1.0.x leave the SEB parts off (warning).

**Reverb: nearly every concrete room at the +1.5 s limit**
- Bare concrete (α 0.02) gave +4 … +5 s for almost every room, so all concrete rooms sounded the same. New setting `9. Reverb by Material / Min. absorption` (0.05): the rays only see walls, openings and furniture absorb too, so the mean α is never taken below 0.05 (at most twice the game's baked reverb). The air absorption inside the room (4mV term, ISO 354; ISO 9613-1 at 20 °C / 50 %) is now included, which shortens the high bands of large halls. The `[Reverb]` line reports how many rooms still reach the limit.

**Smaller fixes**
- Timbre is no longer calculated in Physics Mode, where it was never applied (`timbre 316 calculations, applied 0x`).
- Looping sounds: their playback time restarts at 0 on every loop, so they counted as "just started" and their filter could jump; a loop that was already playing now glides.
- Rows of windows: the remaining glass of a partly broken row was counted twice (the game already uses the size of the broken part); removed.
- Sound log: rooms now show their names (`rooms src=… ear=…` was empty), and the weather in the file header is marked as incomplete when the raid weather is not there yet at raid start; the first complete reading follows as a `[Weather] complete` line.
- Live tuning and F12 changes rebuild only what the setting affects: door/window values and the game's portal data only for `4. Doors and Windows` and `1. General / Enabled`, wall measurements for Physics Mode, materials, `Overall strength` and walls; everything else (air, reverb, loudness, …) no longer forces the game to recalculate every occlusion.
- Errors: after the first three full error messages, the number of further errors is logged every 30 s (also with `Diagnostic log` off).

**Checked and left unchanged**
- The fog scale of the humidity estimate matches the game (`EFogType`: no fog 0.004, faint 0.015, fog 0.035, heavy 0.075).
- The forest low-pass limit of 500 Hz matches the README.
- Volume steps when you or a bot change between indoors and outdoors come from the game's indoor/outdoor factor switching at once; documented under known limits, not smoothed yet.
- The minimum over transmission (dB scale in Physics Mode) and the game's diffraction/reflection heuristics is documented as a known limit.

**Tests** (`Materialklang-KlangTest`, sections 15–20, 245 checks): the per-sound value with the log numbers (game: 57 octave jumps, 0.8.3: none), pool reset, wall lines (full rays, gap, offset lines in geometry, single surfaces, weights), reverb (old formula, floor, air), SEB version and error classes, loops, refresh rules, fog scale.

## 0.8.2-beta

Fix for one-shot sounds (explosions, bullet impacts, ricochets, dropped items) that sometimes sounded fully muffled in the open - e.g. an F1 grenade 17 m away outdoors on Ground Zero heard only as a dull "puff". The plugin reports the numeric version 0.8.2.

**Cause** (found in a Ground Zero sound log of 0.8.1; the same lines are already in 0.6.0 logs, so it is not caused by the 0.8.1 steady-occlusion fix)
- The game handles all one-shot sounds with one shared `MultiSourceSpatialAudioOccluder`. Each sound has its own pooled calculator (a room-path calculator *or* a transmission calculator), but every result is written into the *same* two fields of the occluder: the room-path calculator sets only `PropagationEffect`, the transmission calculator only `ObstructionEffect`. The other field keeps the value of whichever one-shot sound was calculated before.
- `CalculateTotalEffect` uses `PropagationEffect > 0 ? PropagationEffect : ObstructionEffect`. A sound whose room path is clear (score 0, e.g. between two outdoor areas) therefore took the transmission score of an unrelated sound - here an impact behind terrain with 1.0. Physics Mode turns 1.0 into -30 dB and an 800 Hz low-pass. The reverse also happened: a transmission sound after a room-path sound with a score above 0 was treated as a room-path sound with the other sound's score.
- In the sound log these lines show as `path=unknown [T=n/a D=n/a R=n/a effect=...]` for `@GameWorld` sources; several sounds at different places in the same frame had the identical effect. Vanilla has the same bug, but its volume curve ends at -4.4 dB, so it is barely audible there.

**Fix**
- Right before the game applies the occlusion of a one-shot sound, the field that belongs to no calculator of *that* sound is set to 0, so every one-shot sound uses only its own result. Active whenever the plugin is enabled. The 0.8.1 steady-occlusion fix (no flip-flop during recalculation) is unchanged.
- New diagnostic line every 30 s: `One-shot sounds: N of M would have inherited the occlusion of another sound`.
- Tests (`Materialklang-KlangTest`, section 14): the shared-field sequence from the log (game: 4 of 6 sounds with a foreign effect, the grenade at -30 dB instead of 0 dB; 0.8.2: none), edge cases, no allocations.

## 0.8.1-beta

Fix for crackling, clicking and jumping volume behind walls. The plugin reports the numeric version 0.8.1.

**Cause** (found in a Streets sound log of 0.8.0)
- While the game recalculates the path of a sound (every time the source or you move about 0.25 m, so constantly for walking bots), `SpatialAudioOcclusionCombinedCalculator` already sets its "which paths ran" flags for the *new* calculation (transmission only, then transmission + diffraction, then all three) while the stored result is still the *old* one. Physics Mode takes the easiest path (minimum); when the easiest path was the reflection (score 0 = clear) and the flags said "reflection not run", the score jumped from 0 to 1 for a few frames: from 0 dB to -30 dB and from 22 kHz to 800 Hz, and back when the calculation finished. In the log, 47 of 69 volume jumps of 6 dB or more within 6 frames were exactly this (same stored result, only the flags differed). Vanilla averages instead of taking the minimum and its volume curve ends at -4.4 dB, which is why the same game behaviour is hardly audible there.
- In addition, the game applies small changes (below its 0.05 change threshold) with `updateImmediately`, so the low-pass of a playing sound jumped to its new value instead of gliding, and the game's own glide is linear in Hz: going up from Physics Mode's 800 Hz it moves about 2 octaves in the first frame.

**Fix** (new setting `1. General / Steady occlusion`, on; off = 0.8.0 behaviour for comparison; applies live)
- While a path calculation is running, Physics Mode keeps the score of the last *finished* calculation (per calculator; forgotten when the game resets the calculator). No more flip-flop between "clear" and "fully blocked".
- A filter of a sound that is already audibly playing is no longer set "immediately"; it glides. New, silent or just-started sounds (first 50 ms) and the one-shot sounds of the game's multi-source occluder still get their first value at once, so a sound never starts with the wrong filter.
- Filters of game sounds (including SEB gunshot sources) glide in octaves instead of linearly in Hz: exponential with 50 ms, at most 16 octaves per second (0.4 octaves per frame at 40 FPS, 800 Hz -> 22 kHz in about 0.3 s). The Q factor glides as in the game. The volume keeps the game's own per-frame ramp.
- New diagnostic line every 30 s: `Steady occlusion: N of M path scores held from the last finished calculation, ... filter jumps of playing sounds turned into glides, ... set at once`.
- Tests (`Materialklang-KlangTest`, section 13): the recalculation cycle from the log (0.8.0: 20 jumps 0 <-> 1 in 40 updates, up to 15 dB per update; 0.8.1: none), reset handling, the "set at once" rule, the octave glide (limits, time to target), a simulated walk behind a wall and back (0.8.0: up to 2.0 octaves in one frame; 0.8.1: at most 0.4), no allocations.

## 0.8.0-beta

Foliage attenuation. The plugin reports the numeric version 0.8.0.

**New: forest** (F12 section `12. Forest`; see the README section "Forest (foliage attenuation)")
- Sound that travels through dense forest now loses level and highs, following the foliage table of **ISO 9613-2:1996, annex A**: 0.02 dB/m at 63 Hz rising to 0.12 dB/m at 8 kHz (fixed 0-3 dB for 10-20 m), at most 200 m counted. The table values were checked against the CadnaA documentation, not the standard itself (README, "Verification status").
- The trees come from the map: the plugin finds the tree objects (`pine`, `birch`, `tree`, `ash` = 1, bushes 0.5 / 0.25; billboards, stumps, ferns, grass 0) once at raid start and counts them in a 10 m grid (about 23,600 on Woods). Terrain trees are not used because the six forest maps have 0 of them.
- Per sound the line between source and listener is followed through the grid (up to 400 m, not when both are indoors); the metres of dense foliage give a level reduction (500 Hz band, e.g. 100 m = -5 dB) and a low-pass (the frequency where air absorption plus the foliage slope above 500 Hz reach 3 dB; e.g. 4.4 kHz -> 1.6 kHz for 100 m of forest at 100 m distance). Never louder than without the forest. Works for game sounds and for SEB gunshots.
- New settings: `Enabled` (on), `Strength` (1.0), `Density threshold (trees per 100 m2)` (3), `Show forest in sound log` (on; sound log lines get `forest=48 m -2.1 dB lp=5.2 kHz`). All apply live; switching `Enabled` off and on rescans the map.
- Cost: one scan at raid start (a single `FindObjectsOfTypeAll` call, then 2 ms per frame), about 1.5 µs per sound and trace, results cached per source; `[Forest]` lines in the log report the tree count and the scan time, and every 30 s the mean cost.
- Tests (`Materialklang-KlangTest`, section 12): table, limits, interpolation, grid tracing, name classification, cost and memory.

## 0.7.0-beta

Weather-based air absorption. The plugin reports the numeric version 0.7.0.

**New: air absorption from the raid weather** (F12 section `8. Air Absorption`; see the README section "Air absorption")
- The air low-pass is now calculated with **ISO 9613-1:1993** from the raid's **temperature** and **air pressure** and an **estimated humidity**, instead of the fixed curve (12 kHz at 19 m, falling with d^(-2/3)). The cut-off is the frequency at which the air absorbs 3 dB over the distance; the `Air clarity factor` and the 2.5 kHz floor still apply. At 20 °C / 70 % the result stays within about 20 % of the old curve.
- The weather is read at raid start and every 30 s: temperature, rain, fog and cloudiness from the game's weather curve, pressure from the server's weather nodes (SPT: mmHg). The game has no humidity, so it is estimated: rain 95-100 %, fog 80-100 %, otherwise `Base humidity (%)` (default 60) plus up to 10 points for clouds. These rules are assumptions, documented in the README.
- New settings: `Use raid weather (ISO 9613-1)` (on; off = the old fixed curve), `Base humidity (%)` (60), `Show weather in log` (on: `[Weather] ...` lines in the BepInEx log and the sound log header at raid start and on noticeable changes). All apply live and work with the live-tuning file.
- A table "cut-off versus distance" is rebuilt only when the weather or a setting changes (about 0.4 ms); each sound costs one lookup.
- Tests (`Materialklang-KlangTest`, section 11): ISO formula against reference table values, humidity estimate, pressure units, cut-off versus distance, old against new at 20 °C / 70 %, table accuracy and cost.

## 0.6.0-beta

Developer/tuning tools and three physics fixes. The plugin reports the numeric version 0.6.0.

**New: sound log and live tuning** (F12 section `11. Sound log & live tuning`, both off by default; see the README section "Sound log and live tuning")
- **Sound log:** one line per processed sound within a radius (default 60 m) in `BepInEx/Materialklang-Logs/<date_time>-<map>.log`, one file per raid, last 10 kept, 50 MB cap. Each line: real time and game time, kind/clip/source/owner, distance, path (direct / through walls / diffraction / reflection / via doors / through wall between rooms) with the three scores, the obstacles hit (material group, thickness, dB per piece, collider name), total volume and low-pass, Physics Mode or sliders, timbre and air values, SEB directivity and air values, rooms and reverb zone. Rate-limited per source (default 4 lines/s). Written by a background thread; the game thread only fills a recycled record (no allocation per sound; checked offline). Off = one boolean check.
- **Mark odd sound** key (unbound by default): writes `=== MARK n ===` with time and position into the log.
- **Live tuning:** `BepInEx/config/kaan.materialklang.live.json` with overrides `"Section/Key": value` (exact F12 names) is checked once per second; changed values are written into the settings (and the cfg) without a restart, each one logged (`[Live] key: old -> new`, also in the sound log). `"reload": true` applies everything again. Helper script `materialklang-live.sh`.
- Almost all settings are now read live, including some that used to need the next raid: `Clear-path threshold` (both), door/window values, `Only largest box per room`, Timbre `Enabled`. Every change from F12 or from the file also rebuilds the cached door/wall values and makes the game recalculate all sound occlusion. Still restart-only: `3. Bot Hearing (SAIN) / Mode` and the four `10. Advanced (rays)` settings.

**Fixes**
- **Air absorption slider renamed**: `Humidity` is now `Air clarity factor` with a correct description. The old text said "below 1 = duller (damp air/fog)", which is the wrong way round: by ISO 9613-1 dry air absorbs highs more (8 kHz at 20 °C: ~217 dB/km at 20 % humidity, ~59 dB/km at 100 %). The factor itself works exactly as before and your value is carried over automatically.
- **Snow is its own material group** (`2. 15 Snow`, `Density 15 Snow`, `Solid up to 15 Snow`): before, snow shared group 03 with gravel at 1600 kg/m³ (far too heavy). New defaults 300 kg/m³ and 3 m. Group 03 is now `Gravel, Pebbles` and keeps your values (renamed sections/keys are carried over); snow starts with the slider values of the old shared group, but with the new density. In Physics Mode snow is therefore much quieter to hear through than before (1 m of snow: 51 dB instead of 65 → 60 dB).
- **Coincidence correction for thick heavy walls** (Physics Mode; new settings `Coincidence correction` (on) and `Coincidence loss factor` (0.1)): above its coincidence frequency a thick concrete/stone, thick metal or thick wood piece insulates less than the plain mass law (Sharp's three-region method, README "Coincidence correction"). Typical at 250 Hz: 10 cm concrete −11 dB, 20 cm −8 dB, 1 m about −1 dB. Thin groups (thin wood/metal, glass, door leaf, soil, gravel, snow, soft, body) and any piece with `fc ≥ 2 × reference frequency` are unchanged. The loss factor is a fitted value, not a literature value.

**Other**
- New offline tests `Materialklang-KlangTest` (JSON overrides to cfg keys, rate limiter, line format, writer/rotation/size cap, allocation per entry, acoustics) and extended `Materialklang-MigrationTest` (0.5.0 -> 0.6.0 carry-over).

## 0.5.0-beta

First public release (**beta**: listened to in the F12 menu, not yet checked in raids). The plugin itself still reports the numeric version 0.5.0 (BepInEx needs a plain version number).

- **All labels are now English**: F12 section names, setting names, tooltips and log lines. Version 0.4.0 (never published) used German names.
- **Settings carry over automatically.** On the first start, values from the old German entries in `kaan.materialklang.cfg` are copied to the new English entries. The old entries are only read and stay in the file, so going back to 0.4.0 works. New entries that already exist are never overwritten. The log shows "Settings: N values carried over ...". Checked offline against a copy of a real 0.4.0 file: all 109 values arrived unchanged.
- Bot hearing `Mode` values renamed: `Aus` / `NurLeiser` / `Ersetzen` are now `Off` / `QuieterOnly` / `Replace` (converted by the migration).
- Log lines are English (`[Diagnostics]`, `[Reverb]` instead of `[Diagnose]`, `[Hall]`).
- Short note in the tooltips of settings that are ignored while Physics Mode is on (`Clear-path threshold` in section 1, `Surcharge`, Timbre).
- Build: references to SEB and SAIN now go through the game folder setting like every other mod of the pack; installing into the game only happens with `-p:Installieren=true` (`./build.sh --install`).

## 0.4.0 (unpublished)

- Physics Mode with the mass law, loudness factor 0.2 after a listening test.
- Doors and windows by material, walls between rooms, timbre, weapon directivity and air absorption (via SEB), reverb zones per room (Meta XR Audio), optional bot hearing (SAIN).
