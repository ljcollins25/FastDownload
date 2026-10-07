# I_selftest: small end-to-end check of CfLazy serve (artifact source): placeholder content must equal the original. Run elevated.
param($t='D:\cf\t', $extra='')
Remove-Item "$t\root","$t\s.stop" -Recurse -Force -ea 0
$p=Start-Process D:\cf\lazy\CfLazy.exe -ArgumentList "serve $t\root --src art:cflazy-test --index $t\data.idx --name parent.vhd --stop $t\s.stop --log $t\s.log --sweep-delay-ms 250 $extra" -PassThru -RedirectStandardOutput "$t\s.out" -WindowStyle Hidden
for($i=0;$i -lt 100 -and -not (Select-String "$t\s.out" -Pattern READY -Quiet -ea 0);$i++){Start-Sleep -Milliseconds 100}
Get-Content "$t\s.out"
$sw=[Diagnostics.Stopwatch]::StartNew()
$h1=(Get-FileHash "$t\root\parent.vhd" -Algorithm MD5).Hash; $h0=(Get-FileHash "$t\raw.bin" -Algorithm MD5).Hash
"match=$($h1 -eq $h0) time=$($sw.Elapsed.TotalSeconds)"
New-Item "$t\s.stop" -ItemType File | Out-Null; $p.WaitForExit(15000)|Out-Null
Get-Content "$t\s.out" | Select -Last 2; Get-Content "$t\s.log" | Select -First 8
