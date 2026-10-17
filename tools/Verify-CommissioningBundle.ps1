param([Parameter(Mandatory=$true)][string]$BundleDirectory)
$ErrorActionPreference='Stop'
$verifyRoot=[IO.Path]::GetFullPath($BundleDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)
$verifyManifestPath=Join-Path $verifyRoot 'release-manifest.json'
$verifyManifest=Get-Content -LiteralPath $verifyManifestPath -Raw | ConvertFrom-Json
if($verifyManifest.schema -ne 'agv-commissioning-release' -or $verifyManifest.version -ne 1){throw '不支持的候选清单。'}
$verifySeen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($verifyFile in $verifyManifest.files){
    $verifyPath=[IO.Path]::GetFullPath((Join-Path $verifyRoot $verifyFile.path))
    if(!$verifyPath.StartsWith($verifyRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or !$verifySeen.Add($verifyPath)){throw '清单路径越界或重复。'}
    $verifyItem=Get-Item -LiteralPath $verifyPath
    if($verifyItem.PSIsContainer){throw "清单不是普通文件：$($verifyFile.path)"}
    $verifyAncestor=$verifyItem
    while($verifyAncestor.FullName.Length -ge $verifyRoot.Length){
        if(($verifyAncestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw '不接受候选目录中的链接跳转。'}
        $verifyAncestor=if($verifyAncestor -is [IO.DirectoryInfo]){$verifyAncestor.Parent}else{$verifyAncestor.Directory}
        if(!$verifyAncestor){break}
    }
    if($verifyItem.Length -ne $verifyFile.bytes -or (Get-FileHash -LiteralPath $verifyPath -Algorithm SHA256).Hash -ne $verifyFile.sha256){throw "候选文件不同：$($verifyFile.path)"}
}
foreach($verifyActual in Get-ChildItem -LiteralPath $verifyRoot -File -Recurse){
    if($verifyActual.FullName -ne $verifyManifestPath -and !$verifySeen.Contains($verifyActual.FullName)){throw "存在未列入初始清单的文件：$($verifyActual.FullName)。运行后的数据/日志也会使目录不再是初始包。"}
}
foreach($verifyComponent in @(@{Dir='Service';Name='AgvDispatch.Service'},@{Dir='WPF';Name='AgvDispatch.Wpf'},@{Dir='Web/host';Name='AgvDispatch.WebHost'})){
    if([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $verifyRoot "$($verifyComponent.Dir)/$($verifyComponent.Name).dll")).ProductVersion -ne $verifyManifest.releaseId){throw '候选程序集批次不符。'}
}
Write-Output "PASS $($verifyManifest.releaseId): $($verifySeen.Count) initial files, hashes and three component versions match."
