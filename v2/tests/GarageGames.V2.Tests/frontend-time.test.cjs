const test = require("node:test");
const assert = require("node:assert/strict");
const {
  elapsedMsFromRemainingSeconds,
  remainingSecondsFromElapsedMs,
  durationMsFromRemainingSeconds
} = require("../../src/GarageGames.V2/wwwroot/mvp-scorekeeper-time.js");

test("countdown timestamps convert to persisted elapsed milliseconds and back", () => {
  const elapsed = elapsedMsFromRemainingSeconds(247.5, 300);
  assert.equal(elapsed, 52_500);
  assert.equal(remainingSecondsFromElapsedMs(elapsed, 300), 247.5);
});

test("countdown timestamps preserve event duration and imply event order", () => {
  assert.equal(durationMsFromRemainingSeconds(280, 263, 300), 17_000);
  assert.ok(280 > 263, "the earlier start timestamp should be larger than the stop timestamp");
});

test("blank, invalid, and out-of-range countdown fields are rejected or kept blank", () => {
  assert.equal(elapsedMsFromRemainingSeconds("", 300), null);
  assert.equal(remainingSecondsFromElapsedMs(null, 300), null);
  assert.equal(elapsedMsFromRemainingSeconds(301, 300), null);
  assert.equal(elapsedMsFromRemainingSeconds("not a time", 300), null);
});
