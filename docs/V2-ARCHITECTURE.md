# Garage Games v2 implementation decisions

Owner: Astra (design and review). Implementation and test author: Luna, Extra High.
This document defines the first runnable local app; physical firmware follows.

## Application boundary

Use a separate `v2/` application and solution, preserving the old controller and
its database. Reuse the available .NET 10 toolchain and SQLite dependencies.
The initial Windows executable hosts its operator and spectator views locally on
loopback. These views open in browser windows; a native window wrapper is not
required for this milestone. Operation must work without internet or Google.
Opening a TV window must not open a second game engine or database writer.

Expose only spectator data to the TV view: current competitor/category, countdown,
awarded points, visible challenge prompt, event progress, official standings,
and on-deck name. Raw packets, answers, corrections, and operator actions belong
to the operator view. Render user-entered names/prompts as text.

## Authoritative state and timing

One synchronized command handler owns the active session. Use an injectable
monotonic clock for timing and tests, plus wall-clock timestamps for history.
Persist active elapsed time, pause state, and run revision with atomic changes.
A periodic checkpoint bounds elapsed-time loss in an unexpected process crash;
document that bound and recover any unfinished session paused for review.
Never count application downtime automatically after uncertain recovery.

The five-minute budget measures active time. Pausing freezes both the run clock
and active event durations. A resumed bonus continues the remaining same budget.
At elapsed >= duration limit, timeout takes precedence over a gameplay input;
log the rejected input. Paused input is logged without changing results.

Run phases: armed, running, paused (with remembered running/bonus phase), bonus,
finished, aborted. Superseded is an independent result/lineage marker.
Roster and scoring snapshots belong to each run. No hard-coded event count.

Physical master timing, clock synchronization, acknowledgments of pause/resume,
and reconnect replay will need a separately tested firmware implementation.
The simulator validates the software flow without establishing radio readiness.

## Results, edits, and durable history

Keep competitor, edition, queue entry, run, event definition/result, and device
identities distinct. Queue entries can request official, playoff, or exhibition
runs. Arming is explicit; only a master start input begins the clock.

Enforce one accepted official run per competitor per edition. An intentional
restart links a new attempt and marks the old attempt superseded. Keep all
attempts and raw inputs. Other run categories can appear live and in history
without affecting official standings. Ties initially share rank; no invented
tie-break winner until an event rule is selected.

Use SQLite transactions for state plus its audit entry. Record edits as before
and after values with a reason and timestamp. Undo appends another edit; it
does not erase history. Use optimistic revisions to reject an editor overwriting
newer incoming results. Preserve raw messages independently of corrected results.
Reject corrupt/unknown storage schemas visibly instead of resetting storage.

Provide simple fields for start/finish, duration, status, measured values, and
score override. Make calculated scores versus overrides visible and provide
clear override. Changing duration updates consistent finish timing; changing
start/finish recalculates duration. Validate negative/impossible intervals.
Advanced edits include participant/category, run time limit, status, bonus
result, and notes; stable internal IDs and audit records are not editable.
Historical edits cannot select another active run or emit gameplay commands.

Record failed-device manual overrides without dropping events from the roster
or bonus requirements. Ordinary manual corrections remain available after timeout.
If a live correction changes all-required-events completion, reconcile bonus
eligibility against the same remaining clock; a historical correction never
restarts a finished clock. No unspecified bonus points are awarded automatically.

## Device and simulator boundary

Each input has a session/run ID, stable device ID, unique message ID, event kind,
and captured timing/measurement fields. Resolve the event from the run's device
mapping. Reject or quarantine unknown, duplicate, wrong-session, paused, and
late input and expose its disposition in the raw-data inspector.

Virtual buttons use this same command boundary. Provide virtual master start,
ordinary event press, online/offline state, keypad response, emerald-placement
start, and arcade completion. Show a simulated status/LED indication per device.
Test-only clock control must not be mistaken for a physical master command.

Keypad: one press starts, displays the configured demo prompt, then only a valid
response completes. An ordinary second press cannot bypass keypad validation.
Arcade: all-emeralds signal starts; arcade-completion signal finishes. Repeated
sensor signals cannot reset a timer or finish the event. Completion before start
is rejected. Placeholder prompts and event names are editable demo configuration.

## First-version review scenarios

1. Add/reorder competitors; arm the first; verify on-deck and TV views.
2. Simulate a missing station; require explicit manual override; retain its event.
3. Start through master; overlap events; pause; prove clocks and inputs freeze;
   resume and verify the score boundary at each full five seconds.
4. Finish keypad and arcade through their correct signals; reject incorrect ones.
5. Complete the roster; enter bonus with only the original active time remaining;
   timeout locks gameplay but still permits corrections.
6. Restart an attempt; retain prior results; show one official result only;
   display exhibition/playoff labels and exclude them from official standings.
7. Edit a past result while another run is active; update standings, preserve the
   active session, undo without erasing audit history, and reject stale edits.
8. Restart the app against saved data; recover an unfinished run paused; retain
   competitors, queue, raw records, completed runs, and corrections.
9. Independently reload/close the TV; the operator run continues. Demonstrate a
   portable backup/export without accessing the old application's database.
