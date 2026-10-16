param([string]$EvidenceDirectory='')
$ErrorActionPreference='Stop'
$taskProjectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskReleaseRoot=[IO.Path]::GetFullPath((Join-Path $taskProjectRoot '../发布包'))
$taskStamp=Get-Date -Format yyyyMMdd_HHmmss
$taskEvidence=if($EvidenceDirectory){[IO.Path]::GetFullPath($(if([IO.Path]::IsPathRooted($EvidenceDirectory)){$EvidenceDirectory}else{Join-Path $PWD.Path $EvidenceDirectory}))}else{Join-Path $taskProjectRoot "docs/commissioning/evidence/unified-selfcontained-$taskStamp"}
New-Item -ItemType Directory -Path $taskEvidence -Force | Out-Null
$taskPairs=@(@{Project='AgvDispatch.Service';Target='调度Windows服务'},@{Project='AgvDispatch.Wpf';Target='WPF上位机'})
# Do not publish over a user's running application, and never stop it here.
foreach($taskProcess in Get-Process 'AgvDispatch*' -ErrorAction SilentlyContinue){
    if($taskProcess.Path -and ($taskProcess.Path.StartsWith($taskReleaseRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or
        $taskProcess.Path.StartsWith($taskProjectRoot+'\',[StringComparison]::OrdinalIgnoreCase))){throw "程序正在使用，无法统一更新：$($taskProcess.Path)"}
}
$taskProtected=@()
foreach($taskPair in $taskPairs){
    $taskDestination=Join-Path $taskReleaseRoot $taskPair.Target
    foreach($taskFile in Get-ChildItem -LiteralPath $taskDestination -File -Recurse){
        $taskRelative=[IO.Path]::GetRelativePath($taskDestination,$taskFile.FullName)
        if($taskRelative -like 'appsettings*.json' -or $taskRelative -eq 'NLog.config' -or $taskRelative -match '^(config|data)[\\/]'){
            $taskProtected+=@{Path=$taskFile.FullName;SHA256=(Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash}
        }
    }
}
dotnet build (Join-Path $taskProjectRoot 'AgvDispatch.sln') -c Debug *> (Join-Path $taskEvidence 'build-debug.log')
if($LASTEXITCODE -ne 0){throw "Debug编译失败，见 $taskEvidence/build-debug.log"}
$taskStaging=Join-Path $taskEvidence 'publish'
foreach($taskPair in $taskPairs){
    dotnet build (Join-Path $taskProjectRoot "$($taskPair.Project)/$($taskPair.Project).csproj") -c Debug -r win-x64 --self-contained true *> (Join-Path $taskEvidence "$($taskPair.Project)-debug-selfcontained.log")
    if($LASTEXITCODE -ne 0){throw "Debug自带运行时编译失败：$($taskPair.Project)"}
    dotnet publish (Join-Path $taskProjectRoot "$($taskPair.Project)/$($taskPair.Project).csproj") -c Release -r win-x64 --self-contained true -o (Join-Path $taskStaging $taskPair.Project) *> (Join-Path $taskEvidence "$($taskPair.Project)-publish.log")
    if($LASTEXITCODE -ne 0){throw "Release发布失败：$($taskPair.Project)"}
    # Keep the conventional bin/Release/.../publish entry current as well.
    $taskConventional=Join-Path $taskProjectRoot "$($taskPair.Project)/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish"
    New-Item -ItemType Directory -Path $taskConventional -Force | Out-Null
    foreach($taskItem in Get-ChildItem -LiteralPath (Join-Path $taskStaging $taskPair.Project)){
        if($taskItem.Name -like 'appsettings*.json' -or $taskItem.Name -in @('data','logs','config','NLog.config')){continue}
        Copy-Item -LiteralPath $taskItem.FullName -Destination $taskConventional -Recurse -Force
    }
}
$taskBackup=Join-Path $taskReleaseRoot "更新备份/自带运行时统一编译_$taskStamp"
foreach($taskPair in $taskPairs){
    $taskBackupDirectory=Join-Path $taskBackup $taskPair.Target
    New-Item -ItemType Directory -Path $taskBackupDirectory -Force | Out-Null
    foreach($taskItem in Get-ChildItem -LiteralPath (Join-Path $taskReleaseRoot $taskPair.Target)){
        if($taskItem.Name -like 'appsettings*.json' -or $taskItem.Name -in @('data','logs','config','NLog.config')){continue}
        Copy-Item -LiteralPath $taskItem.FullName -Destination $taskBackupDirectory -Recurse
    }
}
& (Join-Path $PSScriptRoot 'Update-Release.ps1') -PublishRoot $taskStaging
$taskHashes=@()
foreach($taskPair in $taskPairs){
    $taskSource=Join-Path $taskStaging $taskPair.Project
    $taskDestination=Join-Path $taskReleaseRoot $taskPair.Target
    foreach($taskFile in Get-ChildItem -LiteralPath $taskSource -File -Recurse){
        $taskRelative=[IO.Path]::GetRelativePath($taskSource,$taskFile.FullName)
        if($taskRelative -like 'appsettings*.json' -or $taskRelative -eq 'NLog.config' -or $taskRelative -match '^(config|data|logs)[\\/]'){continue}
        $taskTarget=Join-Path $taskDestination $taskRelative
        $taskHash=(Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash
        if((Get-FileHash -LiteralPath $taskTarget -Algorithm SHA256).Hash -ne $taskHash){throw "发布文件哈希不一致：$taskTarget"}
        $taskHashes+=@{Path=$taskTarget;SHA256=$taskHash}
    }
}
foreach($taskItem in $taskProtected){
    if((Get-FileHash -LiteralPath $taskItem.Path -Algorithm SHA256).Hash -ne $taskItem.SHA256){throw "既有配置数据变化：$($taskItem.Path)"}
}
$taskNow=(Get-Date).ToString('o')
@{Time=$taskNow;Mode='win-x64 self-contained';ProgramFiles=$taskHashes.Count;ProtectedFiles=$taskProtected.Count;Programs=$taskHashes;Protected=$taskProtected;Backup=$taskBackup} |
    ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $taskEvidence 'publish-hashes.json') -Encoding utf8
$taskManifestPath=Join-Path $taskReleaseRoot '最新统一构建清单.json'
$taskManifest=Get-Content -LiteralPath $taskManifestPath -Raw | ConvertFrom-Json
$taskManifest.BuildTime=$taskNow
foreach($taskBinary in $taskManifest.Binaries){$taskBinary.SHA256=(Get-FileHash -LiteralPath $taskBinary.Path -Algorithm SHA256).Hash}
$taskManifest | Add-Member -NotePropertyName SelfContainedUpdateTime -NotePropertyValue $taskNow -Force
$taskManifest | Add-Member -NotePropertyName DeploymentMode -NotePropertyValue 'win-x64 self-contained; WPF desktop runtime and backend runtime bundled' -Force
$taskManifest | Add-Member -NotePropertyName SelfContainedEvidence -NotePropertyValue $taskEvidence -Force
$taskManifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $taskManifestPath -Encoding utf8
Write-Output "Debug自带运行时编译、Release成对发布完成；$($taskHashes.Count)文件哈希一致，$($taskProtected.Count)既有配置/数据保留。证据：$taskEvidence"
