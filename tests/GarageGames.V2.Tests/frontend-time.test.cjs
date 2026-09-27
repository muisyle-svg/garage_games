const test = require("node:test");
const assert = require("node:assert/strict");
const {
  parseClockTimeToSeconds,
  formatClockMs,
  millisecondsFromClockTime,
  elapsedMsFromRemainingSeconds,
  remainingSecondsFromElapsedMs,
  durationMsFromRemainingSeconds,
  elapsedMsForDraft,
  previewEventScore,
  eventScoreDraftView
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

test("event score preview follows the edition rule and full decay steps", () => {
  const scoring = { basePoints: 50, decayEverySeconds: 5, decayPoints: 5, minimumPoints: 25 };
  const completed = (durationMs) => ({ status: "completed", startElapsedMs: 90_000, finishElapsedMs: 90_000 + durationMs });

  assert.equal(previewEventScore(completed(30_000), scoring), 25, "3:30 remaining to 3:00 remaining is 30 seconds and clamps to 25 points");
  assert.equal(previewEventScore(completed(4_999), scoring), 50);
  assert.equal(previewEventScore(completed(5_000), scoring), 45);
  assert.equal(previewEventScore(completed(24_999), scoring), 30);
  assert.equal(previewEventScore(completed(25_000), scoring), 25);
  assert.equal(previewEventScore(completed(20_000), { basePoints: 80, decayEverySeconds: 10, decayPoints: 8, minimumPoints: 32 }), 64);
});

test("positive grace sets the first loss boundary, then repeats by interval", () => {
  const scoring = { basePoints: 50, minimumPoints: 0, decayPoints: 5, decayEverySeconds: 5 };
  const event = { graceSeconds: 10 };
  const completed = (durationMs) => ({ status: "completed", startElapsedMs: 0, finishElapsedMs: durationMs });

  assert.equal(previewEventScore(completed(9_999), scoring, event), 50);
  assert.equal(previewEventScore(completed(10_000), scoring, event), 45, "the first loss occurs exactly at grace");
  assert.equal(previewEventScore(completed(14_999), scoring, event), 45);
  assert.equal(previewEventScore(completed(15_000), scoring, event), 40);
  assert.equal(previewEventScore(completed(20_000), scoring, event), 35);
});

test("zero grace preserves the legacy first loss at one full interval", () => {
  const scoring = { basePoints: 50, minimumPoints: 0, decayPoints: 5, decayEverySeconds: 5 };
  const completed = (durationMs) => ({ status: "completed", startElapsedMs: 0, finishElapsedMs: durationMs });

  assert.equal(previewEventScore(completed(0), scoring, { graceSeconds: 0 }), 50);
  assert.equal(previewEventScore(completed(4_999), scoring, { graceSeconds: 0 }), 50);
  assert.equal(previewEventScore(completed(5_000), scoring, { graceSeconds: 0 }), 45);
});

test("event score preview honors override precedence, manual scoring, and incomplete events", () => {
  const completed = { status: "completed", startElapsedMs: 10_000, finishElapsedMs: 40_000 };
  const scoring = { basePoints: 50, decayEverySeconds: 5, decayPoints: 5, minimumPoints: 25 };

  assert.equal(previewEventScore({ ...completed, scoreOverride: 17 }, { ...scoring, manualEventPoints: true }), 17);
  assert.equal(previewEventScore(completed, { ...scoring, manualEventPoints: true }), 0);
  assert.equal(previewEventScore({ ...completed, status: "active" }, scoring), 0);
  assert.equal(previewEventScore({ ...completed, finishElapsedMs: 9_999 }, scoring), 0);
});

test("legacy snapshots without a non-null event base keep their saved edition base and minimum", () => {
  const scoring = { basePoints: 83, decayEverySeconds: 10, decayPoints: 7, minimumPoints: 31 };
  const completed = (durationMs) => ({ status: "completed", startElapsedMs: 0, finishElapsedMs: durationMs });

  assert.equal(previewEventScore(completed(0), scoring, {}), 83);
  assert.equal(previewEventScore(completed(10_000), scoring, { basePoints: null }), 76);
  assert.equal(previewEventScore(completed(100_000), scoring, { basePoints: null }), 31);
});

test("explicit per-event starting points use rounded-up half minimum and saved edition decay", () => {
  const scoring = { basePoints: 80, decayEverySeconds: 10, decayPoints: 8, minimumPoints: 7 };
  const completed = (durationMs) => ({ status: "completed", startElapsedMs: 0, finishElapsedMs: durationMs });

  assert.equal(previewEventScore(completed(0), scoring, { basePoints: 51 }), 51);
  assert.equal(previewEventScore(completed(10_000), scoring, { basePoints: 51 }), 43);
  assert.equal(previewEventScore(completed(100_000), scoring, { basePoints: 51 }), 26,
    "an odd starting value clamps at ceil(51 / 2), even if the edition minimum differs");
  assert.equal(previewEventScore(completed(100_000), scoring, { basePoints: 0 }), 0);
});

test("event snapshot scoring overrides every matching global rule and honors explicit zero", () => {
  const scoring = { basePoints: 80, minimumPoints: 25, decayPoints: 5, decayEverySeconds: 5 };
  const completed = (durationMs) => ({ status: "completed", startElapsedMs: 0, finishElapsedMs: durationMs });
  const event = {
    basePoints: 51,
    minimumPoints: 11,
    decayPoints: 7,
    decayEverySeconds: 10,
    graceSeconds: 5
  };

  assert.equal(previewEventScore(completed(4_999), scoring, event), 51);
  assert.equal(previewEventScore(completed(5_000), scoring, event), 44);
  assert.equal(previewEventScore(completed(14_999), scoring, event), 44);
  assert.equal(previewEventScore(completed(15_000), scoring, event), 37);
  assert.equal(previewEventScore(completed(100_000), scoring, event), 11);

  const zeroEvent = { basePoints: 0, minimumPoints: 0, decayPoints: 0, decayEverySeconds: 1, graceSeconds: 0 };
  assert.equal(previewEventScore(completed(50_000), scoring, zeroEvent), 0);
});

test("score overrides still win when an event definition has its own starting points", () => {
  const result = { status: "completed", startElapsedMs: 0, finishElapsedMs: 100_000, scoreOverride: 12 };
  assert.equal(previewEventScore(result, { basePoints: 80, minimumPoints: 40 }, { basePoints: 51 }), 12);
});

test("blank score draft stays blank while its total uses auto preview; typed points remain editable", () => {
  assert.deepEqual(eventScoreDraftView({ scoreTouched: true, scoreValue: "", timingTouched: true, previewScore: 25, persistedScore: 0 }), {
    inputValue: "",
    totalPoints: 25
  });
  assert.deepEqual(eventScoreDraftView({ scoreTouched: true, scoreValue: "37", timingTouched: true, previewScore: 25, persistedScore: 0 }), {
    inputValue: "37",
    totalPoints: 37
  });
  assert.deepEqual(eventScoreDraftView({ timingTouched: true, previewScore: 25, persistedScore: 0 }), {
    inputValue: "25",
    totalPoints: 25
  });
  assert.deepEqual(eventScoreDraftView({ persistedScore: 12 }), {
    inputValue: "12",
    totalPoints: 12
  });
});

test("one edited timestamp preserves the untouched millisecond value for cutoff scoring", () => {
  const runLimitSeconds = 300;
  const untouchedFinishMs = 124_999;
  const editedStartMs = elapsedMsForDraft(null, true, "3:00", runLimitSeconds);
  const retainedFinishMs = elapsedMsForDraft(untouchedFinishMs, false, "", runLimitSeconds);
  const roundedFinishMs = elapsedMsFromRemainingSeconds("2:55", runLimitSeconds);
  const scoring = { basePoints: 50, decayEverySeconds: 5, decayPoints: 5, minimumPoints: 25 };

  assert.equal(editedStartMs, 120_000);
  assert.equal(retainedFinishMs, 124_999);
  assert.equal(retainedFinishMs - editedStartMs, 4_999);
  assert.equal(formatClockMs(retainedFinishMs - editedStartMs), "0:05", "duration display rounds while score math keeps the exact milliseconds");
  assert.equal(previewEventScore({ status: "completed", startElapsedMs: editedStartMs, finishElapsedMs: retainedFinishMs }, scoring), 50);
  assert.equal(previewEventScore({ status: "completed", startElapsedMs: editedStartMs, finishElapsedMs: roundedFinishMs }, scoring), 45,
    "using the rounded M:SS display would incorrectly cross the five-second cutoff");
});

test("both edited M:SS timestamps are parsed as exact displayed seconds", () => {
  const runLimitSeconds = 300;
  const startElapsedMs = elapsedMsForDraft(null, true, "3:30", runLimitSeconds);
  const finishElapsedMs = elapsedMsForDraft(null, true, "3:00", runLimitSeconds);
  assert.equal(startElapsedMs, 90_000);
  assert.equal(finishElapsedMs, 120_000);
  assert.equal(previewEventScore({ status: "completed", startElapsedMs, finishElapsedMs }), 25);
});

test("blank, invalid, and out-of-range countdown fields are rejected or kept blank", () => {
  assert.equal(elapsedMsFromRemainingSeconds("", 300), null);
  assert.equal(remainingSecondsFromElapsedMs(null, 300), null);
  assert.equal(elapsedMsFromRemainingSeconds(301, 300), null);
  assert.equal(elapsedMsFromRemainingSeconds("not a time", 300), null);
});
