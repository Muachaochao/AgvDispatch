param(
    [Parameter(Mandatory=$true)][string]$PatchDirectory,
    [Parameter(Mandatory=$true)][string]$BundleDirectory
)
$ErrorActionPreference='Stop'
$patchRoot=[IO.Path]::GetFullPath($PatchDirectory)
$bundleRoot=[IO.Path]::GetFullPath($BundleDirectory).TrimEnd('\','/')
$patch=Get-Content -LiteralPath (Join-Path $patchRoot 'patch.json') -Raw | ConvertFrom-Json
if($patch.schema -ne 'agv-commissioning-patch-v1'){throw '不支持的补丁清单。'}
$manifest=Get-Content -LiteralPath (Join-Path $bundleRoot 'release-manifest.json') -Raw | ConvertFrom-Json
if($manifest.releaseId -ne $patch.baseReleaseId){throw '补丁基线版本不符。'}
foreach($process in Get-Process 'AgvDispatch*' -ErrorAction SilentlyContinue){
    if($process.Path -and $process.Path.StartsWith($bundleRoot+'\',[StringComparison]::OrdinalIgnoreCase)){
        throw "请先通过原停止入口关闭本发布目录：$($process.Path)"
    }
}
$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$files=@(foreach($file in $patch.files){
    if($file.path -notmatch '^(release-manifest\.json|(?:Service|Web/host|WPF)/AgvDispatch\.[\w.]+\.(?:dll|pdb|exe|deps\.json|runtimeconfig\.json)|Web/wwwroot/(?:index\.html|assets/[\w.-]+)|Docs/[^/\\]+\.md)$'){
        throw "补丁不允许修改配置或运行数据：$($file.path)"
    }
    $target=[IO.Path]::GetFullPath((Join-Path $bundleRoot $file.path))
    $source=[IO.Path]::GetFullPath((Join-Path (Join-Path $patchRoot 'payload') $file.path))
    if(!$target.StartsWith($bundleRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or !$seen.Add($target)){throw '补丁路径越界或重复。'}
    foreach($path in @($source,$target)){
        for($ancestor=Split-Path $path;$ancestor;$ancestor=Split-Path $ancestor){
            if((Get-Item -LiteralPath $ancestor -ErrorAction SilentlyContinue).Attributes -band [IO.FileAttributes]::ReparsePoint){throw '补丁路径不接受链接跳转。'}
        }
        if((Get-Item -LiteralPath $path -ErrorAction SilentlyContinue).Attributes -band [IO.FileAttributes]::ReparsePoint){throw '补丁文件不接受链接跳转。'}
    }
    if((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $file.afterSha256){throw "补丁内容损坏：$($file.path)"}
    $exists=Test-Path -LiteralPath $target
    if($file.beforeSha256){
        if(!$exists -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $file.beforeSha256){throw "原文件已变化，拒绝覆盖：$($file.path)"}
    }elseif($exists){throw "新增文件已存在，拒绝覆盖：$($file.path)"}
    [pscustomobject]@{Entry=$file;Source=$source;Target=$target;Existed=$exists}
})
# Validate the prospective manifest as one set, including Windows/portable path aliases,
# before writing anything. Rebuilding only file hashes must not create duplicate entries.
$nextManifestFile=$files | Where-Object {$_.Entry.path -eq 'release-manifest.json'}
if($nextManifestFile){
    $nextManifest=Get-Content -LiteralPath $nextManifestFile.Source -Raw | ConvertFrom-Json
    if($nextManifest.schema -ne 'agv-commissioning-release' -or $nextManifest.releaseId -ne $patch.baseReleaseId){throw '补丁发布清单身份不符。'}
    $manifestSeen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($entry in $nextManifest.files){
        $relative=$entry.path.Replace('\','/')
        $entryPath=[IO.Path]::GetFullPath((Join-Path $bundleRoot $relative))
        if(!$entryPath.StartsWith($bundleRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or !$manifestSeen.Add($entryPath)){throw '补丁发布清单路径越界或重复。'}
        $replacement=$files | Where-Object {$_.Target -eq $entryPath}
        $candidate=if($replacement){$replacement.Source}else{$entryPath}
        if((Get-Item -LiteralPath $candidate).Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $candidate).Hash -ne $entry.sha256){throw "补丁发布清单与文件不符：$relative"}
    }
}
$backupRoot=Join-Path (Split-Path $bundleRoot) ('升级备份/patch-'+$patch.patchId+'-'+(Get-Date -Format yyyyMMdd-HHmmss))
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $patchRoot 'patch.json') -Destination (Join-Path $backupRoot 'patch.json')
# Back up the shared daily data separately; it is never an update target.
$dailyData=Join-Path (Split-Path $bundleRoot) '运行数据'
if(Test-Path -LiteralPath $dailyData){Copy-Item -LiteralPath $dailyData -Destination (Join-Path $backupRoot '运行数据') -Recurse}
foreach($file in $files){
    if($file.Existed){
        $saved=Join-Path (Join-Path $backupRoot 'program') $file.Entry.path
        New-Item -ItemType Directory -Path (Split-Path $saved) -Force | Out-Null
        Copy-Item -LiteralPath $file.Target -Destination $saved
    }
}
$written=[Collections.Generic.List[object]]::new()
try{
    foreach($file in $files){
        New-Item -ItemType Directory -Path (Split-Path $file.Target) -Force | Out-Null
        $written.Add($file)
        Copy-Item -LiteralPath $file.Source -Destination $file.Target -Force
        if((Get-FileHash -LiteralPath $file.Target -Algorithm SHA256).Hash -ne $file.Entry.afterSha256){throw "写入校验失败：$($file.Entry.path)"}
    }
}catch{
    foreach($file in $written){
        if($file.Existed){Copy-Item -LiteralPath (Join-Path (Join-Path $backupRoot 'program') $file.Entry.path) -Destination $file.Target -Force}
        elseif(Test-Path -LiteralPath $file.Target){Remove-Item -LiteralPath $file.Target}
    }
    throw
}
Write-Output "PATCH OK: $($patch.patchId), $($files.Count) files. Backup: $backupRoot. 未启动服务或放行任务。"
