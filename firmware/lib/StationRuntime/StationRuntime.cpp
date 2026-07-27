#include "StationRuntime.h"

namespace gg {

StationRuntime* StationRuntime::instance = nullptr;
static constexpr uint8_t kBroadcastMac[6] = {0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF};

StationRuntime::StationRuntime(
    StationGameModule& module,
    uint8_t redPin,
    uint8_t greenPin,
    uint8_t bluePin)
    : module_(module), redPin_(redPin), greenPin_(greenPin), bluePin_(bluePin) {}

void StationRuntime::begin() {
  instance = this;
  pinMode(redPin_, OUTPUT);
  pinMode(greenPin_, OUTPUT);
  pinMode(bluePin_, OUTPUT);
  setLed(LedState::Discovering);

  const uint64_t mac = ESP.getEfuseMac();
  snprintf(deviceId_, sizeof(deviceId_), "%04X%08X",
           static_cast<uint16_t>(mac >> 32u), static_cast<uint32_t>(mac));
  snprintf(bootId_, sizeof(bootId_), "%08lX", static_cast<unsigned long>(esp_random()));

  WiFi.mode(WIFI_STA);
  WiFi.disconnect(true, true);
  esp_wifi_set_ps(WIFI_PS_MIN_MODEM);
  esp_wifi_set_channel(channel_, WIFI_SECOND_CHAN_NONE);
  if (esp_now_init() != ESP_OK) {
    setLed(LedState::Error);
    return;
  }
  esp_now_register_recv_cb(receiveThunk);
  esp_now_peer_info_t peer{};
  memcpy(peer.peer_addr, kBroadcastMac, sizeof(kBroadcastMac));
  peer.ifidx = WIFI_IF_STA;
  peer.channel = 0;
  peer.encrypt = false;
  esp_now_add_peer(&peer);
  module_.begin();
}

void StationRuntime::update() {
  const uint32_t now = millis();
  if (!masterFound_) discoverChannel(now);
  updatePending(now);
  updateLed(now);
  module_.update(now);

  if (active_ && module_.consumeAttempt()) {
    setLed(LedState::Attempted);
    sendEvent(MessageType::AttemptStarted);
  }
  if (active_ && module_.consumeCompletion()) {
    if (bonusCued_) {
      bonusCued_ = false;
      setLed(LedState::Success);
      sendEvent(MessageType::BonusHit);
    } else {
      setLed(LedState::Completed);
      sendEvent(MessageType::GameCompleted);
    }
  }
  if (masterFound_ && now >= nextHealthAt_) {
    sendHealth();
    nextHealthAt_ = now + 30000;
  }
}

bool StationRuntime::sendEvent(MessageType type, const char* payload) {
  Pending* slot = nullptr;
  for (auto& pending : pending_) {
    if (!pending.used) {
      slot = &pending;
      break;
    }
  }
  if (slot == nullptr || !masterFound_) {
    setLed(LedState::Error);
    return false;
  }

  const uint32_t sequence = ++sequence_;
  char eventId[33]{};
  makeEventId(eventId, sizeof(eventId), sequence);
  slot->frame = makeFrame(
      type, eventId, deviceId_, bootId_, sequence,
      active_ ? millis() - runStartedAt_ : 0, runId_, payload);
  slot->used = true;
  slot->retryDelay = kAckRetryStartMs;
  slot->nextAttemptAt = millis();
  return true;
}

void StationRuntime::receiveThunk(
    const esp_now_recv_info_t* info,
    const uint8_t* data,
    int length) {
  if (instance != nullptr) instance->receive(info, data, length);
}

void StationRuntime::receive(
    const esp_now_recv_info_t* info,
    const uint8_t* data,
    int length) {
  if (data == nullptr || length != static_cast<int>(sizeof(Frame))) return;
  Frame frame{};
  memcpy(&frame, data, sizeof(frame));
  if (!valid(frame) || !logicalTarget(frame)) return;

  if (frame.type == MessageType::Beacon) {
    memcpy(masterMac_, info->src_addr, sizeof(masterMac_));
    channel_ = frame.channel;
    masterFound_ = true;
    setLed(LedState::Ready);
    sendRegistration();
    return;
  }
  handleFrame(frame);
}

void StationRuntime::handleFrame(const Frame& frame) {
  if (frame.type == MessageType::Acknowledgement) {
    for (auto& pending : pending_) {
      if (pending.used && strcmp(pending.frame.eventId, frame.payload) == 0) {
        pending.used = false;
        if (pending.frame.type == MessageType::AttemptStarted) setLed(LedState::Attempted);
        if (pending.frame.type == MessageType::GameCompleted) setLed(LedState::Completed);
        if (pending.frame.type == MessageType::BonusHit) setLed(LedState::Success);
      }
    }
    return;
  }

  if ((frame.flags & RequiresAcknowledgement) != 0) acknowledge(frame);
  switch (frame.type) {
    case MessageType::RunStarted:
      copyText(runId_, frame.runId);
      active_ = true;
      bonusCued_ = false;
      runStartedAt_ = millis();
      module_.onRunStarted();
      setLed(LedState::Ready);
      break;
    case MessageType::RunPaused:
      active_ = false;
      break;
    case MessageType::RunResumed:
      active_ = true;
      break;
    case MessageType::RunAborted:
    case MessageType::RunTimedOut:
      active_ = false;
      module_.onRunStopped();
      setLed(frame.type == MessageType::RunTimedOut ? LedState::Error : LedState::Off);
      break;
    case MessageType::BonusCue:
      bonusCued_ = true;
      if (millis() >= bonusPreludeUntil_) setLed(LedState::BonusCue);
      break;
    case MessageType::BonusStarted:
      bonusCued_ = false;
      bonusPreludeUntil_ = millis() + 5600;
      setLed(LedState::BonusPrelude);
      break;
    case MessageType::BonusAllDone:
      active_ = false;
      setLed(LedState::Success);
      break;
    default:
      break;
  }
}

void StationRuntime::discoverChannel(uint32_t now) {
  if (now < nextChannelHopAt_) return;
  channel_ = channel_ >= 13 ? 1 : channel_ + 1;
  esp_wifi_set_channel(channel_, WIFI_SECOND_CHAN_NONE);
  nextChannelHopAt_ = now + 220;
}

void StationRuntime::updatePending(uint32_t now) {
  for (auto& pending : pending_) {
    if (!pending.used || now < pending.nextAttemptAt) continue;
    if (pending.retryDelay > kAckRetryStartMs) pending.frame.flags |= IsRetry;
    finalize(pending.frame);
    transmit(pending.frame);
    pending.nextAttemptAt = now + pending.retryDelay;
    pending.retryDelay = min(kAckRetryMaximumMs, pending.retryDelay * 2);
  }
}

void StationRuntime::setLed(LedState state) {
  ledState_ = state;
  ledChangedAt_ = millis();
}

void StationRuntime::updateLed(uint32_t now) {
  bool red = false;
  bool green = false;
  bool blue = false;
  const bool phase = ((now - ledChangedAt_) / 250u) % 2u == 0u;
  switch (ledState_) {
    case LedState::Off: break;
    case LedState::Discovering: blue = phase; break;
    case LedState::Ready: red = true; break;
    case LedState::Attempted: red = phase; green = phase; break;
    case LedState::Completed: green = true; break;
    case LedState::BonusCue: red = phase; green = phase; blue = phase; break;
    case LedState::BonusPrelude: {
      if (now >= bonusPreludeUntil_) {
        setLed(bonusCued_ ? LedState::BonusCue : LedState::Off);
        break;
      }
      const uint8_t color = ((now - ledChangedAt_) / 180u) % 6u;
      red = color == 0 || color == 1 || color == 5;
      green = color == 1 || color == 2 || color == 3;
      blue = color == 3 || color == 4 || color == 5;
      break;
    }
    case LedState::Success: green = phase; break;
    case LedState::Error: red = phase; blue = phase; break;
  }
  // Existing hardware uses a common-anode RGB LED.
  digitalWrite(redPin_, red ? LOW : HIGH);
  digitalWrite(greenPin_, green ? LOW : HIGH);
  digitalWrite(bluePin_, blue ? LOW : HIGH);
}

void StationRuntime::sendRegistration() {
  char payload[kPayloadLength]{};
  snprintf(payload, sizeof(payload), "{\"module\":\"%s\",\"firmware\":\"%s\"}",
           module_.name(), GG_FIRMWARE_VERSION);
  sendEvent(MessageType::Registration, payload);
}

void StationRuntime::sendHealth() {
  char payload[kPayloadLength]{};
  snprintf(payload, sizeof(payload),
           "fw=%s;mv=%d;rssi=%d;rf=%lu;module=%s;ch=%u",
           GG_FIRMWARE_VERSION, 0, WiFi.RSSI(),
           static_cast<unsigned long>(radioFailures_), module_.name(), channel_);
  sendEvent(MessageType::Health, payload);
}

void StationRuntime::acknowledge(const Frame& source) {
  const uint32_t sequence = ++sequence_;
  char eventId[33]{};
  makeEventId(eventId, sizeof(eventId), sequence);
  Frame ack = makeFrame(
      MessageType::Acknowledgement, eventId, deviceId_, bootId_, sequence,
      active_ ? millis() - runStartedAt_ : 0, runId_, source.eventId,
      source.deviceId, 0);
  transmit(ack);
}

bool StationRuntime::logicalTarget(const Frame& frame) const {
  return strcmp(frame.targetDeviceId, "*") == 0 ||
         strcmp(frame.targetDeviceId, deviceId_) == 0;
}

void StationRuntime::transmit(Frame& frame) {
  const esp_err_t result = esp_now_send(kBroadcastMac,
      reinterpret_cast<const uint8_t*>(&frame), sizeof(frame));
  if (result != ESP_OK) ++radioFailures_;
}

void StationRuntime::makeEventId(
    char* output,
    std::size_t length,
    uint32_t sequence) const {
  snprintf(output, length, "%s-%s-%08lX", deviceId_, bootId_,
           static_cast<unsigned long>(sequence));
}

}  // namespace gg
