# Garage Games working guidance

## Scope and versioning

- This directory is the Git root; its parent `Garage Games` is only a container.
- Inspect status before edits. Use a feature branch; preserve user changes and
  existing history. Never force-push or discard work without explicit permission.
- Answer status/diagnosis requests with targeted evidence. Expand checks only when
  findings require them. Keep live event data and credentials out of Git and logs.

## Efficient investigation

- Search filenames first, then read the relevant function or small line range.
  For Google sync, start with `GoogleSyncHostedService.cs`; avoid broad `sync`
  searches that also match every `Async` method.
- Exclude `.tools`, `.pio`, `bin`, `obj`, `release`, and `artifacts` from source
  discovery unless investigating those outputs specifically.
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

- The last verified controller toolchain is `.tools/dotnet/dotnet.exe`.
  Use repository-local process environment paths: `DOTNET_CLI_HOME=.tools`,
  `APPDATA=.tools/appdata`, `LOCALAPPDATA=.tools/localappdata`, and
  `NUGET_PACKAGES=.tools/nuget-packages` (resolve these to absolute paths).
- Restore `GarageGames.slnx` with the repository `NuGet.Config`, then build Release
  with `--no-restore`. After that finishes, run `tests/GarageGames.Tests` in Release
  with `--no-build --no-restore`. Check each exit code before proceeding.
- Keep dependency vulnerability auditing enabled for normal checks; disclose any
  temporary offline bypass rather than reporting the audit as passed.
- For relevant changes, run `node scripts/validate-config.mjs` and
  `node simulator/simulate.mjs`. Documentation-only changes need diff checks.
- PlatformIO has a project cache at `.tools/platformio-core`; set
  `PLATFORMIO_CORE_DIR` explicitly when using it. Prior checks found missing native
  gcc/g++ and an ESP compiler launch failure. Check availability once; avoid
  repeated installations or builds until the specific toolchain issue is resolved.
