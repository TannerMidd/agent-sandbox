#requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
. (Join-Path -Path $PSScriptRoot -ChildPath 'common.ps1')

$config = Get-VmConfig
$name = Get-VmName
$storagePath = Get-StoragePath
Write-Host "Workspace: $((Get-WorkspaceRoot))"
Write-Host "Storage:   $storagePath"
Write-Host "Instance:  $name"

try {
    Assert-MultipassAvailable
}
catch {
    Write-Warning $_.Exception.Message
    return
}

try {
    $list = Invoke-MultipassCommand -Arguments @('list', '--format', 'json')
    (($list.Output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine) | Out-Host
}
catch {
    Write-Warning $_.Exception.Message
}

try {
    $record = Get-MultipassInstanceRecord -Name $name
    if ($null -ne $record) {
        $info = Invoke-MultipassCommand -Arguments @('info', $name)
        (($info.Output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine) | Out-Host
    }
    else {
        Write-Host "Instance '$name' is not present."
    }
}
catch {
    Write-Warning $_.Exception.Message
}

