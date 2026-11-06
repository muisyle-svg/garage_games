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

test("keypad spokes report typing, submit codes on '*', and flash red on a wrong code", () => {
  const parser = master.match(
    /bool parseGarageKeypadPacket\([\s\S]*?\n}\n\nvoid handleGarageKeypad/
  );
  assert.ok(parser, "dedicated keypad packet parser exists");
  assert.match(parser[0], /GKEY:3:/);
  assert.match(parser[0], /readPacketField\(cursor, packetEnd, entry, entrySize\)/);
  assert.match(parser[0], /validKeypadEntry\(entry\)/);
  assert.match(master, /GG1 KEYPAD %lu %s %s %lu %c %s %lu/);
  assert.match(master, /handleGarageKeypad\(packet\)/);

  assert.match(spoke, /#include <Wire\.h>/);
  assert.match(spoke, /GKEY:3:%s:%lu:K:%s:0/, "typing updates carry the whole entry");
  assert.match(spoke, /GKEY:3:%s:%lu:S:%s/, "submissions are retried like presses");
  assert.match(spoke, /if \(key == '\*'\) \{\s*submitKeypadEntry\(\);/);
  assert.match(spoke, /garagePendingIsKeypad = true;/);
  assert.match(spoke, /flashKeypadWrong\(\);/);
  assert.match(spoke, /strcmp\(garageResultState, "ACTIVE"\) == 0/,
    "keys count only while the spoke's event is running");
  assert.match(spoke, /Wire\.end\(\);/, "spokes without a keypad release the I2C pins");
  assert.match(master, /strcmp\(resultState, "NEXT"\) == 0/, "the master relays NEXT results");
  assert.match(spoke, /strcmp\(text, "NEXT"\) == 0/);
  assert.match(spoke, /flashKeypadCorrect\(\);/, "a right code that is not the last flashes green");
});

test("the Garage bonus round is relayed by the master and lit on spokes, apart from the Speed game", () => {
  assert.match(master, /bool parseBonusLine\(/);
  assert.match(master, /GBONUS:3:%s:%lu:%c:%s:%lu/, "the master rebroadcasts the scorekeeper's bonus state");
  assert.match(master, /GG1 BONUSNODE %lu %s %s/, "poll answers go back to the scorekeeper");
  assert.match(master, /handleBonusHello\(packet\)/);
  assert.match(master, /updateBonusBroadcast\(\);/);

  assert.match(spoke, /void handleBonusState\(const RxPacket& packet\)/);
  assert.match(spoke, /GBHELLO:3:%s:%lu/, "each spoke answers the intro poll");
  assert.match(spoke, /applyTargetColorsForRemaining\(remainingMs\);/, "bonus targets use the Speed game's look");
  assert.match(spoke, /if \(bonusTargetPressable\(now\)\) sendBonusPress\(\);/, "only the lit spoke reports a press");
  assert.match(spoke, /if \(bonusVisualOwnsLed\) return;/, "the bonus round owns the LED while it runs");

  // The standalone Speed game still drives its own targets from the master.
  assert.match(master, /void selectAndCueNextTarget|bool selectAndCueNextTarget/);
  assert.match(spoke, /void updateTargetVisual\(\)/);
});

test("bonus heartbeats let the app swap dead buttons, and a new run forgets the last round", () => {
  assert.match(spoke, /GBHB:3:%s:%lu/, "spokes beat through the bonus round with the target they show");
  assert.match(spoke, /bonusNextBeatMs = now;/, "a newly lit spoke reports at once (its READY)");
  assert.match(master, /GG1 BONUSBEAT %lu %s %s %lu/, "the master relays heartbeats to the app");
  assert.match(master, /handleBonusBeat\(packet\)/);
  assert.match(spoke, /void resetBonusMemory\(\)/);
  const newSession = spoke.match(/if \(!sameSession\) \{[\s\S]*?\n  \}/);
  assert.ok(newSession && /resetBonusMemory\(\);/.test(newSession[0]), "a new run clears bonus memory");
});

test("presses for a paused or just-timed-out run still reach the app, which checks their age", () => {
  const relays = master.match(/const bool runStillAnswering = [^;]+;/g) || [];
  assert.equal(relays.length, 3, "the press, keypad, and arcade relays");
  relays.forEach((relay) => assert.match(relay, /GARAGE_STATUS_PAUSED[\s\S]*GARAGE_STATUS_TIMED_OUT/));
  assert.match(master, /Serial\.setRxBufferSize\(HOST_RX_BUFFER_BYTES\);/, "the master's serial buffer holds a full status burst");
});

test("a waiting press survives state syncs and dropouts instead of being dropped", () => {
  const sync = spoke.match(/void handleGarageEventState\([\s\S]*?\n}\n/);
  assert.ok(sync, "event-state sync handler exists");
  assert.match(sync[0], /eventStateLetter\(eventState\) == garagePressFromState\) return;/,
    "a sync that still shows the pre-press state keeps the press retrying");
  assert.match(spoke, /garagePressFromState = currentEventStateLetter\(\);/);
  assert.match(spoke, /garagePressFromState = 'C';/, "bonus presses are made from a completed event");

  const clear = spoke.match(/void clearGarageMode\([\s\S]*?\n}\n/);
  assert.ok(clear && /holdGarageSession\(millis\(\)\)/.test(clear[0]), "a lost session is held, not forgotten");
  const newSession = spoke.match(/if \(!sameSession\) \{[\s\S]*?\n  \}\n/);
  assert.ok(newSession && /restoreHeldGarageSession\(packet\.receivedAtMs\)/.test(newSession[0]),
    "the same run coming back restores the held session");
  assert.match(spoke, /strcmp\(heldGarage\.token, newToken\) == 0/);
  assert.match(spoke, /GARAGE_HELD_SESSION_MS = 9000;/, "held presses stay inside the app's 10 s press-age limit");
  assert.match(spoke, /clearGarageMode\(true, false\);/, "a Speed discovery does not hold the Garage session");
  assert.match(spoke, /!garageStateFresh\(now\) && holdPressDuringDropout\(now\)/,
    "a press made during a dropout is kept and sent when the run comes back");
  assert.match(spoke, /nextGaragePressRetryMs = now;/, "a restored press is resent at once");
});

test("presses go straight to the master, and spokes report restarts and dropouts", () => {
  const sendPress = spoke.match(/void sendGaragePress\(\) \{[\s\S]*?\n}\n/);
  assert.ok(sendPress && /sendToGarageMaster\(message\);/.test(sendPress[0]));
  assert.match(spoke, /esp_now_send\(garageMasterMac,/, "a direct send gets the radio's acknowledgement and retries");
  assert.match(spoke, /return sendBroadcast\(message\);/, "falls back to a broadcast");
  assert.match(spoke, /GNOTE:3:%s/);
  assert.match(spoke, /case ESP_RST_BROWNOUT: return "BROWNOUT";/);
  assert.match(spoke, /REJOIN:%lu:%u/);
  assert.match(master, /handleGarageNote\(packet\)/);
  assert.match(master, /GG1 NOTE %lu %s %s/);
});
