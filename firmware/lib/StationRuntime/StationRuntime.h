#pragma once

#include <Arduino.h>
#include <WiFi.h>
#include <esp_now.h>
#include <esp_wifi.h>
#include <array>
#include "Protocol.h"

namespace gg {

enum class LedState : uint8_t {
  Off,
  Discovering,
  Ready,
  Attempted,
  Completed,
  BonusCue,
  BonusPrelude,
  Success,
  Error
};

class StationGameModule {
 public:
  virtual ~StationGameModule() = default;
  virtual const char* name() const = 0;
  virtual void begin() = 0;
  virtual void onRunStarted() = 0;
  virtual void onRunStopped() = 0;
  virtual void update(uint32_t now) = 0;
  virtual bool consumeAttempt() = 0;
  virtual bool consumeCompletion() = 0;
};

class StationRuntime {
 public:
  StationRuntime(StationGameModule& module, uint8_t redPin, uint8_t greenPin, uint8_t bluePin);
  void begin();
  void update();
  void setLed(LedState state);
  bool sendEvent(MessageType type, const char* payload = "");
  bool active() const { return active_; }
  const char* runId() const { return runId_; }
  const char* deviceId() const { return deviceId_; }

  static void receiveThunk(const esp_now_recv_info_t* info, const uint8_t* data, int length);
  static StationRuntime* instance;

 private:
  struct Pending {
    bool used = false;
    Frame frame{};
    uint32_t nextAttemptAt = 0;
    uint32_t retryDelay = kAckRetryStartMs;
  };

  void receive(const esp_now_recv_info_t* info, const uint8_t* data, int length);
  void handleFrame(const Frame& frame);
  void discoverChannel(uint32_t now);
  void updatePending(uint32_t now);
  void updateLed(uint32_t now);
  void sendRegistration();
  void sendHealth();
  void acknowledge(const Frame& source);
  bool logicalTarget(const Frame& frame) const;
  void transmit(Frame& frame);
  void makeEventId(char* output, std::size_t length, uint32_t sequence) const;

  StationGameModule& module_;
  uint8_t redPin_;
  uint8_t greenPin_;
  uint8_t bluePin_;
  char deviceId_[kDeviceIdLength]{};
  char bootId_[kBootIdLength]{};
  char runId_[kRunIdLength]{};
  uint8_t masterMac_[6]{};
  uint8_t channel_ = 1;
  uint32_t sequence_ = 0;
  uint32_t runStartedAt_ = 0;
  uint32_t nextChannelHopAt_ = 0;
  uint32_t nextHealthAt_ = 0;
  uint32_t ledChangedAt_ = 0;
  uint32_t radioFailures_ = 0;
  uint32_t bonusPreludeUntil_ = 0;
  bool masterFound_ = false;
  bool active_ = false;
  bool bonusCued_ = false;
  LedState ledState_ = LedState::Discovering;
  std::array<Pending, 4> pending_{};
};

}  // namespace gg
