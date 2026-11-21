# Renders Unity reference frames for the camera poses a native port captured, and dumps scene data.
# Installs UnityReference.cs.txt into <Source>/Assets/Editor/CodexFidelity for the run and removes it afterwards.
#   -Config  JSON: {"quality":3,"scenes":[{"name":"Level","scene":"Assets/..unity","additive":[],"cameraPrefab":"Assets/..prefab"}]}
#            A scene's optional "probeAt":[x,y,z, x,y,z, ...] adds the interpolated light probe and probe occlusion at
#            those points to its .lights.txt: what a moving object standing there receives.
#   -Poses   folder with <name>.png.camera.json files: {"eye":[x,y,z],"forward":[..],"up":[..],"fov":deg,"width":w,"height":h}
#   -Out     folder for <name>[.<experiment>].png, .post.json (URP volume stack), .lights.txt, .ambient.json,
#            .surfaces.json (per-mesh URP Lit parameters), .lightmaps.txt, .probe.txt and, where the scene has baked
#            light probes, .lightprobes (their positions and six-axis irradiance)
param([Parameter(Mandatory)][string]$Source,[Parameter(Mandatory)][string]$Config,[Parameter(Mandatory)][string]$Poses,
      [Parameter(Mandatory)][string]$Out,[string]$Experiment="",[int]$Quality=-1,[string]$Unity,[string]$Probe="")
$ErrorActionPreference="Stop"
$Source=(Resolve-Path $Source).Path;$Config=(Resolve-Path $Config).Path;$Poses=(Resolve-Path $Poses).Path
New-Item -ItemType Directory -Force $Out | Out-Null;$Out=(Resolve-Path $Out).Path
if(!$Unity){
  $version=(Select-String -Path (Join-Path $Source "ProjectSettings/ProjectVersion.txt") -Pattern "m_EditorVersion: (\S+)").Matches[0].Groups[1].Value
  $Unity="C:\Program Files\Unity\Hub\Editor\$version\Editor\Unity.exe"
}
$open=Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.Replace([char]92,'/') -like "*$($Source.Replace([char]92,'/'))*" }
if($open){throw "The source Unity project is open in the editor"}
$folder=Join-Path $Source "Assets/Editor/CodexFidelity"
$editorExisted=Test-Path (Join-Path $Source "Assets/Editor")
New-Item -ItemType Directory -Force $folder | Out-Null
Copy-Item (Join-Path $PSScriptRoot "UnityReference.cs.txt") (Join-Path $folder "CodexFidelityCapture.cs")
$env:CODEX_FIDELITY_POSES=$Poses;$env:CODEX_FIDELITY_OUT=$Out;$env:CODEX_FIDELITY_CONFIG=$Config;$env:CODEX_FIDELITY_EXPERIMENT=$Experiment;$env:CODEX_FIDELITY_PROBE=$Probe
$env:CODEX_FIDELITY_QUALITY=if($Quality -ge 0){"$Quality"}else{$null}
$log=Join-Path $Out "unity$(if($Experiment){'-'+$Experiment}).log"
try{
  # Wait on the editor itself: Start-Process -Wait also waits for helper processes it may leave behind.
  $p=Start-Process -FilePath $Unity -ArgumentList "-batchmode -force-d3d11 -projectPath `"$Source`" -executeMethod CodexFidelityCapture.Run -logFile `"$log`"" -PassThru -WindowStyle Hidden
  $p.WaitForExit()
  Select-String -Path $log -Pattern "FIDELITY|error CS" | ForEach-Object { $_.Line }
  if($p.ExitCode -ne 0){Write-Warning "Unity exited $($p.ExitCode); read $log"}
}finally{
  Remove-Item -Recurse -Force $folder
  Remove-Item -Force "$folder.meta" -ErrorAction SilentlyContinue
  if(!$editorExisted){Remove-Item -Recurse -Force (Join-Path $Source "Assets/Editor");Remove-Item -Force (Join-Path $Source "Assets/Editor.meta") -ErrorAction SilentlyContinue}
  foreach($k in "POSES","OUT","CONFIG","EXPERIMENT","QUALITY","PROBE"){Remove-Item "Env:CODEX_FIDELITY_$k" -ErrorAction SilentlyContinue}
}
