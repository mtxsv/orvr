// Orts.VR — runtime-convention conversions.
//
// OpenXR uses a right-handed Y-up space with -Z forward; Open Rails / XNA
// camera space is left-handed-ish with +Z forward (RotatingCamera builds its
// look direction from +UnitZ) and +Y up. The rig (VRCameraRig / VRHeadPose)
// works in the XNA-side convention, so every tracked sample crosses this
// boundary exactly once, here.
//
// Mapping (basis change B = diag(1,1,-1), rotation conjugated R' = B*R*B):
//   position    (x, y, z)      -> (x, y, -z)
//   orientation (x, y, z, w)   -> (-x, -y, z, w)   (yaw/pitch negate, roll same)
// The mapping is an involution: applying it twice is identity (unit-tested).

using System.Numerics;

namespace Orts.VR
{
    public static class VRConventions
    {
        /// <summary>Converts an OpenXR-space head/hand pose to rig space.</summary>
        public static VRRawPose OpenXRToRig(Vector3 xrPosition, Quaternion xrOrientation, bool valid = true)
        {
            var p = new Vector3(xrPosition.X, xrPosition.Y, -xrPosition.Z);
            var q = new Quaternion(-xrOrientation.X, -xrOrientation.Y, xrOrientation.Z, xrOrientation.W);
            return new VRRawPose(p, Quaternion.Normalize(q), valid);
        }

        /// <summary>Converts a rig-space ray direction back to OpenXR space.</summary>
        public static Vector3 RigToOpenXRDirection(Vector3 rigDirection)
        {
            return new Vector3(rigDirection.X, rigDirection.Y, -rigDirection.Z);
        }
    }
}
