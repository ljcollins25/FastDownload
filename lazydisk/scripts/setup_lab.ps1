# One-step lab setup on a fresh elevated Windows runner: builds the tools into D:\cf (CfLazy -> lazy, VdAttach -> vd, ExtentMap -> em, VhdHeat -> heat),
# creates the working folders and sets a symbol cache path. (The original lab script also built CfLab and a base image; those are not part of this import.)
$ErrorActionPreference='Stop'; $R=(Resolve-Path "$PSScriptRoot\..").Path
New-Item -ItemType Directory -Force D:\cf,D:\lazy,D:\sym | Out-Null
foreach($x in @(@('CfLazy','lazy'),@('VdAttach','vd'),@('ExtentMap','em'),@('VhdHeat','heat'))){ dotnet publish "$R\tools\$($x[0])" -c Release -o "D:\cf\$($x[1])" 2>&1 | Select-String ' -> ' }
[Environment]::SetEnvironmentVariable('_NT_SYMBOL_PATH','srv*D:\sym*https://msdl.microsoft.com/download/symbols','Process')
Get-ChildItem D:\cf\lazy\CfLazy.exe,D:\cf\vd\VdAttach.exe,D:\cf\em\ExtentMap.exe,D:\cf\heat\VhdHeat.exe | select FullName,Length
