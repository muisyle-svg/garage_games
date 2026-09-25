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

No hardware test has been performed for the combined Garage spoke yet.
