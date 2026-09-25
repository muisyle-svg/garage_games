const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const masterPath = path.join(
  __dirname,
  "../../firmware/garage_games_master/garage_games_master.ino"
);
const master = fs.readFileSync(masterPath, "utf8");

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
  assert.match(handler[0], /parseGaragePressPacket\(packet, parsedToken, sequence\)/);

  const hostParser = master.match(
    /bool parseGarageStatusLine\([\s\S]*?\n}\n\n/
  );
  assert.ok(hostParser, "space-delimited host serial parser remains present");
  assert.match(hostParser[0], /readProtocolToken/);
});
