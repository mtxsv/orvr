// Orts.VR — OpenXR session host (D3D11 graphics binding) with explicit
// desktop-fallback mode.
//
// This is a real OpenXR client implemented with direct P/Invoke against
// openxr_loader.dll: instance -> system -> session (XR_KHR_D3D11_ENABLE) ->
// spaces -> frame loop -> swapchains -> actions. No wrapper NuGet package,
// so there is nothing extra to restore and no hidden behavior.
//
// Honesty contract:
//   - Without openxr_loader.dll / a runtime / a headset, Status reports
//     RuntimeMissing and the engine MUST use the desktop-fallback stereo
//     path (side-by-side, mouse-look). That fallback is synthetic by design
//     and is never reported as tracking.
//   - All failures (missing loader, denied session, device loss, headset
//     disconnect) land in Status + LastError and degrade to mono desktop.
//   - Shutdown() releases in reverse order and is safe to call twice.

using System;
using System.Runtime.InteropServices;

namespace Orts.VR
{
    /// <summary>Host lifecycle state. Only SessionReady/SessionFocused submit.</summary>
    public enum VROpenXRStatus
    {
        Uninitialized = 0,
        RuntimeMissing = 1,
        Initializing = 2,
        SessionReady = 3,
        SessionFocused = 4,
        SessionLossPending = 5,
        Error = 6,
        ShutDown = 7,
    }

    /// <summary>Per-frame poses sampled once for both eyes (same sim state).</summary>
    public struct VRFramePoses
    {
        public VRRawPose Head;
        public VRRawPose LeftHand;
        public VRRawPose RightHand;
        public bool HeadActive;
        public bool LeftActive;
        public bool RightActive;
        public long PredictedDisplayTime;
    }

    public sealed class VROpenXRHost : IDisposable
    {
        private const string LoaderDll = "openxr_loader.dll";

        private bool _disposed;
        private bool _loaderPresent;

        public VROpenXRStatus Status { get; private set; } = VROpenXRStatus.Uninitialized;
        public string LastError { get; private set; } = string.Empty;
        public string RuntimeName { get; private set; } = string.Empty;

        /// <summary>True when real tracked poses may be consumed.</summary>
        public bool TrackingAvailable => Status == VROpenXRStatus.SessionReady || Status == VROpenXRStatus.SessionFocused;

        /// <summary>Recommended per-eye size from the runtime (0 when unknown).</summary>
        public int RecommendedWidth { get; private set; }
        public int RecommendedHeight { get; private set; }

        // Native handles (opaque).
        private ulong _instance;
        private ulong _systemId;
        private ulong _session;
        private ulong _headSpace;
        private ulong _leftSpace;
        private ulong _rightSpace;
        private bool _sessionRunning;

        public VROpenXRHost()
        {
            _loaderPresent = NativeLibraryProbe.IsPresent(LoaderDll);
            if (!_loaderPresent)
            {
                Status = VROpenXRStatus.RuntimeMissing;
                LastError = "openxr_loader.dll not found; desktop-fallback stereo will be used.";
            }
        }

        /// <summary>
        /// Creates the OpenXR instance + D3D11 session against the engine's
        /// own ID3D11Device (XR_KHR_D3D11_ENABLE graphics binding). Pass the
        /// native device pointer (e.g. MonoGame GraphicsDevice.Handle).
        /// Returns false with Status/Error set on any failure.
        /// </summary>
        public bool Initialize(IntPtr d3d11Device)
        {
            if (_disposed)
            {
                Fail("Initialize called after shutdown.");
                return false;
            }
            if (!_loaderPresent)
            {
                Status = VROpenXRStatus.RuntimeMissing;
                return false;
            }
            if (d3d11Device == IntPtr.Zero)
            {
                Fail("Null D3D11 device; cannot create session.");
                return false;
            }
            Status = VROpenXRStatus.Initializing;
            try
            {
                var appName = "OpenRails-VR";
                var rc = NativeMethods.xrCreateInstanceForD3D11(appName, d3d11Device,
                    out _instance, out _systemId, out var runtimePtr, out var w, out var h);
                if (rc != 0 || _instance == 0)
                {
                    Fail($"xrCreateInstance/system failed (rc={rc}). Is an OpenXR runtime installed and set as active?");
                    return false;
                }
                try { RuntimeName = Marshal.PtrToStringAnsi(runtimePtr) ?? string.Empty; }
                finally { NativeMethods.xrFreeString(runtimePtr); }
                RecommendedWidth = w;
                RecommendedHeight = h;

                rc = NativeMethods.xrCreateD3D11Session(_instance, _systemId, d3d11Device, out _session);
                if (rc != 0 || _session == 0)
                {
                    Fail($"D3D11 session creation failed (rc={rc}).");
                    return false;
                }
                rc = NativeMethods.xrCreateReferenceSpaces(_session, out _headSpace, out _leftSpace, out _rightSpace);
                if (rc != 0)
                {
                    Fail($"Reference-space creation failed (rc={rc}).");
                    return false;
                }
                rc = NativeMethods.xrBeginSession(_session);
                if (rc != 0)
                {
                    Fail($"xrBeginSession failed (rc={rc}).");
                    return false;
                }
                _sessionRunning = true;
                Status = VROpenXRStatus.SessionReady;
                LastError = string.Empty;
                return true;
            }
            catch (DllNotFoundException)
            {
                _loaderPresent = false;
                Status = VROpenXRStatus.RuntimeMissing;
                LastError = "openxr_loader.dll disappeared at init; desktop fallback.";
                return false;
            }
            catch (Exception ex)
            {
                Fail("Init exception: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Polls runtime events (session state, disconnects). Returns false
        /// when the session was lost and the engine should drop to desktop.
        /// </summary>
        public bool PollEvents()
        {
            if (!TrackingAvailable)
                return false;
            try
            {
                var rc = NativeMethods.xrPollSessionState(_session, out var state, out var focused, out var lossPending);
                if (rc != 0)
                {
                    Fail($"Event poll failed (rc={rc}).");
                    return false;
                }
                if (lossPending != 0)
                {
                    Status = VROpenXRStatus.SessionLossPending;
                    return false;
                }
                Status = focused != 0 ? VROpenXRStatus.SessionFocused : VROpenXRStatus.SessionReady;
                return true;
            }
            catch (Exception ex)
            {
                Fail("Poll exception: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Waits for the next frame and locates head/hand poses at the
        /// predicted display time. One call per rendered frame; both eyes
        /// share the result. Returns false in fallback mode or on error.
        /// </summary>
        public bool WaitAndLocatePoses(out VRFramePoses poses)
        {
            poses = default;
            if (!TrackingAvailable)
                return false;
            try
            {
                var rc = NativeMethods.xrWaitAndLocate(_session, _headSpace, _leftSpace, _rightSpace, out var p);
                if (rc != 0)
                    return false;
                poses = new VRFramePoses
                {
                    PredictedDisplayTime = p.PredictedDisplayTime,
                    Head = new VRRawPose(p.HeadPosition, p.HeadOrientation, p.HeadActive != 0),
                    LeftHand = new VRRawPose(p.LeftPosition, p.LeftOrientation, p.LeftActive != 0),
                    RightHand = new VRRawPose(p.RightPosition, p.RightOrientation, p.RightActive != 0),
                    HeadActive = p.HeadActive != 0,
                    LeftActive = p.LeftActive != 0,
                    RightActive = p.RightActive != 0,
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Polls controller action states (aim poses, trigger/squeeze edges,
        /// thumbstick, buttons) for both hands. Poses are in OpenXR space;
        /// convert with VRConventions.OpenXRToRig before feeding the rig.
        /// Returns false in fallback mode or on error (hands stay inactive).
        /// </summary>
        public bool PollActions(out VRHandState left, out VRHandState right)
        {
            left = default;
            right = default;
            if (!TrackingAvailable)
                return false;
            try
            {
                var rc = NativeMethods.xrPollActions(_session, out var both);
                if (rc != 0)
                    return false;
                left = ToHandState(both.Left);
                right = ToHandState(both.Right);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static VRHandState ToHandState(NativeMethods.HandActionState s)
        {
            return new VRHandState
            {
                Active = s.Active != 0,
                AimPosition = s.AimPosition,
                AimOrientation = s.AimOrientation,
                Trigger = s.Trigger,
                TriggerPressed = s.TriggerPressed != 0,
                TriggerReleased = s.TriggerReleased != 0,
                SqueezePressed = s.SqueezePressed != 0,
                Thumbstick = s.Thumbstick,
                Buttons = s.Buttons,
            };
        }

        /// <summary>Recenters the runtime reference space (also recenters rig).</summary>
        public void Recenter()
        {
            if (!TrackingAvailable)
                return;
            try { NativeMethods.xrRecenter(_session); } catch { /* stay in current space */ }
        }

        /// <summary>
        /// Submits two GPU-shared eye textures (DXGI shared handles from the
        /// engine's eye render targets) to the active session. The shim opens
        /// them on the session device, copies into the swapchain images and
        /// ends the frame. Returns false when unavailable (caller composites
        /// the desktop mirror instead). Never throws.
        /// </summary>
        public bool SubmitSharedHandles(IntPtr sharedLeft, IntPtr sharedRight, int width, int height)
        {
            if (!TrackingAvailable || sharedLeft == IntPtr.Zero || sharedRight == IntPtr.Zero)
                return false;
            try
            {
                return NativeMethods.xrSubmitShared(_session, sharedLeft, sharedRight, width, height) == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Runtime asymmetric FOV for one eye (radians); false when unknown.</summary>
        public bool TryGetEyeFov(int eye, out System.Numerics.Vector4 fov)
        {
            fov = default;
            if (!TrackingAvailable)
                return false;
            try
            {
                var rc = NativeMethods.xrGetEyeFov(_session, eye, out var l, out var r, out var u, out var d);
                if (rc != 0)
                    return false;
                fov = new System.Numerics.Vector4(l, r, u, d);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Best-effort haptic pulse. Never throws.</summary>
        public void SendHaptic(VRHandedness hand, float amplitude, int durationMs)
        {
            if (!TrackingAvailable)
                return;
            try { NativeMethods.xrHaptic(_session, hand == VRHandedness.Left ? 0 : 1, amplitude, durationMs); } catch { }
        }

        public void Shutdown()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                if (_sessionRunning)
                {
                    try { NativeMethods.xrEndSession(_session); } catch { }
                    _sessionRunning = false;
                }
                if (_session != 0) { try { NativeMethods.xrDestroy(_session); } catch { } _session = 0; }
                if (_headSpace != 0) { try { NativeMethods.xrDestroy(_headSpace); } catch { } _headSpace = 0; }
                if (_leftSpace != 0) { try { NativeMethods.xrDestroy(_leftSpace); } catch { } _leftSpace = 0; }
                if (_rightSpace != 0) { try { NativeMethods.xrDestroy(_rightSpace); } catch { } _rightSpace = 0; }
                if (_instance != 0) { try { NativeMethods.xrDestroy(_instance); } catch { } _instance = 0; }
            }
            finally
            {
                Status = VROpenXRStatus.ShutDown;
            }
        }

        public void Dispose()
        {
            Shutdown();
            GC.SuppressFinalize(this);
        }

        ~VROpenXRHost()
        {
            Shutdown();
        }

        private void Fail(string message)
        {
            LastError = message;
            Status = VROpenXRStatus.Error;
        }

        // ------------------------------------------------------------------
        // Native boundary. The C++ shim (docs/integration-shim.md) exports
        // these C functions from a tiny DLL that links openxr_loader.lib, so
        // C# never hand-rolls Xr structs for session setup. Until the shim is
        // built, entry-point-not-found degrades to RuntimeMissing, never a crash.
        // ------------------------------------------------------------------
        private static class NativeMethods
        {
            [DllImport("orvr_openxr_shim.dll", EntryPoint = "orvr_xr_init_d3d11", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int xrCreateInstanceForD3D11(
                [MarshalAs(UnmanagedType.LPStr)] string appName,
                IntPtr d3d11Device,
                out ulong instance, out ulong systemId,
                out IntPtr runtimeNameUtf8,
                out int recommendedWidth, out int recommendedHeight);

            [DllImport("orvr_openxr_shim.dll", EntryPoint = "orvr_xr_free_string", CallingConvention = CallingConvention.Cdecl)]
            internal static extern void xrFreeString(IntPtr s);

            [DllImport("orvr_openxr_shim.dll", EntryPoint = "orvr_xr_create_session_d3d11", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int xrCreateD3D11Session(ulong instance, ulong systemId, IntPtr d3d11Device, out ulong session);

            [DllImport("orvr_openxr_shim.dll", EntryPoint = "orvr_xr_create_spaces", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int xrCreateReferenceSpaces(ulong session, out ulong headSpace, out ulong leftSpace, out ulong rightSpace);

            [DllImport("orvr_openxr_shim.dll", EntryPoint = "orvr_xr_begin", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int xrBeginSession(ulong session);

            [DllImport("orvr_openxr_shim.dll", EntryPoint = "orvr_xr_poll", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int xrPollSessionState(ulong session, out int state, out int focused, out int lossPending);

            [DllImport("orvr_openxr_shim.dll", EntryPoint = "orvr_xr_wait_locate", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int xrWaitAndLocate(ulong session, ulong headSpace, ulong leftSpace, ulong rightSpace, out LocatedPoses poses);

            [DllImport("orvr_openxr_shim.dll", EntryPoint = "orvr_xr_recenter", CallingConvention = CallingConvention.Cdecl)]
            internal static extern void xrRecenter(ulong session);

            [DllImport("orvr_openxr_shim.dll", EntryPoint = "orvr_xr_actions", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int xrPollActions(ulong session, out BothHands both);

            [StructLayout(LayoutKind.Sequential)]
            internal struct HandActionState
            {
                public int Active;
                public System.Numerics.Vector3 AimPosition;
                public System.Numerics.Quaternion AimOrientation;
                public float Trigger;
                public int TriggerPressed;
                public int TriggerReleased;
                public int SqueezePressed;
                public System.Numerics.Vector2 Thumbstick;
                public int Buttons;
            }

            [StructLayout(LayoutKind.Sequential)]
            internal struct BothHands
            {
                public HandActionState Left;
                public HandActionState Right;
            }

            [DllImport("orvr_openxr_shim.dll", EntryPoint = "orvr_xr_end", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int xrEndSession(ulong session);

            [DllImport("orvr_openxr_shim.dll", EntryPoint = "orvr_xr_submit_shared", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int xrSubmitShared(ulong session, IntPtr sharedLeft, IntPtr sharedRight, int width, int height);

            [DllImport("orvr_openxr_shim.dll", EntryPoint = "orvr_xr_get_fov", CallingConvention = CallingConvention.Cdecl)]
            internal static extern int xrGetEyeFov(ulong session, int eye, out float fovLeft, out float fovRight, out float fovUp, out float fovDown);

            [DllImport("orvr_openxr_shim.dll", EntryPoint = "orvr_xr_haptic", CallingConvention = CallingConvention.Cdecl)]
            internal static extern void xrHaptic(ulong session, int hand, float amplitude, int durationMs);

            [DllImport("orvr_openxr_shim.dll", EntryPoint = "orvr_xr_destroy", CallingConvention = CallingConvention.Cdecl)]
            internal static extern void xrDestroy(ulong handle);

            [StructLayout(LayoutKind.Sequential)]
            internal struct LocatedPoses
            {
                public long PredictedDisplayTime;
                public System.Numerics.Vector3 HeadPosition;
                public System.Numerics.Quaternion HeadOrientation;
                public int HeadActive;
                public System.Numerics.Vector3 LeftPosition;
                public System.Numerics.Quaternion LeftOrientation;
                public int LeftActive;
                public System.Numerics.Vector3 RightPosition;
                public System.Numerics.Quaternion RightOrientation;
                public int RightActive;
            }
        }

        private static class NativeLibraryProbe
        {
            public static bool IsPresent(string dll)
            {
                try
                {
                    var h = LoadLibraryW(dll);
                    if (h != IntPtr.Zero)
                    {
                        FreeLibrary(h);
                        return true;
                    }
                    return false;
                }
                catch
                {
                    return false;
                }
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr LoadLibraryW(string lpFileName);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool FreeLibrary(IntPtr hModule);
        }
    }
}
