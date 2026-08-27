#requires -Version 5.1

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string]$Snapshot
)

Set-StrictMode -Version Latest
. (Join-Path -Path $PSScriptRoot -ChildPath 'common.ps1')

$config = Get-VmConfig
$instanceName = Get-VmName
if ([string]::IsNullOrWhiteSpace($Snapshot)) {
    $Snapshot = [string]$config.BaselineSnapshot
}
Assert-SnapshotName -Name $Snapshot
Assert-MultipassAvailable
$record = Assert-MultipassInstanceExists -Name $instanceName
$restoreTarget = "$instanceName.$Snapshot"
Assert-MultipassSnapshotExists -InstanceName $instanceName -SnapshotName $Snapshot

if (-not $PSCmdlet.ShouldProcess($restoreTarget, 'Stop the exact instance and restore the snapshot destructively')) {
    return
}

if ([string]$record.state -ne 'STOPPED') {
    [void](Invoke-MultipassCommand -Arguments @('stop', $instanceName))
    [void](Wait-MultipassInstanceState -Name $instanceName -ExpectedState @('STOPPED') -TimeoutSeconds ([int]$config.TimeoutSeconds))
}

[void](Invoke-MultipassCommand -Arguments @('restore', '--destructive', $restoreTarget))
[void](Invoke-MultipassCommand -Arguments @('set', "local.$instanceName.memory=$([string]$config.Memory)"))
Write-Host "Restored '$restoreTarget' and reapplied the configured $($config.Memory) memory limit. The instance remains stopped; run start.ps1 when you are ready to use it."
