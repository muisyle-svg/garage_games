# Garage Games master firmware

This is the current combined master sketch. The untouched original Speed
master source copy is in `../speed_button_master/speed_button_master.ino`.

## Flash

1. Open `garage_games_master.ino` in Arduino IDE. Install the ESP32 Arduino
   board package and the TM1637 library that provides `TM1637Display.h`.
2. Connect the XIAO ESP32C3 with a USB-C data cable. Select board
   `XIAO_ESP32C3` and the connected COM port, then upload.
3. Open Serial Monitor at 115200 baud. USB serial is enabled by default; no
   extra UART adapter or UART wiring is needed.

The sketch uses D2 for the button, D3/D4/D5 for the RGB LED, and D0 CLK/D1 DIO
for the TM1637. Seeed documents the [board and COM port selection](https://wiki.seeedstudio.com/XIAO_ESP32C3_Getting_Started/),
[pin mappings](https://wiki.seeedstudio.com/XIAO_ESP32C3_Pin_Multiplexing/#digital),
and [USB serial default](https://wiki.seeedstudio.com/XIAO_ESP32C3_Pin_Multiplexing/#serial---uart).

## Check

- Confirm `GG1 HELLO <bootToken>` repeats every two seconds and `GG1 MODE IDLE`
  appears on boot, with each hello, and whenever mode changes. Ignore legacy
  debug lines that do not begin with `GG1`.
- Send `GG1 STATUS COUNTDOWN 300`, `GG1 STATUS ACTIVE 300`, and
  `GG1 STATUS PAUSED 42`, each followed by a newline. While the Speed game is
  idle, COUNTDOWN should show `WAIT` with a blinking amber LED and ignore button
  gestures. ACTIVE should show `05:00`; the other timer values use MM:SS as
  well. ACTIVE and PAUSED block host starts and the five-second Speed hold.
  Send `GG1 STATUS FINISHED 0` to clear the timer, redraw the idle screen, and
  unlock the hold.
- With status `NONE` or `ARMED`, a short debounced press/release should emit
  one `GG1 START <bootToken> <seq>`. Five quick idle taps should still clear
  the saved high score. A host with no run armed may reject those START lines;
  the local five-tap reset should still work. A five-second hold should enter
  the existing Speed discovery/game; verify button presses do not carry into
  another game.
- For a spoke preflight scan, send a fresh idle status such as
  `GG1 STATUS NONE 0`, then `GG1 SCAN preflight_1`. Scan IDs are 1–32 ASCII
  letters, digits, hyphens, or underscores. Over about two seconds, each unique
  responding MAC produces `GG1 SCAN preflight_1 NODE <12-hex-MAC>`, followed by
  `GG1 SCAN preflight_1 DONE <count>`. A non-idle Speed state, host status of
  COUNTDOWN, ACTIVE, or PAUSED, missing or stale host status, or unavailable
  radio returns `GG1 SCAN preflight_1 BUSY` and stops discovery. The scan reuses
  the compatible version-3 `DISCOVER`/`HELLO` exchange and keeps its responders
  out of the Speed game's discovery registry. Its separate table holds 64 MACs;
  overflow ends with BUSY rather than reporting a partial count as complete.
- Pair with the combined Garage spoke or the standalone Speed spokes and
  confirm discovery/game operation on ESP-NOW channel 1.

## Spoke compatibility

For Garage event testing, use the combined
`../garage_games_spoke/garage_games_spoke.ino`. It supports Garage event presses
and retains Speed Button gameplay. The standalone Speed-only spoke is
`../speed_button_spoke/speed_button_spoke.ino`. Both use ESP-NOW channel 1.
Hardware interoperability has not been tested.
