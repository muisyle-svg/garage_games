const test = require("node:test");
const assert = require("node:assert/strict");
const {
  canArmPhysical,
  canStartVirtual,
  handshakeGuidance,
  connectMaster,
  disconnectMaster,
  armForPhysicalStart,
  startVirtually
} = require("../../src/GarageGames.V2/wwwroot/mvp-scorekeeper-master.js");

test("master connection actions use the selected port and the expected endpoints", async () => {
  const calls = [];
  const request = async (...args) => calls.push(args);

  await connectMaster(request, "COM7");
  await disconnectMaster(request);

  assert.deepEqual(calls, [
    ["/api/master/connect", { method: "POST", body: JSON.stringify({ port: "COM7" }) }],
    ["/api/master/disconnect", { method: "POST" }]
  ]);
  await assert.rejects(connectMaster(request, ""), /Select a COM port/);
});

test("physical arming posts the selected competitor and category without starting the timer", async () => {
  const calls = [];
  const request = async (...args) => calls.push(args);

  assert.equal(canArmPhysical({ connected: true, mode: "IDLE" }, null, "competitor-1"), true);
  await armForPhysicalStart(request, {
    master: { connected: true, mode: "IDLE" },
    currentRun: null,
    competitorId: "competitor-1",
    category: "playoff"
  });

  assert.deepEqual(calls, [["/api/run/arm", {
    method: "POST",
    body: JSON.stringify({ competitorId: "competitor-1", category: "playoff" })
  }]]);
});

test("an open COM port waits for the master handshake before physical arming", async () => {
  const master = { connected: true, port: "COM7", mode: null };
  const calls = [];
  const request = async (...args) => calls.push(args);
  const options = { master, currentRun: null, competitorId: "competitor-1", category: "official" };

  assert.equal(canArmPhysical(master, null, "competitor-1"), false);
  assert.match(handshakeGuidance(master), /Waiting for the physical master handshake \(GG1 HELLO and MODE IDLE\)/);
  await assert.rejects(armForPhysicalStart(request, options), /Waiting for the physical master handshake/);
  assert.deepEqual(calls, []);

  assert.equal(canStartVirtual(master, null, "competitor-1"), true);
  await startVirtually(request, options);
  assert.deepEqual(calls.map(([path]) => path), ["/api/run/arm", "/api/run/start"]);
});

test("virtual Start arms and starts without a connected physical master", async () => {
  const calls = [];
  const request = async (...args) => calls.push(args);
  const afterArm = async () => calls.push(["snapshot-refresh"]);

  assert.equal(canStartVirtual({ connected: false }, null, "competitor-2"), true);
  const started = await startVirtually(request, {
    master: { connected: false },
    currentRun: null,
    competitorId: "competitor-2",
    category: "official"
  }, afterArm);

  assert.deepEqual(calls.map(([path]) => path), ["/api/run/arm", "snapshot-refresh", "/api/run/start"]);
  assert.deepEqual(started, { competitorId: "competitor-2", category: "official" });
});

test("virtual Start can start a previously armed physical run", async () => {
  const calls = [];
  const request = async (...args) => calls.push(args);
  const started = await startVirtually(request, {
    master: { connected: false, mode: "IDLE" },
    currentRun: { status: "armed", competitorId: "competitor-3", category: "exhibition" },
    competitorId: "ignored-selection",
    category: "official"
  });

  assert.deepEqual(calls, [["/api/run/start", { method: "POST" }]]);
  assert.deepEqual(started, { competitorId: "competitor-3", category: "exhibition" });
});

test("virtual Start returns the countdown run so audio can begin immediately", async () => {
  const countdownRun = { id: "run-countdown", status: "countdown" };
  const request = async (path) => path === "/api/run/start" ? countdownRun : null;
  const started = await startVirtually(request, {
    master: { connected: false },
    currentRun: { status: "armed", competitorId: "competitor-4", category: "official" },
    competitorId: "competitor-4",
    category: "official"
  });

  assert.deepEqual(started, { competitorId: "competitor-4", category: "official", run: countdownRun });
});

test("SPEED mode blocks physical arming and virtual start with a clear error", async () => {
  const request = async () => assert.fail("SPEED mode must not send a start request");
  const master = { connected: true, mode: "SPEED" };

  assert.equal(canArmPhysical(master, null, "competitor-1"), false);
  assert.equal(canStartVirtual(master, null, "competitor-1"), false);
  await assert.rejects(
    armForPhysicalStart(request, { master, currentRun: null, competitorId: "competitor-1", category: "official" }),
    /SPEED mode/
  );
  await assert.rejects(
    startVirtually(request, { master, currentRun: null, competitorId: "competitor-1", category: "official" }),
    /SPEED mode/
  );
});

test("physical arming requires a connected master and no live run", async () => {
  const request = async () => assert.fail("invalid physical arm request must not be sent");
  assert.equal(canArmPhysical({ connected: false }, null, "competitor-1"), false);
  assert.equal(canArmPhysical({ connected: true, mode: "IDLE" }, { status: "active" }, "competitor-1"), false);
  await assert.rejects(
    armForPhysicalStart(request, { master: { connected: false }, currentRun: null, competitorId: "competitor-1", category: "official" }),
    /Connect the physical master/
  );
});
