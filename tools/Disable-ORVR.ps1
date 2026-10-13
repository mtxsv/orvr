#Requires -Version 5.1
<#
.SYNOPSIS
  Disables ORVR stereo VR (sets VREnabled=0). The game is then stock desktop.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File Disable-ORVR.ps1
#>
$ErrorActionPreference = 'Stop'
$key = 'HKCU:\SOFTWARE\OpenRails\ORTS'
if (-not (Test-Path -LiteralPath $key)) { throw "Settings key not found: $key (has the game run once?)" }
New-ItemProperty -Path $key -Name 'VREnabled' -Value 0 -PropertyType DWord -Force | Out-Null
Write-Output 'ORVR disabled (VREnabled=0). Desktop mode, mod files stay installed.'
