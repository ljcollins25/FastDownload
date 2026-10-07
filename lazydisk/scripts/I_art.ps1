# Actions artifact v4 helpers: upload raw blob (block blob, 64MiB blocks), get signed URL for Range reads.
function Art-Ctx {
  $t=$env:ACTIONS_RUNTIME_TOKEN
  $p=$t.Split('.')[1].Replace('-','+').Replace('_','/'); while($p.Length%4){$p+='='}
  $j=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($p))|ConvertFrom-Json
  $m=[regex]::Match($j.scp,'Actions.Results:([0-9a-f-]+):([0-9a-f-]+)')
  @{run=$m.Groups[1].Value;job=$m.Groups[2].Value;h=@{Authorization="Bearer $t";'Content-Type'='application/json'};base=$env:ACTIONS_RESULTS_URL.TrimEnd('/')+'/twirp/github.actions.results.api.v1.ArtifactService'}
}
function Art-Call($c,$m,$o){ Invoke-RestMethod -Method Post -Uri "$($c.base)/$m" -Headers $c.h -Body ($o|ConvertTo-Json -Depth 5) }
function Art-Up($name,$file){
  $c=Art-Ctx
  $r=Art-Call $c CreateArtifact @{workflow_run_backend_id=$c.run;workflow_job_run_backend_id=$c.job;name=$name;version=4}
  $u=$r.signed_upload_url
  $fs=[IO.File]::OpenRead($file); $bs=64MB; $buf=New-Object byte[] $bs; $ids=@(); $i=0
  while(($n=$fs.Read($buf,0,$bs)) -gt 0){
    $id=[Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(('{0:D8}' -f $i)))
    Invoke-RestMethod -Method Put -Uri "$u&comp=block&blockid=$([uri]::EscapeDataString($id))" -Body ([byte[]]$buf[0..($n-1)]) -ContentType 'application/octet-stream' | Out-Null
    $ids+=$id; $i++
  }
  $len=$fs.Length; $fs.Close()
  $xml='<?xml version="1.0" encoding="utf-8"?><BlockList>'+(($ids|%{"<Latest>$_</Latest>"}) -join '')+'</BlockList>'
  Invoke-RestMethod -Method Put -Uri "$u&comp=blocklist" -Body $xml -ContentType 'application/xml' | Out-Null
  Art-Call $c FinalizeArtifact @{workflow_run_backend_id=$c.run;workflow_job_run_backend_id=$c.job;name=$name;size=$len}
}
function Art-Url($name){
  $c=Art-Ctx
  (Art-Call $c GetSignedArtifactURL @{workflow_run_backend_id=$c.run;workflow_job_run_backend_id=$c.job;name=$name}).signed_url
}
