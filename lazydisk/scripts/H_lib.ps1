# Shared helpers for part H (run elevated). Dot-source.
$H='D:\lazy\h'; New-Item -ItemType Directory -Force $H | Out-Null
function DP($script){ $f="$H\dp_$([guid]::NewGuid().ToString('N').Substring(0,6)).txt"; Set-Content $f $script -Encoding ascii; $o=& diskpart /s $f; Remove-Item $f; $o }
function New-Base($path,$mb,$letter){
  DP "create vdisk file=`"$path`" maximum=$mb type=fixed`nselect vdisk file=`"$path`"`nattach vdisk`ncreate partition primary`nformat fs=ntfs quick label=BASE`nassign letter=$letter" | Out-Null }
function Detach-Vhd($path){ DP "select vdisk file=`"$path`"`ndetach vdisk" | Out-Null }
