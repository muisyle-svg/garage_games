$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptRoot '..')).Path
$dotnet = Join-Path $repoRoot '.tools\dotnet\dotnet.exe'
$dataPath = Join-Path $repoRoot '.tools\localappdata\GarageGamesV2'
$published = Join-Path $scriptRoot 'publish\win-x64\GarageGames.V2.exe'
$project = Join-Path $scriptRoot 'src\GarageGames.V2\GarageGames.V2.csproj'
$url = 'http://127.0.0.1:5187/'
$health = 'http://127.0.0.1:5187/api/health'

function Show-StartupFailure([string]$Message, [string]$OutputLog, [string]$ErrorLog) {
    Write-Host "`n$Message" -ForegroundColor Red
    foreach ($log in @($OutputLog, $ErrorLog)) {
        if (Test-Path -LiteralPath $log) {
            $contents = Get-Content -LiteralPath $log -Raw
            if (-not [string]::IsNullOrWhiteSpace($contents)) {
                Write-Host "`n--- $log ---"
                Write-Host $contents
            }
        }
    }
    Read-Host 'Press Enter to close this window'
}

function Start-LocalHiddenProcess([string]$CommandLine, [string]$WorkingDirectory, [string]$OutputLog, [string]$ErrorLog) {
    # Keep the server hidden, but capture its output locally for startup errors.
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = Join-Path $env:WINDIR 'System32\cmd.exe'
    $localEnvironment = @(
        'set "DOTNET_CLI_HOME=' + $env:DOTNET_CLI_HOME + '"'
        'set "APPDATA=' + $env:APPDATA + '"'
        'set "LOCALAPPDATA=' + $env:LOCALAPPDATA + '"'
        'set "NUGET_PACKAGES=' + $env:NUGET_PACKAGES + '"'
        'set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"'
    ) -join ' && '
    $startInfo.Arguments = '/d /c "' + $localEnvironment + ' && ' + $CommandLine + ' > "' + $OutputLog + '" 2> "' + $ErrorLog + '"'
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    $child = New-Object System.Diagnostics.Process
    $child.StartInfo = $startInfo
    if (-not $child.Start()) { throw "Could not start command: $CommandLine" }
    $script:launcherStdoutTask = $child.StandardOutput.ReadToEndAsync()
    $script:launcherStderrTask = $child.StandardError.ReadToEndAsync()
    return $child
}

$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.tools'
$env:APPDATA = Join-Path $repoRoot '.tools\appdata'
$env:LOCALAPPDATA = Join-Path $repoRoot '.tools\localappdata'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.tools\nuget-packages'

# Reuse an already-running local server instead of starting a second copy.
try {
    $response = Invoke-WebRequest -UseBasicParsing -Uri $health -TimeoutSec 1
    if ($response.StatusCode -eq 200) {
        Start-Process $url
        exit 0
    }
} catch { }

$logDirectory = Join-Path $repoRoot '.tools\logs'
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
$logStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outputLog = Join-Path $logDirectory "garage-games-v2-$logStamp.out.log"
$errorLog = Join-Path $logDirectory "garage-games-v2-$logStamp.err.log"

try {
    if (Test-Path -LiteralPath $published) {
        # Quote each path so spaces in "Garage Games" remain within one argument.
        $arguments = '"' + $published + '" --data-path "' + $dataPath + '" --urls "' + $url.TrimEnd('/') + '"'
        $process = Start-LocalHiddenProcess $arguments $scriptRoot $outputLog $errorLog
    } else {
        if (-not (Test-Path -LiteralPath $dotnet)) {
            Show-StartupFailure "The bundled .NET runtime was not found at: $dotnet" $outputLog $errorLog
            exit 1
        }
        if (-not (Test-Path -LiteralPath $project)) {
            Show-StartupFailure "The Garage Games V2 project was not found at: $project" $outputLog $errorLog
            exit 1
        }
        $arguments = '"' + $dotnet + '" run --configuration Release --no-build --no-restore --project "' + $project + '" -- --data-path "' + $dataPath + '" --urls "' + $url.TrimEnd('/') + '"'
        $process = Start-LocalHiddenProcess $arguments $repoRoot $outputLog $errorLog
    }
} catch {
    Show-StartupFailure "Windows could not start Garage Games v2: $($_.Exception.Message)" $outputLog $errorLog
    exit 1
}

$ready = $false
for ($attempt = 0; $attempt -lt 40; $attempt++) {
    Start-Sleep -Milliseconds 250
    try {
        $response = Invoke-WebRequest -UseBasicParsing -Uri $health -TimeoutSec 1
        if ($response.StatusCode -eq 200) {
            $ready = $true
            break
        }
    } catch {
        if ($process.HasExited) { break }
    }
}

if ($ready) {
    Start-Process $url
} else {
    $details = "Garage Games v2 did not become ready on $url."
    if ($process.HasExited) {
        $details += " The launcher process exited with code $($process.ExitCode)."
    } else {
        $details += " The launcher process (PID $($process.Id)) is still running."
    }
    Show-StartupFailure $details $outputLog $errorLog
    if ($process.HasExited) {
        foreach ($task in @($script:launcherStdoutTask, $script:launcherStderrTask)) {
            if ($null -ne $task -and $task.IsCompleted -and -not [string]::IsNullOrWhiteSpace($task.Result)) {
                Write-Host $task.Result
            }
        }
    }
    if ($process.HasExited) { exit $process.ExitCode }
    exit 1
}
