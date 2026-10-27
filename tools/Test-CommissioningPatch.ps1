$ErrorActionPreference='Stop'
$fixtureRoot=Join-Path ([IO.Path]::GetTempPath()) ('agv-patch-test-'+[guid]::NewGuid().ToString('N'))
$bundle=Join-Path $fixtureRoot 'bundle'
$patchDir=Join-Path $fixtureRoot 'patch'
New-Item -ItemType Directory -Path (Join-Path $bundle 'Web/wwwroot/assets'),(Join-Path $patchDir 'payload/Web/wwwroot/assets'),(Join-Path $fixtureRoot '运行数据') -Force | Out-Null
@{releaseId='test-base'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $bundle 'release-manifest.json')
Set-Content -LiteralPath (Join-Path $fixtureRoot '运行数据/sentinel.txt') -Value 'keep original data'
Set-Content -LiteralPath (Join-Path $bundle 'Web/wwwroot/index.html') -Value 'old page'
Set-Content -LiteralPath (Join-Path $patchDir 'payload/Web/wwwroot/index.html') -Value 'patched page'
Set-Content -LiteralPath (Join-Path $patchDir 'payload/Web/wwwroot/assets/new.js') -Value 'new script'
$hashBefore=(Get-FileHash -LiteralPath (Join-Path $bundle 'Web/wwwroot/index.html')).Hash
$patch=@{schema='agv-commissioning-patch-v1';baseReleaseId='test-base';patchId='fixture';files=@(
    @{path='Web/wwwroot/index.html';beforeSha256=$hashBefore;afterSha256=(Get-FileHash -LiteralPath (Join-Path $patchDir 'payload/Web/wwwroot/index.html')).Hash},
    @{path='Web/wwwroot/assets/new.js';beforeSha256=$null;afterSha256='tampered'}
)}
$patch | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $patchDir 'patch.json')
$rejected=$false
try{& (Join-Path $PSScriptRoot 'Apply-CommissioningPatch.ps1') -PatchDirectory $patchDir -BundleDirectory $bundle}catch{$rejected=$true}
if(!$rejected -or (Get-FileHash -LiteralPath (Join-Path $bundle 'Web/wwwroot/index.html')).Hash -ne $hashBefore){throw '损坏补丁必须在任何写入前被拒绝。'}
$patch.files[1].afterSha256=(Get-FileHash -LiteralPath (Join-Path $patchDir 'payload/Web/wwwroot/assets/new.js')).Hash
$patch | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $patchDir 'patch.json')
& (Join-Path $PSScriptRoot 'Apply-CommissioningPatch.ps1') -PatchDirectory $patchDir -BundleDirectory $bundle
if((Get-FileHash -LiteralPath (Join-Path $bundle 'Web/wwwroot/index.html')).Hash -ne $patch.files[0].afterSha256){throw '页面更新失败。'}
if(!(Test-Path -LiteralPath (Join-Path $bundle 'Web/wwwroot/assets/new.js'))){throw '新增资源缺失。'}
$saved=@(Get-ChildItem -LiteralPath (Join-Path $fixtureRoot '升级备份') -Directory)
if($saved.Count -ne 1 -or (Get-FileHash -LiteralPath (Join-Path $saved[0].FullName 'program/Web/wwwroot/index.html')).Hash -ne $hashBefore){throw '原程序备份错误。'}
if((Get-Content -LiteralPath (Join-Path $saved[0].FullName '运行数据/sentinel.txt') -Raw).Trim() -ne 'keep original data'){throw '运行数据备份错误。'}
$rejected=$false
try{& (Join-Path $PSScriptRoot 'Apply-CommissioningPatch.ps1') -PatchDirectory $patchDir -BundleDirectory $bundle}catch{$rejected=$true}
if(!$rejected){throw '重复安装或基线变化必须拒绝。'}
$originalManifestHash=(Get-FileHash -LiteralPath (Join-Path $bundle 'release-manifest.json')).Hash
$entry=@{path='Web/wwwroot/index.html';bytes=(Get-Item -LiteralPath (Join-Path $bundle 'Web/wwwroot/index.html')).Length;sha256=$patch.files[0].afterSha256}
@{schema='agv-commissioning-release';releaseId='test-base';files=@($entry,@{path='Web\wwwroot\index.html';bytes=$entry.bytes;sha256=$entry.sha256})} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $patchDir 'payload/release-manifest.json')
$patch.files=@(@{path='release-manifest.json';beforeSha256=$originalManifestHash;afterSha256=(Get-FileHash -LiteralPath (Join-Path $patchDir 'payload/release-manifest.json')).Hash})
$patch | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $patchDir 'patch.json')
$rejected=$false
try{& (Join-Path $PSScriptRoot 'Apply-CommissioningPatch.ps1') -PatchDirectory $patchDir -BundleDirectory $bundle}catch{if($_.Exception.Message.Contains('重复')){$rejected=$true}else{throw}}
if(!$rejected -or (Get-FileHash -LiteralPath (Join-Path $bundle 'release-manifest.json')).Hash -ne $originalManifestHash){throw '不同路径分隔符的同文件重复登记必须在写入前拒绝。'}
Write-Output 'PASS: 损坏补丁写入前拒绝、差量更新、新增资源、原程序与数据备份、重复安装拒绝、发布清单路径别名重复拒绝；未连接现场。'
