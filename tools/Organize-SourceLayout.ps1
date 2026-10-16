$ErrorActionPreference = 'Stop'
$taskWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskMoves = Get-Content -LiteralPath (Join-Path $taskWorkspace 'docs/architecture/source-layout-migration.json') -Raw | ConvertFrom-Json
$taskPending = @()
foreach ($taskMove in $taskMoves) {
    $taskSource = [IO.Path]::GetFullPath((Join-Path $taskWorkspace $taskMove.from))
    $taskTarget = [IO.Path]::GetFullPath((Join-Path $taskWorkspace $taskMove.to))
    if (-not $taskSource.StartsWith($taskWorkspace + '\', [StringComparison]::OrdinalIgnoreCase) -or -not $taskTarget.StartsWith($taskWorkspace + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '移动路径超出项目目录。' }
    if (Test-Path -LiteralPath $taskSource -PathType Leaf) {
        if (Test-Path -LiteralPath $taskTarget) { throw "目标已存在，禁止覆盖：$taskTarget" }
        if ((Get-Item -LiteralPath $taskSource).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw "不移动符号链接：$taskSource" }
        $taskPending += [pscustomobject]@{ Source=$taskSource; Target=$taskTarget }
    } elseif (-not (Test-Path -LiteralPath $taskTarget -PathType Leaf)) { throw "源文件和目标文件均不存在：$taskSource" }
}
foreach ($taskMove in $taskPending) {
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($taskMove.Target)) -Force | Out-Null
    Move-Item -LiteralPath $taskMove.Source -Destination $taskMove.Target
}
Write-Output "已按职责移动 $($taskPending.Count) 个源码文件；未覆盖文件，未修改命名空间或运行配置。"
