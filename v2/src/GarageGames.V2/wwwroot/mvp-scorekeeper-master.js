((root, factory) => {
  const actions = factory();
  if (typeof module === "object" && module.exports) module.exports = actions;
  if (root) root.GarageGamesMasterActions = actions;
})(typeof window !== "undefined" ? window : globalThis, () => {
  "use strict";

  const liveStatuses = new Set(["armed", "countdown", "active", "paused", "finished"]);

  function isSpeedMode(master) {
    return String(master?.mode || "").toUpperCase() === "SPEED";
  }

  function isLiveRun(run) {
    return Boolean(run && liveStatuses.has(run.status));
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

  async function armForPhysicalStart(request, { master, currentRun, competitorId, category }) {
    assertStartAllowed(master);
    if (!master?.connected) throw new Error("Connect the physical master before arming a physical Start.");
    const guidance = handshakeGuidance(master);
    if (guidance) throw new Error(guidance);
    if (isLiveRun(currentRun)) throw new Error("Finish or discard the current run before arming another.");
    if (!competitorId) throw new Error("Choose a competitor before arming a physical Start.");
    return request("/api/run/arm", {
      method: "POST",
      body: JSON.stringify({ competitorId, category })
    });
  }

  async function startVirtually(request, { master, currentRun, competitorId, category }, afterArm = async () => {}) {
    assertStartAllowed(master);
    if (currentRun?.status === "armed") {
      competitorId = currentRun.competitorId;
      category = currentRun.category;
    } else {
      if (isLiveRun(currentRun)) throw new Error("Finish or discard the current run before starting another.");
      if (!competitorId) throw new Error("Choose a competitor before starting a run.");
      await request("/api/run/arm", {
        method: "POST",
        body: JSON.stringify({ competitorId, category })
      });
      await afterArm();
    }

    const run = await request("/api/run/start", { method: "POST" });
    return { competitorId, category, ...(run && typeof run === "object" ? { run } : {}) };
  }

  return Object.freeze({
    isSpeedMode,
    handshakeGuidance,
    canArmPhysical,
    canStartVirtual,
    connectMaster,
    disconnectMaster,
    armForPhysicalStart,
    startVirtually
  });
});
