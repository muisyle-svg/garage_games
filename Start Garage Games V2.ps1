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

function Test-ServerHealthy([switch]$Owned) {
    $script:serverIdentityError = ''
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

function Start-OwnedServer {
    $environment = @{
        DOTNET_CLI_HOME = $script:toolRoot
        APPDATA = (Join-Path $script:toolRoot 'appdata')
        NUGET_PACKAGES = (Join-Path $script:toolRoot 'nuget-packages')
        DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
        GARAGE_GAMES_V2_SHUTDOWN_TOKEN = $script:shutdownToken
    }
    $previousEnvironment = @{}

    foreach ($name in $environment.Keys) {
        $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process')
    }

    try {
        if ($script:usePublished) {
            if (-not (Test-Path -LiteralPath $script:published)) {
                throw "The published Garage Games app was not found at: $($script:published)"
            }
            $filePath = $script:published
            $workingDirectory = $script:scriptRoot
            $arguments = '--hardware-mode --data-path ' + (Quote-ProcessArgument $script:dataPath) +
                ' --legacy-data-path ' + (Quote-ProcessArgument $script:legacyDataPath) +
                ' --build-id ' + (Quote-ProcessArgument $script:buildId) +
                ' --urls ' + (Quote-ProcessArgument $script:url.TrimEnd('/'))
        } else {
            if (-not (Test-Path -LiteralPath $script:dotnet)) {
                throw "The bundled .NET runtime was not found at: $($script:dotnet)"
            }
            if (-not (Test-Path -LiteralPath $script:project)) {
                throw "The Garage Games v2 project was not found at: $($script:project)"
            }
            $filePath = $script:dotnet
            $workingDirectory = $script:repoRoot
            $arguments = 'run --configuration Release --no-restore --project ' + (Quote-ProcessArgument $script:project) +
                ' -- --hardware-mode --data-path ' + (Quote-ProcessArgument $script:dataPath) +
                ' --legacy-data-path ' + (Quote-ProcessArgument $script:legacyDataPath) +
                ' --build-id ' + (Quote-ProcessArgument $script:buildId) +
                ' --urls ' + (Quote-ProcessArgument $script:url.TrimEnd('/'))
        }

        return Start-Process `
            -FilePath $filePath `
            -ArgumentList $arguments `
            -WorkingDirectory $workingDirectory `
            -WindowStyle Hidden `
            -PassThru `
            -RedirectStandardOutput $script:outputLog `
            -RedirectStandardError $script:errorLog
    } finally {
        foreach ($name in $environment.Keys) {
            [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name], 'Process')
        }
    }
}

function Open-GarageGames {
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
    $icon.Text = if ($script:ownsServer) { 'Garage Games v2 (this tray session owns the server)' } else { 'Garage Games v2 (using an existing server)' }
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
    $script:outputLog = Join-Path (Join-Path $script:toolRoot 'logs') "garage-games-v2-$logStamp-$PID.out.log"
    $script:errorLog = Join-Path (Join-Path $script:toolRoot 'logs') "garage-games-v2-$logStamp-$PID.err.log"

    if (Test-ServerHealthy) {
        # Only reuse an instance that reports this build and the canonical data directory.
        $script:ownsServer = $false
    } elseif ($script:serverIdentityError) {
        Show-Notice $script:serverIdentityError 'Garage Games version or data folder mismatch' ([System.Windows.Forms.MessageBoxIcon]::Warning)
        return
    } else {
        $script:shutdownToken = New-ShutdownToken
        $script:serverProcess = Start-OwnedServer
        $script:ownsServer = $true
    }

    New-TrayIcon

    if ($script:ownsServer) {
        $ready = $false
        for ($attempt = 0; $attempt -lt 40; $attempt++) {
            if ($script:serverProcess.HasExited) { break }
            if (Test-ServerHealthy -Owned) { $ready = $true; break }
            Start-Sleep -Milliseconds 250
        }
        if ($ready) {
            Start-Process -FilePath $script:url
        } else {
            $message = if ($script:serverProcess.HasExited) {
                "Garage Games v2 exited during startup. Review the local logs for details."
            } else {
                "Garage Games v2 is taking longer than expected to start. The tray remains available; choose Open to retry when it is ready."
            }
            Show-StartupFailure $message
        }
    } else {
        Start-Process -FilePath $script:url
    }

    while (-not $script:exitRequested) {
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 200
    }
} catch {
    Show-StartupFailure "Windows could not start Garage Games v2: $($_.Exception.Message)"
} finally {
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
