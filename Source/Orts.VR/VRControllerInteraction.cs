// Orts.VR — controller interaction state machines (engine-agnostic).
//
// Boundary: the thin engine adapter (RunActivity, with XNA types) turns a
// controller aim pose into either a 3D cab-space ray or a 2D virtual cursor
// and feeds the resulting events here. This library owns the interaction
// *semantics* so they behave identically regardless of input device:
//
//   - Buttons/switches: press / release / toggle / hold use the same command
//     path as the mouse (the adapter forwards to
//     ICabViewMouseControlRenderer.HandleUserInput); this library only
//     decides *which* control is targeted and debounces edges.
//   - Sliders/levers/throttle/brakes/reverser: grab stores the starting
//     value + anchor so there is no jump; motion maps to the control's
//     [MinValue, MaxValue] fraction; notches snap; limits clamp.
//   - Rotary controls: same fraction mapping over the angular range.
//   - Exterior guard: when the camera is external, rays never target cab
//     controls (prevents activating cab controls through the train body).
//
// All math is System.Numerics + plain floats; no engine references.

using System;
using System.Numerics;

namespace Orts.VR
{
    /// <summary>Which hand drives the interaction.</summary>
    public enum VRHandedness
    {
        Right = 0,
        Left = 1,
    }

    /// <summary>Snapshot of one controller for the current frame.</summary>
    public struct VRHandState
    {
        public bool Active;
        public Vector3 AimPosition;
        public Quaternion AimOrientation;
        public float Trigger;          // 0..1
        public bool TriggerPressed;    // rising edge this frame
        public bool TriggerReleased;   // falling edge this frame
        public bool SqueezePressed;
        public Vector2 Thumbstick;
        public int Buttons;            // runtime button bitmask
    }

    /// <summary>2D virtual cursor produced by the engine adapter from a ray.</summary>
    public struct VRVirtualCursor
    {
        public float X;                // pixels (same space as cab DestinationRectangle)
        public float Y;
        public bool OnScreen;
        public VRHandedness Hand;
    }

    /// <summary>Description of a mappable cab control for grab/drag.</summary>
    public struct VRMappableControl
    {
        public float MinValue;
        public float MaxValue;
        public float CurrentValue;
        /// <summary>Notch positions in raw value units; empty/null = continuous.</summary>
        public float[] Notches;
        /// <summary>Pixels of controller/cursor travel mapping to the full range.</summary>
        public float FullRangePixels;
        /// <summary>True for vertical drag (most levers), false for horizontal.</summary>
        public bool VerticalDrag;
        /// <summary>Flip drag direction (some levers increase downward).</summary>
        public bool InvertDrag;

        public float Range => MaxValue - MinValue;

        public float CurrentFraction()
        {
            var r = Range;
            if (r <= 0f)
                return 0f;
            var f = (CurrentValue - MinValue) / r;
            if (f < 0f) return 0f;
            if (f > 1f) return 1f;
            return f;
        }
    }

    /// <summary>
    /// Grab session: stable anchor, no jumps, clean release. The adapter owns
    /// reading/writing the real simulator value; this maps motion -> value.
    /// </summary>
    public sealed class VRGrabSession
    {
        public bool Active { get; private set; }
        public VRHandedness Hand { get; private set; }

        private float _grabFraction;   // control fraction at grab time
        private float _anchorPrimary;  // cursor/controller primary axis at grab
        private VRMappableControl _control;

        public void Begin(VRMappableControl control, float cursorPrimaryAxis, VRHandedness hand)
        {
            _control = control;
            _grabFraction = control.CurrentFraction();
            _anchorPrimary = cursorPrimaryAxis;
            Hand = hand;
            Active = true;
        }

        /// <summary>
        /// Maps current primary-axis position to a control value.
        /// Returns the value the adapter should apply through the real path.
        /// </summary>
        public float MoveTo(float cursorPrimaryAxis)
        {
            if (!Active)
                return _control.CurrentValue;
            var range = _control.Range;
            var pixels = _control.FullRangePixels > 0f ? _control.FullRangePixels : 300f;
            var delta = (cursorPrimaryAxis - _anchorPrimary) / pixels;
            if (_control.VerticalDrag)
                delta = -delta; // screen Y grows downward; up = increase
            if (_control.InvertDrag)
                delta = -delta;
            var fraction = Clamp01(_grabFraction + delta);
            return ValueFromFraction(_control, fraction);
        }

        /// <summary>Thumbstick/trackpad nudge while grabbed (or hovered).</summary>
        public float Nudge(float secondsHeld, float stickAxis, float fullRangePerSecond = 0.5f)
        {
            if (!Active)
                return _control.CurrentValue;
            var fraction = Clamp01(_grabFraction + stickAxis * fullRangePerSecond * secondsHeld);
            return ValueFromFraction(_control, fraction);
        }

        /// <summary>Re-anchor after applying (keeps thumbstick nudges relative).</summary>
        public void Rebase(float appliedValue, float cursorPrimaryAxis)
        {
            var r = _control.Range;
            _grabFraction = r > 0f ? Clamp01((appliedValue - _control.MinValue) / r) : 0f;
            _control.CurrentValue = appliedValue;
            _anchorPrimary = cursorPrimaryAxis;
        }

        public void End()
        {
            Active = false;
        }

        public static float ValueFromFraction(in VRMappableControl control, float fraction)
        {
            fraction = Clamp01(fraction);
            var value = control.MinValue + fraction * control.Range;
            if (control.Notches != null && control.Notches.Length > 0)
                value = SnapToNotches(value, control.Notches, control.Range);
            if (value < control.MinValue) value = control.MinValue;
            if (value > control.MaxValue) value = control.MaxValue;
            return value;
        }

        public static float SnapToNotches(float value, float[] notches, float range)
        {
            if (notches == null || notches.Length == 0)
                return value;
            // Snap radius: 8% of range (matches MSTS detent feel).
            var radius = range * 0.08f;
            var best = value;
            var bestDist = radius;
            for (var i = 0; i < notches.Length; i++)
            {
                var d = Math.Abs(notches[i] - value);
                if (d <= bestDist)
                {
                    bestDist = d;
                    best = notches[i];
                }
            }
            return best;
        }

        private static float Clamp01(float v)
        {
            if (v < 0f) return 0f;
            if (v > 1f) return 1f;
            return v;
        }
    }

    /// <summary>
    /// Ray-vs-rectangle picking for 2D cab controls and popup windows.
    /// Rectangles use the same pixel space as
    /// ICabViewMouseControlRenderer.DestinationRectangleGet.
    /// </summary>
    public static class VRPicking
    {
        /// <summary>
        /// Returns the index of the topmost rect containing the cursor, or -1.
        /// Rects are (x, y, w, h) rows; later entries are on top.
        /// </summary>
        public static int PickTopmost(float cursorX, float cursorY, float[,] rects, int count)
        {
            var hit = -1;
            for (var i = 0; i < count; i++)
            {
                var x = rects[i, 0];
                var y = rects[i, 1];
                var w = rects[i, 2];
                var h = rects[i, 3];
                if (w <= 0f || h <= 0f)
                    continue;
                if (cursorX >= x && cursorX < x + w && cursorY >= y && cursorY < y + h)
                    hit = i;
            }
            return hit;
        }

        /// <summary>
        /// Ray (origin + normalized direction) vs. a cab-plane control sphere.
        /// Used for 3D-cab bounds. Returns distance along the ray, or -1.
        /// </summary>
        public static float RaySphere(Vector3 origin, Vector3 direction, Vector3 center, float radius)
        {
            var ox = center.X - origin.X;
            var oy = center.Y - origin.Y;
            var oz = center.Z - origin.Z;
            var t = ox * direction.X + oy * direction.Y + oz * direction.Z;
            if (t < 0f)
                return -1f;
            var d2 = ox * ox + oy * oy + oz * oz - t * t;
            var r2 = radius * radius;
            if (d2 > r2)
                return -1f;
            var half = (float)Math.Sqrt(r2 - d2);
            return t - half;
        }
    }

    /// <summary>
    /// Guards: cab controls are only interactive from cab-family cameras.
    /// External cameras never route controller rays to the cab.
    /// </summary>
    public static class VRInteractionGuard
    {
        public static bool CanInteractWithCab(VRCameraStyle style)
        {
            return style == VRCameraStyle.Cab2D
                || style == VRCameraStyle.Cab3D
                || style == VRCameraStyle.Passenger;
        }

        public static bool CanInteractWithUI(VRCameraStyle style)
        {
            return true; // popups/menus work from every camera
        }
    }

    /// <summary>Haptic pulse request; engine forwards to OpenXR when present.</summary>
    public struct VRHapticPulse
    {
        public VRHandedness Hand;
        /// <summary>Amplitude 0..1.</summary>
        public float Amplitude;
        /// <summary>Duration in milliseconds.</summary>
        public int DurationMs;

        public static VRHapticPulse Select(VRHandedness hand) => new VRHapticPulse { Hand = hand, Amplitude = 0.4f, DurationMs = 25 };
        public static VRHapticPulse Grab(VRHandedness hand) => new VRHapticPulse { Hand = hand, Amplitude = 0.6f, DurationMs = 40 };
        public static VRHapticPulse Notch(VRHandedness hand) => new VRHapticPulse { Hand = hand, Amplitude = 0.3f, DurationMs = 15 };
    }
}
