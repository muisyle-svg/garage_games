const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const setup = require("../../src/GarageGames.V2/wwwroot/mvp-scorekeeper-setup.js");

test("legacy station placeholders remain unassigned and save as distinct nonempty device IDs", () => {
  const draft = setup.normalizeSetup({
    editionId: "edition-1",
    name: "Garage Games",
    events: [
      { eventId: "station-01", name: "Start", deviceId: "station-01", type: "standard" },
      { eventId: "station-02", name: "Finish", deviceId: "station-02", type: "standard" }
    ]
  });

  assert.equal(draft.events[0].assignmentValue, "");
  assert.equal(setup.readiness(draft.events[0], null, false).key, "unassigned");
  const payload = setup.buildSetupPayload(draft);
  assert.equal(payload.events[0].deviceId, "station-01");
  assert.equal(new Set(payload.events.map((event) => event.deviceId.toLowerCase())).size, 2);
});

test("new events receive unique non-MAC placeholders and MAC assignments normalize to unique uppercase IDs", () => {
  const draft = setup.normalizeSetup({
    editionId: "edition-2",
    name: "Summer edition",
    events: [{ eventId: "station-01", name: "Start", deviceId: "station-01", type: "standard" }]
  });
  const added = setup.addEvent(draft);
  assert.notEqual(added.eventId, draft.events[0].eventId);
  assert.notEqual(added.unassignedDeviceId, draft.events[0].unassignedDeviceId);
  assert.equal(added.basePoints, 50);
  assert.equal(added.basePointsInherited, true);
  assert.equal(setup.hardwareId("aa:bb:cc:dd:ee:ff"), "AABBCCDDEEFF");

  added.assignmentValue = "aa:bb:cc:dd:ee:ff";
  const payload = setup.buildSetupPayload(draft);
  assert.equal(payload.events[1].deviceId, "AABBCCDDEEFF");
  assert.throws(() => {
    draft.events[0].assignmentValue = "AABBCCDDEEFF";
    setup.buildSetupPayload(draft);
  }, /already assigned/);
});

test("inherited starting points display the default but remain null when unrelated setup fields are saved", () => {
  const draft = setup.normalizeSetup({
    editionId: "edition-inherited",
    name: "Edition",
    scoring: { basePoints: 63 },
    events: [
      { eventId: "event-null", name: "Inherited null", deviceId: "unassigned-null", basePoints: null },
      { eventId: "event-missing", name: "Inherited missing", deviceId: "unassigned-missing" }
    ]
  });

  assert.equal(draft.events[0].basePoints, 63);
  assert.equal(draft.events[1].basePoints, 63);
  assert.equal(draft.events[0].basePointsInherited, true);
  const payload = setup.buildSetupPayload(draft);
  assert.equal(payload.events[0].basePoints, null);
  assert.equal(payload.events[1].basePoints, null);

  draft.events[0].name = "Renamed only";
  draft.events[1].assignmentValue = "AABBCCDDEEFF";
  assert.equal(setup.buildSetupPayload(draft).events[0].basePoints, null);
  assert.equal(setup.buildSetupPayload(draft).events[1].basePoints, null);
});

test("edited starting points, including zero and odd values, save explicitly", () => {
  const draft = setup.normalizeSetup({
    editionId: "edition-explicit",
    name: "Edition",
    events: [
      { eventId: "event-zero", name: "Zero", deviceId: "unassigned-zero", basePoints: 0 },
      { eventId: "event-inherited", name: "Odd", deviceId: "unassigned-odd", basePoints: null }
    ]
  });
  draft.events[1].basePoints = "51";
  draft.events[1].basePointsInherited = false;

  const payload = setup.buildSetupPayload(draft);
  assert.equal(payload.events[0].basePoints, 0);
  assert.equal(payload.events[1].basePoints, 51);
});

test("per-event scoring displays effective defaults while unrelated saves preserve inherited nulls", () => {
  const draft = setup.normalizeSetup({
    editionId: "edition-scoring-defaults",
    name: "Edition",
    scoring: { basePoints: 63, minimumPoints: 31, decayPoints: 7, decayEverySeconds: 9 },
    events: [
      { eventId: "legacy", name: "Legacy", deviceId: "unassigned-legacy" },
      { eventId: "explicit-base", name: "Explicit base", deviceId: "unassigned-explicit", basePoints: 51 }
    ]
  });
  const [legacy, explicitBase] = draft.events;

  assert.deepEqual(draft.scoringDefaults, {
    basePoints: 63,
    minimumPoints: 31,
    decayPoints: 7,
    decayEverySeconds: 9,
    graceSeconds: 0
  });
  assert.deepEqual(
    [legacy.basePoints, legacy.minimumPoints, legacy.decayPoints, legacy.decayEverySeconds, legacy.graceSeconds],
    [63, 31, 7, 9, 0]
  );
  assert.deepEqual(
    [explicitBase.basePoints, explicitBase.minimumPoints, explicitBase.decayPoints, explicitBase.decayEverySeconds, explicitBase.graceSeconds],
    [51, 26, 7, 9, 0],
    "an odd explicit base derives a rounded-up minimum while the other fields inherit"
  );

  const payload = setup.buildSetupPayload(draft);
  for (const event of payload.events) {
    assert.equal(event.minimumPoints, null);
    assert.equal(event.decayPoints, null);
    assert.equal(event.decayEverySeconds, null);
    assert.equal(event.graceSeconds, null);
  }
  assert.equal(payload.events[0].basePoints, null, "legacy missing base points remain inherited");
  assert.equal(payload.events[1].basePoints, 51);

  const roundTrip = setup.normalizeSetup(payload, draft.scoringDefaults);
  assert.equal(roundTrip.events[0].basePoints, 63);
  assert.equal(roundTrip.events[0].minimumPoints, 31);
  assert.equal(roundTrip.events[0].decayPoints, 7);
  assert.equal(roundTrip.events[0].graceSeconds, 0);
});

test("editing base points recalculates an inherited minimum but keeps an edited minimum", () => {
  const draft = setup.normalizeSetup({
    editionId: "edition-derived-minimum",
    name: "Edition",
    scoring: { basePoints: 50, minimumPoints: 20 },
    events: [{ eventId: "event-1", name: "Button", deviceId: "unassigned-event-1", basePoints: null }]
  });
  const event = draft.events[0];

  assert.equal(setup.updateEventScoring(event, "basePoints", "51"), true);
  assert.equal(event.minimumPoints, 26);
  assert.equal(event.minimumPointsInherited, true);
  assert.equal(setup.buildSetupPayload(draft).events[0].minimumPoints, null);

  setup.updateEventScoring(event, "minimumPoints", "17");
  setup.updateEventScoring(event, "basePoints", "49");
  assert.equal(event.minimumPoints, "17");
  assert.equal(event.minimumPointsInherited, false);
  assert.equal(setup.buildSetupPayload(draft).events[0].minimumPoints, 17);
});

test("explicit scoring zeros and odd values save without becoming inherited", () => {
  const draft = setup.normalizeSetup({
    editionId: "edition-explicit-scoring",
    name: "Edition",
    events: [{
      eventId: "event-1",
      name: "Button",
      deviceId: "unassigned-event-1",
      basePoints: 51,
      minimumPoints: 0,
      decayPoints: 0,
      decayEverySeconds: 1,
      graceSeconds: 0
    }]
  });

  const event = draft.events[0];
  for (const field of ["minimumPoints", "decayPoints", "decayEverySeconds", "graceSeconds"]) {
    assert.equal(event[`${field}Inherited`], false);
  }
  assert.deepEqual(setup.buildSetupPayload(draft).events[0], {
    eventId: "event-1",
    name: "Button",
    deviceId: "unassigned-event-1",
    type: "standard",
    basePoints: 51,
    minimumPoints: 0,
    decayPoints: 0,
    decayEverySeconds: 1,
    graceSeconds: 0
  });
});

test("scoring validation enforces minimum, nonnegative points, and backend caps", () => {
  const draft = setup.normalizeSetup({
    editionId: "edition-scoring-validation",
    name: "Edition",
    events: [{ eventId: "event-1", name: "Button", deviceId: "unassigned-event-1" }]
  });
  const event = draft.events[0];

  event.minimumPoints = "51";
  assert.throws(() => setup.buildSetupPayload(draft), /minimum points must be a whole number from 0 to the starting points/);
  event.minimumPoints = "25";

  for (const invalid of ["-1", "1.5", "1000001", ""]) {
    event.decayPoints = invalid;
    assert.throws(() => setup.buildSetupPayload(draft), /points lost each step must be a whole number from 0 to 1,000,000/);
  }
  event.decayPoints = "1000000";
  event.decayEverySeconds = "0";
  assert.throws(() => setup.buildSetupPayload(draft), /seconds per step must be a whole number from 1 to 86,400/);
  event.decayEverySeconds = "86401";
  assert.throws(() => setup.buildSetupPayload(draft), /seconds per step must be a whole number from 1 to 86,400/);
  event.decayEverySeconds = "86400";
  event.graceSeconds = "-1";
  assert.throws(() => setup.buildSetupPayload(draft), /initial grace seconds must be a whole number from 0 to 86,400/);
  event.graceSeconds = "86401";
  assert.throws(() => setup.buildSetupPayload(draft), /initial grace seconds must be a whole number from 0 to 86,400/);
  setup.updateEventScoring(event, "graceSeconds", "86400");
  assert.equal(setup.buildSetupPayload(draft).events[0].graceSeconds, 86400);
});

test("save validation rejects malformed MACs, duplicate event IDs, empty names, and an empty roster", () => {
  const draft = setup.normalizeSetup({
    editionId: "edition-3",
    name: "Edition",
    events: [{ eventId: "event-1", name: "Button", deviceId: "unassigned-event-1", type: "standard" }]
  });
  draft.events[0].assignmentValue = "not-a-mac";
  assert.throws(() => setup.buildSetupPayload(draft), /12-hex MAC/);
  draft.events[0].assignmentValue = "";
  draft.events[0].name = "  ";
  assert.throws(() => setup.buildSetupPayload(draft), /Enter a name/);
  draft.events[0].name = "Button";
  draft.events.push({ ...draft.events[0] });
  assert.throws(() => setup.buildSetupPayload(draft), /unique event ID/);
  draft.events = [];
  assert.throws(() => setup.buildSetupPayload(draft), /at least one event/);
});

test("save validation requires starting points to be a nonnegative whole number", () => {
  const draft = setup.normalizeSetup({
    editionId: "edition-points-validation",
    name: "Edition",
    events: [{ eventId: "event-1", name: "Button", deviceId: "unassigned-event-1" }]
  });

  for (const invalid of ["", "  ", "1.5", "-1", "1000001", "not a number"]) {
    draft.events[0].basePoints = invalid;
    draft.events[0].basePointsInherited = false;
    assert.throws(() => setup.buildSetupPayload(draft), /starting points must be a whole number from 0 to 1,000,000/);
  }
  setup.updateEventScoring(draft.events[0], "basePoints", "0");
  assert.equal(setup.buildSetupPayload(draft).events[0].basePoints, 0);
});

test("editable scoring inputs expose the backend maxima and draft handler", () => {
  const source = fs.readFileSync(require.resolve("../../src/GarageGames.V2/wwwroot/mvp-scorekeeper.js"), "utf8");
  assert.match(source, /setupScoringField\(event, "basePoints", "Starting points", 0, 1_000_000\)/);
  assert.match(source, /setupScoringField\(event, "decayPoints", "Points lost \/ step", 0, 1_000_000\)/);
  assert.match(source, /setupScoringField\(event, "decayEverySeconds", "Seconds \/ step", 1, 86_400\)/);
  assert.match(source, /setupScoringField\(event, "graceSeconds", "Initial grace seconds", 0, 86_400\)/);
  assert.match(source, /setupTools\.updateEventScoring\(target, input\.dataset\.setupField, input\.value\)/);
  assert.match(source, /caption\.dataset\.setupScoringTitle = title/);
});

test("scan results accept canonical and flexible response shapes without claiming disconnected devices", () => {
  const scan = setup.normalizeScanResponse({
    connected: true,
    completed: true,
    detectedDeviceIds: ["aa:bb:cc:dd:ee:ff"],
    devices: [{ eventId: "event-1", name: "Button", deviceId: "aa:bb:cc:dd:ee:ff", status: "not_responding" }]
  });
  assert.deepEqual(scan.detectedDeviceIds, ["AABBCCDDEEFF"]);
  assert.equal(scan.devices[0].status, "NotResponding");
  assert.equal(setup.scanSummary(scan, true), "Fresh scan completed · 0 devices responding.");

  const disconnected = setup.normalizeScanResponse({ result: { connected: false, completed: false, devices: [] } });
  assert.equal(setup.readiness({ eventId: "event-1", assignmentValue: "AABBCCDDEEFF" }, disconnected, false).key, "unverified");
  assert.match(setup.scanSummary(disconnected, false), /Master disconnected/);
});

test("arm-time snapshot readiness reports missing devices and never keeps green after disconnect", () => {
  const event = { eventId: "event-1", assignmentValue: "AABBCCDDEEFF" };
  const respondingSnapshot = { devices: [{ eventId: "event-1", deviceId: "AABBCCDDEEFF", availability: "online", lastSeenAt: "2026-09-23T12:00:00Z" }] };
  const missingSnapshot = { devices: [{ eventId: "event-1", deviceId: "AABBCCDDEEFF", availability: "offline", lastSeenAt: null }] };

  assert.equal(setup.readinessFromSnapshot(event, respondingSnapshot, true).key, "responding");
  assert.equal(setup.readinessFromSnapshot(event, missingSnapshot, true).key, "not-responding");
  assert.equal(setup.readinessFromSnapshot(event, respondingSnapshot, false).key, "unverified");
  assert.equal(setup.readinessFromSnapshot(event, { devices: [{ ...respondingSnapshot.devices[0], lastSeenAt: null }] }, true).key, "unverified");
  assert.equal(setup.readinessFromSnapshot({ eventId: "event-2", assignmentValue: "" }, respondingSnapshot, true).key, "unassigned");
});

test("current readiness prefers the newest scan, uses arm snapshots, and invalidates green after disconnect", () => {
  const event = { eventId: "event-1", assignmentValue: "AABBCCDDEEFF" };
  const respondingScan = setup.normalizeScanResponse({
    connected: true,
    completed: true,
    devices: [{ eventId: "event-1", deviceId: "AABBCCDDEEFF", status: "Responding" }]
  });
  const missingSnapshot = { devices: [{ eventId: "event-1", deviceId: "AABBCCDDEEFF", availability: "offline" }] };
  const options = { scan: respondingScan, scanFresh: true, masterConnected: true };

  assert.equal(setup.currentReadiness(event, { ...options, snapshot: missingSnapshot, scanIsNewer: true }).key, "responding");
  assert.equal(setup.currentReadiness(event, { ...options, snapshot: missingSnapshot, scanIsNewer: false }).key, "not-responding");
  assert.equal(setup.currentReadiness(event, { ...options, snapshot: missingSnapshot, scanIsNewer: true, masterConnected: false }).key, "unverified");
  assert.equal(setup.currentReadiness(event, { ...options, snapshot: missingSnapshot, scanIsNewer: true, dirty: true }).key, "unverified");
});

test("operator UI derives arm-time readiness from operator snapshot and requires connected master", () => {
  const source = fs.readFileSync(require.resolve("../../src/GarageGames.V2/wwwroot/mvp-scorekeeper.js"), "utf8");
  assert.match(source, /currentReadiness\(configured, \{/);
  assert.match(source, /scanIsNewer: state\.setupScanAt > state\.receivedAt/);
  assert.match(source, /masterConnected: state\.master\?\.connected/);
  assert.match(source, /Latest device status:/);
  assert.match(source, /Physical availability is unverified while the master is disconnected or unavailable/);
});
