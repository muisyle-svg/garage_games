# Garage Games v2

Garage Games v2 is a local .NET 10 ASP.NET Core application with an embedded
SQLite store. Its MVP scorekeeper has 13 regular events, competitor selection,
a five-minute run clock, pause/resume, virtual two-press event buttons, editable
timestamps, automatic event points with editable overrides and bonus scoring,
and a history that can be corrected later.
Timed-out runs remain as incomplete history and do not prevent starting the next
competitor. The original Garage Games app and firmware remain untouched;
existing historical data is not used or modified.

The app supports virtual Start without a connected master and can also connect a
manually attached XIAO ESP32-C3 over USB serial. A short physical button press
starts a competitor after the operator explicitly arms that competitor in the
app. The current `GG1` serial protocol runs at 115200 baud; a five-second master
button hold enters the existing Speed game when no Garage run is active or
paused. Garage event scoring from physical spokes, the keypad and magnetic
special events, bonus rounds, and event-specific scoring formulas remain future
work. The app runs locally and has no Google Sheets or other network-service
dependency.

The combined master sketch is in `v2\firmware\garage_games_master`. The
untouched original Speed master and spoke sketches are versioned alongside it
under `v2\firmware\speed_button_master` and `v2\firmware\speed_button_spoke`.
The current master retains Speed discovery on ESP-NOW channel 1; its Garage
serial bridge and physical Start have not yet been verified on hardware.

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
This takes precedence over the source project, so an old published executable
can hide newer source changes until it is republished or otherwise removed.
Because the launcher deliberately redirects its local .NET environment, its
data path is `v2\.tools\localappdata\GarageGamesV2` in this repository. A
direct command-line launch without those environment overrides uses the normal
Windows `%LOCALAPPDATA%\GarageGamesV2` path instead.

When the launcher falls back to the source project, it runs with
`--no-restore`. The app now references `System.IO.Ports`, so run the restore
command above with the repository `NuGet.Config` before launching the source
build; the launcher does not restore packages itself. Exit the current tray
instance before trying updated code; an already-running tray session can keep
serving its existing process.

Use the on-screen event buttons to test a run. Press an event once to record its
start and again to record its finish; different events may overlap. The operator
Start begins the supplied 3-2-1 Go audio; the run timer and event buttons remain
inactive until playback ends. A blocked or failed playback leaves the run in
Countdown and offers an explicit retry. This audio gate applies to virtual and
physical Start; the TV scoreboard never plays the audio. Event times are shown
and edited as M:SS remaining from the run limit (seconds-only input is also
accepted), making earlier event timestamps larger, while the app persists
elapsed milliseconds. Untouched event times retain their original millisecond
precision. The simulator's custom clock advance also accepts M:SS or seconds
and sends milliseconds to the backend. Completing all
regular events freezes the timer and leaves the run marked finished but
unrecorded until **Record result**. The operator can also finish a partial run
and choose whether to record it. Event times and points remain editable before
or after recording. When correcting a stopped or recorded run, missing event
timestamps may be added anywhere within the run limit; the saved elapsed time
extends through the latest corrected event. Points preview automatically from
event times using the edition's scoring rules unless manually overridden;
clearing a points override restores automatic scoring. Run bonus scoring is
also editable.

## Physical master smoke test

Physical hardware support is implemented but not yet physically verified: the
XIAO board is not connected, and the firmware has not been compiled or flashed.
The countdown audio flow and physical master/spoke interaction have not been
validated on hardware; virtual/UI behavior is the only validation target so far.
Treat the following as a test procedure, not a report of successful hardware
operation.

1. Exit the current Garage Games tray instance so the next launch loads the
   updated application. Make sure the source project has been restored as
   described above; if `v2\publish\win-x64\GarageGames.V2.exe` exists, the
   launcher will prefer that published executable.
2. In Arduino IDE, open
   `v2\firmware\garage_games_master\garage_games_master.ino`. Install the
   ESP32 Arduino board package and `TM1637Display`, select board
   `XIAO_ESP32C3` and the board's COM port, then flash over USB.
3. Close Arduino IDE's Serial Monitor before the app opens the port. Start
   Garage Games, select the master's COM port, choose **Connect**, and wait for
   the master status to show `IDLE`. The serial protocol is `GG1` at 115200 baud.
4. Choose a competitor and use **Arm for physical Start**. A short press and
   release of the master button starts that competitor's run. The on-screen
   virtual **Start** remains available with no master connected.
5. An active or paused Garage run blocks the five-second Speed hold. With no
   active or paused Garage run, hold the master button for five seconds to
   start Speed discovery. Discovery currently requires at least three
   compatible spokes on ESP-NOW channel 1.

The current integration uses the master for Garage Start and run-status
display; physical spokes still run the existing Speed protocol and do not yet
report Garage event inputs.

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

The launcher will then use `v2/publish/win-x64/GarageGames.V2.exe`. The
`--hardware-mode` flag disables simulated device availability and simulator
clock/input routes and the virtual event-press route, while retaining the
physical USB serial master transport and operator controls, including virtual
Start. The default launch keeps virtual event presses enabled alongside master
connection, so use it for the first master-plus-virtual-events test.

## Future spoke architecture

The planned spoke network keeps ESP-NOW on fixed channel 1 and uses it only for
master-to-spoke and spoke-to-master traffic. Garage event IDs will map through
a separate Garage protocol, with per-device sequence numbers, acknowledgments,
preflight status, and LED feedback. This design will not use Google Sheet row
IDs or Wi-Fi. Battery-powered spokes must keep their radio listening to receive
a wireless start; deep sleep cannot receive that start signal.
