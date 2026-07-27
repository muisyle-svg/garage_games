# Device protocol v1

ESP-NOW frames use the packed `GarageProtocol::Envelope` in
`firmware/lib/GarageProtocol/Protocol.h`. The 241-byte frame stays below ESP-NOW's
250-byte payload limit.

Each frame contains:

- magic marker and protocol version
- message type and logical target
- hardware, boot, run, and event IDs
- per-boot sequence and run-relative timestamp
- acknowledgement sequence and retry count
- JSON payload and CRC-32

Stations discover a master's channel from periodic beacons. Commands are broadcast
with logical targets (`*`, hardware ID, or assignment target); stations ignore
messages not addressed to them. Meaningful station events retain their event ID and
sequence across retry attempts. The master persists them before acknowledging the
station, then keeps its own outbound queue until the controller acknowledges them.

## Standard lifecycle

`registration`, `health`, `run_started`, `run_paused`, `run_resumed`,
`attempt_started`, `game_completed`, `bonus_started`, `bonus_cue`, `bonus_hit`,
`bonus_all_done`, `run_timed_out`, `run_aborted`, `acknowledgement`, and `fault`.

Custom game firmware may make local decisions, drive extra displays, or validate
answers. It must still emit the same lifecycle events so the controller and scoring
modules remain game-independent.

## Compatibility

Change `Protocol::Version` only for a breaking wire change. Additive payload fields
must be optional. CI compiles every firmware variant and the controller rejects an
incompatible protocol version with a visible fault.

