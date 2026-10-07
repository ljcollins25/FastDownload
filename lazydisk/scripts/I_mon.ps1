# I_mon: resource sampler (every 5 s) so a runner disconnect can be correlated with disk/memory/process state. usage: I_mon.ps1 outfile stopfile
param($out,$stop)
"time,cFreeGB,dFreeGB,memFreeMB,commitPct,cpu,procs" | Set-Content $out
while(-not (Test-Path $stop)){
  $os=Get-CimInstance Win32_OperatingSystem; $c=(Get-PSDrive C).Free/1GB; $d=(Get-PSDrive D).Free/1GB
  $cpu=(Get-Counter '\Processor(_Total)\% Processor Time' -ea 0).CounterSamples.CookedValue
  $top=(Get-Process | ? {$_.Name -match 'CfLazy|VdAttach|dotnet|diskspd|git|MsBuild|testhost'} | % { "$($_.Name):$([int]($_.WorkingSet64/1MB))MB" }) -join ' '
  "{0:HH:mm:ss},{1:F1},{2:F1},{3},{4:F0},{5:F0},{6}" -f (Get-Date),$c,$d,[int]($os.FreePhysicalMemory/1024),(100*(1-$os.FreeVirtualMemory/$os.TotalVirtualMemorySize)),$cpu,$top | Add-Content $out
  Start-Sleep -Seconds 5 }
