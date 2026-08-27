#requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
. (Join-Path -Path $PSScriptRoot -ChildPath 'common.ps1')

$destination = '/home/ubuntu/work/inbox'
$manifestPath = $env:AGENT_DEV_TRANSFER_MANIFEST

if ([string]::IsNullOrWhiteSpace($manifestPath)) {
    throw 'The transfer manifest was not provided by the VM control panel.'
}

try {
    $resolvedManifest = Resolve-Path -LiteralPath $manifestPath -ErrorAction Stop
    $sources = @(
        [System.IO.File]::ReadAllLines($resolvedManifest.ProviderPath) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )

    if ($sources.Count -eq 0) {
        throw 'No files or folders were selected for transfer.'
    }

    $name = Get-VmName
    Assert-MultipassInstanceRunning -Name $name | Out-Null

    # The inbox is wholly inside the guest. Creating it does not expose a host mount.
    [void](Invoke-MultipassCommand -Arguments @(
        'exec',
        $name,
        '--',
        'mkdir',
        '-p',
        '--',
        $destination
    ))

    foreach ($source in $sources) {
        $resolvedSource = Resolve-Path -LiteralPath $source -ErrorAction Stop
        $sourceItem = Get-Item -LiteralPath $resolvedSource.ProviderPath -ErrorAction Stop

        $arguments = @('transfer')
        if ($sourceItem.PSIsContainer) {
            $arguments += '--recursive'
        }
        $arguments += [string]$sourceItem.FullName
        $arguments += "$name`:$destination"

        [void](Invoke-MultipassCommand -Arguments $arguments)
        Write-Host "Copied '$($sourceItem.FullName)' to '$destination'."
    }

    Write-Host "Transfer complete: $($sources.Count) item(s) copied to '$destination'."
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($manifestPath)) {
        Remove-Item -LiteralPath $manifestPath -Force -ErrorAction SilentlyContinue
    }
}
