#requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest

$script:WorkspaceRoot = [System.IO.Path]::GetFullPath((Join-Path -Path $PSScriptRoot -ChildPath '..'))
$script:ConfigPath = Join-Path -Path $script:WorkspaceRoot -ChildPath 'vm.config.psd1'

if (-not (Test-Path -LiteralPath $script:ConfigPath -PathType Leaf)) {
    throw "VM configuration was not found at '$script:ConfigPath'."
}

$script:VmConfig = Import-PowerShellDataFile -LiteralPath $script:ConfigPath
$requiredConfigKeys = @(
    'Name',
    'Image',
    'Cpus',
    'Memory',
    'Disk',
    'TimeoutSeconds',
    'StorageDirectory',
    'BaselineSnapshot'
)

foreach ($key in $requiredConfigKeys) {
    if (-not $script:VmConfig.ContainsKey($key)) {
        throw "VM configuration key '$key' is required."
    }
}

function Get-WorkspaceRoot {
    [CmdletBinding()]
    param()

    return $script:WorkspaceRoot
}

function Get-VmConfig {
    [CmdletBinding()]
    param()

    return $script:VmConfig
}

function Get-VmName {
    [CmdletBinding()]
    param()

    $name = [string]$script:VmConfig.Name
    if ([string]::IsNullOrWhiteSpace($name)) {
        throw 'VM configuration Name cannot be empty.'
    }

    if ($name -ieq 'primary') {
        throw "The instance name '$name' is reserved by Multipass and is not allowed."
    }

    Assert-MultipassIdentifier -Name $name

    return $name
}

function Assert-MultipassIdentifier {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($Name -notmatch '^[A-Za-z](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$') {
        throw "Multipass identifier '$Name' must start with a letter, end with a letter or number, and contain only letters, numbers, and hyphens."
    }
}

function Get-CloudInitPath {
    [CmdletBinding()]
    param()

    $path = Join-Path -Path $script:WorkspaceRoot -ChildPath 'cloud-init.yaml'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "cloud-init.yaml was not found at '$path'."
    }

    return [System.IO.Path]::GetFullPath($path)
}

function Get-StoragePath {
    [CmdletBinding()]
    param()

    $configuredPath = [string]$script:VmConfig.StorageDirectory
    if ([string]::IsNullOrWhiteSpace($configuredPath)) {
        throw 'VM configuration StorageDirectory cannot be empty.'
    }

    $root = [System.IO.Path]::GetFullPath($script:WorkspaceRoot).TrimEnd('\')
    $candidate = [System.IO.Path]::GetFullPath((Join-Path -Path $root -ChildPath $configuredPath)).TrimEnd('\')
    $rootPrefix = $root + '\'

    if (-not $candidate.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "StorageDirectory '$configuredPath' resolves outside the workspace root."
    }

    if ($candidate.Equals($root, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'StorageDirectory must resolve to a workspace subdirectory, not the workspace root.'
    }

    return $candidate
}

function Resolve-StorageSecurityIdentity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object]$IdentityReference
    )

    $identityText = [string]$IdentityReference
    try {
        if ($IdentityReference -is [System.Security.Principal.SecurityIdentifier]) {
            return [string]$IdentityReference.Value
        }

        $account = if ($IdentityReference -is [System.Security.Principal.NTAccount]) {
            $IdentityReference
        }
        else {
            New-Object -TypeName System.Security.Principal.NTAccount -ArgumentList $identityText
        }

        return [string]$account.Translate([System.Security.Principal.SecurityIdentifier]).Value
    }
    catch {
        switch -Regex ($identityText) {
            '(?i)^Everyone$' { return 'S-1-1-0' }
            '(?i)^(NT AUTHORITY\\)?Authenticated Users$' { return 'S-1-5-11' }
            '(?i)^BUILTIN\\Users$' { return 'S-1-5-32-545' }
            '(?i)^BUILTIN\\Guests$' { return 'S-1-5-32-546' }
            '(?i)^BUILTIN\\Administrators$' { return 'S-1-5-32-544' }
            '(?i)^(NT AUTHORITY\\)?SYSTEM$' { return 'S-1-5-18' }
            '(?i)^CREATOR OWNER$' { return 'S-1-3-0' }
            '(?i)^CREATOR GROUP$' { return 'S-1-3-1' }
            default { return $null }
        }
    }
}

function Get-StorageSecurityStatus {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [switch]$RequireInheritedSystem
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        return [pscustomobject]@{
            Valid  = $false
            Mode   = $null
            Detail = "Storage path '$Path' is not an existing directory."
        }
    }

    try {
        $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    }
    catch {
        return [pscustomobject]@{
            Valid  = $false
            Mode   = $null
            Detail = "Unable to inspect ACLs for '$Path': $($_.Exception.Message)"
        }
    }

    $systemSid = 'S-1-5-18'
    $administratorsSid = 'S-1-5-32-544'
    $creatorOwnerSid = 'S-1-3-0'
    $broadPrincipalSids = @(
        'S-1-1-0',
        'S-1-5-4',
        'S-1-5-11',
        'S-1-5-32-545',
        'S-1-5-32-546'
    )
    $fullControl = [int][System.Security.AccessControl.FileSystemRights]::FullControl
    $allowedReadExecuteRights = [int]([System.Security.AccessControl.FileSystemRights]::ReadAndExecute -bor [System.Security.AccessControl.FileSystemRights]::Synchronize)
    $genericRead = -2147483648
    $genericExecute = 0x20000000
    $genericAll = 0x10000000
    $allowedBroadRights = $allowedReadExecuteRights -bor $genericRead -bor $genericExecute
    $inheritedSystemFullControl = $false
    $administratorsFullControl = $false

    # A new workspace directory inherits the host's trusted SYSTEM Full Control
    # and may also inherit development-user Modify rules. Reject deny ACEs,
    # then evaluate this pre-install mode before applying the protected-install
    # broad-principal policy below.
    foreach ($accessRule in @($acl.Access)) {
        if ($accessRule.AccessControlType -eq [System.Security.AccessControl.AccessControlType]::Deny) {
            return [pscustomobject]@{
                Valid  = $false
                Mode   = $null
                Detail = "Storage ACL on '$Path' contains a deny ACE; refusing to operate on it."
            }
        }

        $identitySid = Resolve-StorageSecurityIdentity -IdentityReference $accessRule.IdentityReference
        $rights = [int]$accessRule.FileSystemRights
        $isSystem = $identitySid -eq $systemSid

        if ($isSystem -and $accessRule.IsInherited -and
            (($rights -band $fullControl) -eq $fullControl)) {
            $inheritedSystemFullControl = $true
        }
    }

    if ($inheritedSystemFullControl) {
        return [pscustomobject]@{
            Valid  = $true
            Mode   = 'InheritedSystemFullControl'
            Detail = "Inherited NT AUTHORITY\\SYSTEM Full Control is present on '$Path'."
        }
    }

    if ($RequireInheritedSystem) {
        return [pscustomobject]@{
            Valid  = $false
            Mode   = $null
            Detail = "Inherited NT AUTHORITY\\SYSTEM Full Control was not found on '$Path'; refusing to broaden ACLs before installation."
        }
    }

    # Canonical's installed ACL is protected and intentionally has no
    # inherited SYSTEM ACE. Apply the strict broad-principal policy only to
    # this installed mode (and reject any unexpected non-privileged writes).
    foreach ($accessRule in @($acl.Access)) {
        $identitySid = Resolve-StorageSecurityIdentity -IdentityReference $accessRule.IdentityReference
        $rights = [int]$accessRule.FileSystemRights
        $isBroadPrincipal = $broadPrincipalSids -contains $identitySid
        $isCreatorOwner = $identitySid -eq $creatorOwnerSid
        $isAdministrators = $identitySid -eq $administratorsSid
        $isSystem = $identitySid -eq $systemSid

        if ($isBroadPrincipal) {
            $disallowedRights = $rights -band (-bnot $allowedBroadRights)
            if ($disallowedRights -ne 0) {
                return [pscustomobject]@{
                    Valid  = $false
                    Mode   = $null
                    Detail = "Broad principal '$($accessRule.IdentityReference)' has write/delete/ACL/ownership or other non-read/execute rights on '$Path'; refusing to operate on it."
                }
            }
        }
        elseif ($isCreatorOwner) {
            $disallowedRights = $rights -band (-bnot $genericAll)
            if ($disallowedRights -ne 0) {
                return [pscustomobject]@{
                    Valid  = $false
                    Mode   = $null
                    Detail = "CREATOR OWNER has an unexpected permission set on '$Path'; refusing to operate on it."
                }
            }
        }
        elseif (-not $isAdministrators -and -not $isSystem) {
            $disallowedRights = $rights -band (-bnot $allowedBroadRights)
            if ($disallowedRights -ne 0) {
                return [pscustomobject]@{
                    Valid  = $false
                    Mode   = $null
                    Detail = "Non-privileged principal '$($accessRule.IdentityReference)' has write/delete/ACL/ownership or other non-read/execute rights on '$Path'; refusing to operate on it."
                }
            }
        }

        if ($isAdministrators -and -not $accessRule.IsInherited -and
            (($rights -band $fullControl) -eq $fullControl)) {
            $administratorsFullControl = $true
        }
    }

    $ownerSid = Resolve-StorageSecurityIdentity -IdentityReference $acl.Owner
    $hasInheritedRules = @($acl.Access | Where-Object { $_.IsInherited }).Count -gt 0
    if ([bool]$acl.AreAccessRulesProtected -and -not $hasInheritedRules -and
        $ownerSid -eq $administratorsSid -and $administratorsFullControl) {
        return [pscustomobject]@{
            Valid  = $true
            Mode   = 'CanonicalProtectedAdministrators'
            Detail = "Canonical protected ACL is present on '$Path': BUILTIN\\Administrators owns the directory and has explicit Full Control; broad principals are limited to read/execute-style access."
        }
    }

    return [pscustomobject]@{
        Valid  = $false
        Mode   = $null
        Detail = "Storage ACL on '$Path' is neither an inherited SYSTEM Full Control directory nor a protected Canonical Administrators-owned ACL; refusing to broaden ACLs."
    }
}

function ConvertTo-NormalizedPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not [System.IO.Path]::IsPathRooted($Path)) {
        throw "Path '$Path' is not absolute."
    }

    return [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
}

function Assert-PathChainSafe {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string[]]$Paths
    )

    foreach ($path in $Paths) {
        $fullPath = ConvertTo-NormalizedPath -Path $path
        $driveRoot = [System.IO.Path]::GetPathRoot($fullPath)
        if ([string]::IsNullOrWhiteSpace($driveRoot)) {
            throw "Unable to determine a drive root for '$fullPath'."
        }

        $currentPath = $driveRoot
        $components = $fullPath.Substring($driveRoot.Length) -split '[\\/]'
        $components = @($components | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        $chain = @($driveRoot) + $components

        foreach ($component in $chain) {
            if ($component -ne $driveRoot) {
                $currentPath = Join-Path -Path $currentPath -ChildPath $component
            }

            try {
                $item = Get-Item -LiteralPath $currentPath -Force -ErrorAction Stop
            }
            catch [System.Management.Automation.ItemNotFoundException] {
                break
            }
            catch {
                throw "Unable to inspect path component '$currentPath': $($_.Exception.Message)"
            }

            if ($null -eq $item) {
                break
            }

            if (([System.IO.FileAttributes]$item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Path safety check rejected reparse point '$currentPath'; refusing junction/symlink traversal."
            }
        }
    }
}

function Get-MultipassStorageEnvironment {
    [CmdletBinding()]
    param()

    $scopeDefinitions = @(
        [pscustomobject]@{ Name = 'Machine'; Target = [System.EnvironmentVariableTarget]::Machine },
        [pscustomobject]@{ Name = 'User'; Target = [System.EnvironmentVariableTarget]::User },
        [pscustomobject]@{ Name = 'Process'; Target = [System.EnvironmentVariableTarget]::Process }
    )

    foreach ($scopeDefinition in $scopeDefinitions) {
        $value = [System.Environment]::GetEnvironmentVariable('MULTIPASS_STORAGE', $scopeDefinition.Target)
        $normalizedValue = $null
        $errorMessage = $null
        $isSet = -not [string]::IsNullOrWhiteSpace($value)
        if ($isSet) {
            try {
                $normalizedValue = ConvertTo-NormalizedPath -Path $value
            }
            catch {
                $errorMessage = $_.Exception.Message
            }
        }

        [pscustomobject]@{
            Scope           = $scopeDefinition.Name
            Value           = $value
            IsSet           = $isSet
            NormalizedValue = $normalizedValue
            Error           = $errorMessage
        }
    }
}

function Assert-MultipassStorageEnvironment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$TargetStorage
    )

    $target = ConvertTo-NormalizedPath -Path $TargetStorage
    $scopes = @(Get-MultipassStorageEnvironment)
    $invalidScopes = @($scopes | Where-Object { $_.IsSet -and $null -ne $_.Error })
    if ($invalidScopes.Count -gt 0) {
        $details = ($invalidScopes | ForEach-Object { "$($_.Scope)='$($_.Value)': $($_.Error)" }) -join '; '
        throw "MULTIPASS_STORAGE contains invalid path values: $details"
    }

    $mismatchedScopes = @($scopes | Where-Object {
            $_.IsSet -and -not $_.NormalizedValue.Equals($target, [System.StringComparison]::OrdinalIgnoreCase)
        })
    if ($mismatchedScopes.Count -gt 0) {
        $details = ($mismatchedScopes | ForEach-Object { "$($_.Scope)='$($_.Value)'" }) -join '; '
        throw "MULTIPASS_STORAGE mismatch for exact target '$TargetStorage': $details. Refusing migration or overwrite."
    }

    return $scopes
}

function Assert-MultipassOperationalContext {
    [CmdletBinding()]
    param()

    $targetStorage = Get-StoragePath
    Assert-PathChainSafe -Paths @((Get-WorkspaceRoot), (Split-Path -Path $targetStorage -Parent), $targetStorage)

    if (-not (Test-Path -LiteralPath $targetStorage -PathType Container)) {
        throw "Multipass storage target '$targetStorage' is not an existing directory; refusing to run the CLI."
    }

    $security = Get-StorageSecurityStatus -Path $targetStorage
    if (-not $security.Valid) {
        throw "Multipass storage target '$targetStorage' failed the SYSTEM ACL check: $($security.Detail)"
    }

    $scopes = @(Assert-MultipassStorageEnvironment -TargetStorage $targetStorage)
    $machineScope = @($scopes | Where-Object { $_.Scope -eq 'Machine' })[0]
    if ($null -eq $machineScope -or -not $machineScope.IsSet) {
        throw "Machine MULTIPASS_STORAGE is unset; refusing to run Multipass without the exact storage target '$targetStorage'."
    }

    if ($null -eq $machineScope.Error -and -not $machineScope.NormalizedValue.Equals((ConvertTo-NormalizedPath -Path $targetStorage), [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Machine MULTIPASS_STORAGE does not resolve to the exact storage target '$targetStorage'; refusing to run Multipass."
    }

    return [pscustomobject]@{
        StoragePath = $targetStorage
        Scopes      = $scopes
    }
}

function Get-MultipassCommand {
    [CmdletBinding()]
    param()

    $command = Get-Command -Name 'multipass' -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        $expectedInstallPath = 'C:\Program Files\Multipass\bin\multipass.exe'
        if (Test-Path -LiteralPath $expectedInstallPath -PathType Leaf) {
            return $expectedInstallPath
        }

        throw "Multipass was not found on PATH or at '$expectedInstallPath'. Run '$script:WorkspaceRoot\scripts\install-host.ps1' from an elevated PowerShell session."
    }

    if ($command.CommandType -notin @('Application', 'ExternalScript')) {
        throw "The command named 'multipass' is not an executable command."
    }

    if ([string]::IsNullOrWhiteSpace([string]$command.Source)) {
        return [string]$command.Path
    }

    return [string]$command.Source
}

function Assert-MultipassAvailable {
    [CmdletBinding()]
    param()

    [void](Get-MultipassCommand)
}

function Invoke-MultipassCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [ValidateNotNullOrEmpty()]
        [string[]]$Arguments,

        [switch]$AllowFailure
    )

    [void](Assert-MultipassOperationalContext)
    $commandPath = Get-MultipassCommand
    Write-Verbose ("multipass " + ($Arguments -join ' '))
    $output = @(& $commandPath @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $result = [pscustomobject]@{
        ExitCode = $exitCode
        Output   = $output
    }

    if (-not $AllowFailure -and $exitCode -ne 0) {
        $details = (($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine).Trim()
        if ([string]::IsNullOrWhiteSpace($details)) {
            $details = 'Multipass returned no diagnostic output.'
        }

        throw "multipass $($Arguments -join ' ') failed with exit code $exitCode. $details"
    }

    return $result
}

function Invoke-MultipassInteractive {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [ValidateNotNullOrEmpty()]
        [string[]]$Arguments
    )

    [void](Assert-MultipassOperationalContext)
    $commandPath = Get-MultipassCommand
    Write-Verbose ("multipass " + ($Arguments -join ' '))
    & $commandPath @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "multipass $($Arguments -join ' ') failed with exit code $exitCode."
    }
}

function Get-MultipassInstanceRecord {
    [CmdletBinding()]
    param(
        [string]$Name = (Get-VmName)
    )

    Assert-MultipassIdentifier -Name $Name
    if ($Name -ieq 'primary') {
        throw "The instance name '$Name' is reserved by Multipass and is not allowed."
    }

    $result = Invoke-MultipassCommand -Arguments @('list', '--format', 'json')
    $jsonText = (($result.Output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine).Trim()
    if ([string]::IsNullOrWhiteSpace($jsonText)) {
        return $null
    }

    try {
        $document = $jsonText | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Multipass returned invalid JSON from 'list --format json': $($_.Exception.Message)"
    }

    $listProperty = $document.PSObject.Properties['list']
    if ($null -eq $listProperty) {
        return $null
    }

    foreach ($candidate in @($listProperty.Value)) {
        if ([string]$candidate.name -ceq $Name) {
            return $candidate
        }
    }

    return $null
}

function Get-MultipassInstanceState {
    [CmdletBinding()]
    param(
        [string]$Name = (Get-VmName)
    )

    $record = Get-MultipassInstanceRecord -Name $Name
    if ($null -eq $record) {
        return $null
    }

    return [string]$record.state
}

function Wait-MultipassInstanceState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$ExpectedState,

        [int]$TimeoutSeconds = [int]$script:VmConfig.TimeoutSeconds,

        [string]$Name = (Get-VmName)
    )

    if ($TimeoutSeconds -le 0) {
        throw 'TimeoutSeconds must be a positive number.'
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $state = Get-MultipassInstanceState -Name $Name
        if ($null -ne $state -and $ExpectedState -contains $state) {
            return $state
        }

        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)

    $lastState = Get-MultipassInstanceState -Name $Name
    throw "Timed out after $TimeoutSeconds seconds waiting for '$Name' to reach [$($ExpectedState -join ', ')]. Last state: '$lastState'."
}

function Wait-MultipassCloudInit {
    [CmdletBinding()]
    param(
        [int]$TimeoutSeconds = [int]$script:VmConfig.TimeoutSeconds,

        [string]$Name = (Get-VmName)
    )

    if ($TimeoutSeconds -le 0) {
        throw 'TimeoutSeconds must be a positive number.'
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $result = Invoke-MultipassCommand -Arguments @('exec', $Name, '--', 'cloud-init', 'status', '--long') -AllowFailure
        $statusText = (($result.Output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine)

        if ($statusText -match '(?im)^status:\s*done\b') {
            return
        }

        if ($statusText -match '(?im)^status:\s*(error|disabled)\b' -or $result.ExitCode -ge 2) {
            throw "cloud-init did not complete successfully for '$Name'. $($statusText.Trim())"
        }

        Start-Sleep -Seconds 5
    } while ((Get-Date) -lt $deadline)

    throw "Timed out after $TimeoutSeconds seconds waiting for cloud-init on '$Name'."
}

function Ensure-MultipassDefaults {
    [CmdletBinding()]
    param()

    [void](Invoke-MultipassCommand -Arguments @('set', 'local.driver=hyperv'))
    [void](Invoke-MultipassCommand -Arguments @('set', 'local.privileged-mounts=false'))
}

function Assert-MultipassInstanceExists {
    [CmdletBinding()]
    param(
        [string]$Name = (Get-VmName)
    )

    $record = Get-MultipassInstanceRecord -Name $Name
    if ($null -eq $record) {
        throw "Multipass instance '$Name' does not exist. Run '$script:WorkspaceRoot\scripts\create.ps1'."
    }

    return $record
}

function Assert-MultipassInstanceRunning {
    [CmdletBinding()]
    param(
        [string]$Name = (Get-VmName)
    )

    $record = Assert-MultipassInstanceExists -Name $Name
    if ([string]$record.state -ne 'RUNNING') {
        throw "Multipass instance '$Name' must be RUNNING for this operation; current state is '$($record.state)'."
    }

    return $record
}

function Assert-SnapshotName {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($Name -notmatch '^[A-Za-z](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$') {
        throw "Snapshot name '$Name' must start with a letter, end with a letter or number, and contain only letters, numbers, and hyphens."
    }
}

function Assert-MultipassSnapshotExists {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$InstanceName,

        [Parameter(Mandatory = $true)]
        [string]$SnapshotName
    )

    Assert-MultipassIdentifier -Name $InstanceName
    Assert-SnapshotName -Name $SnapshotName
    $target = "$InstanceName.$SnapshotName"
    $result = Invoke-MultipassCommand -Arguments @('list', '--snapshots', '--format', 'json') -AllowFailure
    if ($result.ExitCode -ne 0) {
        throw "Unable to preflight exact snapshot '$target'; Multipass snapshot listing failed with exit code $($result.ExitCode). Refusing to stop or restore."
    }

    $jsonText = (($result.Output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine).Trim()
    if ([string]::IsNullOrWhiteSpace($jsonText)) {
        throw "Unable to preflight exact snapshot '$target'; Multipass returned no structured snapshot data. Refusing to stop or restore."
    }

    try {
        $document = $jsonText | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Unable to preflight exact snapshot '$target'; Multipass returned invalid snapshot JSON. Refusing to stop or restore."
    }

    function Test-SnapshotNode {
        param(
            [object]$Node,
            [string]$ExpectedInstance,
            [string]$ExpectedSnapshot,
            [string]$ExpectedName
        )

        if ($null -eq $Node) {
            return $false
        }

        if ($Node -is [System.Collections.IEnumerable] -and $Node -isnot [string]) {
            foreach ($child in $Node) {
                if (Test-SnapshotNode -Node $child -ExpectedInstance $ExpectedInstance -ExpectedSnapshot $ExpectedSnapshot -ExpectedName $ExpectedName) {
                    return $true
                }
            }

            return $false
        }

        $properties = @($Node.PSObject.Properties)
        $nameValue = [string](@($properties | Where-Object { $_.Name -ieq 'name' } | Select-Object -First 1).Value)
        $instanceValue = [string](@($properties | Where-Object { $_.Name -ieq 'instance' } | Select-Object -First 1).Value)
        $snapshotValue = [string](@($properties | Where-Object { $_.Name -in @('snapshot', 'snapshot_name') } | Select-Object -First 1).Value)
        if ($nameValue -ceq $ExpectedName -or
            ($instanceValue -ceq $ExpectedInstance -and ($nameValue -ceq $ExpectedSnapshot -or $snapshotValue -ceq $ExpectedSnapshot))) {
            return $true
        }

        foreach ($property in $properties) {
            if ($property.Name -ieq 'name' -and [string]$property.Value -ceq $ExpectedName) {
                return $true
            }

            if (Test-SnapshotNode -Node $property.Value -ExpectedInstance $ExpectedInstance -ExpectedSnapshot $ExpectedSnapshot -ExpectedName $ExpectedName) {
                return $true
            }
        }

        return $false
    }

    $found = Test-SnapshotNode -Node $document -ExpectedInstance $InstanceName -ExpectedSnapshot $SnapshotName -ExpectedName $target
    if (-not $found) {
        $exactPattern = [regex]::Escape($target)
        $found = $jsonText -match "(?<![A-Za-z0-9_.-])$exactPattern(?![A-Za-z0-9_.-])"
    }

    if (-not $found) {
        throw "Exact snapshot '$target' was not found; refusing to stop or restore."
    }
}
