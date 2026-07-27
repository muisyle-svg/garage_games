# Garage Games 2026

Garage Games 2026 is a local-first event controller for a timed series of
ESP32-powered minigames. A Windows laptop owns the run state and records every
accepted event in SQLite. A plugged-in XIAO ESP32-C3 master bridges Wi-Fi to
battery-powered ESP-NOW stations. Google Sheets is an asynchronous participant
view, correction queue, and emergency fallback rather than the live database.

## Repository layout

- `src/GarageGames.Core` - run projection, protocol contracts, and scoring.
- `src/GarageGames.Controller` - Windows web controller, SQLite, bridge, exports,
  backups, and Google synchronization.
- `firmware` - PlatformIO master, standard station, prompt-game example, and
  shared protocol/runtime libraries.
- `google-apps-script` - authenticated Sheet mirror, correction queue, and
  emergency workflow.
- `config/seasons` - versioned game rosters and scoring settings.
- `tests` and `simulator` - deterministic tests and a 20-station loss simulator.
- `docs` - setup, operation, protocol, recovery, and release runbooks.

## Quick start for operators

1. Download the latest Windows release from GitHub Releases.
2. Start `GarageGames.Controller.exe`.
3. Open the displayed local URL and complete the setup checklist.
4. Power the master, then assign discovered station IDs to event slots.
5. Add competitors, select the 2026 season, and run diagnostics.

No developer tools are required for released builds. Google credentials,
participant databases, Wi-Fi passwords, and signing secrets are never committed.

## Developer checks

```powershell
dotnet build GarageGames.slnx -c Release
dotnet run --project tests/GarageGames.Tests -c Release
pio test -d firmware -e native
pio run -d firmware
```

See `docs/OPERATOR_RUNBOOK.md` for the event-day workflow,
`docs/GOOGLE_SHEET_SETUP.md` for synchronization setup, and
`docs/HARDWARE_VALIDATION.md` for release validation.
