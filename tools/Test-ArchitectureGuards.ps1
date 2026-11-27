param(
    [string]$Root = ''
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Root)) {
    $Root = Split-Path -Parent $PSScriptRoot
}
$rootPath = (Resolve-Path -LiteralPath $Root).Path
$productionRoots = Get-ChildItem -LiteralPath $rootPath -Directory |
    Where-Object { $_.Name -like 'AgvDispatch.*' } |
    Select-Object -ExpandProperty FullName

$sourceFiles = foreach ($productionRoot in $productionRoots) {
    Get-ChildItem -LiteralPath $productionRoot -Recurse -Filter '*.cs' -File |
        Where-Object {
            $_.FullName -notmatch '[\\/](bin|obj)[\\/]'
        }
}

$violations = @()
foreach ($file in $sourceFiles) {
    $rows = Select-String -LiteralPath $file.FullName -Pattern 'new\s+SqlSugar(Client|Scope)\s*\('
    if ($rows) {
        $relative = $file.FullName.Substring($rootPath.Length).TrimStart('\', '/')
        if ($relative -ne 'AgvDispatch.Infrastructure\Persistence\Database\DatabaseFactory.cs') {
            $violations += "$relative : low-level SqlSugar construction must go through DatabaseFactory"
        }
    }
}

if ($violations.Count -gt 0) {
    throw ("数据库连接架构守卫失败：`n" + ($violations -join "`n"))
}

Write-Host "PASS: production database clients are constructed only by DatabaseFactory."
