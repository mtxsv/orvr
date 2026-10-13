// Orts.VR — pure stereo math. No engine or OpenXR dependencies.
//
// Coordinate notes (Open Rails / MonoGame XNA convention):
//   - View matrices are left-handed XNA style (Camera.XnaView).
//   - Eye offsets are applied along camera-right in *head space* before the
//     base view, so IPD never depends on world orientation.
//   - All functions are pure, allocation-free, and unit-tested in Orts.VR.Tests.

using System;
using System.Numerics;

namespace Orts.VR
{
    /// <summary>Left or right eye.</summary>
    public enum VREye
    {
        Left = 0,
        Right = 1,
    }

    /// <summary>Which Open Rails camera family the rig is augmenting.</summary>
    public enum VRCameraStyle
    {
        External = 0,
        Cab2D = 1,
        Cab3D = 2,
        Passenger = 3,
    }

    /// <summary>
    /// Stereo view/projection math shared by the desktop-fallback renderer and
    /// the OpenXR submission path. Both eyes are always derived from the same
    /// base camera state so they can never desynchronize.
    /// </summary>
    public static class VRStereoMath
    {
        /// <summary>
        /// Builds a per-eye view matrix from the base (mono) camera view.
        /// headInverse is the inverse of the tracked head pose expressed in
        /// camera space; eyeOffsetX is -IPD/2 (left) or +IPD/2 (right) in meters.
        /// </summary>
        public static Matrix4x4 EyeView(in Matrix4x4 baseView, in Matrix4x4 headInverse, float eyeOffsetX)
        {
            var eyeShift = Matrix4x4.CreateTranslation(eyeOffsetX, 0f, 0f);
            // Head motion first (camera-relative), then lateral eye shift.
            var m = Matrix4x4.Multiply(headInverse, eyeShift);
            return Matrix4x4.Multiply(m, baseView);
        }

        /// <summary>
        /// Asymmetric perspective projection from per-side OpenXR FOV angles
        /// (radians, positive magnitudes). Matches xrLocateViews recommended
        /// projection. Returns false when inputs are degenerate.
        /// </summary>
        public static bool TryProjectionFromFov(
            float fovLeft, float fovRight, float fovUp, float fovDown,
            float nearPlane, float farPlane,
            out Matrix4x4 projection)
        {
            projection = Matrix4x4.Identity;
            if (nearPlane <= 0f || farPlane <= nearPlane)
                return false;
            var tanL = (float)Math.Tan(fovLeft);
            var tanR = (float)Math.Tan(fovRight);
            var tanU = (float)Math.Tan(fovUp);
            var tanD = (float)Math.Tan(fovDown);
            var width = tanR + tanL;
            var height = tanU + tanD;
            if (width <= 0f || height <= 0f)
                return false;

            // XNA/MonoGame left-handed perspective with off-center frustum.
            var m11 = 2f / width;
            var m22 = 2f / height;
            var m31 = (tanR - tanL) / width;
            var m32 = (tanU - tanD) / height;
            var m33 = farPlane / (farPlane - nearPlane);
            var m34 = 1f;
            var m43 = -nearPlane * farPlane / (farPlane - nearPlane);

            projection = new Matrix4x4(
                m11, 0f, 0f, 0f,
                0f, m22, 0f, 0f,
                m31, m32, m33, m34,
                0f, 0f, m43, 0f);
            return true;
        }

        /// <summary>
        /// Symmetric fallback projection (no runtime). Per-eye aspect should be
        /// half-width aspect: (width/2)/height.
        /// </summary>
        public static bool TrySymmetricProjection(
            float fovYRadians, float aspect, float nearPlane, float farPlane,
            out Matrix4x4 projection)
        {
            projection = Matrix4x4.Identity;
            if (fovYRadians <= 0f || fovYRadians >= (float)Math.PI || aspect <= 0f)
                return false;
            if (nearPlane <= 0f || farPlane <= nearPlane)
                return false;
            var h = 1f / (float)Math.Tan(fovYRadians * 0.5f);
            var w = h / aspect;
            projection = new Matrix4x4(
                w, 0f, 0f, 0f,
                0f, h, 0f, 0f,
                0f, 0f, farPlane / (farPlane - nearPlane), 1f,
                0f, 0f, -nearPlane * farPlane / (farPlane - nearPlane), 0f);
            return true;
        }

        /// <summary>Eye lateral offset in meters: left = -IPD/2, right = +IPD/2.</summary>
        public static float EyeOffsetX(VREye eye, float ipdMeters)
        {
            return (eye == VREye.Left ? -0.5f : 0.5f) * ipdMeters;
        }

        /// <summary>
        /// Per-eye aspect for side-by-side rendering from a full-frame aspect.
        /// </summary>
        public static float PerEyeAspect(float fullAspect)
        {
            return fullAspect * 0.5f;
        }

        /// <summary>
        /// Extracts camera-right in world units from a view matrix row
        /// (XNA row-major: M11..M13). Used to sanity-check eye direction.
        /// </summary>
        public static Vector3 CameraRight(in Matrix4x4 view)
        {
            return new Vector3(view.M11, view.M21, view.M31);
        }
    }
}
