const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");

const source = fs.readFileSync(
  require.resolve("../../src/GarageGames.V2/wwwroot/mvp-scorekeeper.js"),
  "utf8"
);

function extractFunction(name, asynchronous = false) {
  const prefix = asynchronous ? `  async function ${name}(` : `  function ${name}(`;
  const start = source.indexOf(prefix);
  assert.notEqual(start, -1, `${name} should exist`);
  const end = source.indexOf("\n  }", start);
  assert.notEqual(end, -1, `${name} should have a closing brace`);
  return source.slice(start, end + 4).trim();
}

function makeSaveHarness({ dirty = false, recorded = false } = {}) {
  const currentRun = { id: "current", status: "finished", isRecorded: recorded };
  const state = {
    snapshot: { currentRun, history: [] },
    busy: false,
    selectedHistoryId: null,
    drafts: new Map(dirty ? [[currentRun.id, true]] : []),
    bonusDrafts: new Map()
  };
  const ui = {
    currentSave: {},
    currentSaveState: {},
    record: {},
    historySave: {},
    historySaveState: {}
  };
  const hasDrafts = (id) => state.drafts.has(id);
  const hasBonusDraft = (id) => state.bonusDrafts.has(id);
  const setSaveStates = vm.runInNewContext(`(${extractFunction("setSaveStates")})`, {
    state,
    ui,
    hasDrafts,
    hasBonusDraft,
    isLiveLock: () => true
  });
  return { state, ui, hasDrafts, hasBonusDraft, setSaveStates };
}

test("Record is disabled and explains that current scorecard edits must be saved", () => {
  const harness = makeSaveHarness({ dirty: true });
  harness.setSaveStates();
  assert.equal(harness.ui.record.disabled, true);
  assert.equal(harness.ui.record.textContent, "Save edits before recording");
  assert.match(harness.ui.record.title, /Save your scorecard corrections/);
});

test("Record is enabled for a clean finished run and remains disabled after recording", () => {
  const harness = makeSaveHarness();
  harness.setSaveStates();
  assert.equal(harness.ui.record.disabled, false);
  assert.equal(harness.ui.record.textContent, "Record result");

  harness.state.snapshot.currentRun.isRecorded = true;
  harness.setSaveStates();
  assert.equal(harness.ui.record.disabled, true);
  assert.equal(harness.ui.record.textContent, "Already recorded");
});

test("record action independently rejects drafts without sending a save request", async () => {
  const run = { id: "current", status: "finished", isRecorded: false };
  const state = { snapshot: { currentRun: run }, selectPromotedAfterRecord: false };
  let requestCount = 0;
  const record = vm.runInNewContext(`(${extractFunction("recordCurrentRun", true)})`, {
    state,
    hasDrafts: () => true,
    hasBonusDraft: () => false,
    request: async () => { requestCount++; }
  });

  await assert.rejects(record(), /Save your scorecard corrections before recording/);
  assert.equal(requestCount, 0);
});

function makeLoadHarness({ response, render }) {
  const previous = { marker: "last-good" };
  const state = {
    snapshot: previous,
    snapshotRequestId: 0,
    appliedSnapshotRequestId: 0,
    receivedAt: 123,
    timeoutRefreshRunId: "previous-run",
    armScanPending: false,
    armScanAfterSnapshotRequestId: null,
    virtualKey: null,
    displayError: ""
  };
  const ui = { lastUpdated: { textContent: "last good update" } };
  const alerts = [];
  const connectionStates = [];
  const loadSnapshot = vm.runInNewContext(`(${extractFunction("loadSnapshot", true)})`, {
    state,
    ui,
    request: async () => {
      if (response instanceof Error) throw response;
      return response;
    },
    setConnection: (online) => {
      state.connected = online;
      connectionStates.push(online);
    },
    showAlert: (message) => alerts.push(message),
    render: () => render(state.snapshot),
    console: { error() {} },
    Date
  });
  return { state, ui, alerts, connectionStates, previous, loadSnapshot };
}

test("fetch failure is reported as a data connection failure", async () => {
  const harness = makeLoadHarness({ response: new Error("server unavailable"), render() {} });
  assert.equal(await harness.loadSnapshot(false), false);
  assert.equal(harness.state.connected, false);
  assert.equal(harness.state.snapshot, harness.previous);
  assert.match(harness.alerts[0], /^Could not load scorekeeper data:/);
});

test("render failure keeps data connected and restores the last good snapshot", async () => {
  const next = { marker: "new-snapshot" };
  const harness = makeLoadHarness({
    response: next,
    render(snapshot) {
      if (snapshot === next) throw new Error("render exploded");
    }
  });

  assert.equal(await harness.loadSnapshot(false), false);
  assert.equal(harness.state.connected, true);
  assert.equal(harness.state.snapshot, harness.previous);
  assert.equal(harness.state.appliedSnapshotRequestId, 0);
  assert.equal(harness.state.displayError, "render exploded");
  assert.equal(harness.ui.lastUpdated.textContent, "last good update");
  assert.match(harness.alerts[0], /^Scorekeeper data is connected, but the screen could not refresh:/);
});

test("undo availability tracks the latest still-effective standard event press", () => {
  const run = {
    id: "run-1",
    status: "active",
    events: [{
      eventId: "pour",
      deviceId: "AABBCCDDEEFF",
      type: "standard",
      status: "active",
      startElapsedMs: 1000,
      finishElapsedMs: null,
      lastSignalElapsedMs: 1000
    }]
  };
  const state = {
    snapshot: {
      messages: [
        { id: 3, runId: "run-1", type: "event-press", disposition: "accepted", deviceId: "AABBCCDDEEFF", elapsedMilliseconds: 1000 },
        { id: 4, runId: "another-run", type: "event-press", disposition: "accepted", deviceId: "AABBCCDDEEFF", elapsedMilliseconds: 2000 },
        { id: 5, runId: "run-1", type: "event-press", disposition: "undone", deviceId: "AABBCCDDEEFF", elapsedMilliseconds: 3000 }
      ]
    }
  };
  const find = vm.runInNewContext(`(${extractFunction("undoableEventPress")})`, { state });
  assert.equal(find(run).message.id, 3);

  run.events[0].status = "completed";
  run.events[0].finishElapsedMs = 4000;
  run.events[0].lastSignalElapsedMs = 4000;
  state.snapshot.messages.unshift({ id: 6, runId: "run-1", type: "event-press", disposition: "accepted", deviceId: "AABBCCDDEEFF", elapsedMilliseconds: 4000 });
  assert.equal(find(run).message.id, 6);
  state.snapshot.messages[0].disposition = "undone";
  run.events[0].status = "active";
  run.events[0].finishElapsedMs = null;
  run.events[0].lastSignalElapsedMs = 1000;
  assert.equal(find(run).message.id, 3);
});
