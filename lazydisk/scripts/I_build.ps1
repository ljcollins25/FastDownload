# I_build: build the serilog image (size MiB), keep ORIGINAL at root\parent.vhd path, make the EMPTY differencing child at that path,
# map metadata (Part E), pack the image into 1 MiB brotli chunks, upload data/idx/child/ranges as Actions artifacts. Run elevated, in a workflow job (artifacts are per run).
param([int]$Size=2048)
. "$PSScriptRoot\H_lib.ps1"
$ErrorActionPreference='Continue'; $ProgressPreference='SilentlyContinue'; $env:GIT_PAGER='cat'; $env:DOTNET_NOLOGO=1; $env:DOTNET_CLI_TELEMETRY_OPTOUT=1
$BD="D:\lazy\i\$Size"; Remove-Item $BD -Recurse -Force -ea 0; New-Item -ItemType Directory "$BD\root","$BD\store" | Out-Null
Start-Transcript "$BD\build.txt" -Force
$img="$BD\root\parent.vhd"; $CL='D:\cf\lazy\CfLazy.exe'; $sw=[Diagnostics.Stopwatch]::StartNew()
DP "create vdisk file=`"$img`" maximum=$Size type=fixed`nselect vdisk file=`"$img`"`nattach vdisk`ncreate partition primary offset=1024`nformat fs=ntfs unit=4096 quick label=LAZY`nassign letter=V" | Select -Last 2; "image created+formatted: $($sw.Elapsed.TotalSeconds) s"
$sw.Restart(); git clone -q --depth 1 https://github.com/serilog/serilog.git V:\serilog 2>&1 | select -Last 2; "clone $($sw.Elapsed.TotalSeconds) s"
Set-Location V:\serilog
$sw.Restart(); dotnet build -c Debug 2>&1 | Select -Last 4; "build $($sw.Elapsed.TotalSeconds) s"
$sw.Restart(); dotnet test --no-build -c Debug 2>&1 | Select -Last 4; "test $($sw.Elapsed.TotalSeconds) s"
dotnet build-server shutdown 2>&1 | Out-Null; Set-Location D:\
$buf=New-Object byte[] (1MB); $rng=New-Object Random 11; $fs=[IO.File]::Create('V:\bench.dat'); 1..512 | % { $rng.NextBytes($buf); $fs.Write($buf,0,$buf.Length) }; $fs.Close()
"files: " + (Get-ChildItem V:\serilog -Recurse -File -Force | measure).Count + "  used MB: " + [int]((Get-PSDrive V).Used/1MB)
$sw.Restart(); & D:\cf\em\ExtentMap.exe map V: 1048576 ([int64]$Size*1MB) "$BD\holes.txt"; "map $($sw.Elapsed.TotalSeconds) s"
Start-Sleep 2; Detach-Vhd $img
$len=(Get-Item $img).Length; "image file bytes $len"
DP "create vdisk file=`"$BD\child.vhd`" parent=`"$img`"" | Out-Null; "child bytes $((Get-Item "$BD\child.vhd").Length)"
& $CL ranges "$BD\holes.txt" $len "$BD\meta.ranges"
& $CL pack $img "$BD\data.bin" "$BD\data.idx" 1024 1
Move-Item $img "$BD\store\parent.vhd"; Copy-Item "$BD\child.vhd" "$BD\store\child.vhd"
foreach($x in @(@('data','data.bin'),@('idx','data.idx'),@('child','child.vhd'),@('ranges','meta.ranges'))){ & $CL up "cfl$Size-$($x[0])" "$BD\$($x[1])" }
Stop-Transcript
