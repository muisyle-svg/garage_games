const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const masterPath = path.join(
  __dirname,
  "../../firmware/garage_games_master/garage_games_master.ino"
);
const master = fs.readFileSync(masterPath, "utf8");
const spokePath = path.join(
  __dirname,
  "../../firmware/garage_games_spoke/garage_games_spoke.ino"
);
const spoke = fs.readFileSync(spokePath, "utf8");

test("master parses bounded colon-delimited spoke presses separately from host tokens", () => {
  const parser = master.match(
    /bool parseGaragePressPacket\([\s\S]*?\n}\n\nvoid handleGaragePress/
  );
  assert.ok(parser, "dedicated spoke press parser exists");
  assert.match(parser[0], /memchr\(tokenStart, ':',/);
  assert.match(parser[0], /tokenEnd - tokenStart != 16/);
  assert.match(parser[0], /packetEnd - sequenceStart/);
  assert.match(parser[0], /parseUint32Token\(sequenceText, sequence\)/);
  const executableParser = parser[0].replace(/\/\/.*$/gm, "");
  assert.doesNotMatch(executableParser, /readProtocolToken\s*\(/);

  const handler = master.match(
    /void handleGaragePress\([\s\S]*?\n}\n\nvoid processRx/
  );
  assert.ok(handler, "spoke press handler exists");
  assert.match(handler[0], /parseGaragePressPacket\(packet, parsedToken, sequence, pressAgeMs\)/);
  assert.match(handler[0], /GG1 PRESS %lu %s %s %lu %lu/, "the relayed press carries its age");
  assert.match(handler[0], /now - packet\.receivedAtMs/, "master adds its own relay delay to the press age");
  assert.match(parser[0], /parseUint32Token\(ageText, pressAgeMs\)/);

  const hostParser = master.match(
    /bool parseGarageStatusLine\([\s\S]*?\n}\n\n/
  );
  assert.ok(hostParser, "space-delimited host serial parser remains present");
  assert.match(hostParser[0], /readProtocolToken/);
});

test("spoke press sequences survive a reboot and report press age", () => {
  assert.doesNotMatch(spoke, /\n\s+garagePressSequence = 0;/,
    "a reset to 0 lets a rebooted spoke reuse sequences the host already recorded");
  assert.match(spoke, /garagePressSequence = randomGarageSequenceBase\(\);/);
  assert.match(spoke, /esp_random\(\) & 0x3FFFFFFFUL/);
  assert.match(spoke, /garagePressAtMs = now;/);
  assert.match(spoke, /millis\(\) - garagePressAtMs/);
  assert.doesNotMatch(spoke, /sendBroadcast\(pendingGaragePressMessage\)/,
    "retries must recompute the press age instead of resending a fixed message");
});

test("idle spoke presses and virtual identification are relayed through the master", () => {
  assert.match(master, /bool parseIdentifyCommand\(/);
  assert.match(master, /GG1 IDENTIFY/);
  assert.match(master, /GIDENTIFY:%u:%s:%lu/);
  assert.match(master, /GG1 TEST %lu %s %lu/);
  assert.match(master, /handleButtonTest\(packet\)/);
  assert.match(spoke, /void handleIdentify\(const RxPacket& packet\)/);
  assert.match(spoke, /macEqual\(targetMac, ownMac\)/);
  assert.match(spoke, /GTEST:%u:%s:%lu/);
  assert.match(spoke, /startBlink\(true, false, true, 120, 850\)/);
  assert.match(spoke, /else if \(key == 10\) startBlink\(true, true, false, 500\)/,
    "an in-progress event flashes yellow rather than holding a solid yellow LED");
});

test("authoritative event-state sync lets virtual actions and undo reset physical spokes", () => {
  assert.match(master, /bool parseGarageEventLine\(/);
  assert.match(master, /GG1 EVENT/);
  assert.match(master, /GSTATE:3:%s:%lu:%s:%s/);
  assert.match(spoke, /void handleGarageEventState\(const RxPacket& packet\)/);
  assert.match(spoke, /garagePressAwaitingResult = false;/);
  assert.match(spoke, /garageCompleted = strcmp\(eventState, "COMPLETED"\) == 0/);
  assert.match(spoke, /revision <= garageEventRevision/);
  assert.match(spoke, /garageEventRevision = revision;/);
});
