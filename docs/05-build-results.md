# 05 — Build and test results (2026-10-13, no headset)

Environment: Windows 10.0.22631 x64, .NET 8.0.425 SDK (no .NET 6 SDK;
`net6-windows` builds via targeting packs + `EnableWindowsTargeting`),
upstream `openrails/openrails` master shallow clone, MonoGame 3.8.1.303.

## Orts.VR library + tests (this repo)

```powershell
dotnet build C:\orvr\Source\Orts.VR\Orts.VR.csproj
# -> Orts.VR.dll, 0 warnings, 0 errors

dotnet run --project C:\orvr\Source\Orts.VR.Tests\Orts.VR.Tests.csproj
# -> RESULT 29 passed, 0 failed
```

Covers: eye offsets/parallax (incl. rotated base rigidity), symmetric +
asymmetric projections (valid + degenerate inputs), recenter, positional
clamp, tracking toggles, camera-switch reset, style defaults, OpenXR
projection application, rect picking, ray-sphere, grab (no-jump, direction,
notches, clamp, rebase, release), cab/UI guards, haptics ranges, settings
defaults/validation/propagation, OpenXR host fallback (missing loader: safe
`RuntimeMissing`, idempotent shutdown).

## Patched Open Rails build (scratch checkout, patch script applied)

```powershell
powershell -ExecutionPolicy Bypass -File C:\orvr\Integration\Apply-VR-Patch.ps1 `
  -Checkout C:\OR-Source -OrvrRepo C:\orvr

dotnet build C:\OR-Source\Source\RunActivity\RunActivity.csproj
# -> 0 errors; 62 warnings, all pre-existing in upstream files
#    (unused fields, CA2200 rethrows, etc.). Zero warnings in any
#    VR-touched file (VRSystem, VRNative, UserInput, RenderFrame,
#    RenderProcess, Viewer, UserSettings, InputSettings, UserCommand).
```

Also verified: `dotnet restore --force` picks up the new `Orts.VR`
`ProjectReference`; the `Viewer3D\VR\VRSystem.cs` + `VRNative.cs` files
compile inside the real `RunActivity` assembly against the real engine APIs
(`Camera.XnaView`, `RenderFrame` additions, `ControlMap`, `CVCWithFrames`,
`UserCommand`, `UserSettings`, SharpDX via MonoGame).

## Upstream regression tests (patched tree)

```powershell
dotnet test C:\OR-Source\Source\Tests\Tests.csproj
# -> Passed: 212, Failed: 0, Skipped: 0
```

The `UserCommand` enum append, `InputSettings` default, and `UserSettings`
additions break no existing tests.

## Not run here (no hardware)

- On-headset session, controllers, HMD submit path (`orvr_openxr_shim.dll`
  needs the OpenXR SDK + MSVC to build; see `Integration/shim/`).
- Runtime packaging (`Build.cmd` full modes) and in-sim manual runs.
- See `docs/04-limitations.md` for the honest boundary and the HW checklist.
