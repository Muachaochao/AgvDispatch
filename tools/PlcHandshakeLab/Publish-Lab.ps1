param([string]$DefaultLabRoot = '')
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Push-Location $workspace
try {
    $commit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify source revision.' }
    $documents = @('28阶段统一执行与联调-20261021.md','PLC测试工具优化与28阶段回归-20261022.md','PLC测试工具会话回看与证据导出-20261022.md','PLC测试工具界面优化-20261024.md','循环模拟箱体原动作反馈-20261025.md','双车双工位竞争交接测试-20261027.md')
    # This self-contained project has no project references. Require its complete source,
    # verification source, packaged docs and inherited build configuration to match HEAD;
    # unrelated main-application edits are not inputs to this independent carrying package.
    $sourceScope = @('tools/PlcHandshakeLab','tools/PlcHandshakeLabSmokeTest','AgvDispatch.Connectors.Plc/Signals/PlcSignalCatalog.cs',
        'global.json','NuGet.Config','nuget.config','Directory.Build.props','Directory.Build.targets','Directory.Packages.props',
        'tools/Directory.Build.props','tools/Directory.Build.targets','tools/Directory.Packages.props') + @($documents | ForEach-Object { 'docs/architecture/' + $_ })
    $sourceChanges = & git status --porcelain --untracked-files=all -- @sourceScope
    if ($LASTEXITCODE -ne 0 -or $sourceChanges) { throw 'Commit validated tool source, verification, build configuration and packaged docs before publishing.' }
    $defaultReady = $null
    if ($DefaultLabRoot) {
        $labPath = [IO.Path]::GetFullPath((Join-Path $workspace $DefaultLabRoot))
        $artifactsPrefix = [IO.Path]::GetFullPath((Join-Path $workspace 'artifacts')) + [IO.Path]::DirectorySeparatorChar
        if (!$labPath.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Default laboratory must be inside workspace artifacts.' }
        $labInfo = Get-Content -LiteralPath (Join-Path $labPath 'lab.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($labInfo.simulationOnly -ne $true -or $labInfo.plcContractVersion -ne 'plc-28-stage-v2') { throw 'Choose a new synthetic 28-stage laboratory.' }
        $defaultReady = Join-Path $labPath 'ready.json'
        if (!(Test-Path -LiteralPath $defaultReady)) { throw 'Start the selected independent laboratory first.' }
    }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $version = ([xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'PlcHandshakeLab.csproj') -Raw)).Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid carrying package version.' }
    $name = "PlcHandshakeLab-$version-$stamp-$($commit.Substring(0,8))"
    $package = Join-Path $workspace "artifacts/$name"
    if (Test-Path -LiteralPath $package) { throw 'Release path already exists.' }
    & dotnet publish tools/PlcHandshakeLab/PlcHandshakeLab.csproj -c Release -r win-x64 --self-contained true -o $package
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $package '使用说明.md')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'FIELD-GUIDE.md') -Destination (Join-Path $package '厂家联调操作说明.md')
    foreach ($document in $documents) {
        Copy-Item -LiteralPath (Join-Path $workspace ('docs/architecture/' + $document)) -Destination (Join-Path $package $document)
    }
    $utf8 = [Text.UTF8Encoding]::new($false)
    [IO.File]::WriteAllText((Join-Path $package '打开人工联调台.cmd'), "@echo off`r`nstart `"`" `"%~dp0PlcHandshakeLab.exe`" --network`r`n", $utf8)
    [IO.File]::WriteAllText((Join-Path $package '打开离线演示.cmd'), "@echo off`r`nstart `"`" `"%~dp0PlcHandshakeLab.exe`"`r`n", $utf8)
    $qa = Join-Path $workspace "artifacts/$name-ui.png"
    $process = Start-Process -FilePath (Join-Path $package 'PlcHandshakeLab.exe') -ArgumentList @('--network-smoke-test', ('"' + $qa + '"')) -WindowStyle Hidden -PassThru -Wait
    if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath ($qa + '.result.txt')) -or
        !(Get-Content -LiteralPath ($qa + '.result.txt') -Raw -Encoding UTF8).StartsWith('PASS:')) { throw 'Published executable UI smoke check failed.' }
    $files = @(Get-ChildItem -LiteralPath $package -File -Recurse | ForEach-Object {
        $full = [IO.Path]::GetFullPath($_.FullName)
        if (!$full.StartsWith($package + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Published file escaped package directory.' }
        [ordered]@{ path=$full.Substring($package.Length + 1); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    $manifest = [ordered]@{ version=$version; sourceCommit=$commit; publishedAt=[DateTimeOffset]::Now.ToString('O');
        contract='plc-28-stage-v2'; runtime='win-x64 self-contained'; sourceScope=$sourceScope; uiSmoke='PASS'; files=$files }
    [IO.File]::WriteAllText((Join-Path $package 'release-manifest.json'), ($manifest | ConvertTo-Json -Depth 8), $utf8)
    $zip = Join-Path $workspace "artifacts/PLC人工联调台_${version}_${stamp}_$($commit.Substring(0,8)).zip"
    Compress-Archive -LiteralPath $package -DestinationPath $zip -CompressionLevel Optimal
    $pointer = [ordered]@{ packageDirectory=$package; sourceCommit=$commit; version=$version; zip=$zip; defaultLabReady=$defaultReady }
    $pointerPath = Join-Path $workspace 'artifacts/PlcHandshakeLab-current.json'
    [IO.File]::WriteAllText(($pointerPath + '.tmp'), ($pointer | ConvertTo-Json), $utf8)
    Move-Item -LiteralPath ($pointerPath + '.tmp') -Destination $pointerPath -Force
    Write-Output ($pointer | ConvertTo-Json)
} finally { Pop-Location }
