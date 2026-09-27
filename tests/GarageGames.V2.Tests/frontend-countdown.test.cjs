const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const countdown = require("../../src/GarageGames.V2/wwwroot/mvp-scorekeeper-countdown.js");
const { createCountdownCoordinator } = countdown;

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

function fakeScheduler() {
  const timers = [];
  return {
    timers,
    schedule(callback, delay) {
      const timer = { callback, delay, cancelled: false };
      timers.push(timer);
      return timer;
    },
    cancel(timer) { timer.cancelled = true; }
  };
}

test("fixed Go time is anchored to the 3.48-second track with a 125ms lead", () => {
  assert.equal(countdown.COUNTDOWN_TRACK_DURATION_MS, 3480);
  assert.equal(countdown.COUNTDOWN_GO_LEAD_MS, 125);
  assert.equal(countdown.COUNTDOWN_GO_AT_MS, 3355);
});

test("countdown starts on the fixed Go timer, independent of the audio ended event", async () => {
  const audio = new FakeAudio();
  const scheduler = fakeScheduler();
  const finishCalls = [];
  const updates = [];
  const coordinator = createCountdownCoordinator({
    readState: async () => ({ runId: "run-1", status: "countdown" }),
    finish: async (runId) => finishCalls.push(runId),
    createAudio: () => audio,
    schedule: scheduler.schedule,
    cancel: scheduler.cancel,
    onChange: (update) => updates.push(update)
  });

  await coordinator.poll();
  coordinator.observe({ runId: "run-1", status: "countdown" });
  await flush();
  assert.equal(audio.playCalls, 1);
  assert.equal(scheduler.timers.length, 1);
  assert.equal(scheduler.timers[0].delay, 3355);
  assert.deepEqual(finishCalls, []);
  assert.equal(updates.some((update) => update.playback === "playing"), true);

  audio.emit("ended");
  await flush();
  assert.deepEqual(finishCalls, [], "audio completion must not determine the run start");
  scheduler.timers[0].callback();
  await flush();
  assert.deepEqual(finishCalls, ["run-1"]);
  assert.equal(updates.at(-1).status, "active");
});

test("late observation uses server countdown elapsed time for both audio seek and remaining Go delay", async () => {
  const audio = new FakeAudio();
  const scheduler = fakeScheduler();
  const finishCalls = [];
  const coordinator = createCountdownCoordinator({
    readState: async () => ({ runId: "run-late", status: "countdown", elapsedMilliseconds: 800 }),
    finish: async (runId) => finishCalls.push(runId),
    createAudio: () => audio,
    schedule: scheduler.schedule,
    cancel: scheduler.cancel
  });

  await coordinator.poll();
  assert.equal(scheduler.timers.length, 1);
  assert.equal(scheduler.timers[0].delay, 2555);
  assert.equal(audio.currentTime, 0.8);
  scheduler.timers[0].callback();
  await flush();
  assert.deepEqual(finishCalls, ["run-late"]);
});

test("a countdown first observed after Go activates immediately without replaying late audio", async () => {
  const scheduler = fakeScheduler();
  const finishCalls = [];
  let playCalls = 0;
  const coordinator = createCountdownCoordinator({
    readState: async () => ({ runId: "run-after-go", status: "countdown", elapsedMilliseconds: 3400 }),
    finish: async (runId) => finishCalls.push(runId),
    createAudio: () => { playCalls += 1; return new FakeAudio(); },
    schedule: scheduler.schedule,
    cancel: scheduler.cancel
  });

  await coordinator.poll();
  await flush();
  assert.equal(scheduler.timers[0].delay, 0);
  assert.equal(scheduler.timers[0].cancelled, true);
  assert.equal(playCalls, 0);
  assert.deepEqual(finishCalls, ["run-after-go"]);
});

test("blocked audio still activates the run at the fixed Go time", async () => {
  const audio = new FakeAudio(Promise.reject(new Error("Playback was denied.")));
  const scheduler = fakeScheduler();
  const finishCalls = [];
  const updates = [];
  const coordinator = createCountdownCoordinator({
    readState: async () => ({ runId: "run-blocked", status: "countdown" }),
    finish: async (runId) => finishCalls.push(runId),
    createAudio: () => audio,
    schedule: scheduler.schedule,
    cancel: scheduler.cancel,
    onChange: (update) => updates.push(update)
  });

  coordinator.observe({ runId: "run-blocked", status: "countdown" });
  await flush();
  assert.equal(updates.some((update) => update.playback === "failed"), true);
  assert.deepEqual(finishCalls, []);
  scheduler.timers[0].callback();
  await flush();
  assert.deepEqual(finishCalls, ["run-blocked"]);
  assert.equal(updates.at(-1).status, "active");
});

test("missing audio still schedules the run start", async () => {
  const scheduler = fakeScheduler();
  const finishCalls = [];
  const coordinator = createCountdownCoordinator({
    readState: async () => ({ runId: "run-missing-audio", status: "countdown" }),
    finish: async (runId) => finishCalls.push(runId),
    createAudio: () => { throw new Error("File unavailable"); },
    schedule: scheduler.schedule,
    cancel: scheduler.cancel
  });

  coordinator.observe({ runId: "run-missing-audio", status: "countdown" });
  await flush();
  assert.equal(scheduler.timers.length, 1);
  scheduler.timers[0].callback();
  await flush();
  assert.deepEqual(finishCalls, ["run-missing-audio"]);
});

test("failed Go request can be retried after the fixed countdown expires", async () => {
  const scheduler = fakeScheduler();
  const finishCalls = [];
  const coordinator = createCountdownCoordinator({
    readState: async () => ({ runId: "run-retry", status: "countdown" }),
    finish: async () => {
      finishCalls.push("run-retry");
      if (finishCalls.length === 1) throw new Error("Temporary server failure");
    },
    createAudio: () => new FakeAudio(),
    schedule: scheduler.schedule,
    cancel: scheduler.cancel
  });

  coordinator.observe({ runId: "run-retry", status: "countdown" });
  scheduler.timers[0].callback();
  await flush();
  assert.equal(coordinator.retry(), true);
  await flush();
  assert.deepEqual(finishCalls, ["run-retry", "run-retry"]);
});

test("leaving countdown cancels scheduled Go and stops pending audio", async () => {
  const firstAudio = new FakeAudio(Promise.reject(new Error("Playback was denied.")));
  const retryAudio = new FakeAudio();
  const audios = [firstAudio, retryAudio];
  const scheduler = fakeScheduler();
  const finishCalls = [];
  const updates = [];
  const coordinator = createCountdownCoordinator({
    readState: async () => ({ runId: "run-2", status: "countdown" }),
    finish: async (runId) => finishCalls.push(runId),
    createAudio: () => audios.shift(),
    schedule: scheduler.schedule,
    cancel: scheduler.cancel,
    onChange: (update) => updates.push(update)
  });

  coordinator.observe({ runId: "run-2", status: "countdown" });
  await flush();
  assert.equal(updates.at(-1).playback, "failed");
  assert.equal(coordinator.retry(), true);
  assert.equal(retryAudio.playCalls, 1);
  coordinator.observe({ runId: "run-2", status: "aborted" });
  assert.equal(scheduler.timers[0].cancelled, true);
  scheduler.timers[0].callback();
  await flush();
  assert.deepEqual(finishCalls, []);
});
