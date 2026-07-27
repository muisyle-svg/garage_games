#include <Arduino.h>
#include "StationRuntime.h"

namespace {
constexpr uint8_t kButtonPin = D2;
constexpr uint8_t kRedPin = D3;
constexpr uint8_t kGreenPin = D4;
constexpr uint8_t kBluePin = D5;

class StandardButtonModule final : public gg::StationGameModule {
 public:
  const char* name() const override { return "standard"; }
  void begin() override { pinMode(kButtonPin, INPUT_PULLUP); }
  void onRunStarted() override {
    pressCount_ = 0;
    attemptPending_ = false;
    completionPending_ = false;
  }
  void onRunStopped() override {}
  void update(uint32_t now) override {
    const bool reading = digitalRead(kButtonPin);
    if (reading != lastReading_) {
      lastReading_ = reading;
      changedAt_ = now;
    }
    if (now - changedAt_ < 30 || reading == stable_) return;
    stable_ = reading;
    if (stable_ != LOW) return;
    if (pressCount_ == 0) {
      attemptPending_ = true;
      pressCount_ = 1;
    } else {
      completionPending_ = true;
      pressCount_ = 2;
    }
  }
  bool consumeAttempt() override {
    const bool value = attemptPending_;
    attemptPending_ = false;
    return value;
  }
  bool consumeCompletion() override {
    const bool value = completionPending_;
    completionPending_ = false;
    return value;
  }

 private:
  bool lastReading_ = HIGH;
  bool stable_ = HIGH;
  bool attemptPending_ = false;
  bool completionPending_ = false;
  uint8_t pressCount_ = 0;
  uint32_t changedAt_ = 0;
};

StandardButtonModule game;
gg::StationRuntime station(game, kRedPin, kGreenPin, kBluePin);
}  // namespace

void setup() {
  Serial.begin(115200);
  station.begin();
}

void loop() {
  station.update();
  delay(1);
}

