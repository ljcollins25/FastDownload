# I_setup: build helper tools into D:\cf and fetch diskspd (elevated). Fresh runner => run first.
$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'
$repo=(Resolve-Path "$PSScriptRoot\..").Path
foreach($x in @(@('VdAttach','vd'),@('ExtentMap','em'),@('CfLazy','lazy'))){ dotnet publish "$repo\tools\$($x[0])" -c Release -o "D:\cf\$($x[1])" 2>&1 | Select-String ' -> ' }
if(-not (Test-Path D:\lazy\diskspd\amd64\diskspd.exe)){
  New-Item -ItemType Directory D:\lazy -Force | Out-Null
  Invoke-WebRequest https://github.com/microsoft/diskspd/releases/latest/download/DiskSpd.zip -OutFile D:\lazy\diskspd.zip
  Expand-Archive D:\lazy\diskspd.zip D:\lazy\diskspd -Force }
Test-Path D:\lazy\diskspd\amd64\diskspd.exe
