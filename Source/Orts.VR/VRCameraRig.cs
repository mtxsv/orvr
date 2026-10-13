// Orts.VR — camera rig: applies the tracked head delta on top of whatever
// base camera Open Rails computed, without touching train or camera state.
//
// Integration boundary (keeps this library free of MonoGame references):
//   - The engine passes base view/projection as sixteen floats (XNA Matrix
//     M11..M44 layout, identical to System.Numerics.Matrix4x4).
//   - The rig returns per-eye view/projection the same way.
//   - Z-negation / tile-space conventions of XnaLocation() stay in the thin
//     engine adapter (docs/integration), never here.
//
// Guarantees:
//   - Both eyes derive from ONE base state + ONE head sample (same sim state).
//   - NotifyCameraSwitched() drops transient deltas: no jumps, no accumulation,
//     no double head transform when toggling cab <-> external.
//   - Cab styles apply the head offset in cab-local space (after the car
//     transform), so the train itself never moves.

using System;
using System.Numerics;

namespace Orts.VR
{
    /// <summary>Per-frame eye output (row-major M11..M44 float layout).</summary>
    public struct VREyeOutput
    {
        public Matrix4x4 View;
        public Matrix4x4 Projection;
    }

    public sealed class VRCameraRig
    {
        private readonly VRHeadPose _head = new VRHeadPose();

        private VRCameraStyle _style = VRCameraStyle.External;
        private VRCameraStyle _lastStyle = VRCameraStyle.External;

        /// <summary>Interpupillary distance in meters.</summary>
        public float IpdMeters { get; set; } = 0.063f;

        /// <summary>Render scale / supersampling multiplier.</summary>
        public float RenderScale { get; set; } = 1.0f;

        /// <summary>Master head-tracking toggle (rotation + position).</summary>
        public bool HeadTrackingEnabled { get; set; } = true;

        public VRHeadPose Head => _head;

        public VRCameraStyle Style
        {
            get => _style;
            set
            {
                if (value != _style)
                {
                    _lastStyle = _style;
                    _style = value;
                    NotifyCameraSwitched();
                }
            }
        }

        /// <summary>
        /// Must be called whenever Open Rails activates a different camera
        /// (cab <-> external, car change, viewpoint change). Clears transient
        /// deltas so the new camera starts exactly where its own logic puts it.
        /// </summary>
        public void NotifyCameraSwitched()
        {
            _head.ResetTransient();
        }

        public void Recenter(in VRRawPose currentRaw)
        {
            _head.Recenter(currentRaw);
        }

        public void Recenter()
        {
            _head.Recenter();
        }

        public void UpdateHead(in VRRawPose raw)
        {
            _head.Update(raw);
        }

        /// <summary>
        /// Computes both eye views from one base view + current head state.
        /// </summary>
        public void ComputeEyeViews(in Matrix4x4 baseView, out VREyeOutput left, out VREyeOutput right)
        {
            var headInv = _head.HeadInverse(HeadTrackingEnabled);
            left = new VREyeOutput
            {
                View = VRStereoMath.EyeView(baseView, headInv, VRStereoMath.EyeOffsetX(VREye.Left, IpdMeters)),
            };
            right = new VREyeOutput
            {
                View = VRStereoMath.EyeView(baseView, headInv, VRStereoMath.EyeOffsetX(VREye.Right, IpdMeters)),
            };
        }

        /// <summary>
        /// Full per-eye output including projections. When per-eye FOVs are
        /// available (OpenXR xrLocateViews) they are used; otherwise a
        /// symmetric fallback from the mono FOV + per-eye aspect is used.
        /// nearPlane follows the active camera family (0.1 inside-3D cabs,
        /// 0.25 head-out, 1.0 default) and is passed in by the adapter.
        /// </summary>
        public bool ComputeEyes(
            in Matrix4x4 baseView,
            float monoFovYRadians, float fullAspect,
            float nearPlane, float farPlane,
            out VREyeOutput left, out VREyeOutput right)
        {
            ComputeEyeViews(baseView, out left, out right);
            var eyeAspect = VRStereoMath.PerEyeAspect(fullAspect);
            if (!VRStereoMath.TrySymmetricProjection(monoFovYRadians, eyeAspect, nearPlane, farPlane, out var proj))
                return false;
            left.Projection = proj;
            right.Projection = proj;
            return true;
        }

        /// <summary>
        /// Applies asymmetric OpenXR FOV projections to an already-computed
        /// eye pair (used by the real submission path).
        /// </summary>
        public bool ApplyOpenXrProjections(
            ref VREyeOutput left, ref VREyeOutput right,
            in Vector4 leftFov, in Vector4 rightFov,
            float nearPlane, float farPlane)
        {
            // Vector4 layout: X=left, Y=right, Z=up, W=down (radians, positive).
            if (!VRStereoMath.TryProjectionFromFov(leftFov.X, leftFov.Y, leftFov.Z, leftFov.W, nearPlane, farPlane, out var pl))
                return false;
            if (!VRStereoMath.TryProjectionFromFov(rightFov.X, rightFov.Y, rightFov.Z, rightFov.W, nearPlane, farPlane, out var pr))
                return false;
            left.Projection = pl;
            right.Projection = pr;
            return true;
        }

        /// <summary>
        /// Configures head limits per camera family. Cab-local clamps stay
        /// tight (lean, don't walk through the cab); external allows more.
        /// </summary>
        public void ApplyStyleDefaults(VRCameraStyle style)
        {
            switch (style)
            {
                case VRCameraStyle.Cab3D:
                case VRCameraStyle.Cab2D:
                case VRCameraStyle.Passenger:
                    _head.MaxRadiusM = 0.35f;
                    _head.MaxUpM = 0.30f;
                    _head.MaxDownM = 0.30f;
                    break;
                default:
                    _head.MaxRadiusM = 0.50f;
                    _head.MaxUpM = 0.50f;
                    _head.MaxDownM = 0.50f;
                    break;
            }
        }
    }
}
