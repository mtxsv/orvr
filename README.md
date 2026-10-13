# orvr — Open Rails VR Mod (OpenXR, MonoGame/D3D11)

VR modification for Open Rails. Two supported codebases:

- **OR NewYear MG** (`Csantucci/openrails` branch `OR_NewYear_MG_NoWD`,
  .NET Framework 4.7.2) — the mod is **installed and tested in a real game
  folder** (see INSTALL below).
- Upstream `openrails/openrails` master (`net6-windows`) — patch + build
  verified (see BUILD below).

Upstream sources are **not** vendored here.

## INSTALL (players — no build needed)

1. Close the game completely (locked files cannot be replaced).
2. Copy the mod files into the game folder (e.g.
   `C:\Users\MetaXSV\Desktop\OR_vR`), backing up originals to
   `ORVR-Backup\` first. Full file list: `docs/06-install-ORNYMG.md`.
   The set must be installed **together** (patched `RunActivity.exe` /
   `Orts.Settings.dll` / `Orts.Common.dll` / consistent `Orts.*.dll` set +
   new `Orts.VR.dll`, `System.Resources.Extensions.dll`, updated configs) —
   a partial mix causes `TypeLoadException`s.
3. Enable VR: run `tools/Enable-ORVR.ps1` (sets `VREnabled=1` in
   `HKCU\SOFTWARE\OpenRails\ORTS`; use `tools/Disable-ORVR.ps1` to go back
   to stock desktop). VR starts disabled, so the game is unchanged until you
   opt in. Details + tuning keys + uninstall: `docs/06-install-ORNYMG.md`.
4. Headset display additionally needs `orvr_openxr_shim.dll` (build
   `Integration/shim` with the OpenXR SDK + MSVC, drop next to
   `RunActivity.exe`) and an OpenXR runtime (e.g. SteamVR). Without it you
   get the side-by-side desktop mirror (real stereo, no headset).
5. Start the game normally. Recenter in the headset: `Alt+Shift+R`.
   Play-tested reference combo: `docs/06-install-ORNYMG.md` (troubleshooting).

In-game proof so far (`docs/07-gametest-ORvR.md`): patched game reaches live
3D with zero errors; with VR on and no headset the SBS stereo composite
renders (`docs/img-vr-sbs-fallback.png`).

## BUILD (developers)

```powershell
# 1. VR library + unit tests (no headset, no Open Rails needed)
dotnet build Source\Orts.VR\Orts.VR.csproj
dotnet run --project Source\Orts.VR.Tests\Orts.VR.Tests.csproj   # 29/29 green

# 2a. Upstream master checkout
git clone --depth 1 https://github.com/openrails/openrails.git C:\OR-Source
powershell -ExecutionPolicy Bypass -File Integration\Apply-VR-Patch.ps1 `
  -Checkout C:\OR-Source -OrvrRepo $PWD
dotnet build C:\OR-Source\Source\RunActivity\RunActivity.csproj

# 2b. ORNYMG checkout (Rev. 174 line, .NET Framework 4.7.2)
git clone --depth 1 --branch OR_NewYear_MG_NoWD https://github.com/Csantucci/openrails.git C:\OR-NYMG
powershell -ExecutionPolicy Bypass -File Integration\Apply-VR-Patch.ps1 `
  -Checkout C:\OR-NYMG -OrvrRepo $PWD -Net472
dotnet build C:\OR-NYMG\Source\RunActivity\RunActivity.csproj -c Release
```

Status: library + both patched-simulator builds verified (0 errors), 29/29 VR
tests and 212/212 upstream unit tests green. Real-headset validation is
pending and marked honestly — see `docs/04-limitations.md` and
`docs/05-build-results.md`.

## Repo layout

- `Source/Orts.VR/` — VR library (OpenXR host w/ D3D11 binding + desktop fallback, stereo math, camera rig, controller + cab/UI interaction, settings).
- `Source/Orts.VR.Tests/` — automated tests.
- `Integration/` — engine adapter (`VRSystem`, `VRNative`), additive patch blocks, `Apply-VR-Patch.ps1` (master + ORNYMG via `-Net472`), exact `PATCH.md`, native `shim/`.
- `tools/` — `Enable-ORVR.ps1` / `Disable-ORVR.ps1` (flip `VREnabled`).
- `docs/` — investigation, architecture, enable/test, limitations, results.

## Quick links

- `docs/01-investigation.md` — engine findings (renderer, cameras, cab, settings, input)
- `docs/02-architecture.md` — VR design (OpenXR session, two-pass stereo, camera rig, controllers)
- `docs/03-enable-and-test.md` — enable VR, test cab/external cameras, controllers, UI
- `docs/04-limitations.md` — honest limits + hardware-test checklist
- `docs/05-build-results.md` — library/upstream build + test commands and results
- `docs/06-install-ORNYMG.md` — player install/uninstall/troubleshooting
- `docs/07-gametest-ORvR.md` — in-game verification evidence
- `Integration/RunActivity/VR/PATCH.md` — every engine edit, file by file
