#requires -Version 5.1

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
param()

Set-StrictMode -Version Latest
. (Join-Path -Path $PSScriptRoot -ChildPath 'common.ps1')

$name = Get-VmName
Assert-MultipassAvailable
$record = Get-MultipassInstanceRecord -Name $name
if ($null -eq $record) {
    Write-Host "Multipass instance '$name' does not exist."
    return
}

if ([string]$record.state -eq 'STOPPED') {
    Write-Host "Multipass instance '$name' is already stopped."
    return
}

if (-not $PSCmdlet.ShouldProcess($name, 'Stop the Multipass guest')) {
    return
}

[void](Invoke-MultipassCommand -Arguments @('stop', $name))
[void](Wait-MultipassInstanceState -Name $name -ExpectedState @('STOPPED') -TimeoutSeconds ([int](Get-VmConfig).TimeoutSeconds))
Write-Host "Multipass instance '$name' is stopped."

