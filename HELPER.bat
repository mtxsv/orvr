@ECHO OFF
SETLOCAL ENABLEEXTENSIONS ENABLEDELAYEDEXPANSION
TITLE ORVR Helper - Open Rails VR installer
REM ============================================================================
REM  ORVR HELPER.bat - one-file installer for Open Rails VR (ORNYMG line)
REM  Run this file from a checkout of https://github.com/mtxsv/orvr
REM
REM  Menu:
REM    1. Full install    - prerequisites, Open Rails source, VR patch, build,
REM                         deploy finished game to Desktop\OpenRailsVR
REM    2. Custom install  - same pipeline with choices (config, VR on/off,
REM                         desktop shortcut, optional OpenXR shim build)
REM    3. Repair          - re-patch, rebuild and redeploy (keeps your settings)
REM    4. Credits
REM    5. Settings        - flip VR on/off and tune it (registry, no rebuild)
REM    6. Quit
REM
REM  Set ORVR_NOPAUSE=1 to skip end-of-step pauses (unattended / CI).
REM ============================================================================

REM ---------- fixed locations (edit here if you must) -------------------------
SET "ORVR_ROOT=%~dp0"
IF "%ORVR_ROOT:~-1%"=="\" SET "ORVR_ROOT=%ORVR_ROOT:~0,-1%"
SET "ORVR_BRANCH=OR_NewYear_MG_NoWD"
SET "ORVR_UPSTREAM=https://github.com/Csantucci/openrails.git"
SET "SRC_DIR=%LOCALAPPDATA%\ORVR\src\or-nymg"
SET "TARGET_DIR=%USERPROFILE%\Desktop\OpenRailsVR"
SET "REG_KEY=HKCU\SOFTWARE\OpenRails\ORTS"
SET "LOG=%LOCALAPPDATA%\ORVR\helper.log"
IF NOT EXIST "%LOCALAPPDATA%\ORVR" MKDIR "%LOCALAPPDATA%\ORVR" >NUL 2>&1

REM defaults used by Full install (Custom install asks instead)
SET "CFG=Release"
SET "WANT_VR=0"
SET "WANT_SHORTCUT=0"
SET "WANT_SHIM=0"

:MENU
CLS
ECHO  ======================================================================
ECHO   ORVR HELPER - Open Rails VR installer  (branch %ORVR_BRANCH%)
ECHO  ======================================================================
ECHO   Target folder : %TARGET_DIR%
ECHO   Sources in    : %SRC_DIR%
ECHO.
ECHO   1. Full install      (everything, hands-free)
ECHO   2. Custom install    (choose options step by step)
ECHO   3. Repair            (re-patch + rebuild + redeploy, keeps settings)
ECHO   4. Credits
ECHO   5. Settings          (enable/disable VR, tune it - no rebuild needed)
ECHO   6. Quit
ECHO.
SET "CHOICE="
SET /P "CHOICE=Pick [1-6]: "
IF "%CHOICE%"=="" GOTO QUIT
IF "%CHOICE%"=="1" GOTO FULL_INSTALL
IF "%CHOICE%"=="2" GOTO CUSTOM_INSTALL
IF "%CHOICE%"=="3" GOTO REPAIR
IF "%CHOICE%"=="4" GOTO CREDITS
IF "%CHOICE%"=="5" GOTO SETTINGS_MENU
IF "%CHOICE%"=="6" GOTO QUIT
ECHO Not a choice, try again.
CALL :PAUSE2
GOTO MENU

REM ============================================================================
:FULL_INSTALL
CLS
ECHO  --- FULL INSTALL ---
CALL :STEP_PREREQS   || GOTO FAILED
CALL :STEP_SOURCE    || GOTO FAILED
CALL :STEP_PATCH     || GOTO FAILED
CALL :STEP_BUILD     || GOTO FAILED
CALL :STEP_DEPLOY    || GOTO FAILED
CALL :STEP_FINISH_NOTE
GOTO MENU

:CUSTOM_INSTALL
CLS
ECHO  --- CUSTOM INSTALL ---
SET "CFG=Release"
SET "WANT_VR=0"
SET "WANT_SHORTCUT=0"
SET "WANT_SHIM=0"
ECHO.
SET /P "IN_CFG=Build config [Release/Debug, default Release]: "
IF /I "%IN_CFG%"=="Debug" SET "CFG=Debug"
IF /I "%IN_CFG%"=="Release" SET "CFG=Release"
SET /P "IN_VR=Enable VR right away? [y/N]: "
IF /I "%IN_VR%"=="y" SET "WANT_VR=1"
IF /I "%IN_VR%"=="yes" SET "WANT_VR=1"
SET /P "IN_SC=Desktop shortcut to the game menu? [y/N]: "
IF /I "%IN_SC%"=="y" SET "WANT_SHORTCUT=1"
IF /I "%IN_SC%"=="yes" SET "WANT_SHORTCUT=1"
SET /P "IN_SHIM=Try building the OpenXR shim too (needs OpenXR SDK + MSVC)? [y/N]: "
IF /I "%IN_SHIM%"=="y" SET "WANT_SHIM=1"
IF /I "%IN_SHIM%"=="yes" SET "WANT_SHIM=1"
ECHO Config=%CFG% VR=%WANT_VR% Shortcut=%WANT_SHORTCUT% Shim=%WANT_SHIM%
CALL :STEP_PREREQS   || GOTO FAILED
CALL :STEP_SOURCE    || GOTO FAILED
CALL :STEP_PATCH     || GOTO FAILED
CALL :STEP_BUILD     || GOTO FAILED
CALL :STEP_DEPLOY    || GOTO FAILED
IF "%WANT_SHIM%"=="1" CALL :STEP_SHIM
IF "%WANT_SHORTCUT%"=="1" CALL :STEP_SHORTCUT
IF "%WANT_VR%"=="1" CALL :VR_ON
CALL :STEP_FINISH_NOTE
GOTO MENU

:REPAIR
CLS
ECHO  --- REPAIR (re-patch + rebuild + redeploy, settings are kept) ---
IF NOT EXIST "%SRC_DIR%\Source\ORTS.sln" (
  ECHO No sources found, repair needs a fresh clone first.
  CALL :STEP_PREREQS || GOTO FAILED
  CALL :STEP_SOURCE  || GOTO FAILED
)
CALL :STEP_PATCH     || GOTO FAILED
CALL :STEP_BUILD     || GOTO FAILED
CALL :STEP_DEPLOY    || GOTO FAILED
ECHO Repair done. Your VR on/off choice and tuning were kept.
CALL :PAUSE2
GOTO MENU

:CREDITS
CLS
ECHO  ======================================================================
ECHO   ORVR - Open Rails VR mod              https://github.com/mtxsv/orvr
ECHO  ======================================================================
ECHO   - The Open Rails team            (simulator, openrails.org)
ECHO   - Carlo Santucci and testers     (OR NewYear MG line, Elvas Tower)
ECHO   - MonoGame team                  (DirectX game framework)
ECHO   - Khronos Group                  (OpenXR standard)
ECHO   - SharpDX / Vortice contributors (DirectX bindings)
ECHO   - ReShade + DLSS community presets ship with some game folders
ECHO     and are untouched by this installer.
ECHO   ORVR itself: GPL-3.0-or-later, same as Open Rails.
ECHO.
CALL :PAUSE2
GOTO MENU

:QUIT
ECHO Bye. Your finished game (if installed) is at: %TARGET_DIR%
EXIT /B 0

:FAILED
ECHO.
ECHO  *** A step failed - see messages above. Log: %LOG%
ECHO  *** Fix the issue and choose Repair (3) to continue where it stopped.
CALL :PAUSE2
GOTO MENU

REM ============================================================================
REM  SETTINGS MENU - registry only, game must have run once is NOT required
REM  (values are simply read on next launch). Game folder not touched.
REM ============================================================================
:SETTINGS_MENU
CLS
ECHO  --- ORVR SETTINGS (registry %REG_KEY%) ---
CALL :SHOW_SETTINGS
ECHO.
ECHO   1. Turn VR ON
ECHO   2. Turn VR OFF  (stock desktop game)
ECHO   3. Render scale percent  (50-200, supersampling)
ECHO   4. IPD millimeters       (50-80, eye separation)
ECHO   5. Head radius millimeters (50-1000, lean limit)
ECHO   6. Dominant hand right/left
ECHO   7. Haptics on/off
ECHO   0. Back to main menu
ECHO.
SET "S="
SET /P "S=Pick [0-7]: "
IF "%S%"=="" GOTO MENU
IF "%S%"=="1" CALL :VR_ON
IF "%S%"=="2" CALL :VR_OFF
IF "%S%"=="3" CALL :SET_NUM VRRenderScalePercent 50 200
IF "%S%"=="4" CALL :SET_NUM VRIPDMillimeters 50 80
IF "%S%"=="5" CALL :SET_NUM VRHeadRadiusMM 50 1000
IF "%S%"=="6" CALL :SET_HAND
IF "%S%"=="7" CALL :SET_BOOL VRHaptics
IF "%S%"=="0" GOTO MENU
IF NOT "%S%"=="" CALL :PAUSE2
GOTO SETTINGS_MENU

:SHOW_SETTINGS
FOR %%K IN (VREnabled VRRenderScalePercent VRIPDMillimeters VRHeadRadiusMM VRHeadMaxUpMM VRHeadMaxDownMM VRHeadTracking VRPositionalTracking VRDominantHandRight VRHaptics) DO (
  SET "V=?"
  FOR /F "tokens=3" %%V IN ('REG QUERY "%REG_KEY%" /v %%K 2^>NUL ^| FINDSTR /R "REG_"') DO SET "V=%%V"
  IF "%%K"=="VREnabled" (
    IF "!V!"=="0x1" ECHO   VR is ON
    IF NOT "!V!"=="0x1" ECHO   VR is OFF ^(desktop^)
  ) ELSE (
    ECHO   %%K = !V!
  )
)
ECHO   Recenter key: Alt+Shift+R ^(rebind in game: Options ^> Keyboard ^> "VR Recenter View"^)
EXIT /B 0

:VR_ON
REG ADD "%REG_KEY%" /v VREnabled /t REG_DWORD /d 1 /f >NUL
ECHO VR enabled. Start the game normally; press Alt+Shift+R in the headset to recenter.
EXIT /B 0

:VR_OFF
REG ADD "%REG_KEY%" /v VREnabled /t REG_DWORD /d 0 /f >NUL
ECHO VR disabled. Stock desktop game.
EXIT /B 0

:SET_NUM
REM %1=name %2=min %3=max
SET "N="
SET /P "N=%1 [%2-%3]: "
ECHO %N%| FINDSTR /R "^[0-9][0-9]*$" >NUL || (ECHO Numbers only. & EXIT /B 0)
IF %N% LSS %2 (ECHO Too small, min %2. & EXIT /B 0)
IF %N% GTR %3 (ECHO Too big, max %3. & EXIT /B 0)
REG ADD "%REG_KEY%" /v %1 /t REG_DWORD /d %N% /f >NUL
ECHO %1 = %N%
EXIT /B 0

:SET_HAND
SET "H="
SET /P "H=Dominant hand [R/L]: "
IF /I "%H%"=="R" REG ADD "%REG_KEY%" /v VRDominantHandRight /t REG_DWORD /d 1 /f >NUL
IF /I "%H%"=="L" REG ADD "%REG_KEY%" /v VRDominantHandRight /t REG_DWORD /d 0 /f >NUL
ECHO Done.
EXIT /B 0

:SET_BOOL
REM %1=name, asks 1/0
SET "B="
SET /P "B=%1 on? [y/N]: "
SET "D=0"
IF /I "%B%"=="y" SET "D=1"
IF /I "%B%"=="yes" SET "D=1"
REG ADD "%REG_KEY%" /v %1 /t REG_DWORD /d %D% /f >NUL
ECHO %1 = %D%
EXIT /B 0

REM ============================================================================
REM  PIPELINE STEPS (each returns 0 ok / nonzero fail)
REM ============================================================================
:STEP_PREREQS
ECHO [1/5] Checking prerequisites (git, .NET SDK, .NET Framework 4.8)...
WHERE git >NUL 2>&1
IF NOT ERRORLEVEL 1 GOTO PREREQ_GIT_OK
ECHO   git missing - installing with winget...
CALL :WINGET_INSTALL Git.Git
IF ERRORLEVEL 1 EXIT /B 1
CALL :REFRESH_PATH
WHERE git >NUL 2>&1
IF ERRORLEVEL 1 (
  ECHO   git still missing - install it by hand from https://git-scm.com/
  EXIT /B 1
)
:PREREQ_GIT_OK
ECHO   git ok.
dotnet --list-sdks 2>NUL | FINDSTR /B /C:"8." >NUL
IF NOT ERRORLEVEL 1 GOTO PREREQ_DOTNET_OK
ECHO   .NET 8 SDK missing - installing with winget (needs admin approval)...
CALL :WINGET_INSTALL Microsoft.DotNet.SDK.8
IF ERRORLEVEL 1 EXIT /B 1
CALL :REFRESH_PATH
dotnet --list-sdks 2>NUL | FINDSTR /B /C:"8." >NUL
IF ERRORLEVEL 1 (
  ECHO   dotnet 8 SDK still missing - install by hand from https://dotnet.microsoft.com/
  EXIT /B 1
)
:PREREQ_DOTNET_OK
ECHO   dotnet SDK ok.
REM Release is a hex DWORD (4.8 = 0x80EB0/528040, 4.8.1 = 0x82348/533576).
SET "RELNUM=0"
FOR /F "tokens=3" %%V IN ('REG QUERY "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release 2^>NUL') DO SET /A RELNUM=%%V >NUL 2>&1
IF %RELNUM% GEQ 528040 GOTO PREREQ_NETFX_OK
ECHO   WARNING: .NET Framework 4.8 runtime not detected. The game needs it
ECHO   to RUN - build works without it. It is built into Windows 10 and 11;
ECHO   on older systems install it from:
ECHO   https://dotnet.microsoft.com/download/dotnet-framework/net48
GOTO PREREQ_NETFX_DONE
:PREREQ_NETFX_OK
ECHO   .NET Framework 4.8 runtime ok.
:PREREQ_NETFX_DONE
EXIT /B 0

:WINGET_INSTALL
REM %1 = winget package id
WHERE winget >NUL 2>&1
IF ERRORLEVEL 1 (ECHO   winget not found, install %1 by hand. & EXIT /B 1)
ECHO   winget installing %1 ...
winget install --id %1 -e --silent --accept-package-agreements --accept-source-agreements >>"%LOG%" 2>&1
IF ERRORLEVEL 1 (ECHO   winget failed for %1, see %LOG%. & EXIT /B 1)
EXIT /B 0

:REFRESH_PATH
REM re-read machine+user PATH so just-installed tools are found now
FOR /F "tokens=2*" %%A IN ('REG QUERY "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment" /v Path 2^>NUL') DO SET "MPATH=%%B"
FOR /F "tokens=2*" %%A IN ('REG QUERY "HKCU\Environment" /v Path 2^>NUL') DO SET "UPATH=%%B"
IF DEFINED MPATH IF DEFINED UPATH SET "PATH=%MPATH%;%UPATH%"
EXIT /B 0

:STEP_SOURCE
ECHO [2/5] Getting Open Rails sources (branch %ORVR_BRANCH%)...
IF NOT EXIST "%SRC_DIR%\Source\ORTS.sln" (
  IF NOT EXIST "%SRC_DIR%" MKDIR "%SRC_DIR%" >NUL 2>&1
  ECHO   cloning ^(shallow, one branch - a minute or two^)...
  git clone --depth 1 --branch "%ORVR_BRANCH%" "%ORVR_UPSTREAM%" "%SRC_DIR%" >>"%LOG%" 2>&1
  IF ERRORLEVEL 1 (ECHO   git clone failed, see %LOG%. & EXIT /B 1)
) ELSE (
  ECHO   sources exist, updating...
  git -C "%SRC_DIR%" pull --ff-only >>"%LOG%" 2>&1
  IF ERRORLEVEL 1 ECHO   (pull skipped - keeping existing sources, patch is idempotent)
)
EXIT /B 0

:STEP_PATCH
ECHO [3/5] Applying the ORVR patch (idempotent, backups as *.orvr-bak)...
powershell -NoProfile -ExecutionPolicy Bypass -File "%ORVR_ROOT%\Integration\Apply-VR-Patch.ps1" -Checkout "%SRC_DIR%" -OrvrRepo "%ORVR_ROOT%" -Net472
IF ERRORLEVEL 1 (ECHO   patch script failed. & EXIT /B 1)
EXIT /B 0

:STEP_BUILD
ECHO [4/5] Building RunActivity (%CFG% - a few minutes, shaders recompile)...
dotnet build "%SRC_DIR%\Source\RunActivity\RunActivity.csproj" -c %CFG% --nologo -v minimal >>"%LOG%" 2>&1
IF ERRORLEVEL 1 GOTO BUILD_FAILED
ECHO   building game menu too...
dotnet build "%SRC_DIR%\Source\Menu\Menu.csproj" -c %CFG% --nologo -v minimal >>"%LOG%" 2>&1
IF ERRORLEVEL 1 GOTO BUILD_FAILED
IF NOT EXIST "%SRC_DIR%\Program\RunActivity.exe" (ECHO   build produced no exe?! & EXIT /B 1)
IF NOT EXIST "%SRC_DIR%\Program\Menu.exe" (ECHO   menu build produced no exe?! & EXIT /B 1)
IF NOT EXIST "%SRC_DIR%\Program\Orts.VR.dll" (ECHO   Orts.VR.dll missing from build output?! & EXIT /B 1)
ECHO   build ok.
EXIT /B 0
:BUILD_FAILED
ECHO   build failed - last lines of the log:
powershell -NoProfile -Command "Get-Content '%LOG%' | Select-Object -Last 15"
EXIT /B 1

:STEP_DEPLOY
ECHO [5/5] Deploying finished game to %TARGET_DIR% ...
REM stop a running game so locked files can be replaced (best effort)
powershell -NoProfile -Command "Get-Process RunActivity,Menu -ErrorAction SilentlyContinue | Stop-Process -Force" >NUL 2>&1
IF NOT EXIST "%TARGET_DIR%" MKDIR "%TARGET_DIR%" >NUL 2>&1
robocopy "%SRC_DIR%\Program" "%TARGET_DIR%" /E /XF *.pdb *.xml *.orvr-bak /NFL /NDL /NJH /NJS /R:2 /W:2 >>"%LOG%" 2>&1
IF ERRORLEVEL 8 (ECHO   robocopy failed, see %LOG%. & EXIT /B 1)
IF NOT EXIST "%TARGET_DIR%\RunActivity.exe" (ECHO   deploy incomplete?! & EXIT /B 1)
IF NOT EXIST "%TARGET_DIR%\Orts.VR.dll" (ECHO   deploy incomplete?! & EXIT /B 1)
CALL :WRITE_TARGET_README
ECHO   deployed to %TARGET_DIR%
EXIT /B 0

:WRITE_TARGET_README
(
ECHO OpenRailsVR - Open Rails NewYear MG + ORVR virtual-reality mod
ECHO ==============================================================
ECHO Built %DATE% %TIME% from %ORVR_UPSTREAM% ^(%ORVR_BRANCH%^) + ORVR patch.
ECHO.
ECHO START HERE:
ECHO   1. Run Menu.exe ^(pick route/train^) or RunActivity.exe directly.
ECHO   2. VR is OFF by default. Turn it on: re-run HELPER.bat from the
ECHO      orvr repo and pick 5. Settings ^> 1, or set VREnabled=1 under
ECHO      HKCU\SOFTWARE\OpenRails\ORTS.
ECHO   3. Without a headset you get a side-by-side stereo mirror. With a
ECHO      headset: install an OpenXR runtime ^(e.g. SteamVR^), build
ECHO      Integration\shim\orvr_openxr_shim.dll and drop it next to
ECHO      RunActivity.exe, then press Alt+Shift+R in the headset to recenter.
ECHO.
ECHO Full docs: https://github.com/mtxsv/orvr
) > "%TARGET_DIR%\ORVR-README.txt"
EXIT /B 0

:STEP_SHORTCUT
powershell -NoProfile -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut('%USERPROFILE%\Desktop\OpenRailsVR-Menu.lnk'); $s.TargetPath='%TARGET_DIR%\Menu.exe'; $s.WorkingDirectory='%TARGET_DIR%'; $s.Save()" >NUL 2>&1
ECHO Desktop shortcut created.
EXIT /B 0

:STEP_SHIM
ECHO Trying the optional OpenXR shim build...
WHERE cmake >NUL 2>&1
IF ERRORLEVEL 1 (ECHO   cmake not found - skipping shim. Install cmake + OpenXR SDK + MSVC to build Integration\shim yourself. & EXIT /B 0)
IF NOT DEFINED OpenXR_ROOT IF NOT EXIST "C:\OpenXR-SDK" (ECHO   OpenXR SDK not found ^(set OpenXR_ROOT^) - skipping shim. & EXIT /B 0)
IF NOT DEFINED OpenXR_ROOT SET "OpenXR_ROOT=C:\OpenXR-SDK"
IF NOT EXIST "%LOCALAPPDATA%\ORVR\shimbuild" MKDIR "%LOCALAPPDATA%\ORVR\shimbuild" >NUL 2>&1
cmake -S "%ORVR_ROOT%\Integration\shim" -B "%LOCALAPPDATA%\ORVR\shimbuild" >>"%LOG%" 2>&1
IF ERRORLEVEL 1 (ECHO   cmake configure failed, skipping shim. & EXIT /B 0)
cmake --build "%LOCALAPPDATA%\ORVR\shimbuild" --config Release >>"%LOG%" 2>&1
IF ERRORLEVEL 1 (ECHO   shim build failed, skipping. Game works without it ^(desktop mirror^). & EXIT /B 0)
COPY /Y "%LOCALAPPDATA%\ORVR\shimbuild\bin\orvr_openxr_shim.dll" "%TARGET_DIR%\" >NUL
ECHO Shim built and installed.
EXIT /B 0

:STEP_FINISH_NOTE
IF "%WANT_VR%"=="1" CALL :VR_ON >NUL
ECHO.
ECHO  ======================================================================
ECHO   DONE. Finished game: %TARGET_DIR%
ECHO   VR is currently:
CALL :SHOW_SETTINGS > "%TEMP%\orvr-show.txt"
FINDSTR /B "  VR is" "%TEMP%\orvr-show.txt"
DEL "%TEMP%\orvr-show.txt" >NUL 2>&1
ECHO   Start with Menu.exe. Docs: https://github.com/mtxsv/orvr
ECHO  ======================================================================
CALL :PAUSE2
EXIT /B 0

:PAUSE2
IF DEFINED ORVR_NOPAUSE EXIT /B 0
PAUSE
EXIT /B 0
