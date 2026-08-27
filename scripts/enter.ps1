#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [Alias('Exec')]
    [string[]]$Command
)

Set-StrictMode -Version Latest
. (Join-Path -Path $PSScriptRoot -ChildPath 'common.ps1')

$name = Get-VmName
Assert-MultipassInstanceRunning -Name $name | Out-Null

if ($PSBoundParameters.ContainsKey('Command') -and $null -ne $Command -and $Command.Count -gt 0) {
    $arguments = @('exec', $name, '--')
    $arguments += $Command
    Invoke-MultipassInteractive -Arguments $arguments
}
else {
    Invoke-MultipassInteractive -Arguments @('shell', $name)
}
