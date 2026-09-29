/*
  SPECIAL BUTTON + I2C KEYPAD TEST
  --------------------------------
  Target: Seeed XIAO ESP32-C3

  This is a standalone wiring test. It does not use ESP-NOW or the Garage
  Games protocol.

  Normal spoke wiring:
    Button       D2 -> button -> GND (active LOW, internal pull-up)
    RGB LED      D3 red, D4 green, D5 blue
                 Common anode -> 3V3, one resistor on each color leg

  Special keypad wiring:
    SDA          D7
    SCL          D8
    VCC          3V3
    GND          GND

  The keypad section expects an MCP23017 I2C GPIO expander. The default
  mapping below uses GPA0-GPA3 for rows and GPA4-GPA7 for columns. No extra
  keypad library is required.

  IMPORTANT: Power the I2C expander from 3V3. Do not put 5V pull-ups on SDA
  or SCL because the ESP32-C3 pins are not 5V tolerant.

  Open Serial Monitor at 115200 baud. On startup the sketch:
    - flashes the RGB LED red, green, blue, then white;
    - scans the I2C bus and selects the first MCP23017 address it finds;
    - reports button presses/releases and keypad key presses/releases.

  If the MCP23017 address is not detected automatically, set
  MCP23017_I2C_ADDRESS below to the address printed by the scan, normally
  0x20 through 0x27.
*/

#include <Arduino.h>
#include <Wire.h>

// --------------------------- Normal spoke pins ---------------------------
constexpr uint8_t BUTTON_PIN = D2;
constexpr uint8_t LED_RED_PIN = D3;
constexpr uint8_t LED_GREEN_PIN = D4;
constexpr uint8_t LED_BLUE_PIN = D5;

// --------------------------- Special keypad pins -------------------------
constexpr uint8_t I2C_SDA_PIN = D7;
constexpr uint8_t I2C_SCL_PIN = D8;

// Use 0xFF for automatic selection. MCP23017 addresses are normally 0x20-0x27.
constexpr uint8_t MCP23017_I2C_ADDRESS = 0xFF;
constexpr uint8_t I2C_ADDRESS_AUTO = 0xFF;

constexpr uint8_t KEYPAD_ROWS = 4;
constexpr uint8_t KEYPAD_COLUMNS = 4;
constexpr uint16_t I2C_FREQUENCY_HZ = 100000;
constexpr uint32_t DEBOUNCE_MS = 30;
constexpr uint32_t KEYPAD_SCAN_INTERVAL_MS = 10;
constexpr uint32_t LED_FLASH_MS = 220;

const char KEYMAP[KEYPAD_ROWS][KEYPAD_COLUMNS] = {
  { '1', '2', '3', 'A' },
  { '4', '5', '6', 'B' },
  { '7', '8', '9', 'C' },
  { '*', '0', '#', 'D' }
};

// MCP23017 pin mapping for the keypad matrix.
// Port 0 = GPIOA/GPA, port 1 = GPIOB/GPB.
// If your keypad is wired to GPB0-GPB3, change the corresponding port values.
constexpr uint8_t MCP_PORT_A = 0;
constexpr uint8_t MCP_PORT_B = 1;
const uint8_t KEYPAD_ROW_PORT[KEYPAD_ROWS] = {
  MCP_PORT_A, MCP_PORT_A, MCP_PORT_A, MCP_PORT_A
};
const uint8_t KEYPAD_ROW_BIT[KEYPAD_ROWS] = { 0, 1, 2, 3 };
const uint8_t KEYPAD_COLUMN_PORT[KEYPAD_COLUMNS] = {
  MCP_PORT_A, MCP_PORT_A, MCP_PORT_A, MCP_PORT_A
};
const uint8_t KEYPAD_COLUMN_BIT[KEYPAD_COLUMNS] = { 4, 5, 6, 7 };

// --------------------------- LED test helpers ----------------------------
void setLed(bool red, bool green, bool blue) {
  // The normal spoke RGB LED is common-anode, so LOW turns a color on.
  analogWrite(LED_RED_PIN, red ? 0 : 255);
  analogWrite(LED_GREEN_PIN, green ? 0 : 255);
  analogWrite(LED_BLUE_PIN, blue ? 0 : 255);
}

bool ledFlashActive = false;
uint32_t ledFlashUntil = 0;

void flashLed(bool red, bool green, bool blue) {
  setLed(red, green, blue);
  ledFlashActive = true;
  ledFlashUntil = millis() + LED_FLASH_MS;
}

void updateLedFlash() {
  if (ledFlashActive && (int32_t)(millis() - ledFlashUntil) >= 0) {
    ledFlashActive = false;
    setLed(false, false, false);
  }
}

void runStartupLedTest() {
  const uint16_t stepMs = 350;

  setLed(true, false, false);
  delay(stepMs);
  setLed(false, true, false);
  delay(stepMs);
  setLed(false, false, true);
  delay(stepMs);
  setLed(true, true, true);
  delay(stepMs);
  setLed(false, false, false);
}

// --------------------------- Button test ---------------------------------
bool buttonRawPressed = false;
bool buttonStablePressed = false;
uint32_t buttonRawChangedAt = 0;

void updateButton() {
  const uint32_t now = millis();
  const bool pressed = digitalRead(BUTTON_PIN) == LOW;

  if (pressed != buttonRawPressed) {
    buttonRawPressed = pressed;
    buttonRawChangedAt = now;
  }

  if (pressed == buttonStablePressed ||
      now - buttonRawChangedAt < DEBOUNCE_MS) {
    return;
  }

  buttonStablePressed = pressed;

  if (pressed) {
    Serial.println("[BUTTON] pressed");
    flashLed(false, true, false);
  } else {
    Serial.println("[BUTTON] released");
  }
}

// --------------------------- I2C / keypad test ----------------------------
// MCP23017 register addresses with the normal BANK=0 layout.
constexpr uint8_t MCP_IODIRA = 0x00;
constexpr uint8_t MCP_IODIRB = 0x01;
constexpr uint8_t MCP_GPPUA = 0x0C;
constexpr uint8_t MCP_GPPUB = 0x0D;
constexpr uint8_t MCP_GPIOA = 0x12;
constexpr uint8_t MCP_GPIOB = 0x13;
constexpr uint8_t MCP_OLATA = 0x14;
constexpr uint8_t MCP_OLATB = 0x15;

uint8_t mcpAddress = 0;
bool mcpPresent = false;
uint8_t mcpOutputLatch[2] = { 0xFF, 0xFF };
bool rawKeyPressed[KEYPAD_ROWS][KEYPAD_COLUMNS] = {};
bool stableKeyPressed[KEYPAD_ROWS][KEYPAD_COLUMNS] = {};
uint32_t rawKeyChangedAt[KEYPAD_ROWS][KEYPAD_COLUMNS] = {};

bool isMcp23017Address(uint8_t address) {
  return address >= 0x20 && address <= 0x27;
}

bool probeI2cAddress(uint8_t address) {
  Wire.beginTransmission(address);
  return Wire.endTransmission() == 0;
}

uint8_t mcpRegisterForPort(uint8_t port, uint8_t registerA, uint8_t registerB) {
  return port == MCP_PORT_A ? registerA : registerB;
}

bool writeMcpRegister(uint8_t registerAddress, uint8_t value) {
  Wire.beginTransmission(mcpAddress);
  Wire.write(registerAddress);
  Wire.write(value);
  return Wire.endTransmission() == 0;
}

bool readMcpRegister(uint8_t registerAddress, uint8_t &value) {
  Wire.beginTransmission(mcpAddress);
  Wire.write(registerAddress);
  if (Wire.endTransmission(false) != 0) {
    return false;
  }

  if (Wire.requestFrom(mcpAddress, (uint8_t)1) != 1 || !Wire.available()) {
    return false;
  }

  value = Wire.read();
  return true;
}

bool writeMcpPortLatch(uint8_t port, uint8_t value) {
  mcpOutputLatch[port] = value;
  return writeMcpRegister(mcpRegisterForPort(port, MCP_OLATA, MCP_OLATB), value);
}

bool readMcpPort(uint8_t port, uint8_t &value) {
  return readMcpRegister(mcpRegisterForPort(port, MCP_GPIOA, MCP_GPIOB), value);
}

void scanI2cBus() {
  Serial.println("[I2C] scanning addresses...");
  uint8_t automaticCandidate = 0;
  uint8_t deviceCount = 0;

  for (uint8_t address = 1; address < 127; ++address) {
    if (!probeI2cAddress(address)) {
      continue;
    }

    ++deviceCount;
    Serial.printf("[I2C] device found at 0x%02X", address);
    if (isMcp23017Address(address)) {
      Serial.print(" (MCP23017 address range)");
      if (automaticCandidate == 0) {
        automaticCandidate = address;
      }
    }
    Serial.println();
  }

  if (deviceCount == 0) {
    Serial.println("[I2C] no devices found");
  }

  if (MCP23017_I2C_ADDRESS == I2C_ADDRESS_AUTO) {
    mcpAddress = automaticCandidate;
  } else {
    mcpAddress = MCP23017_I2C_ADDRESS;
  }

  if (mcpAddress == 0) {
    Serial.println("[MCP23017] no address found; check VCC, GND, SDA, and SCL");
    return;
  }

  if (!probeI2cAddress(mcpAddress)) {
    Serial.printf("[MCP23017] configured address 0x%02X did not acknowledge\n",
                  mcpAddress);
    mcpAddress = 0;
    return;
  }

  // Start with all pins released. Rows are then changed to outputs and
  // columns get the MCP23017's internal pull-ups.
  uint8_t direction[2] = { 0xFF, 0xFF };
  uint8_t pullups[2] = { 0x00, 0x00 };

  for (uint8_t row = 0; row < KEYPAD_ROWS; ++row) {
    direction[KEYPAD_ROW_PORT[row]] &= (uint8_t)~(1u << KEYPAD_ROW_BIT[row]);
  }
  for (uint8_t column = 0; column < KEYPAD_COLUMNS; ++column) {
    pullups[KEYPAD_COLUMN_PORT[column]] |= (uint8_t)(1u << KEYPAD_COLUMN_BIT[column]);
  }

  bool initialized = true;
  initialized = writeMcpRegister(MCP_IODIRA, direction[MCP_PORT_A]) && initialized;
  initialized = writeMcpRegister(MCP_IODIRB, direction[MCP_PORT_B]) && initialized;
  initialized = writeMcpRegister(MCP_GPPUA, pullups[MCP_PORT_A]) && initialized;
  initialized = writeMcpRegister(MCP_GPPUB, pullups[MCP_PORT_B]) && initialized;
  initialized = writeMcpPortLatch(MCP_PORT_A, 0xFF) && initialized;
  initialized = writeMcpPortLatch(MCP_PORT_B, 0xFF) && initialized;

  mcpPresent = initialized;
  if (mcpPresent) {
    Serial.printf("[MCP23017] ready at 0x%02X; rows GPA0-GPA3, columns GPA4-GPA7\n",
                  mcpAddress);
  } else {
    mcpAddress = 0;
    Serial.println("[MCP23017] found an address but initialization failed");
  }
}

void reportKeyChange(uint8_t row, uint8_t column, bool pressed) {
  const char key = KEYMAP[row][column];

  if (pressed) {
    Serial.printf("[KEYPAD] '%c' pressed (row %u, column %u)\n", key, row, column);
    flashLed(false, false, true);
  } else {
    Serial.printf("[KEYPAD] '%c' released\n", key);
  }
}

void updateKeyState(uint8_t row, uint8_t column, bool pressed, uint32_t now) {
  if (pressed != rawKeyPressed[row][column]) {
    rawKeyPressed[row][column] = pressed;
    rawKeyChangedAt[row][column] = now;
  }

  if (pressed == stableKeyPressed[row][column] ||
      now - rawKeyChangedAt[row][column] < DEBOUNCE_MS) {
    return;
  }

  stableKeyPressed[row][column] = pressed;
  reportKeyChange(row, column, pressed);
}

bool scanKeypadMatrix() {
  if (!mcpPresent) {
    return false;
  }

  const uint32_t now = millis();
  bool readSucceeded = true;

  for (uint8_t row = 0; row < KEYPAD_ROWS; ++row) {
    // Release every row HIGH, then drive one row LOW.
    for (uint8_t otherRow = 0; otherRow < KEYPAD_ROWS; ++otherRow) {
      mcpOutputLatch[KEYPAD_ROW_PORT[otherRow]] |=
        (uint8_t)(1u << KEYPAD_ROW_BIT[otherRow]);
    }
    mcpOutputLatch[KEYPAD_ROW_PORT[row]] &=
      (uint8_t)~(1u << KEYPAD_ROW_BIT[row]);

    bool rowWritten = true;
    rowWritten = writeMcpPortLatch(MCP_PORT_A, mcpOutputLatch[MCP_PORT_A]) && rowWritten;
    rowWritten = writeMcpPortLatch(MCP_PORT_B, mcpOutputLatch[MCP_PORT_B]) && rowWritten;
    if (!rowWritten) {
      readSucceeded = false;
      continue;
    }

    delayMicroseconds(150);

    uint8_t portValue[2] = { 0xFF, 0xFF };
    bool portRead[2] = { false, false };

    for (uint8_t column = 0; column < KEYPAD_COLUMNS; ++column) {
      const uint8_t port = KEYPAD_COLUMN_PORT[column];
      if (!portRead[port]) {
        portRead[port] = readMcpPort(port, portValue[port]);
        if (!portRead[port]) {
          readSucceeded = false;
          continue;
        }
      }

      const bool pressed =
        (portValue[port] & (1u << KEYPAD_COLUMN_BIT[column])) == 0;
      updateKeyState(row, column, pressed, now);
    }
  }

  for (uint8_t row = 0; row < KEYPAD_ROWS; ++row) {
    mcpOutputLatch[KEYPAD_ROW_PORT[row]] |=
      (uint8_t)(1u << KEYPAD_ROW_BIT[row]);
  }
  writeMcpPortLatch(MCP_PORT_A, mcpOutputLatch[MCP_PORT_A]);
  writeMcpPortLatch(MCP_PORT_B, mcpOutputLatch[MCP_PORT_B]);
  return readSucceeded;
}

// --------------------------- Arduino entry points ------------------------
uint32_t lastKeypadScanAt = 0;

void setup() {
  Serial.begin(115200);
  delay(300);

  pinMode(BUTTON_PIN, INPUT_PULLUP);
  pinMode(LED_RED_PIN, OUTPUT);
  pinMode(LED_GREEN_PIN, OUTPUT);
  pinMode(LED_BLUE_PIN, OUTPUT);
  setLed(false, false, false);

  Serial.println();
  Serial.println("=== SPECIAL BUTTON / KEYPAD WIRING TEST ===");
  Serial.println("Button: D2 to GND; LED: D3/D4/D5 common-anode");
  Serial.println("Keypad: SDA=D7, SCL=D8, MCP23017 expander");

  runStartupLedTest();

  Wire.begin(I2C_SDA_PIN, I2C_SCL_PIN, I2C_FREQUENCY_HZ);
  scanI2cBus();

  buttonRawPressed = digitalRead(BUTTON_PIN) == LOW;
  buttonStablePressed = buttonRawPressed;
  buttonRawChangedAt = millis();
  lastKeypadScanAt = millis();

  Serial.println("[TEST] ready: press the button and press every keypad key");
}

void loop() {
  const uint32_t now = millis();

  updateButton();

  if (mcpPresent && now - lastKeypadScanAt >= KEYPAD_SCAN_INTERVAL_MS) {
    lastKeypadScanAt = now;
    if (!scanKeypadMatrix()) {
      Serial.println("[KEYPAD] I2C read failed; check keypad power and wiring");
    }
  }

  updateLedFlash();
  delay(1);
}
