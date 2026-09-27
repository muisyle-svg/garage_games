const test = require("node:test");
const assert = require("node:assert/strict");
const { orderScorecardEvents } = require("../../src/GarageGames.V2/wwwroot/mvp-scorecard-order.js");

const configured = [
  { eventId: "one", name: "One" },
  { eventId: "two", name: "Two" },
  { eventId: "three", name: "Three" },
  { eventId: "four", name: "Four" },
  { eventId: "five", name: "Five" }
];

function orderedIds(results) {
  return orderScorecardEvents(configured, results).map(({ definition }) => definition.eventId);
}

test("completed scorecard events sort by elapsed finish time, not countdown value", () => {
  assert.deepEqual(orderedIds([
    { eventId: "one", status: "completed", finishElapsedMs: 90_000 },
    { eventId: "two", status: "completed", finishElapsedMs: 20_000 },
    { eventId: "three", status: "completed", finishElapsedMs: 55_000 }
  ]), ["two", "three", "one", "four", "five"]);
});

test("equal and invalid/missing completion times retain configured order after timestamped completions", () => {
  assert.deepEqual(orderedIds([
    { eventId: "one", status: "completed", finishElapsedMs: 30_000 },
    { eventId: "two", status: "completed", finishElapsedMs: Number.NaN },
    { eventId: "three", status: "completed", finishElapsedMs: 10_000 },
    { eventId: "four", status: "completed", finishElapsedMs: 30_000 },
    { eventId: "five", status: "completed" }
  ]), ["three", "one", "four", "two", "five"]);
});

test("active, pending, and absent events trail all completions in configured order", () => {
  assert.deepEqual(orderedIds([
    { eventId: "one", status: "pending", finishElapsedMs: 1_000 },
    { eventId: "two", status: "active", finishElapsedMs: null },
    { eventId: "four", status: "completed", finishElapsedMs: 8_000 },
    { eventId: "five", status: "completed", finishElapsedMs: null }
  ]), ["four", "five", "one", "two", "three"]);
});
