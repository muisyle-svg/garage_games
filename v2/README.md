# Garage Games v2

Garage Games v2 is a local .NET 10 ASP.NET Core application with an embedded
SQLite store. Its MVP scorekeeper has 13 regular events, competitor selection,
a five-minute run clock, pause/resume, virtual two-press event buttons, editable
timestamps and manual event points, and a history that can be corrected later.
Timed-out runs remain as incomplete history and do not prevent starting the next
competitor. The original app, firmware, and existing historical data are not
used or modified.

The virtual buttons are for testing. Physical hardware, the keypad and magnetic
special events, bonus rounds, and event-specific scoring formulas are deferred.
The app runs locally and has no Google Sheets or other network-service
dependency; it does not yet claim physical hardware support.

## Launch

From the repository root in PowerShell:

```powershell
$env:DOTNET_CLI_HOME = (Resolve-Path .tools).Path
$env:APPDATA = (Resolve-Path .tools).Path + '\appdata'
$env:LOCALAPPDATA = (Resolve-Path .tools).Path + '\localappdata'
$env:NUGET_PACKAGES = (Resolve-Path .tools).Path + '\nuget-packages'
& .tools/dotnet/dotnet.exe restore v2/GarageGames.V2.slnx --configfile NuGet.Config
& .tools/dotnet/dotnet.exe run --project v2/src/GarageGames.V2/GarageGames.V2.csproj -- --data-path "$env:LOCALAPPDATA\GarageGamesV2" --urls http://127.0.0.1:5187
```

Open `http://127.0.0.1:5187/` for the operator view and use **Open scoreboard**
to open the separate spectator view at `/scoreboard` (the alias is also directly
usable). The default data path is
`%LOCALAPPDATA%\GarageGamesV2`; pass `--data-path` to isolate a test or event
store. The app enforces loopback-only URLs; a `--urls` value such as `0.0.0.0`
is rejected rather than exposed. For normal Windows use, double-click
`v2\Start Garage Games V2.cmd`. It opens the operator view in your browser and
leaves Garage Games in the Windows notification area (system tray). Right-click
the tray icon for **Open Garage Games** or **Exit**; double-clicking the icon
also opens the app. Closing the browser only closes that window. Choosing Exit
asks the server started by this tray session to shut down cleanly so the local
database can close safely. The tray launcher is single-instance for the
current Windows user, creates no Windows startup entry, and stores launcher logs under
`v2\.tools\logs`.

If the launcher finds a healthy server that was already running before this
tray session, it reuses that server. In that case the tray menu says
**Exit (leave existing server running)** and will not stop it or any unrelated
process. If shutdown of a server owned by this tray session cannot be confirmed,
the launcher will not force-kill it; the tray stays available so Exit can be
retried. If a startup problem appears, check the `.out.log` and `.err.log` files
under `v2\.tools\logs`.

The launcher uses the repository-local toolchain and a distinct `GarageGamesV2`
data directory. If a published executable exists at
`v2\publish\win-x64\GarageGames.V2.exe`, the launcher uses it directly.
Because the launcher deliberately redirects its local .NET environment, its
data path is `v2\.tools\localappdata\GarageGamesV2` in this repository. A
direct command-line launch without those environment overrides uses the normal
Windows `%LOCALAPPDATA%\GarageGamesV2` path instead.

Use the on-screen event buttons to test a run. Press an event once to record its
start and again to record its finish; different events may overlap. Event times
are displayed as seconds remaining from the run limit, making earlier event
timestamps larger, while the app persists elapsed milliseconds. Completing all
regular events freezes the timer and leaves the run marked finished but
unrecorded until **Record result**. The operator can also finish a partial run
and choose whether to record it. Event times and points remain editable before
or after recording; points are manually entered and summed.

## Storage and recovery

SQLite uses WAL mode and `synchronous=FULL`; each command and accepted or
rejected input message is committed transactionally. The run stores active
elapsed time, not wall-clock downtime. If a process stops while a run is active,
the next start changes that run to paused, preserves its last committed active
elapsed value, and writes a recovery ledger entry. No downtime is awarded and
the operator must resume explicitly. Unknown schema versions, missing required
tables, failed integrity checks, or malformed persisted snapshots fail visibly;
the app never resets the database.

The local `/api/backup` endpoint creates an explicit SQLite backup in the data
directory's `backups` folder. `/api/export` returns an operator export of the
current state and audit ledger.

The server checkpoints an active run at least once per second. An unexpected
stop can therefore lose at most the last checkpoint interval of active elapsed
time; recovery still pauses the run and never awards the uncertain downtime.

## Verification

```powershell
$env:DOTNET_CLI_HOME = (Resolve-Path .tools).Path
$env:APPDATA = (Resolve-Path .tools).Path + '\appdata'
$env:LOCALAPPDATA = (Resolve-Path .tools).Path + '\localappdata'
$env:NUGET_PACKAGES = (Resolve-Path .tools).Path + '\nuget-packages'
& .tools/dotnet/dotnet.exe restore v2/GarageGames.V2.slnx --configfile NuGet.Config
& .tools/dotnet/dotnet.exe build v2/GarageGames.V2.slnx -c Release --no-restore
& .tools/dotnet/dotnet.exe run --project v2/tests/GarageGames.V2.Tests/GarageGames.V2.Tests.csproj -c Release --no-build --no-restore
node --test v2/tests/GarageGames.V2.Tests/frontend-time.test.cjs
```

For a self-contained Windows build, run:

```powershell
& .tools/dotnet/dotnet.exe publish v2/src/GarageGames.V2/GarageGames.V2.csproj -c Release -r win-x64 --self-contained true -o v2/publish/win-x64
```

The launcher will then use `v2/publish/win-x64/GarageGames.V2.exe`. Hardware mode
is reserved for a future physical transport; virtual start, simulated device
availability, and simulator clock/input routes are disabled there.
