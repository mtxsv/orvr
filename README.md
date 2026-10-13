# orvr — Open Rails VR Mod (OpenXR, MonoGame/D3D11)

VR modification for [Open Rails](https://github.com/openrails/openrails) (`master`, `net6-windows`, MonoGame 3.8.1 WindowsDX / DirectX 11).

Upstream is **not** vendored here. Clone it separately:

```powershell
git clone --depth 1 https://github.com/openrails/openrails.git C:\OR-Source
```

This repo contains the VR overlay:

- `Source/Orts.VR/` — VR library (OpenXR host w/ D3D11 binding + desktop fallback, stereo math, camera rig, controller + cab/UI interaction, settings). Drop-in project referenced by `RunActivity`.
- `Source/Orts.VR.Tests/` — automated tests (stereo math, head pose, camera switching, raycast, slider mapping, config).
- `docs/` — investigation notes, architecture, enable/test instructions, limitations.
- `scripts/` — integration helpers (add project reference + hook points, verify build).

Status: in development. Desktop-fallback stereo + unit tests are verified by build; real-headset validation is pending (documented honestly in `docs/`).

## Quick links

- `docs/01-investigation.md` — engine findings (renderer, cameras, cab, settings, input)
- `docs/02-architecture.md` — VR design (OpenXR session, two-pass stereo, camera rig, controllers)
- `docs/03-enable-and-test.md` — enable VR, test cab/external cameras, controllers, UI
- `docs/04-limitations.md` — honest limits + hardware-test checklist
