// ORVR — VR pointer overlay for UserInput (additive patch block).
//
// Insert this region into Source/RunActivity/Viewer3D/UserInput.cs inside the
// UserInput class (e.g. after the MouseWheelChange property). It adds a
// virtual mouse driven by the VR controller pointer. When inactive (default),
// every getter returns the physical mouse state verbatim, so desktop behavior
// is bit-for-bit unchanged. Precedent: RailDriver/external-device overlays.

#region ORVR VR pointer overlay
static bool VRPointerActive;
static int VRPointerX, VRPointerY, VRPointerLastX, VRPointerLastY;
static bool VRPointerDown, VRPointerPressed, VRPointerReleased;

/// <summary>Drives the virtual mouse from the VR controller pointer (updater thread).</summary>
public static void SetVRPointer(bool active, int x, int y, bool leftDown, bool leftPressed, bool leftReleased)
{
    VRPointerLastX = VRPointerActive ? VRPointerX : x;
    VRPointerLastY = VRPointerActive ? VRPointerY : y;
    VRPointerActive = active;
    VRPointerX = x;
    VRPointerY = y;
    VRPointerDown = leftDown;
    VRPointerPressed = leftPressed;
    VRPointerReleased = leftReleased;
}

/// <summary>Releases the virtual mouse back to the physical mouse.</summary>
public static void ClearVRPointer()
{
    VRPointerActive = false;
    VRPointerDown = VRPointerPressed = VRPointerReleased = false;
}

public static bool VRPointerIsActive { get { return VRPointerActive; } }
#endregion
