# Changelog

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
