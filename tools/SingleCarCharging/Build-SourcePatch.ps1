param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Za-z0-9_-]{1,80}$')][string]$PatchId
)
$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $sourceRoot '../..'))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputRoot) { throw '目标已存在，拒绝覆盖。' }
if ($outputRoot.StartsWith($repositoryRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $outputRoot -eq $repositoryRoot -or $repositoryRoot.StartsWith($outputRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw '补丁输出不得与源码目录互相包含。'
}
$archivePath = $outputRoot + '.zip'
if (Test-Path -LiteralPath $archivePath) { throw '同名压缩包已存在，拒绝覆盖。' }
$files = @(Get-ChildItem -LiteralPath $sourceRoot -File -Filter '*.py' | Sort-Object Name)
$files += @(Get-Item -LiteralPath (Join-Path $sourceRoot 'README.md'))
$files += @(Get-Item -LiteralPath (Join-Path $sourceRoot 'site-parameters-20261031.md'))
$files += @(Get-Item -LiteralPath (Join-Path $sourceRoot 'Build-SourcePatch.ps1'))
$files += @(Get-Item -LiteralPath (Join-Path $repositoryRoot 'docs/architecture/单车充电流程与恢复契约-20261101.md'))
$manifestFiles = foreach ($file in $files) {
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '不接受链接源文件。' }
    $relative = [IO.Path]::GetRelativePath($repositoryRoot, $file.FullName).Replace('\', '/')
    [pscustomobject]@{path=$relative;bytes=$file.Length;sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
}
$sourceCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw '无法读取源码提交。' }
$gitPaths = @($manifestFiles | ForEach-Object { $_.path })
$workingChanges = @(& git -C $repositoryRoot status --porcelain -- $gitPaths)
if ($LASTEXITCODE -ne 0) { throw '无法读取源码状态。' }
New-Item -ItemType Directory -Path $outputRoot | Out-Null
foreach ($entry in $manifestFiles) {
    $destination = Join-Path (Join-Path $outputRoot 'payload') $entry.path
    New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repositoryRoot $entry.path) -Destination $destination
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $entry.sha256) { throw '补丁复制哈希不一致。' }
}
[pscustomobject]@{
    schema='single-car-charging-source-patch-v1'
    patchId=$PatchId
    sourceCommit=$sourceCommit
    workingTreeContainsChanges=($workingChanges.Count -gt 0)
    createdAt=[DateTimeOffset]::Now.ToString('o')
    files=@($manifestFiles)
    application='Review and replace only matching source files in an offline checkout. No installer, restart, robot upload or hardware command is included.'
    exclusions=@('runtime','database','communication settings','vehicle pose evidence','site calibration','charging permission')
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $outputRoot 'source-patch.json') -Encoding utf8
Compress-Archive -LiteralPath (Join-Path $outputRoot 'payload'),(Join-Path $outputRoot 'source-patch.json') -DestinationPath $archivePath
$zip = Get-Item -LiteralPath $archivePath
[pscustomobject]@{patchId=$PatchId;files=$manifestFiles.Count;bytes=$zip.Length;archive=$archivePath;sha256=(Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash}
