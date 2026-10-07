# bench_run: one measured variant of a workload. Run elevated, after bench_build.ps1 in the SAME job/run (artifacts are per run).
#   native  : child over a LOCAL full copy of the parent (the image copied from local disk) = baseline
#   fd      : FastDownload full download of the Compact + SkipHoles-free blob (--skip-zero-regions, default 128 MiB chunks), clear the sparse flag, then native mount
#   lazy    : CfLazy placeholder parent, 1 MiB brotli chunks (CfLazy format), metadata pre-hydrated, sweep held
#   lazyfd  : same provider reading the blob FastDownload itself uploaded (1 MiB blocks, SkipHoles) through an index generated from its manifest
# Output: one JSON line appended to D:\lazy\results\results.jsonl (+ per-run logs in $BD\run_<variant>).
param(
  [Parameter(Mandatory)][string]$Workload, [Parameter(Mandatory)][ValidateSet('native','fd','lazy','lazyfd')][string]$Variant,
  [string]$Prov = '--sweep-delay-ms 250 --sweep-promote-ms 2000',   # extra args for 'CfLazy serve' (hold the sweep)
  [string]$FdArgs = '--skip-zero-regions'
)
. "$PSScriptRoot\bench_lib.ps1"
$cfg = Get-Workload $Workload; $BD = Get-BenchDir $Workload; $R = "$BD\root"; $W = "$BD\run_$Variant"
Remove-Item $R,$W -Recurse -Force -ea 0; New-Item -ItemType Directory $R,$W | Out-Null
Start-Transcript "$W\run.txt" -Force | Out-Null
$rec = [ordered]@{ kind='run'; workload=$Workload; variant=$Variant; ok=$false }
$job = [Diagnostics.Stopwatch]::StartNew(); $sw = [Diagnostics.Stopwatch]::StartNew()
function T($k){ $rec[$k] = Round2 $sw.Elapsed.TotalSeconds; "[$Variant] $k = $($rec[$k]) s"; $sw.Restart() }
$mon = Start-Process pwsh -ArgumentList '-NoProfile','-File',"$PSScriptRoot\I_mon.ps1","$W\mon.csv","$W\mon.stop" -PassThru -WindowStyle Hidden
$prov = $null
function Finish([bool]$ok){
  $rec.ok = $ok; $rec.total_job_s = Round2 $job.Elapsed.TotalSeconds
  New-Item "$W\mon.stop" -ItemType File -Force | Out-Null
  ($rec | ConvertTo-Json -Compress) | Add-Content D:\lazy\results\results.jsonl
  Stop-Transcript | Out-Null
}
# --- obtain the parent + child -------------------------------------------------------------------------------------------------------------
switch($Variant){
  'native' { Copy-Item "$BD\store\parent.vhd" "$R\parent.vhd"; Copy-Item "$BD\store\child.vhd" "$W\child.vhd"; T 'fetch_s'; $rec.fetch_bytes = (Get-Item "$R\parent.vhd").Length }
  'fd' {
    & $CL down "$ART-$Workload-child" "$W\child.vhd" | Out-Null
    $env:FASTDL_WRITE_ONLY_SAS = '0'
    $u = (& $CL art-url "$ART-$Workload-fdfull" | Select -Last 1).Trim()
    $fdargs = @('download','--uri',$u,'--output',"$R\parent.vhd",'--manifest-path',"$W\manifest.json") + ($FdArgs -split ' ' | ? {$_})
    & $FDX @fdargs *> "$W\fd-download.log"; $rec.fd_exit = $LASTEXITCODE
    if($rec.fd_exit){ Get-Content "$W\fd-download.log" -Tail 20; $rec.error='fastdownload failed'; Finish $false; return }
    $rec.fd_download_s = Round2 $sw.Elapsed.TotalSeconds
    $rec.fd_net_bytes = [int64](((Select-String '"StorageBytesDownloaded"\s*:\s*(\d+)' "$W\fd-download.log" | Select -First 1).Matches.Groups[1].Value))
    # a sparse file cannot be attached as a VHD: clear the flag (the image content is unchanged)
    fsutil sparse setflag "$R\parent.vhd" 0 | Out-Null; T 'fetch_s'
  }
  { $_ -in 'lazy','lazyfd' } {
    $d = if($Variant -eq 'lazy'){ @{ blob="$ART-$Workload-data"; idx='idx'; idxf='data.idx' } } else { @{ blob="$ART-$Workload-fdlazy"; idx='fdidx'; idxf='fdlazy.idx' } }
    foreach($x in @(@('child','child.vhd'),@('ranges','meta.ranges'),@($d.idx,$d.idxf))){ & $CL down "$ART-$Workload-$($x[0])" "$W\$($x[1])" | Out-Null }
    T 'fetch_small_s'
    $pa = @('serve',$R,'--src',"art:$($d.blob)",'--index',"$W\$($d.idxf)",'--name','parent.vhd','--prehydrate',"$W\meta.ranges",'--stop',"$W\cf.stop",'--drain',"$W\drain",'--log',"$W\cf.log") + ($Prov -split ' ' | ? {$_})
    $prov = Start-Process $CL -ArgumentList $pa -PassThru -RedirectStandardOutput "$W\prov.txt" -WindowStyle Hidden
    for($i=0;$i -lt 3000 -and -not (Select-String "$W\prov.txt" -Pattern 'READY' -Quiet -ea 0) -and -not $prov.HasExited;$i++){ Start-Sleep -Milliseconds 100 }
    $rec.prehydrate = ((Select-String "$W\prov.txt" -Pattern 'PREHYDRATE').Line); T 'provider_prehydrate_s'
  }
}
# --- attach ----------------------------------------------------------------------------------------------------------------------------------
$vd = Start-Process $VD -ArgumentList "$W\child.vhd",'0','0','--hold',"$W\vdstop" -PassThru -RedirectStandardOutput "$W\vd.txt" -NoNewWindow
for($i=0;$i -lt 2400 -and -not (Select-String "$W\vd.txt" -Pattern 'ATTACHED|AttachVirtualDisk rc=[1-9]|OpenVirtualDisk rc=[1-9]' -Quiet -ea 0);$i++){ Start-Sleep -Milliseconds 100 }
if(-not (Select-String "$W\vd.txt" -Pattern 'ATTACHED' -Quiet)){ $rec.error = 'attach failed: ' + ((Get-Content "$W\vd.txt") -join ' '); New-Item "$W\drain" -ItemType File -Force | Out-Null; Finish $false; return }
$d = Get-Disk | ? Location -like '*child.vhd'; if($d.IsOffline){ Set-Disk $d.Number -IsOffline $false }; if($d.IsReadOnly){ Set-Disk $d.Number -IsReadOnly $false }
$pt = Get-Partition -DiskNumber $d.Number | ? Type -eq 'IFS' | select -First 1
if($pt.DriveLetter -ne 'V'){ if($pt.DriveLetter){ Remove-PartitionAccessPath -DiskNumber $d.Number -PartitionNumber $pt.PartitionNumber -AccessPath "$($pt.DriveLetter):\" -ea 0 }; Add-PartitionAccessPath -DiskNumber $d.Number -PartitionNumber $pt.PartitionNumber -AccessPath 'V:\' }
T 'attach_s'
$lt = [Diagnostics.Stopwatch]::StartNew(); $n = (Get-ChildItem "V:\$($cfg.dir)" -Recurse -Force -File -ea 0 | Measure-Object).Count
$rec.first_listing_files = $n; $rec.first_listing_s = Round2 $lt.Elapsed.TotalSeconds; $rec.job_start_to_first_listing_s = Round2 $job.Elapsed.TotalSeconds
"[$Variant] first listing ($n files): $($rec.first_listing_s) s; JOB START -> FIRST LISTING: $($rec.job_start_to_first_listing_s) s"
if($n -eq 0){ $rec.error='listing empty'; New-Item "$W\vdstop","$W\drain" -ItemType File -Force | Out-Null; Finish $false; return }
# --- the workload ------------------------------------------------------------------------------------------------------------------------------
$sw.Restart(); $o = Invoke-Workload $cfg $W; $rec.workload_exit = ($o | Select -Last 1); T 'workload_s'
$o | Select -SkipLast 1 | Write-Host
$rec.workload_log_tail = (Get-Content "$W\workload-build.log" -Tail 3 -ea 0) -join ' | '
$rec.job_start_to_workload_done_s = Round2 $job.Elapsed.TotalSeconds
# --- detach / provider summary ------------------------------------------------------------------------------------------------------------------
New-Item "$W\vdstop" -ItemType File -Force | Out-Null; $vd.WaitForExit(30000) | Out-Null
if(-not $vd.HasExited){ New-Item "$W\drain" -ItemType File | Out-Null; $vd.WaitForExit(60000) | Out-Null }
$rec.child_mib_after = Round2 ((Get-Item "$W\child.vhd").Length/1MB)
if($prov){ New-Item "$W\cf.stop" -ItemType File | Out-Null; $prov.WaitForExit(30000) | Out-Null; $rec.provider_summary = (Select-String "$W\prov.txt" -Pattern 'SUMMARY').Line }
Finish ($rec.workload_exit -eq 0 -or $Workload -eq 'serilog')
