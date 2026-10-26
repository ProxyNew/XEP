# Produce a complete, inspectable release ZIP without replacing an installed build.
param([Parameter(Mandatory)][string]$Destination,[string]$Version)
$ErrorActionPreference='Stop'
$Destination=[IO.Path]::GetFullPath($Destination)
if(Test-Path -LiteralPath $Destination){throw 'Choose a new release directory; existing artifacts are preserved.'}
New-Item -ItemType Directory -Path $Destination|Out-Null
$package=Join-Path $Destination 'package'
& (Join-Path $PSScriptRoot 'publish.ps1') -Destination $package -Version $Version
if(-not (Test-Path -LiteralPath (Join-Path $package 'source/XEP-source.zip'))){throw 'Corresponding source missing; no release ZIP created'}
$manifest=@(Get-ChildItem -LiteralPath $package -Recurse -File|Where-Object Name -ne '.xep-publish.lock'|ForEach-Object {
 [ordered]@{Path=[IO.Path]::GetRelativePath($package,$_.FullName).Replace('\','/');Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash;Bytes=$_.Length}
})
$manifest|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $Destination 'package-files.json') -Encoding utf8
$zip=Join-Path $Destination 'XEnchantmentPatcher.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($package,$zip)
$archive=[IO.Compression.ZipFile]::OpenRead($zip)
try{
 foreach($file in $manifest){
  $entry=$archive.GetEntry($file.Path)
  if(-not $entry){throw "ZIP missing $($file.Path)"}
  $stream=$entry.Open()
  try{$sha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
  if($sha -cne $file.Sha256){throw "ZIP hash mismatch: $($file.Path)"}
 }
}finally{$archive.Dispose()}
Get-FileHash -LiteralPath $zip -Algorithm SHA256|Format-List
"Verified release: $zip"
