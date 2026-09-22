# Garage Games v2: requirements and interview

Status: core interview complete; first app and virtual-button implementation underway.
This is a new version,
informed by the earlier implementation, not an instruction to delete or replace it.

## Confirmed requirements

- Standalone Windows application providing operator controls and a spectator scoreboard.
- All scoring, timing, competitor management, corrections, and history work locally.
  No Google Sheets, Apps Script, Google polling, credentials, or hosted services
  are required by v2. Reproduce the useful Sheet workflows within the application.
- Spoke buttons communicate with the master exclusively through ESP-NOW.
  The master starts the overall run and connects to the computer.
- Every normal-event button has a persistent, explicit association with one event.
  Discovery order must not determine which event receives a press or measurement.
- Normal events record start and finish times. Special-event modules can report
  their own measurements and scores.
- An edition has a configurable event roster, likely 13 or 14 events this year.
  Event count, names, order, button assignment, and scoring must not be hard-coded
  to one year's roster.
- Operators can add competitors and select/change the current competitor.
- Operators can recall any competitor's current or historical run, view its event
  times and scores, and make manual adjustments before or after the run finishes.
- The scoreboard shows a points leaderboard, the on-deck competitor, and the
  current run's completed and remaining events.
- Speed Button should supply reusable radio/game behavior and eventually be an
  integrated bonus mode. Its final bonus rules remain explicitly deferred.
- One special event will reuse magnetic arcade sensor code.

## Confirmed operator, display, and device behavior

- The operator uses a Windows computer and monitor for the control application.
  It must show run state, per-event status and scores, competitor controls,
  device health, and a raw incoming-data view for troubleshooting.
- The application opens a separate scoreboard window that can be moved to a
  secondary monitor or TV. The spectator view shows the leaderboard, on-deck
  competitor, current run category, and completed/remaining events without
  exposing operator controls or raw data.
- Before a run starts, the operator can run a device preflight. It checks that
  every assigned ESP32 is known, connected to the expected master, and
  responding. Each device's result is visible before the master accepts the
  run start.
- The operator can explicitly mark a failed device for manual scoring. Its event
  stays in the roster and completion requirements; the override is recorded.
- Provide virtual buttons and simulated device availability in the app for
  testing before physical hardware is ready. Label simulated operation clearly.
  Full button firmware sketches are a later deliverable, not an acceptance
  condition for the first virtual-button app.
- Device LEDs are driven by explicit state from the shared protocol. The v2
  states must support at least ready, event available, event active, event
  completed, bonus, offline/error, and run-finished indications. Exact colors
  and patterns remain hardware configuration rather than scoring logic.
- The master-to-PC connection is transport-agnostic. Implement reliable USB
  as the first path unless a wireless path passes an equivalent reliability
  test; the controller must not depend on Google services or internet access.

## Confirmed special-event shapes

- A prompt-and-keypad event starts when its assigned button is pressed. A text
  prompt is shown on the button's LCD, the master, or the scoreboard, and a
  keypad connected to that event's ESP32 accepts the response. The ESP32
  validates a successful response and sends an event-complete message through
  the master. The prompt, response/result, and timestamps are retained as raw
  event data.
- A magnetic arcade event starts from the arcade ESP32 when all emeralds are
  detected in position. A completion signal from the arcade application is
  delivered to the connected ESP32 and then relayed through the master. The
  app must correlate both signals to the same run and event rather than
  treating either signal as an unscoped button press.
- Standard events use the common start/finish protocol and application scoring;
  special modules may provide their own start conditions, completion signals,
  measurements, and validation while using the same run identity and history.

## Confirmed run categories and lifecycle

- Each competitor gets one official/main run per edition. Only the current
  accepted official run contributes to the official leaderboard.
- A restart creates a replacement attempt linked to the same competitor and
  edition. The prior attempt is retained and marked restarted/superseded; it
  is never permanently deleted and does not remain the counted official result.
- Playoff and exhibition runs are first-class records with an explicit run
  category. They remain visible in history and can be shown on the scoreboard
  with their category label, but they do not contribute to the official
  leaderboard or official points.
- Manual correction may change any run or result parameter needed by the
  operator, including competitor, category, event state, timestamps, duration,
  measurements, points, bonus results, and run metadata. Every correction is
  recorded in an audit trail so the prior value can be reviewed or restored.
- Replacing or correcting a run must update the scoreboard and official
  standings immediately, while preserving the original device messages and
  prior run versions for recovery.

## Confirmed run flow

- The scorekeeper selects the player in the app before the player presses the
  physical master button to start. Selection itself does not start the clock.
- The master starts a single five-minute (300-second) run. Remaining time appears
  on the master display and spectator scoreboard, along with live points.
- The operator can pause and resume a run. Initial implementation decision:
  pause freezes the overall clock and active event timers, and gameplay input
  is ignored while paused. Resume continues the previous normal/bonus phase.
- Provide an ordered, reorderable on-deck competitor queue. Initial workflow:
  completing a run updates availability of the next entry, but selecting/arming
  the next competitor remains an explicit operator action.
- All normal events become available at run start. Special events may have their
  own availability conditions, to be defined individually.
- First press of a normal event's button starts its timer; second press completes
  it. Different events may be active simultaneously: starting another event does
  not pause or finish the first. Each duration spans that event's start to finish.
- Completed normal events lose points as their duration increases, using the
  configurable initial rule recorded below unless an edition overrides it.
- Completing every event before time expires transitions the run into bonus mode.
  The bonus uses only the remaining portion of the original five minutes; there
  is no new or extended bonus timer. Detailed bonus gameplay remains deferred.
- At the original five-minute deadline, gameplay input is disabled, including
  bonus scoring. The implementation must distinguish normal-event and bonus-mode
  button input while retaining each normal event's device assignment.
- Historical/current manual corrections remain an operator capability; disabling
  gameplay input at timeout does not prevent subsequent results editing.

## Confirmed initial scoring rule

- Build v2 initially with the existing draft time-decay rule for normal events:
  100 points for a completed event, minus 5 points for every full 5 seconds of
  that event's duration, with a minimum of 50 points for any completed event.
- An unfinished event scores 0 points.
- Keep the rule configurable at the edition/event level so it can be revised
  later without changing already-recorded runs. Store the applied rule snapshot
  with each run.
- Bonus scoring is deferred. It must be represented as a separate module and
  separate result record so later bonus rules do not alter normal-event scoring.

## Source review

- `src/GarageGames.Controller/Services/RunService.cs`: corrections go through
  `AppendToCurrentAsync`; there is no historical-run correction workflow.
  Leaderboard currently lists each completed/timed-out run, rather than applying
  a confirmed rule for multiple runs by the same person.
- `src/GarageGames.Core/State/RunProjector.cs`: first attempt/completion readings
  win; corrections support remaining-time values and a bonus flag. A missing start
  is inferred from completion. These behaviors need explicit review for v2.
- `config/seasons/2026.json`: existing 300-second, 13-event time-decay configuration
  is marked draft. It is evidence of earlier work, not an approved v2 rule set.
- `archive/legacy-scorekeeper/code.gs.txt`: first and second presses fill attempt
  and completion cells using remaining overall time. Completion checks hard-code
  13 events. The script references scoring cells; it does not contain all original
  workbook formulas.
- `firmware/lib/StationRuntime/`: reusable ideas include station identity, run IDs,
  acknowledgements, retry handling, and game modules. Existing production master
  communicates with the PC over network WebSocket, not USB serial.
- `C:/Users/Projector/Documents/Speed Button/speed_button_master/` and
  `speed_button_spoke/`: fixed-channel ESP-NOW, MAC-based discovery, queued receive
  callbacks, run/cue identifiers, retransmission, and reaction-game state machines.
  Its current expected-spoke capacity is 13. Integrate via a deliberate shared
  protocol; the old Garage Games and Speed Button protocols are not interchangeable.
- `C:/Users/Projector/Documents/Emerald`: saved Magnetic Arcade Sensors project
  contains media assets in the inspected folder; sensor source has not been located.

## Proposed foundations, subject to interview

- Separate competitors, editions, runs, per-event attempts, and physical devices.
  Persist run-specific roster, assignments, scoring configuration, run category,
  and attempt lineage so changing next year's events does not reinterpret
  historical scores.
- Provide an editable results grid with start, finish, duration, measurements,
  calculated points, and manual overrides. Preserve original readings and an
  adjustment history behind simple cell editing; allow corrections to be undone.
- Recalculate the edited run and affected leaderboard after corrections. Editing
  a past run must not switch or send control commands to the active physical run.
- Bind incoming messages to device identity and run/session identity. Deduplicate
  retries, retain measurement timestamps, and flag unknown/unassigned/late packets
  rather than silently assigning them to the next competitor.
- Give standard timing, sensor events, and bonus games separate modules using
  common device discovery, transport, persistence, and UI contracts.
- Make the operator and scoreboard views separate clients of the same local
  run state so a TV can be restarted or disconnected without interrupting a
  run. Expose a raw-message/event inspector without making raw protocol data
  part of the spectator display.
- Use a separate v2 data location during development. Historical import, if wanted,
  should be explicit and preserve original databases and files.

## Interview decisions still needed

1. Remaining run edge cases: physical master-button behavior mid-run and exact
   hardware recovery behavior. Initial software defaults: only the operator can
   reset results; presses after event completion are ignored; an interrupted
   app recovers its unfinished run paused for operator review.
2. Remaining scoring decisions: partial credit, tie-breaks, and whether future
   editions need event-specific formulas or weights beyond the initial rule above.
3. Hardware/display: validate the USB master-to-PC path first, then evaluate
   wireless only if it meets the same reliability requirement. Confirm board
   revisions, LED patterns, and the exact TV resolution/layout.
4. Competitors/history: the queue is reorderable. Correcting a run's competitor
   is distinct from selecting the next competitor; neither silently starts a
   new physical session. Official/playoff/exhibition categories are defined above.
5. Editing: precision, and expected interaction when an operator edits during
   incoming presses. The editable scope is intentionally broad; the remaining
   question is the safest operator workflow.
6. Special events: locate the magnetic sensor source, finalize keypad prompt
   and validation rules, and define the arcade application's completion signal.
   Final Speed Button bonus gameplay may be decided later.

## Build sequence after interview

1. New local app with configurable roster, competitors, persistent run history,
   editable current/past results, and independent scoreboard window/view.
2. Device simulator and tests for corrections, history isolation, changing event
   counts, duplicate/late messages, and interrupted runs.
3. Master-to-PC link and normal-event ESP-NOW firmware, followed by real-button tests.
4. Magnetic event adapter and Speed Button bonus integration as rules are confirmed.

The first app milestone requires a simulated run through the operator and TV views,
local persistence across app restart, editable current/past results with audit
history, and scoreboard changes reflecting corrections. Production hardware
acceptance additionally requires real buttons to update their assigned events,
device preflight and timing/LED behavior, and verified full firmware sketches.
Simulated input alone does not establish hardware readiness.
