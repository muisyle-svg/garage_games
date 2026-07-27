#include <Arduino.h>
#include "StationRuntime.h"

namespace {
constexpr uint8_t kStartButton = D2;
constexpr uint8_t kAnswerPins[] = {D6, D7, D8, D9};
constexpr uint8_t kRedPin = D3;
constexpr uint8_t kGreenPin = D4;
constexpr uint8_t kBluePin = D5;
const char* kPrompts[] = {"RED", "BLUE", "NORTH", "SOUTH"};

class PromptGameModule final : public gg::StationGameModule {
 public:
  const char* name() const override { return "prompt-demo"; }
  void begin() override {
    pinMode(kStartButton, INPUT_PULLUP);
    for (const uint8_t pin : kAnswerPins) pinMode(pin, INPUT_PULLUP);
  }
  void onRunStarted() override {
    state_ = State::Waiting;
    attemptPending_ = false;
    completionPending_ = false;
    previousStart_ = HIGH;
  }
  void onRunStopped() override { state_ = State::Disabled; }
  void update(uint32_t now) override {
    if (state_ == State::Disabled) return;
    const bool start = digitalRead(kStartButton);
    if (state_ == State::Waiting && previousStart_ == HIGH && start == LOW) {
      selected_ = esp_random() % 4;
      state_ = State::Answering;
      attemptPending_ = true;
      Serial.printf("[PROMPT DISPLAY] %s\n", kPrompts[selected_]);
    }
    previousStart_ = start;
    if (state_ != State::Answering || now < nextInputAt_) return;
    for (uint8_t index = 0; index < 4; ++index) {
      if (digitalRead(kAnswerPins[index]) == LOW) {
        nextInputAt_ = now + 200;
        if (index == selected_) {
          completionPending_ = true;
          state_ = State::Complete;
          Serial.println("[PROMPT DISPLAY] CORRECT");
        } else {
          Serial.println("[PROMPT DISPLAY] TRY AGAIN");
        }
        break;
      }
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
  enum class State { Disabled, Waiting, Answering, Complete };
  State state_ = State::Disabled;
  bool previousStart_ = HIGH;
  bool attemptPending_ = false;
  bool completionPending_ = false;
  uint8_t selected_ = 0;
  uint32_t nextInputAt_ = 0;
};

PromptGameModule game;
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

