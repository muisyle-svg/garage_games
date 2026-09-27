const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");

const scorekeeperSource = fs.readFileSync(
  require.resolve("../../src/GarageGames.V2/wwwroot/mvp-scorekeeper.js"),
  "utf8"
);
const declarations = scorekeeperSource.match(
  /  const loadedMasterActions = window\.GarageGamesMasterActions \|\| \{\};[\s\S]*?\n  const runActions = window\.GarageGamesRunActions;/
);

assert.ok(declarations, "scorekeeper master-action compatibility setup should remain present");
const masterActionsSetup = declarations[0].replace(/\n  const runActions = window\.GarageGamesRunActions;$/, "");

function createMasterActions(masterActions) {
  return vm.runInNewContext(`(() => {\n${masterActionsSetup}\nreturn masterActions;\n})()`, {
    window: { GarageGamesMasterActions: masterActions }
  });
}

test("scorekeeper falls back when isRunDurationLocked is missing", () => {
  for (const suppliedActions of [undefined, {}, { shouldResetRunDuration: () => false }]) {
    const actions = createMasterActions(suppliedActions);

    for (const status of ["armed", "countdown", "active", "paused", "finished"]) {
      assert.equal(actions.isRunDurationLocked({ status }), true, `${status} should keep duration locked`);
    }
    for (const status of ["completed", "timedOut", "aborted", "superseded"]) {
      assert.equal(actions.isRunDurationLocked({ status }), false, `${status} should unlock duration`);
    }
    assert.equal(actions.isRunDurationLocked(null), false);
    assert.equal(actions.shouldResetRunDuration({ id: "run-complete", status: "completed" }, null),
      suppliedActions?.shouldResetRunDuration ? false : true);
  }
});
