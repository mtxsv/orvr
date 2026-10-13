<#
.SYNOPSIS
  Applies the ORVR VR patch to an Open Rails checkout (idempotent).
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File Apply-VR-Patch.ps1 `
    -Checkout C:\OR-Source `
    -OrvrRepo C:\orvr
.NOTES
  - Creates *.orvr-bak backups before the first edit of each file.
  - Safe to re-run: every step checks for its ORVR marker first.
  - Requires the Orts.VR library at <OrvrRepo>\Source\Orts.VR\Orts.VR.csproj.
#>
param(
    [Parameter(Mandatory = $true)][string]$Checkout,
    [Parameter(Mandatory = $true)][string]$OrvrRepo,
    [switch]$Net472
)

$ErrorActionPreference = 'Stop'

function Get-RelativePath([string]$from, [string]$to) {
    $fromUri = New-Object Uri((Join-Path $from 'x'))
    $toUri = New-Object Uri($to)
    $rel = $fromUri.MakeRelativeUri($toUri).ToString().Replace('/', '\')
    return [System.Uri]::UnescapeDataString($rel)
}

function Backup-Once([string]$path) {
    $bak = "$path.orvr-bak"
    if (-not (Test-Path -LiteralPath $bak)) {
        Copy-Item -LiteralPath $path -Destination $bak -Force
        Write-Output "backup: $bak"
    }
}

function Insert-AfterAnchorOnce([string]$path, [string]$marker, [string]$anchor, [string]$insert) {
    # Line-aware: inserts after the END OF THE LINE containing the anchor,
    # so the same anchor works on trees with different line prefixes
    # (e.g. "RenderFrame CurrentFrame;" vs "public RenderFrame CurrentFrame;").
    $text = [IO.File]::ReadAllText($path)
    if ($text.Contains($marker)) { Write-Output "skip (marker present): $path :: $marker"; return }
    $idx = $text.IndexOf($anchor, [StringComparison]::Ordinal)
    if ($idx -lt 0) { throw "anchor not found in $path : $anchor" }
    Backup-Once $path
    $eol = $text.IndexOf("`n", $idx)
    $text = $text.Insert($eol + 1, $insert)
    [IO.File]::WriteAllText($path, $text)
    Write-Output "patched: $path :: $marker"
}

function Wrap-ReturnOnce([string]$path, [string]$marker, [string]$lineContains, [string]$vrExpr) {
    # Tree-agnostic getter wrap: finds the full property line containing
    # $lineContains (must look like "{ get { return EXPR; } }") and rewrites
    # it as "{ get { return VRPointerActive ? (VREXPR) : (EXPR); } } // MARKER".
    # Works with or without MouseButtonsSwapped logic (upstream vs ORNYMG).
    $text = [IO.File]::ReadAllText($path)
    if ($text.Contains($marker)) { Write-Output "skip (marker present): $path :: $marker"; return }
    $lines = $text -split "`r?`n"
    $done = $false
    for ($i = 0; $i -lt $lines.Length; $i++) {
        if ($lines[$i].Contains($lineContains) -and $lines[$i].Contains('{ get { return ') -and $lines[$i].TrimEnd().EndsWith('; } }')) {
            $line = $lines[$i]
            $ri = $line.IndexOf('{ get { return ')
            $prefix = $line.Substring(0, $ri + '{ get { return '.Length)
            $expr = $line.Substring($ri + '{ get { return '.Length)
            $expr = $expr.Substring(0, $expr.Length - '; } }'.Length)
            $lines[$i] = $prefix + 'VRPointerActive ? (' + $vrExpr + ') : (' + $expr + '); } } // ' + $marker
            $done = $true
            break
        }
    }
    if (-not $done) { throw "getter not found in $path : $lineContains" }
    Backup-Once $path
    [IO.File]::WriteAllText($path, ($lines -join "`r`n"))
    Write-Output "patched: $path :: $marker"
}

function Replace-Once([string]$path, [string]$marker, [string]$old, [string]$new) {    $text = [IO.File]::ReadAllText($path)
    if ($text.Contains($marker)) { Write-Output "skip (marker present): $path :: $marker"; return }
    if (-not $text.Contains($old)) { throw "pattern not found in $path : $($old.Substring(0, [Math]::Min(80, $old.Length)))..." }
    Backup-Once $path
    $text = $text.Replace($old, $new)
    [IO.File]::WriteAllText($path, $text)
    Write-Output "patched: $path :: $marker"
}

# --- 0. Layout ---------------------------------------------------------------
$runActivity = Join-Path $Checkout 'Source\RunActivity'
$vrDir = Join-Path $runActivity 'Viewer3D\VR'
if (-not (Test-Path -LiteralPath $vrDir)) { New-Item -ItemType Directory -Path $vrDir -Force | Out-Null }
Copy-Item -LiteralPath (Join-Path $OrvrRepo 'Integration\RunActivity\VR\VRSystem.cs') -Destination (Join-Path $vrDir 'VRSystem.cs') -Force
Copy-Item -LiteralPath (Join-Path $OrvrRepo 'Integration\RunActivity\VR\VRNative.cs') -Destination (Join-Path $vrDir 'VRNative.cs') -Force
Write-Output 'copied VRSystem.cs + VRNative.cs'

# --- 0b. .NET Framework reference assemblies (only with -Net472) -----------------
# SDK-style net472 builds on machines without VS/.NET Framework targeting
# packs (e.g. .NET 8 SDK only) need the reference-assemblies package. This
# touches only the disposable checkout, never the game folder.
if ($Net472) {
    $props = Join-Path $Checkout 'Source\Directory.Build.props'
    if (-not (Test-Path -LiteralPath $props)) { throw "Directory.Build.props not found: $props" }
    $pt = [IO.File]::ReadAllText($props)
    if (-not $pt.Contains('Microsoft.NETFramework.ReferenceAssemblies')) {
        Backup-Once $props
        $anchor = '</Project>'
        $idx = $pt.LastIndexOf($anchor, [StringComparison]::Ordinal)
        $add = "  <ItemGroup>`r`n    <!-- ORVR-Net472: ref assemblies for SDK-only machines -->`r`n    <PackageReference Include=`"Microsoft.NETFramework.ReferenceAssemblies`" Version=`"1.0.3`" PrivateAssets=`"all`" />`r`n    <!-- ORVR-Net472: binary .resx support when building net472 with a modern SDK -->`r`n    <PackageReference Include=`"System.Resources.Extensions`" Version=`"8.0.0`" />`r`n  </ItemGroup>`r`n"
        $pt = $pt.Insert($idx, $add)
        [IO.File]::WriteAllText($props, $pt)
        Write-Output 'patched: Directory.Build.props (net472 ref assemblies)'
    } else { Write-Output 'skip: Directory.Build.props already has ref assemblies' }
}

# --- 1. ProjectReference ------------------------------------------------------
$csproj = Join-Path $runActivity 'RunActivity.csproj'
$projText = [IO.File]::ReadAllText($csproj)
if (-not $projText.Contains('Orts.VR.csproj')) {
    Backup-Once $csproj
    # NOTE: insert before the FINAL </Project> only (LastIndexOf). A blanket
    # Replace would also hit <Project>{guid}</Project> metadata. (ORVR lesson.)
    $rel = Get-RelativePath $runActivity (Join-Path $OrvrRepo 'Source\Orts.VR\Orts.VR.csproj')
    $ref = "  <ItemGroup>`r`n    <ProjectReference Include=`"$rel`" />`r`n  </ItemGroup>`r`n"
    $closeIdx = $projText.LastIndexOf('</Project>', [StringComparison]::Ordinal)
    if ($closeIdx -lt 0) { throw "no </Project> in $csproj" }
    $projText = $projText.Insert($closeIdx, $ref)
    [IO.File]::WriteAllText($csproj, $projText)
    Write-Output "patched: RunActivity.csproj (ProjectReference -> $rel)"
} else { Write-Output 'skip: RunActivity.csproj already references Orts.VR' }

# --- 2. UserSettings -----------------------------------------------------------
$userSettings = Join-Path $Checkout 'Source\Orts.Settings\UserSettings.cs'
$vrSettingsBlock = [IO.File]::ReadAllText((Join-Path $OrvrRepo 'Integration\RunActivity\VR\UserSettings.VR.inc.cs'))
Insert-AfterAnchorOnce $userSettings 'VREnabled { get; set; }' '        public int ViewingFOV { get; set; }' ("`r`n" + $vrSettingsBlock.TrimEnd() + "`r`n")

# --- 3. UserCommand -------------------------------------------------------------
$userCommand = Join-Path $Checkout 'Source\ORTS.Common\Input\UserCommand.cs'
Replace-Once $userCommand 'VRRecenter,' `
    '        [GetString("Control DP Less")] ControlDPLess,' `
    "        [GetString(`"Control DP Less`")] ControlDPLess,`r`n`r`n        // ORVR: appended at end so saved key bindings keep their values.`r`n        [GetString(`"VR Recenter View`")] VRRecenter,"

# --- 4. InputSettings default ----------------------------------------------------
$inputSettings = Join-Path $Checkout 'Source\Orts.Settings\InputSettings.cs'
Insert-AfterAnchorOnce $inputSettings 'UserCommand.VRRecenter' `
    '            Commands[(int)UserCommand.CameraZoomOut] = new UserCommandModifiableKeyInput(0x51, Commands[(int)UserCommand.CameraMoveFast], Commands[(int)UserCommand.CameraMoveSlow]);' `
    "`r`n            // ORVR default (rebindable in Options > Keyboard).`r`n            Commands[(int)UserCommand.VRRecenter] = new UserCommandKeyInput(0x13, KeyModifiers.Alt | KeyModifiers.Shift);"

# --- 5. UserInput overlay ---------------------------------------------------------
$userInput = Join-Path $runActivity 'Viewer3D\UserInput.cs'
$vrRegion = [IO.File]::ReadAllText((Join-Path $OrvrRepo 'Integration\RunActivity\VR\UserInput.VR.inc.cs'))
Insert-AfterAnchorOnce $userInput 'VRPointerActive' '        public static int MouseWheelChange { get { return MouseState.ScrollWheelValue - LastMouseState.ScrollWheelValue; } }' ("`r`n`r`n" + $vrRegion.TrimEnd() + "`r`n")
# NOTE: tree-agnostic wraps (work on upstream master and ORNYMG, with or
# without the MouseButtonsSwapped logic). Trailing // markers sit AFTER the
# closing braces so they cannot swallow code.
Wrap-ReturnOnce $userInput 'ORVR-MouseMoveX' 'public static int MouseMoveX' 'VRPointerX - VRPointerLastX'
Wrap-ReturnOnce $userInput 'ORVR-MouseMoveY' 'public static int MouseMoveY' 'VRPointerY - VRPointerLastY'
Wrap-ReturnOnce $userInput 'ORVR-MouseX' 'public static int MouseX' 'VRPointerX'
Wrap-ReturnOnce $userInput 'ORVR-MouseY' 'public static int MouseY' 'VRPointerY'
Wrap-ReturnOnce $userInput 'ORVR-LeftDown' 'public static bool IsMouseLeftButtonDown' 'VRPointerDown'
Wrap-ReturnOnce $userInput 'ORVR-LeftPressed' 'public static bool IsMouseLeftButtonPressed' 'VRPointerPressed'
Wrap-ReturnOnce $userInput 'ORVR-LeftReleased' 'public static bool IsMouseLeftButtonReleased' 'VRPointerReleased'

# --- 6. RenderFrame ---------------------------------------------------------------
$renderFrame = Join-Path $runActivity 'Viewer3D\RenderFrame.cs'
$vrFrameInc = [IO.File]::ReadAllText((Join-Path $OrvrRepo 'Integration\RunActivity\VR\RenderFrame.VR.inc.cs'))
function Get-MarkedBlock([string]$text, [string]$begin, [string]$end) {
    $i = $text.IndexOf($begin, [StringComparison]::Ordinal)
    $j = $text.IndexOf($end, [StringComparison]::Ordinal)
    if ($i -lt 0 -or $j -lt 0 -or $j -le $i) { throw "block markers missing: $begin / $end" }
    return $text.Substring($i + $begin.Length, $j - ($i + $begin.Length))
}
function Find-MethodSpan([string]$text, [string]$signature) {
    $i = $text.IndexOf($signature, [StringComparison]::Ordinal)
    if ($i -lt 0) { throw "method not found: $signature" }
    $brace = $text.IndexOf('{', $i)
    $depth = 0
    $j = $brace
    while ($j -lt $text.Length) {
        if ($text[$j] -eq '{') { $depth++ }
        elseif ($text[$j] -eq '}') { $depth--; if ($depth -eq 0) { break } }
        $j++
    }
    if ($depth -ne 0) { throw "unbalanced braces for: $signature" }
    return @{ Open = $brace; Close = $j }
}

function Insert-AfterMethodCloseOnce([string]$path, [string]$marker, [string]$signature, [string]$insert) {
    $text = [IO.File]::ReadAllText($path)
    if ($text.Contains($marker)) { Write-Output "skip (marker present): $path :: $marker"; return }
    Backup-Once $path
    $span = Find-MethodSpan $text $signature
    $eol = $text.IndexOf("`n", $span.Close)
    $text = $text.Insert($eol + 1, $insert)
    [IO.File]::WriteAllText($path, $text)
    Write-Output "patched: $path :: $marker"
}

function Insert-AfterMethodOpenOnce([string]$path, [string]$marker, [string]$signature, [string]$insert) {
    $text = [IO.File]::ReadAllText($path)
    if ($text.Contains($marker)) { Write-Output "skip (marker present): $path :: $marker"; return }
    Backup-Once $path
    $span = Find-MethodSpan $text $signature
    $eol = $text.IndexOf("`n", $span.Open)
    $text = $text.Insert($eol + 1, $insert)
    [IO.File]::WriteAllText($path, $text)
    Write-Output "patched: $path :: $marker"
}

function Insert-BeforeAnchorOnce([string]$path, [string]$marker, [string]$anchor, [string]$insert) {
    $text = [IO.File]::ReadAllText($path)
    if ($text.Contains($marker)) { Write-Output "skip (marker present): $path :: $marker"; return }
    $idx = $text.IndexOf($anchor, [StringComparison]::Ordinal)
    if ($idx -lt 0) { throw "anchor not found in $path : $anchor" }
    Backup-Once $path
    $lineStart = $text.LastIndexOf("`n", $idx) + 1
    $text = $text.Insert($lineStart, $insert)
    [IO.File]::WriteAllText($path, $text)
    Write-Output "patched: $path :: $marker"
}

function Replace-Method([string]$text, [string]$signature, [string]$replacement) {    $i = $text.IndexOf($signature, [StringComparison]::Ordinal)
    if ($i -lt 0) { throw "method not found: $signature" }
    $brace = $text.IndexOf('{', $i)
    $depth = 0
    $j = $brace
    while ($j -lt $text.Length) {
        if ($text[$j] -eq '{') { $depth++ }
        elseif ($text[$j] -eq '}') { $depth--; if ($depth -eq 0) { break } }
        $j++
    }
    if ($depth -ne 0) { throw "unbalanced braces replacing: $signature" }
    return $text.Substring(0, $i) + $replacement.TrimEnd() + "`r`n" + $text.Substring($j + 1)
}
# 6a. insert eye methods after SetCamera
$eyeMethods = Get-MarkedBlock $vrFrameInc '// ==== ORVR-EYE-METHODS-BEGIN ====' '// ==== ORVR-EYE-METHODS-END ===='
Insert-AfterMethodCloseOnce $renderFrame 'public void SetStereoView(' 'public void SetCamera(Camera camera)' ("`r`n" + $eyeMethods.TrimEnd() + "`r`n")
# 6b. DrawSimple -> DrawSimple + DrawSimpleTo.
# Upstream master renders through RenderSurface/RenderSurfaceMaterial (needs
# the parameterized replacement from the .inc file). ORNYMG draws straight to
# the current target, so the original body is preserved verbatim and only
# wrapped with an eye-target bind.
$rfText = [IO.File]::ReadAllText($renderFrame)
if (-not $rfText.Contains('void DrawSimpleTo(GraphicsDevice graphicsDevice,')) {
    Backup-Once $renderFrame
    $span = Find-MethodSpan $rfText 'void DrawSimple(GraphicsDevice graphicsDevice, bool logging)'
    $bodyStart = $rfText.IndexOf('{', $span.Open) + 1
    $origBody = $rfText.Substring($bodyStart, $span.Close - $bodyStart).Trim("`r", "`n")
    if ($origBody.Contains('RenderSurfaceMaterial')) {
        $drawSimpleNew = Get-MarkedBlock $vrFrameInc '// ==== ORVR-DRAWSIMPLE-BEGIN ====' '// ==== ORVR-DRAWSIMPLE-END ===='
        $rfText = Replace-Method $rfText 'void DrawSimple(GraphicsDevice graphicsDevice, bool logging)' $drawSimpleNew
    } else {
        $wrapped = "        void DrawSimple(GraphicsDevice graphicsDevice, bool logging)`r`n" +
            "        {`r`n" +
            "            DrawSimpleTo(graphicsDevice, logging, null, true);`r`n" +
            "        }`r`n`r`n" +
            "        /// <summary>`r`n" +
            "        /// ORVR: eye-target wrapper. Mono path binds no target (original`r`n" +
            "        /// behavior); VR binds one eye target and reuses the same body.`r`n" +
            "        /// </summary>`r`n" +
            "        void DrawSimpleTo(GraphicsDevice graphicsDevice, bool logging, RenderTarget2D eyeTarget, bool presentToBackbuffer)`r`n" +
            "        {`r`n" +
            "            if (eyeTarget != null)`r`n" +
            "                graphicsDevice.SetRenderTarget(eyeTarget);`r`n" +
            $origBody + "`r`n" +
            "        }`r`n"
        $rfText = Replace-Method $rfText 'void DrawSimple(GraphicsDevice graphicsDevice, bool logging)' $wrapped
    }
    [IO.File]::WriteAllText($renderFrame, $rfText)
    Write-Output 'patched: RenderFrame.cs :: DrawSimpleTo'
} else { Write-Output 'skip (marker present): RenderFrame.cs :: DrawSimpleTo' }
# --- 7. RenderProcess ------------------------------------------------------------
$renderProcess = Join-Path $runActivity 'Viewer3D\Processes\RenderProcess.cs'
Insert-AfterAnchorOnce $renderProcess 'ORVR-VRSystem-field' 'RenderFrame CurrentFrame;   // a frame contains a list of primitives to draw at a specified time' @'

        // ORVR-VRSystem-field: stereo VR owner (lazy; null until first Viewer frame).
        public Orts.Viewer3D.VR.VRSystem VRSystem;
        readonly object VRSystemLock = new object();
'@
Insert-AfterMethodOpenOnce $renderProcess 'ORVR-Draw-hook' 'internal void Draw()' @'

            // ORVR-Draw-hook: genuine two-pass stereo. Mono path below is untouched.
            EnsureVRSystem();
            if (VRSystem != null && VRSystem.BeginDraw(this))
            {
                try
                {
                    CurrentFrame.SetCamera(Viewer.Camera);
                    CurrentFrame.DrawShadowsOnly(GraphicsDevice);
                    VRSystem.DrawEye(CurrentFrame, Orts.VR.VREye.Left);
                    VRSystem.DrawEye(CurrentFrame, Orts.VR.VREye.Right);
                }
                finally
                {
                    VRSystem.EndDraw(this);
                }
                return;
            }
'@
Insert-BeforeAnchorOnce $renderProcess 'ORVR-EnsureVRSystem' '        static void SwapFrames(ref RenderFrame frame1, ref RenderFrame frame2)' @'
        // ORVR-EnsureVRSystem: creates the VR owner once a Viewer exists.
        void EnsureVRSystem()
        {
            if (VRSystem == null && Viewer != null)
            {
                lock (VRSystemLock)
                {
                    if (VRSystem == null)
                        VRSystem = new Orts.Viewer3D.VR.VRSystem(Viewer);
                }
            }
        }

'@
Replace-Once $renderProcess 'ORVR-Stop-dispose' '            Game.WatchdogProcess.Unregister(WatchdogToken);' '            Game.WatchdogProcess.Unregister(WatchdogToken);

            // ORVR-Stop-dispose
            try { VRSystem?.Dispose(); } catch { }
            VRSystem = null;'

# --- 8. Viewer interaction hook -----------------------------------------------------
$viewer = Join-Path $runActivity 'Viewer3D\Viewer.cs'
Insert-AfterAnchorOnce $viewer 'ORVR-UpdateInteraction' '            HandleUserInput(elapsedTime);' @'

            // ORVR-UpdateInteraction: headset + controller state (updater thread; never throws out).
            try { RenderProcess.VRSystem?.UpdateInteraction(frame, elapsedTime); } catch { }
'@

Write-Output 'ORVR patch complete.'
