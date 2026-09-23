(() => {
  "use strict";

  function parseClockTimeToSeconds(value) {
    if (value === "" || value === null || value === undefined) return null;
    if (typeof value === "string") {
      const input = value.trim();
      if (!input) return null;
      const clockMatch = /^(\d+):([0-5]\d)$/.exec(input);
      if (clockMatch) return Number(clockMatch[1]) * 60 + Number(clockMatch[2]);
      value = input;
    }
    const seconds = Number(value);
    return Number.isFinite(seconds) && seconds >= 0 ? seconds : null;
  }

  function formatClockMs(milliseconds) {
    if (milliseconds === null || milliseconds === undefined || milliseconds === "") return "";
    const value = Number(milliseconds);
    if (!Number.isFinite(value)) return "";
    const totalSeconds = Math.max(0, Math.round(value / 1000));
    return `${Math.floor(totalSeconds / 60)}:${String(totalSeconds % 60).padStart(2, "0")}`;
  }

  function millisecondsFromClockTime(value) {
    const seconds = parseClockTimeToSeconds(value);
    return seconds === null ? null : Math.round(seconds * 1000);
  }

  function elapsedMsFromRemainingSeconds(value, durationSeconds) {
    const remainingSeconds = parseClockTimeToSeconds(value);
    const duration = Number(durationSeconds);
    if (remainingSeconds === null || !Number.isFinite(duration) || remainingSeconds > duration) return null;
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
    parseClockTimeToSeconds,
    formatClockMs,
    millisecondsFromClockTime,
    elapsedMsFromRemainingSeconds,
    remainingSecondsFromElapsedMs,
    durationMsFromRemainingSeconds
  });

  if (typeof module !== "undefined" && module.exports) module.exports = api;
  if (typeof window !== "undefined") window.GarageGamesScorekeeperTime = api;
})();
