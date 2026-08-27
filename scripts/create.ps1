#requires -Version 5.1

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param()

Set-StrictMode -Version Latest
. (Join-Path -Path $PSScriptRoot -ChildPath 'common.ps1')

$config = Get-VmConfig
$name = Get-VmName
$cloudInitPath = Get-CloudInitPath
$baselineSnapshot = [string]$config.BaselineSnapshot
Assert-SnapshotName -Name $baselineSnapshot
Assert-MultipassAvailable

$existing = Get-MultipassInstanceRecord -Name $name
if ($null -ne $existing) {
    Write-Host "Multipass instance '$name' already exists in state '$($existing.state)'. No changes were made. Use start.ps1, snapshot.ps1, restore.ps1, or rebuild.ps1 as appropriate."
    return
}

$launchArguments = @(
    'launch',
    [string]$config.Image,
    '--name',
    $name,
    '--cpus',
    [string]$config.Cpus,
    '--memory',
    [string]$config.Memory,
    '--disk',
    [string]$config.Disk,
    '--cloud-init',
    $cloudInitPath,
    '--timeout',
    [string]$config.TimeoutSeconds
)

if (-not $PSCmdlet.ShouldProcess($name, 'Launch and provision the Multipass guest')) {
    return
}

Ensure-MultipassDefaults
Write-Host "Launching '$name' from Ubuntu $($config.Image) with $($config.Cpus) CPUs, $($config.Memory) RAM, and a dynamic $($config.Disk) disk."
[void](Invoke-MultipassCommand -Arguments $launchArguments)

try {
    [void](Wait-MultipassInstanceState -Name $name -ExpectedState @('RUNNING') -TimeoutSeconds ([int]$config.TimeoutSeconds))

    Write-Host 'Waiting for cloud-init to finish.'
    Wait-MultipassCloudInit -Name $name -TimeoutSeconds ([int]$config.TimeoutSeconds)

    $healthScript = 'set -eu; test -d /home/ubuntu/work; command -v git >/dev/null; command -v python3 >/dev/null; command -v rg >/dev/null; command -v shellcheck >/dev/null; command -v docker >/dev/null; sudo -n docker info >/dev/null'
    Write-Host 'Running the guest health check.'
    [void](Invoke-MultipassCommand -Arguments @('exec', $name, '--', 'bash', '-lc', $healthScript))

    Write-Host 'Stopping the guest before creating the baseline snapshot.'
    [void](Invoke-MultipassCommand -Arguments @('stop', $name))
    [void](Wait-MultipassInstanceState -Name $name -ExpectedState @('STOPPED') -TimeoutSeconds ([int]$config.TimeoutSeconds))

    Write-Host "Creating baseline snapshot '$baselineSnapshot'."
    [void](Invoke-MultipassCommand -Arguments @('snapshot', $name, '--name', $baselineSnapshot))

    Write-Host "Restarting '$name'."
    [void](Invoke-MultipassCommand -Arguments @('start', $name))
    [void](Wait-MultipassInstanceState -Name $name -ExpectedState @('RUNNING') -TimeoutSeconds ([int]$config.TimeoutSeconds))
    Write-Host "Guest '$name' is ready. Baseline snapshot: '$baselineSnapshot'."
}
catch {
    $primaryError = $_
    $observedInstance = 'unknown (status query failed)'
    try {
        $observedRecord = Get-MultipassInstanceRecord -Name $name
        if ($null -eq $observedRecord) {
            $observedInstance = 'absent'
        }
        else {
            $observedInstance = "present; state '$([string]$observedRecord.state)'"
        }
    }
    catch {
        $observedInstance = "unknown ($($_.Exception.Message))"
    }

    $snapshotStatus = 'unknown (snapshot listing failed)'
    try {
        $snapshotResult = Invoke-MultipassCommand -Arguments @('list', '--snapshots') -AllowFailure
        $snapshotText = (($snapshotResult.Output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine)
        if ($snapshotResult.ExitCode -eq 0) {
            if ($snapshotText -match [regex]::Escape("$name.$baselineSnapshot")) {
                $snapshotStatus = 'present'
            }
            else {
                $snapshotStatus = 'absent'
            }
        }
    }
    catch {
        $snapshotStatus = "unknown ($($_.Exception.Message))"
    }

    $recoveryReport = @(
        "Provisioning failed after launch for exact instance '$name': $($primaryError.Exception.Message)"
        "Observed exact instance: $observedInstance."
        "Baseline snapshot '$baselineSnapshot': $snapshotStatus."
        "Non-destructive inspection: '$PSScriptRoot\status.ps1'."
        "Destructive recovery only after review and confirmation: '$PSScriptRoot\rebuild.ps1'."
        'No automatic purge or rebuild was performed.'
    ) -join [Environment]::NewLine
    Write-Warning $recoveryReport
    throw $primaryError
}
