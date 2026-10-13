# 06 — Installing ORVR into an ORNYMG game folder (player guide)

No build needed. You only need the files listed below — either take them from
a release pack or have someone with the SDK build them once
(`docs/05-build-results.md`).

## What this installs

Your game: **OR NewYear MG (ORNYMG Rev. 174)**, .NET Framework 4.7.2, e.g.
`C:\Users\MetaXSV\Desktop\OR_vR`. The mod replaces a small set of program
files with VR-capable builds from the matching source
(`Csantucci/openrails` branch `OR_NewYear_MG_NoWD`) plus two new DLLs.
Routes, trains, content, ReShade, and settings are untouched.

| File(s) | Change |
|---|---|
| `RunActivity.exe`, `RunActivity32.exe` (+`.pdb`) | stereo two-pass rendering + controller interaction hooks |
| `Orts.Settings.dll` | new `VR*` settings keys |
| `Orts.Common.dll` | new `VRRecenter` key command |
| `Orts.Formats.Msts.dll`, `Orts.Simulation.dll`, other `Orts.*.dll`, `Menu.exe`, etc. | refreshed to the same matching source build so all assemblies stay mutually consistent (same code, VR patch only where listed above) |
| `RunActivity.exe.config`, `RunActivity32.exe.config` | + binding redirects for the new dependencies |
| `Orts.VR.dll` (**new**) | VR library (stereo math, OpenXR host, controllers) |
| `System.Resources.Extensions.dll` (**new**) | runtime support library |

Everything replaced is backed up first to `<game>\ORVR-Backup\`.

## Install steps

1. **Close the game completely** (Menu and RunActivity — locked files cannot
   be replaced; the installer will fail on them if running).
2. Copy the files above from the ORVR pack into the game folder,
   backing up the originals to `ORVR-Backup\` first.
3. VR starts **disabled** (normal desktop game). To enable it, set the
   registry value (or run `tools/Enable-ORVR.ps1` from this repo):
   - Key: `HKEY_CURRENT_USER\SOFTWARE\OpenRails\ORTS`
   - `VREnabled` (DWORD) = `1`
   - Optional tuning (DWORD/strings, same key): `VRRenderScalePercent` (100),
     `VRIPDMillimeters` (63), `VRHeadRadiusMM` (350), `VRHeadMaxUpMM`/`VRHeadMaxDownMM`
     (300), `VRHeadTracking`/`VRPositionalTracking`/`VRDominantHandRight`/`VRHaptics` (=1).
   - If your install uses `OpenRails.ini` next to the exe instead of the
     registry, put the same keys there instead.
4. For headset display, also build `Integration/shim` (`orvr_openxr_shim.dll`,
   needs the OpenXR SDK + MSVC) and drop it next to `RunActivity.exe`, with an
   OpenXR runtime installed (e.g. SteamVR). Without it you get the
   side-by-side desktop mirror (still real stereo, no headset).
5. Start the game normally (Menu or `RunActivity.exe -start -explorer …`).
   Press `Alt+Shift+R` once after putting the headset on to recenter.

## Disable / uninstall

- Disable VR but keep the mod: set `VREnabled` = `0` (or run
  `tools/Disable-ORVR.ps1`). The game is then pixel-identical to stock.
- Full uninstall: close the game, copy everything from `ORVR-Backup\` back
  over the game folder, delete the two **new** DLLs (`Orts.VR.dll`,
  `Orts.VR.pdb`, `System.Resources.Extensions.dll`), and remove the `VR*`
  registry values.

## Troubleshooting

- **Game crashes on start with no log**: you launched with files still locked
  or mixed old/new DLLs — reinstall the full consistent set (step 2), never a
  subset. (The `TypeLoadException: TelepoleDataFile` error specifically means
  a stale `Orts.Formats.Msts.dll`.)
- **`OpenRailsLog.txt`** (Desktop by default) always tells how far loading got;
  content problems (missing stock, bad path/consist) look the same with or
  without the mod — compare against the `ORVR-Backup` binaries if in doubt.
- ReShade stays installed and untouched; disable its overlay if it steals the
  HMD output.
- Reference test combination used during development (known good on the
  CGL_V22N route pack): path `Aceros Alonso - Mango.pat` + consist
  `RENFE 446 Original.con`, `12:00`, season `1`, weather `0`.
