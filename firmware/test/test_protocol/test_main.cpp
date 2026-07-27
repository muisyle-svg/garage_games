#include <unity.h>
#include "Protocol.h"

void test_frame_is_within_esp_now_limit() {
  TEST_ASSERT_LESS_OR_EQUAL_UINT32(250, sizeof(gg::Frame));
}

void test_valid_frame_round_trip() {
  gg::Frame frame = gg::makeFrame(
      gg::MessageType::AttemptStarted, "event-1", "station-1", "boot-1",
      42, 1200, "run-1", "{}");
  TEST_ASSERT_TRUE(gg::valid(frame));
  TEST_ASSERT_EQUAL_STRING("event-1", frame.eventId);
  TEST_ASSERT_EQUAL_UINT32(42, frame.sequence);
}

void test_corruption_is_detected() {
  gg::Frame frame = gg::makeFrame(
      gg::MessageType::GameCompleted, "event-2", "station-2", "boot-2",
      7, 9900, "run-1", "{}");
  frame.payload[0] = 'X';
  TEST_ASSERT_FALSE(gg::valid(frame));
}

void test_message_names_are_stable() {
  TEST_ASSERT_EQUAL_STRING("attempt_started", gg::messageTypeName(gg::MessageType::AttemptStarted));
  TEST_ASSERT_EQUAL(
      static_cast<int>(gg::MessageType::GameCompleted),
      static_cast<int>(gg::messageTypeFromName("game_completed")));
}

int main(int, char**) {
  UNITY_BEGIN();
  RUN_TEST(test_frame_is_within_esp_now_limit);
  RUN_TEST(test_valid_frame_round_trip);
  RUN_TEST(test_corruption_is_detected);
  RUN_TEST(test_message_names_are_stable);
  return UNITY_END();
}

