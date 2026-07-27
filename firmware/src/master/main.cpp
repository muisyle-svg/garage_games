#include <Arduino.h>
#include <ArduinoJson.h>
#include <ArduinoWebsockets.h>
#include <Preferences.h>
#include <TM1637Display.h>
#include <WiFi.h>
#include <WiFiManager.h>
#include <WiFiUdp.h>
#include <esp_now.h>
#include <esp_wifi.h>
#include "Protocol.h"

using namespace websockets;

namespace {
constexpr uint8_t kButtonPin = D2;
constexpr uint8_t kRedPin = D3;
constexpr uint8_t kGreenPin = D4;
constexpr uint8_t kBluePin = D5;
constexpr uint8_t kClockPin = D0;
constexpr uint8_t kDisplayPin = D1;
constexpr uint16_t kControllerPort = 5260;
constexpr uint16_t kDiscoveryPort = 20260;
constexpr uint8_t kBroadcastMac[6] = {0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF};
constexpr uint8_t kOutboxCapacity = 32;

TM1637Display display(kClockPin, kDisplayPin);
WebsocketsClient webSocket;
WiFiUDP udp;
Preferences preferences;
char masterId[gg::kDeviceIdLength]{};
char bootId[gg::kBootIdLength]{};
char activeRunId[gg::kRunIdLength]{};
IPAddress controllerIp;
uint32_t sequence = 0;
uint32_t runStartedAt = 0;
uint32_t timerSeconds = 300;
uint32_t nextBeaconAt = 0;
uint32_t nextConnectAt = 0;
uint32_t nextDisplayAt = 0;
uint32_t radioFailures = 0;
bool controllerConnected = false;
bool runActive = false;
bool runPaused = false;
uint32_t pausedRemaining = 300;
bool buttonReading = HIGH;
bool buttonStable = HIGH;
uint32_t buttonChangedAt = 0;

struct PendingBroadcast {
  bool active = false;
  gg::Frame frame{};
  uint8_t remaining = 0;
  uint32_t nextAt = 0;
} pendingBroadcast;

class PersistentOutbox {
 public:
  void begin() {
    preferences.begin("gg-outbox", false);
    head_ = preferences.getUChar("head", 0);
    tail_ = preferences.getUChar("tail", 0);
    count_ = preferences.getUChar("count", 0);
    if (head_ >= kOutboxCapacity || tail_ >= kOutboxCapacity || count_ > kOutboxCapacity) {
      clear();
    }
  }

  bool enqueue(const gg::Frame& frame) {
    if (contains(frame.eventId)) return true;
    if (count_ >= kOutboxCapacity) return false;
    const String key = slotKey(tail_);
    if (preferences.putBytes(key.c_str(), &frame, sizeof(frame)) != sizeof(frame)) return false;
    tail_ = (tail_ + 1) % kOutboxCapacity;
    ++count_;
    savePointers();
    return true;
  }

  bool peek(gg::Frame& frame) {
    if (count_ == 0) return false;
    return preferences.getBytes(slotKey(head_).c_str(), &frame, sizeof(frame)) == sizeof(frame);
  }

  bool popIf(const char* eventId) {
    gg::Frame frame{};
    if (!peek(frame) || strcmp(frame.eventId, eventId) != 0) return false;
    preferences.remove(slotKey(head_).c_str());
    head_ = (head_ + 1) % kOutboxCapacity;
    --count_;
    savePointers();
    return true;
  }

  bool contains(const char* eventId) {
    for (uint8_t offset = 0; offset < count_; ++offset) {
      const uint8_t index = (head_ + offset) % kOutboxCapacity;
      gg::Frame frame{};
      if (preferences.getBytes(slotKey(index).c_str(), &frame, sizeof(frame)) == sizeof(frame) &&
          strcmp(frame.eventId, eventId) == 0) {
        return true;
      }
    }
    return false;
  }

  uint8_t count() const { return count_; }

 private:
  String slotKey(uint8_t index) const {
    char key[5]{};
    snprintf(key, sizeof(key), "o%02u", index);
    return String(key);
  }
  void savePointers() {
    preferences.putUChar("head", head_);
    preferences.putUChar("tail", tail_);
    preferences.putUChar("count", count_);
  }
  void clear() {
    preferences.clear();
    head_ = tail_ = count_ = 0;
    savePointers();
  }

  uint8_t head_ = 0;
  uint8_t tail_ = 0;
  uint8_t count_ = 0;
} outbox;

void setLed(bool red, bool green, bool blue) {
  digitalWrite(kRedPin, red ? LOW : HIGH);
  digitalWrite(kGreenPin, green ? LOW : HIGH);
  digitalWrite(kBluePin, blue ? LOW : HIGH);
}

void makeEventId(char* output, size_t size, uint32_t eventSequence) {
  snprintf(output, size, "%s-%s-%08lX", masterId, bootId,
           static_cast<unsigned long>(eventSequence));
}

void radioSend(gg::Frame& frame) {
  gg::finalize(frame);
  if (esp_now_send(kBroadcastMac, reinterpret_cast<uint8_t*>(&frame), sizeof(frame)) != ESP_OK) {
    ++radioFailures;
  }
}

void sendAck(const gg::Frame& source) {
  char eventId[33]{};
  makeEventId(eventId, sizeof(eventId), ++sequence);
  gg::Frame ack = gg::makeFrame(
      gg::MessageType::Acknowledgement, eventId, masterId, bootId,
      sequence, runActive ? millis() - runStartedAt : 0, activeRunId,
      source.eventId, source.deviceId, 0);
  radioSend(ack);
}

void queueRepeatedBroadcast(gg::Frame frame) {
  pendingBroadcast.active = true;
  pendingBroadcast.frame = frame;
  pendingBroadcast.remaining = 5;
  pendingBroadcast.nextAt = millis();
}

void handleRadioFrame(const esp_now_recv_info_t*, const uint8_t* data, int length) {
  if (data == nullptr || length != static_cast<int>(sizeof(gg::Frame))) return;
  gg::Frame frame{};
  memcpy(&frame, data, sizeof(frame));
  if (!gg::valid(frame)) return;
  if (frame.type == gg::MessageType::Acknowledgement) return;

  if (outbox.contains(frame.eventId)) {
    sendAck(frame);
    return;
  }
  if (!outbox.enqueue(frame)) {
    setLed(true, false, true);
    return;  // Station will retry because it did not receive an acknowledgement.
  }
  sendAck(frame);  // Persisted locally; safe for the battery station to stop retrying.
}

void radioReceiveThunk(const esp_now_recv_info_t* info, const uint8_t* data, int length) {
  handleRadioFrame(info, data, length);
}

bool parseCompactValue(const char* payload, const char* key, char* output, size_t outputSize) {
  const String source(payload);
  const String needle = String(key) + "=";
  const int start = source.indexOf(needle);
  if (start < 0) return false;
  const int valueStart = start + needle.length();
  int end = source.indexOf(';', valueStart);
  if (end < 0) end = source.length();
  const String value = source.substring(valueStart, end);
  strlcpy(output, value.c_str(), outputSize);
  return true;
}

String frameJson(const gg::Frame& frame) {
  JsonDocument document;
  document["protocolVersion"] = gg::kProtocolVersion;
  document["eventId"] = frame.eventId;
  document["runId"] = strlen(frame.runId) > 0 ? frame.runId : activeRunId;
  document["deviceId"] = frame.deviceId;
  document["bootId"] = frame.bootId;
  document["sequence"] = frame.sequence;
  document["type"] = gg::messageTypeName(frame.type);
  document["elapsedMilliseconds"] = frame.elapsedMs;

  JsonObject payload = document["payload"].to<JsonObject>();
  if (frame.type == gg::MessageType::Health) {
    char value[40]{};
    if (parseCompactValue(frame.payload, "fw", value, sizeof(value))) payload["firmwareVersion"] = value;
    if (parseCompactValue(frame.payload, "mv", value, sizeof(value))) payload["batteryMillivolts"] = atoi(value);
    if (parseCompactValue(frame.payload, "rssi", value, sizeof(value))) payload["rssi"] = atoi(value);
    if (parseCompactValue(frame.payload, "rf", value, sizeof(value))) payload["radioFailures"] = atoi(value);
    if (parseCompactValue(frame.payload, "module", value, sizeof(value))) payload["stationModule"] = value;
    if (parseCompactValue(frame.payload, "ch", value, sizeof(value))) payload["channel"] = atoi(value);
  } else if (frame.type == gg::MessageType::Registration) {
    payload["firmwareVersion"] = GG_FIRMWARE_VERSION;
    payload["stationModule"] = frame.payload;
  }
  String output;
  serializeJson(document, output);
  return output;
}

void handleControllerMessage(WebsocketsMessage message) {
  JsonDocument document;
  if (deserializeJson(document, message.data()) != DeserializationError::Ok) return;
  const char* acknowledged = document["acknowledgesEventId"] | "";
  if (strlen(acknowledged) > 0) {
    outbox.popIf(acknowledged);
    return;
  }
  const char* typeName = document["type"] | "";
  const char* runId = document["runId"] | "";
  const char* target = document["targetDeviceId"] | "*";
  const gg::MessageType type = gg::messageTypeFromName(typeName);
  char eventId[33]{};
  makeEventId(eventId, sizeof(eventId), ++sequence);
  gg::Frame frame = gg::makeFrame(
      type, eventId, masterId, bootId, sequence,
      document["elapsedMilliseconds"] | 0, runId, "", target);
  frame.channel = WiFi.channel();
  queueRepeatedBroadcast(frame);

  if (type == gg::MessageType::RunStarted) {
    strlcpy(activeRunId, runId, sizeof(activeRunId));
    timerSeconds = document["payload"]["timerSeconds"] | 300;
    runStartedAt = millis();
    runActive = true;
    runPaused = false;
  } else if (type == gg::MessageType::RunPaused) {
    const uint32_t elapsed = (millis() - runStartedAt) / 1000u;
    pausedRemaining = timerSeconds - min(timerSeconds, elapsed);
    runPaused = true;
  } else if (type == gg::MessageType::RunResumed) {
    timerSeconds = pausedRemaining;
    runStartedAt = millis();
    runPaused = false;
  } else if (type == gg::MessageType::RunAborted ||
             type == gg::MessageType::RunTimedOut) {
    runActive = false;
    runPaused = false;
  }
}

bool discoverController() {
  udp.begin(0);
  udp.beginPacket(IPAddress(255, 255, 255, 255), kDiscoveryPort);
  udp.write(reinterpret_cast<const uint8_t*>("GG_DISCOVER_V1"), 14);
  udp.endPacket();
  const uint32_t deadline = millis() + 1800;
  while (millis() < deadline) {
    const int size = udp.parsePacket();
    if (size > 0) {
      char response[96]{};
      const int read = udp.read(response, sizeof(response) - 1);
      if (read > 0 && strncmp(response, "GG_CONTROLLER_V1|", 17) == 0) {
        controllerIp = udp.remoteIP();
        Serial.printf("[controller] Discovered %s\n", controllerIp.toString().c_str());
        udp.stop();
        return true;
      }
    }
    delay(5);
  }
  udp.stop();
  return false;
}

void connectController() {
  if (controllerConnected || millis() < nextConnectAt || WiFi.status() != WL_CONNECTED) return;
  nextConnectAt = millis() + 3000;
  if (controllerIp == IPAddress()) discoverController();
  if (controllerIp == IPAddress()) return;
  const String path = String("/bridge?deviceId=") + masterId;
  Serial.printf("[controller] Connecting to %s:%u%s\n",
                controllerIp.toString().c_str(), kControllerPort, path.c_str());
  controllerConnected = webSocket.connect(controllerIp.toString(), kControllerPort, path);
}

void sendOutboxHead() {
  if (!controllerConnected) return;
  static uint32_t nextSendAt = 0;
  if (millis() < nextSendAt) return;
  gg::Frame frame{};
  if (outbox.peek(frame)) {
    webSocket.send(frameJson(frame));
    nextSendAt = millis() + 500;
  }
}

void sendBeacon() {
  if (millis() < nextBeaconAt) return;
  nextBeaconAt = millis() + 500;
  char eventId[33]{};
  makeEventId(eventId, sizeof(eventId), ++sequence);
  gg::Frame beacon = gg::makeFrame(
      gg::MessageType::Beacon, eventId, masterId, bootId, sequence, 0,
      activeRunId, GG_FIRMWARE_VERSION, "*", 0);
  beacon.channel = WiFi.channel();
  radioSend(beacon);
}

void updateRepeatedBroadcast() {
  if (!pendingBroadcast.active || millis() < pendingBroadcast.nextAt) return;
  radioSend(pendingBroadcast.frame);
  if (--pendingBroadcast.remaining == 0) {
    pendingBroadcast.active = false;
  } else {
    pendingBroadcast.nextAt = millis() + 100;
  }
}

void updateButton() {
  const bool reading = digitalRead(kButtonPin);
  if (reading != buttonReading) {
    buttonReading = reading;
    buttonChangedAt = millis();
  }
  if (millis() - buttonChangedAt < 30 || reading == buttonStable) return;
  buttonStable = reading;
  if (buttonStable != LOW) return;

  char eventId[33]{};
  makeEventId(eventId, sizeof(eventId), ++sequence);
  gg::Frame frame = gg::makeFrame(
      gg::MessageType::MasterStartRequested, eventId, masterId, bootId,
      sequence, 0, "", "");
  if (!outbox.enqueue(frame)) setLed(true, false, true);
}

void updateDisplay() {
  if (millis() < nextDisplayAt) return;
  nextDisplayAt = millis() + 200;
  if (!runActive) {
    display.clear();
    return;
  }
  const uint32_t elapsed = runPaused ? 0 : (millis() - runStartedAt) / 1000u;
  const int remaining = runPaused
      ? static_cast<int>(pausedRemaining)
      : max(0, static_cast<int>(timerSeconds - min(timerSeconds, elapsed)));
  display.showNumberDecEx((remaining / 60) * 100 + remaining % 60, 0b01000000, false);
  if (remaining == 0) runActive = false;
}
}  // namespace

void setup() {
  Serial.begin(115200);
  delay(300);
  Serial.println();
  Serial.println("[boot] Garage Games master starting");
  pinMode(kButtonPin, INPUT_PULLUP);
  pinMode(kRedPin, OUTPUT);
  pinMode(kGreenPin, OUTPUT);
  pinMode(kBluePin, OUTPUT);
  display.setBrightness(7);
  display.clear();
  Serial.println("[boot] RGB self-test: red, green, blue");
  setLed(true, false, false);
  delay(250);
  setLed(false, true, false);
  delay(250);
  setLed(false, false, true);
  delay(250);
  setLed(false, false, false);

  const uint64_t mac = ESP.getEfuseMac();
  snprintf(masterId, sizeof(masterId), "M-%04X%08X",
           static_cast<uint16_t>(mac >> 32u), static_cast<uint32_t>(mac));
  snprintf(bootId, sizeof(bootId), "%08lX", static_cast<unsigned long>(esp_random()));
  Serial.printf("[boot] Device %s, firmware %s\n", masterId, GG_FIRMWARE_VERSION);
  outbox.begin();

  WiFiManager manager;
  manager.setConfigPortalTimeout(180);
  if (digitalRead(kButtonPin) == LOW) {
    Serial.println("[wifi] Master button held: clearing saved Wi-Fi");
    setLed(true, false, true);
    manager.resetSettings();
    delay(500);
  }
  Serial.println("[wifi] Connecting; setup network is GarageGames-Master-Setup");
  if (!manager.autoConnect("GarageGames-Master-Setup")) {
    Serial.println("[wifi] Setup timed out; restarting");
    setLed(true, false, true);
    delay(1000);
    ESP.restart();
  }
  WiFi.setSleep(false);
  Serial.printf("[wifi] Connected to %s, IP %s, channel %d\n",
                WiFi.SSID().c_str(), WiFi.localIP().toString().c_str(), WiFi.channel());

  if (esp_now_init() != ESP_OK) {
    Serial.println("[esp-now] Initialization failed");
    setLed(true, false, true);
    return;
  }
  Serial.println("[esp-now] Radio initialized");
  esp_now_register_recv_cb(radioReceiveThunk);
  esp_now_peer_info_t peer{};
  memcpy(peer.peer_addr, kBroadcastMac, sizeof(kBroadcastMac));
  peer.ifidx = WIFI_IF_STA;
  peer.channel = 0;
  peer.encrypt = false;
  esp_now_add_peer(&peer);

  webSocket.onMessage(handleControllerMessage);
  webSocket.onEvent([](WebsocketsEvent event, String) {
    if (event == WebsocketsEvent::ConnectionOpened) {
      controllerConnected = true;
      Serial.println("[controller] Connected");
    }
    if (event == WebsocketsEvent::ConnectionClosed) {
      controllerConnected = false;
      Serial.println("[controller] Disconnected");
    }
  });
  setLed(false, true, false);
}

void loop() {
  connectController();
  if (controllerConnected) webSocket.poll();
  sendBeacon();
  sendOutboxHead();
  updateRepeatedBroadcast();
  updateButton();
  updateDisplay();
  setLed(!controllerConnected, controllerConnected, outbox.count() > 0);
  delay(1);
}
