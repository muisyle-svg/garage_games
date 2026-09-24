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

  function elapsedMsForDraft(originalElapsedMs, touched, value, durationSeconds) {
    if (!touched) return originalElapsedMs === null || originalElapsedMs === undefined ? null : Number(originalElapsedMs);
    if (value === "") return null;
    return elapsedMsFromRemainingSeconds(value, durationSeconds);
  }

  function previewEventScore(result, scoring = {}, eventDefinition = null) {
    if (result?.scoreOverride !== null && result?.scoreOverride !== undefined) {
      const override = Number(result.scoreOverride);
      return Number.isFinite(override) ? override : 0;
    }
    if (scoring.manualEventPoints) return 0;
    if (result?.status !== "completed" || result.startElapsedMs === null || result.startElapsedMs === undefined ||
        result.finishElapsedMs === null || result.finishElapsedMs === undefined) return 0;

    const durationMs = Number(result.finishElapsedMs) - Number(result.startElapsedMs);
    const hasEventStartingPoints = eventDefinition?.basePoints !== null && eventDefinition?.basePoints !== undefined;
    const basePoints = Number(hasEventStartingPoints ? eventDefinition.basePoints : scoring.basePoints ?? 50);
    const minimumPoints = Number(eventDefinition?.minimumPoints !== null && eventDefinition?.minimumPoints !== undefined
      ? eventDefinition.minimumPoints
      : hasEventStartingPoints ? Math.ceil(basePoints / 2) : scoring.minimumPoints ?? 25);
    const decayEverySeconds = Number(eventDefinition?.decayEverySeconds ?? scoring.decayEverySeconds ?? 5);
    const decayPoints = Number(eventDefinition?.decayPoints ?? scoring.decayPoints ?? 5);
    const graceSeconds = Number(eventDefinition?.graceSeconds ?? 0);
    if (!Number.isFinite(durationMs) || durationMs < 0 || !Number.isFinite(decayEverySeconds) || decayEverySeconds <= 0 ||
        !Number.isFinite(basePoints) || !Number.isFinite(decayPoints) || decayPoints < 0 ||
        !Number.isFinite(minimumPoints) || !Number.isFinite(graceSeconds) || graceSeconds < 0) return 0;

    const intervalMs = decayEverySeconds * 1000;
    const fullDecaySteps = graceSeconds > 0
      ? durationMs < graceSeconds * 1000 ? 0 : 1 + Math.floor((durationMs - graceSeconds * 1000) / intervalMs)
      : Math.floor(durationMs / intervalMs);
    return Math.max(minimumPoints, basePoints - fullDecaySteps * decayPoints);
  }

  function eventScoreDraftView({
    scoreTouched = false,
    scoreValue = "",
    timingTouched = false,
    previewScore = 0,
    persistedScore = 0
  } = {}) {
    const preview = Number(previewScore);
    const safePreview = Number.isFinite(preview) && preview >= 0 ? preview : 0;
    if (scoreTouched) {
      if (scoreValue === "") return { inputValue: "", totalPoints: safePreview };
      const typed = Number(scoreValue);
      return {
        inputValue: String(scoreValue),
        totalPoints: Number.isFinite(typed) && typed >= 0 ? typed : 0
      };
    }
    if (timingTouched) return { inputValue: String(safePreview), totalPoints: safePreview };
    const persisted = Number(persistedScore ?? 0);
    return {
      inputValue: String(persistedScore ?? 0),
      totalPoints: Number.isFinite(persisted) && persisted >= 0 ? persisted : 0
    };
  }

  const api = Object.freeze({
    parseClockTimeToSeconds,
    formatClockMs,
    millisecondsFromClockTime,
    elapsedMsFromRemainingSeconds,
    remainingSecondsFromElapsedMs,
    durationMsFromRemainingSeconds,
    elapsedMsForDraft,
    previewEventScore,
    eventScoreDraftView
  });

  if (typeof module !== "undefined" && module.exports) module.exports = api;
  if (typeof window !== "undefined") window.GarageGamesScorekeeperTime = api;
})();
