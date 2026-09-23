((root, factory) => {
  const actions = factory();
  if (typeof module === "object" && module.exports) module.exports = actions;
  if (root) root.GarageGamesRunActions = actions;
})(typeof window !== "undefined" ? window : globalThis, () => {
  "use strict";

  const discardableStatuses = new Set(["armed", "active", "paused", "finished"]);

  function isDiscardableRun(run) {
    return Boolean(run && run.isRecorded !== true && discardableStatuses.has(run.status));
  }

  return Object.freeze({ isDiscardableRun });
});
