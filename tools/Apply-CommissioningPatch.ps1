param(
    [Parameter(Mandatory=$true)][string]$PatchDirectory,
    [Parameter(Mandatory=$true)][string]$BundleDirectory,
    [string]$BackupDirectory='',
    [switch]$ValidateOnly
)
$ErrorActionPreference='Stop'
$patchRoot=[IO.Path]::GetFullPath($PatchDirectory)
$bundleRoot=[IO.Path]::GetFullPath($BundleDirectory).TrimEnd('\','/')
$patch=Get-Content -LiteralPath (Join-Path $patchRoot 'patch.json') -Raw | ConvertFrom-Json
if($patch.schema -ne 'agv-commissioning-patch-v1'){throw '不支持的补丁清单。'}
if($patch.patchId -notmatch '^[A-Za-z0-9_-]{1,80}$'){throw '补丁编号无效。'}
$manifest=Get-Content -LiteralPath (Join-Path $bundleRoot 'release-manifest.json') -Raw | ConvertFrom-Json
if($manifest.releaseId -ne $patch.baseReleaseId){throw '补丁基线版本不符。'}
if(!$ValidateOnly){foreach($process in Get-Process 'AgvDispatch*' -ErrorAction SilentlyContinue){
    if(!$process.Path){throw "无法核实进程 $($process.Id) 的程序目录；请先核对并通过原停止入口退出，不能跳过进程检查安装补丁。"}
    if($process.Path.StartsWith($bundleRoot+'\',[StringComparison]::OrdinalIgnoreCase)){
        throw "请先通过原停止入口关闭本发布目录：$($process.Path)"
    }
}}
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
if($manifest.schema -eq 'agv-commissioning-release' -and !$nextManifestFile){throw '正式发布补丁必须同步完整发布清单。'}
if($nextManifestFile){
    $nextManifest=Get-Content -LiteralPath $nextManifestFile.Source -Raw | ConvertFrom-Json
    if($nextManifest.schema -ne 'agv-commissioning-release' -or $nextManifest.version -ne 1 -or $nextManifest.releaseId -ne $patch.baseReleaseId){throw '补丁发布清单身份不符。'}
    $manifestSeen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($entry in $nextManifest.files){
        $relative=$entry.path.Replace('\','/')
        $entryPath=[IO.Path]::GetFullPath((Join-Path $bundleRoot $relative))
        if(!$entryPath.StartsWith($bundleRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or !$manifestSeen.Add($entryPath)){throw '补丁发布清单路径越界或重复。'}
        $replacement=$files | Where-Object {$_.Target -eq $entryPath}
        $candidate=if($replacement){$replacement.Source}else{$entryPath}
        if((Get-Item -LiteralPath $candidate).Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $candidate).Hash -ne $entry.sha256){throw "补丁发布清单与文件不符：$relative"}
    }
    foreach($entry in @($manifest.files)+@($patch.files | Where-Object {$_.path -ne 'release-manifest.json'})){
        if(!$entry){continue}
        $requiredPath=[IO.Path]::GetFullPath((Join-Path $bundleRoot $entry.path.Replace('\','/')))
        if(!$manifestSeen.Contains($requiredPath)){throw "补丁发布清单遗漏原文件或补丁文件：$($entry.path)"}
    }
}
if($ValidateOnly){
    Write-Output "PATCH VALID: $($patch.patchId), $($files.Count) files. 仅核对文件和清单；未检查停机状态，未备份、写入或启动。"
    return
}
$backupRoot=if($BackupDirectory){[IO.Path]::GetFullPath($BackupDirectory).TrimEnd('\','/')}else{Join-Path (Split-Path $bundleRoot) ('升级备份/patch-'+$patch.patchId+'-'+(Get-Date -Format yyyyMMdd-HHmmss))}
$dailyData=Join-Path (Split-Path $bundleRoot) '运行数据'
foreach($protectedRoot in @($bundleRoot,$patchRoot.TrimEnd('\','/'),$dailyData)){
    if($backupRoot -eq $protectedRoot -or $backupRoot.StartsWith($protectedRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or $protectedRoot.StartsWith($backupRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw '备份位置不能包含或位于程序、补丁、运行数据目录内。'}
}
if(Test-Path -LiteralPath $backupRoot){throw '备份目标已存在，拒绝覆盖。'}
for($ancestor=$backupRoot;$ancestor;$ancestor=Split-Path $ancestor){
    if((Get-Item -LiteralPath $ancestor -ErrorAction SilentlyContinue).Attributes -band [IO.FileAttributes]::ReparsePoint){throw '备份位置不接受链接跳转。'}
}
New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $patchRoot 'patch.json') -Destination (Join-Path $backupRoot 'patch.json')
# Back up the shared daily data separately; it is never an update target.
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
