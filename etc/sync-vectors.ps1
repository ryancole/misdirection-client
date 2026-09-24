#Requires -Version 7
<#
.SYNOPSIS
  Copy the protocol test vectors from the firmware repo into the test project.
.DESCRIPTION
  PROTOCOL.md and etc/protocol-vectors.json in the misdirection firmware repo are the source of
  truth. This copies the vectors into src/Misdirection.Client.Tests/TestData so the suite
  asserts against the exact bytes the firmware was generated to expect. Run it after
  regenerating the vectors upstream.
.EXAMPLE
  etc\sync-vectors.ps1
  etc\sync-vectors.ps1 -FirmwareRepo D:\other\misdirection
#>
[CmdletBinding()]
param(
    [string]$FirmwareRepo = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'misdirection')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $FirmwareRepo 'etc' 'protocol-vectors.json'
$dest = Join-Path $root 'src' 'Misdirection.Client.Tests' 'TestData' 'protocol-vectors.json'

if (-not (Test-Path $source)) {
    throw "Vector file not found at $source. Pass -FirmwareRepo to point at the firmware checkout."
}

New-Item -ItemType Directory -Force (Split-Path -Parent $dest) | Out-Null
Copy-Item $source $dest -Force
Write-Host "Copied $source -> $dest"
