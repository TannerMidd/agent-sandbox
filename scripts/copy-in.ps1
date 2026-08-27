#requires -Version 5.1

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [Alias('Path')]
    [string]$Source,

    [Parameter(Position = 1)]
    [Alias('Target')]
    [string]$Destination = '/home/ubuntu/work',

    [switch]$Recurse
)

Set-StrictMode -Version Latest
. (Join-Path -Path $PSScriptRoot -ChildPath 'common.ps1')

$name = Get-VmName
Assert-MultipassInstanceRunning -Name $name | Out-Null

if ($Destination -notmatch '^/[^\r\n]*$' -or $Destination.Contains(':')) {
    throw "Guest destination '$Destination' must be an absolute Linux path without a colon."
}

$resolvedSource = Resolve-Path -LiteralPath $Source -ErrorAction Stop
$sourceItem = Get-Item -LiteralPath $resolvedSource.ProviderPath -ErrorAction Stop
if ($sourceItem.PSIsContainer -and -not $Recurse) {
    throw "Source '$($sourceItem.FullName)' is a directory. Use -Recurse for directory transfers."
}

$guestTarget = "$name`:$Destination"
$arguments = @('transfer')
if ($Recurse) {
    $arguments += '--recursive'
}
$arguments += [string]$sourceItem.FullName
$arguments += $guestTarget

if (-not $PSCmdlet.ShouldProcess($guestTarget, "Transfer '$($sourceItem.FullName)' into the guest")) {
    return
}

[void](Invoke-MultipassCommand -Arguments $arguments)
Write-Host "Transferred '$($sourceItem.FullName)' to '$guestTarget'."

