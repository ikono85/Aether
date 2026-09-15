<#
.SYNOPSIS
    Test d'intégration « appliquer puis annuler » des optimisations d'AETHER.

.DESCRIPTION
    Pour chaque module réversible : instantané du registre et des réglages d'alimentation
    concernés, application (Aether.exe --apply), vérification qu'un changement a eu lieu,
    annulation (Aether.exe --revert), puis comparaison avec l'instantané de départ.

    À EXÉCUTER DANS UNE MACHINE VIRTUELLE JETABLE, en administrateur, AETHER fermé :
    ce script modifie réellement Windows.

.EXAMPLE
    .\RestoreRoundTrip.ps1 -AetherExe "C:\Program Files\AETHER\Aether.exe"
#>
param(
    [Parameter(Mandatory)] [string] $AetherExe,
    [string[]] $Modules = @('visualfx', 'gamebar', 'usb_power', 'gaming_boost')
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Ce script doit être lancé en administrateur.'
}

# Ce que chaque module touche : clés de registre et/ou commande dont la sortie doit revenir à l'identique.
$Probes = @{
    visualfx     = @{ Keys = @('HKCU\Control Panel\Desktop', 'HKCU\Control Panel\Desktop\WindowMetrics',
                                'HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects',
                                'HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize') }
    gamebar      = @{ Keys = @('HKCU\System\GameConfigStore', 'HKCU\Software\Microsoft\GameBar',
                                'HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR',
                                'HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR') }
    usb_power    = @{ Command = { powercfg /query SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 } }
    gaming_boost = @{ Keys = @('HKCU\Software\Microsoft\GameBar'); Command = { powercfg /getactivescheme } }
}

function Get-Snapshot([hashtable] $probe) {
    $text = New-Object System.Text.StringBuilder
    foreach ($key in @($probe.Keys)) {
        $file = [IO.Path]::GetTempFileName()
        & reg.exe export $key $file /y *> $null
        if ($LASTEXITCODE -eq 0) { [void]$text.AppendLine((Get-Content $file -Raw)) }
        else { [void]$text.AppendLine("<absent> $key") }
        Remove-Item $file -ErrorAction SilentlyContinue
    }
    if ($probe.Command) { [void]$text.AppendLine((& $probe.Command | Out-String)) }
    return $text.ToString()
}

function Invoke-Aether([string[]] $arguments) {
    $p = Start-Process -FilePath $AetherExe -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    return $p.ExitCode
}

$failures = 0
foreach ($module in $Modules) {
    $probe = $Probes[$module]
    if (-not $probe) { Write-Warning "Aucune sonde pour $module : ignoré."; continue }

    $before = Get-Snapshot $probe

    $code = Invoke-Aether @('--apply', $module)
    $applied = Get-Snapshot $probe
    if ($code -ne 0) { Write-Host "ÉCHEC  $module : --apply a renvoyé $code" -ForegroundColor Red; $failures++; continue }
    if ($applied -eq $before) { Write-Host "INFO   $module : aucun changement observé (déjà dans l'état cible ?)" -ForegroundColor Yellow }

    $code = Invoke-Aether @('--revert', $module)
    $after = Get-Snapshot $probe
    if ($code -ne 0) { Write-Host "ÉCHEC  $module : --revert a renvoyé $code" -ForegroundColor Red; $failures++; continue }

    if ($after -eq $before) { Write-Host "OK     $module : état d'origine restauré à l'identique" -ForegroundColor Green }
    else {
        Write-Host "ÉCHEC  $module : l'état après annulation diffère de l'état initial" -ForegroundColor Red
        Compare-Object ($before -split "`n") ($after -split "`n") | Select-Object -First 20 | Format-Table | Out-String | Write-Host
        $failures++
    }
}

Write-Host ''
Write-Host "Journal détaillé : $env:LOCALAPPDATA\Aether\logs"
exit $failures
