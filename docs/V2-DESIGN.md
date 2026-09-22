# Garage Games v2: requirements and interview

Status: interview in progress; normal run flow confirmed. This is a new version,
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
  on the master display; showing it on the spectator scoreboard remains optional.
- All normal events become available at run start. Special events may have their
  own availability conditions, to be defined individually.
- First press of a normal event's button starts its timer; second press completes
  it. Different events may be active simultaneously: starting another event does
  not pause or finish the first. Each duration spans that event's start to finish.
- Completed normal events lose points as their duration increases. The exact
  formula is still an interview decision.
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
- Use a separate v2 data location during development. Historical import, if wanted,
  should be explicit and preserve original databases and files.

## Interview decisions still needed

1. Remaining run edge cases: whether a started event can be reset/retried, presses
   after completion, pause/recovery behavior, and master-button behavior mid-run.
2. Remaining scoring decisions: partial credit, tie-breaks, and whether future
   editions need event-specific formulas or weights beyond the initial rule above.
3. Hardware/display: USB or wireless master-to-PC, controller board revisions,
   laptop plus second display, and preferred standalone window behavior.
4. Competitors/history: on-deck ordering, and what changing a competitor
   mid-run should mean. Official, playoff, exhibition, and superseded attempts
   are otherwise defined above.
5. Editing: precision, and expected interaction when an operator edits during
   incoming presses. The editable scope is intentionally broad; the remaining
   question is the safest operator workflow.
6. Special events: sensor source location and the measurements it produces.
   Final Speed Button bonus gameplay may be decided later.

## Build sequence after interview

1. New local app with configurable roster, competitors, persistent run history,
   editable current/past results, and independent scoreboard window/view.
2. Device simulator and tests for corrections, history isolation, changing event
   counts, duplicate/late messages, and interrupted runs.
3. Master-to-PC link and normal-event ESP-NOW firmware, followed by real-button tests.
4. Magnetic event adapter and Speed Button bonus integration as rules are confirmed.

Acceptance requires a real physical button to update its assigned event, a completed
run to remain editable after restart, and scoreboard changes to reflect corrections.
Simulated input alone does not establish hardware readiness.
