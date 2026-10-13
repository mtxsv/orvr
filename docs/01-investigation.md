# 01 — Open Rails engine investigation (2026-10-13)

Upstream: `https://github.com/openrails/openrails`, `master`, shallow clone `--depth 1`.
Checkout used for inspection: `C:\Users\MetaXSV\AppData\Local\Temp\opencode\openrails`.

## 1. Version / framework / graphics

- Solution: `Source/ORTS.sln` (VS 17). `Source/Directory.Build.props` sets output to `../Program/`, `win-x64`, `SelfContained=false`, `EnableWindowsTargeting=true`.
- `Source/RunActivity/RunActivity.csproj`: `net6-windows`, `UseWindowsForms=true`, `OutputType=WinExe`.
- Graphics: `MonoGame.Framework.WindowsDX 3.8.1.303` (DirectX 11 path on Windows; `GraphicsProfile.HiDef` preferred, MSAA configurable).
- Build entry: `Build.cmd <unstable|testing|stable>` → `dotnet build` + `dotnet test`. Toolchain here: .NET 8.0.425 SDK only (no .NET 6 SDK/runtime installed); `net6-windows` targeting relies on targeting packs / `EnableWindowsTargeting`.
- `SystemInfo.cs` (`Orts.Common`) P/Invokes `D3D11CreateDevice` and references `SharpDX.DXGI` — confirms a D3D11 device is available in-process, which is what OpenXR's `XR_KHR_D3D11_ENABLE` binding needs.

## 2. Render loop

- `Viewer3D/Processes/Game.cs` (`Orts.Viewer3D.Processes.Game : Microsoft.Xna.Framework.Game`): owns `RenderProcess`, `UpdaterProcess`, `LoaderProcess`, `SoundProcess`, `WebServerProcess`, `HostProcess`. `Update()` → `RenderProcess.Update()`, `Draw()` → `RenderProcess.Draw()`.
- `Viewer3D/Processes/RenderProcess.cs`: creates `GraphicsDeviceManager` (back buffer from `Game.Settings.WindowSize` or fullscreen, `Depth24Stencil8`, MSAA from `AntiAliasing` setting), double-buffers `RenderFrame CurrentFrame/NextFrame`; `BeginDraw()` → `Game.State.BeginRender(CurrentFrame)`; `Draw()` → `CurrentFrame.Draw(GraphicsDevice)`; `EndDraw()` → `Game.State.EndRender`.
- `Viewer3D/RenderFrame.cs`: `RenderPrimitiveSequence` order is `CabOpaque, Sky, WorldOpaque, WorldBlended, Lights, Precipitation, Particles, InteriorOpaque, InteriorBlended, Labels, CabBlended, OverlayOpaque, OverlayBlended`. Camera is set per frame via `Camera.PrepareFrame(frame, elapsedTime)` → `frame.SetCamera(this)`. **Stereo hook point: call `PrepareFrame` + `Draw` twice with per-eye view/projection.**
- `GameForm = (Form)Control.FromHandle(Game.Window.Handle)` — WinForms HWND available for OpenXR D3D11 session creation if needed.

## 3. Cameras (`Viewer3D/Cameras.cs`, ~2880 lines)

- Base `Camera` (abstract): `cameraLocation: WorldLocation`, `xnaView`/`xnaProjection` (`Matrix XnaView/XnaProjection`), `FieldOfView` from `Viewer.Settings.ViewingFOV`, `Style { External, Cab, Passenger, ThreeDimCab }`, `NearPlane` (default 1.0, 0.25 head-out/brakeman, 0.1 inside-3D), `Activate()` (sets `Viewer.Camera`, `PlayerIsInCab` when Cab/ThreeDimCab), `PrepareFrame()` (recomputes `xnaView = GetCameraView()`), `ScreenChanged()` (recomputes projection from aspect + `ViewingDistance`; aspect = `DisplaySize.X/Y` — **must use per-eye aspect in VR**), `XnaLocation()` (tile → XNA space, Z negated).
- `RotatingCamera : Camera`: yaw `RotationYRadians` / pitch `RotationXRadians`, `GetCameraView()` = look-at from `cameraLocation` rotated by pitch then yaw. Mouse rotate on right-button; replay commands recorded (`CameraMouseRotateCommand`, etc.).
- `LookAtCamera : RotatingCamera`: `Matrix.CreateLookAt(camera, target, Up)`.
- `AttachedCamera : RotatingCamera`: bound to `TrainCar attachedCar` + local `attachedLocation`; `UpdateLocation(worldPosition)` and `Update()` transform local → world via `attachedCar.WorldPosition.XNAMatrix` with Z-negation convention; `GetCameraView()` builds look-at in car space then transforms by car matrix. `MoveCar()` (next/prev/first/last car commands).
- `NonTrackingCamera : AttachedCamera`: keyboard/mouse rotate + zoom; base for cab + inside-3D.
- `InsideThreeDimCamera : NonTrackingCamera` (`NearPlane 0.1`): cab/passenger viewpoint list (`viewPointLocation`, `StartViewPointLocation`, `MoveCameraXYZ` with rear-cab axis flip, `Reset()` restores start viewpoint).
- `ThreeDimCabCamera : InsideThreeDimCamera` (`Style.ThreeDimCab`): available when player loco has front/rear 3D cab; `SetCameraCar` loads `CabViewpoints[ActViewPoint]`; `ChangeCab` handles front/rear switch (yaw += π).
- `CabCamera : NonTrackingCamera` (`Style.Cab`): legacy **2D cab** — cab is a screen-space `SpriteBatch` overlay (`Viewer.CabWidthPixels/HeightPixels`, offsets, letterbox), external view direction derived from pan offsets. VR positional tracking does not map 1:1 here (documented limit; VR still provides look rotation + stereo for the 3D world behind the cab overlay).
- `TrackingCamera`, `BrakemanCamera`, `HeadOutCamera`, `PassengerCamera`, `FreeRoamCamera`, `TracksideCamera/SpecialTracksideCamera` cover external modes. **VR approach: head pose applied as a delta on top of the base camera's `GetCameraView()` result, in camera-local space, cleared on camera switch (no accumulation).**

## 4. Cab controls (`Viewer3D/RollingStock/MSTSLocomotiveViewer.cs`)

- `CabViewControlRenderer : RenderPrimitive` base; `ICabViewMouseControlRenderer { IsMouseWithin(), HandleUserInput(), GetControlName(), ControlLabel, DestinationRectangleGet(), isMouseControl() }`.
- `CabViewDiscreteRenderer : CabViewControlRenderer, ICabViewMouseControlRenderer` (buttons/switches), `CabViewDialRenderer`, `CabViewGaugeRenderer`, `CabViewDigitalRenderer`, `CabViewAnimationsRenderer`, DMI renderer. 2D-cab controls are **screen rectangles** (`DestinationRectangleGet`), so a VR controller ray is converted to a virtual screen cursor → reuse `IsMouseWithin/HandleUserInput`. 3D-cab controls reuse the same locomotive data path (`Locomotive.GetDataOf(Control)`, `GetRangeFraction()` 0..1 mapping with min/max clamp) — grab/drag maps controller motion to that fraction, preserving notches/limits.
- Real actuation goes through existing commands (`Viewer.Log` command pattern), never visual-only.

## 5. Settings (`Orts.Settings/UserSettings.cs`)

- `UserSettings : PropertySettingsBase`; auto-properties of `string/int/bool/string[]/int[]` with `[Default(...)]` auto-load/save. VR settings follow the same convention (`bool VREnabled`, `float VRRenderScale`, etc. — new properties only, existing desktop behavior untouched).

## 6. Input/UI

- `Viewer3D/UserInput.cs` (+ `UserInputExternal`, `UserInputRailDriver`): `UserCommand` set, `IsDown/IsPressed/IsReleased`, mouse state. VR controller actions merge here as additional `UserCommand` sources, never replacing keyboard/mouse.
- UI: `Viewer3D/Popups/WindowManager.cs` + `Window*.cs` (custom in-game windows, `mouseActiveWindow.HandleUserInput()`), plus WinForms (`MapForm`, debug forms) and an embedded web server (`Viewer3D/WebServices/`). VR reuses `WindowManager` hit-testing with the controller virtual cursor; text input falls back to the desktop keyboard mirror.

## 7. Integration decision

- **No renderer rewrite.** Stereo = two `PrepareFrame`/`Draw` passes per frame with per-eye view (base view × head delta × eye offset) and per-eye projection (from OpenXR FOV or symmetric fallback using half-width aspect). Desktop mirror preserved when VR disabled or runtime missing (graceful fallback: side-by-side stereo preview without HMD).
- OpenXR via direct `openxr_loader.dll` P/Invoke (`XR_KHR_D3D11_ENABLE`) in `Orts.VR`, sharing the in-process D3D11 device; controllers via OpenXR actions (`/user/hand/left|right`, trigger/squeeze/thumbstick/buttons) with graceful missing-device handling.
- Coordinate care: MSTS world uses tile + Z-negated XNA space (`XnaLocation`); HMD meters map 1:1 to world meters with configurable clamp; head delta applied **after** car transform, never to the train itself.
