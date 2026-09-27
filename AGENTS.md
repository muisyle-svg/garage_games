# Garage Games working guidance

## Scope and versioning

- This directory is the Git root; its parent `Garage Games` is only a container.
- The app is v2 (`src/GarageGames.V2`). The v1 controller, PlatformIO firmware,
  Google Sheets sync, and legacy Apps Script scorekeeper live only at the
  `archive/v1-final` tag; do not restore them without an explicit request.
- Inspect status before edits. Use a feature branch; preserve user changes and
  existing history. Never force-push or discard work without explicit permission.
- Answer status/diagnosis requests with targeted evidence. Expand checks only when
  findings require them. Keep live event data and credentials out of Git and logs.
  Live data is in `%LOCALAPPDATA%\GarageGamesV2`; never write to it from tests.

## Efficient investigation

- Search filenames first, then read the relevant function or small line range.
  Run state lives in `RunService.cs`, persistence in `RunStore.cs`, and the USB
  master bridge in `PhysicalMasterSerialService.cs` / `MasterProtocol.cs`.
- Exclude `.tools`, `bin`, `obj`, and `publish` from source discovery unless
  investigating those outputs specifically.
- Do not repeat unchanged file reads or status/history dumps within a task.
  Limit routine tool output to about 2,000 tokens; save long logs locally and
  retrieve the relevant failure excerpt. Expand when needed to resolve uncertainty.
- Prefer a working Git CLI or connector over repeated GUI inspection. After an
  unchanged failure, diagnose once and try a supported alternative; do not repeat
  equivalent attempts without new evidence.
- Retain returned process/session IDs and resume running commands instead of
  relaunching them. Parallelize independent reads, not shared build outputs.
- Report meaningful findings concisely; avoid narrating every tool operation.

## Windows verification

- The verified toolchain is `.tools/dotnet/dotnet.exe`. Use repository-local
  process environment paths: `DOTNET_CLI_HOME=.tools`, `APPDATA=.tools/appdata`,
  `LOCALAPPDATA=.tools/localappdata`, and `NUGET_PACKAGES=.tools/nuget-packages`
  (resolve these to absolute paths).
- Restore `GarageGames.slnx` with the repository `NuGet.Config`, then build Release
  with `--no-restore`. After that finishes, run `tests/GarageGames.V2.Tests` in
  Release with `--no-build --no-restore` from the repository root. Check each exit
  code before proceeding.
- A running tray instance locks `src/GarageGames.V2/bin/Release`. Do not stop it;
  build to a separate `-p:OutDir=` instead.
- Keep dependency vulnerability auditing enabled for normal checks; disclose any
  temporary offline bypass rather than reporting the audit as passed.
- Run the Node frontend and firmware source tests with
  `node --test tests/GarageGames.V2.Tests/` when Node is available; CI runs them.
- Firmware is Arduino IDE sketches under `firmware/` (board `XIAO_ESP32C3`,
  `TM1637Display` library). Flash the Garage master and spokes together when the
  serial or ESP-NOW protocol changes. Hardware behavior is not yet verified.
