# orvr — Open Rails VR Mod (OpenXR, MonoGame/D3D11)

VR modification for [Open Rails](https://github.com/openrails/openrails) (`master`, `net6-windows`, MonoGame 3.8.1 WindowsDX / DirectX 11).

Upstream is **not** vendored here. Clone it separately:

```powershell
git clone --depth 1 https://github.com/openrails/openrails.git C:\OR-Source
```

This repo contains the VR overlay:

- `Source/Orts.VR/` — VR library (OpenXR host w/ D3D11 binding + desktop fallback, stereo math, camera rig, controller + cab/UI interaction, settings). Drop-in project referenced by `RunActivity`.
- `Source/Orts.VR.Tests/` — automated tests (stereo math, head pose, camera switching, raycast, slider mapping, config).
- `Integration/` — engine-side adapter (`VRSystem`, `VRNative`), additive patch blocks, `Apply-VR-Patch.ps1`, exact `PATCH.md`, and the native `shim/` (OpenXR session/swapchain/actions) sources.
- `docs/` — investigation notes, architecture, enable/test instructions, limitations.
- `scripts/` — integration helpers (add project reference + hook points, verify build).

Status: library + patched-simulator build verified (0 errors), 29/29 VR tests
and 212/212 upstream tests green. Real-headset validation is pending and
marked honestly — see `docs/04-limitations.md` and `docs/05-build-results.md`.

## Quick links

- `docs/01-investigation.md` — engine findings (renderer, cameras, cab, settings, input)
- `docs/02-architecture.md` — VR design (OpenXR session, two-pass stereo, camera rig, controllers)
- `docs/03-enable-and-test.md` — enable VR, test cab/external cameras, controllers, UI
- `docs/04-limitations.md` — honest limits + hardware-test checklist
- `docs/05-build-results.md` — exact build/test commands and results
- `Integration/RunActivity/VR/PATCH.md` — every engine edit, file by file

## Build & test in 3 commands

```powershell
dotnet build Source\Orts.VR\Orts.VR.csproj
dotnet run --project Source\Orts.VR.Tests\Orts.VR.Tests.csproj
powershell -ExecutionPolicy Bypass -File Integration\Apply-VR-Patch.ps1 -Checkout C:\OR-Source -OrvrRepo (Get-Location)
dotnet build C:\OR-Source\Source\RunActivity\RunActivity.csproj
```
