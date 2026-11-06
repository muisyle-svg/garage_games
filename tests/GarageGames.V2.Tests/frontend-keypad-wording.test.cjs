const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");

const source = fs.readFileSync(require.resolve("../../src/GarageGames.V2/wwwroot/app.js"), "utf8");
const start = source.indexOf("  function keypadWording(eventName) {");
const end = source.indexOf("  function renderKeypadChallenge(challenge) {");
const keypadWording = new Function(`${source.slice(start, end)}; return keypadWording;`)();

test("Wall-D's TV panel talks about discs and coordinates", () => {
  for (const name of ["Wall-D", "Wall D", "WALLD", " wall-d "]) {
    const wording = keypadWording(name);
    assert.equal(wording.found(1, 3), "1 of 3 discs found");
    assert.equal(wording.found(0, 1), "0 of 1 disc found");
    assert.equal(wording.enter, "Enter the coordinates, then press ✱");
    assert.equal(wording.wrong, "Wrong coordinates — try again");
  }
});

test("other keypad events keep codes", () => {
  for (const name of ["Keypad", "Wall-E", "Walldorf", "", null]) {
    const wording = keypadWording(name);
    assert.equal(wording.found(2, 3), "2 of 3 codes solved");
    assert.equal(wording.enter, "Enter the code, then press ✱");
    assert.equal(wording.wrong, "Wrong code — try again");
  }
});
