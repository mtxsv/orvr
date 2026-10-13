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
    [Parameter(Mandatory = $true)][string]$OrvrRepo
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
    $text = [IO.File]::ReadAllText($path)
    if ($text.Contains($marker)) { Write-Output "skip (marker present): $path :: $marker"; return }
    $idx = $text.IndexOf($anchor, [StringComparison]::Ordinal)
    if ($idx -lt 0) { throw "anchor not found in $path : $anchor" }
    Backup-Once $path
    $text = $text.Insert($idx + $anchor.Length, $insert)
    [IO.File]::WriteAllText($path, $text)
    Write-Output "patched: $path :: $marker"
}

function Replace-Once([string]$path, [string]$marker, [string]$old, [string]$new) {
    $text = [IO.File]::ReadAllText($path)
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
# NOTE: full-line replacements; trailing // markers sit AFTER the closing
# braces so they cannot swallow code. A marker before a brace would break
# the build (a // comment hides the rest of the line).
Replace-Once $userInput 'ORVR-MouseMoveX' 'public static int MouseMoveX { get { return MouseState.X - LastMouseState.X; } }' 'public static int MouseMoveX { get { return VRPointerActive ? VRPointerX - VRPointerLastX : MouseState.X - LastMouseState.X; } } // ORVR-MouseMoveX'
Replace-Once $userInput 'ORVR-MouseMoveY' 'public static int MouseMoveY { get { return MouseState.Y - LastMouseState.Y; } }' 'public static int MouseMoveY { get { return VRPointerActive ? VRPointerY - VRPointerLastY : MouseState.Y - LastMouseState.Y; } } // ORVR-MouseMoveY'
Replace-Once $userInput 'ORVR-MouseX' 'public static int MouseX { get { return MouseState.X; } }' 'public static int MouseX { get { return VRPointerActive ? VRPointerX : MouseState.X; } } // ORVR-MouseX'
Replace-Once $userInput 'ORVR-MouseY' 'public static int MouseY { get { return MouseState.Y; } }' 'public static int MouseY { get { return VRPointerActive ? VRPointerY : MouseState.Y; } } // ORVR-MouseY'
Replace-Once $userInput 'ORVR-LeftDown' 'public static bool IsMouseLeftButtonDown { get { return MouseButtonsSwapped ? MouseState.RightButton == ButtonState.Pressed : MouseState.LeftButton == ButtonState.Pressed; } }' 'public static bool IsMouseLeftButtonDown { get { return VRPointerActive ? VRPointerDown : (MouseButtonsSwapped ? MouseState.RightButton == ButtonState.Pressed : MouseState.LeftButton == ButtonState.Pressed); } } // ORVR-LeftDown'
Replace-Once $userInput 'ORVR-LeftPressed' 'public static bool IsMouseLeftButtonPressed { get { return MouseButtonsSwapped ? MouseState.RightButton == ButtonState.Pressed && LastMouseState.RightButton == ButtonState.Released : MouseState.LeftButton == ButtonState.Pressed && LastMouseState.LeftButton == ButtonState.Released; } }' 'public static bool IsMouseLeftButtonPressed { get { return VRPointerActive ? VRPointerPressed : (MouseButtonsSwapped ? MouseState.RightButton == ButtonState.Pressed && LastMouseState.RightButton == ButtonState.Released : MouseState.LeftButton == ButtonState.Pressed && LastMouseState.LeftButton == ButtonState.Released); } } // ORVR-LeftPressed'
Replace-Once $userInput 'ORVR-LeftReleased' 'public static bool IsMouseLeftButtonReleased { get { return MouseButtonsSwapped ? MouseState.RightButton == ButtonState.Released && LastMouseState.RightButton == ButtonState.Pressed : MouseState.LeftButton == ButtonState.Released && LastMouseState.LeftButton == ButtonState.Pressed; } }' 'public static bool IsMouseLeftButtonReleased { get { return VRPointerActive ? VRPointerReleased : (MouseButtonsSwapped ? MouseState.RightButton == ButtonState.Released && LastMouseState.RightButton == ButtonState.Pressed : MouseState.LeftButton == ButtonState.Released && LastMouseState.LeftButton == ButtonState.Pressed); } } // ORVR-LeftReleased'

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
# 6b. replace DrawSimple with DrawSimple + DrawSimpleTo
$rfText = [IO.File]::ReadAllText($renderFrame)
if (-not $rfText.Contains('void DrawSimpleTo(GraphicsDevice graphicsDevice, bool logging, RenderTarget2D surface, bool presentToBackbuffer)')) {
    Backup-Once $renderFrame
    $drawSimpleNew = Get-MarkedBlock $vrFrameInc '// ==== ORVR-DRAWSIMPLE-BEGIN ====' '// ==== ORVR-DRAWSIMPLE-END ===='
    $rfText = Replace-Method $rfText '        void DrawSimple(GraphicsDevice graphicsDevice, bool logging)' $drawSimpleNew
    [IO.File]::WriteAllText($renderFrame, $rfText)
    Write-Output 'patched: RenderFrame.cs :: ORVR-DrawSimpleTo'
} else { Write-Output 'skip (marker present): RenderFrame.cs :: ORVR-DrawSimpleTo' }
# --- 7. RenderProcess ------------------------------------------------------------
$renderProcess = Join-Path $runActivity 'Viewer3D\Processes\RenderProcess.cs'
Insert-AfterAnchorOnce $renderProcess 'ORVR-VRSystem-field' '        RenderFrame CurrentFrame;   // a frame contains a list of primitives to draw at a specified time' @'

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
