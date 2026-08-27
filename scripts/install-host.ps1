#requires -Version 5.1

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param()

Set-StrictMode -Version Latest
. (Join-Path -Path $PSScriptRoot -ChildPath 'common.ps1')

function Assert-Administrator {
    if ($env:OS -ne 'Windows_NT') {
        throw 'install-host.ps1 is supported only on Windows.'
    }

    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'install-host.ps1 must be run from an elevated PowerShell session.'
    }
}

function Assert-CanonicalControlPlaneScaffolding {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    Assert-PathChainSafe -Paths @($Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "Canonical control-plane data path '$Path' is not an existing directory."
    }

    $security = Get-StorageSecurityStatus -Path $Path
    if (-not $security.Valid -or $security.Mode -ne 'CanonicalProtectedAdministrators') {
        throw "Canonical control-plane data path '$Path' does not have the protected Administrators-owned ACL required for the certificate-only scaffold; observed mode '$($security.Mode)'."
    }

    try {
        $entries = @(Get-ChildItem -LiteralPath $Path -Force -ErrorAction Stop)
    }
    catch {
        throw "Unable to inspect Canonical control-plane data path '$Path': $($_.Exception.Message)"
    }

    if ($entries.Count -ne 1) {
        throw "Canonical control-plane data path '$Path' must contain exactly one entry named 'multipass_root_cert.pem'; observed $($entries.Count) entries."
    }

    $certificate = $entries[0]
    $isReparsePoint = (([System.IO.FileAttributes]$certificate.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)
    if ($certificate.PSIsContainer -or $isReparsePoint -or [string]$certificate.Name -ine 'multipass_root_cert.pem') {
        throw "Canonical control-plane data path '$Path' contains an unexpected, non-regular, or reparse entry '$($certificate.Name)'; refusing to treat it as certificate-only scaffolding."
    }

    $certificateLength = [long]$certificate.Length
    if ($certificateLength -le 0 -or $certificateLength -gt 1MB) {
        throw "Canonical control-plane certificate '$($certificate.FullName)' has size $certificateLength bytes outside the sensible 1-byte-to-1MiB bound."
    }
}

function Assert-NoConflictingMultipassState {
    param(
        [Parameter(Mandatory = $true)]
        [string]$TargetStorage,

        [switch]$AllowCanonicalControlPlaneScaffolding
    )

    [void](Assert-MultipassStorageEnvironment -TargetStorage $TargetStorage)
    $target = ConvertTo-NormalizedPath -Path $TargetStorage

    $programData = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::CommonApplicationData)
    if (-not [string]::IsNullOrWhiteSpace($programData)) {
        $defaultData = Join-Path -Path $programData -ChildPath 'Multipass\data'
        if (Test-Path -LiteralPath $defaultData) {
            Assert-PathChainSafe -Paths @($defaultData)
            $normalizedDefault = ConvertTo-NormalizedPath -Path $defaultData
            if (-not $normalizedDefault.Equals($target, [System.StringComparison]::OrdinalIgnoreCase)) {
                if ($AllowCanonicalControlPlaneScaffolding) {
                    Assert-CanonicalControlPlaneScaffolding -Path $defaultData
                    return
                }

                throw "Existing Multipass data was found at '$defaultData', which conflicts with '$TargetStorage'. Automatic migration is refused."
            }
        }
    }

}

function Assert-StorageDirectoryReady {
    param(
        [Parameter(Mandatory = $true)]
        [string]$TargetStorage,

        [switch]$RequireEmpty,

        [switch]$RequireInheritedSystem
    )

    Assert-PathChainSafe -Paths @((Get-WorkspaceRoot), (Split-Path -Path $TargetStorage -Parent), $TargetStorage)

    if (-not (Test-Path -LiteralPath $TargetStorage)) {
        throw "Required Multipass storage directory '$TargetStorage' does not exist."
    }

    if (-not (Test-Path -LiteralPath $TargetStorage -PathType Container)) {
        throw "Multipass storage target '$TargetStorage' exists but is not a directory."
    }

    $security = Get-StorageSecurityStatus -Path $TargetStorage -RequireInheritedSystem:$RequireInheritedSystem
    if (-not $security.Valid) {
        throw $security.Detail
    }

    if ($RequireEmpty) {
        try {
            $entries = @(Get-ChildItem -LiteralPath $TargetStorage -Force -ErrorAction Stop)
        }
        catch {
            throw "Unable to inspect the contents of '$TargetStorage': $($_.Exception.Message)"
        }

        if ($entries.Count -gt 0) {
            throw "Storage directory '$TargetStorage' is nonempty, but no validated matching Multipass installation was found; refusing to reuse or migrate it."
        }
    }
}

function Ensure-NewInstallStorage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$TargetStorage
    )

    Assert-PathChainSafe -Paths @((Get-WorkspaceRoot), (Split-Path -Path $TargetStorage -Parent), $TargetStorage)

    if (Test-Path -LiteralPath $TargetStorage) {
        Assert-StorageDirectoryReady -TargetStorage $TargetStorage -RequireEmpty -RequireInheritedSystem
        return $true
    }

    if (-not $PSCmdlet.ShouldProcess($TargetStorage, 'Create the exact Multipass storage directory')) {
        return $false
    }

    try {
        New-Item -ItemType Directory -Force -Path $TargetStorage -ErrorAction Stop | Out-Null
    }
    catch {
        throw "Unable to create exact Multipass storage directory '$TargetStorage': $($_.Exception.Message)"
    }

    Assert-PathChainSafe -Paths @((Get-WorkspaceRoot), (Split-Path -Path $TargetStorage -Parent), $TargetStorage)
    Assert-StorageDirectoryReady -TargetStorage $TargetStorage -RequireEmpty -RequireInheritedSystem
    return $true
}

function Get-ExistingMultipassCommand {
    try {
        return Get-MultipassCommand
    }
    catch {
        return $null
    }
}

function Assert-MachineStorageMatches {
    param(
        [Parameter(Mandatory = $true)]
        [string]$TargetStorage
    )

    $scopes = @(Assert-MultipassStorageEnvironment -TargetStorage $TargetStorage)
    $machineScope = @($scopes | Where-Object { $_.Scope -eq 'Machine' })[0]
    if (-not $machineScope.IsSet) {
        throw "An existing Multipass installation was found, but machine MULTIPASS_STORAGE is unset; refusing ambiguous migration. Review the installation before retrying."
    }

    return $machineScope
}

function Get-MultipassService {
    foreach ($serviceName in @('Multipass', 'multipass')) {
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($null -ne $service) {
            return $service
        }
    }

    return $null
}

function Assert-MultipassServiceRunning {
    param(
        [int]$TimeoutSeconds = 30
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastService = $null
    do {
        $lastService = Get-MultipassService
        if ($null -ne $lastService -and [string]$lastService.Status -eq 'Running') {
            return
        }

        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)

    if ($null -eq $lastService) {
        throw "Multipass service was not found after ${TimeoutSeconds}s. No automatic service start or migration was attempted."
    }

    throw "Multipass service '$($lastService.Name)' did not reach Running state within ${TimeoutSeconds}s; observed '$($lastService.Status)'. No automatic service start was attempted."
}

function Assert-InstalledMultipassState {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExpectedInstallPath,

        [Parameter(Mandatory = $true)]
        [string]$TargetStorage
    )

    try {
        if (-not (Test-Path -LiteralPath $ExpectedInstallPath -PathType Leaf)) {
            throw "expected executable '$ExpectedInstallPath' was not found"
        }

        Assert-MachineStorageMatches -TargetStorage $TargetStorage
        Assert-StorageDirectoryReady -TargetStorage $TargetStorage
        Assert-MultipassServiceRunning -TimeoutSeconds 30
    }
    catch {
        throw "Post-install verification failed: $($_.Exception.Message) Recovery: inspect the MSI result and host state before retrying; no VM purge was attempted."
    }
}

function Get-LatestStableMultipassAsset {
    $apiUri = 'https://api.github.com/repos/canonical/multipass/releases/latest'
    $headers = @{ 'User-Agent' = 'agent-dev-multipass-installer' }

    try {
        $release = Invoke-RestMethod -Uri $apiUri -Headers $headers -Method Get -ErrorAction Stop
    }
    catch {
        throw "Unable to query the latest stable Canonical Multipass release: $($_.Exception.Message)"
    }

    if ([bool]$release.draft -or [bool]$release.prerelease) {
        throw "GitHub did not return a stable release from '$apiUri'."
    }

    $asset = @($release.assets) |
        Where-Object { ([string]$_.name) -match '(?i)^multipass.*win-win64\.msi$' } |
        Select-Object -First 1

    if ($null -eq $asset) {
        throw "The stable release '$($release.tag_name)' did not contain a Multipass Windows .msi asset."
    }

    $downloadUrl = [string]$asset.browser_download_url
    if ($downloadUrl -notmatch '^https://github\.com/canonical/multipass/releases/download/') {
        throw "The release asset URL '$downloadUrl' is not a canonical Multipass release URL."
    }

    return [pscustomobject]@{
        Tag        = [string]$release.tag_name
        Name       = [string]$asset.name
        DownloadUrl = $downloadUrl
    }
}

function Assert-CanonicalAuthenticodeSignature {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $signature = Get-AuthenticodeSignature -FilePath $Path
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "Authenticode validation failed for '$Path': status '$($signature.Status)', status message '$($signature.StatusMessage)'."
    }

    if ($null -eq $signature.SignerCertificate) {
        throw "Authenticode reported a valid signature for '$Path' without a signer certificate."
    }

    $certificate = $signature.SignerCertificate
    $simpleName = $certificate.GetNameInfo([System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false)
    $publisherText = "$simpleName $($certificate.Subject)"
    if ($publisherText -notmatch '(?i)Canonical') {
        throw "The signer for '$Path' is not Canonical. Certificate identity: $publisherText"
    }
}

Assert-Administrator
$targetStorage = Get-StoragePath
$expectedInstallPath = 'C:\Program Files\Multipass\bin\multipass.exe'
Assert-PathChainSafe -Paths @((Get-WorkspaceRoot), (Split-Path -Path $targetStorage -Parent), $targetStorage)
$existingCommand = Get-ExistingMultipassCommand

if ($null -ne $existingCommand) {
    $normalizedExistingCommand = ConvertTo-NormalizedPath -Path ([string]$existingCommand)
    $normalizedExpectedInstallPath = ConvertTo-NormalizedPath -Path $expectedInstallPath
    if (-not $normalizedExistingCommand.Equals($normalizedExpectedInstallPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "An existing Multipass executable was found at '$existingCommand', not the expected '$expectedInstallPath'; refusing ambiguous reinstall or migration."
    }

    Assert-MachineStorageMatches -TargetStorage $targetStorage
    Assert-StorageDirectoryReady -TargetStorage $targetStorage
    Assert-MultipassServiceRunning -TimeoutSeconds 30
    Assert-NoConflictingMultipassState -TargetStorage $targetStorage -AllowCanonicalControlPlaneScaffolding
    Write-Host "Existing Multipass installation verified at '$expectedInstallPath' with storage '$targetStorage'; no installation or migration was performed."
    return
}

Assert-NoConflictingMultipassState -TargetStorage $targetStorage

if (-not (Ensure-NewInstallStorage -TargetStorage $targetStorage)) {
    return
}

$asset = Get-LatestStableMultipassAsset
$downloadPath = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath ("multipass-{0}.msi" -f ([guid]::NewGuid().ToString('N')))
$priorMachineStorage = [System.Environment]::GetEnvironmentVariable('MULTIPASS_STORAGE', [System.EnvironmentVariableTarget]::Machine)
$priorProcessStorage = [System.Environment]::GetEnvironmentVariable('MULTIPASS_STORAGE', [System.EnvironmentVariableTarget]::Process)
$priorProcessScope = @((Get-MultipassStorageEnvironment | Where-Object { $_.Scope -eq 'Process' }))[0]
$machineStorageChanged = $false
$processStorageChanged = $false
$installationSucceeded = $false
$primaryError = $null
$rollbackDiagnostics = @()
$cleanupDiagnostics = @()

try {
    if (-not $PSCmdlet.ShouldProcess($downloadPath, "Download Canonical Multipass $($asset.Tag) installer")) {
        return
    }

    Invoke-WebRequest -Uri $asset.DownloadUrl -OutFile $downloadPath -UseBasicParsing -ErrorAction Stop
    Assert-CanonicalAuthenticodeSignature -Path $downloadPath

    $currentMachineStorage = $priorMachineStorage
    if ([string]::IsNullOrWhiteSpace($currentMachineStorage) -or
        -not (ConvertTo-NormalizedPath -Path ([string]$currentMachineStorage)).Equals(
            (ConvertTo-NormalizedPath -Path $targetStorage),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        if ($PSCmdlet.ShouldProcess("Machine MULTIPASS_STORAGE=$targetStorage", 'Set the Multipass storage location before installation')) {
            [System.Environment]::SetEnvironmentVariable('MULTIPASS_STORAGE', $targetStorage, [System.EnvironmentVariableTarget]::Machine)
            $machineStorageChanged = $true
        }
        else {
            return
        }
    }

    if (-not $priorProcessScope.IsSet) {
        $env:MULTIPASS_STORAGE = $targetStorage
        $processStorageChanged = $true
    }

    $msiArguments = "/i `"$downloadPath`" /qn /norestart"
    if ($PSCmdlet.ShouldProcess("Canonical Multipass $($asset.Tag)", 'Install the signed MSI')) {
        $process = Start-Process -FilePath 'msiexec.exe' -ArgumentList $msiArguments -Wait -PassThru -ErrorAction Stop
        if ($process.ExitCode -notin @(0, 3010)) {
            throw "Multipass MSI installation failed with exit code $($process.ExitCode)."
        }

        $installationSucceeded = $true
        Assert-InstalledMultipassState -ExpectedInstallPath $expectedInstallPath -TargetStorage $targetStorage

        if ($process.ExitCode -eq 3010) {
            Write-Warning 'Multipass installed successfully but Windows reports that a restart may be required.'
        }
        else {
            Write-Host "Multipass $($asset.Tag) installed. Storage: $targetStorage"
        }
    }
}
catch {
    $primaryError = $_
    throw
}
finally {
    if (-not $installationSucceeded) {
        if ($machineStorageChanged) {
            try {
                [System.Environment]::SetEnvironmentVariable('MULTIPASS_STORAGE', $priorMachineStorage, [System.EnvironmentVariableTarget]::Machine)
            }
            catch {
                $rollbackDiagnostics += "Machine MULTIPASS_STORAGE rollback failed: $($_.Exception.Message)"
            }
        }

        if ($processStorageChanged) {
            try {
                [System.Environment]::SetEnvironmentVariable('MULTIPASS_STORAGE', $priorProcessStorage, [System.EnvironmentVariableTarget]::Process)
            }
            catch {
                $rollbackDiagnostics += "Process MULTIPASS_STORAGE rollback failed: $($_.Exception.Message)"
            }
        }
    }

    if (Test-Path -LiteralPath $downloadPath) {
        try {
            Remove-Item -LiteralPath $downloadPath -Force -ErrorAction Stop
        }
        catch {
            $cleanupDiagnostics += "Temporary MSI cleanup failed for exact path '$downloadPath': $($_.Exception.Message)"
        }
    }

    $diagnostics = @($rollbackDiagnostics + $cleanupDiagnostics)
    if ($diagnostics.Count -gt 0) {
        $diagnosticText = $diagnostics -join ' '
        if ($null -ne $primaryError) {
            Write-Warning "Primary installer error was preserved. Additional cleanup/rollback diagnostics: $diagnosticText"
        }
        else {
            throw "Installer cleanup/rollback failed: $diagnosticText"
        }
    }
}
