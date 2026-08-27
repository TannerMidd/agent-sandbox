[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$version = '1.2.1'
$expectedSha256 = 'A76A8F4E44B29BAD331ACF6B6C248FCC65324F502F28826AD2ACD5F3C80857FE'
$url = "https://github.com/microsoft/WinAppDriver/releases/download/v$version/WindowsApplicationDriver_$version.msi"
$temporaryRoot = if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) { [IO.Path]::GetTempPath() } else { $env:RUNNER_TEMP }
$directory = Join-Path $temporaryRoot 'agent-sandbox-winappdriver'
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$installer = Join-Path $directory "WindowsApplicationDriver_$version.msi"
Invoke-WebRequest -Uri $url -OutFile $installer
$actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $installer).Hash
if (![System.Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
    [System.Text.Encoding]::ASCII.GetBytes($actual),
    [System.Text.Encoding]::ASCII.GetBytes($expectedSha256))) {
    throw 'WinAppDriver installer did not match the pinned SHA-256 value.'
}
New-Item -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -Force | Out-Null
New-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -Name AllowDevelopmentWithoutDevLicense -PropertyType DWord -Value 1 -Force | Out-Null
$process = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -ArgumentList @('/i', $installer, '/qn', '/norestart') -Wait -PassThru
if ($process.ExitCode -notin @(0, 3010)) { throw "WinAppDriver installation failed with code $($process.ExitCode)." }
