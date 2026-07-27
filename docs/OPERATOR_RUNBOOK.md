# Event operator runbook

## Before the event

1. Install the current release on the primary and backup Windows laptops.
2. Rotate venue and device credentials. Never reuse credentials found in a legacy
   sketch.
3. Connect the master by USB, power stations, and open the controller.
4. Complete setup, choose the 2026 season, and enter Google sync settings.
5. Assign every detected hardware ID to its game slot.
6. Run a physical press test for every station and verify battery, last contact, and
   firmware version on the device dashboard.
7. Open `/display.html` full-screen on the event monitor.
8. Confirm a database backup exists and rehearse emergency Sheet mode.

## Running competitors

1. Add competitors to the queue and announce current/on-deck names.
2. Select the next competitor and use the master button or virtual Start.
3. Watch station health and the immutable event feed. Pause/resume only for an
   actual event interruption.
4. If an event is wrong, use Undo or a reasoned correction. Do not edit generated
   Google results.
5. When the run completes or times out, confirm the leaderboard and sync status.

## Failures

- **Station press not shown:** keep the station powered; retries are automatic.
  Confirm immediate local LED feedback, station last contact, and assignment.
- **Google offline:** continue normally. The local outbox catches up later.
- **Controller stopped mid-run:** restart it, preserve the interrupted evidence, and
  begin a clean new run.
- **Master rebooted mid-run:** abort/restart the run cleanly.
- **Controller unavailable:** use the Sheet's Garage Games menu to start and
  finalize independent emergency runs. Import those finalized records after the
  controller returns; never resume the distributed run.

## End of event

Export CSV and Excel, copy the SQLite database and backups to two separate storage
locations, confirm the Google audit caught up, and record any equipment faults
before powering down.

