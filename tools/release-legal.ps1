# Add notices and matching source to a freshly published private tree.
# Called by publish.ps1 after restore/build; never uses normal settings/output.
param(
 [Parameter(Mandatory)][string]$Root,
 [Parameter(Mandatory)][string]$Stage,
 [Parameter(Mandatory)][string]$AssetsFile,
 [Parameter(Mandatory)][string]$LockFile,
 [Parameter(Mandatory)]$Source
)
$ErrorActionPreference='Stop'
$assets=Get-Content -LiteralPath $AssetsFile -Raw|ConvertFrom-Json
$catalog=Get-Content -LiteralPath (Join-Path $Root 'licenses/dependencies.json') -Raw|ConvertFrom-Json
$legal=Join-Path $Stage 'licenses'
New-Item -ItemType Directory -Path $legal -Force|Out-Null
Get-ChildItem -LiteralPath (Join-Path $Root 'licenses') | Where-Object Name -NotIn @('sources','.gitignore') | ForEach-Object {
 Copy-Item -LiteralPath $_.FullName -Destination $legal -Recurse -Force
}
# Large upstream snapshots stay out of Git. Release assembly fetches pinned,
# hash-verified bytes, or uses an existing local cache with the same hash.
$downloads=Get-Content -LiteralPath (Join-Path $legal 'source-downloads.json') -Raw|ConvertFrom-Json
foreach($download in $downloads){
 if($download.Archive -notmatch '^sources/[A-Za-z0-9_.-]+$'){throw 'Invalid source archive path'}
 $target=Join-Path $legal $download.Archive
 $cached=Join-Path (Join-Path $Root 'licenses') $download.Archive
 New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force|Out-Null
 if(Test-Path -LiteralPath $cached -PathType Leaf){Copy-Item -LiteralPath $cached -Destination $target}
 else{Invoke-WebRequest -Uri $download.Url -OutFile $target}
 if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ine $download.Sha256){throw "Upstream source hash mismatch: $($download.Archive)"}
}
foreach($name in @('LICENSE','LICENSING.md','THIRD-PARTY-NOTICES.md','README.md')){
 Copy-Item -LiteralPath (Join-Path $Root $name) -Destination (Join-Path $Stage $name) -Force
}
$components=@()
foreach($library in $assets.libraries.PSObject.Properties){
 if($library.Value.type -ne 'package'){continue}
 $id,$version=$library.Name.Split('/')
 # Build-time linker tooling is not part of the distributed application.
 if($id -eq 'Microsoft.NET.ILLink.Tasks'){continue}
 $entry=@($catalog.Packages|Where-Object {$_.Id -ieq $id -and $_.Version -ceq $version})
 if($entry.Count -ne 1){throw "Unreviewed dependency: $id/$version. Update licenses/dependencies.json before release."}
 $entry=$entry[0]
 if(-not $entry.License -or -not $entry.LicenseFiles.Count){throw "Missing license: $id/$version"}
 foreach($file in $entry.LicenseFiles){if(-not (Test-Path -LiteralPath (Join-Path $legal $file) -PathType Leaf)){throw "Missing notice: $file"}}
 if($entry.SourceArchive){
  $archive=Join-Path $legal $entry.SourceArchive
  if(-not (Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $entry.SourceSha256){throw "Missing or changed source archive: $id/$version"}
 }else{throw "Missing corresponding source: $id/$version"}
 $components += $entry
}
# Copy the exact runtime packs' own license/third-party notices (not SDK notices).
$runtimeRows=@()
foreach($dependency in $assets.project.frameworks.PSObject.Properties.Value.downloadDependencies){
 if($dependency.name -notin @('Microsoft.NETCore.App.Runtime.win-x64','Microsoft.WindowsDesktop.App.Runtime.win-x64')){continue}
 $version=$dependency.version.Trim('[',']').Split(',')[0].Trim()
 $relative=$dependency.name.ToLowerInvariant()+'/'+$version
 $pack=@($assets.packageFolders.PSObject.Properties.Name|ForEach-Object {Join-Path $_ $relative}|Where-Object {Test-Path -LiteralPath $_})|Select-Object -First 1
 if(-not $pack){throw "Missing runtime pack: $relative"}
 $target=Join-Path $legal ('dotnet/'+$dependency.name+'/'+$version)
 New-Item -ItemType Directory -Path $target -Force|Out-Null
 $notices=@(Get-ChildItem -LiteralPath $pack -File|Where-Object {$_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)(\..*)?$|\.nuspec$|\.versions\.txt$'})
 if(-not ($notices|Where-Object {$_.Name -match '^LICENSE'})){throw "Runtime pack has no license: $relative"}
 if($dependency.name -eq 'Microsoft.NETCore.App.Runtime.win-x64' -and -not ($notices|Where-Object Name -eq 'THIRD-PARTY-NOTICES.TXT')){throw 'Missing .NET runtime third-party notices'}
 $notices|ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $target}
 if($dependency.name -eq 'Microsoft.WindowsDesktop.App.Runtime.win-x64'){
  $desktop=Join-Path $legal ('dotnet-desktop/'+$version)
  $provenance=Get-Content -LiteralPath (Join-Path $desktop 'provenance.json') -Raw|ConvertFrom-Json
  $spec=[xml](Get-Content -LiteralPath (Join-Path $pack ($dependency.name.ToLowerInvariant()+'.nuspec')) -Raw)
  if($spec.package.metadata.repository.commit -cne $provenance.Commit){throw 'Windows Desktop notice source does not match runtime pack'}
  foreach($file in $provenance.Files){if((Get-FileHash -LiteralPath (Join-Path $desktop $file.Name) -Algorithm SHA256).Hash -cne $file.Sha256){throw "Desktop notice changed: $($file.Name)"}}
 }
 $runtimeRows += [ordered]@{Id=$dependency.name;Version=$version;Files=@($notices|ForEach-Object {[ordered]@{Name=$_.Name;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}})}
}
if(@($runtimeRows|ForEach-Object {$_.Id}|Select-Object -Unique).Count -ne 2){throw 'Both .NET runtime and Windows Desktop pack identities are required'}
[ordered]@{Packages=$components;RuntimePacks=$runtimeRows}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $legal 'resolved-components.json') -Encoding utf8
$boost=Get-Content -LiteralPath (Join-Path $legal 'boost-source.json') -Raw|ConvertFrom-Json
if((Get-FileHash -LiteralPath (Join-Path $legal $boost.Archive) -Algorithm SHA256).Hash -ine $boost.Sha256){throw 'Boost source archive mismatch'}

# Snapshot source bytes used by this build, including the permitted busy GIF.
$sourceDir=Join-Path $Stage 'source'
$snapshot=Join-Path (Split-Path $Stage -Parent) 'corresponding-source'
New-Item -ItemType Directory -Path $sourceDir,$snapshot -Force|Out-Null
$files=@()
foreach($folder in @('src','tools','docs')){
 $files += Get-ChildItem -LiteralPath (Join-Path $Root $folder) -Recurse -File|Where-Object {
  $_.FullName -notmatch '[\\/](bin|obj|__pycache__|node_modules)[\\/]' -and
  $_.Name -notlike '*_wpftmp*' -and $_.FullName -notmatch '[\\/]tools[\\/]cloudsync[\\/]'
 }
}
foreach($name in @('LICENSE','LICENSING.md','THIRD-PARTY-NOTICES.md','README.md','CHANGELOG.md','.gitignore')){$files+=Get-Item -LiteralPath (Join-Path $Root $name)}
$receipt=@()
foreach($file in $files){
 $relative=[IO.Path]::GetRelativePath($Root,$file.FullName)
 $target=Join-Path $snapshot $relative
 New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force|Out-Null
 Copy-Item -LiteralPath $file.FullName -Destination $target
 $receipt += [ordered]@{Path=$relative.Replace('\','/');Sha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash}
}
foreach($input in $Source.Inputs){
 $actual=@($receipt|Where-Object Path -CEQ $input.Path)
 if($actual.Count -ne 1 -or $actual[0].Sha256 -cne $input.Sha256){throw "Source changed while packaging: $($input.Path)"}
}
Copy-Item -LiteralPath $LockFile -Destination (Join-Path $snapshot 'src/packages.lock.json')
$Source|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $snapshot 'release-source.json') -Encoding utf8
$sdk=(& dotnet --version).Trim()
@{sdk=@{version=$sdk;rollForward='latestPatch'}}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $snapshot 'global.json') -Encoding utf8
$receipt|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $sourceDir 'source-files.json') -Encoding utf8
@"
# Corresponding source for this binary

XEP base commit: $($Source.BaseCommit)
Source fingerprint: $($Source.SourceSha256)
SDK used: $sdk

Extract XEP-source.zip to an empty directory. Copy the release's licenses folder
beside its extracted src and tools folders. Install the .NET SDK named in global.json.
Run dotnet restore src/XEnchantmentPatcher.csproj --locked-mode, then
pwsh -File tools/publish.ps1 -Destination <new-directory>.
NuGet restore needs network access. The lock file pins dependency versions/hashes;
the included licenses/sources archives contain the reviewed upstream source.
Build instructions for those dependencies are in their complete upstream archives.
The source-files.json hashes identify XEP snapshot files. The source archive includes
the unmodified Champollion source/build archive and its separately replaceable binary.
Compiler-generated metadata/build identity may differ when rebuilding outside Git.
The program does not require byte-identical replacement binaries.
"@|Set-Content -LiteralPath (Join-Path $sourceDir 'README.md') -Encoding utf8
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($snapshot,(Join-Path $sourceDir 'XEP-source.zip'))
foreach($name in @('Champollion.exe','Champollion-v1.3.2-source.zip','LICENSE-GPL-3.0.txt','LICENSE-LGPL-3.0.txt','LICENSE-Boost-1.0.txt','README.md')){
 if(-not (Test-Path -LiteralPath (Join-Path $Stage ('Champollion/'+$name)))){throw "Missing Champollion distribution input: $name"}
}
