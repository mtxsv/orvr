# 04 — Known limitations and hardware-test checklist

Honest status: the VR library, the engine patch, and the full simulator build
are verified by compilation plus 29 automated tests and the 212 upstream unit
tests. **No headset was available in this environment**, so anything marked
`[HW]` below is implemented against the real OpenXR/D3D11 APIs but awaits
on-headset confirmation. Nothing marked `[HW]` is claimed as working.

## Genuine limitations (by design, v1)

1. **Culling uses the center (mono) camera.** Per-eye culling would require
   preparing the frame twice on the updater thread. A +12° FOV margin is added
   while VR is active so fringe pixels are over- rather than under-rendered.
   Residual risk: tiny near-geometry pops at extreme eye angles. `[HW]`
2. **Shadows render once per frame** from the center view. Correct for the sun;
   stereo disparity in shadow texels is negligible. `[HW]`
3. **2D cab overlay draws identically in both eyes.** The 3D world behind it is
   true stereo; the panel itself has no positional parallax (it behaves like a
   fixed panel). Look rotation and zoom work normally.
4. **Controller pointer is head-anchored (angular).** No world calibration is
   required: the hand direction relative to the head maps through the FOV to
   screen space (GearVR-style). Full 6DOF world-anchored rays need a
   head-to-world calibration pass (future work).
5. **UI text entry needs the physical keyboard.** Menus, buttons, sliders, and
   lists work with the controller; typing falls back to desktop.
6. **No in-game VR options menu yet.** Settings are edited in `OpenRails.ini`
   (same store as all options) and take effect on the next frame/activity.
7. **MSAA is off in eye targets** (shared D3D11 targets + OpenXR swapchains are
   resolved by the runtime). Use `VRRenderScalePercent` (supersampling) instead.
8. **Performance is ~2x scene cost + mirror composite** (two full-size eye
   passes). The mono pass is skipped in VR mode; the desktop mirror is a cheap
   SBS composite.
9. **The native shim is not built here** (no OpenXR SDK/MSVC in this
   environment). Build it per `Integration/shim/CMakeLists.txt`. Without it,
   everything degrades to desktop-fallback SBS — by design, never a crash.
10. **Adapters with a different GPU than the runtime** print a debug note and
    continue (shared-handle copy path). Prefer running the sim on the GPU the
    runtime uses. `[HW]`

## Hardware-test checklist (for the first headset session)

- [ ] HMD image present, stable, correctly oriented; no flicker or tearing.
- [ ] Left/right eyes not swapped; IPD feels correct; no eye strain.
- [ ] Head rotation 1:1 in cab and external cameras; positional lean clamped.
- [ ] Recenter (`Alt+Shift+R`) works from cab and external views.
- [ ] Camera switching (1–8) never jumps or accumulates offset.
- [ ] Controllers appear; trigger/squeeze/stick/buttons all respond.
- [ ] Buttons, levers, throttle/brake/reverser, notched and rotary controls.
- [ ] Exterior guard: no cab activation from external cameras.
- [ ] UI windows operable; keyboard fallback for text.
- [ ] Disconnect/reconnect headset and controllers mid-session: graceful
      fallback, no crash, recovery on reconnect.
- [ ] Frame pacing acceptable; adjust `VRRenderScalePercent` as needed.

## Out of scope (not attempted)

- Non-Windows platforms (Open Rails targets `net6-windows`; the shim and the
  D3D11 binding are Windows-only, same as the engine).
- 2D-cab positional parallax, full 6DOF cab reach, hand tracking without
  controllers, VR menus rendered inside the HMD as 3D overlays.
