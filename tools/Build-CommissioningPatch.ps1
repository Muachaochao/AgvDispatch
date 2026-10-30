param(
    [Parameter(Mandatory=$true)][string]$BundleDirectory,
    [Parameter(Mandatory=$true)][string]$PayloadDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Za-z0-9_-]{1,80}$')][string]$PatchId
)
$ErrorActionPreference='Stop'
$baseRoot=[IO.Path]::GetFullPath($BundleDirectory).TrimEnd('\','/')
$payloadRoot=[IO.Path]::GetFullPath($PayloadDirectory).TrimEnd('\','/')
$outputRoot=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\','/')
if(Test-Path -LiteralPath $outputRoot){throw '补丁目标已存在；拒绝覆盖。'}
foreach($left in @($baseRoot,$payloadRoot,$outputRoot)){
    foreach($right in @($baseRoot,$payloadRoot,$outputRoot)){
        if($left -ne $right -and $left.StartsWith($right+'\',[StringComparison]::OrdinalIgnoreCase)){throw '基线、暂存与补丁目录不得互相包含。'}
    }
    for($ancestor=$left;$ancestor;$ancestor=Split-Path $ancestor){
        if((Get-Item -LiteralPath $ancestor -ErrorAction SilentlyContinue).Attributes -band [IO.FileAttributes]::ReparsePoint){throw '不接受链接目录。'}
    }
}
if($baseRoot -eq $payloadRoot -or $baseRoot -eq $outputRoot -or $payloadRoot -eq $outputRoot){throw '基线、暂存与补丁必须是不同目录。'}
& (Join-Path $PSScriptRoot 'Verify-CommissioningBundle.ps1') -BundleDirectory $baseRoot -AllowRuntimeData
$manifest=Get-Content -LiteralPath (Join-Path $baseRoot 'release-manifest.json') -Raw | ConvertFrom-Json
$entries=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($entry in $manifest.files){$entries.Add($entry.path.Replace('\','/'),$entry)}
$changes=[Collections.Generic.List[object]]::new()
foreach($source in Get-ChildItem -LiteralPath $payloadRoot -Recurse -Force){
    if($source.Attributes -band [IO.FileAttributes]::ReparsePoint){throw '暂存文件不接受链接跳转。'}
    if($source.PSIsContainer){continue}
    $relative=[IO.Path]::GetRelativePath($payloadRoot,$source.FullName).Replace('\','/')
    # Runtime, dependencies, launchers and configuration need a separately reviewed
    # release. This path only packages existing managed assemblies and UI/docs.
    if($relative -notmatch '^(?:(?:Service|Web/host|WPF)/AgvDispatch\.[\w.]+\.(?:dll|pdb)|Web/wwwroot/(?:index\.html|assets/[\w.-]+)|Docs/[^/\\]+\.md)$'){
        throw "补丁禁止包含运行时、依赖描述、配置或运行数据：$relative"
    }
    if($relative -match '\.(dll|pdb)$' -and !$entries.ContainsKey($relative)){throw "不能通过补丁引入新程序集或依赖：$relative"}
    $after=(Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash
    $before=if($entries.ContainsKey($relative)){$entries[$relative].sha256}else{$null}
    if($after -eq $before){continue}
    $target=Join-Path $baseRoot $relative
    if(!$before -and (Test-Path -LiteralPath $target)){throw "目标存在未登记文件，拒绝覆盖：$relative"}
    if($relative -match '/AgvDispatch\.(?:Service|Wpf|WebHost)\.dll$' -and
        [Diagnostics.FileVersionInfo]::GetVersionInfo($source.FullName).ProductVersion -ne $manifest.releaseId){throw "主程序集必须沿用基线版本：$relative"}
    $changes.Add([pscustomobject]@{path=$relative;beforeSha256=$before;afterSha256=$after;bytes=$source.Length;source=$source.FullName})
}
if(!$changes.Count){throw '没有发生变化的允许文件，无需生成补丁。'}
# A shared assembly must remain identical in every installed component.
foreach($assembly in @($changes | Where-Object {$_.path.EndsWith('.dll',[StringComparison]::OrdinalIgnoreCase)} | ForEach-Object {[IO.Path]::GetFileName($_.path)} | Select-Object -Unique)){
    $hashes=@(foreach($entry in $manifest.files | Where-Object {[IO.Path]::GetFileName($_.path) -eq $assembly}){
        $change=$changes | Where-Object {$_.path -eq $entry.path.Replace('\','/')}
        if($change){$change.afterSha256}else{$entry.sha256}
    })
    if(@($hashes | Select-Object -Unique).Count -ne 1){throw "共享程序集必须同步更新所有组件副本：$assembly"}
}
foreach($change in $changes){
    $entry=[pscustomobject]@{path=$change.path;bytes=$change.bytes;sha256=$change.afterSha256}
    if($entries.ContainsKey($change.path)){
        $entries[$change.path].bytes=$change.bytes
        $entries[$change.path].sha256=$change.afterSha256
    }else{$manifest.files=@($manifest.files)+@($entry)}
}
$history=@($manifest.appliedPatches | Where-Object {$_})
if($history | Where-Object {$_.patchId -eq $PatchId}){throw '该补丁编号已包含在基线中。'}
$history+=@([pscustomobject]@{patchId=$PatchId;createdAt=[DateTimeOffset]::Now.ToString('o');changedFiles=$changes.Count})
$manifest | Add-Member -NotePropertyName appliedPatches -NotePropertyValue $history -Force
New-Item -ItemType Directory -Path (Join-Path $outputRoot 'payload') -Force | Out-Null
foreach($change in $changes){
    $destination=Join-Path (Join-Path $outputRoot 'payload') $change.path
    New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
    Copy-Item -LiteralPath $change.source -Destination $destination
}
$nextManifest=Join-Path $outputRoot 'payload/release-manifest.json'
$manifest | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $nextManifest -Encoding utf8
$patchFiles=@($changes | Select-Object path,beforeSha256,afterSha256)
$patchFiles+=@([pscustomobject]@{path='release-manifest.json';beforeSha256=(Get-FileHash -LiteralPath (Join-Path $baseRoot 'release-manifest.json')).Hash;afterSha256=(Get-FileHash -LiteralPath $nextManifest).Hash})
@{schema='agv-commissioning-patch-v1';baseReleaseId=$manifest.releaseId;patchId=$PatchId;files=$patchFiles} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot 'patch.json') -Encoding utf8
$bytes=(Get-ChildItem -LiteralPath $outputRoot -Recurse -File | Measure-Object Length -Sum).Sum
Write-Output "PATCH BUILT: $PatchId, $($changes.Count) changed files, $bytes bytes. 未复制完整运行时，未更改现场程序或数据。"
