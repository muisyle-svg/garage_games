const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");

const wwwroot = "../../src/GarageGames.V2/wwwroot/";
const html = fs.readFileSync(require.resolve(`${wwwroot}index.html`), "utf8");
const app = fs.readFileSync(require.resolve(`${wwwroot}mvp-scorekeeper.js`), "utf8");
const css = fs.readFileSync(require.resolve(`${wwwroot}mvp-scorekeeper.css`), "utf8");

test("scorekeeping has a labeled 5:00 M:SS field with a 99:59 input limit", () => {
  assert.match(html, /label for="run-duration-input">Run length \(M:SS\)/);
  assert.match(html, /id="run-duration-input"[^>]*maxlength="5"[^>]*value="5:00"/);
  assert.match(html, /id="run-duration-help"[^>]*aria-live="polite"/);
  assert.match(app, /durationLimitSeconds: selectedRunDurationSeconds\(\)/);
  assert.match(app, /Start \$\{masterActions\.formatRunDuration\(durationSeconds \|\| 300\)\} run/);
});

test("run countdown and scorecard time conversion use the run's saved edition duration", () => {
  assert.match(app, /function runDurationSeconds\(run\)\s*\{\s*const seconds = Number\(run\?\.edition\?\.durationLimitSeconds\)/);
  assert.match(app, /const limitMs = \(liveRun \? runDurationSeconds\(liveRun\) : idleDuration \|\| 300\) \* 1000/);
  assert.doesNotMatch(app, /run\?\.edition\?\.durationLimitSeconds \|\| state\.snapshot\?\.durationLimitSeconds/);
  assert.match(app, /remainingSecondsFromElapsedMs\(elapsed, runDurationSeconds\(run\)\)/);
});

test("terminal displayed runs unlock the next run duration without changing their saved time basis", () => {
  assert.match(app, /function syncRunDurationControl\(run\)\s*\{\s*if \(masterActions\.isRunDurationLocked\(run\)\)/);
  assert.match(app, /masterActions\.shouldResetRunDuration\(run, state\.durationResetRunId\)/);
  assert.match(app, /ui\.durationInput\.value = "5:00";\s*state\.durationResetRunId = run\.id/);
  assert.match(app, /const liveRun = masterActions\.isRunDurationLocked\(run\) \? run : null/);
  assert.match(app, /function runEventTimestamp\(run, elapsedMs\)[\s\S]*?const durationSeconds = runDurationSeconds\(run\)/);
  assert.match(app, /function displayedValue\(run, event, field\)[\s\S]*?remainingSecondsFromElapsedMs\(elapsed, runDurationSeconds\(run\)\)/);
});

test("virtual event buttons show accessible readiness labels and visual state classes", () => {
  assert.match(app, /function virtualDeviceStatus\(readiness\)/);
  for (const label of ["Virtual", "Responding", "Not responding", "Unverified"]) {
    assert.ok(app.includes(`label: "${label}"`), `missing visible ${label} readiness label`);
  }
  assert.match(app, /button\.classList\.add\(`device-\$\{status\.key\}`\)/);
  assert.match(app, /physical button \$\{status\.label\}/);
  assert.match(app, /virtual-device-status/);
  assert.match(app, /virtual-action-hint/);
  assert.match(app, /Stop · \$\{formatSeconds\(remainingMs\)\} left/);
  assert.match(app, /Done · \$\{formatDuration\(event\.finishElapsedMs - event\.startElapsedMs\)\}/);
  for (const status of ["responding", "not-responding", "unverified", "virtual"]) {
    assert.match(css, new RegExp(`\\.virtual-button\\.device-${status}\\s*\\{`));
  }
});

test("physical arming hides old readiness until a post-arm snapshot is received", () => {
  assert.match(app, /if \(!setupTools\.hardwareId\(configured\.assignmentValue \|\| configured\.deviceId \|\| ""\)\) \{\s*return \{ key: "unassigned", label: "Unassigned" \};\s*\}\s*if \(state\.armScanPending\) return \{ key: "unverified", label: "Unverified" \}/);
  assert.match(app, /state\.armScanPending = true;[\s\S]*?await masterActions\.armForPhysicalStart[\s\S]*?state\.armScanAfterSnapshotRequestId = state\.snapshotRequestId;[\s\S]*?await loadSnapshot\(true\)/);
  assert.match(app, /requestId > state\.armScanAfterSnapshotRequestId/);
  assert.match(app, /const armRequestVersionAtStart = state\.armScanRequestVersion/);
  assert.match(app, /armRequestVersionAtStart !== state\.armScanRequestVersion[\s\S]*?state\.setupScanFresh = false/);
});
