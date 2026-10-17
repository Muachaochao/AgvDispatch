param(
    [ValidateRange(1024,65535)][int]$Port = 5188,
    [string]$PipeName = 'AgvDispatch.Service'
)
$ErrorActionPreference = 'Stop'
$exePath = Join-Path $PSScriptRoot 'host/AgvDispatch.WebHost.exe'
$staticPath = Join-Path $PSScriptRoot 'wwwroot'
if (!(Test-Path -LiteralPath $exePath) -or !(Test-Path -LiteralPath (Join-Path $staticPath 'index.html'))) {
    throw 'Run this launcher from the packaged Web preview directory (host + wwwroot).'
}
$previewUrl = "http://127.0.0.1:$Port/"
try {
    $existing = Invoke-RestMethod -Uri ($previewUrl + 'health') -TimeoutSec 1
    if ($existing.role -eq 'web-adapter') {
        Write-Host "An AGV Web adapter already uses $previewUrl. Stop it before changing pipe settings."
        Start-Process $previewUrl
        exit 0
    }
} catch { }
$arguments = @("--Web:Port=$Port", "--Web:PipeName=$PipeName", '--Web:EnableTaskSubmission=false', ('--Web:StaticPath="' + $staticPath + '"'))
$webProcess = Start-Process -FilePath $exePath -ArgumentList $arguments -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru
@{ ProcessId=$webProcess.Id; StartTimeTicks=$webProcess.StartTime.ToUniversalTime().Ticks; Executable=$exePath; Port=$Port } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'web-preview-process.json') -Encoding UTF8
$ready = $false
for ($attempt=0; $attempt -lt 20; $attempt++) {
    if ($webProcess.HasExited) { throw 'Web adapter exited. Check port availability.' }
    try { $health = Invoke-RestMethod -Uri ($previewUrl + 'health') -TimeoutSec 1; if ($health.role -eq 'web-adapter') { $ready=$true; break } } catch { }
    Start-Sleep -Milliseconds 250
}
if (!$ready) { throw 'Web adapter did not become ready. Use Stop-WebPreview.ps1 to stop this owned process.' }
Write-Host "AGV Web preview: $previewUrl"
Write-Host "Pipe: $PipeName; Web task submission is OFF."
Start-Process $previewUrl
