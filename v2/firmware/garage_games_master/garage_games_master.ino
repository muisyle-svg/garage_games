/*
  SPEED BUTTON - MASTER
  ---------------------
  Standalone reaction game for a master XIAO ESP32 and up to 13 spoke buttons.

  Hardware:
    Button : D2 (active LOW)
    RGB LED: D3/D4/D5 (common anode, active LOW)
    TM1637: D0 = CLK, D1 = DIO

  Radio:
    ESP-NOW only. There is no Wi-Fi association, password, scan, or AP.
    All devices must use the same fixed channel below.

  Game:
    - On boot, the master displays RDY until the first start attempt.
    - Hold the master button for 5 seconds to begin discovery.
    - Five quick master-button taps while idle clear the saved high score.
    - A configurable minimum number of the expected 13 spokes must be visible
      to start (set below; currently 3 for testing).
    - Spokes flash red, yellow, green while the master displays 3, 2, 1.
    - After GO, one random live spoke blinks green, then yellow, then red;
      its blink rate accelerates as its deadline approaches.
    - The player has 10 seconds for the first target. Every 10 seconds of
      elapsed game time, future target windows lose 1 s, down to a
      2-second minimum. This rewards getting more hits before each reduction.
    - The same spoke is never selected twice in a row.
    - A valid hit increments the score and immediately cues the next spoke.
    - A timeout ends the game. The master shows GAME / OVER, all spokes hold
      solid red for five seconds, and the final score remains displayed while idle.

  Spoke identity is its ESP-NOW station MAC address. This means the same spoke
  sketch can be flashed to every button; no per-button numeric ID is required.

  IMPORTANT POWER NOTE:
    A completely deep-sleeping ESP32 cannot receive an arbitrary wireless
    ESP-NOW packet. The spokes therefore remain in radio-listening modem mode
    while idle so a new game can be started wirelessly. See the corresponding
    note in the spoke sketch if a wired wake line is added later.
*/

#include <WiFi.h>
#include <esp_now.h>
#include <esp_wifi.h>
#include <esp_system.h>
#include <Preferences.h>
#include <TM1637Display.h>
#include <string.h>
#include <stdlib.h>

// Arduino's automatic prototype generator can place handleHit() before the
// packet definition below. Forward-declare the type so that prototype builds.
struct RxPacket;
enum HostStatus : uint8_t;
enum GarageStatus : uint8_t;
void finishGame(const char* reason);
void finishHostScan(bool busy);
void processRx();
void beginHostScan(const char* scanId);

// --------------------------- Configuration ---------------------------
constexpr uint8_t PROTOCOL_VERSION = 3;
constexpr uint8_t ESPNOW_CHANNEL = 1;
constexpr uint8_t EXPECTED_SPOKES = 13;
constexpr uint8_t MIN_SPOKES_TO_START = 3;

constexpr uint32_t DISCOVERY_MS = 3000;
constexpr uint32_t DISCOVERY_PING_MS = 500;
constexpr uint32_t HEARTBEAT_TIMEOUT_MS = 3500;
constexpr uint32_t TARGET_HEARTBEAT_TIMEOUT_MS = 1800;
constexpr uint32_t NODE_RECLAIM_MS = 15000;
constexpr uint32_t MASTER_HOLD_MS = 5000;
constexpr uint32_t DEBOUNCE_MS = 30;
constexpr uint32_t HOST_HELLO_INTERVAL_MS = 2000;
constexpr uint32_t HOST_SCAN_DURATION_MS = 2000;
constexpr uint32_t HOST_SCAN_PING_MS = 500;
constexpr uint32_t HOST_STATUS_STALE_MS = 3000;
constexpr uint32_t GARAGE_BROADCAST_INTERVAL_MS = 250;
constexpr uint8_t HOST_SCAN_MAX_RESPONDERS = 64;
constexpr size_t HOST_SCAN_ID_MAX_LENGTH = 32;
constexpr uint8_t HOST_RX_BYTES_PER_LOOP = 32;
constexpr size_t HOST_RX_LINE_CAPACITY = 80;
constexpr uint32_t CONTROL_REPEAT_GAP_MS = 25;
constexpr uint8_t HIGH_SCORE_RESET_TAPS = 5;
constexpr uint32_t QUICK_TAP_MAX_MS = 600;
constexpr uint32_t QUICK_TAP_GAP_MS = 900;
constexpr uint32_t SCORE_RESET_FEEDBACK_MS = 1500;

constexpr uint32_t INITIAL_WINDOW_MS = 10000;
constexpr uint32_t DIFFICULTY_INTERVAL_MS = 10000;
constexpr uint32_t WINDOW_STEP_MS = 1000;
constexpr uint32_t MIN_WINDOW_MS = 2000;
constexpr uint32_t CUE_RESEND_MS = 350;
constexpr uint32_t CUE_READY_TIMEOUT_MS = 1500;
constexpr uint32_t TARGET_LOST_GRACE_MS = 1000;
constexpr uint32_t TARGET_PACKET_GRACE_MS = 250;

constexpr uint32_t PREP_PHASE_MS = 500;
constexpr uint32_t GO_PHASE_MS = 700;
constexpr uint32_t END_FLASH_MS = 5000;
constexpr uint32_t END_MESSAGE_RESEND_MS = 250;
constexpr uint32_t LAST_SCORE_DISPLAY_MS = 30000;
constexpr uint32_t HIGH_SCORE_ALTERNATE_MS = 1500;

constexpr char HIGH_SCORE_NAMESPACE[] = "speedbtn";
constexpr char HIGH_SCORE_KEY[] = "high";

constexpr uint8_t PENDING_TX_SLOTS = 8;
constexpr uint8_t PENDING_TX_MESSAGE_MAX = 63;

#define BTN_PIN D2
#define LED_R   D3
#define LED_G   D4
#define LED_B   D5
#define TM_CLK  D0
#define TM_DIO  D1

TM1637Display display(TM_CLK, TM_DIO);
Preferences highScorePreferences;

const uint8_t BROADCAST_MAC[6] = {0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF};

// --------------------------- LED helpers ---------------------------
void setLEDLevels(uint8_t red, uint8_t green, uint8_t blue) {
  // The RGB LED is common-anode: 255 means fully on, but the PWM output
  // value must be inverted because LOW is the active state.
  analogWrite(LED_R, 255 - red);
  analogWrite(LED_G, 255 - green);
  analogWrite(LED_B, 255 - blue);
}

void setLED(bool r, bool g, bool b) {
  setLEDLevels(r ? 255 : 0, g ? 255 : 0, b ? 255 : 0);
}

bool ledBlinking = false;
bool ledOn = false;
bool ledR = false, ledG = false, ledB = false;
uint32_t ledPeriodMs = 250;
uint32_t ledNextToggleMs = 0;
uint32_t ledUntilMs = 0;  // 0 = no expiry

void stopLEDPattern() {
  ledBlinking = false;
  ledOn = false;
  ledUntilMs = 0;
  setLED(false, false, false);
}

void solidLED(bool r, bool g, bool b) {
  ledBlinking = false;
  ledUntilMs = 0;
  setLED(r, g, b);
}

void startLEDBlink(bool r, bool g, bool b, uint32_t periodMs, uint32_t durationMs = 0) {
  ledR = r;
  ledG = g;
  ledB = b;
  ledPeriodMs = periodMs;
  ledBlinking = true;
  ledOn = false;
  ledNextToggleMs = millis();
  ledUntilMs = durationMs == 0 ? 0 : millis() + durationMs;
  setLED(false, false, false);
}

void updateLED() {
  uint32_t now = millis();
  if (ledUntilMs != 0 && (int32_t)(now - ledUntilMs) >= 0) {
    stopLEDPattern();
    return;
  }
  if (!ledBlinking || (int32_t)(now - ledNextToggleMs) < 0) return;
  ledOn = !ledOn;
  ledNextToggleMs = now + ledPeriodMs;
  setLED(ledOn && ledR, ledOn && ledG, ledOn && ledB);
}

// --------------------------- Display helpers ---------------------------
uint8_t segmentFor(char c) {
  switch (c) {
    case '0': return 0x3F; case '1': return 0x06; case '2': return 0x5B;
    case '3': return 0x4F; case '4': return 0x66; case '5': return 0x6D;
    case '6': return 0x7D; case '7': return 0x07; case '8': return 0x7F;
    case '9': return 0x6F;
    case 'A': return 0x77; case 'B': return 0x7C; case 'C': return 0x39;
    case 'D': return 0x5E; case 'E': return 0x79; case 'F': return 0x71;
    case 'G': return 0x3D; case 'H': return 0x76; case 'I': return 0x06;
    case 'J': return 0x1E; case 'L': return 0x38; case 'M': return 0x37;
    case 'N': return 0x54; case 'O': return 0x3F; case 'P': return 0x73;
    case 'R': return 0x50; case 'S': return 0x6D; case 'T': return 0x78;
    case 'U': return 0x3E; case 'V': return 0x3E; case 'W': return 0x2A;
    case 'Y': return 0x6E; case '-': return 0x40;
    default: return 0x00;
  }
}

void showText4(const char* text) {
  uint8_t segments[4] = {0, 0, 0, 0};
  for (uint8_t i = 0; i < 4 && text[i] != '\0'; ++i) {
    char c = text[i];
    bool lowercaseI = c == 'i';
    if (c >= 'a' && c <= 'z') c = (char)(c - 'a' + 'A');
    segments[i] = c == ' ' ? 0 : (lowercaseI ? 0x10 : segmentFor(c));
  }
  display.setSegments(segments);
}

void showScore(uint16_t score) {
  display.showNumberDec(score, false);
}

void showMinutesSeconds(uint32_t totalSeconds) {
  // TM1637 numeric formatting expects MMSS, not a total-seconds value.
  if (totalSeconds > 5999) totalSeconds = 5999;
  uint16_t minutesSeconds = (uint16_t)((totalSeconds / 60) * 100 + totalSeconds % 60);
  display.showNumberDecEx(minutesSeconds, 0b01000000, true);
}

void showRemainingSeconds(uint16_t seconds) {
  showMinutesSeconds(seconds);
}

// --------------------------- ESP-NOW packets ---------------------------
constexpr uint8_t RX_MAX_LEN = 63;
struct RxPacket {
  uint8_t source[6];
  uint8_t len;
  uint32_t receivedAtMs;
  char data[RX_MAX_LEN + 1];
};

QueueHandle_t rxQueue = nullptr;
volatile uint32_t rxQueueDropCount = 0;
volatile uint32_t sendCallbackFailureCount = 0;
bool radioReady = false;

struct PendingTransmission {
  bool active;
  uint8_t attemptsLeft;
  uint32_t dueMs;
  char message[PENDING_TX_MESSAGE_MAX + 1];
};

PendingTransmission pendingTx[PENDING_TX_SLOTS];

void onSent(const wifi_tx_info_t*, esp_now_send_status_t status) {
  if (status != ESP_NOW_SEND_SUCCESS) ++sendCallbackFailureCount;
}

void onRecv(const esp_now_recv_info* info, const uint8_t* data, int len) {
  if (!info || !info->src_addr || !data || len <= 0 || !rxQueue) return;
  RxPacket packet{};
  memcpy(packet.source, info->src_addr, 6);
  packet.len = (uint8_t)min(len, (int)RX_MAX_LEN);
  packet.receivedAtMs = millis();
  memcpy(packet.data, data, packet.len);
  packet.data[packet.len] = '\0';
  if (xQueueSend(rxQueue, &packet, 0) != pdTRUE) ++rxQueueDropCount;
}

bool sendBroadcast(const char* message) {
  if (!radioReady || !message) return false;
  esp_err_t result = esp_now_send(BROADCAST_MAC, (const uint8_t*)message, strlen(message));
  if (result != ESP_OK) {
    Serial.printf("[RADIO] Send failed (%d): %s\n", (int)result, message);
    return false;
  }
  return true;
}

bool scheduleTransmission(const char* message, uint32_t dueMs, uint8_t attempts) {
  if (!message || strlen(message) > PENDING_TX_MESSAGE_MAX) return false;
  for (uint8_t i = 0; i < PENDING_TX_SLOTS; ++i) {
    if (pendingTx[i].active) continue;
    pendingTx[i].active = true;
    pendingTx[i].attemptsLeft = attempts;
    pendingTx[i].dueMs = dueMs;
    strncpy(pendingTx[i].message, message, sizeof(pendingTx[i].message) - 1);
    pendingTx[i].message[sizeof(pendingTx[i].message) - 1] = '\0';
    return true;
  }
  Serial.println("[RADIO] Pending transmit queue full");
  return false;
}

void sendBroadcastTwice(const char* message) {
  sendBroadcast(message);
  scheduleTransmission(message, millis() + CONTROL_REPEAT_GAP_MS, 2);
}

void updatePendingTransmissions() {
  uint32_t now = millis();
  for (uint8_t i = 0; i < PENDING_TX_SLOTS; ++i) {
    if (!pendingTx[i].active || (int32_t)(now - pendingTx[i].dueMs) < 0) continue;
    bool sent = sendBroadcast(pendingTx[i].message);
    if (sent || pendingTx[i].attemptsLeft <= 1) {
      pendingTx[i].active = false;
    } else {
      --pendingTx[i].attemptsLeft;
      pendingTx[i].dueMs = now + CONTROL_REPEAT_GAP_MS;
    }
  }
}

// --------------------------- Spoke tracking ---------------------------
struct Spoke {
  bool used;
  uint8_t mac[6];
  uint32_t lastSeenMs;
  uint32_t seenDiscoveryToken;
};

Spoke spokes[EXPECTED_SPOKES];
uint32_t discoveryToken = 0;

bool macEqual(const uint8_t* a, const uint8_t* b) {
  return memcmp(a, b, 6) == 0;
}

void macToHex(const uint8_t* mac, char* out, size_t outSize) {
  if (outSize < 13) return;
  snprintf(out, outSize, "%02X%02X%02X%02X%02X%02X",
           mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
}

bool hexNibble(char c, uint8_t& value) {
  if (c >= '0' && c <= '9') { value = (uint8_t)(c - '0'); return true; }
  if (c >= 'A' && c <= 'F') { value = (uint8_t)(c - 'A' + 10); return true; }
  if (c >= 'a' && c <= 'f') { value = (uint8_t)(c - 'a' + 10); return true; }
  return false;
}

bool hexToMac(const char* text, uint8_t* mac) {
  if (!text || strlen(text) < 12) return false;
  for (uint8_t i = 0; i < 6; ++i) {
    uint8_t hi, lo;
    if (!hexNibble(text[i * 2], hi) || !hexNibble(text[i * 2 + 1], lo)) return false;
    mac[i] = (uint8_t)((hi << 4) | lo);
  }
  return true;
}

int findSpoke(const uint8_t* mac) {
  for (int i = 0; i < EXPECTED_SPOKES; ++i) {
    if (spokes[i].used && macEqual(spokes[i].mac, mac)) return i;
  }
  return -1;
}

int upsertSpoke(const uint8_t* mac, bool& newlyAdded) {
  newlyAdded = false;
  int existing = findSpoke(mac);
  if (existing >= 0) {
    spokes[existing].lastSeenMs = millis();
    return existing;
  }

  for (int i = 0; i < EXPECTED_SPOKES; ++i) {
    if (!spokes[i].used) {
      spokes[i].used = true;
      memcpy(spokes[i].mac, mac, 6);
      spokes[i].lastSeenMs = millis();
      spokes[i].seenDiscoveryToken = 0;
      newlyAdded = true;
      char id[13]; macToHex(mac, id, sizeof(id));
      Serial.printf("[NODE] Added %s as slot %d\n", id, i);
      return i;
    }
  }

  int reclaim = -1;
  uint32_t oldestAgeMs = 0;
  uint32_t now = millis();
  for (int i = 0; i < EXPECTED_SPOKES; ++i) {
    uint32_t ageMs = now - spokes[i].lastSeenMs;
    if (ageMs >= NODE_RECLAIM_MS && ageMs >= oldestAgeMs) {
      reclaim = i;
      oldestAgeMs = ageMs;
    }
  }
  if (reclaim >= 0) {
    char oldId[13];
    char newId[13];
    macToHex(spokes[reclaim].mac, oldId, sizeof(oldId));
    macToHex(mac, newId, sizeof(newId));
    memcpy(spokes[reclaim].mac, mac, 6);
    spokes[reclaim].lastSeenMs = now;
    spokes[reclaim].seenDiscoveryToken = 0;
    newlyAdded = true;
    Serial.printf("[NODE] Reclaimed stale slot %d: %s -> %s\n", reclaim, oldId, newId);
    return reclaim;
  }

  Serial.println("[NODE] Table full; compatible spoke could not be added");
  return -1;
}

bool spokeAlive(int index) {
  if (index < 0 || index >= EXPECTED_SPOKES || !spokes[index].used) return false;
  if (spokes[index].seenDiscoveryToken != discoveryToken) return false;
  if (spokes[index].lastSeenMs == 0) return false;
  return (uint32_t)(millis() - spokes[index].lastSeenMs) <= HEARTBEAT_TIMEOUT_MS;
}

bool targetSpokeAlive(int index) {
  if (index < 0 || index >= EXPECTED_SPOKES || !spokes[index].used) return false;
  if (spokes[index].seenDiscoveryToken != discoveryToken) return false;
  if (spokes[index].lastSeenMs == 0) return false;
  return (uint32_t)(millis() - spokes[index].lastSeenMs) <= TARGET_HEARTBEAT_TIMEOUT_MS;
}

int aliveSpokeCount() {
  int count = 0;
  for (int i = 0; i < EXPECTED_SPOKES; ++i) if (spokeAlive(i)) ++count;
  return count;
}

int discoveredSpokeCount(uint32_t token) {
  int count = 0;
  for (int i = 0; i < EXPECTED_SPOKES; ++i) {
    if (spokes[i].used && spokes[i].seenDiscoveryToken == token) ++count;
  }
  return count;
}

void printSpokeCount() {
  Serial.printf("[NODES] %d/%d live\n", aliveSpokeCount(), EXPECTED_SPOKES);
}

// --------------------------- Game state ---------------------------
enum GameState { IDLE, DISCOVERING, COUNTDOWN, PLAYING, ENDING, ERROR_STATE };
GameState gameState = IDLE;

uint32_t runId = 0;
uint32_t cueSequence = 0;
uint32_t currentWindowMs = INITIAL_WINDOW_MS;
uint32_t activeTargetWindowMs = INITIAL_WINDOW_MS;
uint32_t gameStartedMs = 0;
uint32_t difficultyIntervalsApplied = 0;
uint32_t targetDeadlineMs = 0;
uint32_t nextCueResendMs = 0;
uint32_t cueReadyDeadlineMs = 0;
uint32_t targetLostSinceMs = 0;
bool targetReady = false;
int lastRemainingShown = -1;
int currentTarget = -1;
int lastSelectedTarget = -1;
uint16_t score = 0;
uint16_t lastScore = 0;
bool haveScore = false;
uint16_t highScore = 0;
uint32_t lastScoreDisplayUntilMs = 0;
bool highScoreLabelPhase = true;
uint32_t nextHighScoreDisplayMs = 0;
bool highScoreSmallShown = false;
bool bootReadyDisplayActive = true;
bool bootReadyDisplayed = false;
uint32_t scoreResetFeedbackUntilMs = 0;

uint32_t discoveryUntilMs = 0;
uint32_t nextDiscoveryPingMs = 0;
uint8_t countdownStep = 0;
uint32_t nextCountdownStepMs = 0;
uint32_t goDisplayUntilMs = 0;

uint32_t endingUntilMs = 0;
uint32_t nextEndMessageMs = 0;
uint8_t endDisplayStep = 0;
uint32_t nextEndDisplayMs = 0;

uint32_t errorUntilMs = 0;
bool errorWasNoButtons = false;

uint32_t masterHoldStartedMs = 0;
bool masterHoldActive = false;
bool masterStartTriggered = false;
bool masterStartArmed = true;
bool lastButtonRead = true;
bool stableButton = true;
uint32_t lastButtonChangeMs = 0;
uint8_t quickTapCount = 0;
uint32_t lastQuickTapMs = 0;
uint32_t lastRadioDiagnosticMs = 0;
uint32_t lastReportedRxDrops = 0;
uint32_t lastReportedSendFailures = 0;

enum HostStatus : uint8_t {
  HOST_STATUS_NONE,
  HOST_STATUS_ARMED,
  HOST_STATUS_COUNTDOWN,
  HOST_STATUS_ACTIVE,
  HOST_STATUS_PAUSED,
  HOST_STATUS_FINISHED
};

enum GarageStatus : uint8_t {
  GARAGE_STATUS_NONE,
  GARAGE_STATUS_ARMED,
  GARAGE_STATUS_COUNTDOWN,
  GARAGE_STATUS_ACTIVE,
  GARAGE_STATUS_PAUSED,
  GARAGE_STATUS_FINISHED
};

enum FirmwareMode { FIRMWARE_MODE_IDLE, FIRMWARE_MODE_SPEED };

HostStatus hostStatus = HOST_STATUS_NONE;
uint32_t hostRemainingSeconds = 0;
uint32_t lastHostStatusMs = 0;
bool hostStatusReceived = false;
GarageStatus garageStatus = GARAGE_STATUS_NONE;
char garageToken[17] = "-";
bool garageStatusReceived = false;
uint32_t lastGarageStatusMs = 0;
uint32_t nextGarageBroadcastMs = 0;
uint8_t masterMac[6] = {};
uint32_t bootToken = 0;
uint32_t hostStartSequence = 0;
uint32_t lastHostHelloMs = 0;
FirmwareMode lastReportedMode = FIRMWARE_MODE_IDLE;
bool hostHelloSent = false;
bool hostModeReported = false;
char hostRxLine[HOST_RX_LINE_CAPACITY];
size_t hostRxLength = 0;
bool hostRxOverflow = false;
bool hostCountdownShown = false;
uint32_t hostCountdownLastSeconds = 0;
HostStatus hostCountdownLastStatus = HOST_STATUS_NONE;
bool hostCountdownWasActive = false;
bool scoreResetSaved = true;

bool hostStatusFresh(uint32_t now) {
  return hostStatusReceived &&
         (uint32_t)(now - lastHostStatusMs) <= HOST_STATUS_STALE_MS;
}

bool hostBlocksStarts() {
  return hostStatusFresh(millis()) &&
         (hostStatus == HOST_STATUS_COUNTDOWN ||
          hostStatus == HOST_STATUS_ACTIVE || hostStatus == HOST_STATUS_PAUSED);
}

struct HostScanResponder {
  uint8_t mac[6];
};

HostScanResponder hostScanResponders[HOST_SCAN_MAX_RESPONDERS];
uint16_t hostScanResponderCount = 0;
bool hostScanActive = false;
char hostScanId[HOST_SCAN_ID_MAX_LENGTH + 1];
uint32_t hostScanToken = 0;
uint32_t lastHostScanToken = 0;
uint32_t hostScanStartedMs = 0;
uint32_t hostScanUntilMs = 0;
uint32_t nextHostScanPingMs = 0;

bool hostScanSafe(uint32_t now) {
  return gameState == IDLE && !hostBlocksStarts() && hostStatusFresh(now);
}

bool hostScanWindowContains(uint32_t receivedAtMs) {
  return (int32_t)(receivedAtMs - hostScanStartedMs) >= 0 &&
         (int32_t)(hostScanUntilMs - receivedAtMs) > 0;
}

bool hostScanHasResponder(const uint8_t* mac) {
  for (uint16_t i = 0; i < hostScanResponderCount; ++i) {
    if (macEqual(hostScanResponders[i].mac, mac)) return true;
  }
  return false;
}

void recordHostScanResponder(const uint8_t* mac) {
  if (hostScanHasResponder(mac)) return;
  if (hostScanResponderCount >= HOST_SCAN_MAX_RESPONDERS) {
    Serial.printf("[SCAN] Responder table full for scan %s\n", hostScanId);
    finishHostScan(true);
    return;
  }

  memcpy(hostScanResponders[hostScanResponderCount].mac, mac, 6);
  ++hostScanResponderCount;
  char macText[13];
  macToHex(mac, macText, sizeof(macText));
  Serial.printf("GG1 SCAN %s NODE %s\n", hostScanId, macText);
}

void emitHostScanDiscovery() {
  char message[40];
  snprintf(message, sizeof(message), "DISCOVER:%u:%lu",
           PROTOCOL_VERSION, (unsigned long)hostScanToken);
  sendBroadcast(message);
}

void finishHostScan(bool busy) {
  if (!hostScanActive) return;
  if (busy) {
    Serial.printf("GG1 SCAN %s BUSY\n", hostScanId);
  } else {
    Serial.printf("GG1 SCAN %s DONE %u\n", hostScanId,
                  (unsigned)hostScanResponderCount);
  }
  hostScanActive = false;
  hostScanId[0] = '\0';
}

void updateHostScan() {
  if (!hostScanActive) return;
  uint32_t now = millis();
  if (!hostScanSafe(now)) {
    finishHostScan(true);
    return;
  }
  if ((int32_t)(now - hostScanUntilMs) >= 0) {
    finishHostScan(false);
    return;
  }
  if ((int32_t)(now - nextHostScanPingMs) >= 0) {
    emitHostScanDiscovery();
    nextHostScanPingMs = now + HOST_SCAN_PING_MS;
  }
}

bool hostWaitingLedActive = false;

void updateHostStatusLED() {
  if (gameState != IDLE) {
    hostWaitingLedActive = false;
    return;
  }

  if (hostStatusFresh(millis()) && hostStatus == HOST_STATUS_COUNTDOWN) {
    if (!hostWaitingLedActive) {
      startLEDBlink(true, true, false, 700);
      hostWaitingLedActive = true;
    }
  } else if (hostWaitingLedActive) {
    stopLEDPattern();
    hostWaitingLedActive = false;
  }
}

bool readProtocolToken(const char*& cursor, char* output, size_t outputSize) {
  if (!cursor || !output || outputSize < 2 || *cursor == '\0' || *cursor == ' ') {
    return false;
  }
  size_t length = 0;
  while (*cursor != '\0' && *cursor != ' ') {
    if (length + 1 >= outputSize) return false;
    output[length++] = *cursor++;
  }
  output[length] = '\0';
  if (*cursor == ' ') {
    ++cursor;
    if (*cursor == '\0' || *cursor == ' ') return false;
  }
  return length > 0;
}

bool parseGarageToken(const char* text, char* output) {
  if (!text || !output) return false;
  if (strcmp(text, "-") == 0) {
    strcpy(output, "-");
    return true;
  }
  if (strlen(text) != 16) return false;
  for (uint8_t i = 0; i < 16; ++i) {
    char value = text[i];
    if (!((value >= '0' && value <= '9') ||
          (value >= 'A' && value <= 'F'))) return false;
    output[i] = value;
  }
  output[16] = '\0';
  return true;
}

bool parseGarageStatusName(const char* text, GarageStatus& status) {
  if (strcmp(text, "NONE") == 0) status = GARAGE_STATUS_NONE;
  else if (strcmp(text, "ARMED") == 0) status = GARAGE_STATUS_ARMED;
  else if (strcmp(text, "COUNTDOWN") == 0) status = GARAGE_STATUS_COUNTDOWN;
  else if (strcmp(text, "ACTIVE") == 0) status = GARAGE_STATUS_ACTIVE;
  else if (strcmp(text, "PAUSED") == 0) status = GARAGE_STATUS_PAUSED;
  else if (strcmp(text, "FINISHED") == 0) status = GARAGE_STATUS_FINISHED;
  else return false;
  return true;
}

bool parseGarageStatusLine(const char* line, char* token, GarageStatus& status) {
  static const char prefix[] = "GG1 GARAGE ";
  if (!line || !token || strncmp(line, prefix, sizeof(prefix) - 1) != 0) return false;
  const char* cursor = line + sizeof(prefix) - 1;
  char tokenText[17];
  char statusText[10];
  if (!readProtocolToken(cursor, tokenText, sizeof(tokenText)) ||
      !readProtocolToken(cursor, statusText, sizeof(statusText)) || *cursor != '\0' ||
      !parseGarageToken(tokenText, token) || !parseGarageStatusName(statusText, status)) {
    return false;
  }
  if ((status == GARAGE_STATUS_NONE) != (strcmp(token, "-") == 0)) return false;
  return true;
}

bool parseUpperHexMac(const char* text, uint8_t* mac) {
  if (!text || !mac || strlen(text) != 12) return false;
  for (uint8_t i = 0; i < 6; ++i) {
    uint8_t value = 0;
    for (uint8_t digit = 0; digit < 2; ++digit) {
      char c = text[i * 2 + digit];
      uint8_t nibble;
      if (c >= '0' && c <= '9') nibble = (uint8_t)(c - '0');
      else if (c >= 'A' && c <= 'F') nibble = (uint8_t)(c - 'A' + 10);
      else return false;
      value = (uint8_t)((value << 4) | nibble);
    }
    mac[i] = value;
  }
  return true;
}

bool validStationMac(const uint8_t* mac) {
  bool allZero = true;
  for (uint8_t i = 0; i < 6; ++i) if (mac[i] != 0) allZero = false;
  return !allZero && (mac[0] & 0x01) == 0;
}

bool parseUint32Token(const char* text, uint32_t& value) {
  if (!text || *text == '\0') return false;
  uint32_t parsed = 0;
  for (const char* cursor = text; *cursor != '\0'; ++cursor) {
    if (*cursor < '0' || *cursor > '9') return false;
    uint32_t digit = (uint32_t)(*cursor - '0');
    if (parsed > (UINT32_MAX - digit) / 10) return false;
    parsed = parsed * 10 + digit;
  }
  value = parsed;
  return true;
}

bool parseGarageResultLine(const char* line, char* token, uint8_t* mac,
                           uint32_t& sequence, char* resultState, size_t stateSize) {
  static const char prefix[] = "GG1 RESULT ";
  if (!line || strncmp(line, prefix, sizeof(prefix) - 1) != 0) return false;
  const char* cursor = line + sizeof(prefix) - 1;
  char tokenText[17];
  char macText[13];
  char sequenceText[11];
  if (!readProtocolToken(cursor, tokenText, sizeof(tokenText)) ||
      !readProtocolToken(cursor, macText, sizeof(macText)) ||
      !readProtocolToken(cursor, sequenceText, sizeof(sequenceText)) ||
      !readProtocolToken(cursor, resultState, stateSize) || *cursor != '\0' ||
      !parseGarageToken(tokenText, token) || strcmp(token, "-") == 0 ||
      !parseUpperHexMac(macText, mac) || !validStationMac(mac) ||
      !parseUint32Token(sequenceText, sequence) || sequence == 0) return false;
  return strcmp(resultState, "PENDING") == 0 || strcmp(resultState, "ACTIVE") == 0 ||
         strcmp(resultState, "COMPLETED") == 0 || strcmp(resultState, "REJECTED") == 0;
}

void acceptHostStatus(HostStatus status, uint32_t remainingSeconds) {
  if (status == HOST_STATUS_COUNTDOWN && hostStatus != HOST_STATUS_COUNTDOWN) {
    quickTapCount = 0;
    lastQuickTapMs = 0;
    if (masterHoldActive) {
      masterHoldActive = false;
      masterStartTriggered = true;
      masterStartArmed = false;
    }
  }
  hostStatus = status;
  hostRemainingSeconds = remainingSeconds;
  lastHostStatusMs = millis();
  hostStatusReceived = true;
}

bool garageStatusFresh(uint32_t now) {
  return garageStatusReceived &&
         (uint32_t)(now - lastGarageStatusMs) <= HOST_STATUS_STALE_MS;
}

bool sendGarageState(const char* token, GarageStatus status) {
  const char* stateText = "NONE";
  switch (status) {
    case GARAGE_STATUS_ARMED: stateText = "ARMED"; break;
    case GARAGE_STATUS_COUNTDOWN: stateText = "COUNTDOWN"; break;
    case GARAGE_STATUS_ACTIVE: stateText = "ACTIVE"; break;
    case GARAGE_STATUS_PAUSED: stateText = "PAUSED"; break;
    case GARAGE_STATUS_FINISHED: stateText = "FINISHED"; break;
    default: break;
  }
  char message[40];
  int length = snprintf(message, sizeof(message), "GARAGE:3:%s:%s",
                        token, stateText);
  return length > 0 && length <= 63 && sendBroadcast(message);
}

void updateGarageBroadcast() {
  uint32_t now = millis();
  if ((int32_t)(now - nextGarageBroadcastMs) < 0) return;
  nextGarageBroadcastMs = now + GARAGE_BROADCAST_INTERVAL_MS;
  if (gameState != IDLE) return;
  if (!garageStatusFresh(now) || !hostStatusFresh(now)) {
    sendGarageState("-", GARAGE_STATUS_NONE);
    return;
  }
  sendGarageState(garageToken, garageStatus);
}

bool parseHostStatusLine(const char* line, HostStatus& parsedStatus,
                         uint32_t& parsedRemainingSeconds) {
  static const char prefix[] = "GG1 STATUS ";
  if (strncmp(line, prefix, sizeof(prefix) - 1) != 0) return false;

  const char* statusText = line + sizeof(prefix) - 1;
  const char* separator = strchr(statusText, ' ');
  if (!separator || separator == statusText) return false;
  size_t statusLength = (size_t)(separator - statusText);

  if (statusLength == 4 && strncmp(statusText, "NONE", 4) == 0) {
    parsedStatus = HOST_STATUS_NONE;
  } else if (statusLength == 5 && strncmp(statusText, "ARMED", 5) == 0) {
    parsedStatus = HOST_STATUS_ARMED;
  } else if (statusLength == 9 && strncmp(statusText, "COUNTDOWN", 9) == 0) {
    parsedStatus = HOST_STATUS_COUNTDOWN;
  } else if (statusLength == 6 && strncmp(statusText, "ACTIVE", 6) == 0) {
    parsedStatus = HOST_STATUS_ACTIVE;
  } else if (statusLength == 6 && strncmp(statusText, "PAUSED", 6) == 0) {
    parsedStatus = HOST_STATUS_PAUSED;
  } else if (statusLength == 8 && strncmp(statusText, "FINISHED", 8) == 0) {
    parsedStatus = HOST_STATUS_FINISHED;
  } else {
    return false;
  }

  const char* secondsText = separator + 1;
  if (*secondsText == '\0') return false;
  uint32_t seconds = 0;
  for (const char* cursor = secondsText; *cursor != '\0'; ++cursor) {
    if (*cursor < '0' || *cursor > '9') return false;
    uint32_t digit = (uint32_t)(*cursor - '0');
    if (seconds > (UINT32_MAX - digit) / 10) return false;
    seconds = seconds * 10 + digit;
  }

  parsedRemainingSeconds = seconds;
  return true;
}

bool parseHostScanCommand(const char* line, char* scanId, size_t scanIdSize) {
  static const char prefix[] = "GG1 SCAN ";
  if (!line || !scanId || scanIdSize < 2 ||
      strncmp(line, prefix, sizeof(prefix) - 1) != 0) return false;

  const char* cursor = line + sizeof(prefix) - 1;
  size_t length = 0;
  while (*cursor != '\0') {
    char value = *cursor++;
    bool allowed = (value >= 'A' && value <= 'Z') ||
                   (value >= 'a' && value <= 'z') ||
                   (value >= '0' && value <= '9') || value == '-' || value == '_';
    if (!allowed || length >= HOST_SCAN_ID_MAX_LENGTH || length + 1 >= scanIdSize) {
      return false;
    }
    scanId[length++] = value;
  }
  if (length == 0) return false;
  scanId[length] = '\0';
  return true;
}

void beginHostScan(const char* scanId) {
  if (!scanId) return;
  uint32_t now = millis();
  if (hostScanActive || masterHoldActive || !radioReady || !hostScanSafe(now)) {
    Serial.printf("GG1 SCAN %s BUSY\n", scanId);
    return;
  }

  strncpy(hostScanId, scanId, sizeof(hostScanId) - 1);
  hostScanId[sizeof(hostScanId) - 1] = '\0';
  hostScanResponderCount = 0;
  if (lastHostScanToken == 0) {
    do {
      hostScanToken = esp_random();
    } while (hostScanToken == 0 || hostScanToken == discoveryToken);
  } else {
    hostScanToken = lastHostScanToken;
    do {
      ++hostScanToken;
    } while (hostScanToken == 0 || hostScanToken == discoveryToken);
  }
  lastHostScanToken = hostScanToken;
  hostScanStartedMs = now;
  hostScanUntilMs = now + HOST_SCAN_DURATION_MS;
  nextHostScanPingMs = now;
  hostScanActive = true;
}

void processHostSerialLine(const char* line) {
  char parsedGarageToken[17];
  GarageStatus parsedGarageStatus;
  if (parseGarageStatusLine(line, parsedGarageToken, parsedGarageStatus)) {
    memcpy(garageToken, parsedGarageToken, sizeof(garageToken));
    garageStatus = parsedGarageStatus;
    garageStatusReceived = true;
    lastGarageStatusMs = millis();
    return;
  }

  char resultToken[17];
  uint8_t resultMac[6];
  uint32_t resultSequence = 0;
  char resultState[10];
  if (parseGarageResultLine(line, resultToken, resultMac, resultSequence,
                            resultState, sizeof(resultState))) {
    uint32_t now = millis();
    if (gameState != IDLE || !garageStatusFresh(now) || !hostStatusFresh(now) ||
        hostStatus == HOST_STATUS_NONE ||
        garageStatus == GARAGE_STATUS_NONE || strcmp(resultToken, garageToken) != 0) {
      return;
    }
    char macText[13];
    macToHex(resultMac, macText, sizeof(macText));
    char message[64];
    int length = snprintf(message, sizeof(message), "GRESULT:3:%s:%lu:%s:%s",
                          resultToken, (unsigned long)resultSequence,
                          resultState, macText);
    if (length > 0 && length <= 63) sendBroadcastTwice(message);
    return;
  }

  HostStatus parsedStatus;
  uint32_t parsedRemainingSeconds;
  if (parseHostStatusLine(line, parsedStatus, parsedRemainingSeconds)) {
    acceptHostStatus(parsedStatus, parsedRemainingSeconds);
    return;
  }

  char scanId[HOST_SCAN_ID_MAX_LENGTH + 1];
  if (parseHostScanCommand(line, scanId, sizeof(scanId))) beginHostScan(scanId);
}

void updateHostSerialInput() {
  uint8_t bytesRead = 0;
  while (Serial.available() > 0 && bytesRead < HOST_RX_BYTES_PER_LOOP) {
    int incoming = Serial.read();
    if (incoming < 0) break;
    ++bytesRead;
    char value = (char)incoming;

    if (value == '\r' || value == '\n') {
      if (!hostRxOverflow && hostRxLength > 0) {
        hostRxLine[hostRxLength] = '\0';
        processHostSerialLine(hostRxLine);
      }
      hostRxLength = 0;
      hostRxOverflow = false;
      continue;
    }
    if (incoming < 0x20 || incoming > 0x7E) {
      hostRxOverflow = true;
      continue;
    }
    if (!hostRxOverflow) {
      if (hostRxLength < sizeof(hostRxLine) - 1) {
        hostRxLine[hostRxLength++] = value;
      } else {
        hostRxOverflow = true;
      }
    }
  }
}

void reportHostMode(bool force = false) {
  FirmwareMode mode = gameState == IDLE ? FIRMWARE_MODE_IDLE : FIRMWARE_MODE_SPEED;
  if (!force && hostModeReported && mode == lastReportedMode) return;
  Serial.printf("GG1 MODE %s\n", mode == FIRMWARE_MODE_IDLE ? "IDLE" : "SPEED");
  lastReportedMode = mode;
  hostModeReported = true;
}

void emitHostHello() {
  Serial.printf("GG1 HELLO %lu\n", (unsigned long)bootToken);
  lastHostHelloMs = millis();
  hostHelloSent = true;
  reportHostMode(true);
}

void updateHostSerialOutput() {
  uint32_t now = millis();
  if (!hostHelloSent || (uint32_t)(now - lastHostHelloMs) >= HOST_HELLO_INTERVAL_MS) {
    emitHostHello();
    return;
  }
  reportHostMode();
}

void emitHostStart() {
  ++hostStartSequence;
  if (hostStartSequence == 0) ++hostStartSequence;
  Serial.printf("GG1 START %lu %lu\n", (unsigned long)bootToken,
                (unsigned long)hostStartSequence);
}

void loadHighScore() {
  if (!highScorePreferences.begin(HIGH_SCORE_NAMESPACE, true)) {
    highScore = 0;
    Serial.println("[SCORE] Could not open high-score storage; using 0");
    return;
  }
  highScore = highScorePreferences.getUShort(HIGH_SCORE_KEY, 0);
  highScorePreferences.end();
}

bool saveHighScore() {
  if (!highScorePreferences.begin(HIGH_SCORE_NAMESPACE, false)) return false;
  size_t written = highScorePreferences.putUShort(HIGH_SCORE_KEY, highScore);
  highScorePreferences.end();
  return written == sizeof(uint16_t);
}

void clearSavedHighScore() {
  uint16_t previousHighScore = highScore;
  highScore = 0;
  bool saved = saveHighScore();
  if (!saved) highScore = previousHighScore;

  quickTapCount = 0;
  lastQuickTapMs = 0;
  highScoreLabelPhase = true;
  nextHighScoreDisplayMs = millis();
  highScoreSmallShown = false;
  if (saved) lastScoreDisplayUntilMs = 0;

  scoreResetFeedbackUntilMs = millis() + SCORE_RESET_FEEDBACK_MS;
  scoreResetSaved = saved;
  showText4(saved ? "CLr " : "ERR ");
  bootReadyDisplayed = false;
  Serial.println(saved ? "[SCORE] Saved high score cleared"
                       : "[SCORE] Could not clear saved high score");
}

void recordQuickTap(uint32_t now, uint32_t pressDurationMs) {
  if (pressDurationMs > QUICK_TAP_MAX_MS) {
    quickTapCount = 0;
    lastQuickTapMs = 0;
    return;
  }

  if (quickTapCount == 0 ||
      (uint32_t)(now - lastQuickTapMs) > QUICK_TAP_GAP_MS) {
    quickTapCount = 1;
  } else {
    ++quickTapCount;
  }
  lastQuickTapMs = now;
  Serial.printf("[SCORE] Reset tap %u/%u\n", quickTapCount, HIGH_SCORE_RESET_TAPS);

  if (quickTapCount >= HIGH_SCORE_RESET_TAPS) clearSavedHighScore();
}

void showHighScoreSmall() {
  char text[5];
  snprintf(text, sizeof(text), "Hi%02u", (unsigned)highScore);
  showText4(text);
}

void showHighScoreNumber() {
  uint16_t displayScore = highScore > 9999 ? 9999 : highScore;
  display.showNumberDec(displayScore, false);
}

void updateIdleDisplay() {
  uint32_t now = millis();
  if (hostBlocksStarts()) {
    uint32_t displayedSeconds = hostRemainingSeconds > 5999 ? 5999 : hostRemainingSeconds;
    if (!hostCountdownShown || hostCountdownLastSeconds != displayedSeconds ||
        hostCountdownLastStatus != hostStatus) {
      if (hostStatus == HOST_STATUS_COUNTDOWN) showText4("WAIT");
      else showMinutesSeconds(displayedSeconds);
      hostCountdownLastSeconds = displayedSeconds;
      hostCountdownLastStatus = hostStatus;
      hostCountdownShown = true;
    }
    hostCountdownWasActive = true;
    return;
  }
  hostCountdownShown = false;

  if (hostCountdownWasActive) {
    hostCountdownWasActive = false;
    bootReadyDisplayed = false;
    highScoreSmallShown = false;
    highScoreLabelPhase = true;
    nextHighScoreDisplayMs = now;

    if (scoreResetFeedbackUntilMs != 0 &&
        (int32_t)(now - scoreResetFeedbackUntilMs) < 0) {
      showText4(scoreResetSaved ? "CLr " : "ERR ");
    } else if (bootReadyDisplayActive) {
      showText4("RDY ");
      bootReadyDisplayed = true;
    } else if (lastScoreDisplayUntilMs != 0 &&
               (int32_t)(now - lastScoreDisplayUntilMs) < 0) {
      showScore(lastScore);
    }
  }

  if (scoreResetFeedbackUntilMs != 0) {
    if ((int32_t)(now - scoreResetFeedbackUntilMs) < 0) return;
    scoreResetFeedbackUntilMs = 0;
    highScoreLabelPhase = true;
    nextHighScoreDisplayMs = now;
    highScoreSmallShown = false;
  }

  if (bootReadyDisplayActive) {
    if (!bootReadyDisplayed) {
      showText4("RDY ");
      bootReadyDisplayed = true;
    }
    return;
  }

  if (lastScoreDisplayUntilMs != 0) {
    if ((int32_t)(now - lastScoreDisplayUntilMs) < 0) return;
    lastScoreDisplayUntilMs = 0;
    highScoreLabelPhase = true;
    nextHighScoreDisplayMs = now;
    highScoreSmallShown = false;
  }

  if (highScore <= 99) {
    if (!highScoreSmallShown) {
      showHighScoreSmall();
      highScoreSmallShown = true;
    }
    return;
  }

  if ((int32_t)(now - nextHighScoreDisplayMs) < 0) return;
  if (highScoreLabelPhase) showText4("Hi  ");
  else showHighScoreNumber();
  highScoreLabelPhase = !highScoreLabelPhase;
  nextHighScoreDisplayMs = now + HIGH_SCORE_ALTERNATE_MS;
}

int remainingTargetSeconds() {
  if (gameState != PLAYING || currentTarget < 0) return 0;
  if ((int32_t)(millis() - targetDeadlineMs) >= 0) return 0;
  uint32_t remaining = targetDeadlineMs - millis();
  return (int)((remaining + 999) / 1000);
}

void updateDifficultyForElapsed() {
  if (gameState != PLAYING) return;

  uint32_t elapsedMs = millis() - gameStartedMs;
  uint32_t intervals = elapsedMs / DIFFICULTY_INTERVAL_MS;
  if (intervals <= difficultyIntervalsApplied) return;

  difficultyIntervalsApplied = intervals;
  uint32_t totalReductionMs = intervals * WINDOW_STEP_MS;
  if (totalReductionMs >= INITIAL_WINDOW_MS) {
    currentWindowMs = MIN_WINDOW_MS;
  } else {
    currentWindowMs = INITIAL_WINDOW_MS - totalReductionMs;
    if (currentWindowMs < MIN_WINDOW_MS) currentWindowMs = MIN_WINDOW_MS;
  }

  Serial.printf("[DIFFICULTY] %lu s elapsed; next window=%lu ms\n",
                (unsigned long)(elapsedMs / 1000),
                (unsigned long)currentWindowMs);
}

int chooseNextTarget() {
  int candidates[EXPECTED_SPOKES];
  int candidateCount = 0;
  for (int i = 0; i < EXPECTED_SPOKES; ++i) {
    if (targetSpokeAlive(i) && i != lastSelectedTarget) candidates[candidateCount++] = i;
  }
  if (candidateCount == 0) return -1;
  return candidates[esp_random() % candidateCount];
}

void sendCurrentCue() {
  if (currentTarget < 0 || !spokes[currentTarget].used) return;
  char macText[13];
  macToHex(spokes[currentTarget].mac, macText, sizeof(macText));
  char message[64];
  snprintf(message, sizeof(message), "CUE:%u:%lu:%lu:%s:%lu",
           PROTOCOL_VERSION, (unsigned long)runId,
           (unsigned long)cueSequence, macText,
           (unsigned long)activeTargetWindowMs);
  sendBroadcast(message);
  nextCueResendMs = millis() + CUE_RESEND_MS;
}

bool selectAndCueNextTarget() {
  // Difficulty is based on elapsed game time, not on the number of hits.
  // If a threshold passed while a target was active, apply it to the next
  // target rather than shortening the already-running target unexpectedly.
  updateDifficultyForElapsed();
  int next = chooseNextTarget();
  if (next < 0) return false;
  currentTarget = next;
  lastSelectedTarget = next;
  ++cueSequence;
  activeTargetWindowMs = currentWindowMs;
  targetReady = false;
  targetDeadlineMs = 0;
  cueReadyDeadlineMs = millis() + CUE_READY_TIMEOUT_MS;
  targetLostSinceMs = 0;
  if (goDisplayUntilMs == 0 || (int32_t)(millis() - goDisplayUntilMs) >= 0) {
    showText4("WAIT");
  }
  sendCurrentCue();

  char id[13]; macToHex(spokes[currentTarget].mac, id, sizeof(id));
  Serial.printf("[CUE] %s, window %lu ms, score %u\n",
                id, (unsigned long)activeTargetWindowMs, score);
  return true;
}

void cancelCurrentCue() {
  if (currentTarget < 0 || !spokes[currentTarget].used) return;
  char macText[13];
  macToHex(spokes[currentTarget].mac, macText, sizeof(macText));
  char message[56];
  snprintf(message, sizeof(message), "CANCEL:%u:%lu:%lu:%s",
           PROTOCOL_VERSION, (unsigned long)runId,
           (unsigned long)cueSequence, macText);
  sendBroadcastTwice(message);
}

bool replaceCurrentTarget(const char* reason) {
  if (gameState != PLAYING || currentTarget < 0) return false;
  char oldId[13];
  macToHex(spokes[currentTarget].mac, oldId, sizeof(oldId));
  Serial.printf("[CUE] Replacing target %s: %s\n", oldId, reason);
  cancelCurrentCue();
  currentTarget = -1;
  targetReady = false;
  targetDeadlineMs = 0;
  cueReadyDeadlineMs = 0;
  targetLostSinceMs = 0;
  if (!selectAndCueNextTarget()) {
    finishGame("no live replacement target");
    return false;
  }
  return true;
}

void sendEndMessage() {
  char message[40];
  snprintf(message, sizeof(message), "END:%u:%lu:%u",
           PROTOCOL_VERSION, (unsigned long)runId, score);
  sendBroadcast(message);
}

void finishGame(const char* reason) {
  if (gameState != PLAYING) return;
  Serial.printf("[GAME] Over: %s; score=%u\n", reason, score);
  if (score > highScore) {
    highScore = score;
    if (saveHighScore()) Serial.printf("[SCORE] New high score saved: %u\n", highScore);
    else Serial.printf("[SCORE] New high score %u is RAM-only; storage write failed\n", highScore);
  }
  gameState = ENDING;
  currentTarget = -1;
  targetReady = false;
  targetDeadlineMs = 0;
  cueReadyDeadlineMs = 0;
  haveScore = true;
  lastScore = score;
  endingUntilMs = millis() + END_FLASH_MS;
  nextEndMessageMs = millis();
  endDisplayStep = 0;
  nextEndDisplayMs = millis();
  lastScoreDisplayUntilMs = 0;
  highScoreSmallShown = false;
  solidLED(true, false, false);
  showText4("GAME");
}

void showNodeCountError(int count) {
  char text[5];
  if (count < 0) count = 0;
  if (count > 999) count = 999;
  snprintf(text, sizeof(text), "N%03d", count);
  showText4(text);
}

void sendRunMessage() {
  char message[48];
  snprintf(message, sizeof(message), "RUN:%u:%lu:%lu",
           PROTOCOL_VERSION, (unsigned long)runId,
           (unsigned long)discoveryToken);
  sendBroadcastTwice(message);
}

void sendPrepMessage(char color) {
  char message[40];
  snprintf(message, sizeof(message), "PREP:%u:%lu:%c",
           PROTOCOL_VERSION, (unsigned long)runId, color);
  sendBroadcastTwice(message);
}

void startDiscovery() {
  sendGarageState("-", GARAGE_STATUS_NONE);
  gameState = DISCOVERING;
  bootReadyDisplayActive = false;
  bootReadyDisplayed = false;
  scoreResetFeedbackUntilMs = 0;
  quickTapCount = 0;
  lastQuickTapMs = 0;
  discoveryToken = esp_random();
  if (discoveryToken == 0) discoveryToken = 1;
  uint32_t now = millis();
  discoveryUntilMs = now + DISCOVERY_MS;
  nextDiscoveryPingMs = now;
  masterStartTriggered = true;
  startLEDBlink(true, true, false, 250);
  showText4("FIND");
  Serial.printf("[START] Discovering spokes (token %lu)...\n",
                (unsigned long)discoveryToken);
}

void beginCountdown() {
  int live = discoveredSpokeCount(discoveryToken);
  Serial.printf("[NODES] %d/%d answered this discovery\n", live, EXPECTED_SPOKES);
  if (live < MIN_SPOKES_TO_START) {
    Serial.printf("[START] Refused: only %d live; need %d.\n", live, MIN_SPOKES_TO_START);
    errorWasNoButtons = live == 0;
    gameState = ERROR_STATE;
    errorUntilMs = millis() + 3000;
    startLEDBlink(true, false, false, 250, 3000);
    showNodeCountError(live);
    char abortMessage[48];
    snprintf(abortMessage, sizeof(abortMessage), "ABORT:%u:%lu",
             PROTOCOL_VERSION, (unsigned long)discoveryToken);
    sendBroadcastTwice(abortMessage);
    return;
  }

  ++runId;
  if (runId == 0) ++runId;
  cueSequence = 0;
  score = 0;
  currentTarget = -1;
  lastSelectedTarget = -1;
  currentWindowMs = INITIAL_WINDOW_MS;
  activeTargetWindowMs = INITIAL_WINDOW_MS;
  gameStartedMs = 0;
  difficultyIntervalsApplied = 0;
  goDisplayUntilMs = 0;
  lastScoreDisplayUntilMs = 0;
  highScoreSmallShown = false;

  gameState = COUNTDOWN;
  countdownStep = 0;
  nextCountdownStepMs = millis() + PREP_PHASE_MS;
  sendRunMessage();

  startLEDBlink(true, false, false, 150, PREP_PHASE_MS);
  sendPrepMessage('R');
  display.showNumberDec(3, false);
  Serial.printf("[START] Run %lu countdown\n", (unsigned long)runId);
}

void beginPlaying() {
  gameState = PLAYING;
  gameStartedMs = millis();
  difficultyIntervalsApplied = 0;
  currentWindowMs = INITIAL_WINDOW_MS;
  lastRemainingShown = -1;
  solidLED(false, true, false);
  goDisplayUntilMs = millis() + GO_PHASE_MS;
  sendRunMessage();
  char goMessage[32];
  snprintf(goMessage, sizeof(goMessage), "GO:%u:%lu",
           PROTOCOL_VERSION, (unsigned long)runId);
  sendBroadcastTwice(goMessage);
  showText4(" GO ");
  if (!selectAndCueNextTarget()) finishGame("no live target");
}

void updateDiscovery() {
  uint32_t now = millis();
  if ((int32_t)(now - nextDiscoveryPingMs) >= 0) {
    char message[40];
    snprintf(message, sizeof(message), "DISCOVER:%u:%lu",
             PROTOCOL_VERSION, (unsigned long)discoveryToken);
    sendBroadcast(message);
    nextDiscoveryPingMs = now + DISCOVERY_PING_MS;
  }
  if ((int32_t)(now - discoveryUntilMs) >= 0) beginCountdown();
}

void updateCountdown() {
  uint32_t now = millis();
  if ((int32_t)(now - nextCountdownStepMs) < 0) return;
  ++countdownStep;
  if (countdownStep == 1) {
    sendRunMessage();
    startLEDBlink(true, true, false, 150, PREP_PHASE_MS);
    sendPrepMessage('Y');
    display.showNumberDec(2, false);
    nextCountdownStepMs = now + PREP_PHASE_MS;
  } else if (countdownStep == 2) {
    sendRunMessage();
    startLEDBlink(false, true, false, 150, PREP_PHASE_MS);
    sendPrepMessage('G');
    display.showNumberDec(1, false);
    nextCountdownStepMs = now + PREP_PHASE_MS;
  } else if (countdownStep == 3) {
    beginPlaying();
  }
}

void updatePlaying() {
  uint32_t now = millis();
  updateDifficultyForElapsed();
  if (!targetReady && (int32_t)(now - nextCueResendMs) >= 0) sendCurrentCue();

  if (currentTarget < 0) {
    finishGame("no live target");
    return;
  }

  // A target that stops heartbeating is treated as a hardware/radio failure,
  // not as a player miss. Give the radio a grace period, then replace it.
  if (!targetSpokeAlive(currentTarget)) {
    if (targetLostSinceMs == 0) targetLostSinceMs = now;
    if ((uint32_t)(now - targetLostSinceMs) >= TARGET_LOST_GRACE_MS) {
      replaceCurrentTarget("heartbeat timeout");
      return;
    }
  } else {
    targetLostSinceMs = 0;
  }

  if (!targetReady) {
    if ((int32_t)(now - cueReadyDeadlineMs) >= 0) {
      replaceCurrentTarget("no READY acknowledgement");
    }
    return;
  }

  // The receive callback timestamps packets before queueing them. This tiny
  // processing grace lets a hit that physically arrived before the deadline
  // be handled even if the main loop was briefly busy at that exact instant.
  if ((int32_t)(now - (targetDeadlineMs + TARGET_PACKET_GRACE_MS)) >= 0) {
    if (!targetSpokeAlive(currentTarget)) {
      replaceCurrentTarget("target disappeared at timeout");
    } else {
      finishGame("timeout");
    }
    return;
  }

  int remaining = remainingTargetSeconds();
  if ((goDisplayUntilMs == 0 || (int32_t)(now - goDisplayUntilMs) >= 0) &&
      remaining != lastRemainingShown) {
    goDisplayUntilMs = 0;
    showRemainingSeconds((uint16_t)remaining);
    lastRemainingShown = remaining;
  }
}

void updateEnding() {
  uint32_t now = millis();
  // Keep repeating END throughout the entire solid-red period. A spoke that
  // missed the first broadcast must still be able to leave target mode.
  if ((int32_t)(now - endingUntilMs) < 0 &&
      (int32_t)(now - nextEndMessageMs) >= 0) {
    sendEndMessage();
    nextEndMessageMs = now + END_MESSAGE_RESEND_MS;
  }

  if ((int32_t)(now - nextEndDisplayMs) >= 0) {
    if (endDisplayStep < 6) {
      showText4((endDisplayStep % 2 == 0) ? "GAME" : "OVER");
      ++endDisplayStep;
      nextEndDisplayMs = now + 500;
    } else {
      showScore(lastScore);
      if (lastScoreDisplayUntilMs == 0) {
        lastScoreDisplayUntilMs = now + LAST_SCORE_DISPLAY_MS;
        highScoreSmallShown = false;
      }
      nextEndDisplayMs = now + 100000;
    }
  }

  if ((int32_t)(now - endingUntilMs) >= 0) {
    stopLEDPattern();
    gameState = IDLE;
    showScore(lastScore);
    if (lastScoreDisplayUntilMs == 0) {
      lastScoreDisplayUntilMs = now + LAST_SCORE_DISPLAY_MS;
      highScoreSmallShown = false;
    }
    Serial.println("[GAME] Idle; final score remains displayed.");
  }
}

void updateError() {
  if ((int32_t)(millis() - errorUntilMs) < 0) return;
  stopLEDPattern();
  gameState = IDLE;
  if (haveScore) showScore(lastScore); else display.clear();
}

void handleHit(const RxPacket& packet) {
  if (gameState != PLAYING || currentTarget < 0 || !targetReady) return;

  unsigned int version = 0;
  unsigned long packetRun = 0, packetCue = 0, pressElapsedMs = 0;
  char macText[13] = {};
  if (sscanf(packet.data, "HIT:%u:%lu:%lu:%12[^:]:%lu",
             &version, &packetRun, &packetCue, macText,
             &pressElapsedMs) != 5) return;
  if (version != PROTOCOL_VERSION) return;
  if (packetRun != runId || packetCue != cueSequence) return;
  if (!macEqual(packet.source, spokes[currentTarget].mac)) return;

  uint8_t claimedMac[6];
  if (!hexToMac(macText, claimedMac) || !macEqual(claimedMac, spokes[currentTarget].mac)) return;
  if (pressElapsedMs >= activeTargetWindowMs) return;
  if ((int32_t)(packet.receivedAtMs -
                (targetDeadlineMs + TARGET_PACKET_GRACE_MS)) >= 0) return;
  spokes[currentTarget].lastSeenMs = packet.receivedAtMs;

  char confirm[40];
  snprintf(confirm, sizeof(confirm), "HIT_OK:%u:%lu:%lu",
           PROTOCOL_VERSION, (unsigned long)runId,
           (unsigned long)cueSequence);
  sendBroadcastTwice(confirm);

  ++score;
  currentTarget = -1;
  Serial.printf("[HIT] score=%u; next window=%lu ms\n", score, (unsigned long)currentWindowMs);

  if (!selectAndCueNextTarget()) finishGame("not enough live targets");
}

void handleReady(const RxPacket& packet) {
  if (gameState != PLAYING || currentTarget < 0) return;

  unsigned int version = 0;
  unsigned long packetRun = 0, packetCue = 0, visualElapsedMs = 0;
  char macText[13] = {};
  if (sscanf(packet.data, "READY:%u:%lu:%lu:%12[^:]:%lu",
             &version, &packetRun, &packetCue, macText,
             &visualElapsedMs) != 5) return;
  if (version != PROTOCOL_VERSION) return;
  if (packetRun != runId || packetCue != cueSequence) return;
  if (!macEqual(packet.source, spokes[currentTarget].mac)) return;

  uint8_t claimedMac[6];
  if (!hexToMac(macText, claimedMac) || !macEqual(claimedMac, spokes[currentTarget].mac)) return;

  // The master keeps resending the cue for reliability. Duplicate READY
  // packets must not restart or extend the player's timer.
  spokes[currentTarget].lastSeenMs = packet.receivedAtMs;
  if (targetReady) return;

  targetReady = true;
  uint32_t elapsedMs = (uint32_t)visualElapsedMs;
  if (elapsedMs > activeTargetWindowMs) elapsedMs = activeTargetWindowMs;
  targetDeadlineMs = packet.receivedAtMs + (activeTargetWindowMs - elapsedMs);
  cueReadyDeadlineMs = 0;
  targetLostSinceMs = 0;
  lastRemainingShown = -1;
  if (goDisplayUntilMs == 0 || (int32_t)(millis() - goDisplayUntilMs) >= 0) {
    goDisplayUntilMs = 0;
    showRemainingSeconds((uint16_t)remainingTargetSeconds());
  }
  char id[13]; macToHex(spokes[currentTarget].mac, id, sizeof(id));
  Serial.printf("[READY] %s; visual already active %lu ms, %lu ms remain\n",
                id, (unsigned long)elapsedMs,
                (unsigned long)(activeTargetWindowMs - elapsedMs));
}

bool parseGaragePressPacket(const RxPacket& packet, char* parsedToken,
                            uint32_t& sequence) {
  static const char prefix[] = "GPRESS:3:";
  const size_t prefixLength = sizeof(prefix) - 1;
  if (!parsedToken || packet.len <= prefixLength ||
      memchr(packet.data, '\0', packet.len) != nullptr ||
      memcmp(packet.data, prefix, prefixLength) != 0) return false;

  // Spoke packets use colon separators. Keep this parser length-bounded and
  // separate from readProtocolToken(), which handles host serial's spaces.
  const char* tokenStart = packet.data + prefixLength;
  const char* packetEnd = packet.data + packet.len;
  const char* tokenEnd = static_cast<const char*>(
      memchr(tokenStart, ':', (size_t)(packetEnd - tokenStart)));
  if (!tokenEnd || tokenEnd - tokenStart != 16) return false;

  char packetToken[17];
  memcpy(packetToken, tokenStart, 16);
  packetToken[16] = '\0';
  if (!parseGarageToken(packetToken, parsedToken) ||
      strcmp(parsedToken, "-") == 0) return false;

  const char* sequenceStart = tokenEnd + 1;
  size_t sequenceLength = (size_t)(packetEnd - sequenceStart);
  if (sequenceLength == 0 || sequenceLength >= 11) return false;
  char sequenceText[11];
  memcpy(sequenceText, sequenceStart, sequenceLength);
  sequenceText[sequenceLength] = '\0';
  return parseUint32Token(sequenceText, sequence) && sequence != 0;
}

void handleGaragePress(const RxPacket& packet) {
  char parsedToken[17];
  uint32_t sequence = 0;
  if (!parseGaragePressPacket(packet, parsedToken, sequence) ||
      !validStationMac(packet.source) || memcmp(packet.source, masterMac, 6) == 0) return;

  uint32_t now = millis();
  if (gameState != IDLE || garageStatus != GARAGE_STATUS_ACTIVE ||
      hostStatus != HOST_STATUS_ACTIVE || !hostStatusFresh(now) ||
      !garageStatusFresh(now) || strcmp(parsedToken, garageToken) != 0 ||
      (int32_t)(now - packet.receivedAtMs) < 0 ||
      (uint32_t)(now - packet.receivedAtMs) > HOST_STATUS_STALE_MS) return;

  char macText[13];
  macToHex(packet.source, macText, sizeof(macText));
  Serial.printf("GG1 PRESS %lu %s %s %lu\n", (unsigned long)bootToken,
                parsedToken, macText, (unsigned long)sequence);
}

void processRx() {
  RxPacket packet;
  while (rxQueue && xQueueReceive(rxQueue, &packet, 0) == pdTRUE) {
    if (strncmp(packet.data, "GPRESS:", 7) == 0) {
      handleGaragePress(packet);
      continue;
    }
    unsigned int version = 0;
    unsigned long packetToken = 0;
    unsigned long packetRun = 0;

    int helloLength = 0;
    if (sscanf(packet.data, "HELLO:%u:%lu%n", &version, &packetToken,
               &helloLength) == 2 && helloLength > 0 &&
        packet.data[helloLength] == '\0') {
      if (version == PROTOCOL_VERSION && hostScanActive &&
          packetToken == hostScanToken && hostScanSafe(millis()) &&
          hostScanWindowContains(packet.receivedAtMs)) {
        recordHostScanResponder(packet.source);
      } else if (version == PROTOCOL_VERSION && gameState == DISCOVERING &&
                 packetToken == discoveryToken) {
        bool newlyAdded = false;
        int nodeIndex = upsertSpoke(packet.source, newlyAdded);
        if (nodeIndex >= 0) {
          spokes[nodeIndex].lastSeenMs = packet.receivedAtMs;
          spokes[nodeIndex].seenDiscoveryToken = discoveryToken;
        }
      }
      continue;
    }

    if (sscanf(packet.data, "HB:%u:%lu", &version, &packetRun) == 2) {
      if (version == PROTOCOL_VERSION && packetRun == runId &&
          (gameState == COUNTDOWN || gameState == PLAYING)) {
        int nodeIndex = findSpoke(packet.source);
        if (nodeIndex >= 0 &&
            spokes[nodeIndex].seenDiscoveryToken == discoveryToken) {
          spokes[nodeIndex].lastSeenMs = packet.receivedAtMs;
        }
      }
      continue;
    }

    if (strncmp(packet.data, "READY:", 6) == 0) handleReady(packet);
    if (strncmp(packet.data, "HIT:", 4) == 0) handleHit(packet);
  }
}

void updateRadioDiagnostics() {
  uint32_t now = millis();
  if ((uint32_t)(now - lastRadioDiagnosticMs) < 1000) return;
  lastRadioDiagnosticMs = now;

  uint32_t drops = rxQueueDropCount;
  uint32_t failures = sendCallbackFailureCount;
  if (drops != lastReportedRxDrops) {
    Serial.printf("[RADIO] RX queue drops: %lu (+%lu)\n",
                  (unsigned long)drops,
                  (unsigned long)(drops - lastReportedRxDrops));
    lastReportedRxDrops = drops;
  }
  if (failures != lastReportedSendFailures) {
    Serial.printf("[RADIO] Delivery callback failures: %lu (+%lu)\n",
                  (unsigned long)failures,
                  (unsigned long)(failures - lastReportedSendFailures));
    lastReportedSendFailures = failures;
  }
}

void updateHoldIndicator(uint32_t now) {
  uint32_t elapsedMs = now - masterHoldStartedMs;
  if (elapsedMs > MASTER_HOLD_MS) elapsedMs = MASTER_HOLD_MS;

  // Start yellow (red + green), then fade the red channel out to become
  // green as the five-second hold completes.
  uint8_t redLevel = (uint8_t)(255UL -
                               ((255UL * elapsedMs) / MASTER_HOLD_MS));
  setLEDLevels(redLevel, 255, 0);
}

void updateMasterButton() {
  bool reading = digitalRead(BTN_PIN) == LOW;
  uint32_t now = millis();
  if (reading != lastButtonRead) {
    lastButtonRead = reading;
    lastButtonChangeMs = now;
  }
  if ((uint32_t)(now - lastButtonChangeMs) < DEBOUNCE_MS || reading == stableButton) return;
  stableButton = reading;

  // Host countdowns and spoke scans temporarily own idle control. Ignore all
  // button gestures so they cannot start a game or alter the local high score.
  if (gameState == IDLE &&
      (hostStatus == HOST_STATUS_COUNTDOWN || hostScanActive)) {
    quickTapCount = 0;
    lastQuickTapMs = 0;
    masterHoldActive = false;
    if (reading) {
      masterStartTriggered = true;
      masterStartArmed = false;
    } else {
      masterStartArmed = true;
    }
    return;
  }

  if (reading) {
    // A press that began during a game or ending animation cannot carry over
    // and start a later game. The button must be released and pressed again
    // while the master is idle.
    if (gameState == IDLE && masterStartArmed) {
      masterHoldStartedMs = now;
      masterHoldActive = true;
      masterStartTriggered = false;
      solidLED(true, true, false);
      updateHoldIndicator(now);
    } else {
      masterHoldActive = false;
    }
  } else {
    bool completedIdlePress = gameState == IDLE && masterHoldActive;
    uint32_t pressDurationMs = now - masterHoldStartedMs;
    masterHoldActive = false;
    masterStartArmed = true;
    if (gameState == IDLE) {
      stopLEDPattern();
      if (completedIdlePress) {
        if (!masterStartTriggered) {
          if (pressDurationMs >= MASTER_HOLD_MS) {
            masterStartTriggered = true;
            quickTapCount = 0;
            lastQuickTapMs = 0;
            if (!hostBlocksStarts()) startDiscovery();
          } else if (!hostBlocksStarts()) {
            emitHostStart();
          }
        }
        if (gameState == IDLE) {
          recordQuickTap(now, pressDurationMs);
        } else {
          quickTapCount = 0;
          lastQuickTapMs = 0;
        }
      }
    } else {
      quickTapCount = 0;
      lastQuickTapMs = 0;
    }
  }
}

void checkMasterHold() {
  if (!masterHoldActive || masterStartTriggered || !masterStartArmed || gameState != IDLE) return;
  uint32_t now = millis();
  updateHoldIndicator(now);
  if ((uint32_t)(now - masterHoldStartedMs) >= MASTER_HOLD_MS) {
    masterStartTriggered = true;
    masterStartArmed = false;
    quickTapCount = 0;
    lastQuickTapMs = 0;
    if (hostBlocksStarts()) return;
    startDiscovery();
  }
}

// --------------------------- Setup / loop ---------------------------
void restartAfterRadioFailure(const char* stage, esp_err_t error) {
  radioReady = false;
  Serial.printf("[FATAL] %s failed (%d); restarting in 5 seconds\n",
                stage, (int)error);
  showText4("RAD ");
  startLEDBlink(true, false, true, 250);
  uint32_t restartAtMs = millis() + 5000;
  while ((int32_t)(millis() - restartAtMs) < 0) {
    updateLED();
    delay(10);
  }
  ESP.restart();
  while (true) delay(1000);
}

void setup() {
  Serial.begin(115200);
  delay(150);
  bootToken = esp_random();
  if (bootToken == 0) bootToken = 1;
  emitHostHello();

  pinMode(BTN_PIN, INPUT_PULLUP);
  pinMode(LED_R, OUTPUT);
  pinMode(LED_G, OUTPUT);
  pinMode(LED_B, OUTPUT);
  stopLEDPattern();

  display.setBrightness(7);
  display.clear();
  loadHighScore();

  if (!WiFi.mode(WIFI_STA)) restartAfterRadioFailure("Wi-Fi station mode", ESP_FAIL);
  WiFi.disconnect(false, false);
  WiFi.setSleep(false);
  esp_err_t radioResult = esp_wifi_set_ps(WIFI_PS_NONE);
  if (radioResult != ESP_OK) restartAfterRadioFailure("Wi-Fi power-save setup", radioResult);
  radioResult = esp_wifi_set_channel(ESPNOW_CHANNEL, WIFI_SECOND_CHAN_NONE);
  if (radioResult != ESP_OK) restartAfterRadioFailure("Wi-Fi channel setup", radioResult);
  WiFi.macAddress(masterMac);

  // Fresh boot/session token: if the master resets, spokes will not mistake
  // the next game for an old run whose cue sequence has already advanced.
  runId = esp_random();
  if (runId == 0) runId = 1;
  rxQueue = xQueueCreate(64, sizeof(RxPacket));
  if (!rxQueue) restartAfterRadioFailure("RX queue allocation", ESP_ERR_NO_MEM);
  radioResult = esp_now_init();
  if (radioResult != ESP_OK) restartAfterRadioFailure("ESP-NOW initialization", radioResult);
  radioResult = esp_now_register_send_cb(onSent);
  if (radioResult != ESP_OK) restartAfterRadioFailure("send callback registration", radioResult);
  radioResult = esp_now_register_recv_cb(onRecv);
  if (radioResult != ESP_OK) restartAfterRadioFailure("receive callback registration", radioResult);

  esp_now_peer_info_t peer{};
  memcpy(peer.peer_addr, BROADCAST_MAC, 6);
  peer.ifidx = WIFI_IF_STA;
  peer.channel = ESPNOW_CHANNEL;
  peer.encrypt = false;
  radioResult = esp_now_add_peer(&peer);
  if (radioResult != ESP_OK && radioResult != ESP_ERR_ESPNOW_EXIST) {
    restartAfterRadioFailure("broadcast peer setup", radioResult);
  }
  radioReady = true;

  showText4("RDY ");
  bootReadyDisplayed = true;
  Serial.printf("[OK] Master ready; ESP-NOW channel %u; hold button 5 seconds.\n", ESPNOW_CHANNEL);
}

void loop() {
  updateHostSerialInput();
  processRx();
  updateHostScan();
  updatePendingTransmissions();
  updateGarageBroadcast();
  updateRadioDiagnostics();
  updateMasterButton();
  checkMasterHold();

  switch (gameState) {
    case DISCOVERING: updateDiscovery(); break;
    case COUNTDOWN: updateCountdown(); break;
    case PLAYING: updatePlaying(); break;
    case ENDING: updateEnding(); break;
    case ERROR_STATE: updateError(); break;
    case IDLE: updateIdleDisplay(); break;
  }

  updateHostStatusLED();

  if (gameState != IDLE) hostCountdownShown = false;
  updateLED();
  updateHostSerialOutput();
  delay(2);
}
