const test = require("node:test");
const assert = require("node:assert/strict");
const {
  parseClockTimeToSeconds,
  formatClockMs,
  millisecondsFromClockTime,
  elapsedMsFromRemainingSeconds,
  remainingSecondsFromElapsedMs,
  durationMsFromRemainingSeconds
} = require("../../src/GarageGames.V2/wwwroot/mvp-scorekeeper-time.js");

test("visible run and event times format as M:SS without milliseconds", () => {
  assert.equal(formatClockMs(0), "0:00");
  assert.equal(formatClockMs(65_400), "1:05");
  assert.equal(formatClockMs(3_599_600), "60:00");
  assert.equal(formatClockMs(null), "");
});

test("M:SS and seconds input parse to seconds", () => {
  assert.equal(parseClockTimeToSeconds("3:07"), 187);
  assert.equal(parseClockTimeToSeconds("47"), 47);
  assert.equal(parseClockTimeToSeconds("2.5"), 2.5);
  assert.equal(parseClockTimeToSeconds("2:60"), null);
  assert.equal(parseClockTimeToSeconds(""), null);
});

test("simulator durations accept M:SS or seconds and convert to milliseconds", () => {
  assert.equal(millisecondsFromClockTime("1:30"), 90_000);
  assert.equal(millisecondsFromClockTime("2.5"), 2_500);
  assert.equal(millisecondsFromClockTime("0:00"), 0);
  assert.equal(millisecondsFromClockTime("not a time"), null);
});

test("countdown timestamps convert to persisted elapsed milliseconds and back", () => {
  const elapsed = elapsedMsFromRemainingSeconds(247.5, 300);
  assert.equal(elapsed, 52_500);
  assert.equal(remainingSecondsFromElapsedMs(elapsed, 300), 247.5);
});

test("M:SS event timestamp edits retain millisecond storage units", () => {
  assert.equal(elapsedMsFromRemainingSeconds("4:07", 300), 53_000);
  assert.equal(elapsedMsFromRemainingSeconds("5:01", 300), null);
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
