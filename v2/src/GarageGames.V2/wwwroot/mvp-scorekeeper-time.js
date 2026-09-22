(() => {
  "use strict";

  function elapsedMsFromRemainingSeconds(value, durationSeconds) {
    if (value === "" || value === null || value === undefined) return null;
    const remainingSeconds = Number(value);
    const duration = Number(durationSeconds);
    if (!Number.isFinite(remainingSeconds) || !Number.isFinite(duration) || remainingSeconds < 0 || remainingSeconds > duration) return null;
    return Math.round((duration - remainingSeconds) * 1000);
  }

  function remainingSecondsFromElapsedMs(elapsedMs, durationSeconds) {
    if (elapsedMs === null || elapsedMs === undefined) return null;
    const elapsed = Number(elapsedMs);
    const duration = Number(durationSeconds) * 1000;
    if (!Number.isFinite(elapsed) || !Number.isFinite(duration)) return null;
    return (duration - elapsed) / 1000;
  }

  function durationMsFromRemainingSeconds(startRemaining, finishRemaining, durationSeconds) {
    const start = elapsedMsFromRemainingSeconds(startRemaining, durationSeconds);
    const finish = elapsedMsFromRemainingSeconds(finishRemaining, durationSeconds);
    return start === null || finish === null ? null : finish - start;
  }

  const api = Object.freeze({
    elapsedMsFromRemainingSeconds,
    remainingSecondsFromElapsedMs,
    durationMsFromRemainingSeconds
  });

  if (typeof module !== "undefined" && module.exports) module.exports = api;
  if (typeof window !== "undefined") window.GarageGamesScorekeeperTime = api;
})();
