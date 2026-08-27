#requires -Version 5.1

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param()

Set-StrictMode -Version Latest
. (Join-Path -Path $PSScriptRoot -ChildPath 'common.ps1')

$instanceName = Get-VmName
Assert-MultipassAvailable
$record = Get-MultipassInstanceRecord -Name $instanceName

if ($null -eq $record) {
    if (-not $PSCmdlet.ShouldProcess($instanceName, 'Create the missing Multipass guest')) {
        return
    }

    & (Join-Path -Path $PSScriptRoot -ChildPath 'create.ps1') -Confirm:$false
    return
}

if (-not $PSCmdlet.ShouldProcess($instanceName, 'Delete and purge this exact Multipass instance, then recreate it')) {
    return
}

[void](Invoke-MultipassCommand -Arguments @('delete', '--purge', $instanceName))
Write-Host "Purged exact instance '$instanceName'. Recreating it from cloud-init."
& (Join-Path -Path $PSScriptRoot -ChildPath 'create.ps1') -Confirm:$false

