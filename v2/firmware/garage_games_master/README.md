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
- Send `GG1 STATUS ACTIVE 42` and `GG1 STATUS PAUSED 12`, each followed by a
  newline. While the Speed game is idle, the display should show those seconds;
  the colon is lit for ACTIVE and off for PAUSED. Short starts and the five-
  second Speed hold should be gated. Send `GG1 STATUS FINISHED 0` to clear the
  countdown, redraw the idle screen, and unlock the hold.
- With status `NONE` or `ARMED`, a short debounced press/release should emit
  one `GG1 START <bootToken> <seq>`. Five quick idle taps should still clear
  the saved high score. A host with no run armed may reject those START lines;
  the local five-tap reset should still work. A five-second hold should enter
  the existing Speed discovery/game; verify button presses do not carry into
  another game.
- Pair with the existing Speed spokes and confirm discovery/game operation on
  ESP-NOW channel 1.

## Spoke compatibility

This first pass adds Garage events at the master only; it does not add Garage
event handling or output to the spoke. The versioned
`../speed_button_spoke/speed_button_spoke.ino` is an untouched copy of the
existing Speed spoke sketch. It retains ESP-NOW protocol version 3 and channel
1 and should interoperate with the combined master. Hardware interoperability
has not been tested.
