#pragma once

#include <cstddef>
#include <cstdint>
#include <cstring>

namespace gg {

constexpr uint16_t kMagic = 0x4747;
constexpr uint8_t kProtocolVersion = 1;
constexpr std::size_t kDeviceIdLength = 18;
constexpr std::size_t kBootIdLength = 9;
constexpr std::size_t kRunIdLength = 33;
constexpr std::size_t kPayloadLength = 112;
constexpr uint32_t kAckRetryStartMs = 150;
constexpr uint32_t kAckRetryMaximumMs = 2000;

enum class MessageType : uint8_t {
  Beacon = 1,
  Registration = 2,
  Health = 3,
  RunStarted = 4,
  RunPaused = 5,
  RunResumed = 6,
  RunAborted = 7,
  RunTimedOut = 8,
  AttemptStarted = 9,
  GameCompleted = 10,
  BonusCue = 11,
  BonusHit = 12,
  BonusAllDone = 13,
  Acknowledgement = 14,
  Fault = 15,
  MasterStartRequested = 16,
  BonusStarted = 17
};

enum FrameFlags : uint8_t {
  RequiresAcknowledgement = 1 << 0,
  IsRetry = 1 << 1
};

#pragma pack(push, 1)
struct Frame {
  uint16_t magic;
  uint8_t protocolVersion;
  MessageType type;
  uint8_t flags;
  uint8_t channel;
  uint32_t sequence;
  uint32_t elapsedMs;
  char eventId[33];
  char deviceId[kDeviceIdLength];
  char targetDeviceId[kDeviceIdLength];
  char bootId[kBootIdLength];
  char runId[kRunIdLength];
  char payload[kPayloadLength];
  uint32_t crc32;
};
#pragma pack(pop)

static_assert(sizeof(Frame) <= 250, "ESP-NOW frame exceeds protocol payload limit");

uint32_t crc32(const uint8_t* data, std::size_t length);
void finalize(Frame& frame);
bool valid(const Frame& frame);
Frame makeFrame(
    MessageType type,
    const char* eventId,
    const char* deviceId,
    const char* bootId,
    uint32_t sequence,
    uint32_t elapsedMs,
    const char* runId = "",
    const char* payload = "",
    const char* targetDeviceId = "*",
    uint8_t flags = RequiresAcknowledgement);
const char* messageTypeName(MessageType type);
MessageType messageTypeFromName(const char* value);

template <std::size_t N>
inline void copyText(char (&destination)[N], const char* source) {
  std::memset(destination, 0, N);
  if (source != nullptr) {
    std::strncpy(destination, source, N - 1);
  }
}

}  // namespace gg
