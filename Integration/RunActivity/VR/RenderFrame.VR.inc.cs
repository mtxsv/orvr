// ORVR — RenderFrame additions for Source/RunActivity/Viewer3D/RenderFrame.cs
// (class RenderFrame). Purely additive except DrawSimple, whose replacement
// below behaves identically for the mono path.
//
// A. Insert the three methods (SetStereoView, DrawShadowsOnly, DrawEye) after
//    the existing SetCamera method.
// B. Replace the existing DrawSimple(GraphicsDevice, bool) with the
//    DrawSimple + DrawSimpleTo pair at the end of this file.

// ==== ORVR-EYE-METHODS-BEGIN ====
/// <summary>
/// ORVR: overrides the view/projection used by sequence rendering for one eye.
/// Must be called on the render thread after SetCamera and before DrawEye.
/// </summary>
public void SetStereoView(Matrix view, Matrix projection)
{
    XNACameraView = view;
    XNACameraProjection = projection;
}

/// <summary>
/// ORVR: renders shadow maps once per frame; both eyes share them.
/// Mirrors the shadow section of Draw(GraphicsDevice) without the mono pass.
/// </summary>
public void DrawShadowsOnly(GraphicsDevice graphicsDevice)
{
    if (Game.Settings.DynamicShadows && (RenderProcess.ShadowMapCount > 0) && ShadowMapMaterial != null)
        DrawShadows(graphicsDevice, false);
    for (var shadowMapIndex = 0; shadowMapIndex < RenderProcess.ShadowMapCount; shadowMapIndex++)
        Game.RenderProcess.ShadowPrimitiveCount[shadowMapIndex] = RenderShadowSceneryItems[shadowMapIndex].Count + RenderShadowForestItems[shadowMapIndex].Count + RenderShadowTerrainItems[shadowMapIndex].Count;
}

/// <summary>
/// ORVR: draws all prepared sequences into one eye target using the stereo
/// view/projection installed by SetStereoView. Screen-space cab sprites and
/// overlay popups draw identically in both eyes (eye targets are full-size),
/// while the 3D world genuinely differs per eye.
/// </summary>
public void DrawEye(GraphicsDevice graphicsDevice, RenderTarget2D eyeTarget)
{
    DrawSimpleTo(graphicsDevice, false, eyeTarget, false);
    for (var i = 0; i < (int)RenderPrimitiveSequence.Sentinel; i++)
        Game.RenderProcess.PrimitiveCount[i] = RenderItems[i].Values.Sum(l => l.Count);
}
// ==== ORVR-EYE-METHODS-END ====

// ==== ORVR-DRAWSIMPLE-BEGIN ====
/// <summary>
/// Executed in the RenderProcess thread - simple draw
/// </summary>
/// <param name="graphicsDevice"></param>
/// <param name="logging"></param>
void DrawSimple(GraphicsDevice graphicsDevice, bool logging)
{
    DrawSimpleTo(graphicsDevice, logging, RenderSurface, true);
}

/// <summary>
/// ORVR: target-parameterized draw core. Mono path passes RenderSurface with
/// presentToBackbuffer:true (original behavior); VR passes one eye target
/// with presentToBackbuffer:false (caller composites/submits).
/// </summary>
void DrawSimpleTo(GraphicsDevice graphicsDevice, bool logging, RenderTarget2D surface, bool presentToBackbuffer)
{
    // Mono path (presentToBackbuffer:true) keeps the original
    // RenderSurfaceMaterial null-guards bit-for-bit; eye path binds its target.
    if (presentToBackbuffer ? RenderSurfaceMaterial != null : surface != null)
    {
        graphicsDevice.SetRenderTarget(surface);
    }

    if (Game.Settings.DistantMountains)
    {
        if (logging) Console.WriteLine("  DrawSimple (Distant Mountains) {");
        graphicsDevice.Clear(ClearOptions.Target | ClearOptions.DepthBuffer | ClearOptions.Stencil, Color.Transparent, 1, 0);
        DrawSequencesDistantMountains(graphicsDevice, logging);
        if (logging) Console.WriteLine("  }");
        if (logging) Console.WriteLine("  DrawSimple {");
        graphicsDevice.Clear(ClearOptions.DepthBuffer, Color.Transparent, 1, 0);
        DrawSequences(graphicsDevice, logging);
        if (logging) Console.WriteLine("  }");
    }
    else
    {
        if (logging) Console.WriteLine("  DrawSimple {");
        graphicsDevice.Clear(ClearOptions.Target | ClearOptions.DepthBuffer | ClearOptions.Stencil, Color.Transparent, 1, 0);
        DrawSequences(graphicsDevice, logging);
        if (logging) Console.WriteLine("  }");
    }

    if (surface != null && presentToBackbuffer && RenderSurfaceMaterial != null)
    {
        graphicsDevice.SetRenderTarget(null);
        graphicsDevice.Clear(ClearOptions.Target | ClearOptions.DepthBuffer | ClearOptions.Stencil, Color.Transparent, 1, 0);
        RenderSurfaceMaterial.SetState(graphicsDevice, null);
        RenderSurfaceMaterial.SpriteBatch.Draw(RenderSurface, Vector2.Zero, Color.White);
        RenderSurfaceMaterial.ResetState(graphicsDevice);
    }
}
// ==== ORVR-DRAWSIMPLE-END ====
