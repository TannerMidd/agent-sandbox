#requires -Version 5.1

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string]$Name
)

Set-StrictMode -Version Latest
. (Join-Path -Path $PSScriptRoot -ChildPath 'common.ps1')

$config = Get-VmConfig
$instanceName = Get-VmName
Assert-SnapshotName -Name $Name
Assert-MultipassAvailable
$record = Assert-MultipassInstanceExists -Name $instanceName

if ([string]$record.state -ne 'STOPPED') {
    throw "Snapshot creation requires the exact instance '$instanceName' to be STOPPED; current state is '$($record.state)'. Run stop.ps1 first."
}

if (-not $PSCmdlet.ShouldProcess("$instanceName.$Name", 'Create a named Multipass snapshot')) {
    return
}

[void](Invoke-MultipassCommand -Arguments @('snapshot', $instanceName, '--name', $Name))
Write-Host "Created snapshot '$Name' for '$instanceName'."
