# Split a verified private publish; preserve the original tree for inspection.
param([Parameter(Mandatory)][string]$Package,[Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$app=Join-Path $Destination 'app'
$source=Join-Path $Destination 'matching-source'
$packageDocs=Join-Path $Package 'XEnchantmentPatcher-docs'
$tool=Join-Path $app 'XEnchantmentPatcher-app'
$appLicenses=Join-Path $tool 'licenses'
foreach($name in @('app','matching-source','XEnchantmentPatcher.zip','XEnchantmentPatcher-source.zip','app-files.json','source-files.json')){
 if(Test-Path -LiteralPath (Join-Path $Destination $name)){throw "Existing release artifact: $name"}
}
# The exact snapshot plus the original legal tree is independently rebuildable.
[IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $Package 'source/XEP-source.zip'),$source)
Copy-Item -LiteralPath (Join-Path $packageDocs 'licenses') -Destination $source -Recurse
Copy-Item -LiteralPath (Join-Path $Package 'build-provenance.json') -Destination $source
Copy-Item -LiteralPath (Join-Path $Package 'source/source-files.json') -Destination (Join-Path $source 'source-files.json')
@'
# Building this release

This archive contains the exact XEP source snapshot, original dependency notices,
matching upstream sources in licenses/sources, and build provenance. Install the
.NET SDK named in global.json. From this directory run:

    dotnet restore src/XEnchantmentPatcher.csproj --locked-mode
    pwsh -File tools/package-release.ps1 -Destination <new-directory>

NuGet restore requires network access. Dependency build instructions are in their
upstream archives. Champollion source and build notes are under src/ThirdParty/Champollion.
Compiler-generated identity may differ outside Git; replacement binaries need not
be byte-identical.
'@|Set-Content -LiteralPath (Join-Path $source 'BUILDING.md') -Encoding utf8
# The executable at the root and one folder of run-time files and licences; no documents, symbols, provenance or manifest.
New-Item -ItemType Directory -Path (Join-Path $tool 'Champollion'),$appLicenses|Out-Null
Get-ChildItem -LiteralPath $Package -File|Where-Object {
 $_.Name -notin @('.xep-publish.lock','build-provenance.json','session-capabilities.json','createdump.exe') -and $_.Extension -ne '.pdb'
}|ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $tool}
Copy-Item -LiteralPath (Join-Path $Package 'Champollion/Champollion.exe') -Destination (Join-Path $tool 'Champollion')
# The executable goes to the root of the download, where 1.0.0 had it, and starts the program in the
# folder: the .NET host takes the library name it holds as a path relative to itself.
$rootExe=Join-Path $app 'XEnchantmentPatcher.exe'
Move-Item -LiteralPath (Join-Path $tool 'XEnchantmentPatcher.exe') -Destination $rootExe
$bytes=[IO.File]::ReadAllBytes($rootExe);$text=[Text.Encoding]::Latin1.GetString($bytes)
$name="XEnchantmentPatcher.dll`0";$at=$text.IndexOf($name,[StringComparison]::Ordinal)
$path=[Text.Encoding]::UTF8.GetBytes('XEnchantmentPatcher-app\XEnchantmentPatcher.dll')
if($at -lt 0 -or $text.IndexOf($name,$at+1,[StringComparison]::Ordinal) -ge 0 -or ($bytes[($at+$name.Length)..($at+$path.Length)]|Where-Object {$_ -ne 0})){
 throw 'The executable does not hold its library name once with room for the folder.'
}
[Array]::Copy($path,0,$bytes,$at,$path.Length);[IO.File]::WriteAllBytes($rootExe,$bytes)
# The download as laid out must start: the same owned probe as the publish step, from the root executable.
$check=Join-Path $Destination 'start-check';New-Item -ItemType Directory -Path $check|Out-Null
[IO.File]::WriteAllText((Join-Path $check 'settings.json'),'{}')
$started=Start-Process -FilePath $rootExe -PassThru -WindowStyle Hidden -ArgumentList '--help','--no-save','--settings-snapshot',('"'+(Join-Path $check 'settings.json')+'"'),'--session-dir',('"'+(Join-Path $check 'session')+'"')
if(-not $started.WaitForExit(30000)){$started.Kill();throw 'The packaged executable did not finish its start check.'}
if($started.ExitCode -ne 0){throw "The packaged executable failed its start check ($($started.ExitCode)). Inspect $check"}
Copy-Item -LiteralPath (Join-Path $packageDocs 'THIRD-PARTY-NOTICES.md') -Destination $appLicenses
# Deduplicate only byte-identical notices. Distinct copyright notices stay intact.
$notices=@(Get-Item -LiteralPath (Join-Path $packageDocs 'LICENSE'))
$notices+=Get-ChildItem -LiteralPath (Join-Path $packageDocs 'licenses') -Recurse -File|Where-Object {
 [IO.Path]::GetRelativePath((Join-Path $packageDocs 'licenses'),$_.FullName) -notmatch '^sources[\\/]' -and
 $_.Name -match '(?i)(LICENSE|LICENCE|NOTICE|COPYING|COPYRIGHT)'
}
$notices+=Get-ChildItem -LiteralPath (Join-Path $Package 'Champollion') -File|Where-Object Name -Like 'LICENSE*'
$byHash=@{};$index=@('# License and copyright notices','','Each original component notice below maps to an unchanged notice file. Identical','files share one copy; distinct copyright notices are retained.','')
foreach($file in $notices){
 $hash=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
 $original=[IO.Path]::GetRelativePath($packageDocs,$file.FullName).Replace('\','/')
 if(-not $byHash.ContainsKey($hash)){
  $target=if($original -eq 'LICENSE'){'LICENSE'}else{$hash+'.txt'}
  Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $appLicenses $target)
  $byHash[$hash]=$target
 }
 $index+='- '+$original+': [notice]('+$byHash[$hash]+')'
}
$index|Set-Content -LiteralPath (Join-Path $appLicenses 'README.md') -Encoding utf8
function Write-VerifiedArchive([string]$Tree,[string]$Name,[string]$Inventory){
 $manifest=@(Get-ChildItem -LiteralPath $Tree -Recurse -File|ForEach-Object {
  [ordered]@{Path=[IO.Path]::GetRelativePath($Tree,$_.FullName).Replace('\','/');Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash;Bytes=$_.Length}
 })
 if($Tree -eq $app -and $manifest.Path -match '\.(zip|7z|rar)$'){throw "Archive inside the application ZIP: $Name"}
 # A release names no one: refuse a file that holds the building user or machine name, as plain or UTF-16 text.
 $names=@($env:USERNAME,$env:COMPUTERNAME)|Where-Object {$_ -and $_.Length -ge 4}|ForEach-Object {$_;$_.ToCharArray() -join "`0"}
 foreach($file in Get-ChildItem -LiteralPath $Tree -Recurse -File){
  $text=[Text.Encoding]::Latin1.GetString([IO.File]::ReadAllBytes($file.FullName))
  foreach($n in $names){if($text.IndexOf($n,[StringComparison]::OrdinalIgnoreCase) -ge 0){throw "Build user or machine name inside ${Name}: $($file.Name)"}}
 }
 $manifest|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $Destination $Inventory) -Encoding utf8
 $zip=Join-Path $Destination $Name
 [IO.Compression.ZipFile]::CreateFromDirectory($Tree,$zip,[IO.Compression.CompressionLevel]::SmallestSize,$false)
 $archive=[IO.Compression.ZipFile]::OpenRead($zip)
 try{
  if(@($archive.Entries|Where-Object Name -NE '').Count -ne $manifest.Count){throw "Unexpected ZIP file count: $Name"}
  foreach($file in $manifest){
   $entry=$archive.GetEntry($file.Path)
   if(-not $entry){throw "ZIP missing $($file.Path)"}
   $stream=$entry.Open()
   try{$sha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
   if($sha -cne $file.Sha256){throw "ZIP hash mismatch: $($file.Path)"}
  }
 }finally{$archive.Dispose()}
 (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
}
$sourceHash=Write-VerifiedArchive $source 'XEnchantmentPatcher-source.zip' 'source-files.json'
$identity=Get-Content -LiteralPath (Join-Path $source 'release-source.json') -Raw|ConvertFrom-Json
@"
# Matching corresponding source

Download XEnchantmentPatcher-source.zip from the project's release page for this
version, linked from the mod page, at no additional charge. It contains the matching
XEP and dependency sources, licenses and build instructions (BUILDING.md).

Source ZIP SHA-256: $sourceHash
XEP source fingerprint: $($identity.SourceSha256)
Base commit: $($identity.BaseCommit)

Redistributors: offer both ZIPs together on the release page and keep the matching
source available. If hosting source elsewhere, put clear directions beside the
application download. A moving development branch is not matching source.
"@|Set-Content -LiteralPath (Join-Path $appLicenses 'SOURCE.md') -Encoding utf8
# The root of the download holds the executable and the one folder, nothing else.
if(Get-ChildItem -LiteralPath $app -Force|Where-Object Name -NotIn 'XEnchantmentPatcher.exe','XEnchantmentPatcher-app'){throw 'Unexpected entry at the root of the application ZIP.'}
$appHash=Write-VerifiedArchive $app 'XEnchantmentPatcher.zip' 'app-files.json'
"Verified app ZIP SHA-256: $appHash"
"Verified source ZIP SHA-256: $sourceHash"
