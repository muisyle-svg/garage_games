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

  return Object.freeze({ isDiscardableRun });
});
