const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");

const root = "../../src/GarageGames.V2/wwwroot";
const source = fs.readFileSync(require.resolve(`${root}/mvp-scorekeeper.js`), "utf8");
const html = fs.readFileSync(require.resolve(`${root}/index.html`), "utf8");

function extractFunction(name) {
  const prefix = `  function ${name}(`;
  const start = source.indexOf(prefix);
  assert.notEqual(start, -1, `${name} should exist`);
  const end = source.indexOf("\n  }", start);
  assert.notEqual(end, -1, `${name} should have a closing brace`);
  return source.slice(start, end + 4).trim();
}

test("competitor import reads first-column names, headers, and quoted commas", () => {
  const parse = vm.runInNewContext(`(${extractFunction("parseCompetitorCsv")})`);
  assert.deepEqual(Array.from(parse('Name,Group\r\n"Alex, Jr.",Red\r\nSam,Blue\r\n')), ["Alex, Jr.", "Sam"]);
});

test("duplicate matching ignores case and repeated whitespace", () => {
  const normalize = vm.runInNewContext(`(${extractFunction("normalizedCompetitorName")})`);
  assert.equal(normalize("  Suzy   Q "), normalize("suzy q"));
});

test("scorekeeper keeps roster management archive-aware and removes player search", () => {
  for (const id of ["show-archived-competitors", "competitor-roster-list", "competitor-import-form", "competitor-select", "on-deck-competitor-select"]) {
    assert.match(html, new RegExp(`id="${id}"`));
  }
  for (const id of ["competitor-search", "on-deck-competitor-search", "competitor-roster-search"]) {
    assert.doesNotMatch(html, new RegExp(`id="${id}"`));
  }
  assert.match(source, /Their existing run history will stay linked/);
  assert.match(source, /Nothing will be deleted/);
});

test("setup review and button readiness explain event order and scan freshness", () => {
  assert.match(html, /id="setup-preview-list"/);
  assert.match(source, /order shown is the scorekeeper button order/);
  assert.match(source, /point-in-time, not live/);
  assert.match(source, /Last checked/);
});

test("scorekeeper exposes audited press recovery, physical-button identification, and leaderboard categories", () => {
  for (const id of ["undo-last-event-press", "show-exhibitions-on-leaderboard"]) {
    assert.match(html, new RegExp(`id="${id}"`));
  }
  assert.match(html, /scope="col">Actions/);
  assert.match(source, /api\/run\/undo-last-press/);
  assert.match(source, /api\/master\/identify/);
  assert.match(source, /data-clear-event/);
  assert.match(source, /is-physical-press/);
  assert.match(source, /is-started/);
  assert.match(source, /api\/leaderboards\/preferences/);
  assert.match(html, /Playoff scores are listed first/);
});
