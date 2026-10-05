# Garage Games V2 firmware

The combined Garage Games/Speed Button sketches are
`garage_games_master/garage_games_master.ino` and
`garage_games_spoke/garage_games_spoke.ino`. The combined spoke supports Garage
event presses and retains Speed Button gameplay. The standalone Speed Button
sketches are `speed_button_master/speed_button_master.ino` and
`speed_button_spoke/speed_button_spoke.ino`.

## Flash

1. Install the ESP32 Arduino board package and the `TM1637Display` library.
2. Open `garage_games_master/garage_games_master.ino` in Arduino IDE.
3. Select board `XIAO_ESP32C3` and the connected COM port, then upload over a
   USB-C data cable. USB serial is enabled by default; no extra UART wiring is
   needed.
4. Open Serial Monitor at 115200 baud. Expect `GG1 HELLO <bootToken>` every
   two seconds and `GG1 MODE <IDLE|SPEED>` on each hello and mode transition.

## Check the serial link and controls

Send newline-terminated `GG1 STATUS ACTIVE 90` and `GG1 STATUS PAUSED 42`;
the display shows the supplied seconds while Speed is idle, with the colon lit
for ACTIVE and off for PAUSED. Send either `GG1 STATUS FINISHED 0` or
`GG1 STATUS NONE 0` to restore the idle display and unlock the five-second
Speed hold. A short debounced release emits one `GG1 START <bootToken> <seq>`
unless the last status is ACTIVE or PAUSED. Five quick idle taps still clear
the saved high score; a host may reject those START lines when no run is armed.

For Garage button testing, flash `garage_games_spoke/garage_games_spoke.ino` to
each event button. For a standalone Speed Button setup, flash
`speed_button_spoke/speed_button_spoke.ino`. Either spoke sketch can join the
combined master's Speed game; start it with a five-second hold and confirm
discovery, countdown, hits, and timeout.

The current Garage firmware also accepts authoritative per-event state updates
from the scorekeeper through the master. This keeps virtual presses, physical
presses, undo, and event clearing aligned, including restoring the spoke's
available state after undo. Reflash both combined Garage master and spoke
sketches when testing this synchronization; older firmware does not recognize
the event-state messages.

No hardware test has been performed for the combined Garage spoke yet.

## Bonus speed round

After a run's last event, the scorekeeper can run a bonus round through the
combined master and spokes (see the main README). The scorekeeper sends
`GG1 BONUS <token> <seq> <INTRO|TARGET|OFF> <mac or -> <remainingMs>` with each
status update; the master rebroadcasts it every 150 ms as
`GBONUS:3:<token>:<seq>:<I|T|O>:<mac or ->:<remainingMs>`. During `INTRO` each
spoke flashes red-yellow-green and answers `GBHELLO:3:<token>:<seq>`, which the
master reports as `GG1 BONUSNODE <boot> <token> <mac>`. During `TARGET` the named
spoke shows the Speed game's target colors and blink for the remaining time; its
press is an ordinary `GPRESS` (with press age) that the scorekeeper scores. Other
spokes stay dark. A spoke that hears nothing for 1.5 s returns to its normal
Garage lights. None of this touches the standalone Speed game, which still
starts with the master's five-second hold.

Throughout the round each spoke also sends a heartbeat `GBHB:3:<token>:<shownSeq>`
every 600-800 ms (and at once when it lights), where `shownSeq` is the target it
is showing or 0; the master relays it as `GG1 BONUSBEAT <boot> <token> <mac> <seq>`.
Like the Speed game's HB and READY, the app lights only spokes still beating,
starts a lit spoke's window when its beat confirms the target, and swaps a lit
spoke that never confirms or goes silent. A spoke forgets its bonus state when a
new Garage session (run token) starts. The master relays `GPRESS` and keypad
submissions for the current run while it is `PAUSED` or `TIMED_OUT` too, so a
press made just before the clock stopped still reaches the app, which judges it by
its age. The master's USB receive buffer is 1 KB.

## Special button and keypad wiring test

For the special button prototype, flash
`special_button_test/special_button_test.ino` to a `XIAO_ESP32C3`. It tests the
normal spoke button and common-anode RGB LED, then scans and reads an
MCP23017-connected keypad without requiring an extra keypad library.
Open Serial Monitor at 115200 baud.

- Button: D2 to GND; the sketch uses the internal pull-up.
- RGB LED: D3 red, D4 green, D5 blue; common anode to 3V3 with one resistor per color.
- Keypad backpack: SDA to D7, SCL to D8, VCC to 3V3, and GND to GND.

The startup sequence flashes red, green, blue, and white. A button press
flashes green; a keypad press flashes blue and prints the key. The sketch
expects keypad rows on GPA0-GPA3 and columns on GPA4-GPA7; adjust the mapping
constants in the sketch if your keypad uses GPB pins. Keep the MCP23017 at 3V3
so its I2C pull-ups never place 5V on the ESP32-C3 pins.

## Special button in play

Once the wiring test passes, flash the normal
`garage_games_spoke/garage_games_spoke.ino` to the special button (and the
matching `garage_games_master`). At boot it looks for the MCP23017 at
0x20-0x27 and prints `[KEYPAD] MCP23017 ready at 0x..`; spokes without one print
`[KEYPAD] None detected` and behave as before. The keypad pin mapping constants
match the test sketch, so copy any changes you made there.

In the scorekeeper's Setup, make the button's event a **Keypad code** event and
choose how many codes pass it; the messages and codes come from
`config/keypad-answers.csv`. The button still starts the event and plays the
Speed game. While its event runs, the keypad is live:

- each key blinks the LED blue and is shown on the TV;
- `*` submits the code. The LED blinks yellow quickly while it waits for the
  app, then flashes green three times for a right code when more are needed
  (the TV shows the next message), turns solid green for the last one, or
  flashes red three times for a wrong one (keys are ignored during either
  flash, then the entry starts over);
- keys pressed before the event starts, while paused, or after it finishes are
  ignored.

Spoke-to-master packets are `GKEY:3:<token>:<seq>:<K|S>:<entry or ->:<ageMs>`
(`K` = typed so far, `S` = submitted). The master relays them to the app as
`GG1 KEYPAD <boot> <token> <mac> <seq> <K|S> <entry or -> <ageMs>`, and the app
answers submissions with the usual `GG1 RESULT` line: `COMPLETED` for the code
that finishes the event, `NEXT` for a right code when more are needed, and
`ACTIVE` for a wrong one.

## Chaos Heist arcade station

The master also relays the Chaos Heist shrine's two signals. The shrine's ESP32
(sketch `ChaosHeistController` in the ChaosHeist folder) broadcasts
`GARC:3:<runToken>:<seq>:<S|F>:<ageMs>`: S when all seven emeralds are placed,
F at the ring goal. The master prints `GG1 ARCADE <bootToken> <runToken> <mac>
<seq> <S|F> <ageMs>` for the current run only, adding its own relay delay to
the age. The app's `GG1 RESULT` reply is forwarded as the usual `GRESULT`
packet. The shrine also answers a Garage Games device scan (only when Garage
status packets are flowing, so it never joins the Speed game, and only while
the ChaosHeist app is in Garage Games Mode) and can send
`GTEST` for Setup's press-to-assign. About once a second it also broadcasts
`GARCS:3:<O|C|R>:<0-7>` (C = emeralds to clear, R = one to replace), which the
master prints as `GG1 ARCSTAT <bootToken> <mac> <O|C|R> <count>` for the
operator's tile. Reflash the master for this; button spokes are unaffected.
