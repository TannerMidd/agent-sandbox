#requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
. (Join-Path -Path $PSScriptRoot -ChildPath 'common.ps1')

$checks = New-Object System.Collections.Generic.List[object]

function Add-Check {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [bool]$Passed,

        [Parameter(Mandatory = $true)]
        [string]$Detail
    )

    $status = if ($Passed) { 'PASS' } else { 'WARN' }
    $checks.Add([pscustomobject]@{
            Status = $status
            Check  = $Name
            Detail = $Detail
        })
}

$config = Get-VmConfig
$storagePath = Get-StoragePath

Add-Check -Name 'Windows host' -Passed ($env:OS -eq 'Windows_NT') -Detail ("OS={0}" -f $env:OS)
Add-Check -Name 'PowerShell version' -Passed ($PSVersionTable.PSVersion.Major -ge 5) -Detail ([string]$PSVersionTable.PSVersion)
Add-Check -Name 'Configuration name' -Passed ((Get-VmName) -ne 'primary') -Detail ("Instance={0}" -f (Get-VmName))
Add-Check -Name 'Workspace storage path' -Passed (([System.IO.Path]::GetPathRoot($storagePath)) -ieq 'F:\') -Detail $storagePath
Add-Check -Name 'Cloud-init file' -Passed (Test-Path -LiteralPath (Get-CloudInitPath) -PathType Leaf) -Detail (Get-CloudInitPath)

$storageParent = Split-Path -Path $storagePath -Parent
if (-not (Test-Path -LiteralPath $storageParent -PathType Container)) {
    Add-Check -Name 'Storage parent' -Passed $false -Detail "Parent directory '$storageParent' does not exist yet."
}
else {
    Add-Check -Name 'Storage parent' -Passed $true -Detail $storageParent
}

$storagePathSafe = $true
try {
    Assert-PathChainSafe -Paths @((Get-WorkspaceRoot), $storageParent, $storagePath)
}
catch {
    $storagePathSafe = $false
    Add-Check -Name 'Storage path safety' -Passed $false -Detail $_.Exception.Message
}

$storageEnvironment = @()
try {
    $storageEnvironment = @(Assert-MultipassStorageEnvironment -TargetStorage $storagePath)
    $scopeDetails = ($storageEnvironment | ForEach-Object {
            if ($_.IsSet) { "$($_.Scope)=$($_.NormalizedValue)" } else { "$($_.Scope)=<unset>" }
        }) -join '; '
    Add-Check -Name 'MULTIPASS_STORAGE scopes' -Passed $true -Detail $scopeDetails
}
catch {
    $storageEnvironment = @(Get-MultipassStorageEnvironment)
    $scopeDetails = ($storageEnvironment | ForEach-Object {
            if ($_.IsSet) {
                if ($null -ne $_.Error) { "$($_.Scope)=$($_.Value) [invalid]" } else { "$($_.Scope)=$($_.NormalizedValue)" }
            }
            else { "$($_.Scope)=<unset>" }
        }) -join '; '
    Add-Check -Name 'MULTIPASS_STORAGE scopes' -Passed $false -Detail "$($_.Exception.Message) Scopes: $scopeDetails"
}

$machineScope = @($storageEnvironment | Where-Object { $_.Scope -eq 'Machine' })[0]
$machineStorageMatches = $machineScope.IsSet -and $null -eq $machineScope.Error -and $machineScope.NormalizedValue.Equals((ConvertTo-NormalizedPath -Path $storagePath), [System.StringComparison]::OrdinalIgnoreCase)
$machineStorageConflict = $machineScope.IsSet -and -not $machineStorageMatches

if (-not $storagePathSafe) {
    Add-Check -Name 'Storage readiness' -Passed $false -Detail 'Skipped storage existence/ACL inspection because the path chain failed the reparse-point safety check.'
}
elseif (-not (Test-Path -LiteralPath $storagePath)) {
    if ($machineStorageConflict) {
        Add-Check -Name 'Storage readiness' -Passed $false -Detail "Exact target '$storagePath' is absent, but machine MULTIPASS_STORAGE points elsewhere; installer will refuse migration."
    }
    else {
        Add-Check -Name 'Storage readiness' -Passed $true -Detail "Exact target '$storagePath' is absent, which is expected before the first install; installer will create it after ShouldProcess confirmation."
    }
}
elseif (-not (Test-Path -LiteralPath $storagePath -PathType Container)) {
    Add-Check -Name 'Storage readiness' -Passed $false -Detail "Exact target '$storagePath' exists but is not a directory."
}
else {
    $security = Get-StorageSecurityStatus -Path $storagePath
    if (-not $security.Valid) {
        Add-Check -Name 'Storage readiness' -Passed $false -Detail $security.Detail
    }
    else {
        $securityMode = [string]$security.Mode
        try {
            $storageEntries = @(Get-ChildItem -LiteralPath $storagePath -Force -ErrorAction Stop)
            if ($storageEntries.Count -eq 0) {
                Add-Check -Name 'Storage readiness' -Passed (-not $machineStorageConflict) -Detail ("Exact target '$storagePath' is present and empty with validated ACL mode '$securityMode'; machine storage conflict=$machineStorageConflict.")
            }
            elseif ($machineStorageMatches) {
                Add-Check -Name 'Storage readiness' -Passed $true -Detail ("Existing target '$storagePath' contains data with validated ACL mode '$securityMode' and machine MULTIPASS_STORAGE matches; treat as an existing installation and do not migrate it.")
            }
            else {
                Add-Check -Name 'Storage readiness' -Passed $false -Detail "Existing target '$storagePath' is nonempty with validated ACL mode '$securityMode' but without a validated matching machine storage setting; installer will refuse reuse."
            }
        }
        catch {
            Add-Check -Name 'Storage readiness' -Passed $false -Detail "Unable to inspect '$storagePath': $($_.Exception.Message)"
        }
    }
}

try {
    $driveName = ([System.IO.Path]::GetPathRoot($storagePath)).TrimEnd('\').TrimEnd(':')
    $drive = Get-PSDrive -Name $driveName -ErrorAction Stop
    $freeGiB = [math]::Round(([double]$drive.Free / 1GB), 1)
    Add-Check -Name 'Storage free space' -Passed ($freeGiB -ge 55) -Detail ("{0} GiB free on {1}:" -f $freeGiB, $driveName)
}
catch {
    Add-Check -Name 'Storage free space' -Passed $false -Detail $_.Exception.Message
}

$multipassCommand = $null
try {
    $multipassCommand = Get-MultipassCommand
    Add-Check -Name 'Multipass CLI' -Passed $true -Detail $multipassCommand
}
catch {
    Add-Check -Name 'Multipass CLI' -Passed $false -Detail 'Not found on PATH or the expected Program Files path. Run install-host.ps1 from an elevated shell.'
}

if ($null -ne $multipassCommand) {
    try {
        $version = Invoke-MultipassCommand -Arguments @('version')
        $versionText = (($version.Output | ForEach-Object { [string]$_ }) -join ' ').Trim()
        Add-Check -Name 'Multipass responds' -Passed $true -Detail $versionText
    }
    catch {
        Add-Check -Name 'Multipass responds' -Passed $false -Detail $_.Exception.Message
    }

    try {
        $driverResult = Invoke-MultipassCommand -Arguments @('get', 'local.driver')
        $driver = (($driverResult.Output | ForEach-Object { [string]$_ }) -join '').Trim()
        Add-Check -Name 'Multipass Hyper-V driver' -Passed ($driver -eq 'hyperv') -Detail ("local.driver={0}" -f $driver)
    }
    catch {
        Add-Check -Name 'Multipass Hyper-V driver' -Passed $false -Detail $_.Exception.Message
    }

    try {
        $mountResult = Invoke-MultipassCommand -Arguments @('get', 'local.privileged-mounts')
        $mountSetting = (($mountResult.Output | ForEach-Object { [string]$_ }) -join '').Trim().ToLowerInvariant()
        Add-Check -Name 'Privileged mounts disabled' -Passed ($mountSetting -eq 'false') -Detail ("local.privileged-mounts={0}" -f $mountSetting)
    }
    catch {
        Add-Check -Name 'Privileged mounts disabled' -Passed $false -Detail $_.Exception.Message
    }
}

try {
    $hyperVFeature = Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V -ErrorAction Stop
    $enabled = [string]$hyperVFeature.State -match 'Enabled'
    Add-Check -Name 'Hyper-V Windows feature' -Passed $enabled -Detail ([string]$hyperVFeature.State)
}
catch {
    Add-Check -Name 'Hyper-V Windows feature' -Passed $false -Detail "Unable to inspect feature: $($_.Exception.Message)"
}

try {
    $guestMemoryMatch = [regex]::Match([string]$config.Memory, '[0-9]+(?:\.[0-9]+)?')
    if (-not $guestMemoryMatch.Success) {
        throw "Unable to parse configured guest memory '$($config.Memory)'."
    }

    $guestMemoryGiB = [double]$guestMemoryMatch.Value
    $hostReserveGiB = 4
    $operatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
    $freeMemoryGiB = [math]::Round(([double]$operatingSystem.FreePhysicalMemory / 1MB), 1)
    $requiredFreeGiB = $guestMemoryGiB + $hostReserveGiB
    Add-Check -Name 'Free host memory' -Passed ($freeMemoryGiB -ge $requiredFreeGiB) -Detail ("{0} GiB free; require {1} GiB ({2} GiB guest + {3} GiB host reserve)" -f $freeMemoryGiB, $requiredFreeGiB, $guestMemoryGiB, $hostReserveGiB)
}
catch {
    Add-Check -Name 'Free host memory' -Passed $false -Detail $_.Exception.Message
}

$checks | Format-Table -AutoSize | Out-Host
if (@($checks | Where-Object { $_.Status -eq 'WARN' }).Count -gt 0) {
    Write-Warning 'One or more checks need attention. doctor.ps1 is read-only; it did not install or change anything.'
}
else {
    Write-Host 'All doctor checks passed.'
}
