# Produce an app ZIP and matching source ZIP without replacing an installed build.
param([Parameter(Mandatory)][string]$Destination,[string]$Version)
$ErrorActionPreference='Stop'
$Destination=[IO.Path]::GetFullPath($Destination)
if(Test-Path -LiteralPath $Destination){throw 'Choose a new release directory; existing artifacts are preserved.'}
New-Item -ItemType Directory -Path $Destination|Out-Null
$package=Join-Path $Destination 'package'
& (Join-Path $PSScriptRoot 'publish.ps1') -Destination $package -Version $Version
& (Join-Path $PSScriptRoot 'split-release.ps1') -Package $package -Destination $Destination
