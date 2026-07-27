#!/usr/bin/env node
import assert from "node:assert/strict";

const stations = 20;
const lossRate = Number(process.env.GG_SIM_LOSS ?? 0.15);
const duplicateRate = Number(process.env.GG_SIM_DUPLICATES ?? 0.2);
const maximumAttempts = 50;
const accepted = new Map();
let transmissions = 0;
let duplicates = 0;

function random() {
  // Deterministic xorshift so CI failures reproduce.
  seed ^= seed << 13;
  seed ^= seed >>> 17;
  seed ^= seed << 5;
  return (seed >>> 0) / 0x1_0000_0000;
}

let seed = 0x2026_1024;
const events = [];
for (let station = 1; station <= stations; station++) {
  events.push({ id: `station-${station}:attempt:1`, station, type: "attempt_started" });
  events.push({ id: `station-${station}:complete:2`, station, type: "game_completed" });
}

for (const event of events) {
  let acknowledged = false;
  for (let attempt = 1; attempt <= maximumAttempts && !acknowledged; attempt++) {
    transmissions++;
    if (random() < lossRate) continue;
    if (accepted.has(event.id)) duplicates++;
    accepted.set(event.id, event);
    acknowledged = random() >= lossRate;
    if (random() < duplicateRate) {
      transmissions++;
      duplicates++;
      accepted.set(event.id, event);
    }
  }
  assert.equal(acknowledged, true, `event ${event.id} was not acknowledged`);
}

assert.equal(accepted.size, events.length, "deduplication changed accepted event count");
for (let station = 1; station <= stations; station++) {
  const stationEvents = [...accepted.values()].filter(event => event.station === station);
  assert.deepEqual(stationEvents.map(event => event.type), ["attempt_started", "game_completed"]);
}

console.log(JSON.stringify({
  ok: true,
  stations,
  uniqueEvents: accepted.size,
  transmissions,
  duplicates,
  lossRate,
  duplicateRate
}, null, 2));

