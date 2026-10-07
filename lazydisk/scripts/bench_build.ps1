# bench_build: build the workload image (fixed VHD, NTFS, drive V:), store it, and publish every blob the requested variants need.
#   Part N recipe: workload clone (runtime at a pinned commit), bootstrapped .dotnet SDK, NuGet cache INSIDE the image (NUGET_PACKAGES=V:\nuget), restore done at image-build time.
#   Run elevated, in the same job as bench_run.ps1 (Actions artifacts are per run). Needs D:\cf\{lazy,em,vd,fd} from setup_lab.ps1 + the FastDownload publish.
param(
  [Parameter(Mandatory)][string]$Workload,           # serilog | runtime-libs | runtime-clr-libs | selftest
  [int]$SizeMiB = 0,                                  # 0 = workload default
  [string]$Commit = '60629d14374c56f1cb51819049ad1fa529307f8d',   # dotnet/runtime v10.0.0 (runtime workloads)
  [string]$Variants = 'native,fd,lazy,lazyfd',
  [int]$ChunkKiB = 1024, [int]$FdBlockMiB = 128, [int]$FdLazyBlockKiB = 1024
)
. "$PSScriptRoot\bench_lib.ps1"
$cfg = Get-Workload $Workload; if($SizeMiB -le 0){ $SizeMiB = $cfg.size }
$vs = $Variants -split '[,\s]+' | ? { $_ }
$BD = Get-BenchDir $Workload; Remove-Item $BD -Recurse -Force -ea 0
New-Item -ItemType Directory "$BD\root","$BD\store","$BD\logs" -Force | Out-Null
New-Item -ItemType Directory D:\lazy\results -Force | Out-Null
Start-Transcript "$BD\logs\build.txt" -Force | Out-Null
$img = "$BD\root\parent.vhd"; $sw = [Diagnostics.Stopwatch]::StartNew(); $rec = [ordered]@{ kind='build'; workload=$Workload; size_mib=$SizeMiB; commit=$null }
function Die($m){ "BUILD FAILED: $m"; Stop-Transcript | Out-Null; exit 1 }
function Chk($what){ if($LASTEXITCODE){ Die "$what (exit $LASTEXITCODE)" } }
function T($k){ $rec[$k] = Round2 $sw.Elapsed.TotalSeconds; "$k = $($rec[$k]) s"; $sw.Restart() }

DP "create vdisk file=`"$img`" maximum=$SizeMiB type=fixed`nselect vdisk file=`"$img`"`nattach vdisk`ncreate partition primary offset=1024`nformat fs=ntfs unit=4096 quick label=LAZY`nassign letter=V" | Select -Last 2
T 'create_format_s'
git config --global core.longpaths true; git config --global core.autocrlf false
Set-WorkloadEnv; New-Item -ItemType Directory V:\nuget,V:\dotnet-home | Out-Null

switch -Wildcard ($Workload){
  'serilog' {
    git clone -q --depth 1 https://github.com/serilog/serilog.git V:\serilog 2>&1 | Select -Last 2; T 'clone_s'
    $rec.commit = (git -C V:\serilog rev-parse HEAD); Set-Location V:\serilog
    dotnet restore *> "$BD\logs\restore.log"; "restore exit $LASTEXITCODE"; T 'restore_s'
  }
  'runtime-*' {
    New-Item -ItemType Directory V:\runtime | Out-Null; Set-Location V:\runtime
    git init -q .; git remote add origin https://github.com/dotnet/runtime.git
    git fetch -q --depth 1 origin $Commit 2>&1 | Select -Last 2; git checkout -q FETCH_HEAD 2>&1 | Select -Last 2
    $rec.commit = (git rev-parse HEAD); "pinned commit: $($rec.commit)"; T 'clone_s'
    cmd /c "build.cmd -subset $($cfg.subset) -restore -ci > $BD\logs\restore.log 2>&1"; $rec.restore_exit=$LASTEXITCODE; "restore exit $LASTEXITCODE"; T 'restore_s'
    Get-Content $BD\logs\restore.log -Tail 6
  }
  'selftest' {
    New-Item -ItemType Directory V:\selftest | Out-Null; $rng = New-Object Random 7; $b = New-Object byte[] 1MB
    1..96 | % { $rng.NextBytes($b); [IO.File]::WriteAllBytes("V:\selftest\f$_.bin",$b) }; T 'clone_s'
  }
}
Stop-BuildServers; Set-Location D:\
$m = Get-ChildItem V:\ -Recurse -File -Force -ea 0 | Measure-Object Length -Sum
$rec.files = $m.Count; $rec.file_bytes = $m.Sum; $rec.volume_used_mib = [int]((Get-PSDrive V).Used/1MB)
"files: $($m.Count) bytes: $($m.Sum) volume used MiB: $($rec.volume_used_mib)"
& $EM map V: 1048576 ([int64]$SizeMiB*1MB) "$BD\holes.txt" | Select -Last 3; Chk 'ExtentMap map'; T 'map_s'
Start-Sleep 3; Detach-Vhd $img
$len = (Get-Item $img).Length; $rec.image_bytes = $len
DP "create vdisk file=`"$BD\child.vhd`" parent=`"$img`"" | Out-Null
$rec.child_bytes = (Get-Item "$BD\child.vhd").Length
& $CL ranges "$BD\holes.txt" $len "$BD\meta.ranges" | Tee-Object -Variable rr | Out-Host; Chk 'CfLazy ranges'; $rec.meta_ranges = "$rr"
Move-Item $img "$BD\store\parent.vhd"; Copy-Item "$BD\child.vhd" "$BD\store\child.vhd"

function Up($name,$file){ $s=[Diagnostics.Stopwatch]::StartNew(); & $CL up "$ART-$Workload-$name" $file | Out-Host; Chk "CfLazy up $name"; "upload $name $(Round2 $s.Elapsed.TotalSeconds) s" }
if($vs -contains 'lazy'){
  & $CL pack "$BD\store\parent.vhd" "$BD\data.bin" "$BD\data.idx" $ChunkKiB 1 | Tee-Object -Variable pk | Out-Host; Chk 'CfLazy pack'; T 'pack_s'
  $rec.cfl_data_bytes = (Get-Item "$BD\data.bin").Length; $rec.cfl_idx_bytes = (Get-Item "$BD\data.idx").Length; $rec.cfl_pack = "$pk"
  Up 'data' "$BD\data.bin"; Up 'idx' "$BD\data.idx"; T 'upload_cfl_s'
}
if(($vs -contains 'lazy') -or ($vs -contains 'lazyfd')){ Up 'ranges' "$BD\meta.ranges" }
if(($vs -contains 'lazy') -or ($vs -contains 'lazyfd') -or ($vs -contains 'fd')){ Up 'child' "$BD\child.vhd" }
if(($vs -contains 'fd') -or ($vs -contains 'lazyfd')){
  # FastDownload reads data regions of a SPARSE source: make a sparse copy (all-zero 64 KiB blocks unallocated). Same bytes as the original.
  & $EM sparsecopy "$BD\store\parent.vhd" "$BD\store\parent.sparse" 64 | Out-Host; Chk 'ExtentMap sparsecopy'; T 'sparsecopy_s'
  $env:FASTDL_WRITE_ONLY_SAS = '1'
  function FdUp($name,$mode,$blockBytes){
    $u = (& $CL art-create "$ART-$Workload-$name" | Select -Last 1).Trim(); $s=[Diagnostics.Stopwatch]::StartNew()
    & $FDX upload --uri $u --input "$BD\store\parent.sparse" --sparse-handling $mode --block-size $blockBytes --hash-type Murmur --manifest-path "$BD\$name.manifest.json" *> "$BD\logs\upload-$name.log"
    $rc=$LASTEXITCODE; $t=Round2 $s.Elapsed.TotalSeconds
    $bytes = ((Select-String '"BytesUploaded"\s*:\s*(\d+)' "$BD\logs\upload-$name.log" | Select -First 1).Matches.Groups[1].Value)
    if($rc -eq 0 -and $bytes){ & $CL art-finalize "$ART-$Workload-$name" $bytes | Out-Host }
    $rec["fd_${name}_upload_exit"]=$rc; $rec["fd_${name}_upload_s"]=$t; $rec["fd_${name}_blob_bytes"]=[int64]$bytes; $rec["fd_${name}_manifest_bytes"]=(Get-Item "$BD\$name.manifest.json" -ea 0).Length
    "FD upload $name ($mode, block $blockBytes): exit $rc, $t s, $bytes bytes"; if($rc){ Get-Content "$BD\logs\upload-$name.log" -Tail 15; Die "FastDownload upload $name failed" }
    if($LASTEXITCODE){ Die "CfLazy art-finalize $name (exit $LASTEXITCODE)" }
  }
  if($vs -contains 'fd'){ FdUp 'fdfull' 'Compact' ($FdBlockMiB*1MB) }
  if($vs -contains 'lazyfd'){
    FdUp 'fdlazy' 'SkipHoles' ($FdLazyBlockKiB*1KB)
    & $CL fdidx "$BD\fdlazy.manifest.json" "$BD\fdlazy.idx" | Tee-Object -Variable fi | Out-Host; Chk 'CfLazy fdidx'; $rec.fdidx = "$fi"
    Up 'fdidx' "$BD\fdlazy.idx"
  }
  T 'upload_fd_s'
}
Remove-Item "$BD\store\parent.sparse" -Force -ea 0
($rec | ConvertTo-Json -Compress) | Add-Content D:\lazy\results\results.jsonl
Stop-Transcript | Out-Null
"BUILD DONE"
