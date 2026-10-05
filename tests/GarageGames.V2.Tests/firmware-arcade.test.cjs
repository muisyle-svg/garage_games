const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const master = fs.readFileSync(
  path.join(__dirname, "../../firmware/garage_games_master/garage_games_master.ino"),
  "utf8"
);

test("master relays Chaos Heist arcade packets as GG1 ARCADE lines with their age", () => {
  const parser = master.match(
    /bool parseGarageArcadePacket\([\s\S]*?\n}\n\nvoid handleGarageArcade/
  );
  assert.ok(parser, "dedicated arcade packet parser exists");
  assert.match(parser[0], /"GARC:3:"/);
  assert.match(parser[0], /strlen\(tokenText\) != 16/);
  assert.match(parser[0], /sequence == 0/, "sequence 0 is rejected like presses");
  assert.match(parser[0], /kind == 'S' \|\| kind == 'F'/, "only start and finish signals exist");
  assert.match(parser[0], /cursor != packetEnd/, "trailing fields are rejected");

  const handler = master.match(/void handleGarageArcade\([\s\S]*?\n}\n\nvoid processRx/);
  assert.ok(handler, "arcade relay handler exists");
  assert.match(handler[0], /GG1 ARCADE %lu %s %s %lu %c %lu/);
  assert.match(handler[0], /strcmp\(parsedToken, garageToken\) != 0/, "only the current run's token is relayed");
  assert.match(handler[0], /hostStatusFresh\(now\)/, "nothing is relayed while the app is silent");
  assert.match(handler[0], /now - packet\.receivedAtMs/, "master adds its relay delay to the signal age");
  assert.match(handler[0], /memcmp\(packet\.source, masterMac, 6\) == 0/);

  const dispatch = master.match(/void processRx\(\) \{[\s\S]*?\n}\n/);
  assert.ok(dispatch);
  assert.match(dispatch[0], /strncmp\(packet\.data, "GARC:", 5\) == 0[\s\S]*?handleGarageArcade\(packet\)/);
});
