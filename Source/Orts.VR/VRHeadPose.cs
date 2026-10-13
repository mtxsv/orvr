// Orts.VR — tracked head pose: recenter + movement limits.
//
// Units are meters (OpenXR) which map 1:1 to Open Rails world meters.
// The pose is stored in *camera space*: position offset + orientation delta
// applied on top of the base camera. Recentering records the current raw
// pose as the new origin so subsequent deltas are relative to it.

using System;
using System.Numerics;

namespace Orts.VR
{
    /// <summary>Raw tracked HMD sample for one predicted display time.</summary>
    public struct VRRawPose
    {
        public Vector3 Position;
        public Quaternion Orientation;
        public bool Valid;

        public VRRawPose(Vector3 position, Quaternion orientation, bool valid = true)
        {
            Position = position;
            Orientation = orientation;
            Valid = valid;
        }

        public static VRRawPose Identity => new VRRawPose(Vector3.Zero, Quaternion.Identity, true);
    }

    /// <summary>
    /// Head pose with recenter and positional clamp. Not thread-safe by
    /// design: one instance is owned by VRCameraRig and fed once per frame.
    /// </summary>
    public sealed class VRHeadPose
    {
        // Recenter origin (raw space).
        private Vector3 _originPosition = Vector3.Zero;
        private Quaternion _originOrientationInverse = Quaternion.Identity;

        // Latest relative (recentered) pose.
        private Vector3 _relativePosition = Vector3.Zero;
        private Quaternion _relativeOrientation = Quaternion.Identity;
        private bool _hasValidPose;

        /// <summary>Maximum horizontal head displacement from origin (m).</summary>
        public float MaxRadiusM { get; set; } = 0.35f;

        /// <summary>Maximum upward head displacement (m).</summary>
        public float MaxUpM { get; set; } = 0.30f;

        /// <summary>Maximum downward head displacement (m).</summary>
        public float MaxDownM { get; set; } = 0.30f;

        /// <summary>When false, position is ignored (rotation-only tracking).</summary>
        public bool PositionalTracking { get; set; } = true;

        public bool HasValidPose => _hasValidPose;
        public Vector3 RelativePosition => _relativePosition;
        public Quaternion RelativeOrientation => _relativeOrientation;

        /// <summary>Feeds a new raw sample; applies recenter origin + clamp.</summary>
        public void Update(in VRRawPose raw)
        {
            if (!raw.Valid)
            {
                _hasValidPose = false;
                return;
            }
            _hasValidPose = true;

            var q = Quaternion.Normalize(raw.Orientation);
            _relativeOrientation = Quaternion.Normalize(Quaternion.Multiply(_originOrientationInverse, q));

            if (PositionalTracking)
            {
                var p = raw.Position - _originPosition;
                // Express the translational delta in recentered head space so
                // turning the head does not swing the positional offset.
                p = Vector3.Transform(p, _originOrientationInverse);
                _relativePosition = ClampPosition(p);
            }
            else
            {
                _relativePosition = Vector3.Zero;
            }
        }

        /// <summary>
        /// Makes the *next* raw sample read as identity. Call with the most
        /// recent raw pose (or just mark; the next Update re-anchors).
        /// </summary>
        public void Recenter(in VRRawPose currentRaw)
        {
            if (currentRaw.Valid)
            {
                _originPosition = currentRaw.Position;
                _originOrientationInverse = Quaternion.Inverse(Quaternion.Normalize(currentRaw.Orientation));
            }
            else
            {
                _originPosition = Vector3.Zero;
                _originOrientationInverse = Quaternion.Identity;
            }
            _relativePosition = Vector3.Zero;
            _relativeOrientation = Quaternion.Identity;
        }

        public void Recenter()
        {
            _originPosition = Vector3.Zero;
            _originOrientationInverse = Quaternion.Identity;
            _relativePosition = Vector3.Zero;
            _relativeOrientation = Quaternion.Identity;
        }

        /// <summary>Clears transient state on camera switch (no accumulation).</summary>
        public void ResetTransient()
        {
            _relativePosition = Vector3.Zero;
            _relativeOrientation = Quaternion.Identity;
            _hasValidPose = false;
        }

        public Vector3 ClampPosition(Vector3 p)
        {
            p.Y = MathF.Min(MaxUpM, MathF.Max(-MaxDownM, p.Y));
            var r = MathF.Sqrt(p.X * p.X + p.Z * p.Z);
            if (r > MaxRadiusM && r > 0f)
            {
                var s = MaxRadiusM / r;
                p.X *= s;
                p.Z *= s;
            }
            return p;
        }

        /// <summary>
        /// Inverse of the relative head transform (camera space), for
        /// VRStereoMath.EyeView. Identity when no valid pose or when the
        /// caller disables head tracking for the frame.
        /// </summary>
        public Matrix4x4 HeadInverse(bool headTrackingEnabled)
        {
            if (!headTrackingEnabled || !_hasValidPose)
                return Matrix4x4.Identity;
            var m = Matrix4x4.CreateFromQuaternion(_relativeOrientation);
            m.Translation = _relativePosition;
            Matrix4x4.Invert(m, out var inv);
            return inv;
        }

        /// <summary>Forward (-Z? +Z?) helper: head forward in camera space.</summary>
        public Vector3 HeadForward()
        {
            // Camera looks along +Z in the rig's local convention (matches
            // RotatingCamera.GetCameraView lookAt = UnitZ rotated); head
            // forward is that axis rotated by the relative orientation.
            return Vector3.Transform(Vector3.UnitZ, _relativeOrientation);
        }
    }
}
