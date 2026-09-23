/*
  SPEED BUTTON - SPOKE
  --------------------
  Standalone companion sketch for every non-master XIAO ESP32 button.

  Hardware:
    Button : D2 (active LOW)
    RGB LED: D3/D4/D5 (common anode, active LOW)

  Flash this same sketch to all 13 spokes. The master identifies each spoke by
  its ESP-NOW station MAC address, so no per-device ID is needed.

  ESP-NOW is used on a fixed channel with no Wi-Fi association or network.
  A fully deep-sleeping ESP32 cannot hear an arbitrary wireless ESP-NOW packet,
  so idle spokes remain listening. They transmit only when answering discovery
  or while a game is active. If a future
  hardware revision adds a wired wake signal, deep sleep can be added around
  that signal without changing the game protocol.
*/

#include <WiFi.h>
#include <esp_now.h>
#include <esp_wifi.h>
#include <esp_system.h>
#include <string.h>
#include <stdlib.h>

// Keep Arduino's generated function prototypes valid when they reference the
// packet type declared later in this file.
struct RxPacket;

// --------------------------- Configuration ---------------------------
constexpr uint8_t PROTOCOL_VERSION = 3;
constexpr uint8_t ESPNOW_CHANNEL = 1;
constexpr uint32_t DEBOUNCE_MS = 30;
constexpr uint32_t ACTIVE_HEARTBEAT_MS = 800;
constexpr uint32_t HEARTBEAT_JITTER_MS = 300;
constexpr uint32_t DISCOVERY_RESPONSE_MIN_MS = 20;
constexpr uint32_t DISCOVERY_RESPONSE_MAX_MS = 250;
constexpr uint32_t DISCOVERY_REPLY_COOLDOWN_MS = 300;
constexpr uint32_t MASTER_RESERVATION_MS = 7000;
constexpr uint32_t MASTER_SILENCE_RESET_MS = 15000;
constexpr uint32_t PREP_PHASE_MS = 500;
constexpr uint32_t HIT_GREEN_MS = 2000;
constexpr uint32_t TARGET_BLINK_SLOW_MS = 500;
constexpr uint32_t TARGET_BLINK_FAST_MS = 65;
constexpr uint32_t TARGET_COLOR_GREEN_MS = 8000;
constexpr uint32_t TARGET_COLOR_YELLOW_MS = 6000;
constexpr uint32_t TARGET_COLOR_RED_MS = 3000;
constexpr uint32_t TARGET_WINDOW_MAX_MS = 60000;
constexpr uint32_t END_FLASH_MS = 5000;
constexpr uint32_t HIT_RESEND_INTERVAL_MS = 100;
constexpr uint8_t HIT_RESENDS = 5;

#define BTN_PIN D2
#define LED_R   D3
#define LED_G   D4
#define LED_B   D5

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

bool patternBlinking = false;
bool patternOn = false;
bool patternR = false, patternG = false, patternB = false;
uint8_t patternRLevel = 0, patternGLevel = 0, patternBLevel = 0;
uint32_t patternPeriodMs = TARGET_BLINK_SLOW_MS;
uint32_t patternNextToggleMs = 0;
uint32_t patternUntilMs = 0;  // 0 = no expiry

void stopPattern() {
  patternBlinking = false;
  patternOn = false;
  patternUntilMs = 0;
  setLED(false, false, false);
}

void setSolid(bool r, bool g, bool b, uint32_t durationMs = 0) {
  patternBlinking = false;
  patternUntilMs = durationMs == 0 ? 0 : millis() + durationMs;
  setLED(r, g, b);
}

void startBlink(bool r, bool g, bool b, uint32_t periodMs, uint32_t durationMs = 0) {
  patternR = r;
  patternG = g;
  patternB = b;
  patternRLevel = r ? 255 : 0;
  patternGLevel = g ? 255 : 0;
  patternBLevel = b ? 255 : 0;
  patternPeriodMs = periodMs;
  patternBlinking = true;
  patternOn = false;
  patternNextToggleMs = millis();
  patternUntilMs = durationMs == 0 ? 0 : millis() + durationMs;
  setLED(false, false, false);
}

void updatePattern() {
  uint32_t now = millis();
  if (patternUntilMs != 0 && (int32_t)(now - patternUntilMs) >= 0) {
    stopPattern();
    return;
  }
  if (!patternBlinking || (int32_t)(now - patternNextToggleMs) < 0) return;
  patternOn = !patternOn;
  patternNextToggleMs = now + patternPeriodMs;
  setLEDLevels(patternOn ? patternRLevel : 0,
               patternOn ? patternGLevel : 0,
               patternOn ? patternBLevel : 0);
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
uint8_t ownMac[6] = {};
bool radioReady = false;
volatile uint32_t rxQueueDropCount = 0;
volatile uint32_t sendCallbackFailureCount = 0;
bool helloPending = false;
uint32_t helloDueMs = 0;
uint32_t pendingHelloToken = 0;
uint32_t lastHelloToken = 0;
uint32_t lastHelloSentMs = 0;

void onSent(const wifi_tx_info_t*, esp_now_send_status_t status) {
  if (status != ESP_NOW_SEND_SUCCESS) ++sendCallbackFailureCount;
}

void onRecv(const esp_now_recv_info* info, const uint8_t* data, int len) {
  if (!info || !data || len <= 0 || !rxQueue) return;
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

bool sendHello(uint32_t token) {
  char message[40];
  snprintf(message, sizeof(message), "HELLO:%u:%lu",
           PROTOCOL_VERSION, (unsigned long)token);
  return sendBroadcast(message);
}

void scheduleHello(uint32_t token, uint32_t minDelayMs, uint32_t maxDelayMs) {
  uint32_t now = millis();
  if (helloPending && pendingHelloToken == token) return;
  if (lastHelloToken == token &&
      (uint32_t)(now - lastHelloSentMs) < DISCOVERY_REPLY_COOLDOWN_MS) return;
  if (maxDelayMs < minDelayMs) maxDelayMs = minDelayMs;
  uint32_t span = maxDelayMs - minDelayMs;
  helloPending = true;
  pendingHelloToken = token;
  helloDueMs = now + minDelayMs + (span == 0 ? 0 : esp_random() % (span + 1));
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

bool validStationMac(const uint8_t* mac) {
  bool allZero = true;
  for (uint8_t i = 0; i < 6; ++i) {
    if (mac[i] != 0) allZero = false;
  }
  return !allZero && (mac[0] & 0x01) == 0;
}

// --------------------------- Game state ---------------------------
bool masterReserved = false;
uint8_t reservedMasterMac[6] = {};
uint32_t reservedDiscoveryToken = 0;
uint32_t reservationUntilMs = 0;
uint32_t lastMasterCommandMs = 0;

bool gameActive = false;
uint32_t currentRunId = 0;
uint32_t lastCueSequence = 0;
bool targetActive = false;
uint32_t targetWindowMs = 10000;
uint32_t targetDisplayStartMs = 0;
bool hitPending = false;
uint32_t nextHeartbeatMs = 0;
char pendingHitMessage[64] = {};
uint8_t hitResendsLeft = 0;
uint32_t nextHitResendMs = 0;
uint32_t lastRadioDiagnosticMs = 0;
uint32_t lastReportedRxDrops = 0;
uint32_t lastReportedSendFailures = 0;

bool buttonLastRead = true;
bool buttonStable = true;
uint32_t buttonLastChangeMs = 0;

bool macEqual(const uint8_t* a, const uint8_t* b) {
  return memcmp(a, b, 6) == 0;
}

bool fromReservedMaster(const RxPacket& packet) {
  return masterReserved && macEqual(packet.source, reservedMasterMac);
}

void clearActiveGame(bool clearVisual) {
  gameActive = false;
  currentRunId = 0;
  lastCueSequence = 0;
  targetActive = false;
  targetWindowMs = 10000;
  targetDisplayStartMs = 0;
  hitPending = false;
  pendingHitMessage[0] = '\0';
  hitResendsLeft = 0;
  nextHitResendMs = 0;
  nextHeartbeatMs = 0;
  if (clearVisual) stopPattern();
}

void clearMasterReservation() {
  masterReserved = false;
  memset(reservedMasterMac, 0, sizeof(reservedMasterMac));
  reservedDiscoveryToken = 0;
  reservationUntilMs = 0;
  lastMasterCommandMs = 0;
  helloPending = false;
  pendingHelloToken = 0;
}

bool isOurCue(const char* macText) {
  uint8_t cueMac[6];
  return hexToMac(macText, cueMac) && memcmp(cueMac, ownMac, 6) == 0;
}

void sendReady() {
  char macText[13];
  macToHex(ownMac, macText, sizeof(macText));
  uint32_t elapsedMs = targetActive ? millis() - targetDisplayStartMs : targetWindowMs;
  char message[64];
  snprintf(message, sizeof(message), "READY:%u:%lu:%lu:%s:%lu",
           PROTOCOL_VERSION, (unsigned long)currentRunId,
           (unsigned long)lastCueSequence, macText,
           (unsigned long)elapsedMs);
  sendBroadcast(message);
}

void sendPendingHit() {
  if (pendingHitMessage[0] != '\0') sendBroadcast(pendingHitMessage);
}

void handleDiscover(const RxPacket& packet) {
  unsigned int version = 0;
  unsigned long token = 0;
  if (sscanf(packet.data, "DISCOVER:%u:%lu", &version, &token) != 2 ||
      version != PROTOCOL_VERSION || token == 0) return;

  uint32_t now = packet.receivedAtMs;
  bool sameMaster = masterReserved && macEqual(packet.source, reservedMasterMac);
  bool reservationExpired = masterReserved &&
                            (int32_t)(now - reservationUntilMs) >= 0;
  bool activeMasterSilent = gameActive &&
                            (uint32_t)(now - lastMasterCommandMs) >=
                              MASTER_SILENCE_RESET_MS;

  if (masterReserved && !sameMaster) {
    if ((!gameActive && !reservationExpired) || (gameActive && !activeMasterSilent)) return;
    clearActiveGame(true);
    clearMasterReservation();
  } else if (gameActive && sameMaster) {
    // A new discovery token from the same physical master means it rebooted
    // or deliberately began a fresh session. Recover without a power cycle.
    if ((uint32_t)token == reservedDiscoveryToken) return;
    clearActiveGame(true);
  }

  if (!masterReserved || !macEqual(packet.source, reservedMasterMac) ||
      (uint32_t)token != reservedDiscoveryToken) {
    helloPending = false;
    memcpy(reservedMasterMac, packet.source, 6);
    reservedDiscoveryToken = (uint32_t)token;
  }
  masterReserved = true;
  reservationUntilMs = now + MASTER_RESERVATION_MS;
  lastMasterCommandMs = now;
  scheduleHello(reservedDiscoveryToken,
                DISCOVERY_RESPONSE_MIN_MS, DISCOVERY_RESPONSE_MAX_MS);
}

void handleRun(const RxPacket& packet) {
  unsigned int version = 0;
  unsigned long run = 0, token = 0;
  if (sscanf(packet.data, "RUN:%u:%lu:%lu", &version, &run, &token) != 3 ||
      version != PROTOCOL_VERSION || run == 0 || !fromReservedMaster(packet) ||
      token != reservedDiscoveryToken) return;

  lastMasterCommandMs = packet.receivedAtMs;
  reservationUntilMs = packet.receivedAtMs + MASTER_RESERVATION_MS;
  bool newRun = !gameActive || run != currentRunId;
  if (newRun) {
    clearActiveGame(true);
    currentRunId = (uint32_t)run;
  }
  gameActive = true;
  if (nextHeartbeatMs == 0) nextHeartbeatMs = millis() + (esp_random() % 200);
}

bool validateActiveRun(const RxPacket& packet, unsigned int version,
                       unsigned long run) {
  if (version != PROTOCOL_VERSION || !gameActive || !fromReservedMaster(packet) ||
      run != currentRunId) return false;
  lastMasterCommandMs = packet.receivedAtMs;
  return true;
}

void handlePrep(const RxPacket& packet) {
  unsigned int version = 0;
  unsigned long run = 0;
  char color = '\0';
  if (sscanf(packet.data, "PREP:%u:%lu:%c", &version, &run, &color) != 3 ||
      !validateActiveRun(packet, version, run)) return;
  if (color == 'R') startBlink(true, false, false, 150, PREP_PHASE_MS);
  else if (color == 'Y') startBlink(true, true, false, 150, PREP_PHASE_MS);
  else if (color == 'G') startBlink(false, true, false, 150, PREP_PHASE_MS);
}

void handleGo(const RxPacket& packet) {
  unsigned int version = 0;
  unsigned long run = 0;
  if (sscanf(packet.data, "GO:%u:%lu", &version, &run) != 2 ||
      !validateActiveRun(packet, version, run)) return;
  // GO and CUE are independent broadcasts. Do not erase a cue that arrived
  // first because ESP-NOW delivery order is not guaranteed.
  if (!targetActive && !hitPending) stopPattern();
}

void handleCue(const RxPacket& packet) {
  unsigned int version = 0;
  unsigned long run = 0, sequence = 0;
  unsigned long windowMs = 0;
  char macText[13] = {};
  if (sscanf(packet.data, "CUE:%u:%lu:%lu:%12[^:]:%lu",
             &version, &run, &sequence, macText, &windowMs) != 5 ||
      !validateActiveRun(packet, version, run)) return;
  if (windowMs == 0 || windowMs > TARGET_WINDOW_MAX_MS) return;
  if (!isOurCue(macText)) return;

  if (sequence < lastCueSequence) return;
  if (sequence == lastCueSequence) {
    // Repeated cues double as application-level acknowledgement retries.
    // Never re-arm a button after it has already been pressed for this cue.
    if (hitPending) sendPendingHit();
    else if (targetActive) sendReady();
    return;
  }

  lastCueSequence = (uint32_t)sequence;
  targetActive = true;
  targetWindowMs = (uint32_t)windowMs;
  targetDisplayStartMs = millis();
  hitPending = false;
  pendingHitMessage[0] = '\0';
  hitResendsLeft = 0;
  startBlink(false, true, false, TARGET_BLINK_SLOW_MS);
  sendReady();
}

void updateTargetVisual() {
  if (!targetActive || targetWindowMs == 0) return;

  uint32_t now = millis();
  uint32_t elapsedMs = now - targetDisplayStartMs;
  uint32_t remainingMs = elapsedMs >= targetWindowMs ? 0 : targetWindowMs - elapsedMs;

  uint8_t newRLevel = 255;
  uint8_t newGLevel = 0;
  if (remainingMs >= TARGET_COLOR_GREEN_MS) {
    // Green: plenty of time remains.
    newRLevel = 0;
    newGLevel = 255;
  } else if (remainingMs >= TARGET_COLOR_YELLOW_MS) {
    // Blend green into yellow by adding red from 0 to 255.
    uint32_t progressMs = TARGET_COLOR_GREEN_MS - remainingMs;
    uint32_t spanMs = TARGET_COLOR_GREEN_MS - TARGET_COLOR_YELLOW_MS;
    newRLevel = (uint8_t)((255UL * progressMs) / spanMs);
    newGLevel = 255;
  } else if (remainingMs >= TARGET_COLOR_RED_MS) {
    // Blend yellow into red by reducing green from 255 to 0.
    uint32_t progressMs = TARGET_COLOR_YELLOW_MS - remainingMs;
    uint32_t spanMs = TARGET_COLOR_YELLOW_MS - TARGET_COLOR_RED_MS;
    newRLevel = 255;
    newGLevel = (uint8_t)(255UL - ((255UL * progressMs) / spanMs));
  }

  uint32_t blinkTimeMs = remainingMs > TARGET_COLOR_GREEN_MS
                           ? TARGET_COLOR_GREEN_MS
                           : remainingMs;
  uint32_t periodSpan = TARGET_BLINK_SLOW_MS - TARGET_BLINK_FAST_MS;
  uint32_t newPeriod = TARGET_BLINK_FAST_MS +
                       (blinkTimeMs * periodSpan) / TARGET_COLOR_GREEN_MS;
  if (newPeriod < TARGET_BLINK_FAST_MS) newPeriod = TARGET_BLINK_FAST_MS;

  patternR = newRLevel > 0;
  patternG = newGLevel > 0;
  patternB = false;
  patternRLevel = newRLevel;
  patternGLevel = newGLevel;
  patternBLevel = 0;
  if (patternOn) setLEDLevels(patternRLevel, patternGLevel, patternBLevel);
  if (patternPeriodMs != newPeriod) {
    patternPeriodMs = newPeriod;
    if ((int32_t)(patternNextToggleMs - now) > (int32_t)newPeriod) {
      patternNextToggleMs = now + newPeriod;
    }
  }
}

void handleHitOk(const RxPacket& packet) {
  unsigned int version = 0;
  unsigned long run = 0, sequence = 0;
  if (sscanf(packet.data, "HIT_OK:%u:%lu:%lu",
             &version, &run, &sequence) != 3 ||
      !validateActiveRun(packet, version, run) ||
      sequence != lastCueSequence) return;
  hitPending = false;
  hitResendsLeft = 0;
  pendingHitMessage[0] = '\0';
}

void handleCancel(const RxPacket& packet) {
  unsigned int version = 0;
  unsigned long run = 0, sequence = 0;
  char macText[13] = {};
  if (sscanf(packet.data, "CANCEL:%u:%lu:%lu:%12s",
             &version, &run, &sequence, macText) != 4 ||
      !validateActiveRun(packet, version, run)) return;
  if (sequence != lastCueSequence || !isOurCue(macText)) return;
  targetActive = false;
  hitPending = false;
  hitResendsLeft = 0;
  pendingHitMessage[0] = '\0';
  stopPattern();
}

void handleEnd(const RxPacket& packet) {
  unsigned int version = 0;
  unsigned long run = 0;
  unsigned int ignoredScore = 0;
  if (sscanf(packet.data, "END:%u:%lu:%u",
             &version, &run, &ignoredScore) != 3 ||
      !validateActiveRun(packet, version, run)) return;
  clearActiveGame(false);
  clearMasterReservation();
  setSolid(true, false, false, END_FLASH_MS);
}

void handleAbort(const RxPacket& packet) {
  unsigned int version = 0;
  unsigned long token = 0;
  if (sscanf(packet.data, "ABORT:%u:%lu", &version, &token) != 2 ||
      version != PROTOCOL_VERSION || !fromReservedMaster(packet) ||
      token != reservedDiscoveryToken) return;
  clearActiveGame(true);
  clearMasterReservation();
}

void processRx() {
  RxPacket packet;
  while (rxQueue && xQueueReceive(rxQueue, &packet, 0) == pdTRUE) {
    if (strncmp(packet.data, "DISCOVER:", 9) == 0) {
      handleDiscover(packet);
    } else if (strncmp(packet.data, "RUN:", 4) == 0) {
      handleRun(packet);
    } else if (strncmp(packet.data, "PREP:", 5) == 0) {
      handlePrep(packet);
    } else if (strncmp(packet.data, "GO:", 3) == 0) {
      handleGo(packet);
    } else if (strncmp(packet.data, "CUE:", 4) == 0) {
      handleCue(packet);
    } else if (strncmp(packet.data, "HIT_OK:", 7) == 0) {
      handleHitOk(packet);
    } else if (strncmp(packet.data, "CANCEL:", 7) == 0) {
      handleCancel(packet);
    } else if (strncmp(packet.data, "END:", 4) == 0) {
      handleEnd(packet);
    } else if (strncmp(packet.data, "ABORT:", 6) == 0) {
      handleAbort(packet);
    }
  }
}

void handlePress() {
  // Every non-target press is deliberately ignored.
  if (!gameActive || !targetActive) return;

  uint32_t pressElapsedMs = millis() - targetDisplayStartMs;
  if (pressElapsedMs >= targetWindowMs) {
    targetActive = false;
    stopPattern();
    return;
  }

  targetActive = false;
  hitPending = true;
  targetDisplayStartMs = 0;
  setSolid(false, true, false, HIT_GREEN_MS);

  char macText[13];
  macToHex(ownMac, macText, sizeof(macText));
  snprintf(pendingHitMessage, sizeof(pendingHitMessage),
           "HIT:%u:%lu:%lu:%s:%lu", PROTOCOL_VERSION,
           (unsigned long)currentRunId, (unsigned long)lastCueSequence,
           macText, (unsigned long)pressElapsedMs);
  sendBroadcast(pendingHitMessage);
  hitResendsLeft = HIT_RESENDS;
  nextHitResendMs = millis() + HIT_RESEND_INTERVAL_MS;
}

void updateButton() {
  bool reading = digitalRead(BTN_PIN) == LOW;
  uint32_t now = millis();
  if (reading != buttonLastRead) {
    buttonLastRead = reading;
    buttonLastChangeMs = now;
  }
  if ((uint32_t)(now - buttonLastChangeMs) < DEBOUNCE_MS || reading == buttonStable) return;
  buttonStable = reading;
  if (reading) handlePress();
}

void updateHelloResponse() {
  uint32_t now = millis();
  if (!helloPending || (int32_t)(now - helloDueMs) < 0) return;
  if (!masterReserved || pendingHelloToken != reservedDiscoveryToken) {
    helloPending = false;
    return;
  }
  if (!sendHello(pendingHelloToken)) {
    helloDueMs = now + 50;
    return;
  }
  lastHelloToken = pendingHelloToken;
  lastHelloSentMs = now;
  helloPending = false;
}

void updateHeartbeat() {
  if (!gameActive || currentRunId == 0) return;
  uint32_t now = millis();
  if ((int32_t)(now - nextHeartbeatMs) < 0) return;
  char message[32];
  snprintf(message, sizeof(message), "HB:%u:%lu",
           PROTOCOL_VERSION, (unsigned long)currentRunId);
  sendBroadcast(message);
  nextHeartbeatMs = now + ACTIVE_HEARTBEAT_MS +
                    (esp_random() % (HEARTBEAT_JITTER_MS + 1));
}

void updateHitResend() {
  if (hitResendsLeft == 0 || (int32_t)(millis() - nextHitResendMs) < 0) return;
  sendBroadcast(pendingHitMessage);
  --hitResendsLeft;
  nextHitResendMs += HIT_RESEND_INTERVAL_MS;
}

void updateMasterWatchdog() {
  uint32_t now = millis();
  if (gameActive &&
      (uint32_t)(now - lastMasterCommandMs) >= MASTER_SILENCE_RESET_MS) {
    Serial.println("[RECOVERY] Master silent; abandoning stale game session");
    clearActiveGame(true);
    clearMasterReservation();
    return;
  }
  if (!gameActive && masterReserved &&
      (int32_t)(now - reservationUntilMs) >= 0) {
    clearMasterReservation();
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

// --------------------------- Setup / loop ---------------------------
void restartAfterRadioFailure(const char* stage, esp_err_t error) {
  radioReady = false;
  Serial.printf("[FATAL] %s failed (%d); restarting in 5 seconds\n",
                stage, (int)error);
  startBlink(true, false, true, 250);
  uint32_t restartAtMs = millis() + 5000;
  while ((int32_t)(millis() - restartAtMs) < 0) {
    updatePattern();
    delay(10);
  }
  ESP.restart();
  while (true) delay(1000);
}

void setup() {
  Serial.begin(115200);
  delay(150);

  pinMode(BTN_PIN, INPUT_PULLUP);
  pinMode(LED_R, OUTPUT);
  pinMode(LED_G, OUTPUT);
  pinMode(LED_B, OUTPUT);
  stopPattern();

  if (!WiFi.mode(WIFI_STA)) restartAfterRadioFailure("Wi-Fi station mode", ESP_FAIL);
  WiFi.disconnect(false, false);
  WiFi.setSleep(false);
  esp_err_t radioResult = esp_wifi_set_ps(WIFI_PS_NONE);
  if (radioResult != ESP_OK) restartAfterRadioFailure("Wi-Fi power-save setup", radioResult);
  radioResult = esp_wifi_set_channel(ESPNOW_CHANNEL, WIFI_SECOND_CHAN_NONE);
  if (radioResult != ESP_OK) restartAfterRadioFailure("Wi-Fi channel setup", radioResult);
  WiFi.macAddress(ownMac);
  if (!validStationMac(ownMac)) restartAfterRadioFailure("station MAC lookup", ESP_FAIL);

  rxQueue = xQueueCreate(32, sizeof(RxPacket));
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

  char id[13];
  macToHex(ownMac, id, sizeof(id));
  Serial.printf("[OK] Spoke %s ready; ESP-NOW channel %u.\n", id, ESPNOW_CHANNEL);
}

void loop() {
  processRx();
  updateButton();
  updateHelloResponse();
  updateHeartbeat();
  updateHitResend();
  updateMasterWatchdog();
  updateRadioDiagnostics();
  updateTargetVisual();
  updatePattern();
  delay(2);
}
