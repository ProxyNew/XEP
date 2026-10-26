# Publish into Compiled, or a NEW side-by-side destination for revision comparisons.
# Archives only the replaced EXE/PDB. Does not overwrite an active published build.
param([string]$Destination,[string]$Version)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$sideBySide=[bool]$Destination
if(-not $Destination){$Destination=Join-Path $root 'Compiled'}elseif(-not [IO.Path]::IsPathRooted($Destination)){$Destination=Join-Path $root $Destination}
$Destination=[IO.Path]::GetFullPath($Destination)
$project=Join-Path $root 'src\XEnchantmentPatcher.csproj'
if(-not $Version){$Version=([xml](Get-Content -LiteralPath $project)).Project.PropertyGroup.Version | Where-Object {$_} | Select-Object -First 1}
$exe=Join-Path $Destination 'XEnchantmentPatcher.exe'
function Get-SourceIdentity {
 if(Test-Path -LiteralPath (Join-Path $root 'release-source.json')){
  $head=(Get-Content -LiteralPath (Join-Path $root 'release-source.json') -Raw|ConvertFrom-Json).BaseCommit
  $status=@('Rebuild from corresponding-source archive')
 }else{
  $head=(& git -C $root rev-parse HEAD).Trim()
  if($LASTEXITCODE -ne 0){throw 'Cannot record the source revision.'}
  $status=@(& git -C $root status --porcelain)
 }
 $inputs=@(Get-ChildItem -LiteralPath (Join-Path $root 'src') -File -Recurse | Where-Object {
  $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and $_.Name -notlike '*_wpftmp*'
 } | Sort-Object FullName | ForEach-Object {
  [ordered]@{Path=$_.FullName.Substring($root.Length+1).Replace('\','/');Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
 })
 $canonical=($inputs|ForEach-Object {$_.Path+':'+$_.Sha256}) -join "`n"
 $sha=[Security.Cryptography.SHA256]::Create()
 try{$fingerprint=[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical))).Replace('-','')}finally{$sha.Dispose()}
 [pscustomobject]@{BaseCommit=$head;Dirty=($status.Count -gt 0);WorkingTreeStatus=$status;SourceSha256=$fingerprint;Inputs=$inputs}
}
if($sideBySide -and (Test-Path -LiteralPath $exe)){throw 'A side-by-side build already exists here. Select a new destination; old builds are retained.'}
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
try{$publishLease=[IO.File]::Open((Join-Path $Destination '.xep-publish.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::Read)}catch{throw "This published build is active or another publish owns it. Choose a new destination: $Destination"}
function Assert-NoActiveBuild {
 $active=Get-CimInstance Win32_Process | Where-Object {$_.ExecutablePath -and [string]::Equals($_.ExecutablePath,$exe,[StringComparison]::OrdinalIgnoreCase)}
 if($active){throw "Refusing to replace an active build (PID $($active.ProcessId -join ',')). Choose a new Destination."}
}
try{
 Assert-NoActiveBuild
 $scratch=Join-Path $root ('test-output\publish\'+[guid]::NewGuid().ToString('N'))
 $obj=Join-Path $scratch 'obj\';$bin=Join-Path $scratch 'bin\';$stage=Join-Path $scratch 'published'
 $source=Get-SourceIdentity
 $sourceTag=$source.BaseCommit.Substring(0,12)+$(if($source.Dirty){'.dirty.'+ $source.SourceSha256.Substring(0,12)}else{''})
 $informational=$Version+'+'+$sourceTag
 $lock=Join-Path $scratch 'packages.lock.json'
 $restoreOptions=@()
 if(Test-Path -LiteralPath (Join-Path $root 'release-source.json')){
  New-Item -ItemType Directory -Path $scratch -Force|Out-Null
  Copy-Item -LiteralPath (Join-Path $root 'src/packages.lock.json') -Destination $lock
  $restoreOptions+= '-p:RestoreLockedMode=true'
 }
 dotnet publish $project -c Release -o $stage "-p:Version=$Version" "-p:InformationalVersion=$informational" -p:IncludeSourceRevisionInInformationalVersion=false "-p:BaseIntermediateOutputPath=$obj" "-p:MSBuildProjectExtensionsPath=$obj" "-p:BaseOutputPath=$bin" -p:RestorePackagesWithLockFile=true "-p:NuGetLockFilePath=$lock" @restoreOptions -p:NuGetAudit=false --ignore-failed-sources
 if($LASTEXITCODE -ne 0){throw 'Publish failed. Existing build was not changed.'}
 if((Get-SourceIdentity).SourceSha256 -ne $source.SourceSha256){throw 'Source inputs changed during publishing. No package was installed; rerun from a stable source snapshot.'}
 & (Join-Path $PSScriptRoot 'release-legal.ps1') -Root $root -Stage $stage -AssetsFile (Join-Path $obj 'project.assets.json') -LockFile $lock -Source $source
 # Only the executable just built from this source may receive a manifest.
 # Verify its actual protocol/identity using private CLI startup evidence.
 $stageExe=Join-Path $stage 'XEnchantmentPatcher.exe'
 $hash=(Get-FileHash -LiteralPath $stageExe -Algorithm SHA256).Hash
 $probe=Join-Path $scratch 'capability-probe';$seed=Join-Path $scratch 'probe-settings.json'
 [IO.File]::WriteAllText($seed,'{}',[Text.UTF8Encoding]::new($false))
 $probeId=[guid]::NewGuid().ToString('N')
 $start=[Diagnostics.ProcessStartInfo]::new($stageExe)
 $start.UseShellExecute=$false;$start.CreateNoWindow=$true
 function Quote-ProbeArgument([string]$Value){'"'+[regex]::Replace([regex]::Replace($Value,'(\\*)"','$1$1\"'),'(\\+)$','$1$1')+'"'}
 $start.Arguments=(@('--help','--no-save','--settings-snapshot',$seed,'--session-dir',$probe,'--run-id',$probeId)|ForEach-Object {Quote-ProbeArgument $_}) -join ' '
 $process=[Diagnostics.Process]::Start($start)
 try{if(-not $process.WaitForExit(20000)){$process.Kill($true);$process.WaitForExit();throw 'Owned packaging startup probe timed out.'}
  if($process.ExitCode -ne 0){throw "Packaging startup probe failed ($($process.ExitCode)). Inspect $probe"}
  $actual=Get-Content -LiteralPath (Join-Path $probe 'run.json') -Raw|ConvertFrom-Json
  if($actual.RunId -cne $probeId -or $actual.Pid -ne $process.Id -or $actual.ExecutableSha256 -ne $hash -or $actual.SessionProtocol -ne 2 -or $actual.OutputLockProtocol -cne 'windows-file-id-v2' -or $actual.State -ne 'completed' -or $actual.ExitCode -ne 0 -or $actual.Executable -ine $stageExe -or $actual.Build -cne $informational){throw "Packaged runtime identity/protocol/provenance mismatch. No manifest was emitted. Inspect $probe"}
 }finally{$process.Dispose()}
 @{SchemaVersion=1;SessionProtocol=2;OutputLockProtocol='windows-file-id-v2';ExecutableSha256=$hash}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $stage 'session-capabilities.json') -Encoding utf8
 [ordered]@{Version=$Version;InformationalVersion=$informational;ExecutableSha256=$hash;Source=$source;Sdk=(& dotnet --version);BuiltUtc=[DateTimeOffset]::UtcNow.ToString('O');StartupProbe=$actual;SingleFile=$true}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $stage 'build-provenance.json') -Encoding utf8
 . (Join-Path $PSScriptRoot 'session-capabilities.ps1')
 $null=Assert-SessionCapableBuild $stageExe
 if((Get-SourceIdentity).SourceSha256 -ne $source.SourceSha256){throw 'Source changed during release assembly; destination preserved.'}
 Assert-NoActiveBuild
 if(Test-Path -LiteralPath $exe){
  $oldVer=(Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
  $archive=[IO.Path]::GetFullPath((Join-Path $root "Releases\$oldVer"))
  if(-not $archive.StartsWith([IO.Path]::GetFullPath((Join-Path $root 'Releases'))+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid release archive path.'}
  New-Item -ItemType Directory -Force -Path $archive | Out-Null
  foreach($name in @('XEnchantmentPatcher.exe','XEnchantmentPatcher.pdb')){
   $old=Join-Path $Destination $name;$saved=Join-Path $archive $name
   if(Test-Path -LiteralPath $old){
    if(Test-Path -LiteralPath $saved){if((Get-FileHash -LiteralPath $old).Hash -ne (Get-FileHash -LiteralPath $saved).Hash){throw "Archive differs: $saved"}}
    else{Copy-Item -LiteralPath $old -Destination $saved}
   }
  }
 }
 Get-ChildItem -LiteralPath $stage | ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force}
 $null=Assert-SessionCapableBuild $exe
 "Published $Version -> $Destination"
}finally{$publishLease.Dispose()}
