# Garage Games v2

This is the first runnable local v2 implementation. It is a separate .NET 10
ASP.NET Core loopback application with an embedded SQLite store. The original
Garage Games application, firmware, root solution, and existing data are not
used or modified.

The first executable increment is explicitly Simulation mode. It exercises the
same validated message envelope and dispatcher used by the future transport:
master start, standard event presses, keypad incorrect/success signals, magnetic
arcade start/finish signals, pause/resume, timeout, queueing, corrections, and
scoreboard updates. It does not claim physical hardware support.

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
is rejected rather than exposed. Double-click `v2\Start Garage Games V2.cmd`
for the easiest launch. It uses the repository-local toolchain and a distinct
`GarageGamesV2` data directory. If a published executable exists at
`v2\publish\win-x64\GarageGames.V2.exe`, the launcher uses it directly.
Because the launcher deliberately redirects its local .NET environment, its
data path is `v2\.tools\localappdata\GarageGamesV2` in this repository. A
direct command-line launch without those environment overrides uses the normal
Windows `%LOCALAPPDATA%\GarageGamesV2` path instead.

Use the operator's Simulation panel to toggle device availability, send virtual
signals, and advance the simulation clock. The panel is only registered when the
app is not started with `--hardware-mode`. This version has no physical firmware
adapter, wireless transport, Google service, or network dependency.

## Storage and recovery

SQLite uses WAL mode and `synchronous=FULL`; each command and accepted or
rejected input message is committed transactionally. The run stores active
elapsed time, not wall-clock downtime. If a process stops while a run is active,
the next start changes that run to paused, preserves its last committed active
elapsed value, and writes a recovery ledger entry. No downtime is awarded and
the operator must resume explicitly. Unknown schema versions, missing required
tables, failed integrity checks, or malformed persisted snapshots fail visibly;
the app never resets the database.

The **Create backup** operator action creates an explicit SQLite backup in the
data directory's `backups` folder. **Export JSON** returns an operator export of
the current state and audit ledger.

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
```

For a self-contained Windows build, run:

```powershell
& .tools/dotnet/dotnet.exe publish v2/src/GarageGames.V2/GarageGames.V2.csproj -c Release -r win-x64 --self-contained true -o v2/publish/win-x64
```

The launcher will then use `v2/publish/win-x64/GarageGames.V2.exe`. Hardware mode
is reserved for a future physical transport; virtual start, simulated device
availability, and simulator clock/input routes are disabled there.
