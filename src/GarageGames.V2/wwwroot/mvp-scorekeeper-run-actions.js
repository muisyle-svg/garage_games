((root, factory) => {
  const actions = factory();
  if (typeof module === "object" && module.exports) module.exports = actions;
  if (root) root.GarageGamesRunActions = actions;
})(typeof window !== "undefined" ? window : globalThis, () => {
  "use strict";

  // A timed-out run stays discardable until it is recorded.
  const discardableStatuses = new Set(["armed", "countdown", "active", "paused", "finished", "timedOut"]);

  function isDiscardableRun(run) {
    return Boolean(run && run.isRecorded !== true && discardableStatuses.has(run.status));
  }

  function remainingMs(run) {
    const limitSeconds = Number(run?.edition?.durationLimitSeconds);
    if (!Number.isFinite(limitSeconds)) return 0;
    return Math.max(0, limitSeconds * 1000 - Number(run.activeElapsedMs || 0));
  }

  // Only a run that is over is recorded: finished (by the operator, the last event, or the
  // bonus round) or timed out. Recording never ends a run in progress.
  function isRecordableRun(run) {
    return Boolean(run && run.isRecorded !== true && !run.deletedAt && ["finished", "timedOut"].includes(run.status));
  }

  // A timed-out run waits on screen until it is recorded or discarded; the next run (and
  // the Up Next view) can't start before then.
  function blocksNextRun(run) {
    return Boolean(run && run.status === "timedOut" && run.isRecorded !== true && !run.deletedAt);
  }

  // Mirrors the server: an unrecorded finish with time and events left, and no bonus round
  // played, can be reopened.
  function canReopenRun(run) {
    if (!run || run.status !== "finished" || run.isRecorded === true || run.bonusGame) return false;
    const events = run.events || [];
    if (events.length > 0 && events.every((event) => event.status === "completed")) return false;
    return remainingMs(run) > 0;
  }

  return Object.freeze({ isDiscardableRun, isRecordableRun, blocksNextRun, canReopenRun, remainingMs });
});
