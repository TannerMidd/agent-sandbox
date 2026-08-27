#requires -Version 5.1

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
param()

Set-StrictMode -Version Latest
. (Join-Path -Path $PSScriptRoot -ChildPath 'common.ps1')

$name = Get-VmName
Assert-MultipassAvailable
$record = Assert-MultipassInstanceExists -Name $name

if ([string]$record.state -eq 'RUNNING') {
    Write-Host "Multipass instance '$name' is already running."
    return
}

if (-not $PSCmdlet.ShouldProcess($name, 'Start the Multipass guest')) {
    return
}

Ensure-MultipassDefaults
[void](Invoke-MultipassCommand -Arguments @('start', $name))
[void](Wait-MultipassInstanceState -Name $name -ExpectedState @('RUNNING') -TimeoutSeconds ([int](Get-VmConfig).TimeoutSeconds))
Write-Host "Multipass instance '$name' is running."

