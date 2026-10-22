param([Parameter(Mandatory=$true)][string]$SourceBundle)
$ErrorActionPreference='Stop'
$testWorkspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testArtifacts=Join-Path $testWorkspace 'artifacts'
$testRoot=Join-Path $testArtifacts ('release-verifier-test-'+[Guid]::NewGuid().ToString('N'))
$testExternal=$testRoot+'-external'
$testLink=Join-Path $testRoot 'Service/data/linked'
$testVerifier=Join-Path $PSScriptRoot 'Verify-CommissioningBundle.ps1'
$testChecks=0
function Expect-Rejected([scriptblock]$Action){
    $rejected=$false
    try { & $Action | Out-Null } catch { $rejected=$true }
    if(!$rejected){throw 'Expected verifier rejection.'}
    $script:testChecks++
}
try {
    # Small, non-runnable fixture: actual stamped application DLLs only.
    # This exercises manifest/extra-file boundaries, not application startup.
    $testSource=Get-Content -LiteralPath (Join-Path $SourceBundle 'release-manifest.json') -Raw | ConvertFrom-Json
    $testFiles=@()
    foreach($testRelative in @('Service/AgvDispatch.Service.dll','WPF/AgvDispatch.Wpf.dll','Web/host/AgvDispatch.WebHost.dll')){
        $testFile=Join-Path $testRoot $testRelative
        New-Item -ItemType Directory -Path (Split-Path $testFile) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $SourceBundle $testRelative) -Destination $testFile
        $testFiles+=@{path=$testRelative;bytes=(Get-Item -LiteralPath $testFile).Length;sha256=(Get-FileHash -LiteralPath $testFile -Algorithm SHA256).Hash}
    }
    $testManifest=@{schema='agv-commissioning-release';version=1;releaseId=$testSource.releaseId;files=$testFiles}
    $testManifestPath=Join-Path $testRoot 'release-manifest.json'
    $testManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $testManifestPath -Encoding utf8
    & $testVerifier -BundleDirectory $testRoot | Out-Null
    $testChecks++
    foreach($testRelative in @('Runtime/launcher-session.json','Runtime/launcher.lock','Service/data/agv-dispatch.db','Service/data/state.sqlite-wal','Service/logs/service/service.log','Web/data/projects/example.json')){
        $testFile=Join-Path $testRoot $testRelative
        New-Item -ItemType Directory -Path (Split-Path $testFile) -Force | Out-Null
        [IO.File]::WriteAllText($testFile,'fixture')
    }
    Expect-Rejected { & $testVerifier -BundleDirectory $testRoot }
    & $testVerifier -BundleDirectory $testRoot -AllowRuntimeData | Out-Null
    $testChecks++
    foreach($testRelative in @('Service/data/unlisted.dll','Service/unknown.json')){
        $testFile=Join-Path $testRoot $testRelative
        [IO.File]::WriteAllText($testFile,'unlisted')
        Expect-Rejected { & $testVerifier -BundleDirectory $testRoot -AllowRuntimeData }
        Remove-Item -LiteralPath $testFile
    }
    $testProgram=Join-Path $testRoot 'Service/AgvDispatch.Service.dll'
    [IO.File]::AppendAllText($testProgram,'tampered')
    Expect-Rejected { & $testVerifier -BundleDirectory $testRoot -AllowRuntimeData }
    Copy-Item -LiteralPath (Join-Path $SourceBundle 'Service/AgvDispatch.Service.dll') -Destination $testProgram -Force
    New-Item -ItemType Directory -Path $testExternal | Out-Null
    New-Item -ItemType Junction -Path $testLink -Target $testExternal | Out-Null
    Expect-Rejected { & $testVerifier -BundleDirectory $testRoot -AllowRuntimeData }
    Remove-Item -LiteralPath $testLink -Force
    $testManifest.files=@(@{path='../outside.dll';bytes=0;sha256='invalid'})
    $testManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $testManifestPath -Encoding utf8
    Expect-Rejected { & $testVerifier -BundleDirectory $testRoot -AllowRuntimeData }
    Write-Output "PASS: $testChecks release verification boundary checks. No application processes started."
} finally {
    if(Test-Path -LiteralPath $testLink){Remove-Item -LiteralPath $testLink -Force}
    foreach($testCleanup in @($testRoot,$testExternal)){
        $testResolved=[IO.Path]::GetFullPath($testCleanup)
        if(!$testResolved.StartsWith($testArtifacts+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or
            !(Split-Path $testResolved -Leaf).StartsWith('release-verifier-test-')){throw 'Refused unsafe test cleanup.'}
        if(Test-Path -LiteralPath $testResolved){Remove-Item -LiteralPath $testResolved -Recurse -Force}
    }
}
