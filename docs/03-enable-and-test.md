# 03 — Enable VR, configure, and test

All commands run from the orvr repo unless noted. Build requirements: .NET 8
SDK (or 6+), Windows x64. A headset + OpenXR runtime (SteamVR or any conformant
runtime) is needed only for on-headset testing; everything else works without one.

## 1. Apply the patch to an Open Rails checkout

```powershell
git clone --depth 1 https://github.com/openrails/openrails.git C:\OR-Source
powershell -ExecutionPolicy Bypass -File C:\orvr\Integration\Apply-VR-Patch.ps1 `
  -Checkout C:\OR-Source `
  -OrvrRepo C:\orvr
```

The script is idempotent (safe to re-run) and writes `*.orvr-bak` backups
before the first edit of each file. It copies `VRSystem.cs`/`VRNative.cs`,
adds the `Orts.VR` project reference, settings, the `VRRecenter` key command,
the `UserInput` pointer overlay, the `RenderFrame` eye rendering, and the
`RenderProcess`/`Viewer` hooks. See `Integration/RunActivity/VR/PATCH.md`.

## 2. Build

```powershell
# VR library + unit tests (no headset, no Open Rails needed)
dotnet build C:\orvr\Source\Orts.VR\Orts.VR.csproj
dotnet run --project C:\orvr\Source\Orts.VR.Tests\Orts.VR.Tests.csproj

# Full simulator with VR integration
dotnet build C:\OR-Source\Source\RunActivity\RunActivity.csproj
```

## 3. Build the native shim (for headset use)

Requires the OpenXR SDK and MSVC 2022+ (see `Integration/shim/CMakeLists.txt`):

```powershell
cmake -S C:\orvr\Integration\shim -B build -DOpenXR_ROOT=C:\OpenXR-SDK
cmake --build build --config Release
copy build\bin\orvr_openxr_shim.dll C:\OR-Source\Program\
```

Without the shim (or without a runtime), the sim runs in desktop-fallback
stereo — never an error, never a crash.

## 4. Enable VR

VR settings live in the standard settings store (`OpenRails.ini` next to the
program, same mechanism as every other option). Keys (defaults = desktop):

| Key | Default | Meaning |
|---|---|---|
| `VREnabled` | `false` | master switch |
| `VRRenderScalePercent` | `100` | supersampling % |
| `VRIPDMillimeters` | `63` | eye separation |
| `VRHeadTracking` | `true` | rotation tracking |
| `VRPositionalTracking` | `true` | lean/positional tracking |
| `VRHeadRadiusMM` | `350` | horizontal lean limit |
| `VRHeadMaxUpMM` / `VRHeadMaxDownMM` | `300` | vertical limits |
| `VRDominantHandRight` | `true` | pointer hand |
| `VRHaptics` | `true` | haptic pulses |

Set `VREnabled=true` and start any activity. The desktop window shows a
side-by-side stereo mirror; with a runtime + shim, the HMD shows the two eyes.

Recenter key: `Alt+Shift+R` (`VRRecenter`, rebindable in Options > Keyboard).
Always press it once after putting the headset on.

## 5. Test the cabin camera

1. Start a 3D-cab locomotive, camera `1` (cab). Desktop mirror should show the
   world in side-by-side stereo.
2. With HMD: look around — rotation 1:1; lean limited to ~35 cm. The train
   must not move; keyboard/mouse camera control keeps working.
3. Switch 2D/3D cab (`Alt+1` toggles 3D cab where available): view must cut
   cleanly with no jump or leftover offset.
4. `Ctrl+8` (camera reset) must restore the default viewpoint.

## 6. Test external cameras

Cycle cameras `2` (tracking), `3`, `4` (trackside), `5` (passenger), `6`
(brakeman), `8` (free roam). In each: look around naturally; orbit/follow/zoom
behave as on desktop; switching cameras never accumulates offsets (each
switch resets the head delta — verify by recentering, switching twice, and
confirming the view matches the desktop framing plus head look).

## 7. Test controllers

1. Aim the dominant hand: a highlight follows the targeted cab control and a
   dot mirrors it on the desktop window.
2. Trigger press on buttons/switches: press/toggle/hold behave exactly as with
   the mouse (same command path).
3. Squeeze + move on throttle/brake/reverser levers: no jump on grab; motion
   maps across the full range; notched controls snap to detents; release is
   clean (no stuck values).
4. Thumbstick up/down nudges a grabbed slider.
5. In external cameras, controller rays must NOT operate cab controls
   (exterior guard); menus/popups still accept the pointer from any camera.
6. Disconnect a controller (or start with none): pointer lifts, mouse works,
   no errors.

## 8. Test UI in VR

Open in-game windows (`F5` HUD/driving, `F4` track monitor, `Esc` menu):
activate buttons with the trigger, adjust sliders with grab or thumbstick,
navigate lists. Text entry falls back to the physical keyboard (desktop
mirror shows the same window).
