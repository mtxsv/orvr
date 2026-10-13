// ORVR — engine-side VR adapter. Lives in the RunActivity assembly
// (namespace Orts.Viewer3D.VR) and references the Orts.VR library.
//
// What it does:
//   - Owns one VRCameraRig + VROpenXRHost + VRGrabSession per Viewer.
//   - Updater thread (UpdateInteraction, called from Viewer.Update):
//     camera-switch detection, head-pose feed, recenter key, controller
//     action polling, cab-control actuation through the REAL mouse path
//     (ChangedValue override + HandleUserInput, the same pattern keyboard
//     control already uses in MSTSLocomotiveViewer), UI pointer overlay.
//   - Render thread (BeginDraw / DrawEye / EndDraw, called from
//     RenderProcess.Draw): pose sampling at predicted display time,
//     per-eye RenderTargets, stereo view/projection override, submit to
//     the OpenXR shim or side-by-side desktop composite (fallback).
//
// Desktop behavior is untouched when VREnabled is false: every hook
// early-outs and the original Draw path runs verbatim.

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Orts.Formats.Msts;
using Orts.Viewer3D.Processes;
using Orts.Viewer3D.RollingStock;
using Orts.VR;
using ORTS.Common;
using ORTS.Common.Input;
using ORTS.Settings;
using SN = System.Numerics;

namespace Orts.Viewer3D.VR
{
    public sealed class VRSystem : IDisposable
    {
        readonly Viewer Viewer;
        readonly VRCameraRig Rig = new VRCameraRig();
        readonly VROpenXRHost Host = new VROpenXRHost();
        readonly VRSettings VrSettings = VRSettings.Default;
        readonly VRGrabSession Grab = new VRGrabSession();
        readonly object Sync = new object();

        Camera LastCamera;
        readonly Dictionary<Camera, float> OriginalFov = new Dictionary<Camera, float>();
        bool FovBoosted;

        RenderTarget2D EyeLeftTarget;
        RenderTarget2D EyeRightTarget;
        SpriteBatch CompositeBatch;

        // Latest tracked samples (render thread writes, updater reads).
        VRFramePoses Poses;
        VRHandState HandLeft;
        VRHandState HandRight;
        bool HaveSamples;

        // VR pointer in screen pixels (drives UserInput overlay).
        float PointerX, PointerY;
        bool PointerOnScreen;
        bool PointerLeftDown, PointerLeftPressed, PointerLeftReleased;
        bool LastTriggerDown;

        bool Disposed;

        /// <summary>Cull-margin widening (degrees) so stereo fringe pixels are never wrongly culled.</summary>
        const float CullMarginDegrees = 12f;

        public VRSystem(Viewer viewer)
        {
            Viewer = viewer ?? throw new ArgumentNullException(nameof(viewer));
            LoadSettings();
        }

        public bool IsActive => !Disposed && VrSettings.VREnabled;

        void LoadSettings()
        {
            // UserSettings persists bool/int/string only, so VR floats are
            // stored as percent / millimeters integers (see UserSettings.VR.inc).
            var s = Viewer.Settings;
            VrSettings.VREnabled = s.VREnabled;
            VrSettings.VRRenderScale = s.VRRenderScalePercent / 100f;
            VrSettings.VRIPD = s.VRIPDMillimeters / 1000f;
            VrSettings.VRHeadTracking = s.VRHeadTracking;
            VrSettings.VRPositionalTracking = s.VRPositionalTracking;
            VrSettings.VRHeadRadiusM = s.VRHeadRadiusMM / 1000f;
            VrSettings.VRHeadMaxUpM = s.VRHeadMaxUpMM / 1000f;
            VrSettings.VRHeadMaxDownM = s.VRHeadMaxDownMM / 1000f;
            VrSettings.VRDominantHand = s.VRDominantHandRight ? VRHandedness.Right : VRHandedness.Left;
            VrSettings.VRHaptics = s.VRHaptics;
            VrSettings.Validate();
            VrSettings.ApplyTo(Rig);
        }

        // ------------------------------------------------------------------
        // Updater thread
        // ------------------------------------------------------------------

        /// <summary>Called from Viewer.Update after HandleUserInput.</summary>
        public void UpdateInteraction(RenderFrame frame, ElapsedTime elapsedTime)
        {
            LoadSettings();
            if (!IsActive)
            {
                RestoreFov();
                UserInput.ClearVRPointer();
                return;
            }

            var camera = Viewer.Camera;
            if (camera == null)
                return;
            if (!ReferenceEquals(camera, LastCamera))
            {
                LastCamera = camera;
                Rig.NotifyCameraSwitched();
                Rig.Style = MapStyle(camera.Style);
                Rig.ApplyStyleDefaults(Rig.Style);
                BoostFov(camera);
            }

            if (UserInput.IsPressed(UserCommand.VRRecenter))
                Recenter();

            // Feed the latest head sample (render thread caches it).
            VRFramePoses poses;
            lock (Sync) poses = Poses;
            if (HaveSamples && poses.HeadActive)
                Rig.UpdateHead(VRConventions.OpenXRToRig(ToNumerics(poses.Head.Position), ToNumerics(poses.Head.Orientation), true));

            UpdateControllerInteraction(elapsedTime);
        }

        void Recenter()
        {
            VRFramePoses poses;
            lock (Sync) poses = Poses;
            if (HaveSamples && poses.HeadActive)
            {
                var raw = new VRRawPose(ToNumerics(poses.Head.Position), ToNumerics(poses.Head.Orientation), true);
                Rig.Recenter(raw);
            }
            else
            {
                Rig.Recenter();
            }
            try { Host.Recenter(); } catch { /* fallback has no runtime */ }
        }

        void UpdateControllerInteraction(ElapsedTime elapsedTime)
        {
            VRHandState left, right;
            lock (Sync) { left = HandLeft; right = HandRight; }
            var hand = VrSettings.VRDominantHand == VRHandedness.Right ? right : left;
            var style = Rig.Style;

            if (!HaveSamples || !hand.Active)
            {
                UserInput.ClearVRPointer();
                PointerOnScreen = false;
                return;
            }

            // Head-anchored angular pointer: hand direction relative to head,
            // mapped through the mono FOV to screen pixels. Calibration-free
            // (relative rotations are space-invariant) and stable.
            VRFramePoses poses;
            lock (Sync) poses = Poses;
            var pointer = ComputePointer(hand, poses, style);
            PointerX = pointer.X; PointerY = pointer.Y; PointerOnScreen = pointer.OnScreen;

            var triggerDown = hand.Trigger > 0.5f || hand.TriggerPressed;
            PointerLeftPressed = hand.TriggerPressed || (!LastTriggerDown && triggerDown);
            PointerLeftReleased = hand.TriggerReleased || (LastTriggerDown && !triggerDown);
            PointerLeftDown = triggerDown;
            LastTriggerDown = triggerDown;

            if (pointer.OnScreen)
                UserInput.SetVRPointer(true, (int)pointer.X, (int)pointer.Y, PointerLeftDown, PointerLeftPressed, PointerLeftReleased);
            else
                UserInput.ClearVRPointer();

            // Cab controls only from cab-family cameras (exterior guard).
            if (VRInteractionGuard.CanInteractWithCab(style))
                UpdateCabGrab(hand, pointer, (float)elapsedTime.RealSeconds);
            else if (Grab.Active)
                Grab.End();
        }

        struct PointerResult { public float X, Y; public bool OnScreen; }

        PointerResult ComputePointer(VRHandState hand, VRFramePoses poses, VRCameraStyle style)
        {
            var result = new PointerResult();
            var w = Math.Max(1, Viewer.DisplaySize.X);
            var h = Math.Max(1, Viewer.DisplaySize.Y);
            if (!poses.HeadActive)
                return result;

            // Relative hand-vs-head rotation (OpenXR space, space-invariant).
            var qHead = ToNumerics(poses.Head.Orientation);
            var qHand = ToNumerics(hand.AimOrientation);
            var rel = SN.Quaternion.Normalize(SN.Quaternion.Multiply(SN.Quaternion.Inverse(SN.Quaternion.Normalize(qHead)), SN.Quaternion.Normalize(qHand)));
            // To rig convention (yaw/pitch negate, roll same).
            rel = new SN.Quaternion(-rel.X, -rel.Y, rel.Z, rel.W);
            var dir = SN.Vector3.Transform(SN.Vector3.UnitZ, rel);
            if (dir.Z <= 0.05f)
                return result; // pointing backwards / edge-on: lift pointer

            var fovY = MathHelper.ToRadians(BaseFov());
            var aspect = (float)w / h;
            var tanHalfY = (float)Math.Tan(fovY / 2);
            var tanHalfX = tanHalfY * aspect;
            var nx = (dir.X / dir.Z) / tanHalfX;  // -1..1 across width
            var ny = -(dir.Y / dir.Z) / tanHalfY; // -1..1 down height
            result.X = (nx * 0.5f + 0.5f) * w;
            result.Y = (ny * 0.5f + 0.5f) * h;
            result.OnScreen = result.X >= 0 && result.X < w && result.Y >= 0 && result.Y < h;
            return result;
        }

        float BaseFov()
        {
            var camera = Viewer.Camera;
            if (camera != null && OriginalFov.TryGetValue(camera, out var fov))
                return fov;
            return Viewer.Settings.ViewingFOV;
        }

        void UpdateCabGrab(VRHandState hand, PointerResult pointer, float elapsedSeconds)
        {
            var locoViewer = Viewer.PlayerLocomotiveViewer as MSTSLocomotiveViewer;
            if (locoViewer == null)
                return;
            var renderer = ActiveCabRenderer(locoViewer);
            if (renderer == null)
                return;

            if (!Grab.Active)
            {
                // Hover highlight comes free via UserInput overlay (IsMouseWithin).
                // Trigger press on a mouse control starts press-and-hold or grab.
                if (PointerLeftPressed && pointer.OnScreen)
                {
                    var target = PickControl(renderer, pointer.X, pointer.Y);
                    if (target != null)
                    {
                        var map = DescribeControl(target);
                        if (map.HasValue)
                        {
                            Grab.Begin(map.Value, PrimaryAxis(map.Value, pointer), DominantHand());
                            ActuatePress(target);
                            Haptic(VRHapticPulse.Grab(DominantHand()));
                        }
                    }
                }
                return;
            }

            // Continue grab: motion -> value through the real path.
            var current = GrabTarget(renderer);
            if (current == null)
            {
                Grab.End();
                return;
            }
            var mapNow = DescribeControl(current);
            if (!mapNow.HasValue)
            {
                Grab.End();
                return;
            }
            float value;
            if (Math.Abs(hand.Thumbstick.Y) > 0.15f && VrSettings.VRThumbstickAdjusts)
                value = Grab.Nudge(Math.Max(1f / 240f, elapsedSeconds), hand.Thumbstick.Y);
            else
                value = Grab.MoveTo(PrimaryAxis(mapNow.Value, pointer));
            ActuateValue(current, value);
            Grab.Rebase(value, PrimaryAxis(mapNow.Value, pointer));

            if (PointerLeftReleased || !pointer.OnScreen)
            {
                Grab.End();
                UserInput.ClearVRPointer();
            }
        }

        VRHandedness DominantHand() => VrSettings.VRDominantHand;

        float PrimaryAxis(VRMappableControl map, PointerResult pointer)
        {
            return map.VerticalDrag ? pointer.Y : pointer.X;
        }

        CabRenderer ActiveCabRenderer(MSTSLocomotiveViewer locoViewer)
        {
            if (Viewer.Camera.Style == Camera.Styles.ThreeDimCab)
                return locoViewer.ThreeDimentionCabRenderer ?? locoViewer._CabRenderer;
            if (Viewer.Camera.Style == Camera.Styles.Cab)
                return locoViewer._CabRenderer;
            return null;
        }

        CabViewDiscreteRenderer GrabTarget(CabRenderer renderer)
        {
            // Re-pick under the pointer each frame so highlight + target agree.
            return PickControl(renderer, PointerX, PointerY);
        }

        CabViewDiscreteRenderer PickControl(CabRenderer renderer, float x, float y)
        {
            if (renderer.ControlMap == null)
                return null;
            CabViewDiscreteRenderer best = null;
            foreach (var kv in renderer.ControlMap)
            {
                if (!(kv.Value is CabViewDiscreteRenderer discrete))
                    continue;
                if (!discrete.isMouseControl())
                    continue;
                var rect = discrete.DestinationRectangleGet();
                if (rect.Width <= 0 || rect.Height <= 0)
                    continue;
                if (x >= rect.X && x < rect.X + rect.Width && y >= rect.Y && y < rect.Y + rect.Height)
                    best = discrete; // later entries draw on top
            }
            return best;
        }

        VRMappableControl? DescribeControl(CabViewDiscreteRenderer discrete)
        {
            var control = discrete.Control;
            if (control == null)
                return null;
            var map = new VRMappableControl
            {
                MinValue = (float)control.MinValue,
                MaxValue = (float)control.MaxValue,
                CurrentValue = discrete.GetRangeFraction() * ((float)control.MaxValue - (float)control.MinValue) + (float)control.MinValue,
                FullRangePixels = 300f,
                VerticalDrag = true,
                InvertDrag = false,
            };
            if (control is CVCWithFrames wf)
            {
                map.VerticalDrag = wf.Orientation > 0;
                map.InvertDrag = wf.Direction <= 0;
                if (wf.Values != null && wf.Values.Count > 1)
                {
                    var notches = new float[wf.Values.Count];
                    for (var i = 0; i < notches.Length; i++)
                        notches[i] = (float)wf.Values[i];
                    map.Notches = notches;
                }
            }
            if (map.Range <= 0)
                return null;
            return map;
        }

        /// <summary>Button/switch press: run the real mouse path once.</summary>
        void ActuatePress(CabViewDiscreteRenderer discrete)
        {
            try { discrete.HandleUserInput(); }
            catch { /* control decides; never break the frame */ }
            Haptic(VRHapticPulse.Select(DominantHand()));
        }

        /// <summary>Slider/lever move: ChangedValue override (keyboard pattern) + real path.</summary>
        void ActuateValue(CabViewDiscreteRenderer discrete, float value)
        {
            var old = discrete.ChangedValue;
            try
            {
                discrete.ChangedValue = (oldval) => value;
                discrete.HandleUserInput();
            }
            catch { /* control decides; never break the frame */ }
            finally
            {
                discrete.ChangedValue = old;
            }
        }

        void Haptic(VRHapticPulse pulse)
        {
            if (!VrSettings.VRHaptics)
                return;
            try { VRNative.Haptic(Host, pulse); } catch { }
        }

        // ------------------------------------------------------------------
        // Render thread
        // ------------------------------------------------------------------

        /// <summary>True when the stereo path should run instead of mono Draw.</summary>
        public bool BeginDraw(RenderProcess renderProcess)
        {
            if (!IsActive || Viewer.Camera == null)
                return false;

            // Session (lazy; safe to retry after failure -> falls back).
            if (Host.Status == VROpenXRStatus.Uninitialized || Host.Status == VROpenXRStatus.Error)
            {
                try
                {
                    Host.Initialize(VRNative.GetD3D11DevicePtr(renderProcess.GraphicsDevice));
                }
                catch
                {
                    // Stay in desktop-fallback stereo; never throw out of Draw.
                }
            }
            Host.PollEvents();

            if (Host.TrackingAvailable && Host.WaitAndLocatePoses(out var poses))
            {
                lock (Sync)
                {
                    Poses = poses;
                    HaveSamples = true;
                    if (Host.PollActions(out var l, out var r))
                    {
                        HandLeft = l;
                        HandRight = r;
                    }
                }
                Rig.UpdateHead(VRConventions.OpenXRToRig(ToNumerics(poses.Head.Position), ToNumerics(poses.Head.Orientation), poses.HeadActive));
            }

            EnsureEyeTargets(renderProcess);
            return EyeLeftTarget != null && EyeRightTarget != null;
        }

        /// <summary>Computes one eye's view/projection from the live camera.</summary>
        public bool GetEyeMatrices(VREye eye, out Matrix view, out Matrix projection)
        {
            view = Matrix.Identity;
            projection = Matrix.Identity;
            var camera = Viewer.Camera;
            if (camera == null)
                return false;
            var baseView = ToNumerics(camera.XnaView);
            Rig.ComputeEyeViews(baseView, out var l, out var r);
            var e = eye == VREye.Left ? l : r;
            view = ToXna(e.View);

            var fovY = MathHelper.ToRadians(BaseFov());
            var aspect = EyeAspect();
            if (!VRStereoMath.TrySymmetricProjection(fovY, aspect, camera.NearPlane, Viewer.Settings.ViewingDistance, out var proj))
                return false;
            // Prefer runtime asymmetric FOVs when the shim provides them.
            if (Host.TrackingAvailable && VRNative.TryGetEyeFov(Host, eye, out var fov))
                VRStereoMath.TryProjectionFromFov(fov.X, fov.Y, fov.Z, fov.W, camera.NearPlane, Viewer.Settings.ViewingDistance, out proj);
            projection = ToXna(proj);
            return true;
        }

        float EyeAspect()
        {
            if (EyeLeftTarget != null && EyeLeftTarget.Height > 0)
                return (float)EyeLeftTarget.Width / EyeLeftTarget.Height;
            var d = Viewer.DisplaySize;
            return VRStereoMath.PerEyeAspect((float)Math.Max(1, d.X) / Math.Max(1, d.Y));
        }

        public void DrawEye(RenderFrame frame, VREye eye)
        {
            if (!GetEyeMatrices(eye, out var view, out var proj))
                return;
            frame.SetStereoView(view, proj);
            frame.DrawEye(Viewer.RenderProcess.GraphicsDevice, eye == VREye.Left ? EyeLeftTarget : EyeRightTarget);
        }

        /// <summary>Submit to HMD or composite side-by-side; always leaves a desktop image.</summary>
        public void EndDraw(RenderProcess renderProcess)
        {
            var gd = renderProcess.GraphicsDevice;
            try
            {
                if (Host.TrackingAvailable)
                {
                    if (!VRNative.Submit(Host, EyeLeftTarget, EyeRightTarget))
                        CompositeSideBySide(gd);
                    else
                        CompositeSideBySide(gd); // desktop mirror always on
                }
                else
                {
                    CompositeSideBySide(gd);
                }
            }
            catch
            {
                try { CompositeSideBySide(gd); } catch { }
            }
        }

        void EnsureEyeTargets(RenderProcess renderProcess)
        {
            var gd = renderProcess.GraphicsDevice;
            int w = 1600, h = 1600;
            if (Host.RecommendedWidth > 0 && Host.RecommendedHeight > 0)
            {
                w = Math.Max(64, (int)(Host.RecommendedWidth * VrSettings.VRRenderScale));
                h = Math.Max(64, (int)(Host.RecommendedHeight * VrSettings.VRRenderScale));
                w = Math.Min(w, 4096); h = Math.Min(h, 4096);
            }
            try
            {
                if (EyeLeftTarget == null || EyeLeftTarget.IsDisposed || EyeLeftTarget.Width != w || EyeLeftTarget.Height != h)
                {
                    EyeLeftTarget?.Dispose();
                    EyeRightTarget?.Dispose();
                    var shared = Host.TrackingAvailable; // shared only matters for native submit
                    EyeLeftTarget = new RenderTarget2D(gd, w, h, false, SurfaceFormat.Color, DepthFormat.Depth24, 0, RenderTargetUsage.DiscardContents, shared);
                    EyeRightTarget = new RenderTarget2D(gd, w, h, false, SurfaceFormat.Color, DepthFormat.Depth24, 0, RenderTargetUsage.DiscardContents, shared);
                }
                if (CompositeBatch == null || CompositeBatch.IsDisposed)
                    CompositeBatch = new SpriteBatch(gd);
            }
            catch
            {
                EyeLeftTarget = null;
                EyeRightTarget = null;
            }
        }

        void CompositeSideBySide(GraphicsDevice gd)
        {
            if (EyeLeftTarget == null || EyeRightTarget == null)
                return;
            gd.SetRenderTarget(null);
            var pp = gd.PresentationParameters;
            gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.Black, 1, 0);
            CompositeBatch.Begin(SpriteSortMode.Immediate, BlendState.Opaque, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
            CompositeBatch.Draw(EyeLeftTarget, new Rectangle(0, 0, pp.BackBufferWidth / 2, pp.BackBufferHeight), Color.White);
            CompositeBatch.Draw(EyeRightTarget, new Rectangle(pp.BackBufferWidth / 2, 0, pp.BackBufferWidth - pp.BackBufferWidth / 2, pp.BackBufferHeight), Color.White);
            // Controller pointer dot so desktop users see what VR targets.
            if (PointerOnScreen)
            {
                var s = Math.Max(3, pp.BackBufferWidth / 320);
                var px = (int)(PointerX / Math.Max(1, Viewer.DisplaySize.X) * pp.BackBufferWidth / 2);
                var py = (int)(PointerY / Math.Max(1, Viewer.DisplaySize.Y) * pp.BackBufferHeight);
                CompositeBatch.Draw(EyeLeftTarget, new Rectangle(px - s / 2, py - s / 2, s, s), Color.Yellow);
            }
            CompositeBatch.End();
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        static VRCameraStyle MapStyle(Camera.Styles style)
        {
            switch (style)
            {
                case Camera.Styles.Cab: return VRCameraStyle.Cab2D;
                case Camera.Styles.ThreeDimCab: return VRCameraStyle.Cab3D;
                case Camera.Styles.Passenger: return VRCameraStyle.Passenger;
                default: return VRCameraStyle.External;
            }
        }

        void BoostFov(Camera camera)
        {
            try
            {
                if (!OriginalFov.ContainsKey(camera))
                    OriginalFov[camera] = camera.FieldOfView;
                camera.FieldOfView = OriginalFov[camera] + CullMarginDegrees;
                camera.ScreenChanged();
                FovBoosted = true;
            }
            catch { }
        }

        void RestoreFov()
        {
            if (!FovBoosted)
                return;
            FovBoosted = false;
            try
            {
                foreach (var kv in OriginalFov)
                {
                    kv.Key.FieldOfView = kv.Value;
                    kv.Key.ScreenChanged();
                }
                OriginalFov.Clear();
                LastCamera = null;
            }
            catch { }
        }

        internal static SN.Vector3 ToNumerics(Vector3 v) => new SN.Vector3(v.X, v.Y, v.Z);
        internal static SN.Vector3 ToNumerics(SN.Vector3 v) => v;
        internal static SN.Quaternion ToNumerics(Quaternion q) => new SN.Quaternion(q.X, q.Y, q.Z, q.W);
        internal static SN.Quaternion ToNumerics(SN.Quaternion q) => q;
        internal static SN.Matrix4x4 ToNumerics(Matrix m) => new SN.Matrix4x4(
            m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);
        internal static Matrix ToXna(SN.Matrix4x4 m) => new Matrix(
            m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);

        public void Dispose()
        {
            if (Disposed)
                return;
            Disposed = true;
            try { RestoreFov(); } catch { }
            try { UserInput.ClearVRPointer(); } catch { }
            try { Host.Shutdown(); } catch { }
            try { EyeLeftTarget?.Dispose(); } catch { }
            try { EyeRightTarget?.Dispose(); } catch { }
            try { CompositeBatch?.Dispose(); } catch { }
        }
    }
}
