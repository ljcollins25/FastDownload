# I_run: one measurement job. Mode native = child over a LOCAL full copy of the parent; lazy = child over a CfLazy placeholder parent served from the Actions artifact.
# usage: I_run.ps1 -Size 2048 -Label t1 -Mode lazy -Prov "sweep-delay-ms=250 sweep-promote-ms=2000 ..."   (Prov: key=val pairs for 'CfLazy serve')
param([int]$Size=2048,[string]$Label='r',[string]$Mode='lazy',[string]$Prov='')
. "$PSScriptRoot\H_lib.ps1"
$ErrorActionPreference='Continue'; $ProgressPreference='SilentlyContinue'; $env:GIT_PAGER='cat'; $env:DOTNET_NOLOGO=1; $env:DOTNET_CLI_TELEMETRY_OPTOUT=1
$DS='D:\lazy\diskspd\amd64\diskspd.exe'; $CL='D:\cf\lazy\CfLazy.exe'
$BD="D:\lazy\i\$Size"; $R="$BD\root"; $W="$BD\run_${Label}_$(Get-Date -f HHmmss)"; $data="$PSScriptRoot\..\data"
$provArgs=@(); foreach($t in ($Prov -split ' ' | ? {$_})){ $k,$v=$t -split '=',2; $provArgs+=("--$k"); if($v){$provArgs+=$v} }  # NB: must not be named $prov: PowerShell names are case-insensitive and it clobbered the -Prov parameter
Remove-Item $R -Recurse -Force -ea 0; New-Item -ItemType Directory $R,$W | Out-Null; Start-Transcript "$W\run.txt" -Force
$mon=Start-Process pwsh -ArgumentList "-NoProfile","-File","$PSScriptRoot\I_mon.ps1","$W\mon.csv","$W\mon.stop" -PassThru -WindowStyle Hidden
function Bench($file,$a,$secs=6){ $txt=& $DS -Sh -L -t1 "-d$secs" -W1 -C0 $a.Split(' ') $file | Out-String; $tot=$false; foreach($l in $txt -split "\r?\n"){ if($l -match 'Total IO'){$tot=$true}; $t=$l.Trim(); if($tot -and $t -match '^total:\s*(\d+)\s*\|\s*(\d+)\s*\|\s*([\d.]+)\s*\|\s*([\d.]+)\s*\|\s*([\d.]+)'){ return "IOPS=$($matches[4]) MiB/s=$($matches[3]) avg_lat_ms=$($matches[5])" } }; "parse failed" }
function Step($name,[scriptblock]$sb){ $s=[Diagnostics.Stopwatch]::StartNew(); & $sb | Out-Null; "[$Label] $name : $([math]::Round($s.Elapsed.TotalSeconds,2)) s" }
$job=[Diagnostics.Stopwatch]::StartNew()
"[$Label] mode=$Mode size=$Size prov=$Prov"
if($Mode -eq 'lazy'){
  $s=[Diagnostics.Stopwatch]::StartNew()
  foreach($x in @(@('child','child.vhd'),@('ranges','meta.ranges'),@('idx','data.idx'))){ & $CL down "cfl$Size-$($x[0])" "$W\$($x[1])" | Out-Null }
  "[$Label] download child+ranges+index: $([math]::Round($s.Elapsed.TotalSeconds,2)) s"
  $s.Restart()
  $p=Start-Process $CL -ArgumentList (@('serve',$R,'--src',"art:cfl$Size-data",'--index',"$W\data.idx",'--name','parent.vhd','--prehydrate',"$W\meta.ranges",'--stop',"$W\cf.stop",'--drain',"$W\drain",'--log',"$W\cf.log")+$provArgs) -PassThru -RedirectStandardOutput "$W\prov.txt" -WindowStyle Hidden
  for($i=0;$i -lt 1800 -and -not (Select-String "$W\prov.txt" -Pattern 'READY' -Quiet -ea 0) -and -not $p.HasExited;$i++){Start-Sleep -Milliseconds 100}
  "[$Label] provider + metadata pre-hydration: $([math]::Round($s.Elapsed.TotalSeconds,2)) s :: " + ((Select-String "$W\prov.txt" -Pattern 'PREHYDRATE').Line)
} else {
  $s=[Diagnostics.Stopwatch]::StartNew(); Copy-Item "$BD\store\parent.vhd" "$R\parent.vhd"; Copy-Item "$BD\store\child.vhd" "$W\child.vhd"
  "[$Label] native copy parent+child: $([math]::Round($s.Elapsed.TotalSeconds,2)) s"
}
if($Mode -eq 'lazy'){ $c=(Get-Item "$W\child.vhd").FullName }
$s=[Diagnostics.Stopwatch]::StartNew()
$q=Start-Process D:\cf\vd\VdAttach.exe -ArgumentList "$W\child.vhd",'0','0','--hold',"$W\vdstop" -PassThru -RedirectStandardOutput "$W\vd.txt" -NoNewWindow
for($i=0;$i -lt 2400 -and -not (Select-String "$W\vd.txt" -Pattern 'ATTACHED|AttachVirtualDisk rc=[1-9]|OpenVirtualDisk rc=[1-9]' -Quiet -ea 0);$i++){Start-Sleep -Milliseconds 100}
if(-not (Select-String "$W\vd.txt" -Pattern 'ATTACHED' -Quiet)){ "[$Label] ATTACH FAILED/TIMEOUT: " + ((Get-Content "$W\vd.txt") -join ' '); New-Item "$W\drain" -ItemType File | Out-Null; New-Item "$W\mon.stop" -ItemType File | Out-Null; Stop-Transcript; return }
"[$Label] attach: $([math]::Round($s.Elapsed.TotalSeconds,2)) s"
$d=Get-Disk | ? Location -like '*child.vhd'; if($d.IsOffline){ Set-Disk $d.Number -IsOffline $false }; if($d.IsReadOnly){ Set-Disk $d.Number -IsReadOnly $false }
$pt=Get-Partition -DiskNumber $d.Number | ? Type -eq 'IFS' | select -First 1
if($pt.DriveLetter){ $L=$pt.DriveLetter } else { Add-PartitionAccessPath -DiskNumber $d.Number -PartitionNumber $pt.PartitionNumber -AccessPath 'X:\'; $L='X' }
$lt=[Diagnostics.Stopwatch]::StartNew(); $n=(Get-ChildItem "${L}:\serilog" -Recurse -Force -File | measure).Count
"[$Label] first listing ($n files): $([math]::Round($lt.Elapsed.TotalSeconds,2)) s;  JOB START -> FIRST LISTING: $([math]::Round($job.Elapsed.TotalSeconds,2)) s"
Set-Location "${L}:\serilog"
Step 'git status' { git --no-pager status --short }
Step 'dotnet build --no-incremental' { dotnet build -c Debug --no-incremental 2>&1 | Select -Last 3 }
Step 'dotnet test' { dotnet test --no-build -c Debug 2>&1 | Select -Last 3 }
dotnet build-server shutdown 2>&1 | Out-Null; Set-Location D:\
"[$Label] COLD random 4K QD1: " + (Bench "${L}:\bench.dat" '-b4K -o1 -r -w0')
"[$Label] COLD random 4K QD8: " + (Bench "${L}:\bench.dat" '-b4K -o8 -r -w0')
New-Item "$W\vdstop" -ItemType File -Force | Out-Null; $q.WaitForExit(30000)|Out-Null; "[$Label] detach exited=$($q.HasExited)"
if(-not $q.HasExited){ New-Item "$W\drain" -ItemType File | Out-Null; $q.WaitForExit(30000)|Out-Null; "[$Label] detach after drain exited=$($q.HasExited)" }
"[$Label] total job time: $([math]::Round($job.Elapsed.TotalSeconds,1)) s; child size after job: $([math]::Round((Get-Item "$W\child.vhd").Length/1MB,1)) MiB"
if($Mode -eq 'lazy'){ New-Item "$W\cf.stop" -ItemType File | Out-Null; $p.WaitForExit(20000)|Out-Null; "[$Label] provider exited=$($p.HasExited)"; (Select-String "$W\prov.txt" -Pattern 'SUMMARY').Line }
New-Item "$W\mon.stop" -ItemType File | Out-Null; Stop-Transcript
New-Item -ItemType Directory $data -Force | Out-Null; $tag="I_${Size}_$Label"
Copy-Item "$W\run.txt" "$data\$tag.txt"; Copy-Item "$W\mon.csv" "$data\$tag.mon.csv" -ea 0; if(Test-Path "$W\cf.log"){ Copy-Item "$W\cf.log" "$data\$tag.cf.log" }
