#requires -Version 5.1

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [Alias('Path')]
    [string]$GuestSource,

    [Parameter(Mandatory = $true, Position = 1)]
    [Alias('Target')]
    [string]$Destination,

    [switch]$Recurse
)

Set-StrictMode -Version Latest
. (Join-Path -Path $PSScriptRoot -ChildPath 'common.ps1')

$name = Get-VmName
Assert-MultipassInstanceRunning -Name $name | Out-Null

if ($GuestSource -notmatch '^/[^\r\n]*$' -or $GuestSource.Contains(':')) {
    throw "Guest source '$GuestSource' must be an absolute Linux path without a colon."
}

$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$destinationParent = Split-Path -Path $destinationPath -Parent
if (-not (Test-Path -LiteralPath $destinationParent -PathType Container)) {
    throw "Local destination parent '$destinationParent' does not exist. Create it first so copy-out does not guess a host target."
}

$guestSourceTarget = "$name`:$GuestSource"
$arguments = @('transfer')
if ($Recurse) {
    $arguments += '--recursive'
}
$arguments += $guestSourceTarget
$arguments += $destinationPath

if (-not $PSCmdlet.ShouldProcess($destinationPath, "Transfer '$guestSourceTarget' out of the guest")) {
    return
}

[void](Invoke-MultipassCommand -Arguments $arguments)
Write-Host "Transferred '$guestSourceTarget' to '$destinationPath'."

