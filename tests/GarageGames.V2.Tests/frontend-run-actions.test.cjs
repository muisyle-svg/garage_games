const test = require("node:test");
const assert = require("node:assert/strict");
const { isDiscardableRun, isRecordableRun, blocksNextRun, canReopenRun, remainingMs } =
  require("../../src/GarageGames.V2/wwwroot/mvp-scorekeeper-run-actions.js");

test("only a run that is over can be recorded", () => {
  for (const status of ["armed", "countdown", "active", "paused", "completed", "aborted", "superseded"]) {
    assert.equal(isRecordableRun({ status, isRecorded: false }), false, `${status} should not be recordable`);
  }
  assert.equal(isRecordableRun({ status: "finished", isRecorded: false }), true);
  assert.equal(isRecordableRun({ status: "timedOut", isRecorded: false }), true);
  assert.equal(isRecordableRun({ status: "timedOut", isRecorded: true }), false);
  assert.equal(isRecordableRun({ status: "timedOut", deletedAt: "2026-09-29T00:00:00Z" }), false);
  assert.equal(isRecordableRun(null), false);
});

test("an unrecorded timeout holds the next run until recorded or discarded", () => {
  assert.equal(blocksNextRun({ status: "timedOut", isRecorded: false }), true);
  assert.equal(blocksNextRun({ status: "timedOut", isRecorded: true }), false);
  assert.equal(blocksNextRun({ status: "timedOut", isRecorded: false, deletedAt: "2026-09-29T00:00:00Z" }), false);
  for (const status of ["aborted", "completed", "finished", "armed"]) {
    assert.equal(blocksNextRun({ status, isRecorded: false }), false, status);
  }
  assert.equal(blocksNextRun(null), false);
});

test("a finished run with time and events left can be reopened", () => {
  const run = (extra = {}) => ({
    status: "finished", isRecorded: false, activeElapsedMs: 60_000, edition: { durationLimitSeconds: 300 },
    events: [{ status: "completed" }, { status: "pending" }], ...extra
  });
  assert.equal(remainingMs(run()), 240_000);
  assert.equal(canReopenRun(run()), true);
  assert.equal(canReopenRun(run({ status: "timedOut" })), false);
  assert.equal(canReopenRun(run({ isRecorded: true })), false);
  assert.equal(canReopenRun(run({ bonusGame: { phase: "ended" } })), false, "the bonus round already ended it");
  assert.equal(canReopenRun(run({ events: [{ status: "completed" }] })), false, "nothing left to play");
  assert.equal(canReopenRun(run({ activeElapsedMs: 300_000 })), false, "no time left");
  assert.equal(canReopenRun(null), false);
});

test("only unrecorded runs in abortable current states can be discarded", () => {
  for (const status of ["armed", "countdown", "active", "paused", "finished", "timedOut"]) {
    assert.equal(isDiscardableRun({ status, isRecorded: false }), true, `${status} should be discardable`);
    assert.equal(isDiscardableRun({ status }), true, `${status} with no recorded marker should be discardable`);
  }

  for (const status of ["completed", "aborted", "superseded", "pending"]) {
    assert.equal(isDiscardableRun({ status, isRecorded: false }), false, `${status} should not be discardable`);
  }
  assert.equal(isDiscardableRun({ status: "finished", isRecorded: true }), false);
  assert.equal(isDiscardableRun({ status: "timedOut", isRecorded: true }), false, "a recorded timeout is changed from history");
  assert.equal(isDiscardableRun(null), false);
  assert.equal(isDiscardableRun(undefined), false);
});
