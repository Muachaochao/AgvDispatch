[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    $files = @(git -c core.quotepath=false ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw '无法读取Git候选文件。' }
    $issues = [Collections.Generic.List[string]]::new()
    $sensitivePath = '(?i)(^|/)(bin|obj|data|logs|publish|__pycache__)/|^docs/(vendor|commissioning)/|\.(dwg|dxf|smap|smap3d|agvmap\.json|db|sqlite3?|pfx|p12|pem|lic|exe|dll|pyc|pdf|xlsx|zip|7z)$'
    $secretPatterns = @(
        'gh[pousr]_[A-Za-z0-9_]{20,}',
        'github_pat_[A-Za-z0-9_]{20,}',
        '-----BEGIN (?:[A-Z]+ )?PRIVATE KEY-----',
        '(?i)(?:password|authorizationcode|licensekey|apikey|clientsecret|access_token)["'']?\s*[:=]\s*["''](?!(?:<|\$|\{|YOUR_|EXAMPLE_|REPLACE_|CHANGE_ME))[^"''\s]{8,}["'']'
    )
    foreach ($file in $files) {
        if ($file -match $sensitivePath -or ($file -match '(?i)(^|/)appsettings.*\.json$' -and $file -notmatch 'appsettings\.example\.json$')) {
            $issues.Add("禁止上传的文件：$file")
        }
        $item = Get-Item -LiteralPath $file
        if ($item.Length -gt 10MB) { $issues.Add("超过10MB，需人工核对：$file") }
        if ($file -notmatch '\.(cs|xaml|json|md|ps1|py|config|props|targets|yml|yaml|txt)$') { continue }
        $lines = [IO.File]::ReadAllLines($item.FullName)
        for ($lineIndex = 0; $lineIndex -lt $lines.Length; $lineIndex++) {
            foreach ($pattern in $secretPatterns) {
                if ($lines[$lineIndex] -match $pattern) {
                    # Never print the matched credential or source line.
                    $issues.Add("可能的凭据：${file}:$($lineIndex + 1)（内容已隐藏）")
                    break
                }
            }
        }
    }
    if ($issues.Count) { $issues | ForEach-Object { Write-Host $_ }; throw "检查未通过：$($issues.Count)项。" }
    Write-Host "PASS: $($files.Count)个Git候选文件；未发现禁止路径、大文件或规则命中的明文凭据。此检查不代表完整安全审计。"
} finally { Pop-Location }
