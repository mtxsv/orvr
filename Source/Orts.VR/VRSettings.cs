// Orts.VR — settings. Follows Open Rails UserSettings conventions:
//
//   - bool/int/float/string auto-properties with built-in defaults.
//   - The engine adapter copies these to/from real UserSettings properties
//     (same names) so they persist in OpenRails.ini; standalone use keeps
//     working with VRSettings.Default.
//   - Every default preserves desktop behavior: VR is OFF unless enabled.

namespace Orts.VR
{
    public sealed class VRSettings
    {
        public static VRSettings Default => new VRSettings();

        // Master switch -----------------------------------------------------
        /// <summary>Enable VR stereo + tracking. Default off (desktop intact).</summary>
        public bool VREnabled { get; set; } = false;

        // Rendering ----------------------------------------------------------
        /// <summary>Supersampling multiplier (1.0 = runtime recommended size).</summary>
        public float VRRenderScale { get; set; } = 1.0f;
        /// <summary>Interpupillary distance in meters (measured or manual).</summary>
        public float VRIPD { get; set; } = 0.063f;

        // Head tracking -------------------------------------------------------
        public bool VRHeadTracking { get; set; } = true;
        public bool VRPositionalTracking { get; set; } = true;
        /// <summary>Horizontal lean limit in cab (m).</summary>
        public float VRHeadRadiusM { get; set; } = 0.35f;
        /// <summary>Look-up limit offset (m).</summary>
        public float VRHeadMaxUpM { get; set; } = 0.30f;
        /// <summary>Look-down limit offset (m).</summary>
        public float VRHeadMaxDownM { get; set; } = 0.30f;

        // Controllers ----------------------------------------------------------
        /// <summary>Dominant hand for the pointer ray.</summary>
        public VRHandedness VRDominantHand { get; set; } = VRHandedness.Right;
        /// <summary>Trigger = activate / press.</summary>
        public bool VRTriggerActivates { get; set; } = true;
        /// <summary>Squeeze/grip = grab sliders and levers.</summary>
        public bool VRSqueezeGrabs { get; set; } = true;
        /// <summary>Thumbstick up/down nudges a grabbed control.</summary>
        public bool VRThumbstickAdjusts { get; set; } = true;
        public bool VRHaptics { get; set; } = true;

        public void Validate()
        {
            if (VRRenderScale < 0.5f) VRRenderScale = 0.5f;
            if (VRRenderScale > 2.0f) VRRenderScale = 2.0f;
            if (VRIPD < 0.05f) VRIPD = 0.05f;
            if (VRIPD > 0.08f) VRIPD = 0.08f;
            if (VRHeadRadiusM < 0.05f) VRHeadRadiusM = 0.05f;
            if (VRHeadRadiusM > 1.00f) VRHeadRadiusM = 1.00f;
            if (VRHeadMaxUpM < 0.05f) VRHeadMaxUpM = 0.05f;
            if (VRHeadMaxUpM > 1.00f) VRHeadMaxUpM = 1.00f;
            if (VRHeadMaxDownM < 0.05f) VRHeadMaxDownM = 0.05f;
            if (VRHeadMaxDownM > 1.00f) VRHeadMaxDownM = 1.00f;
        }

        /// <summary>Applies tracking limits to a rig + head pose pair.</summary>
        public void ApplyTo(VRCameraRig rig)
        {
            Validate();
            rig.IpdMeters = VRIPD;
            rig.RenderScale = VRRenderScale;
            rig.HeadTrackingEnabled = VRHeadTracking;
            rig.Head.PositionalTracking = VRPositionalTracking;
            rig.Head.MaxRadiusM = VRHeadRadiusM;
            rig.Head.MaxUpM = VRHeadMaxUpM;
            rig.Head.MaxDownM = VRHeadMaxDownM;
        }
    }
}
