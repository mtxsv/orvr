# ORVR engine patch — exact integration edits

All edits are additive and inert when `VREnabled` is false (default).
`Apply-VR-Patch.ps1` performs them idempotently; this document is the
human-readable source of truth. File paths are relative to the Open Rails
checkout (`Source/...`), verified against `master` @ Oct 2026.

## 0. New files (copied, no edits needed)

- `Integration/RunActivity/VR/VRSystem.cs` → `Source/RunActivity/Viewer3D/VR/VRSystem.cs`
- `Integration/RunActivity/VR/VRNative.cs` → `Source/RunActivity/Viewer3D/VR/VRNative.cs`
- `Source/Orts.VR/*` → referenced as project (see step 1)

## 1. `Source/RunActivity/RunActivity.csproj` — reference Orts.VR

```xml
<ItemGroup>
  <ProjectReference Include="..\..\orvr\Source\Orts.VR\Orts.VR.csproj" />
</ItemGroup>
```

The script computes the relative path from the checkout to the orvr repo.
(Build validated with the library as a sibling checkout; any location works.)

## 2. `Source/Orts.Settings/UserSettings.cs` — VR settings block

Insert `Integration/RunActivity/VR/UserSettings.VR.inc.cs` inside
`#region User Settings` (after the `ViewingFOV` property). Integer
conventions (percent/millimeters) because the settings store persists
bool/int/string only.

## 3. `Source/ORTS.Common/Input/UserCommand.cs` — recenter command

Append at the END of the enum (existing numeric values must not shift —
saved key bindings index by value):

```csharp
[GetString("VR Recenter View")] VRRecenter,
```

## 4. `Source/Orts.Settings/InputSettings.cs` — default binding

In `Reset()`, with the other camera defaults:

```csharp
Commands[(int)UserCommand.VRRecenter] = new UserCommandKeyInput(0x13, KeyModifiers.Alt | KeyModifiers.Shift);
```

(R = recenter; rebindable in Options > Keyboard.)

## 5. `Source/RunActivity/Viewer3D/UserInput.cs` — VR pointer overlay

1. Insert `Integration/RunActivity/VR/UserInput.VR.inc.cs` region (e.g.
   after the `MouseWheelChange` property, ~line 193).
2. Wrap exactly these getters so the virtual mouse supersedes the physical
   mouse only while a controller aims at the screen:

```csharp
public static int MouseMoveX { get { return VRPointerActive ? VRPointerX - VRPointerLastX : MouseState.X - LastMouseState.X; } }
public static int MouseMoveY { get { return VRPointerActive ? VRPointerY - VRPointerLastY : MouseState.Y - LastMouseState.Y; } }
public static int MouseX { get { return VRPointerActive ? VRPointerX : MouseState.X; } }
public static int MouseY { get { return VRPointerActive ? VRPointerY : MouseState.Y; } }
public static bool IsMouseLeftButtonDown { get { return VRPointerActive ? VRPointerDown : <original>; } }
public static bool IsMouseLeftButtonPressed { get { return VRPointerActive ? VRPointerPressed : <original>; } }
public static bool IsMouseLeftButtonReleased { get { return VRPointerActive ? VRPointerReleased : <original>; } }
```

`<original>` = the existing right-hand side verbatim (incl. MouseButtonsSwapped
logic). Middle/right buttons and wheel stay physical: camera rotate/zoom keep
working on the mouse with zero conflicts.

## 6. `Source/RunActivity/Viewer3D/RenderFrame.cs` — eye rendering

1. Insert the `SetStereoView` / `DrawShadowsOnly` / `DrawEye` methods from
   `Integration/RunActivity/VR/RenderFrame.VR.inc.cs` after `SetCamera`.
2. Replace `DrawSimple(GraphicsDevice, bool)` with the
   `DrawSimple` + `DrawSimpleTo` pair from the same file (mono behavior
   identical — same guards, same blit).

## 7. `Source/RunActivity/Viewer3D/Processes/RenderProcess.cs` — stereo hook

1. Add field (near `CurrentFrame`/`NextFrame`):

```csharp
public Orts.Viewer3D.VR.VRSystem VRSystem;
readonly object VRSystemLock = new object();
```

2. In `Draw()`, before the existing try/catch:

```csharp
EnsureVRSystem();
if (VRSystem != null && VRSystem.BeginDraw(this))
{
    try
    {
        CurrentFrame.SetCamera(Viewer.Camera);
        CurrentFrame.DrawShadowsOnly(GraphicsDevice);
        VRSystem.DrawEye(CurrentFrame, Orts.VR.VREye.Left);
        VRSystem.DrawEye(CurrentFrame, Orts.VR.VREye.Right);
    }
    finally
    {
        VRSystem.EndDraw(this);
    }
    return;
}
```

3. Add helper + shutdown:

```csharp
void EnsureVRSystem()
{
    if (VRSystem == null && Viewer != null)
    {
        lock (VRSystemLock)
        {
            if (VRSystem == null)
                VRSystem = new Orts.Viewer3D.VR.VRSystem(Viewer);
        }
    }
}
```

In `Stop()`: `try { VRSystem?.Dispose(); } catch { }` and null it.

4. Add `using Orts.VR;` only if needed (the hook uses fully-qualified names;
   no new using required except `Orts.Viewer3D.VR` when referencing VRSystem
   unqualified — the patch uses the fully-qualified form).

## 8. `Source/RunActivity/Viewer3D/Viewer.cs` — interaction hook

In `Update(RenderFrame frame, float elapsedRealTime)`, immediately after
`HandleUserInput(elapsedTime);` (line ~803):

```csharp
try { RenderProcess.VRSystem?.UpdateInteraction(frame, elapsedTime); } catch { }
```

Runs on the updater thread alongside cab input; render-thread state is
exchanged through VRSystem's lock. Never throws out.

## Why no changes to Cameras.cs / cab renderers / WindowManager

- Head tracking is an additive delta computed at Draw time from
  `Camera.XnaView`; camera state, train transforms, and replay commands are
  never touched. Camera switches are detected by reference comparison, so no
  per-camera edits are needed and accumulated offsets are impossible.
- Cab actuation reuses `ICabViewMouseControlRenderer` + the `ChangedValue`
  override pattern keyboard control already uses; popups reuse `WindowManager`
  hit-testing through the `UserInput` overlay. No control logic is duplicated.
