((root, factory) => {
  const actions = factory();
  if (typeof module === "object" && module.exports) module.exports = actions;
  if (root) root.GarageGamesMasterActions = actions;
})(typeof window !== "undefined" ? window : globalThis, () => {
  "use strict";

  const liveStatuses = new Set(["armed", "countdown", "active", "paused", "finished"]);
  const DEFAULT_DURATION_SECONDS = 300;
  const MAX_DURATION_SECONDS = 5999;

  function parseRunDuration(value) {
    const match = String(value ?? "").trim().match(/^(\d{1,2}):([0-5]\d)$/);
    if (!match) return null;
    const seconds = Number(match[1]) * 60 + Number(match[2]);
    return seconds > 0 && seconds <= MAX_DURATION_SECONDS ? seconds : null;
  }

  function formatRunDuration(value) {
    const seconds = Number(value);
    if (!Number.isInteger(seconds) || seconds < 1 || seconds > MAX_DURATION_SECONDS) return "5:00";
    return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
  }

  function validatedDuration(value = DEFAULT_DURATION_SECONDS) {
    const seconds = Number(value);
    if (!Number.isInteger(seconds) || seconds < 1 || seconds > MAX_DURATION_SECONDS) {
      throw new Error("Run length must be a whole number of seconds from 1 to 5,999.");
    }
    return seconds;
  }

  function confirmArmedDuration(run, requestedSeconds) {
    const actualSeconds = Number(run?.edition?.durationLimitSeconds);
    if (!run || !Number.isInteger(actualSeconds) || actualSeconds < 1) {
      throw new Error("The scorekeeper did not confirm the saved run length. Reload the app before starting this run.");
    }
    if (actualSeconds !== requestedSeconds) {
      throw new Error(`Run length mismatch: you selected ${formatRunDuration(requestedSeconds)}, but the scorekeeper armed ${formatRunDuration(actualSeconds)}. Do not start it; discard this armed run, reload the app, and try again.`);
    }
    return run;
  }

  function isSpeedMode(master) {
    return String(master?.mode || "").toUpperCase() === "SPEED";
  }

  function isLiveRun(run) {
    return Boolean(run && liveStatuses.has(run.status));
  }

  function isRunDurationLocked(run) {
    return isLiveRun(run);
  }

  function shouldResetRunDuration(run, lastResetRunId) {
    return Boolean(run?.id && !isRunDurationLocked(run) && run.id !== lastResetRunId);
  }

  function handshakeGuidance(master) {
    return master?.connected && master.mode !== "IDLE" && !isSpeedMode(master)
      ? "Waiting for the physical master handshake (GG1 HELLO and MODE IDLE). Physical arming is disabled; virtual Start remains available."
      : "";
  }

  function canArmPhysical(master, currentRun, competitorId) {
    return Boolean(master?.connected && master.mode === "IDLE" && !isLiveRun(currentRun) && competitorId);
  }

  function canStartVirtual(master, currentRun, competitorId) {
    return Boolean(!isSpeedMode(master) && (currentRun?.status === "armed" || (!isLiveRun(currentRun) && competitorId)));
  }

  function assertStartAllowed(master) {
    if (isSpeedMode(master)) {
      throw new Error("Arming and Start are disabled while the physical master is in SPEED mode.");
    }
  }

  async function connectMaster(request, port) {
    if (!port) throw new Error("Select a COM port before connecting the physical master.");
    return request("/api/master/connect", {
      method: "POST",
      body: JSON.stringify({ port })
    });
  }

  async function disconnectMaster(request) {
    return request("/api/master/disconnect", { method: "POST" });
  }

  // The competitor's counted official result in the current edition, if any. Arming
  // another official run for them is an official redo that replaces it once recorded.
  function findRecordedOfficial(snapshot, competitorId) {
    if (!snapshot || !competitorId) return null;
    return (snapshot.history || []).find((run) =>
      run.competitorId === competitorId &&
      run.category === "official" &&
      run.editionId === snapshot.editionId &&
      run.isRecorded &&
      !run.supersededByRunId &&
      run.status !== "aborted" && run.status !== "superseded") || null;
  }

  function armBody(competitorId, category, durationLimitSeconds, replaceExistingOfficial) {
    return JSON.stringify(replaceExistingOfficial
      ? { competitorId, category, durationLimitSeconds, replaceExistingOfficial: true }
      : { competitorId, category, durationLimitSeconds });
  }

  async function armForPhysicalStart(request, { master, currentRun, competitorId, category, durationLimitSeconds = DEFAULT_DURATION_SECONDS, replaceExistingOfficial = false }) {
    assertStartAllowed(master);
    if (!master?.connected) throw new Error("Connect the physical master before arming a physical Start.");
    const guidance = handshakeGuidance(master);
    if (guidance) throw new Error(guidance);
    if (isLiveRun(currentRun)) throw new Error("Finish or discard the current run before arming another.");
    if (!competitorId) throw new Error("Choose a competitor before arming a physical Start.");
    const selectedDuration = validatedDuration(durationLimitSeconds);
    const armed = await request("/api/run/arm", {
      method: "POST",
      body: armBody(competitorId, category, selectedDuration, replaceExistingOfficial)
    });
    return confirmArmedDuration(armed, selectedDuration);
  }

  async function startVirtually(request, { master, currentRun, competitorId, category, durationLimitSeconds = DEFAULT_DURATION_SECONDS, replaceExistingOfficial = false }, afterArm = async () => {}) {
    assertStartAllowed(master);
    if (currentRun?.status === "armed") {
      competitorId = currentRun.competitorId;
      category = currentRun.category;
    } else {
      if (isLiveRun(currentRun)) throw new Error("Finish or discard the current run before starting another.");
      if (!competitorId) throw new Error("Choose a competitor before starting a run.");
      const selectedDuration = validatedDuration(durationLimitSeconds);
      const armed = await request("/api/run/arm", {
        method: "POST",
        body: armBody(competitorId, category, selectedDuration, replaceExistingOfficial)
      });
      confirmArmedDuration(armed, selectedDuration);
      await afterArm();
    }

    const run = await request("/api/run/start", { method: "POST" });
    return { competitorId, category, ...(run && typeof run === "object" ? { run } : {}) };
  }

  return Object.freeze({
    isSpeedMode,
    isRunDurationLocked,
    shouldResetRunDuration,
    parseRunDuration,
    formatRunDuration,
    confirmArmedDuration,
    handshakeGuidance,
    canArmPhysical,
    canStartVirtual,
    findRecordedOfficial,
    connectMaster,
    disconnectMaster,
    armForPhysicalStart,
    startVirtually
  });
});
