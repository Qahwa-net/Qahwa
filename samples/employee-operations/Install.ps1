param([string]$BaseUrl='http://localhost:5101',[string]$Token=$env:QAHWA_API_TOKEN)
$ErrorActionPreference='Stop'
if (!$Token) { $Token=Read-Host 'API token' }
$headers=@{Authorization="Bearer $Token"}
function Api($method,$route,$body) {
 $args=@{Uri="$BaseUrl/api/$route";Method=$method;Headers=$headers}
 if($null -ne $body){$args.ContentType='application/json';$args.Body=($body | ConvertTo-Json -Depth 60)}
 (Invoke-RestMethod @args).data
}
$root=$PSScriptRoot
foreach($role in 'Employee','Operations Manager'){
 try { $found=Api Get ("resource/Role/"+[uri]::EscapeDataString($role)) $null }
 catch { if([int]$_.Exception.Response.StatusCode -ne 404){throw}; $found=$null }
 if(!$found){Api Post 'resource/Role' @{role_name=$role} | Out-Null}
}
$bundle=Get-Content "$root/bundle.json" -Raw | ConvertFrom-Json
# Preserve existing master fields and naming, extending their metadata additively.
foreach($name in 'Supplier','Product'){
 $old=Api Get "designer/doctypes/$name" $null
 $item=$bundle.items | Where-Object {$_.kind -eq 'DocType' -and $_.doctype -eq $name}
 $definition=$old.definition
 foreach($field in $item.definition.fields){
  if($definition.fields.name -notcontains $field.name){
   if($name -eq 'Product' -and $field.name -in @('product_name','sku')){$field.required=$false;$field | Add-Member unique $false -Force}
   $definition.fields+= $field
  }
 }
 $definition.statusField='workflow_state';$definition.indicators=$item.definition.indicators
 $definition.trackChanges=$true
 $definition.permissions=@($definition.permissions | Where-Object {$_.role -notin @('Employee','Operations Manager','System Manager')})+@($item.definition.permissions)
 foreach($perm in $definition.permissions){$perm.submit=$false;$perm.cancel=$false}
 $definition.validations=@((@($definition.validations)+@($item.definition.validations)) | Sort-Object expression -Unique)
 if($name -eq 'Product'){$definition.titleField='product_name_english'}
 $saved=Api Put "designer/doctypes/$name" @{definition=$definition;modified=$old.modified}
 Write-Output "Updated $name"
}
$remaining=@($bundle.items | Where-Object {!($_.kind -eq 'DocType' -and $_.doctype -in @('Supplier','Product'))})
$bundle.items=$remaining
$result=Api Post 'bundle/import?dryRun=false' $bundle
$result.items | Format-Table docType,kind,ok,message -AutoSize
if($result.failed -gt 0){throw 'Some definitions failed to install.'}
$dashboardBody=Get-Content "$root/dashboard.json" -Raw | ConvertFrom-Json
$existing=@(Api Get 'dashboards' $null) | Where-Object title -eq $dashboardBody.title | Select-Object -First 1
if($existing){$dashboardBody | Add-Member modified $existing.modified;$dashboard=Api Put "dashboards/$($existing.id)" $dashboardBody}else{$dashboard=Api Post dashboards $dashboardBody}
$workspace=Get-Content "$root/workspace.json" -Raw | ConvertFrom-Json
$workspace.blocks[2].dashboardId=$dashboard.id
$existing=@(Api Get workspaces $null) | Where-Object title -eq $workspace.title | Select-Object -First 1
if($existing){$page=Api Put "workspaces/$($existing.id)" $workspace}else{$page=Api Post workspaces $workspace}
Write-Output "Dashboard: $($dashboard.id)"
$page | ConvertTo-Json -Depth 10



