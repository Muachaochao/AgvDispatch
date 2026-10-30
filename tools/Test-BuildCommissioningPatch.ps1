$ErrorActionPreference='Stop'
$fixtureRoot=Join-Path ([IO.Path]::GetTempPath()) ('agv-patch-build-test-'+[guid]::NewGuid().ToString('N'))
$bundle=Join-Path $fixtureRoot 'bundle'
$payload=Join-Path $fixtureRoot 'staging'
$output=Join-Path $fixtureRoot 'result'
$script:buildPatchChecks=0
function Check-Patch($Condition,[string]$Message){if(!$Condition){throw $Message};$script:buildPatchChecks++}
function Reject-Patch([scriptblock]$Action,[string]$Message){try{& $Action | Out-Null}catch{$script:buildPatchChecks++;return};throw $Message}
# Only file identity is exercised; renamed framework fixture assemblies are
# never executed. No installed bundle, runtime data or device is accessed.
$fixtureAssembly=[uri].Assembly.Location
$release=[Diagnostics.FileVersionInfo]::GetVersionInfo($fixtureAssembly).ProductVersion
foreach($component in @(@{Dir='Service';Name='Service'},@{Dir='Web/host';Name='WebHost'},@{Dir='WPF';Name='Wpf'})){
    $dir=Join-Path $bundle $component.Dir
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    Copy-Item -LiteralPath $fixtureAssembly -Destination (Join-Path $dir "AgvDispatch.$($component.Name).dll")
    Copy-Item -LiteralPath $fixtureAssembly -Destination (Join-Path $dir 'AgvDispatch.Application.dll')
}
New-Item -ItemType Directory -Path (Join-Path $bundle 'Web/wwwroot'),(Join-Path $payload 'Web/wwwroot/assets') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $bundle 'Web/wwwroot/index.html') -Value 'original page'
$manifest=@{schema='agv-commissioning-release';version=1;releaseId=$release;files=@(Get-ChildItem -LiteralPath $bundle -Recurse -File | ForEach-Object {
    @{path=[IO.Path]::GetRelativePath($bundle,$_.FullName);bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
})}
$manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $bundle 'release-manifest.json')
$baseHash=(Get-FileHash -LiteralPath (Join-Path $bundle 'release-manifest.json')).Hash
Set-Content -LiteralPath (Join-Path $payload 'Web/wwwroot/index.html') -Value 'patched page'
Set-Content -LiteralPath (Join-Path $payload 'Web/wwwroot/assets/added.js') -Value 'new asset'
$build=Join-Path $PSScriptRoot 'Build-CommissioningPatch.ps1'
& $build -BundleDirectory $bundle -PayloadDirectory $payload -OutputDirectory $output -PatchId fixture
$patch=Get-Content -LiteralPath (Join-Path $output 'patch.json') -Raw | ConvertFrom-Json
Check-Patch ($patch.files.Count -eq 3) 'Only two changed static files and the new manifest belong in this patch.'
Check-Patch (@(Get-ChildItem -LiteralPath $output -Recurse -Filter '*.dll').Count -eq 0) 'Unchanged runtime/assemblies must not be duplicated.'
Check-Patch ((Get-FileHash -LiteralPath (Join-Path $bundle 'release-manifest.json')).Hash -eq $baseHash) 'Building a patch must not change the baseline.'
Reject-Patch {& $build -BundleDirectory $bundle -PayloadDirectory $payload -OutputDirectory $output -PatchId fixture} 'Existing output must not be overwritten.'
Reject-Patch {& $build -BundleDirectory $bundle -PayloadDirectory $payload -OutputDirectory (Join-Path $bundle 'nested') -PatchId fixture} 'Output inside baseline must be rejected.'
$invalid=Join-Path $fixtureRoot 'invalid'
New-Item -ItemType Directory -Path (Join-Path $invalid 'Service') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $invalid 'Service/appsettings.json') -Value '{}'
Reject-Patch {& $build -BundleDirectory $bundle -PayloadDirectory $invalid -OutputDirectory (Join-Path $fixtureRoot 'invalid-result') -PatchId fixture} 'Configuration must never enter the patch.'
Check-Patch (!(Test-Path -LiteralPath (Join-Path $fixtureRoot 'invalid-result'))) 'Invalid patch must be rejected before creating payload.'
$shared=Join-Path $fixtureRoot 'shared'
New-Item -ItemType Directory -Path (Join-Path $shared 'Service') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $shared 'Service/AgvDispatch.Application.dll') -Value 'different shared identity'
Reject-Patch {& $build -BundleDirectory $bundle -PayloadDirectory $shared -OutputDirectory (Join-Path $fixtureRoot 'shared-result') -PatchId fixture} 'Updating only one shared assembly copy must be rejected.'
# Apply only to the disposable fixture; process lookup is simulated explicitly.
function Get-Process {param($Name)}
$manifestPayload=Join-Path $output 'payload/release-manifest.json'
$manifestText=Get-Content -LiteralPath $manifestPayload -Raw
$manifestEntry=$patch.files | Where-Object {$_.path -eq 'release-manifest.json'}
foreach($omitted in @('Service/AgvDispatch.Service.dll','Web/wwwroot/assets/added.js')){
    $broken=$manifestText | ConvertFrom-Json
    $broken.files=@($broken.files | Where-Object {$_.path.Replace('\','/') -ne $omitted})
    $broken | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $manifestPayload
    $manifestEntry.afterSha256=(Get-FileHash -LiteralPath $manifestPayload).Hash
    $patch | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'patch.json')
    Reject-Patch {& (Join-Path $PSScriptRoot 'Apply-CommissioningPatch.ps1') -PatchDirectory $output -BundleDirectory $bundle} "Manifest must not omit $omitted."
    Check-Patch ((Get-FileHash -LiteralPath (Join-Path $bundle 'release-manifest.json')).Hash -eq $baseHash) 'Manifest omission must be rejected before any baseline change.'
}
[IO.File]::WriteAllText($manifestPayload,$manifestText,[Text.UTF8Encoding]::new($false))
$manifestEntry.afterSha256=(Get-FileHash -LiteralPath $manifestPayload).Hash
$patch | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'patch.json')
$backup=Join-Path $fixtureRoot 'separate-backup-drive'
& (Join-Path $PSScriptRoot 'Apply-CommissioningPatch.ps1') -PatchDirectory $output -BundleDirectory $bundle -ValidateOnly
Check-Patch ((Get-FileHash -LiteralPath (Join-Path $bundle 'release-manifest.json')).Hash -eq $baseHash -and !(Test-Path -LiteralPath $backup)) 'Validation must not write the bundle or create backups.'
& (Join-Path $PSScriptRoot 'Apply-CommissioningPatch.ps1') -PatchDirectory $output -BundleDirectory $bundle -BackupDirectory $backup
& (Join-Path $PSScriptRoot 'Verify-CommissioningBundle.ps1') -BundleDirectory $bundle
$next=Get-Content -LiteralPath (Join-Path $bundle 'release-manifest.json') -Raw | ConvertFrom-Json
Check-Patch ($next.appliedPatches[0].patchId -eq 'fixture') 'Installed manifest must preserve the patch audit.'
Check-Patch (Test-Path -LiteralPath (Join-Path $backup 'program/Web/wwwroot/index.html')) 'Explicit backup location must contain original changed files.'
Write-Output "PASS $script:buildPatchChecks patch build/apply checks; fixture only: $fixtureRoot"
