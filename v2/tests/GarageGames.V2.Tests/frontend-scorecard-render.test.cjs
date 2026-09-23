const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");

const source = fs.readFileSync(require.resolve("../../src/GarageGames.V2/wwwroot/mvp-scorekeeper.js"), "utf8");

test("empty current scorecard rows stay pending without reading a null run ID", () => {
  const rowTimingStart = source.indexOf("  function rowTiming(row, run) {");
  const eventTimingStart = source.indexOf("  function eventElapsedMsForDraft(run, event, field) {");
  const eventScoreStart = source.indexOf("  function previewEventScore(run, event) {");
  assert.notEqual(rowTimingStart, -1);
  assert.notEqual(eventTimingStart, -1);
  assert.notEqual(eventScoreStart, -1);

  const rowTiming = source.slice(rowTimingStart, eventTimingStart);
  const eventTiming = source.slice(eventTimingStart, eventScoreStart);
  assert.match(rowTiming, /if \(!run\) \{[\s\S]*?statusCell\.textContent = "Pending";[\s\S]*?return;/);
  assert.ok(rowTiming.indexOf("if (!run)") < rowTiming.indexOf("eventElapsedMsForDraft("));
  assert.match(eventTiming, /if \(!run \|\| !event\?\.eventId\) return null;/);
});
