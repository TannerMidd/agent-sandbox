#requires -Version 5.1

[CmdletBinding()]
param(
    [switch]$SmokeTest,
    [string]$RenderPreviewPath
)

Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$workspace = [System.IO.Path]::GetFullPath($PSScriptRoot)
$scriptsDirectory = Join-Path $workspace 'scripts'
$powershellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$vmConfig = Import-PowerShellDataFile -LiteralPath (Join-Path $workspace 'vm.config.psd1')
$guestInbox = '/home/ubuntu/work/inbox'

foreach ($scriptName in @('start.ps1', 'stop.ps1', 'status.ps1', 'enter.ps1', 'copy-in-batch.ps1')) {
    $scriptPath = Join-Path $scriptsDirectory $scriptName
    if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
        [System.Windows.Forms.MessageBox]::Show("Required script was not found: $scriptPath", 'Agent Dev VM', 'OK', 'Error') | Out-Null
        exit 1
    }
}

[System.Windows.Forms.Application]::EnableVisualStyles()

$theme = @{
    Canvas        = [Drawing.Color]::FromArgb(11, 15, 20)
    Header        = [Drawing.Color]::FromArgb(14, 20, 29)
    Surface       = [Drawing.Color]::FromArgb(18, 25, 36)
    SurfaceRaised = [Drawing.Color]::FromArgb(24, 33, 47)
    Border        = [Drawing.Color]::FromArgb(42, 55, 75)
    Primary       = [Drawing.Color]::FromArgb(91, 140, 255)
    PrimaryHover  = [Drawing.Color]::FromArgb(112, 155, 255)
    Text          = [Drawing.Color]::FromArgb(242, 246, 252)
    Muted         = [Drawing.Color]::FromArgb(151, 163, 181)
    Faint         = [Drawing.Color]::FromArgb(105, 119, 139)
    Success       = [Drawing.Color]::FromArgb(57, 217, 138)
    Warning       = [Drawing.Color]::FromArgb(244, 184, 96)
    Danger        = [Drawing.Color]::FromArgb(255, 107, 122)
}

function New-UiFont {
    param([float]$Size, [Drawing.FontStyle]$Style = 'Regular', [string]$Family = 'Segoe UI')
    New-Object Drawing.Font($Family, $Size, $Style)
}

function New-Label {
    param(
        [Parameter(Mandatory)][string]$Text,
        [int]$X, [int]$Y, [int]$Width = 0, [int]$Height = 0,
        [float]$FontSize = 9.5, [Drawing.Color]$Color = $theme.Text,
        [Drawing.FontStyle]$FontStyle = 'Regular'
    )

    $label = New-Object Windows.Forms.Label
    $label.Text = $Text
    $label.Location = New-Object Drawing.Point($X, $Y)
    $label.ForeColor = $Color
    $label.BackColor = [Drawing.Color]::Transparent
    $label.Font = New-UiFont $FontSize $FontStyle
    if ($Width -gt 0 -and $Height -gt 0) { $label.Size = New-Object Drawing.Size($Width, $Height) }
    else { $label.AutoSize = $true }
    $label
}

function New-ActionButton {
    param(
        [Parameter(Mandatory)][string]$Text,
        [int]$Width = 116,
        [ValidateSet('Primary', 'Secondary', 'Ghost', 'Danger')][string]$Variant = 'Secondary'
    )

    $button = New-Object Windows.Forms.Button
    $button.Text = $Text
    $button.Size = New-Object Drawing.Size($Width, 38)
    $button.FlatStyle = 'Flat'
    $button.FlatAppearance.BorderSize = 1
    $button.Font = New-UiFont 9 'Bold'
    $button.Cursor = [Windows.Forms.Cursors]::Hand
    $button.UseVisualStyleBackColor = $false
    switch ($Variant) {
        'Primary' {
            $button.BackColor = $theme.Primary; $button.ForeColor = [Drawing.Color]::White
            $button.FlatAppearance.BorderColor = $theme.Primary
            $button.FlatAppearance.MouseOverBackColor = $theme.PrimaryHover
            $button.FlatAppearance.MouseDownBackColor = $theme.Primary
        }
        'Danger' {
            $button.BackColor = $theme.SurfaceRaised; $button.ForeColor = $theme.Danger
            $button.FlatAppearance.BorderColor = $theme.Border
            $button.FlatAppearance.MouseOverBackColor = [Drawing.Color]::FromArgb(48, 36, 48)
            $button.FlatAppearance.MouseDownBackColor = $theme.Surface
        }
        'Ghost' {
            $button.BackColor = $theme.Header; $button.ForeColor = $theme.Muted
            $button.FlatAppearance.BorderColor = $theme.Border
            $button.FlatAppearance.MouseOverBackColor = $theme.SurfaceRaised
            $button.FlatAppearance.MouseDownBackColor = $theme.Surface
        }
        default {
            $button.BackColor = $theme.SurfaceRaised; $button.ForeColor = $theme.Text
            $button.FlatAppearance.BorderColor = $theme.Border
            $button.FlatAppearance.MouseOverBackColor = [Drawing.Color]::FromArgb(33, 45, 63)
            $button.FlatAppearance.MouseDownBackColor = $theme.Surface
        }
    }
    $button
}

function New-Card {
    param([int]$X, [int]$Y, [int]$Width, [int]$Height)
    $panel = New-Object Windows.Forms.Panel
    $panel.Location = New-Object Drawing.Point($X, $Y)
    $panel.Size = New-Object Drawing.Size($Width, $Height)
    $panel.BackColor = $theme.Surface
    $panel.BorderStyle = 'FixedSingle'
    $panel
}

function Enable-DarkTitleBar {
    param([IntPtr]$Handle)
    try {
        if (-not ('NativeDarkMode' -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class NativeDarkMode {
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
}
'@
        }
        $enabled = 1
        $result = [NativeDarkMode]::DwmSetWindowAttribute($Handle, 20, [ref]$enabled, 4)
        if ($result -ne 0) { [void][NativeDarkMode]::DwmSetWindowAttribute($Handle, 19, [ref]$enabled, 4) }
    }
    catch { }
}

$form = New-Object Windows.Forms.Form
$form.Text = 'Agent Dev VM'
$form.StartPosition = 'CenterScreen'
$form.ClientSize = New-Object Drawing.Size(920, 760)
$form.MinimumSize = New-Object Drawing.Size(820, 700)
$form.BackColor = $theme.Canvas
$form.ForeColor = $theme.Text
$form.Font = New-UiFont 9.5
$form.AutoScaleMode = 'Dpi'
$form.KeyPreview = $true

$toolTip = New-Object Windows.Forms.ToolTip
$toolTip.AutoPopDelay = 7000; $toolTip.InitialDelay = 450; $toolTip.ReshowDelay = 100

$headerPanel = New-Object Windows.Forms.Panel
$headerPanel.Dock = 'Top'; $headerPanel.Height = 112; $headerPanel.BackColor = $theme.Header
$form.Controls.Add($headerPanel)
$headerPanel.Controls.Add((New-Label 'LOCAL DEVELOPMENT' 26 19 -FontSize 8 -Color $theme.Primary -FontStyle 'Bold'))
$headerPanel.Controls.Add((New-Label 'Agent development sandbox' 24 40 -FontSize 20 -FontStyle 'Bold'))
$headerPanel.Controls.Add((New-Label ("Ubuntu {0}  |  Hyper-V  |  {1}" -f $vmConfig.Image, $vmConfig.Name) 27 78 -FontSize 9.5 -Color $theme.Muted))

$workspaceButton = New-ActionButton 'Open workspace' 132 'Ghost'
$workspaceButton.Location = New-Object Drawing.Point(626, 37); $workspaceButton.Anchor = 'Top, Right'
$workspaceButton.AccessibleDescription = 'Open the VM toolkit folder in File Explorer.'
$headerPanel.Controls.Add($workspaceButton)
$guideButton = New-ActionButton 'Guide' 104 'Ghost'
$guideButton.Location = New-Object Drawing.Point(770, 37); $guideButton.Anchor = 'Top, Right'
$guideButton.AccessibleDescription = 'Open the Agent Dev VM README.'
$headerPanel.Controls.Add($guideButton)

$actionCard = New-Card 24 132 872 126
$actionCard.Anchor = 'Top, Left, Right'; $form.Controls.Add($actionCard)
$actionCard.Controls.Add((New-Label 'VIRTUAL MACHINE' 18 15 -FontSize 8 -Color $theme.Muted -FontStyle 'Bold'))

$stateDot = New-Object Windows.Forms.Panel
$stateDot.Location = New-Object Drawing.Point(684, 16); $stateDot.Size = New-Object Drawing.Size(9, 9)
$stateDot.BackColor = $theme.Faint; $stateDot.Anchor = 'Top, Right'; $actionCard.Controls.Add($stateDot)
$stateLabel = New-Label 'Checking status' 701 11 146 22 9.5 $theme.Muted 'Bold'
$stateLabel.TextAlign = 'MiddleRight'; $stateLabel.Anchor = 'Top, Right'; $actionCard.Controls.Add($stateLabel)

$startButton = New-ActionButton 'Start VM' 112 'Primary'
$stopButton = New-ActionButton 'Stop VM' 112 'Danger'
$terminalButton = New-ActionButton 'Open terminal' 140 'Secondary'
$refreshButton = New-ActionButton 'Refresh' 104 'Secondary'
$startButton.Location = New-Object Drawing.Point(18, 47)
$stopButton.Location = New-Object Drawing.Point(140, 47)
$terminalButton.Location = New-Object Drawing.Point(262, 47)
$refreshButton.Location = New-Object Drawing.Point(412, 47)
$startButton.AccessibleDescription = 'Start the agent-dev virtual machine.'
$stopButton.AccessibleDescription = 'Gracefully stop the agent-dev virtual machine.'
$terminalButton.AccessibleDescription = 'Open an interactive shell inside the virtual machine.'
$refreshButton.AccessibleDescription = 'Refresh VM status now.'
$actionCard.Controls.AddRange(@($startButton, $stopButton, $terminalButton, $refreshButton))

$resourceLabel = New-Label ("{0} CPU   {1} RAM   {2} disk" -f $vmConfig.Cpus, $vmConfig.Memory, $vmConfig.Disk) 18 94 -FontSize 8.5 -Color $theme.Faint
$actionCard.Controls.Add($resourceLabel)
$autoRefreshCheckBox = New-Object Windows.Forms.CheckBox
$autoRefreshCheckBox.Text = 'Auto-refresh every 15s'; $autoRefreshCheckBox.Checked = $true; $autoRefreshCheckBox.AutoSize = $true
$autoRefreshCheckBox.Location = New-Object Drawing.Point(686, 51); $autoRefreshCheckBox.Anchor = 'Top, Right'
$autoRefreshCheckBox.ForeColor = $theme.Muted; $autoRefreshCheckBox.FlatStyle = 'Flat'
$autoRefreshCheckBox.AccessibleDescription = 'Refresh VM status automatically every fifteen seconds.'
$actionCard.Controls.Add($autoRefreshCheckBox)
$lastUpdatedLabel = New-Label 'Not checked yet' 638 92 210 20 8 $theme.Faint
$lastUpdatedLabel.TextAlign = 'MiddleRight'; $lastUpdatedLabel.Anchor = 'Top, Right'; $actionCard.Controls.Add($lastUpdatedLabel)
$busyBar = New-Object Windows.Forms.ProgressBar
$busyBar.Location = New-Object Drawing.Point(0, 121); $busyBar.Size = New-Object Drawing.Size(870, 3)
$busyBar.Anchor = 'Left, Right, Bottom'; $busyBar.Style = 'Marquee'; $busyBar.MarqueeAnimationSpeed = 28; $busyBar.Visible = $false
$actionCard.Controls.Add($busyBar)

$transferCard = New-Card 24 274 872 184
$transferCard.Anchor = 'Top, Left, Right'; $form.Controls.Add($transferCard)
$transferCard.Controls.Add((New-Label 'SEND TO VM' 18 15 -FontSize 8 -Color $theme.Muted -FontStyle 'Bold'))
$transferCard.Controls.Add((New-Label 'Destination' 18 14 82 22 8.5 $theme.Faint))
$destinationLabel = New-Label $guestInbox 102 13 520 22 9 $theme.Text
$transferCard.Controls.Add($destinationLabel)
$copyPathButton = New-ActionButton 'Copy path' 92 'Ghost'
$copyPathButton.Location = New-Object Drawing.Point(758, 8); $copyPathButton.Size = New-Object Drawing.Size(92, 30)
$copyPathButton.Anchor = 'Top, Right'; $copyPathButton.AccessibleDescription = 'Copy the guest inbox path to the clipboard.'
$transferCard.Controls.Add($copyPathButton)

$dropPanel = New-Object Windows.Forms.Panel
$dropPanel.Location = New-Object Drawing.Point(18, 47); $dropPanel.Size = New-Object Drawing.Size(624, 116)
$dropPanel.Anchor = 'Top, Left, Right'; $dropPanel.BorderStyle = 'FixedSingle'; $dropPanel.BackColor = $theme.SurfaceRaised
$dropPanel.AllowDrop = $true; $dropPanel.Cursor = [Windows.Forms.Cursors]::Hand
$dropPanel.AccessibleDescription = 'Drop files or folders here to copy them into the running virtual machine.'
$transferCard.Controls.Add($dropPanel)
$dropTitle = New-Label 'Drop files or folders here' 0 29 622 25 11 $theme.Text 'Bold'
$dropTitle.TextAlign = 'MiddleCenter'; $dropTitle.Anchor = 'Top, Left, Right'; $dropTitle.AllowDrop = $true
$dropPanel.Controls.Add($dropTitle)
$dropHint = New-Label 'Copied once to the guest | nothing stays mounted' 0 59 622 22 8.5 $theme.Muted
$dropHint.TextAlign = 'MiddleCenter'; $dropHint.Anchor = 'Top, Left, Right'; $dropHint.AllowDrop = $true
$dropPanel.Controls.Add($dropHint)

$chooseFilesButton = New-ActionButton 'Choose files' 190 'Secondary'
$chooseFilesButton.Location = New-Object Drawing.Point(660, 59); $chooseFilesButton.Anchor = 'Top, Right'
$chooseFilesButton.AccessibleDescription = 'Select one or more files to copy into the running VM.'
$transferCard.Controls.Add($chooseFilesButton)
$chooseFolderButton = New-ActionButton 'Choose folder' 190 'Secondary'
$chooseFolderButton.Location = New-Object Drawing.Point(660, 109); $chooseFolderButton.Anchor = 'Top, Right'
$chooseFolderButton.AccessibleDescription = 'Select a folder to copy recursively into the running VM.'
$transferCard.Controls.Add($chooseFolderButton)

$activityCard = New-Card 24 474 872 238
$activityCard.Anchor = 'Top, Bottom, Left, Right'; $form.Controls.Add($activityCard)
$activityCard.Controls.Add((New-Label 'ACTIVITY' 18 16 -FontSize 8 -Color $theme.Muted -FontStyle 'Bold'))
$activityHintLabel = New-Label 'Status and command output' 83 13 360 22 8.5 $theme.Faint
$activityCard.Controls.Add($activityHintLabel)
$copyOutputButton = New-ActionButton 'Copy output' 110 'Ghost'
$copyOutputButton.Location = New-Object Drawing.Point(623, 9); $copyOutputButton.Size = New-Object Drawing.Size(110, 30); $copyOutputButton.Anchor = 'Top, Right'
$copyOutputButton.AccessibleDescription = 'Copy all activity text to the clipboard.'; $activityCard.Controls.Add($copyOutputButton)
$clearOutputButton = New-ActionButton 'Clear' 104 'Ghost'
$clearOutputButton.Location = New-Object Drawing.Point(744, 9); $clearOutputButton.Size = New-Object Drawing.Size(104, 30); $clearOutputButton.Anchor = 'Top, Right'
$clearOutputButton.AccessibleDescription = 'Clear the activity history.'; $activityCard.Controls.Add($clearOutputButton)

$outputBox = New-Object Windows.Forms.TextBox
$outputBox.Location = New-Object Drawing.Point(18, 49); $outputBox.Size = New-Object Drawing.Size(830, 168)
$outputBox.Anchor = 'Top, Bottom, Left, Right'; $outputBox.Multiline = $true; $outputBox.ReadOnly = $true
$outputBox.ScrollBars = 'Vertical'; $outputBox.BorderStyle = 'FixedSingle'
$outputBox.BackColor = [Drawing.Color]::FromArgb(9, 13, 18); $outputBox.ForeColor = [Drawing.Color]::FromArgb(202, 213, 227)
$outputBox.Font = New-UiFont 9 'Regular' 'Consolas'
$outputBox.Text = "[$(Get-Date -Format 'HH:mm:ss')] Ready`r`nWorkspace: $workspace"
$activityCard.Controls.Add($outputBox)

$footerLabel = New-Label 'Ctrl+T terminal   |   Ctrl+O send files   |   F5 refresh   |   Ctrl+L clear activity' 27 725 -FontSize 8.5 -Color $theme.Faint
$footerLabel.Anchor = 'Bottom, Left'; $form.Controls.Add($footerLabel)

$script:activeProcess = $null
$script:activeAction = $null
$script:activeScriptName = $null
$script:activeManifestPath = $null
$script:isBusy = $false
$script:lastKnownState = 'Unknown'
$script:lastStatusCheck = [datetime]::MinValue

function Write-Activity {
    param([Parameter(Mandatory)][string]$Message, [switch]$Compact)
    $separator = if ($Compact) { '' } else { [Environment]::NewLine }
    $entry = "{0}[{1}] {2}" -f $separator, (Get-Date -Format 'HH:mm:ss'), $Message.Trim()
    $outputBox.AppendText($entry + [Environment]::NewLine)
    $outputBox.SelectionStart = $outputBox.TextLength; $outputBox.ScrollToCaret()
}

function Set-ControlAvailability {
    $running = $script:lastKnownState -eq 'Running'
    $stopped = $script:lastKnownState -eq 'Stopped'
    $unknown = $script:lastKnownState -eq 'Unknown'
    $startButton.Enabled = -not $script:isBusy -and ($stopped -or $unknown)
    $stopButton.Enabled = -not $script:isBusy -and ($running -or $unknown)
    $terminalButton.Enabled = -not $script:isBusy -and ($running -or $unknown)
    $refreshButton.Enabled = -not $script:isBusy
    $chooseFilesButton.Enabled = -not $script:isBusy -and $running
    $chooseFolderButton.Enabled = -not $script:isBusy -and $running
    $dropPanel.AllowDrop = -not $script:isBusy -and $running
    $dropTitle.AllowDrop = -not $script:isBusy -and $running
    $dropHint.AllowDrop = -not $script:isBusy -and $running
    $dropPanel.Cursor = if ($running -and -not $script:isBusy) { [Windows.Forms.Cursors]::Hand } else { [Windows.Forms.Cursors]::Default }
    if ($running) {
        $dropTitle.Text = 'Drop files or folders here'; $dropHint.Text = 'Copied once to the guest | nothing stays mounted'; $dropTitle.ForeColor = $theme.Text
    }
    elseif ($script:isBusy) {
        $dropTitle.Text = 'VM action in progress'; $dropHint.Text = 'File transfer will be available when it finishes'; $dropTitle.ForeColor = $theme.Muted
    }
    else {
        $dropTitle.Text = 'Start the VM to send files'; $dropHint.Text = 'Transfers are enabled only while the guest is running'; $dropTitle.ForeColor = $theme.Muted
    }
}

function Set-StateDisplay {
    param([Parameter(Mandatory)][string]$State, [string]$Detail)
    $script:lastKnownState = $State
    switch ($State) {
        'Running' { $stateLabel.Text = 'Running'; $stateLabel.ForeColor = $theme.Success; $stateDot.BackColor = $theme.Success }
        'Stopped' { $stateLabel.Text = 'Stopped'; $stateLabel.ForeColor = $theme.Muted; $stateDot.BackColor = $theme.Faint }
        'Missing' { $stateLabel.Text = 'Setup required'; $stateLabel.ForeColor = $theme.Warning; $stateDot.BackColor = $theme.Warning }
        'Failed' { $stateLabel.Text = 'Action failed'; $stateLabel.ForeColor = $theme.Danger; $stateDot.BackColor = $theme.Danger }
        default { $stateLabel.Text = 'Status unavailable'; $stateLabel.ForeColor = $theme.Warning; $stateDot.BackColor = $theme.Warning }
    }
    if ($Detail) { $resourceLabel.Text = $Detail }
    else { $resourceLabel.Text = "{0} CPU   {1} RAM   {2} disk" -f $vmConfig.Cpus, $vmConfig.Memory, $vmConfig.Disk }
    Set-ControlAvailability
}

function Set-BusyState {
    param([Parameter(Mandatory)][bool]$Busy, [string]$Label)
    $script:isBusy = $Busy; $busyBar.Visible = $Busy; $form.UseWaitCursor = $Busy
    if ($Busy -and $Label) { $stateLabel.Text = $Label; $stateLabel.ForeColor = $theme.Primary; $stateDot.BackColor = $theme.Primary }
    Set-ControlAvailability
}

function Set-StateFromOutput {
    param([string]$Output)
    $ipAddress = $null
    if ($Output -match '(?im)^IPv4:\s+([^\s]+)\s*$') { $ipAddress = $Matches[1] }
    elseif ($Output -match '"ipv4"\s*:\s*\[\s*"([^"]+)"') { $ipAddress = $Matches[1] }
    $detail = "{0} CPU   {1} RAM   {2} disk" -f $vmConfig.Cpus, $vmConfig.Memory, $vmConfig.Disk
    if ($ipAddress) { $detail += "   IP $ipAddress" }
    if ($Output -match '(?im)^State:\s+Running\s*$' -or $Output -match '"state"\s*:\s*"Running"') { Set-StateDisplay 'Running' $detail }
    elseif ($Output -match '(?im)^State:\s+Stopped\s*$' -or $Output -match '"state"\s*:\s*"Stopped"') { Set-StateDisplay 'Stopped' $detail }
    elseif ($Output -match "Instance '[^']+' is not present") { Set-StateDisplay 'Missing' 'Run the first-time setup from the guide' }
    else { Set-StateDisplay 'Unknown' 'Check Activity for status details' }
    $script:lastStatusCheck = Get-Date
    $lastUpdatedLabel.Text = "Updated $($script:lastStatusCheck.ToString('h:mm:ss tt'))"
}

function Start-VmScriptProcess {
    param(
        [Parameter(Mandatory)][string]$ScriptName,
        [Parameter(Mandatory)][string]$ActionLabel,
        [hashtable]$EnvironmentVariables,
        [switch]$QuietStart
    )
    if ($null -ne $script:activeProcess) { return }
    $scriptPath = Join-Path $scriptsDirectory $ScriptName
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $powershellPath
    $startInfo.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`""
    $startInfo.WorkingDirectory = $workspace; $startInfo.UseShellExecute = $false; $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true; $startInfo.RedirectStandardError = $true
    if ($EnvironmentVariables) { foreach ($key in $EnvironmentVariables.Keys) { $startInfo.EnvironmentVariables[[string]$key] = [string]$EnvironmentVariables[$key] } }
    $process = New-Object Diagnostics.Process; $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) { throw "Windows did not start $ScriptName." }
        $script:activeProcess = $process; $script:activeAction = $ActionLabel; $script:activeScriptName = $ScriptName
        Set-BusyState $true $ActionLabel
        if (-not $QuietStart) { Write-Activity "$ActionLabel..." }
    }
    catch {
        $process.Dispose()
        if ($script:activeManifestPath) { Remove-Item -LiteralPath $script:activeManifestPath -Force -ErrorAction SilentlyContinue; $script:activeManifestPath = $null }
        Set-BusyState $false; Set-StateDisplay 'Failed' 'Check Activity for the error'; Write-Activity $_.Exception.Message
    }
}

function Start-TransferItems {
    param([Parameter(Mandatory)][string[]]$Paths)
    if ($null -ne $script:activeProcess) {
        [Windows.Forms.MessageBox]::Show('Wait for the current VM action to finish, then try the transfer again.', 'Agent Dev VM is busy', 'OK', 'Information') | Out-Null
        return
    }
    if ($script:lastKnownState -ne 'Running') {
        [Windows.Forms.MessageBox]::Show('Start the VM before sending files or folders.', 'VM is not running', 'OK', 'Information') | Out-Null
        return
    }
    $validPaths = @($Paths | Where-Object { Test-Path -LiteralPath $_ })
    if ($validPaths.Count -eq 0) {
        [Windows.Forms.MessageBox]::Show('No existing files or folders were selected.', 'Nothing to copy', 'OK', 'Warning') | Out-Null
        return
    }
    $manifestPath = Join-Path ([IO.Path]::GetTempPath()) ("agent-dev-transfer-{0}.txt" -f [guid]::NewGuid().ToString('N'))
    [IO.File]::WriteAllLines($manifestPath, $validPaths, (New-Object Text.UTF8Encoding($false)))
    $script:activeManifestPath = $manifestPath
    Start-VmScriptProcess 'copy-in-batch.ps1' ("Sending {0} item(s)" -f $validPaths.Count) @{ AGENT_DEV_TRANSFER_MANIFEST = $manifestPath }
}

$processTimer = New-Object Windows.Forms.Timer
$processTimer.Interval = 350
$processTimer.Add_Tick({
    if ($null -eq $script:activeProcess -or -not $script:activeProcess.HasExited) { return }
    $process = $script:activeProcess; $action = $script:activeAction; $scriptName = $script:activeScriptName; $exitCode = $process.ExitCode
    $stdout = $process.StandardOutput.ReadToEnd().Trim(); $stderr = $process.StandardError.ReadToEnd().Trim(); $process.Dispose()
    $script:activeProcess = $null; $script:activeAction = $null; $script:activeScriptName = $null
    if ($script:activeManifestPath) { Remove-Item -LiteralPath $script:activeManifestPath -Force -ErrorAction SilentlyContinue; $script:activeManifestPath = $null }
    Set-BusyState $false
    $output = (@($stdout, $stderr) | Where-Object { $_ }) -join [Environment]::NewLine
    if (-not $output) { $output = "$action completed with no output." }
    if ($exitCode -ne 0) { Set-StateDisplay 'Failed' 'Check Activity for the error'; Write-Activity "$action failed.`r`n$output"; return }
    if ($scriptName -eq 'status.ps1') { Set-StateFromOutput $output; Write-Activity $output }
    elseif ($scriptName -eq 'copy-in-batch.ps1') { Set-StateDisplay 'Running'; Write-Activity $output }
    else { Write-Activity $output; Start-VmScriptProcess 'status.ps1' 'Refreshing status' -QuietStart }
})
$processTimer.Start()

$refreshTimer = New-Object Windows.Forms.Timer
$refreshTimer.Interval = 1000
$refreshTimer.Add_Tick({
    if ($autoRefreshCheckBox.Checked -and $null -eq $script:activeProcess -and ((Get-Date) - $script:lastStatusCheck).TotalSeconds -ge 15) {
        Start-VmScriptProcess 'status.ps1' 'Refreshing status' -QuietStart
    }
})
$refreshTimer.Start()

$startButton.Add_Click({ Start-VmScriptProcess 'start.ps1' 'Starting VM' })
$stopButton.Add_Click({ Start-VmScriptProcess 'stop.ps1' 'Stopping VM' })
$refreshButton.Add_Click({ Start-VmScriptProcess 'status.ps1' 'Refreshing status' })

$dragEnterHandler = {
    param($sender, $eventArgs)
    if ($null -eq $script:activeProcess -and $script:lastKnownState -eq 'Running' -and $eventArgs.Data.GetDataPresent([Windows.Forms.DataFormats]::FileDrop)) {
        $eventArgs.Effect = 'Copy'; $dropPanel.BackColor = [Drawing.Color]::FromArgb(28, 45, 70)
    }
    else { $eventArgs.Effect = 'None' }
}
$dragLeaveHandler = { $dropPanel.BackColor = $theme.SurfaceRaised }
$dragDropHandler = {
    param($sender, $eventArgs)
    $dropPanel.BackColor = $theme.SurfaceRaised
    Start-TransferItems ([string[]]$eventArgs.Data.GetData([Windows.Forms.DataFormats]::FileDrop))
}
foreach ($dropControl in @($dropPanel, $dropTitle, $dropHint)) {
    $dropControl.Add_DragEnter($dragEnterHandler); $dropControl.Add_DragLeave($dragLeaveHandler); $dropControl.Add_DragDrop($dragDropHandler)
}

$chooseFilesAction = {
    $dialog = New-Object Windows.Forms.OpenFileDialog
    $dialog.Title = 'Choose files to send to agent-dev'; $dialog.Multiselect = $true; $dialog.CheckFileExists = $true
    try { if ($dialog.ShowDialog($form) -eq 'OK') { Start-TransferItems $dialog.FileNames } } finally { $dialog.Dispose() }
}
$chooseFilesButton.Add_Click($chooseFilesAction)
$chooseFolderButton.Add_Click({
    $dialog = New-Object Windows.Forms.FolderBrowserDialog
    $dialog.Description = "Choose a folder to send to $guestInbox"; $dialog.ShowNewFolderButton = $false
    try { if ($dialog.ShowDialog($form) -eq 'OK') { Start-TransferItems @($dialog.SelectedPath) } } finally { $dialog.Dispose() }
})

$openTerminalAction = {
    if (-not $terminalButton.Enabled) { return }
    try {
        Start-Process -FilePath $powershellPath -ArgumentList @('-NoExit', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $scriptsDirectory 'enter.ps1')) -WorkingDirectory $workspace -WindowStyle Normal | Out-Null
        Write-Activity 'Opened a terminal session.'
    }
    catch { [Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Unable to open terminal', 'OK', 'Error') | Out-Null }
}
$terminalButton.Add_Click($openTerminalAction)
$workspaceButton.Add_Click({
    try { Start-Process -FilePath 'explorer.exe' -ArgumentList @($workspace) | Out-Null }
    catch { [Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Unable to open workspace') | Out-Null }
})
$guideButton.Add_Click({
    try { Start-Process -FilePath (Join-Path $workspace 'README.md') | Out-Null }
    catch { [Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Unable to open guide', 'OK', 'Error') | Out-Null }
})
$copyPathButton.Add_Click({
    try { [Windows.Forms.Clipboard]::SetText($guestInbox); $activityHintLabel.Text = 'Guest path copied to clipboard' }
    catch { Write-Activity "Could not copy the guest path: $($_.Exception.Message)" }
})
$copyOutputButton.Add_Click({
    if ($outputBox.Text) {
        try { [Windows.Forms.Clipboard]::SetText($outputBox.Text); $activityHintLabel.Text = 'Activity copied to clipboard' }
        catch { Write-Activity "Could not copy Activity: $($_.Exception.Message)" }
    }
})
$clearOutputAction = { $outputBox.Clear(); Write-Activity 'Activity cleared.' -Compact; $activityHintLabel.Text = 'Status and command output' }
$clearOutputButton.Add_Click($clearOutputAction)

$form.Add_KeyDown({
    param($sender, $eventArgs)
    if ($eventArgs.KeyCode -eq 'F5') { if ($refreshButton.Enabled) { $refreshButton.PerformClick() }; $eventArgs.SuppressKeyPress = $true }
    elseif ($eventArgs.Control -and $eventArgs.KeyCode -eq 'T') { & $openTerminalAction; $eventArgs.SuppressKeyPress = $true }
    elseif ($eventArgs.Control -and $eventArgs.KeyCode -eq 'O') { if ($chooseFilesButton.Enabled) { & $chooseFilesAction }; $eventArgs.SuppressKeyPress = $true }
    elseif ($eventArgs.Control -and $eventArgs.KeyCode -eq 'L') { & $clearOutputAction; $eventArgs.SuppressKeyPress = $true }
})

$toolTip.SetToolTip($startButton, 'Start the VM')
$toolTip.SetToolTip($stopButton, 'Gracefully stop the VM')
$toolTip.SetToolTip($terminalButton, 'Open terminal  (Ctrl+T)')
$toolTip.SetToolTip($refreshButton, 'Refresh status  (F5)')
$toolTip.SetToolTip($chooseFilesButton, 'Choose files  (Ctrl+O)')
$toolTip.SetToolTip($clearOutputButton, 'Clear activity  (Ctrl+L)')
$toolTip.SetToolTip($copyPathButton, "Copy $guestInbox")

Set-ControlAvailability
$form.Add_Shown({
    Enable-DarkTitleBar $form.Handle
    if (-not $RenderPreviewPath) { Start-VmScriptProcess 'status.ps1' 'Checking status' -QuietStart }
})
$form.Add_FormClosed({
    $processTimer.Stop(); $refreshTimer.Stop()
    if ($null -ne $script:activeProcess) { $script:activeProcess.Dispose() }
    if ($script:activeManifestPath) { Remove-Item -LiteralPath $script:activeManifestPath -Force -ErrorAction SilentlyContinue }
    $toolTip.Dispose()
})

if ($SmokeTest) {
    Write-Output 'VM_GUI_SMOKE_OK'
    $processTimer.Stop(); $refreshTimer.Stop(); $toolTip.Dispose(); $form.Dispose()
    exit 0
}

if ($RenderPreviewPath) {
    Set-StateDisplay 'Running' ("{0} CPU   {1} RAM   {2} disk   IP 10.24.0.8" -f $vmConfig.Cpus, $vmConfig.Memory, $vmConfig.Disk)
    $lastUpdatedLabel.Text = 'Updated just now'
    Write-Activity "State: Running`r`nIPv4: 10.24.0.8`r`nUbuntu $($vmConfig.Image) is ready."
    $form.Show()
    [Windows.Forms.Application]::DoEvents()
    $bitmap = New-Object Drawing.Bitmap($form.Width, $form.Height)
    try {
        $form.DrawToBitmap($bitmap, (New-Object Drawing.Rectangle(0, 0, $bitmap.Width, $bitmap.Height)))
        $previewDirectory = Split-Path -Parent $RenderPreviewPath
        if ($previewDirectory -and -not (Test-Path -LiteralPath $previewDirectory -PathType Container)) {
            [void](New-Item -ItemType Directory -Path $previewDirectory)
        }
        $bitmap.Save($RenderPreviewPath, [Drawing.Imaging.ImageFormat]::Png)
        Write-Output "VM_GUI_PREVIEW=$RenderPreviewPath"
    }
    finally {
        $bitmap.Dispose(); $form.Close(); $form.Dispose()
    }
    exit 0
}

[void]$form.ShowDialog()
