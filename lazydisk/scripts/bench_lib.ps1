# Shared helpers for the lazydisk-bench workflow scripts (dot-source). Run elevated on a Windows runner.
$ErrorActionPreference='Continue'; $ProgressPreference='SilentlyContinue'
$env:GIT_PAGER='cat'; $env:DOTNET_NOLOGO=1; $env:DOTNET_CLI_TELEMETRY_OPTOUT=1; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
. "$PSScriptRoot\H_lib.ps1"      # DP (diskpart), Detach-Vhd
$CF='D:\cf'; $CL="$CF\lazy\CfLazy.exe"; $EM="$CF\em\ExtentMap.exe"; $VD="$CF\vd\VdAttach.exe"; $FDX="$CF\fd\FastDownload.exe"
$ART = if($env:BENCH_ART_PREFIX){ $env:BENCH_ART_PREFIX } else { 'ld' }   # artifact name prefix (artifacts are per run, so names only need to be unique inside one run)

function Get-Workload([string]$name){
  switch($name){
    'serilog'          { @{ name=$name; size=3072;  dir='serilog'; subset=$null } }
    'runtime-libs'     { @{ name=$name; size=40960; dir='runtime'; subset='libs' } }
    'runtime-clr-libs' { @{ name=$name; size=61440; dir='runtime'; subset='clr+libs' } }
    'selftest'         { @{ name=$name; size=256; dir='selftest'; subset=$null } }
    default { throw "unknown workload '$name' (serilog | runtime-libs | runtime-clr-libs)" }
  }
}
function Get-BenchDir([string]$workload){ "D:\lazy\i\$workload" }
function Round2($x){ [math]::Round([double]$x,2) }
# Environment for the workload; identical in every variant (drive letter V: is fixed because restore state in the image holds absolute paths).
function Set-WorkloadEnv { $env:NUGET_PACKAGES='V:\nuget'; $env:DOTNET_MULTILEVEL_LOOKUP='0'; $env:DOTNET_CLI_HOME='V:\dotnet-home'; $env:MSBUILDDISABLENODEREUSE='1' }
function Stop-BuildServers { cmd /c "taskkill /f /im dotnet.exe /im MSBuild.exe /im VBCSCompiler.exe /im msbuild.exe 2>nul" | Out-Null }

# The measured workload. Runs inside the image mounted at V:. Returns the exit code of the (last failing) step.
function Invoke-Workload($cfg,[string]$logDir){
  Set-WorkloadEnv; $rc=0
  if($cfg.name -eq 'selftest'){
    $sw=[Diagnostics.Stopwatch]::StartNew(); $h=(Get-ChildItem V:\selftest -File | % { (Get-FileHash $_.FullName -Algorithm MD5).Hash }) -join ''; "step hash-all: $(Round2 $sw.Elapsed.TotalSeconds) s"
  } elseif($cfg.name -eq 'serilog'){
    Set-Location V:\serilog
    $sw=[Diagnostics.Stopwatch]::StartNew(); dotnet build -c Debug --no-incremental *> "$logDir\workload-build.log"; if($LASTEXITCODE){$rc=$LASTEXITCODE}; "step build: $(Round2 $sw.Elapsed.TotalSeconds) s (exit $LASTEXITCODE)"
    $sw.Restart(); dotnet test --no-build -c Debug *> "$logDir\workload-test.log"; if($LASTEXITCODE){$rc=$LASTEXITCODE}; "step test: $(Round2 $sw.Elapsed.TotalSeconds) s (exit $LASTEXITCODE)"
    dotnet build-server shutdown 2>&1 | Out-Null
  } else {
    Set-Location V:\runtime
    $sw=[Diagnostics.Stopwatch]::StartNew()
    cmd /c "build.cmd -subset $($cfg.subset) -c Release -ci > $logDir\workload-build.log 2>&1"; $rc=$LASTEXITCODE; "step build.cmd -subset $($cfg.subset) -c Release: $(Round2 $sw.Elapsed.TotalSeconds) s (exit $rc)"
  }
  Stop-BuildServers; Set-Location D:\
  return $rc
}
