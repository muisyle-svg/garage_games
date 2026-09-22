#include "Protocol.h"

namespace gg {

uint32_t crc32(const uint8_t* data, std::size_t length) {
  uint32_t crc = 0xFFFFFFFFu;
  for (std::size_t index = 0; index < length; ++index) {
    crc ^= data[index];
    for (uint8_t bit = 0; bit < 8; ++bit) {
      const uint32_t mask = -(crc & 1u);
      crc = (crc >> 1u) ^ (0xEDB88320u & mask);
    }
  }
  return ~crc;
}

void finalize(Frame& frame) {
  frame.magic = kMagic;
  frame.protocolVersion = kProtocolVersion;
  frame.crc32 = 0;
  frame.crc32 = crc32(reinterpret_cast<const uint8_t*>(&frame), sizeof(Frame));
}

bool valid(const Frame& frame) {
  if (frame.magic != kMagic || frame.protocolVersion != kProtocolVersion) {
    return false;
  }
  Frame copy = frame;
  const uint32_t expected = copy.crc32;
  copy.crc32 = 0;
  return crc32(reinterpret_cast<const uint8_t*>(&copy), sizeof(Frame)) == expected;
}

Frame makeFrame(
    MessageType type,
    const char* eventId,
    const char* deviceId,
    const char* bootId,
    uint32_t sequence,
    uint32_t elapsedMs,
    const char* runId,
    const char* payload,
    const char* targetDeviceId,
    uint8_t flags) {
  Frame frame{};
  frame.type = type;
  frame.flags = flags;
  frame.sequence = sequence;
  frame.elapsedMs = elapsedMs;
  copyText(frame.eventId, eventId);
  copyText(frame.deviceId, deviceId);
  copyText(frame.targetDeviceId, targetDeviceId);
  copyText(frame.bootId, bootId);
  copyText(frame.runId, runId);
  copyText(frame.payload, payload);
  finalize(frame);
  return frame;
}

const char* messageTypeName(MessageType type) {
  switch (type) {
    case MessageType::Beacon: return "beacon";
    case MessageType::Registration: return "device_registered";
    case MessageType::Health: return "device_health";
    case MessageType::RunStarted: return "run_started";
    case MessageType::RunPaused: return "run_paused";
    case MessageType::RunResumed: return "run_resumed";
    case MessageType::RunAborted: return "run_aborted";
    case MessageType::RunTimedOut: return "run_timed_out";
    case MessageType::AttemptStarted: return "attempt_started";
    case MessageType::GameCompleted: return "game_completed";
    case MessageType::BonusCue: return "bonus_cue";
    case MessageType::BonusHit: return "bonus_hit";
    case MessageType::BonusAllDone: return "bonus_all_done";
    case MessageType::Acknowledgement: return "acknowledgement";
    case MessageType::Fault: return "device_fault";
    case MessageType::MasterStartRequested: return "master_start_requested";
    case MessageType::BonusStarted: return "bonus_started";
  }
  return "unknown";
}

MessageType messageTypeFromName(const char* value) {
  if (value == nullptr) return MessageType::Fault;
  for (uint8_t raw = static_cast<uint8_t>(MessageType::Beacon);
       raw <= static_cast<uint8_t>(MessageType::BonusStarted);
       ++raw) {
    const auto type = static_cast<MessageType>(raw);
    if (std::strcmp(messageTypeName(type), value) == 0) return type;
  }
  return MessageType::Fault;
}

}  // namespace gg
