#Requires -Version 7
<#
.SYNOPSIS
  Run the test suite.
.EXAMPLE
  etc\test.ps1
  etc\test.ps1 -Filter FrameParser     # only tests whose name contains FrameParser
#>
[CmdletBinding()]
param(
    [string]$Filter
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$args = @((Join-Path $root 'src' 'MisdirectionClient.slnx'))
if ($Filter) { $args += '--filter', "FullyQualifiedName~$Filter" }

dotnet test @args
exit $LASTEXITCODE
