# Consumer contract only. Packaging must create this hash-bound manifest beside
# a verified protocol-2 EXE; never probe an unknown build by executing it.
function Assert-SessionCapableBuild([string]$ExecutablePath){
 if(-not $ExecutablePath){throw 'Parallel launches require -ExecutablePath pointing to a verified session-capable build; registered executable names are not sufficient.'}
 $exe=(Resolve-Path -LiteralPath $ExecutablePath -ErrorAction Stop).Path
 $file=Join-Path (Split-Path $exe -Parent) 'session-capabilities.json'
 if(-not(Test-Path -LiteralPath $file -PathType Leaf)){throw "Session capability manifest missing: $file. Do not use older or unverified builds for parallel launch. Await the integrated session-capable package."}
 try{$cap=Get-Content -LiteralPath $file -Raw -ErrorAction Stop|ConvertFrom-Json -ErrorAction Stop}catch{throw "Malformed session capability manifest: $file. $($_.Exception.Message)"}
 if($null -eq $cap -or ($cap.SchemaVersion -isnot [int] -and $cap.SchemaVersion -isnot [long]) -or $cap.SchemaVersion -ne 1 -or ($cap.SessionProtocol -isnot [int] -and $cap.SessionProtocol -isnot [long]) -or $cap.SessionProtocol -ne 2 -or $cap.OutputLockProtocol -cne 'windows-file-id-v2'){
  throw "Unsupported session capability manifest: $file. Required SchemaVersion=1, SessionProtocol=2, OutputLockProtocol=windows-file-id-v2. Mixed protocols cannot safely share output or settings."
 }
 if($cap.ExecutableSha256 -isnot [string] -or $cap.ExecutableSha256 -notmatch '^[0-9a-fA-F]{64}$'){throw "Malformed executable SHA256 in session capability manifest: $file"}
 $hash=(Get-FileHash -LiteralPath $exe -Algorithm SHA256 -ErrorAction Stop).Hash
 if($hash -ne $cap.ExecutableSha256){throw "Stale session capability manifest: executable SHA256 mismatch for $exe. No launch was performed."}
 [pscustomobject]@{Executable=$exe;ExecutableSha256=$hash;SessionProtocol=2;OutputLockProtocol='windows-file-id-v2';Manifest=$file}
}
function Assert-RunLaunchIdentity($Meta,[string]$RunId,$Capability,[string]$SettingsPath,[string]$OutputPath,[string]$Mode){
 if($Meta.RunId -cne $RunId -or $Meta.SessionProtocol -ne 2 -or $Meta.OutputLockProtocol -cne 'windows-file-id-v2' -or $Meta.ExecutableSha256 -ne $Capability.ExecutableSha256){throw 'Actual session identity/protocol/executable hash does not match the verified launch. Do not treat this run as started.'}
 foreach($pair in @(@($Meta.Executable,$Capability.Executable),@($Meta.SettingsFile,$SettingsPath),@($Meta.OutputFolder,$OutputPath))){
  if(-not $pair[0] -or -not [string]::Equals([IO.Path]::GetFullPath($pair[0]),[IO.Path]::GetFullPath($pair[1]),[StringComparison]::OrdinalIgnoreCase)){throw 'Actual executable/settings/output path does not match the isolated launch. Do not treat this run as started.'}
 }
 if($Meta.Mode -cne $Mode){throw 'Actual session mode does not match the launch.'}
 if($Meta.Pid -isnot [ValueType] -or $Meta.Pid -le 0 -or -not $Meta.ProcessStartedUtc){throw 'Actual session process identity is incomplete.'}
}
