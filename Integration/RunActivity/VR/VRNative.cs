// ORVR — native interop helpers (engine side, RunActivity assembly).
//
// Uses only PUBLIC MonoGame 3.8.1.303 APIs (verified by reflection against
// the pinned package):
//   - GraphicsDevice.Handle -> SharpDX.Direct3D11.Device (same device the
//     OpenXR D3D11 session is created against; XR_KHR_D3D11_ENABLE).
//   - Texture2D.GetSharedHandle() -> DXGI shared NT handle of OUR eye
//     render targets (created with shared:true). The shim opens them on the
//     session device and GPU-copies into the OpenXR swapchain images.
// No reflection, no private API. Every method degrades gracefully.

using System;
using Microsoft.Xna.Framework.Graphics;
using Orts.VR;
using SN = System.Numerics;

namespace Orts.Viewer3D.VR
{
    internal static class VRNative
    {
        /// <summary>Native ID3D11Device pointer behind the MonoGame device. Zero on failure.</summary>
        public static IntPtr GetD3D11DevicePtr(GraphicsDevice graphicsDevice)
        {
            try
            {
                var device = graphicsDevice.Handle as SharpDX.Direct3D11.Device;
                if (device == null)
                    return IntPtr.Zero;
                return device.NativePointer;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        /// <summary>DXGI shared handle of an eye target (IntPtr.Zero on failure).</summary>
        public static IntPtr GetSharedHandle(RenderTarget2D target)
        {
            try
            {
                if (target == null || target.IsDisposed)
                    return IntPtr.Zero;
                return target.GetSharedHandle();
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        /// <summary>Submits both eye targets to the HMD. False = use desktop mirror.</summary>
        public static bool Submit(VROpenXRHost host, RenderTarget2D left, RenderTarget2D right)
        {
            try
            {
                if (left == null || right == null)
                    return false;
                var hL = GetSharedHandle(left);
                var hR = GetSharedHandle(right);
                if (hL == IntPtr.Zero || hR == IntPtr.Zero)
                    return false;
                return host.SubmitSharedHandles(hL, hR, left.Width, left.Height);
            }
            catch
            {
                return false;
            }
        }

        public static bool TryGetEyeFov(VROpenXRHost host, VREye eye, out SN.Vector4 fov)
        {
            fov = default;
            try
            {
                return host.TryGetEyeFov(eye == VREye.Left ? 0 : 1, out fov);
            }
            catch
            {
                return false;
            }
        }

        public static void Haptic(VROpenXRHost host, VRHapticPulse pulse)
        {
            try
            {
                host.SendHaptic(pulse.Hand, pulse.Amplitude, pulse.DurationMs);
            }
            catch { }
        }
    }
}
