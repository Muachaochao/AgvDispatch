[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
foreach ($project in @('AgvDispatch.Service', 'AgvDispatch.Wpf')) {
    $examplePath = Join-Path $projectRoot "$project/appsettings.example.json"
    $localPath = Join-Path $projectRoot "$project/appsettings.json"
    if (Test-Path -LiteralPath $localPath) {
        Write-Host "$project : 已有本地配置，保留不覆盖。"
    } else {
        Copy-Item -LiteralPath $examplePath -Destination $localPath
        Write-Host "$project : 已创建安全的本地示例配置；现场控制默认关闭。"
    }
}
