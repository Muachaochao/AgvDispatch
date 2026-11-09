function Convert-SimulationHeadingToDisplayRadians {
    param([double]$AngleRadians, [bool]$InvertY = $true)
    if ([double]::IsNaN($AngleRadians) -or [double]::IsInfinity($AngleRadians)) {
        throw 'Simulation heading must be a finite value in radians.'
    }
    $mirrored = if ($InvertY) { -$AngleRadians } else { $AngleRadians }
    [Math]::Atan2([Math]::Sin($mirrored), [Math]::Cos($mirrored))
}
