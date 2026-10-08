# Garage Games

Garage Games (v2) is a local .NET 10 ASP.NET Core application with an embedded
SQLite store. Its MVP scorekeeper has 13 regular events, competitor selection,
a five-minute run clock, pause/resume, virtual two-press event buttons, editable
timestamps, automatic event points with editable overrides and bonus scoring,
and a history that can be corrected later.
Timed-out runs remain as incomplete history and do not prevent starting the next
competitor. A prior data folder is copied only when needed to move it to the
standard Windows data location; its original folder is retained.

The earlier v1 controller (Google Sheets sync, PlatformIO firmware) and the
original Apps Script scorekeeper were removed from the working tree when v2
became the only version. They remain available at the `archive/v1-final` tag:
`git checkout archive/v1-final`.

## Repository layout

- `src/GarageGames.V2` - the scorekeeper app (API, SQLite store, serial master bridge, web UI).
- `tests/GarageGames.V2.Tests` - .NET test runner plus Node frontend and firmware tests.
- `firmware` - Arduino sketches for the combined Garage master and spoke, and the standalone Speed Button game.
- `config/edition-2026.json` - default edition roster and scoring.
- `docs` - design requirements and implementation decisions.
- `Start Garage Games V2.cmd` - tray launcher for normal Windows use.

The app supports virtual Start without a connected master and can also connect a
manually attached XIAO ESP32-C3 over USB serial. A short physical button press
starts a competitor after the operator explicitly arms that competitor in the
app. The current `GG1` serial protocol runs at 115200 baud; a five-second master
button hold enters the existing Speed game when no Garage run is active or
paused. Regular and keypad Garage events can receive physical spoke presses
over ESP-NOW (see "Keypad events" below); magnetic special events and bonus
rounds remain future work, except the Chaos Heist arcade event (see "Chaos Heist
arcade event" below). The app runs locally and has no Google Sheets or
other network-service dependency.

The combined Garage Games/Speed Button master and spoke sketches are in
`firmware\garage_games_master` and `firmware\garage_games_spoke`.
The standalone Speed Button sketches remain in
`firmware\speed_button_master` and `firmware\speed_button_spoke`.
The Garage spoke protocol uses ESP-NOW channel 1. Firmware, the Garage serial
bridge, and physical spoke events have not yet been verified on hardware.

## Launch

From the repository root in PowerShell:

```powershell
$env:DOTNET_CLI_HOME = (Resolve-Path .tools).Path
$env:APPDATA = (Resolve-Path .tools).Path + '\appdata'
$env:NUGET_PACKAGES = (Resolve-Path .tools).Path + '\nuget-packages'
& .tools/dotnet/dotnet.exe restore GarageGames.slnx --configfile NuGet.Config
& .tools/dotnet/dotnet.exe run --project src/GarageGames.V2/GarageGames.V2.csproj -- --legacy-data-path "$PWD\.tools\localappdata\GarageGamesV2" --urls http://127.0.0.1:5187
```

Open `http://127.0.0.1:5187/` for the operator view and use **Open scoreboard**
to open the separate spectator view at `/scoreboard` (the alias is also directly
usable). The default data path is
`%LOCALAPPDATA%\GarageGamesV2` for both the tray launcher and direct launches;
pass `--data-path` only when intentionally isolating a test or event store. The
app enforces loopback-only URLs; a `--urls` value such as `0.0.0.0`
is rejected rather than exposed. For normal Windows use, double-click
`Start Garage Games V2.cmd`. The tray launcher starts the app with
`--hardware-mode`, so simulator-only routes are off during an event. It opens the operator view in your browser and
leaves Garage Games in the Windows notification area (system tray). Right-click
the tray icon for **Open Garage Games** or **Exit**; double-clicking the icon
also opens the app. Closing the browser only closes that window. Choosing Exit
asks the server started by this tray session to shut down cleanly so the local
database can close safely. The tray launcher is single-instance for the
current Windows user, creates no Windows startup entry, and stores launcher logs under
`.tools\logs`.

Before reusing an existing server, the launcher verifies its build fingerprint,
application folder, and data folder. If any identity is missing or does not
match, it will not connect or start a second copy; close the other Garage Games
instance cleanly and retry. A verified server that was already running before
this tray session is reused, and the tray menu says
**Exit (leave existing server running)**. The launcher will not stop it or any
unrelated process. If shutdown of a server owned by this tray session cannot be confirmed,
the launcher will not force-kill it; the tray stays available so Exit can be
retried. If a startup problem appears, check the `.out.log` and `.err.log` files
under `.tools\logs`.

The launcher uses the repository-local .NET toolchain but stores run data in
`%LOCALAPPDATA%\GarageGamesV2`, outside the repository and Git history. If the
older repo-local data folder at `.tools\localappdata\GarageGamesV2` exists
and the standard folder does not, the app copies the database and its backups
to the standard folder after confirming the old app no longer holds its data
lock. The old folder is retained unchanged. If both folders already contain a
database, the standard `%LOCALAPPDATA%` store is used and the repo-local copy
is left untouched.

The normal shortcut runs the current source, rebuilding only when it changed.
It fingerprints the source and edition files at each launch and compares that
with a marker saved next to the built app after the last successful build. If
they match, the built app starts directly (about two seconds from double-click
to ready). If the code changed, or the built app was replaced by another build,
the tray shows "Updating Garage Games", rebuilds once, and then starts. A failed
rebuild never falls back to the previous build; the build logs are under
`.tools\logs`. To intentionally launch a published build, run
`& '.\Start Garage Games V2.ps1' -UsePublished`; the tray still checks that
build's identity before reuse.

Rebuilds run with `--no-restore`, so run the restore command above with the
repository `NuGet.Config` after dependency changes; the launcher does not restore
packages itself. The launcher waits up to two minutes for startup and opens the
page as soon as the app responds. Exit the current tray instance before trying
updated code; an already-running tray session can keep serving its existing
process.

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
and choose whether to record it. Only a run that is over can be recorded:
**Record result** appears beside the run status once the run has finished or timed
out, never while it is armed or in progress, and recording never ends a run.
Finishing with time still on the clock asks first, and until it is recorded a
finished run can be **Reopened** (paused, with the clock where it stopped) unless
every event is complete or the bonus round ended it. An unrecorded timed-out run
holds the next run: Start and Up Next wait until it is recorded or discarded. A
finish press within half a second of the event's start press is taken as a double
press and ignored; the event keeps running (the operator's keypad override tap
has the same guard). Event times and points remain editable before
or after recording. When correcting a stopped or recorded run, missing event
timestamps may be added anywhere within the run limit; the saved elapsed time
extends through the latest corrected event. Points preview automatically from
event times using the run's saved edition rules unless manually overridden;
clearing a points override restores automatic scoring. Run bonus scoring is
also editable. Manual event points and the run bonus may be negative (a
penalty) and subtract from the total. Scorecard edits count only once saved:
until then the page marks them "Unsaved" (on the event tiles, the totals, and a
notice in the undo row above the event buttons) because the TV and results don't include them.
Messages, notices, and buttons that come and go on the scorekeeping tab use space
reserved for them (a message slot beside "Current run", another beside the tabs on
other tabs), so nothing you might be about to click moves when they appear.
Edits stay editable and savable after the run finishes or times out, and
**Save edits & record** saves them before recording. Saving applies to the run's
latest state, so a button press or timeout while you type doesn't reject the
save; only the fields you changed are written. Each event may override `basePoints`, `minimumPoints`,
`decayPoints`, `decayEverySeconds`, and `graceSeconds`. Missing values inherit
the edition's global base, decay amount, and interval, and grace defaults to
zero. When an event sets `basePoints` without `minimumPoints`, its floor is half
that base rounded up; otherwise the global minimum is inherited. Decay starts
after the grace period, with the first drop at the grace boundary: 10 seconds
of grace with a 5 second interval drops at 10, 15, 20 seconds, and so on. With
zero grace, the first drop remains at one full interval. Automatic scores never
fall below their effective minimum. Manual point overrides remain in place
until cleared.

## Setup and device readiness

Use the operator **Setup** tab to change the active edition name and event
roster, including event names, types, and device assignments. The setup API is
`GET /api/setup` and `PUT /api/setup`. Each event may include `basePoints`,
`minimumPoints`, `decayPoints`, `decayEverySeconds`, and `graceSeconds`. These
fields are optional nullable integers. Effective base, minimum, and decay
values must be within 0–1,000,000 points; intervals and grace periods must be
within 1–86,400 and 0–86,400 seconds respectively. The minimum cannot exceed
the effective base. `GET /api/setup` and the `PUT` response include `scoring`
with the actual edition-wide defaults. For compatibility, `scoring` is optional
in a PUT request and any submitted value is ignored; only per-event fields are
editable through setup.

To match physical buttons to MAC addresses, connect the master (Garage idle
mode, no run underway) and press a button: its MAC is added to Setup's
**Discovered hardware** list (newest first, no scan needed) and lights up there,
along with any event already using it, and "Last button pressed" names it. The
quickest way to assign: click **Assign** on an event, then press that event's
physical button (or click a MAC in the list); it is assigned to that event, moving
off any other event that had it. Click **Assign** again or press Escape to cancel,
and **Save setup** to keep the assignments. Going the other way, **Flash** beside
a MAC (in the list or on an event row) blinks that physical button.
The active setup is stored transactionally in SQLite metadata; a database
backup is created before a changed setup is saved. Setup is locked while a run
is in progress or waiting to be recorded. Each run keeps its own edition
snapshot, so editing setup never rewrites historical results. If the event
roster or any per-event scoring setting changes while recorded runs exist in
the current edition, the saved setup gets a new edition ID and the leaderboard
starts a separate edition; earlier scores remain in history and are not
deleted.

Use **Scan devices** or arm a run while the physical master is connected. The
server sends `GG1 STATUS` immediately before `GG1 SCAN <id>`; the master
reports discovered 12-hex device IDs. `POST /api/master/scan` returns
`connected`, `completed`, `detectedDeviceIds`, and per-event statuses
`Responding`, `NotResponding`, or `NotScanned`. A scan only confirms devices
that reported during that scan; it does not verify event wiring or gameplay
inputs. Placeholder assignments such as `station-01`, a disconnected master,
an incomplete/BUSY scan, or a process restart are **Unverified**, not Online.
Missing or unverified spokes do not prevent arming: use the operator's virtual
event controls as a fallback. A physical press that arrives through the current
master handshake from the MAC assigned to a standard event is accepted even if
that spoke missed the latest scan (for example, it was asleep or out of range
while the run was armed); the press itself marks the spoke Online. Presses from
unassigned MACs remain rejected. Disconnecting the master invalidates its last
scan readiness.

## Physical master smoke test

Physical hardware support is compiled but not flashed or physically verified:
the XIAO board is not connected. The countdown audio flow and physical
master/spoke interaction have not been validated on hardware; virtual/UI
behavior is the only validation target so far.
Treat the following as a test procedure, not a report of successful hardware
operation.

1. Exit the current Garage Games tray instance so the next launch loads the
   updated application. Make sure the source project has been restored as
   described above. The standard shortcut now runs the source project; a
   published build is used only when explicitly requested with `-UsePublished`.
2. In Arduino IDE, open
   `firmware\garage_games_master\garage_games_master.ino`. Install the
   ESP32 Arduino board package and `TM1637Display`, select board
   `XIAO_ESP32C3` and the board's COM port, then flash over USB.
3. Close Arduino IDE's Serial Monitor before the app opens the port. Start
   Garage Games, select the master's COM port, choose **Connect**, and wait for
   the master status to show `IDLE`. The serial protocol is `GG1` at 115200 baud.
4. For each physical event button, flash
   `firmware\garage_games_spoke\garage_games_spoke.ino` to a XIAO ESP32-C3.
   This combined sketch is used on every Garage spoke and retains Speed Button
   gameplay. Read its 12-hex MAC from the startup serial message, then assign
   that MAC to the matching event in the app's **Setup** tab. Use one physical
   spoke and leave other events on their virtual controls if desired. Spokes use
   ESP-NOW channel 1 and do not connect to the Windows app over USB. If a press
   exhausts retries without receiving a result, that spoke fast-blinks red and
   blocks further physical presses for that event until a matching late result
   arrives or a new Garage session begins; use its virtual event control as
   fallback. An explicit `REJECTED` result allows another physical attempt.
5. Choose a competitor and use **Arm for physical Start**. A short press and
   release of the master button starts that competitor's run. The on-screen
   virtual **Start** remains available with no master connected.
6. An active or paused Garage run blocks the five-second Speed hold. With no
   active or paused Garage run, hold the master button for five seconds to
   attempt Speed discovery. The existing Speed game requires at least three
   compatible spokes and ends the attempt normally if fewer are found.

The master handles Garage Start, run-status display, and physical spoke event
inputs. Physical operation still requires on-device verification.

If the master's USB connection drops without **Disconnect** being chosen (a
bumped cable, a USB glitch, the master rebooting), the app reopens the same port
every two seconds until it is back, and the scorekeeper shows "Physical master
disconnected · reconnecting…" in its message slot until then; virtual buttons keep
working. While it runs, the app also asks Windows not to sleep (start it with
`--allow-sleep` to turn that off). Also turn off USB selective suspend in Windows
power options for the event laptop. The app sends each button's state when it
changes (twice), plus one button per second in rotation, so the master's serial
input is never flooded.

A press made while the run was going that reaches the app just after it paused
or timed out (radio retries can take a few hundred milliseconds) still counts at
the time it was pressed; the timed-out run stays timed out. Presses made after
the pause or buzzer are refused as before.

## Storage and recovery

SQLite uses WAL mode and `synchronous=FULL`; each command and accepted or
rejected input message is committed transactionally. The run stores active
elapsed time, not wall-clock downtime. If a process stops while a run is active,
the next start changes that run to paused, preserves its last committed active
elapsed value, and writes a recovery ledger entry. No downtime is awarded and
the operator must resume explicitly. Unknown schema versions, missing required
tables, failed integrity checks, or malformed persisted snapshots fail visibly;
the app never resets the database.

The database schema is version 3. On first start, an older database is backed up
to `backups\garage-games-v2-pre-schema-3-*.db` and then upgraded in place.
Version 2 added when a run was recorded: completed runs keep their recorded
status, but timed-out runs recorded under version 1 were never saved as recorded,
so record them again from History after upgrading. Version 3 added run deletion.
Older app builds refuse a newer database.

To delete a saved run, select it in Run history and choose **Delete run**. It is
removed from history, the leaderboards, and the TV scoreboard, but kept in the
database with an audit entry and listed under **Deleted runs** at the bottom of
Run history, where **Restore** brings it back. The run in progress can't be
deleted (use Discard). Deleting a recorded redo or replacement makes the result it
replaced count again, and restoring it replaces that result again. A run can't be
restored if the competitor now has a different official result.

To redo an official run, select the competitor with category Official and use
Start or Arm as usual; the app asks you to confirm an official redo. A restart,
redo, or other replacement attempt does not displace the original result until the
replacement is recorded; discarding the attempt leaves the original standing. A
recorded redo replaces the original even if it scores lower.
Recording an older run from History does not advance the on-deck queue, and a
competitor cannot end up with two recorded official results.

The local API answers only loopback `Host` headers and rejects cross-origin
state-changing requests, so another web page open in the operator's browser
cannot drive the scorekeeper.

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
$env:NUGET_PACKAGES = (Resolve-Path .tools).Path + '\nuget-packages'
& .tools/dotnet/dotnet.exe restore GarageGames.slnx --configfile NuGet.Config
& .tools/dotnet/dotnet.exe build GarageGames.slnx -c Release --no-restore
& .tools/dotnet/dotnet.exe run --project tests/GarageGames.V2.Tests/GarageGames.V2.Tests.csproj -c Release --no-build --no-restore
node --test "tests/**/*.test.cjs"
```

For a self-contained Windows build, run:

```powershell
& .tools/dotnet/dotnet.exe publish src/GarageGames.V2/GarageGames.V2.csproj -c Release -r win-x64 --self-contained true -o publish/win-x64
```

To run the published executable, explicitly start the PowerShell launcher with
`-UsePublished`. The
`--hardware-mode` flag disables simulated device availability and simulator
clock/input routes, while retaining the physical USB serial master transport
and operator controls, including virtual Start and virtual event presses. The
trusted operator virtual-event endpoint remains available in hardware mode and
can score events even when their physical devices are unverified or offline.

## Standard spoke protocol and future events

The standard Garage spoke protocol is implemented over ESP-NOW channel 1, with
session gating, per-spoke press sequences, result acknowledgments, and LED
feedback. The firmware has compiled, but physical message delivery and LED
behavior have not been verified on hardware. If bounded retries end without a
result, the spoke reports an unknown outcome with a fast red blink and disables
physical presses for that event until a matching late result resolves it or a
new Garage session begins; use the virtual event control as fallback. A valid
late result applies the event state, and an explicit `REJECTED` result permits
retry. Each press reports its age (time since the button was pushed, plus the
master's relay delay), and the app times the press at that moment rather than at
arrival, capped at 10 seconds and never earlier than the start of the current
active stretch. Spoke press sequences start from a random value each session,
so a spoke that reboots mid-run cannot reuse a sequence the app already
recorded. Flash the master and spokes together: an older master rejects the
new press format. Magnetic arcade (Chaos Heist) messages are described under
"Chaos Heist arcade event". This design does not use Google Sheet row IDs or Wi-Fi. Battery-powered spokes must
keep their radio listening to receive a wireless start; deep sleep cannot
receive that start signal.

## Up Next on the TV

Between runs the TV keeps showing the last run and its scores. To switch it to
the next player before starting, select them (with the run type and length) and
press **Up Next**, left of the start button. The TV then shows them under "Up
next" with the full clock, every event pending, and no points, and moves the
on-deck list past them. Nothing is armed or started; arming or starting a run
replaces the Up Next view, and pressing Up Next again with someone else selected
switches it. It is available once the previous run is recorded or discarded.

## Sounds

Game sounds play from the app itself through this computer's default audio
output (the TV, when it is the connected display and speaker), not from a browser
tab, so they play on time whichever window or tab is in front and need no click
to enable. The app also starts each run at Go on its own; the scorekeeper page
only shows the countdown and reports Go as a backup.

| Sound | When | File |
| --- | --- | --- |
| Countdown voice | A run's countdown starts | `wwwroot/sounds/3-seconds-countdown-deep-voice-game.mp3` |
| Keypad chime | A keypad message appears on the TV | `wwwroot/sounds/keypad-message.wav` |
| Bonus chime | The bonus round's first button lights | `wwwroot/sounds/bonus-start.wav` |
| Keypad buzzer | A wrong code is entered on a keypad | `wwwroot/sounds/keypad-wrong.wav` |
| Keypad success | The keypad event's last required message is solved | `wwwroot/sounds/keypad-success.mp3` |
| Minutes remaining | The clock passes 4:00, 3:00, 2:00 and 1:00 | `wwwroot/sounds/four-minutes-remaining.wav` … `one-minute-remaining.wav` |
| Final countdown | The clock passes 0:05 | `wwwroot/sounds/5-second-countdown.mp3` |
| Time-up buzzer | The clock reaches 0:00, or a bonus-round button is missed | `wwwroot/sounds/time-up-buzzer.wav` |

Replace a file (same name, WAV or MP3) to change a sound; add a `SoundCue` in
`SoundService.cs` with its file to add one. Set the volume with Windows' volume
mixer. Start the app with `--no-sound` to silence it. If a sound cannot play,
the app logs a warning and the game carries on.

## Bonus speed round

When the last event is finished and time remains, the clock keeps running and a
bonus round begins:

1. For 1.5 seconds every button flashes a quick red-yellow-green intro while the
   master polls them. Buttons that answer are the ones that can light up; if none
   answer (for example, no master is connected), every event's tile can be lit
   and the scorekeeper plays it virtually.
2. The bonus chime plays from the computer and the first button lights with the
   Speed game's look (green through yellow to red, blinking faster as its time
   runs out). The TV shows the lit event's name and its time left, with the
   hits and points so far; the time and its bar take the lit button's color and
   blink with it. The scorekeeper highlights that event's tile.
3. Pressing the lit button in time (or tapping its highlighted tile) scores a hit
   and lights a different button. Presses are timed when pressed, with a short
   allowance for the radio. Other buttons and tiles don't count.
4. A missed button ends the run where its window closed; running out of run time
   ends it as a timeout. Either way the points (hits x points per press) are added
   to the run total, shown on the TV and scorekeeper, and counted once recorded.

Buttons that die mid-round (a power switch bumped, a flat battery) are handled
like the standalone Speed game: every button sends a heartbeat through the round,
only buttons still answering are lit, a newly lit button's window starts once it
confirms it is showing the target, and a lit button that never confirms or goes
silent is swapped for another one without counting a miss. If no button is left
answering, the round ends ("no buttons left answering") and keeps its hits. Each
button's bonus memory resets with every run, and each round numbers its targets
from a fresh random start. Buttons on older firmware (no heartbeats) are lit and
timed as before.

Pausing freezes the lit button's time. The scorekeeper's Finish (or Discard) ends
the round too. Presses can't be undone once a bonus round has started; correct
event times on the scorecard instead.

The bonus round counts as an event on the scorecard, in run history, and on the
leaderboards. Its **Bonus round** row shows its start and end times, duration,
hits, and points. Once the round has ended it can be corrected like an event:
change the hits (points follow at the points per press), type points to override
them, adjust the times, or Clear it. Entering a start time and hits on a run that
never reached the bonus records one. The Leaderboards tab has a Bonus round board
that ranks by points (ties share a rank; round length doesn't matter), with runs
that never reached it listed as DNF. Set the round up in Setup under **Bonus
speed round**: its name (shown on the TV, scorecards, and leaderboards; renaming it
doesn't start a new edition version), whether it plays, points per press, the starting seconds per press,
how much and how often that drops, and the minimum. The defaults match the
standalone Speed game (10 s, dropping 1 s every 10 s, to 2 s) at 5 points per
press. Changing them after runs are recorded starts a new edition version, like
changing event scoring. The standalone Speed game on the master (five-second hold)
is unchanged and still runs on its own. Flash the master and spokes together for
the bonus round.

## Keypad events

Keypad messages and their codes come from `config/keypad-answers.csv`, a grid
laid out like the keypad: the header row holds column labels (`1`, `2`, …), the
first column holds row labels (`A`, `B`, …), and each cell's text is a message whose
code is its row letter then column number (`rocket pepper 1819` in row A,
column 2 answers `A2`). The number of rows and columns is whatever the file has;
add or remove either and restart the app. Labels must be typable on the keypad
(0-9, A-D, #). Every message in the row labelled `##` answers `##`.
Blank rows and cells are ignored; each message must appear only once. Edit the
file (for example in Excel, saved as CSV) and restart the app; Setup shows how
many messages loaded or what is wrong with the file. Each run keeps a copy of
the list it started with. Start the app with `--keypad-answers <path>` to use a
different file.

In Setup, set an event's type to **Keypad code**, choose **Codes to pass**, and
assign the special button's MAC to that event as usual. During a run:

- Pressing the button starts the event like any other and shows a randomly
  drawn message large on the TV, over the event grid, with a short chime; the
  clock and points stay visible. The TV also shows how many codes are solved
  out of how many are needed.
- The player types the code on the button's keypad and presses `*` to enter it.
  The TV shows the keys as they are typed. A wrong code flashes the button red
  three times, shows "Wrong code" on the TV, and clears the entry for another
  try. A right code flashes the button green and draws the next message (with
  another chime) until enough are solved; the last one finishes the event.
- No message is shown twice in the same run.
- A second press of the physical button does not advance a keypad event. The
  operator can credit the message on screen by tapping the event tile again (an
  override, recorded as such in the raw messages); with one code to pass, that
  finishes the event.
- The message also disappears when the run finishes or times out.
- Undo steps back one code at a time: the tile's ↶ or **Undo last press** takes
  back the latest solved code (its message returns to the TV and any message
  drawn after it is dropped), and finally the start. Wrong codes are never
  undone; they stay in the raw messages.


Codes are checked by the app, not the button, and every submitted code is
recorded along with which messages each run drew. The typed-so-far entry shown
on the TV is display-only and is not saved. The spoke sketch detects the keypad
automatically, so the same `garage_games_spoke` sketch runs on every button;
flash the master and spokes together when updating to this protocol.

## Chaos Heist arcade event

The Chaos Heist emerald shrine (repository `magnetic-arcade-sensor`) can be one
of the run's events. The seven Chaos Emerald sensors and the coin slot take the
place of a start/stop button. Seven emeralds in place starts the event's timer,
and the 20th ring finishes it. The app owns the timing and scoring exactly as
it does for any button event. The shrine runs on its own Windows PC with the
ChaosHeist app; the two computers are not networked. Everything travels through
the master over ESP-NOW channel 1:

- The shrine's ESP32 listens to the master's run broadcasts (`GARAGE:3:...`)
  and its own event state (`GSTATE:3:...`), and relays them to ChaosHeist over
  USB.
- When ChaosHeist sees the seventh emerald (after a confirmed empty shrine for
  this run) or the 20th ring, the ESP32 sends `GARC:3:<runToken>:<seq>:<S|F>:<ageMs>`
  and repeats it every 250 ms until answered, for up to about 6 seconds.
- The master relays it as `GG1 ARCADE <bootToken> <runToken> <mac> <seq> <S|F> <ageMs>`
  only for the current run while it is active, paused, or just timed out. The
  app records it as `arcade-start` or `arcade-finish` through the same checks as
  a physical press: master handshake, run token, station MAC and event type,
  duplicate message ID, pause, timeout, and late-arrival timing by age. It
  answers with the usual `GG1 RESULT ... <PENDING|ACTIVE|COMPLETED|REJECTED>`,
  which the master forwards to the station.
- A repeated or duplicate start never restarts the timer, a finish before the
  start is rejected, and extra finishes are ignored. A signal sent while the
  run is armed, counting down, or paused is turned down; the competitor then
  lifts and replaces one emerald once the run is going. A 20th ring that lands
  just before a pause or the buzzer still counts by its age.

Setup:

1. Flash the updated `firmware/garage_games_master` sketch to the master.
   Button spokes don't need reflashing; the arcade messages are additions to
   the existing protocol.
2. Flash the updated `ChaosHeistController` sketch from the ChaosHeist folder
   to the shrine's XIAO (board `XIAO_ESP32C3`, **USB CDC On Boot: Enabled**).
   Wiring is unchanged.
3. In **Setup**, add an event (for example "Chaos Heist") and set its type to
   **Chaos Heist (emeralds + rings)**. To assign the shrine's MAC, click
   **Assign** on that event and press **Send ID to Garage Games** in the
   ChaosHeist control panel, or type the MAC shown in that panel. Save setup.
   The shrine must be on and in range.
4. On the shrine's PC, start ChaosHeist and press **Garage Games Mode**. Only
   then does the shrine answer **Scan devices** (and the arm-time scan) like a
   button, so "responding" in Garage Games means ChaosHeist is ready to play.
   If the shrine shows as not responding, check that ChaosHeist is open and in
   Garage Games Mode.

The event's tile also shows what the shrine needs from the operator, in amber:
**Clear shrine · N emeralds on** when emeralds are still in place before a
competitor can start, and **Lift & replace 1 emerald** when all seven went in
before the run started. The shrine sends this through the master about once a
second (`GARCS` → `GG1 ARCSTAT`); it is display-only and never recorded.

During a run the event's tile works as an operator fallback, as it does for a
keypad event. The first tap starts the event and the second finishes it (a
second tap within half a second is ignored as a double tap), so a
failed sensor or radio never blocks a run. The Chaos Heist event counts toward
"every event done": finishing it (with all the others) starts the bonus speed
round when the bonus round is enabled in Setup, or finishes the run when it is
not. The shrine has no button to light, so the speed round only ever targets
the regular and keypad buttons, and its tile never counts as a bonus hit.
