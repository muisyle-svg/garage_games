param([switch]$UsePublished)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$script:scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:repoRoot = $script:scriptRoot
$script:toolRoot = Join-Path $script:repoRoot '.tools'
$script:dotnet = Join-Path $script:toolRoot 'dotnet\dotnet.exe'
$script:localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
$script:dataPath = Join-Path $script:localAppData 'GarageGamesV2'
$script:legacyDataPath = Join-Path $script:toolRoot 'localappdata\GarageGamesV2'
$script:published = Join-Path $script:scriptRoot 'publish\win-x64\GarageGames.V2.exe'
$script:project = Join-Path $script:scriptRoot 'src\GarageGames.V2\GarageGames.V2.csproj'
$script:sourceAssemblyDirectory = Join-Path (Join-Path $script:scriptRoot 'src\GarageGames.V2') 'bin\Release\net10.0'
$script:sourceAssembly = Join-Path $script:sourceAssemblyDirectory 'GarageGames.V2.dll'
# Records which source fingerprint the built app came from, so a launch only rebuilds after a change.
$script:buildMarker = Join-Path $script:sourceAssemblyDirectory '.garage-games-build-id'
$script:buildProcess = $null
$script:buildLog = $null
$script:buildErrorLog = $null
# Startup runs as phases driven by a UI timer: building -> starting -> running (or failed).
$script:phase = 'idle'
$script:phaseStartedAt = $null
$script:slowStartNoticeShown = $false
$script:timer = $null
$script:usePublished = [bool]$UsePublished
$script:expectedApplicationDirectory = if ($script:usePublished) { Split-Path -Parent $script:published } else { $script:sourceAssemblyDirectory }
$script:buildId = $null
$script:url = 'http://127.0.0.1:5187/'
$script:health = $script:url + 'api/health'
$script:trayHealth = $script:url + 'api/internal/tray-health'
$script:shutdownUrl = $script:url + 'api/internal/shutdown'
$script:shutdownHeader = 'X-Garage-Games-Shutdown'
$script:serverProcess = $null
$script:shutdownToken = $null
$script:ownsServer = $false
$script:exitRequested = $false
$script:notifyIcon = $null
$script:contextMenu = $null
$script:mutex = $null
$script:ownsMutex = $false
$script:outputLog = $null
$script:errorLog = $null
$script:serverIdentityError = ''

function Get-TreeBuildId([object[]]$Roots) {
    $records = New-Object 'System.Collections.Generic.List[string]'
    foreach ($root in $Roots) {
        $rootPath = [System.IO.Path]::GetFullPath([string]$root.Path)
        if (-not (Test-Path -LiteralPath $rootPath -PathType Container)) {
            throw "Build identity input folder was not found: $rootPath"
        }
        foreach ($file in (Get-ChildItem -LiteralPath $rootPath -File -Recurse -Force | Sort-Object -Property FullName)) {
            $relativePath = $file.FullName.Substring($rootPath.Length).TrimStart('\', '/')
            if ($relativePath -match '(^|[\\/])(bin|obj)([\\/]|$)') { continue }
            if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
            $contentHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            $records.Add(([string]$root.Name + '/' + $relativePath.Replace('\', '/') + '|' + $contentHash))
        }
    }
    $records.Sort([System.StringComparer]::Ordinal)
    $payload = [System.Text.Encoding]::UTF8.GetBytes([string]::Join("`n", $records))
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($algorithm.ComputeHash($payload)).Replace('-', '').ToLowerInvariant()
    } finally {
        $algorithm.Dispose()
        [Array]::Clear($payload, 0, $payload.Length)
    }
}

function Get-NormalizedPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return '' }
    return [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
}

function Show-Notice([string]$Message, [string]$Title = 'Garage Games v2', [System.Windows.Forms.MessageBoxIcon]$Icon = [System.Windows.Forms.MessageBoxIcon]::Information) {
    [void][System.Windows.Forms.MessageBox]::Show(
        $Message,
        $Title,
        [System.Windows.Forms.MessageBoxButtons]::OK,
        $Icon
    )
}

function Show-StartupFailure([string]$Message) {
    $details = $Message
    if ($script:outputLog -or $script:errorLog) {
        $details += "`n`nLocal startup logs:`n$($script:outputLog)`n$($script:errorLog)"
    }
    Show-Notice $details 'Garage Games v2 could not start' ([System.Windows.Forms.MessageBoxIcon]::Error)
}

function Test-PortListening {
    $port = ([Uri]$script:url).Port
    return [bool]([System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners() |
        Where-Object { $_.Port -eq $port })
}

function Test-ServerHealthy([switch]$Owned) {
    $script:serverIdentityError = ''
    # On Windows an HTTP request to a port with no listener retries for about two seconds
    # before failing; checking the listener table first answers "nothing running yet" instantly.
    if (-not (Test-PortListening)) {
        return $false
    }
    $parameters = @{
        UseBasicParsing = $true
        Uri = $(if ($Owned) { $script:trayHealth } else { $script:health })
        TimeoutSec = 2
    }
    if ($Owned) {
        $parameters.Headers = @{ $script:shutdownHeader = $script:shutdownToken }
    }
    try {
        $response = Invoke-RestMethod @parameters
        $mismatches = New-Object 'System.Collections.Generic.List[string]'
        if ($response.status -ne 'ok') {
            $mismatches.Add('health response is not a recognized Garage Games server')
        }
        if ($response.buildId -ne $script:buildId) {
            $reportedBuild = if ($response.buildId) { [string]$response.buildId } else { 'unknown (older server)' }
            $mismatches.Add("version/build does not match (running: $reportedBuild; expected: $($script:buildId))")
        }
        if ((Get-NormalizedPath ([string]$response.dataDirectory)) -ne (Get-NormalizedPath $script:dataPath)) {
            $reportedData = if ($response.dataDirectory) { [string]$response.dataDirectory } else { 'unknown (older server)' }
            $mismatches.Add("data folder does not match (running: $reportedData; expected: $($script:dataPath))")
        }
        if ((Get-NormalizedPath ([string]$response.applicationDirectory)) -ne (Get-NormalizedPath $script:expectedApplicationDirectory)) {
            $reportedDirectory = if ($response.applicationDirectory) { [string]$response.applicationDirectory } else { 'unknown (older server)' }
            $mismatches.Add("application folder does not match (running: $reportedDirectory; expected: $($script:expectedApplicationDirectory))")
        }

        if ($mismatches.Count -gt 0) {
            $script:serverIdentityError = "Another Garage Games server is responding, but it cannot be verified as this version using this computer's standard data folder:`n`n$([string]::Join("`n", $mismatches))`n`nTo protect your results, this launcher did not connect to it or start a second copy. Close the older Garage Games instance cleanly, then start this shortcut again. Neither data folder was changed."
            return $false
        }
        return $true
    } catch {
        if ($_.Exception.Response) {
            $script:serverIdentityError = "Another local server is responding at $($script:url), but it did not provide a verifiable Garage Games identity. To protect your results, this launcher did not connect to it or start a second copy. Close the other server or Garage Games instance, then retry. No data folder was changed."
        }
        return $false
    }
}

function Get-InstanceMutexName {
    $userSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    return "Local\GarageGamesV2Tray-$userSid"
}

function New-ShutdownToken {
    $bytes = New-Object byte[] 32
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $random.GetBytes($bytes)
        return [BitConverter]::ToString($bytes).Replace('-', '').ToLowerInvariant()
    } finally {
        $random.Dispose()
        [Array]::Clear($bytes, 0, $bytes.Length)
    }
}

function Quote-ProcessArgument([string]$Value) {
    # Windows paths cannot contain a double quote, and none of these arguments end in a slash.
    return '"' + $Value + '"'
}

function Invoke-WithToolEnvironment([hashtable]$Extra, [scriptblock]$Action) {
    $environment = @{
        DOTNET_CLI_HOME = $script:toolRoot
        APPDATA = (Join-Path $script:toolRoot 'appdata')
        NUGET_PACKAGES = (Join-Path $script:toolRoot 'nuget-packages')
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
        DOTNET_NOLOGO = '1'
    }
    foreach ($name in $Extra.Keys) { $environment[$name] = $Extra[$name] }
    $previousEnvironment = @{}
    foreach ($name in $environment.Keys) {
        $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process')
    }
    try {
        return & $Action
    } finally {
        foreach ($name in $environment.Keys) {
            [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
        }
    }
}

# The built app is current only if it was built by this launcher from the current source
# fingerprint and has not been replaced since (for example by a manual or test build).
function Get-SourceAssemblyStamp {
    $item = Get-Item -LiteralPath $script:sourceAssembly -ErrorAction SilentlyContinue
    if (-not $item) { return '' }
    return "$($item.Length):$($item.LastWriteTimeUtc.Ticks)"
}

function Test-SourceBuildCurrent {
    if (-not (Test-Path -LiteralPath $script:buildMarker) -or -not (Test-Path -LiteralPath $script:sourceAssembly)) { return $false }
    $marker = ([string](Get-Content -LiteralPath $script:buildMarker -Raw)).Trim()
    return $marker -eq "$($script:buildId)|$(Get-SourceAssemblyStamp)"
}

function Write-SourceBuildMarker {
    Set-Content -LiteralPath $script:buildMarker -Value "$($script:buildId)|$(Get-SourceAssemblyStamp)" -Encoding ascii
}

function Start-SourceBuild {
    if (-not (Test-Path -LiteralPath $script:dotnet)) {
        throw "The bundled .NET toolchain was not found at: $($script:dotnet)"
    }
    if (-not (Test-Path -LiteralPath $script:project)) {
        throw "The Garage Games v2 project was not found at: $($script:project)"
    }
    $arguments = 'build ' + (Quote-ProcessArgument $script:project) + ' --configuration Release --no-restore -nologo -v q'
    $process = Invoke-WithToolEnvironment @{} {
        Start-Process -FilePath $script:dotnet -ArgumentList $arguments -WorkingDirectory $script:repoRoot `
            -WindowStyle Hidden -PassThru -RedirectStandardOutput $script:buildLog -RedirectStandardError $script:buildErrorLog
    }
    # Windows PowerShell only reports ExitCode later if the handle is opened now.
    $null = $process.Handle
    return $process
}

function Start-OwnedServer {
    if ($script:usePublished) {
        if (-not (Test-Path -LiteralPath $script:published)) {
            throw "The published Garage Games app was not found at: $($script:published)"
        }
        $filePath = $script:published
        $prefix = ''
        $workingDirectory = $script:scriptRoot
    } else {
        # Start the already-built app directly; building happens beforehand only when needed.
        $filePath = $script:dotnet
        $prefix = (Quote-ProcessArgument $script:sourceAssembly) + ' '
        $workingDirectory = $script:repoRoot
    }
    $arguments = $prefix + '--hardware-mode --data-path ' + (Quote-ProcessArgument $script:dataPath) +
        ' --legacy-data-path ' + (Quote-ProcessArgument $script:legacyDataPath) +
        ' --build-id ' + (Quote-ProcessArgument $script:buildId) +
        ' --urls ' + (Quote-ProcessArgument $script:url.TrimEnd('/'))

    $process = Invoke-WithToolEnvironment @{ GARAGE_GAMES_V2_SHUTDOWN_TOKEN = $script:shutdownToken } {
        Start-Process -FilePath $filePath -ArgumentList $arguments -WorkingDirectory $workingDirectory `
            -WindowStyle Hidden -PassThru -RedirectStandardOutput $script:outputLog -RedirectStandardError $script:errorLog
    }
    $null = $process.Handle
    return $process
}

function Set-TrayStatus([string]$Text) {
    # NotifyIcon tooltips are limited to 63 characters.
    if ($script:notifyIcon) { $script:notifyIcon.Text = $Text.Substring(0, [Math]::Min(63, $Text.Length)) }
}

function Show-TrayMessage([string]$Title, [string]$Text) {
    if ($script:notifyIcon) { $script:notifyIcon.ShowBalloonTip(5000, $Title, $Text, [System.Windows.Forms.ToolTipIcon]::Info) }
}

function Enter-Phase([string]$Phase) {
    $script:phase = $Phase
    $script:phaseStartedAt = [DateTime]::UtcNow
}

function Start-BuildPhase {
    Enter-Phase 'building'
    Set-TrayStatus 'Garage Games v2 - updating after code changes'
    Show-TrayMessage 'Updating Garage Games' 'The code changed since the last start, so it is being rebuilt. This takes a few seconds.'
    $script:buildProcess = Start-SourceBuild
}

function Start-ServerPhase {
    Enter-Phase 'starting'
    Set-TrayStatus 'Garage Games v2 - starting'
    $script:serverProcess = Start-OwnedServer
}

# Called by the UI timer. Each step is short, so the tray menu stays responsive throughout.
function Step-Startup {
    try {
        $elapsed = ([DateTime]::UtcNow - $script:phaseStartedAt).TotalSeconds
        switch ($script:phase) {
            'building' {
                if (-not $script:buildProcess.HasExited) {
                    if ($elapsed -gt 300) {
                        Enter-Phase 'failed'
                        Show-StartupFailure "Updating Garage Games took more than five minutes and was abandoned. Build logs:`n$($script:buildLog)`n$($script:buildErrorLog)"
                    }
                    return
                }
                if ($script:buildProcess.ExitCode -ne 0) {
                    Enter-Phase 'failed'
                    Set-TrayStatus 'Garage Games v2 - update failed'
                    Show-StartupFailure "Garage Games could not be rebuilt after a code change (exit code $($script:buildProcess.ExitCode)). The previous version was not started so an out-of-date build cannot run.`n`nBuild logs:`n$($script:buildLog)`n$($script:buildErrorLog)"
                    return
                }
                Write-SourceBuildMarker
                Start-ServerPhase
            }
            'starting' {
                if ($script:serverProcess.HasExited) {
                    Enter-Phase 'failed'
                    Set-TrayStatus 'Garage Games v2 - stopped during startup'
                    Show-StartupFailure 'Garage Games v2 exited during startup. Review the local logs for details.'
                    return
                }
                if (Test-ServerHealthy -Owned) {
                    Enter-Phase 'running'
                    Set-TrayStatus 'Garage Games v2 (this tray session owns the server)'
                    Start-Process -FilePath $script:url
                    return
                }
                if ($elapsed -gt 120 -and -not $script:slowStartNoticeShown) {
                    # Keep waiting; the browser opens as soon as the app responds.
                    $script:slowStartNoticeShown = $true
                    Show-TrayMessage 'Garage Games is still starting' 'This is taking longer than usual. The page opens automatically when it is ready; startup logs are in .tools\logs.'
                }
            }
        }
    } catch {
        Enter-Phase 'failed'
        Show-StartupFailure "Windows could not start Garage Games v2: $($_.Exception.Message)"
    }
}

function Open-GarageGames {
    if ($script:phase -in 'building', 'starting') {
        $what = if ($script:phase -eq 'building') { 'being updated after a code change' } else { 'starting' }
        Show-Notice "Garage Games is still $what. The page opens automatically as soon as it is ready."
        return
    }
    if (-not (Test-ServerHealthy -Owned:$script:ownsServer)) {
        if ($script:serverIdentityError) {
            Show-Notice $script:serverIdentityError 'Garage Games version or data folder mismatch' ([System.Windows.Forms.MessageBoxIcon]::Warning)
            return
        }
        if ($script:ownsServer -and $script:serverProcess -and -not $script:serverProcess.HasExited) {
            Show-Notice "Garage Games is still starting or is not responding yet. You can try Open again shortly.`n`nStartup logs are in:`n$($script:outputLog)`n$($script:errorLog)"
        } else {
            Show-Notice "Garage Games is not responding. If it stopped unexpectedly, start it again with the Start Garage Games V2 shortcut.`n`nStartup logs are in:`n$($script:outputLog)`n$($script:errorLog)" 'Garage Games v2 is unavailable' ([System.Windows.Forms.MessageBoxIcon]::Warning)
        }
        return
    }
    Start-Process -FilePath $script:url
}

function Request-OwnedServerExit {
    if (-not $script:ownsServer -or $null -eq $script:serverProcess) {
        return $true
    }

    if ($script:serverProcess.HasExited) {
        return $true
    }

    try {
        $headers = @{ $script:shutdownHeader = $script:shutdownToken }
        Invoke-WebRequest -UseBasicParsing -Method Post -Uri $script:shutdownUrl -Headers $headers -TimeoutSec 5 | Out-Null
    } catch {
        # The app may close the HTTP connection as the graceful stop completes; verify its process below.
    }

    try {
        if ($script:serverProcess.WaitForExit(25000) -or $script:serverProcess.HasExited) {
            return $true
        }
    } catch {
        if ($script:serverProcess.HasExited) {
            return $true
        }
    }

    Add-Type -AssemblyName System.Windows.Forms
    Show-Notice "Windows did not confirm a graceful stop. To protect the local score database, Garage Games was not force-stopped and the tray will remain open so you can retry Exit.`n`nIf it keeps failing, keep using the app or contact support before ending its process." 'Garage Games is still running' ([System.Windows.Forms.MessageBoxIcon]::Warning)
    return $false
}

function Request-TrayExit {
    if (Request-OwnedServerExit) {
        $script:exitRequested = $true
        [System.Windows.Forms.Application]::ExitThread()
    }
}

function New-TrayIcon {
    $menu = New-Object System.Windows.Forms.ContextMenuStrip
    $openItem = New-Object System.Windows.Forms.ToolStripMenuItem('Open Garage Games')
    $exitItem = New-Object System.Windows.Forms.ToolStripMenuItem
    if ($script:ownsServer) {
        $exitItem.Text = 'Exit and stop this session'
    } else {
        $exitItem.Text = 'Exit (leave existing server running)'
    }

    $openItem.Add_Click({ Open-GarageGames }.GetNewClosure())
    $exitItem.Add_Click({ Request-TrayExit }.GetNewClosure())
    [void]$menu.Items.Add($openItem)
    [void]$menu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
    [void]$menu.Items.Add($exitItem)

    $icon = New-Object System.Windows.Forms.NotifyIcon
    $icon.Icon = [System.Drawing.SystemIcons]::Application
    $icon.Text = if ($script:ownsServer) { 'Garage Games v2 - starting' } else { 'Garage Games v2 (using an existing server)' }
    $icon.ContextMenuStrip = $menu
    $icon.Visible = $true
    $icon.Add_MouseDoubleClick({ Open-GarageGames }.GetNewClosure())

    $script:contextMenu = $menu
    $script:notifyIcon = $icon
}

try {
    if ([string]::IsNullOrWhiteSpace($script:localAppData)) {
        throw 'Windows did not provide the current user Local AppData folder.'
    }
    if ($script:usePublished) {
        $script:buildId = Get-TreeBuildId @(@{ Name = 'published'; Path = (Split-Path -Parent $script:published) })
    } else {
        $script:buildId = Get-TreeBuildId @(
            @{ Name = 'application'; Path = (Join-Path $script:scriptRoot 'src\GarageGames.V2') },
            @{ Name = 'edition'; Path = (Join-Path $script:scriptRoot 'config') }
        )
    }

    foreach ($directory in @(
        $script:toolRoot,
        (Join-Path $script:toolRoot 'appdata'),
        (Join-Path $script:toolRoot 'nuget-packages'),
        (Join-Path $script:toolRoot 'logs')
    )) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $mutexName = Get-InstanceMutexName
    $script:mutex = [System.Threading.Mutex]::new($false, $mutexName)
    try {
        $script:ownsMutex = $script:mutex.WaitOne(0)
    } catch [System.Threading.AbandonedMutexException] {
        $script:ownsMutex = $true
    }

    if (-not $script:ownsMutex) {
        $ready = $false
        for ($attempt = 0; $attempt -lt 40; $attempt++) {
            if (Test-ServerHealthy) { $ready = $true; break }
            Start-Sleep -Milliseconds 250
        }
        if ($ready) {
            Start-Process -FilePath $script:url
        } elseif ($script:serverIdentityError) {
            Show-Notice $script:serverIdentityError 'Garage Games version or data folder mismatch' ([System.Windows.Forms.MessageBoxIcon]::Warning)
        } else {
            Show-Notice 'Another Garage Games tray instance is already running. No second server or tray instance was started.' 'Garage Games v2 is already open' ([System.Windows.Forms.MessageBoxIcon]::Information)
        }
        return
    }

    $logStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $logDirectory = Join-Path $script:toolRoot 'logs'
    $script:outputLog = Join-Path $logDirectory "garage-games-v2-$logStamp-$PID.out.log"
    $script:errorLog = Join-Path $logDirectory "garage-games-v2-$logStamp-$PID.err.log"
    $script:buildLog = Join-Path $logDirectory "garage-games-v2-$logStamp-$PID.build.log"
    $script:buildErrorLog = Join-Path $logDirectory "garage-games-v2-$logStamp-$PID.build.err.log"

    if (Test-ServerHealthy) {
        # Only reuse an instance that reports this build and the canonical data directory.
        $script:ownsServer = $false
    } elseif ($script:serverIdentityError) {
        Show-Notice $script:serverIdentityError 'Garage Games version or data folder mismatch' ([System.Windows.Forms.MessageBoxIcon]::Warning)
        return
    } else {
        $script:shutdownToken = New-ShutdownToken
        $script:ownsServer = $true
    }

    # Show the tray icon first so progress (including a rebuild) is visible right away.
    New-TrayIcon

    if ($script:ownsServer) {
        if ($script:usePublished -or (Test-SourceBuildCurrent)) {
            Start-ServerPhase
        } else {
            Start-BuildPhase
        }
        $script:timer = New-Object System.Windows.Forms.Timer
        $script:timer.Interval = 250
        $script:timer.Add_Tick({ Step-Startup }.GetNewClosure())
        $script:timer.Start()
    } else {
        Start-Process -FilePath $script:url
    }

    # The standard Windows message loop keeps the tray menu responsive; Exit ends it.
    [System.Windows.Forms.Application]::Run()
} catch {
    Show-StartupFailure "Windows could not start Garage Games v2: $($_.Exception.Message)"
} finally {
    if ($script:timer) {
        $script:timer.Stop()
        $script:timer.Dispose()
    }
    if ($script:buildProcess) {
        $script:buildProcess.Dispose()
    }
    if ($script:notifyIcon) {
        $script:notifyIcon.Visible = $false
        $script:notifyIcon.Dispose()
    }
    if ($script:contextMenu) {
        $script:contextMenu.Dispose()
    }
    if ($script:ownsMutex -and $script:mutex) {
        try { $script:mutex.ReleaseMutex() } catch { }
    }
    if ($script:mutex) {
        $script:mutex.Dispose()
    }
    if ($script:serverProcess) {
        $script:serverProcess.Dispose()
    }
}
