// Orts.VR.Tests — dependency-free test harness (no NuGet test SDK).
// Run: dotnet run --project Source/Orts.VR.Tests
// Exit code 0 = all pass; prints PASS/FAIL per test.

using System;
using System.Collections.Generic;
using System.Numerics;
using Orts.VR;

namespace Orts.VR.Tests
{
    internal static class Assert
    {
        public static void True(bool cond, string what)
        {
            if (!cond) throw new Exception("assert true: " + what);
        }

        public static void False(bool cond, string what)
        {
            if (cond) throw new Exception("assert false: " + what);
        }

        public static void Equal(float expected, float actual, float eps, string what)
        {
            if (Math.Abs(expected - actual) > eps)
                throw new Exception($"assert equal {what}: expected {expected}, got {actual}");
        }

        public static void Equal(int expected, int actual, string what)
        {
            if (expected != actual)
                throw new Exception($"assert equal {what}: expected {expected}, got {actual}");
        }
    }

    internal static class Program
    {
        private static int _pass;
        private static int _fail;
        private static readonly List<string> _failures = new List<string>();

        private static void Test(string name, Action body)
        {
            try
            {
                body();
                _pass++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception ex)
            {
                _fail++;
                _failures.Add(name + ": " + ex.Message);
                Console.WriteLine("FAIL " + name + " -- " + ex.Message);
            }
        }

        internal static int Main()
        {
            // ---- VRStereoMath ------------------------------------------------
            Test("EyeOffsetX signs", () =>
            {
                Assert.Equal(-0.0315f, VRStereoMath.EyeOffsetX(VREye.Left, 0.063f), 1e-6f, "left");
                Assert.Equal(0.0315f, VRStereoMath.EyeOffsetX(VREye.Right, 0.063f), 1e-6f, "right");
            });

            Test("PerEyeAspect halves full aspect", () =>
            {
                Assert.Equal(0.8889f, VRStereoMath.PerEyeAspect(16f / 9f), 1e-4f, "aspect");
            });

            Test("EyeViews differ by IPD, share sim state", () =>
            {
                var rig = new VRCameraRig { IpdMeters = 0.064f };
                var baseView = Matrix4x4.Identity;
                rig.ComputeEyeViews(baseView, out var l, out var r);
                // V_eye = V * H^-1 * T(-eyeOffset): with identity base/head,
                // left eye (offset -IPD/2) shifts the world +IPD/2 in X.
                Assert.Equal(0.032f, l.View.Translation.X, 1e-5f, "left shift");
                Assert.Equal(-0.032f, r.View.Translation.X, 1e-5f, "right shift");
                var sep = l.View.Translation.X - r.View.Translation.X;
                Assert.Equal(0.064f, sep, 1e-5f, "ipd separation");
                Assert.Equal(l.View.Translation.Y, r.View.Translation.Y, 1e-6f, "same Y");
                Assert.Equal(l.View.Translation.Z, r.View.Translation.Z, 1e-6f, "same Z");
            });

            Test("Eye separation stays IPD under rotated base", () =>
            {
                var rig = new VRCameraRig { IpdMeters = 0.064f };
                var b = Matrix4x4.CreateLookAt(new Vector3(10, 3, -5), new Vector3(10, 3, 50), Vector3.UnitY);
                rig.ComputeEyeViews(b, out var l, out var r);
                var pl = Vector3.Transform(Vector3.Zero, l.View);
                var pr = Vector3.Transform(Vector3.Zero, r.View);
                Assert.Equal(0.064f, (pl - pr).Length(), 1e-4f, "parallax == IPD");
            });

            Test("Head moving right shifts world left", () =>
            {
                var rig = new VRCameraRig { IpdMeters = 0.0f };
                rig.UpdateHead(new VRRawPose(new Vector3(0.1f, 0f, 0f), Quaternion.Identity));
                rig.ComputeEyeViews(Matrix4x4.Identity, out var l, out _);
                var p = Vector3.Transform(Vector3.Zero, l.View);
                Assert.Equal(-0.1f, p.X, 1e-5f, "world shifts opposite head");
            });

            Test("OpenXR-to-rig convention invariants", () =>
            {
                // Identity stays identity.
                var id = VRConventions.OpenXRToRig(Vector3.Zero, Quaternion.Identity);
                Assert.Equal(0f, id.Position.Length(), 1e-6f, "id pos");
                // OpenXR forward (0,0,-1) must become rig forward (0,0,+1).
                var fwd = VRConventions.OpenXRToRig(Vector3.Zero, Quaternion.Identity);
                var h = new VRHeadPose();
                h.Update(fwd);
                var f = h.HeadForward();
                Assert.Equal(0f, f.X, 1e-5f, "fwd x");
                Assert.Equal(0f, f.Y, 1e-5f, "fwd y");
                Assert.Equal(1f, f.Z, 1e-5f, "fwd z");
                // Position Z mirrors.
                var p = VRConventions.OpenXRToRig(new Vector3(1, 2, 3), Quaternion.Identity);
                Assert.Equal(1f, p.Position.X, 1e-6f, "px");
                Assert.Equal(2f, p.Position.Y, 1e-6f, "py");
                Assert.Equal(-3f, p.Position.Z, 1e-6f, "pz");
                // Involution: converting twice is identity.
                var q = Quaternion.Normalize(new Quaternion(0.1f, 0.2f, 0.3f, 0.9f));
                var once = VRConventions.OpenXRToRig(new Vector3(0.5f, -0.2f, 0.7f), q);
                var twice = VRConventions.OpenXRToRig(once.Position, once.Orientation);
                Assert.Equal(0.5f, twice.Position.X, 1e-5f, "inv x");
                Assert.Equal(-0.2f, twice.Position.Y, 1e-5f, "inv y");
                Assert.Equal(0.7f, twice.Position.Z, 1e-5f, "inv z");
                Assert.Equal(q.X, twice.Orientation.X, 1e-5f, "inv qx");
                Assert.Equal(q.Y, twice.Orientation.Y, 1e-5f, "inv qy");
                Assert.Equal(q.Z, twice.Orientation.Z, 1e-5f, "inv qz");
                // OpenXR left turn (yaw +90 about Y, facing -X) stays facing -X in rig.
                var yawLeft = Quaternion.CreateFromYawPitchRoll((float)Math.PI / 2f, 0f, 0f);
                var mapped = VRConventions.OpenXRToRig(Vector3.Zero, yawLeft);
                var h2 = new VRHeadPose();
                h2.Update(mapped);
                var f2 = h2.HeadForward();
                Assert.Equal(-1f, f2.X, 1e-5f, "yaw-left faces -X");
                Assert.Equal(0f, f2.Z, 1e-4f, "yaw-left z~0");
            });

            Test("EyeViews deterministic for same state", () =>
            {
                var rig = new VRCameraRig();
                rig.UpdateHead(new VRRawPose(new Vector3(0.05f, 0.02f, 0.01f), Quaternion.Identity));
                var b = Matrix4x4.CreateLookAt(new Vector3(0, 0, 0), new Vector3(0, 0, 1), Vector3.UnitY);
                rig.ComputeEyeViews(b, out var l1, out var r1);
                rig.ComputeEyeViews(b, out var l2, out var r2);
                Assert.True(l1.View.Equals(l2.View), "left deterministic");
                Assert.True(r1.View.Equals(r2.View), "right deterministic");
            });

            Test("SymmetricProjection valid/invalid", () =>
            {
                Assert.True(VRStereoMath.TrySymmetricProjection(1.0f, 0.9f, 0.1f, 2000f, out var p), "valid");
                Assert.True(p.M11 > 0 && p.M22 > 0, "positive diag");
                Assert.False(VRStereoMath.TrySymmetricProjection(0f, 0.9f, 0.1f, 2000f, out _), "zero fov");
                Assert.False(VRStereoMath.TrySymmetricProjection(1.0f, -1f, 0.1f, 2000f, out _), "neg aspect");
                Assert.False(VRStereoMath.TrySymmetricProjection(1.0f, 0.9f, 0f, 2000f, out _), "zero near");
                Assert.False(VRStereoMath.TrySymmetricProjection(1.0f, 0.9f, 5f, 5f, out _), "far==near");
            });

            Test("FovProjection valid/invalid", () =>
            {
                Assert.True(VRStereoMath.TryProjectionFromFov(0.8f, 0.8f, 0.7f, 0.7f, 0.1f, 1000f, out _), "valid");
                Assert.False(VRStereoMath.TryProjectionFromFov(0f, 0f, 0.7f, 0.7f, 0.1f, 1000f, out _), "zero width");
                Assert.False(VRStereoMath.TryProjectionFromFov(0.8f, 0.8f, 0.7f, 0.7f, -1f, 1000f, out _), "neg near");
            });

            // ---- VRHeadPose ---------------------------------------------------
            Test("Recenter zeroes current pose", () =>
            {
                var h = new VRHeadPose();
                var raw = new VRRawPose(new Vector3(0.2f, 0.1f, 0.3f), Quaternion.CreateFromYawPitchRoll(0.5f, 0.2f, 0f));
                h.Update(raw);
                h.Recenter(raw);
                h.Update(raw);
                Assert.Equal(0f, h.RelativePosition.Length(), 1e-5f, "pos zero after recenter");
                var fwd = h.HeadForward();
                Assert.Equal(0f, fwd.X, 1e-5f, "fwd x");
                Assert.Equal(1f, fwd.Z, 1e-5f, "fwd z");
            });

            Test("Positional clamp radius + vertical", () =>
            {
                var h = new VRHeadPose { MaxRadiusM = 0.35f, MaxUpM = 0.3f, MaxDownM = 0.3f };
                h.Update(new VRRawPose(new Vector3(10f, 10f, 0f), Quaternion.Identity));
                var p = h.RelativePosition;
                var r = (float)Math.Sqrt(p.X * p.X + p.Z * p.Z);
                Assert.True(r <= 0.35001f, "radius clamped, got " + r);
                Assert.Equal(0.3f, p.Y, 1e-5f, "up clamped");
                h.Update(new VRRawPose(new Vector3(0f, -10f, 0f), Quaternion.Identity));
                Assert.Equal(-0.3f, h.RelativePosition.Y, 1e-5f, "down clamped");
            });

            Test("PositionalTracking off ignores translation", () =>
            {
                var h = new VRHeadPose { PositionalTracking = false };
                h.Update(new VRRawPose(new Vector3(1f, 1f, 1f), Quaternion.Identity));
                Assert.Equal(0f, h.RelativePosition.Length(), 1e-6f, "no translation");
                Assert.True(h.HasValidPose, "still valid (rotation)");
            });

            Test("Invalid pose clears validity; HeadInverse identity", () =>
            {
                var h = new VRHeadPose();
                h.Update(new VRRawPose(Vector3.Zero, Quaternion.Identity, false));
                Assert.False(h.HasValidPose, "invalid");
                Assert.True(h.HeadInverse(true).Equals(Matrix4x4.Identity), "inverse identity");
                Assert.True(h.HeadInverse(false).Equals(Matrix4x4.Identity), "disabled identity");
            });

            Test("Head rotation changes eye views, not train state", () =>
            {
                var rig = new VRCameraRig();
                var b = Matrix4x4.Identity;
                rig.ComputeEyeViews(b, out var l0, out _);
                rig.UpdateHead(new VRRawPose(Vector3.Zero, Quaternion.CreateFromYawPitchRoll(0.6f, 0f, 0f)));
                rig.ComputeEyeViews(b, out var l1, out _);
                Assert.False(l0.View.Equals(l1.View), "head yaw moves view");
            });

            // ---- VRCameraRig ---------------------------------------------------
            Test("Camera switch resets transient head delta", () =>
            {
                var rig = new VRCameraRig();
                rig.UpdateHead(new VRRawPose(new Vector3(0.1f, 0f, 0f), Quaternion.Identity));
                rig.ComputeEyeViews(Matrix4x4.Identity, out var moved, out _);
                rig.NotifyCameraSwitched();
                rig.ComputeEyeViews(Matrix4x4.Identity, out var reset, out _);
                // After reset with no new sample, head inverse is identity:
                // left eye == base shifted by -IPD/2 only.
                var expect = VRStereoMath.EyeView(Matrix4x4.Identity, Matrix4x4.Identity,
                    VRStereoMath.EyeOffsetX(VREye.Left, rig.IpdMeters));
                Assert.True(reset.View.Equals(expect), "switch clears delta");
                Assert.False(moved.View.Equals(reset.View), "delta was present before");
            });

            Test("Style setter auto-resets on change only", () =>
            {
                var rig = new VRCameraRig();
                rig.UpdateHead(new VRRawPose(new Vector3(0.1f, 0f, 0f), Quaternion.Identity));
                rig.Style = VRCameraStyle.Cab3D; // change -> reset
                rig.ComputeEyeViews(Matrix4x4.Identity, out var l, out _);
                var expect = VRStereoMath.EyeView(Matrix4x4.Identity, Matrix4x4.Identity,
                    VRStereoMath.EyeOffsetX(VREye.Left, rig.IpdMeters));
                Assert.True(l.View.Equals(expect), "style change resets");
            });

            Test("Style defaults set head limits", () =>
            {
                var rig = new VRCameraRig();
                rig.ApplyStyleDefaults(VRCameraStyle.Cab3D);
                Assert.Equal(0.35f, rig.Head.MaxRadiusM, 1e-6f, "cab radius");
                rig.ApplyStyleDefaults(VRCameraStyle.External);
                Assert.Equal(0.50f, rig.Head.MaxRadiusM, 1e-6f, "ext radius");
            });

            Test("ComputeEyes + OpenXR projections", () =>
            {
                var rig = new VRCameraRig();
                Assert.True(rig.ComputeEyes(Matrix4x4.Identity, 1.0f, 16f / 9f, 0.1f, 2000f, out var l, out var r), "fallback ok");
                Assert.False(rig.ComputeEyes(Matrix4x4.Identity, 0f, 16f / 9f, 0.1f, 2000f, out _, out _), "bad fov fails");
                Assert.True(rig.ApplyOpenXrProjections(ref l, ref r,
                    new Vector4(0.8f, 0.8f, 0.7f, 0.7f), new Vector4(0.8f, 0.8f, 0.7f, 0.7f), 0.1f, 1000f), "xr proj ok");
                Assert.False(rig.ApplyOpenXrProjections(ref l, ref r,
                    new Vector4(0f, 0f, 0f, 0f), new Vector4(0.8f, 0.8f, 0.7f, 0.7f), 0.1f, 1000f), "xr proj bad fails");
            });

            // ---- VRPicking ------------------------------------------------------
            Test("PickTopmost hits topmost, misses outside", () =>
            {
                var rects = new float[3, 4]
                {
                    { 0, 0, 100, 100 },
                    { 50, 50, 100, 100 },
                    { 200, 200, 0, 10 }, // degenerate, skipped
                };
                Assert.Equal(1, VRPicking.PickTopmost(75, 75, rects, 3), "overlap topmost");
                Assert.Equal(0, VRPicking.PickTopmost(10, 10, rects, 3), "first only");
                Assert.Equal(-1, VRPicking.PickTopmost(500, 500, rects, 3), "miss");
            });

            Test("RaySphere hit/miss/behind", () =>
            {
                var o = new Vector3(0, 0, 0);
                var d = new Vector3(0, 0, 1);
                Assert.True(VRPicking.RaySphere(o, d, new Vector3(0, 0, 5), 1f) > 0f, "hit");
                Assert.Equal(-1f, VRPicking.RaySphere(o, d, new Vector3(10, 0, 5), 1f), 1e-6f, "miss");
                Assert.Equal(-1f, VRPicking.RaySphere(o, d, new Vector3(0, 0, -5), 1f), 1e-6f, "behind");
            });

            // ---- VRGrabSession ---------------------------------------------------
            Test("Grab has no jump at anchor", () =>
            {
                var c = new VRMappableControl { MinValue = 0f, MaxValue = 100f, CurrentValue = 40f, FullRangePixels = 300f, VerticalDrag = true };
                var g = new VRGrabSession();
                g.Begin(c, 500f, VRHandedness.Right);
                Assert.True(g.Active, "active");
                Assert.Equal(40f, g.MoveTo(500f), 1e-4f, "no jump");
            });

            Test("Vertical drag up increases", () =>
            {
                var c = new VRMappableControl { MinValue = 0f, MaxValue = 1f, CurrentValue = 0.5f, FullRangePixels = 200f, VerticalDrag = true };
                var g = new VRGrabSession();
                g.Begin(c, 400f, VRHandedness.Right);
                var v = g.MoveTo(300f); // 100px up of 200px range -> +0.5
                Assert.Equal(1f, v, 1e-4f, "up increases to max");
                var v2 = g.MoveTo(500f); // 100px down -> -0.5 from grab
                Assert.Equal(0f, v2, 1e-4f, "down decreases to min");
            });

            Test("Notches snap within radius, limits clamp", () =>
            {
                var c = new VRMappableControl
                {
                    MinValue = 0f, MaxValue = 100f, CurrentValue = 0f,
                    FullRangePixels = 1000f, VerticalDrag = false,
                    Notches = new float[] { 0f, 25f, 50f, 75f, 100f },
                };
                // fraction 0.26 -> 26, within 8% of 25 -> snaps.
                Assert.Equal(25f, VRGrabSession.ValueFromFraction(c, 0.26f), 1e-4f, "snap");
                // fraction 0.40 -> 40, outside snap radius -> stays.
                Assert.Equal(40f, VRGrabSession.ValueFromFraction(c, 0.40f), 1e-4f, "no snap");
                // clamp.
                Assert.Equal(100f, VRGrabSession.ValueFromFraction(c, 2f), 1e-4f, "clamp high");
                Assert.Equal(0f, VRGrabSession.ValueFromFraction(c, -1f), 1e-4f, "clamp low");
            });

            Test("Rebase + End lifecycle", () =>
            {
                var c = new VRMappableControl { MinValue = 0f, MaxValue = 10f, CurrentValue = 5f, FullRangePixels = 100f, VerticalDrag = false };
                var g = new VRGrabSession();
                g.Begin(c, 0f, VRHandedness.Left);
                var v = g.MoveTo(50f); // +0.5 range -> 10
                Assert.Equal(10f, v, 1e-4f, "drag");
                g.Rebase(v, 50f);
                Assert.Equal(10f, g.MoveTo(50f), 1e-4f, "re-anchored");
                g.End();
                Assert.False(g.Active, "ended");
            });

            // ---- Guards / haptics --------------------------------------------------
            Test("Cab guard blocks external, allows cab", () =>
            {
                Assert.True(VRInteractionGuard.CanInteractWithCab(VRCameraStyle.Cab3D), "3d cab");
                Assert.True(VRInteractionGuard.CanInteractWithCab(VRCameraStyle.Cab2D), "2d cab");
                Assert.True(VRInteractionGuard.CanInteractWithCab(VRCameraStyle.Passenger), "passenger");
                Assert.False(VRInteractionGuard.CanInteractWithCab(VRCameraStyle.External), "external blocked");
                Assert.True(VRInteractionGuard.CanInteractWithUI(VRCameraStyle.External), "ui everywhere");
            });

            Test("Haptic presets in range", () =>
            {
                var s = VRHapticPulse.Select(VRHandedness.Right);
                Assert.True(s.Amplitude > 0 && s.Amplitude <= 1f, "amp");
                Assert.True(s.DurationMs > 0, "dur");
            });

            // ---- VRSettings ---------------------------------------------------------
            Test("Defaults keep desktop intact + validate clamps", () =>
            {
                var s = VRSettings.Default;
                Assert.False(s.VREnabled, "vr off by default");
                s.VRRenderScale = 99f; s.VRIPD = 0.001f; s.VRHeadRadiusM = 99f;
                s.Validate();
                Assert.Equal(2.0f, s.VRRenderScale, 1e-6f, "scale clamp");
                Assert.Equal(0.05f, s.VRIPD, 1e-6f, "ipd clamp");
                Assert.Equal(1.0f, s.VRHeadRadiusM, 1e-6f, "radius clamp");
            });

            Test("ApplyTo propagates to rig", () =>
            {
                var s = new VRSettings { VRIPD = 0.07f, VRRenderScale = 1.5f, VRHeadTracking = false, VRHeadRadiusM = 0.2f };
                var rig = new VRCameraRig();
                s.ApplyTo(rig);
                Assert.Equal(0.07f, rig.IpdMeters, 1e-6f, "ipd");
                Assert.Equal(1.5f, rig.RenderScale, 1e-6f, "scale");
                Assert.False(rig.HeadTrackingEnabled, "tracking off");
                Assert.Equal(0.2f, rig.Head.MaxRadiusM, 1e-6f, "radius");
            });

            Test("OpenXR host degrades without loader", () =>
            {
                var host = new VROpenXRHost();
                // This machine has no orvr_openxr_shim.dll; the host must NOT
                // throw and must report fallback status.
                var ok = host.Initialize(0, 0);
                Assert.False(ok, "init fails safely");
                Assert.True(host.Status == VROpenXRStatus.RuntimeMissing || host.Status == VROpenXRStatus.Error,
                    "fallback status, got " + host.Status);
                Assert.False(host.TrackingAvailable, "no tracking claimed");
                host.Shutdown();
                host.Shutdown(); // idempotent
                Assert.True(host.Status == VROpenXRStatus.ShutDown, "shutdown");
            });

            Console.WriteLine();
            Console.WriteLine($"RESULT {_pass} passed, {_fail} failed");
            foreach (var f in _failures)
                Console.WriteLine("  FAILED: " + f);
            return _fail == 0 ? 0 : 1;
        }
    }
}
