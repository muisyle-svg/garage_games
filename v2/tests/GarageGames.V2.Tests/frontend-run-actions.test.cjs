const test = require("node:test");
const assert = require("node:assert/strict");
const { isDiscardableRun } = require("../../src/GarageGames.V2/wwwroot/mvp-scorekeeper-run-actions.js");

test("only unrecorded runs in abortable current states can be discarded", () => {
  for (const status of ["armed", "countdown", "active", "paused", "finished"]) {
    assert.equal(isDiscardableRun({ status, isRecorded: false }), true, `${status} should be discardable`);
    assert.equal(isDiscardableRun({ status }), true, `${status} with no recorded marker should be discardable`);
  }

  for (const status of ["completed", "timedOut", "aborted", "superseded", "pending"]) {
    assert.equal(isDiscardableRun({ status, isRecorded: false }), false, `${status} should not be discardable`);
  }
  assert.equal(isDiscardableRun({ status: "finished", isRecorded: true }), false);
  assert.equal(isDiscardableRun(null), false);
  assert.equal(isDiscardableRun(undefined), false);
});
