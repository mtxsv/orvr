# 02 — VR architecture (OpenXR + MonoGame D3D11)

## Goals

Genuine stereoscopic VR (not a flat mirror, not duplicated mono): the world is rendered twice per frame with independent per-eye view/projection matrices derived from the current Open Rails camera + tracked HMD pose + eye offsets. Desktop mode is fully preserved when VR is disabled or no runtime is present.

## Why this integrates without a renderer rewrite

- Open Rails (`net6-windows`, MonoGame 3.8.1 WindowsDX) renders through `RenderProcess.Draw()` → `RenderFrame.Draw()`, with the camera installed per frame by `Camera.PrepareFrame(frame, elapsedTime)` (`Source/RunActivity/Viewer3D/...`, see `docs/01-investigation.md`).
- Stereo = run that existing path **twice** (left eye, right eye) with per-eye view/projection, rendering into per-eye targets; submit to the OpenXR swapchain; keep one additional desktop mirror draw using the existing mono path. No changes to materials, shadows, scenery, or train rendering.
- The in-process D3D11 device (MonoGame WindowsDX; `Orts.Common/SystemInfo.cs` already P/Invokes `D3D11CreateDevice`) satisfies the OpenXR `XR_KHR_D3D11_ENABLE` graphics binding: the session is created against the same adapter/LUID, and swapchain images are shared — no copy through system memory.

## Components (`Source/Orts.VR`, `netstandard2.0`, zero external dependencies)

Math uses `System.Numerics` only. The layout of `Matrix4x4` (M11..M44) matches XNA/MonoGame `Matrix`, so the integration layer passes `float[16]` / decomposed eye view-projection pairs without referencing MonoGame from this library.

1. `VRStereoMath` — pure functions:
   - `EyeView(baseView, headPose, eyeOffset)` — base camera view × inverse(head) × eye translation. Eye offsets are ±IPD/2 along camera-right in head space.
   - `ProjectionFromFov(fovLeft, fovRight, fovUp, fovDown, near, far)` — asymmetric OpenXR-style frustum; plus `SymmetricProjection(fovY, aspect, near, far)` fallback (per-eye aspect = half-width aspect).
   - No allocations on the hot path (structs, `in` params).
2. `VRHeadPose` — HMD position (meters, head-local) + orientation (quaternion); `Recenter()` stores the yaw/position offset subtracted from all later poses; `ClampPosition(maxRadius, maxUp, maxDown)` enforces configurable movement limits; `ApplyTo(baseView)` returns the head-relative view delta. Handles the MSTS/XNA Z-negation convention at the boundary (documented in code), never inside the math core.
3. `VRCameraRig` — owns one `VRHeadPose` + per-eye parameters + current `CameraStyle` (Cab / ThreeDimCab / Passenger / External):
   - Cab/3D-cab: head offset applied in **cab-local space** (after the car transform), so looking around never moves the train; rotation comes 1:1 from HMD, position clamped (default 0.35 m radius).
   - External: HMD yaw/pitch applied relative to the external camera's base orientation; base orbit/follow/zoom untouched; small positional head offset (default 0.5 m clamp) so the camera doesn't fly away.
   - `NotifyCameraSwitched()` clears transient deltas (no jumps, no double transforms, no accumulation). Keyboard/mouse camera control keeps working — HMD is an additive delta, and recentering is exposed as a bindable command.
4. `VROpenXRHost` — real OpenXR session state machine via direct `openxr_loader.dll` P/Invoke (`xrCreateInstance`, `xrGetSystem`, D3D11 binding, `xrCreateSession`, reference spaces `LOCAL`/`VIEW`, frame loop `xrWaitFrame/xrBeginFrame/xrEndFrame`, swapchain enumerate/acquire/wait/release):
   - If the loader/runtime is missing, `Status = RuntimeMissing` and the host runs in **desktop-fallback stereo** (side-by-side preview driven by mouse-emulated look, explicitly labeled fallback — never presented as tracking).
   - Errors, headset disconnects, and session loss map to `Status` + `LastError` and fall back to mono desktop rendering. `Shutdown()` releases everything in reverse order.
5. `VRControllerInteraction` — OpenXR action states (`aim pose`, `trigger`, `squeeze`, `thumbstick`, buttons per hand; left/right swappable):
   - One ray per active hand from controller aim pose. 3D-cab: ray vs. control bounds in cab space. 2D-cab/popups: controller ray projected to a **virtual screen cursor** that reuses the existing `ICabViewMouseControlRenderer.IsMouseWithin/DestinationRectangleGet/HandleUserInput` and `WindowManager` hit-testing — the same code path as the mouse, so behavior (press/toggle/hold, detents, limits) is identical.
   - Grab semantics: on trigger press store the grab offset (control value + controller anchor) so there is no jump; while held map controller motion to the control's `[MinValue, MaxValue]` fraction (`GetRangeFraction` convention), snapping to notches where the control defines them; on release commit through the normal command path. External-camera mode never routes rays to cab controls (train-exterior guard).
6. `VRSettings` — POCO following `UserSettings` conventions. The engine settings
   store persists bool/int/string only, so floats use integer conventions
   (`VRRenderScalePercent`, `VRIPDMillimeters`, `VRHeadRadiusMM`, ... — see
   `Integration/RunActivity/VR/UserSettings.VR.inc.cs`): load/save through the
   same property mechanism; all defaults keep desktop behavior unchanged.

## Frame flow (VR active, runtime present)

1. `xrWaitFrame` (predicted display time) → locate HMD + controller poses for that time.
2. Left eye: `rig.EyeView(Eye.Left)` + `ProjectionFromFov(...)` → `Camera.PrepareFrame` override → existing `RenderFrame.Draw` into left swapchain image. Same for right eye. Same simulation state for both eyes (poses sampled once per frame).
3. `xrEndFrame` with both layers; desktop window keeps a mono mirror via the untouched path.
4. Controllers → `VRControllerInteraction.Update` → virtual cursor/commands → existing `UserInput`/cab/UI paths.

## What is verified without a headset

- `Orts.VR` builds with only the .NET SDK (no NuGet needed).
- `Orts.VR.Tests` (dependency-free console harness) covers stereo math, head-pose/recenter/clamp, camera-switch reset, ray-vs-rect picking, slider/notch mapping, and settings defaults.
- Desktop-fallback stereo path compiles and runs (side-by-side, mouse-look).
- Full in-engine two-pass submission + controller actuation require a headset + OpenXR runtime; those paths are implemented against the real API but marked **needs-hardware-test** in `docs/04-limitations.md`.
