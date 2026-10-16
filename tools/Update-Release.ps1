param([string]$PublishRoot = '')
$ErrorActionPreference = 'Stop'
$taskProjectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskReleaseRoot = [IO.Path]::GetFullPath((Join-Path $taskProjectRoot '../发布包'))
$taskDeployPairs = @(
    @{ Project = 'AgvDispatch.Service'; Target = '调度Windows服务' },
    @{ Project = 'AgvDispatch.Wpf'; Target = 'WPF上位机' }
)
foreach ($taskPair in $taskDeployPairs) {
    $taskSource = if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
        Join-Path $taskProjectRoot "$($taskPair.Project)/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish"
    } else {
        Join-Path ([IO.Path]::GetFullPath($PublishRoot)) $taskPair.Project
    }
    $taskDestination = Join-Path $taskReleaseRoot $taskPair.Target
    if (-not (Test-Path -LiteralPath (Join-Path $taskSource "$($taskPair.Project).dll"))) {
        throw "请先执行 dotnet publish $($taskPair.Project) -c Release"
    }
    $taskRuntimeConfig = Get-Content -LiteralPath (Join-Path $taskSource "$($taskPair.Project).runtimeconfig.json") -Raw | ConvertFrom-Json
    if ($taskRuntimeConfig.runtimeOptions.frameworks -or $taskRuntimeConfig.runtimeOptions.framework -or
        -not $taskRuntimeConfig.runtimeOptions.includedFrameworks) {
        throw "拒绝更新依赖系统.NET的发布包。请使用 --self-contained true 发布 $($taskPair.Project)。"
    }
    foreach ($taskRuntimeFile in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $taskSource $taskRuntimeFile))) { throw "缺少自带运行时文件：$taskRuntimeFile" }
    }
    if ($taskPair.Project -eq 'AgvDispatch.Wpf' -and -not (Test-Path -LiteralPath (Join-Path $taskSource 'PresentationFramework.dll'))) {
        throw '客户端发布包缺少WPF运行时。'
    }
    foreach ($taskProcess in (Get-Process 'AgvDispatch*' -ErrorAction SilentlyContinue)) {
        if ($taskProcess.Path -and $taskProcess.Path.StartsWith($taskDestination + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "发布包正在运行，请先关闭：$($taskProcess.Path)"
        }
    }
    New-Item -ItemType Directory -Path $taskDestination -Force | Out-Null
    # Update generated binaries only; preserve site configuration and all runtime data.
    foreach ($taskItem in (Get-ChildItem -LiteralPath $taskSource)) {
        if ($taskItem.Name -like 'appsettings*.json' -or $taskItem.Name -in @('data', 'logs', 'config', 'NLog.config')) { continue }
        Copy-Item -LiteralPath $taskItem.FullName -Destination $taskDestination -Recurse -Force
    }
    $taskSourceConfig = Join-Path $taskSource 'config'
    if (Test-Path -LiteralPath $taskSourceConfig) {
        foreach ($taskFile in (Get-ChildItem -LiteralPath $taskSourceConfig -File -Recurse)) {
            $taskRelative = [IO.Path]::GetRelativePath($taskSourceConfig, $taskFile.FullName)
            $taskTargetFile = Join-Path (Join-Path $taskDestination 'config') $taskRelative
            if (-not (Test-Path -LiteralPath $taskTargetFile)) {
                New-Item -ItemType Directory -Path (Split-Path $taskTargetFile) -Force | Out-Null
                Copy-Item -LiteralPath $taskFile.FullName -Destination $taskTargetFile
            }
        }
    }
    Write-Output "已更新 $taskDestination；保留 appsettings、已有点表、数据库、地图和日志。"
}
