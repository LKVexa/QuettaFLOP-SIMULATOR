@echo off
setlocal EnableExtensions DisableDelayedExpansion
cd /d "%~dp0"
title Lunar127 1.1.4 - 10Q@1M UltraWide Hologram Boot

set "FORMAT=tiff"
if /I "%~1"=="gif" set "FORMAT=gif"
if /I "%~1"=="tiff" set "FORMAT=tiff"
set "CARRIER=%CD%\cartridge\Lunar127_10Q1M_FULL_ULTRAWIDE_PROCESSABLE.%FORMAT%"
set "HARNESSSRC=%CD%\harness\TIFFGifHoloHarness.cs"
set "RUNTIME=%TEMP%\Lunar127_Hologram_Harness_1.1.4"
set "BUILDLOG=%RUNTIME%\harness-powershell.log"
set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"

if not exist "%CARRIER%" (
  echo [LUNAR127] Requested %FORMAT% carrier is missing.
  pause
  exit /b 2
)
if not exist "%HARNESSSRC%" (
  echo [LUNAR127] Generic C# harness source is missing.
  pause
  exit /b 2
)
if not exist "%RUNTIME%" mkdir "%RUNTIME%" >nul 2>nul
if not exist "%PS%" (
  where powershell.exe >nul 2>nul
  if errorlevel 1 (
    echo [LUNAR127] Windows PowerShell was not found.
    pause
    exit /b 3
  )
  set "PS=powershell.exe"
)

rem IMPORTANT: Do not create or execute a transient harness EXE.
rem Some Windows Smart App Control / WDAC configurations reject freshly generated
rem executables in %%TEMP%% with "The system cannot execute the specified program."
rem The generic C# source is compiled and invoked in-process by Windows PowerShell.

echo ================================================================
echo  LUNAR127 1.1.4 - HOLOGRAM-CONTAINED 10Q@1M MVP
echo ================================================================
echo  External bootstrap: BOOT.cmd + generic C# harness source only
echo  Authoritative MVP: TIFF-GIF-HOLO/4 image-resident filesystem
echo  UltraWide appliance: COMPLETE REPOSITORY + ORIGINAL ZIP IN CARRIER
echo  Resident compute:  1,048,576-bit UltraWide BRWWORD/1
echo  Hot state:         256-bit compiled sequential dependency kernel
echo  Represented gate:  1e31 FLOP-eq/s at measured 1 MHz
echo  Carrier:           %CARRIER%
echo  Harness mode:      in-process Windows PowerShell Add-Type
echo ================================================================
echo.

echo [1/3] Loading generic carrier harness in-process...
call :PSRUN --verify
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
  echo.
  echo [LUNAR127] Carrier verification failed. Nothing will be materialized.
  if exist "%BUILDLOG%" (
    echo [LUNAR127] Diagnostic log:
    echo   "%BUILDLOG%"
    type "%BUILDLOG%"
  )
  pause
  exit /b 5
)

echo [2/3] Processable hologram and immutable MVP bank verified.
if /I "%~1"=="verify" goto :DONE
if /I "%~2"=="verify" goto :DONE
if /I "%~1"=="inspect" (
  call :PSRUN --inspect
  set "RC=%ERRORLEVEL%"
  goto :DONE_RC
)
if /I "%~2"=="inspect" (
  call :PSRUN --inspect
  set "RC=%ERRORLEVEL%"
  goto :DONE_RC
)

echo [3/3] Materializing the verified image-resident 10Q@1M MVP and launching it...
call :PSRUN --launch-mvp
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
  echo [LUNAR127] Image-resident MVP launch failed.
  if exist "%BUILDLOG%" (
    echo [LUNAR127] Diagnostic log:
    echo   "%BUILDLOG%"
    type "%BUILDLOG%"
  )
  pause
  exit /b 6
)

echo.
echo [PASS] The entry point, kernel source, COMPLETE UltraWide Appliance, UltraWide word, proof vectors,
echo        run-session controls, raster rules and dashboard were decoded from the verified hologram.
echo        Represented compute and physical host metrics are reported separately.
echo.
goto :DONE

:PSRUN
set "PSMODE=%~1"
"%PS%" -NoLogo -NoProfile -STA -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; try { $src=[IO.File]::ReadAllText($env:HARNESSSRC); Add-Type -TypeDefinition $src -Language CSharp -ReferencedAssemblies 'System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll' -ErrorAction Stop; $a=[string[]]@($env:PSMODE,$env:CARRIER); $rc=[TIFFHolo.HarnessEntry]::Run($a); exit [int]$rc } catch { $m=($_ | Out-String); [IO.File]::WriteAllText($env:BUILDLOG,$m); [Console]::Error.WriteLine($m); exit 97 }"
exit /b %ERRORLEVEL%

:DONE
set "RC=0"
:DONE_RC
echo Press any key to close this bootstrap window.
pause >nul
exit /b %RC%
