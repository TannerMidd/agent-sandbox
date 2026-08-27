[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$AppPath,
    [int]$TimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$serverPath = Join-Path ${env:ProgramFiles(x86)} 'Windows Application Driver\WinAppDriver.exe'
if (!(Test-Path -LiteralPath $serverPath -PathType Leaf)) { throw "WinAppDriver was not found at $serverPath." }
$server = $null
$sessionId = $null
$baseUri = 'http://127.0.0.1:4723'

function Invoke-WebDriver {
    param([string]$Method, [string]$Path, [object]$Body)
    $parameters = @{ Method = $Method; Uri = "$baseUri$Path"; ContentType = 'application/json'; TimeoutSec = 30 }
    if ($null -ne $Body) { $parameters.Body = ConvertTo-Json $Body -Depth 8 -Compress }
    return Invoke-RestMethod @parameters
}

function Find-ElementByName {
    param([string]$Name, [datetime]$Deadline)
    do {
        try {
            $response = Invoke-WebDriver -Method Post -Path "/session/$sessionId/element" -Body @{ using = 'name'; value = $Name }
            $value = $response.value
            $legacy = $value.PSObject.Properties['ELEMENT']
            $w3c = $value.PSObject.Properties['element-6066-11e4-a52e-4f735466cecf']
            $id = if ($null -ne $legacy) { [string]$legacy.Value } elseif ($null -ne $w3c) { [string]$w3c.Value } else { $null }
            if (![string]::IsNullOrWhiteSpace($id)) { return $id }
        }
        catch { Start-Sleep -Milliseconds 250 }
    } while ([datetime]::UtcNow -lt $Deadline)
    throw "WinAppDriver could not find '$Name' before the timeout."
}

try {
    $server = Start-Process -FilePath $serverPath -ArgumentList @('127.0.0.1', '4723') -PassThru -WindowStyle Hidden
    $deadline = [datetime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try { $null = Invoke-RestMethod -Method Get -Uri "$baseUri/status" -TimeoutSec 2; break }
        catch { Start-Sleep -Milliseconds 250 }
    } while ([datetime]::UtcNow -lt $deadline)
    if ($server.HasExited) { throw "WinAppDriver exited with code $($server.ExitCode)." }

    $resolvedApp = (Resolve-Path -LiteralPath $AppPath).Path
    $session = Invoke-WebDriver -Method Post -Path '/session' -Body @{
        desiredCapabilities = @{ app = $resolvedApp; platformName = 'Windows'; deviceName = 'WindowsPC' }
        capabilities = @{ alwaysMatch = @{ platformName = 'Windows'; 'appium:deviceName' = 'WindowsPC'; 'appium:app' = $resolvedApp } }
    }
    $legacySession = $session.PSObject.Properties['sessionId']
    $sessionId = if ($null -ne $legacySession) { [string]$legacySession.Value } else { [string]$session.value.sessionId }
    if ([string]::IsNullOrWhiteSpace($sessionId)) { throw 'WinAppDriver did not return a session ID.' }

    foreach ($name in @('AGENT SANDBOX', 'DEVELOPMENT PREVIEW', 'Dashboard', 'Files', 'Snapshots & Recovery', 'Diagnostics', 'Settings', 'New VM')) {
        $null = Find-ElementByName -Name $name -Deadline $deadline
    }
    $settingsId = Find-ElementByName -Name 'Settings' -Deadline $deadline
    $null = Invoke-WebDriver -Method Post -Path "/session/$sessionId/element/$settingsId/click" -Body @{}
    $null = Find-ElementByName -Name 'Updates & privacy' -Deadline $deadline
    Write-Host 'WinAppDriver launch, accessibility-name, and navigation interaction smoke test passed.'
}
finally {
    if (![string]::IsNullOrWhiteSpace($sessionId)) {
        try { $null = Invoke-WebDriver -Method Delete -Path "/session/$sessionId" -Body $null } catch { }
    }
    if ($null -ne $server -and !$server.HasExited) { Stop-Process -Id $server.Id -Force }
}
