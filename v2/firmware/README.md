# Garage Games V2 firmware

The current combined master is
`garage_games_master/garage_games_master.ino`. It retains the Speed Button
ESP-NOW protocol (version 3, channel 1) and adds the Garage Games USB serial
bridge. `speed_button_master/speed_button_master.ino` is the untouched original
Speed master source copy; `speed_button_spoke/speed_button_spoke.ino` is the
untouched original spoke source copy.

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

For the radio check, use the untouched versioned spoke copy above, start Speed
with a five-second hold, and confirm discovery, countdown, hits, and timeout.

## Spoke source

The first pass adds Garage events at the master only; it does not add Garage
event handling or output to the spoke. The versioned
`speed_button_spoke/speed_button_spoke.ino` is an untouched copy of the existing
Speed spoke sketch. It keeps ESP-NOW protocol version 3 and channel 1 and should
interoperate with the combined master at
`garage_games_master/garage_games_master.ino`. Hardware interoperability has
not been tested.

No hardware test has been performed for this versioned copy.
