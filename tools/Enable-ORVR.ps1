#Requires -Version 5.1
<#
.SYNOPSIS
  Enables ORVR stereo VR in an ORNYMG install (sets VREnabled=1).
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File Enable-ORVR.ps1
#>
$ErrorActionPreference = 'Stop'
$key = 'HKCU:\SOFTWARE\OpenRails\ORTS'
if (-not (Test-Path -LiteralPath $key)) { throw "Settings key not found: $key (has the game run once?)" }
New-ItemProperty -Path $key -Name 'VREnabled' -Value 1 -PropertyType DWord -Force | Out-Null
Write-Output 'ORVR enabled (VREnabled=1). Start the game normally; press Alt+Shift+R in the headset to recenter.'
