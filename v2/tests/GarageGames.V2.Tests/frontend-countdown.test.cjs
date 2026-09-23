const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const { createCountdownCoordinator } = require("../../src/GarageGames.V2/wwwroot/mvp-scorekeeper-countdown.js");

class FakeAudio {
  constructor(playResult = Promise.resolve()) {
    this.listeners = new Map();
    this.playResult = playResult;
    this.playCalls = 0;
    this.pauseCalls = 0;
    this.currentTime = 0;
  }

  addEventListener(name, callback) {
    const listeners = this.listeners.get(name) || [];
    listeners.push(callback);
    this.listeners.set(name, listeners);
  }

  play() {
    this.playCalls += 1;
    return this.playResult;
  }

  pause() { this.pauseCalls += 1; }

  emit(name) {
    for (const listener of this.listeners.get(name) || []) listener();
  }
}

const flush = () => new Promise((resolve) => setImmediate(resolve));

test("hidden countdown controls stay hidden despite shared button display styles", () => {
  const css = fs.readFileSync(require.resolve("../../src/GarageGames.V2/wwwroot/mvp-scorekeeper.css"), "utf8");
  assert.match(css, /\.countdown-audio-notice\s+\[hidden\]\s*\{\s*display:\s*none\s*!important;\s*\}/);
});

test("countdown plays once and finishes the run only after the audio ended event", async () => {
  const audio = new FakeAudio();
  const finishCalls = [];
  const updates = [];
  const coordinator = createCountdownCoordinator({
    readState: async () => ({ runId: "run-1", status: "countdown" }),
    finish: async (runId) => finishCalls.push(runId),
    createAudio: () => audio,
    onChange: (update) => updates.push(update)
  });

  await coordinator.poll();
  coordinator.observe({ runId: "run-1", status: "countdown" });
  await flush();
  assert.equal(audio.playCalls, 1);
  assert.deepEqual(finishCalls, []);
  assert.equal(updates.some((update) => update.playback === "playing"), true);

  audio.emit("ended");
  await flush();
  assert.deepEqual(finishCalls, ["run-1"]);
  assert.equal(updates.at(-1).status, "active");
});

test("rejected playback stays in countdown and can be retried by the operator", async () => {
  const firstAudio = new FakeAudio(Promise.reject(new Error("Playback was denied.")));
  const retryAudio = new FakeAudio();
  const audios = [firstAudio, retryAudio];
  const finishCalls = [];
  const updates = [];
  const coordinator = createCountdownCoordinator({
    readState: async () => ({ runId: "run-2", status: "countdown" }),
    finish: async (runId) => finishCalls.push(runId),
    createAudio: () => audios.shift(),
    onChange: (update) => updates.push(update)
  });

  coordinator.observe({ runId: "run-2", status: "countdown" });
  await flush();
  assert.equal(updates.at(-1).playback, "failed");
  assert.equal(finishCalls.length, 0);
  assert.equal(coordinator.retry(), true);
  retryAudio.emit("ended");
  await flush();
  assert.deepEqual(finishCalls, ["run-2"]);
});

test("leaving countdown stops pending audio and never posts completion", async () => {
  const audio = new FakeAudio();
  const finishCalls = [];
  const coordinator = createCountdownCoordinator({
    readState: async () => ({ runId: "run-3", status: "countdown" }),
    finish: async (runId) => finishCalls.push(runId),
    createAudio: () => audio
  });

  coordinator.observe({ runId: "run-3", status: "countdown" });
  coordinator.observe({ runId: "run-3", status: "aborted" });
  audio.emit("ended");
  await flush();
  assert.equal(audio.pauseCalls, 1);
  assert.deepEqual(finishCalls, []);
});
