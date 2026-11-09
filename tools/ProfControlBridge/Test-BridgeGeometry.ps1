$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'BridgeGeometry.ps1')
$cases = 0
foreach ($invert in @($true, $false)) {
    foreach ($angle in @(0, ([Math]::PI / 2), [Math]::PI, (-[Math]::PI / 2), (7 * [Math]::PI), (-9 * [Math]::PI / 2))) {
        $actual = Convert-SimulationHeadingToDisplayRadians $angle $invert
        $expected = if ($invert) { -$angle } else { $angle }
        if ([Math]::Abs([Math]::Sin($actual)-[Math]::Sin($expected)) -gt 1e-10 -or
            [Math]::Abs([Math]::Cos($actual)-[Math]::Cos($expected)) -gt 1e-10) {
            throw "Heading mismatch: $angle, invertY=$invert"
        }
        $cases++
    }
}
foreach ($invalid in @([double]::NaN, [double]::PositiveInfinity, [double]::NegativeInfinity)) {
    $rejected = $false
    try { Convert-SimulationHeadingToDisplayRadians $invalid | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'Invalid heading accepted.' }
    $cases++
}
Write-Output "PASS $cases heading checks; north/south reflection, east/west direction, wrap and invalid observations."
