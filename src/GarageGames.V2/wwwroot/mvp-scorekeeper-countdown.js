(() => {
  "use strict";

  const COUNTDOWN_TRACK_DURATION_MS = 3480;
  const COUNTDOWN_GO_LEAD_MS = 125;
  const COUNTDOWN_GO_AT_MS = COUNTDOWN_TRACK_DURATION_MS - COUNTDOWN_GO_LEAD_MS;

  function createCountdownCoordinator({
    readState,
    finish,
    createAudio,
    onChange = () => {},
    schedule = setTimeout,
    cancel = clearTimeout,
    goAtMs = COUNTDOWN_GO_AT_MS
  }) {
    let runId = null;
    let audio = null;
    let goTimer = null;
    let playback = "idle";
    let goDue = false;
    let finishing = false;
    let polling = false;

    function notify(status, error = "") {
      onChange({ runId, status, playback, error });
    }

    function stopAudio() {
      const previous = audio;
      audio = null;
      if (!previous) return;
      try { previous.pause(); } catch { /* The run may already have ended. */ }
      try { previous.currentTime = 0; } catch { /* Some audio implementations do not allow seeking yet. */ }
    }

    function clearGoTimer() {
      if (goTimer === null) return;
      cancel(goTimer);
      goTimer = null;
    }

    function resetCountdown() {
      clearGoTimer();
      stopAudio();
      goDue = false;
      finishing = false;
    }

    function failAudio(currentAudio, error) {
      if (audio !== currentAudio || goDue) return;
      stopAudio();
      playback = "failed";
      notify("countdown", error?.message || "The countdown audio could not be played.");
    }

    async function completeAtGo(runToFinish) {
      if (runId !== runToFinish || !goDue || finishing) return;
      finishing = true;
      playback = "finishing";
      notify("countdown");
      try {
        await finish(runToFinish);
        if (runId !== runToFinish) return;
        const completedRunId = runId;
        clearGoTimer();
        // Leave the audio playing through the small lead margin; it is not the clock.
        runId = null;
        playback = "idle";
        goDue = false;
        finishing = false;
        onChange({ runId: completedRunId, status: "active", playback: "complete", error: "" });
      } catch (error) {
        if (runId !== runToFinish) return;
        finishing = false;
        playback = "finishFailed";
        notify("countdown", error?.message || "The run could not be started after the countdown.");
      }
    }

    function play(runToPlay, elapsedMs) {
      if (!runToPlay || runId !== runToPlay || goDue || finishing) return false;
      stopAudio();
      // Without createAudio the app plays the countdown through the computer's speakers;
      // this page only keeps its Go timer as a backup.
      if (!createAudio) {
        playback = "computer";
        notify("countdown");
        return true;
      }
      let currentAudio;
      try {
        currentAudio = createAudio();
        if (!currentAudio) throw new Error("Countdown audio is unavailable.");
      } catch (error) {
        playback = "failed";
        notify("countdown", error?.message || "The countdown audio could not be loaded.");
        return false;
      }

      audio = currentAudio;
      try {
        currentAudio.currentTime = Math.max(0, Math.min(elapsedMs, COUNTDOWN_TRACK_DURATION_MS)) / 1000;
      } catch { /* Audio can still start even when the browser cannot seek before metadata loads. */ }
      playback = "playing";
      notify("countdown");
      currentAudio.addEventListener("ended", () => {
        if (audio === currentAudio) audio = null;
      }, { once: true });
      currentAudio.addEventListener("error", () => {
        failAudio(currentAudio, new Error("The countdown audio file could not be loaded."));
      }, { once: true });

      try {
        const result = currentAudio.play();
        if (result && typeof result.catch === "function") {
          result.catch((error) => failAudio(currentAudio, error));
        }
      } catch (error) {
        failAudio(currentAudio, error);
      }
      return true;
    }

    function beginCountdown(runToStart, elapsedMs = 0) {
      resetCountdown();
      runId = runToStart;
      const elapsed = Number.isFinite(Number(elapsedMs)) ? Math.max(0, Number(elapsedMs)) : 0;
      playback = "waiting";
      notify("countdown");
      // The shared timeline is fixed to the 3.48-second MP3 frame duration.
      // Go is issued 125ms before its scheduled end, even if audio cannot play.
      const remainingMs = Math.max(0, goAtMs - elapsed);
      goTimer = schedule(() => {
        goTimer = null;
        goDue = true;
        void completeAtGo(runToStart);
      }, remainingMs);
      if (remainingMs === 0) {
        clearGoTimer();
        goDue = true;
        playback = "finishing";
        notify("countdown");
        void completeAtGo(runToStart);
        return;
      }
      play(runToStart, elapsed);
    }

    function observe(snapshot) {
      const nextRunId = snapshot?.runId ?? snapshot?.id ?? null;
      const nextStatus = String(snapshot?.status || "").toLowerCase();
      if (nextStatus === "countdown" && nextRunId) {
        if (runId !== nextRunId) {
          beginCountdown(nextRunId, snapshot?.elapsedMilliseconds ?? snapshot?.countdownElapsedMilliseconds ?? 0);
        }
        return;
      }

      if (runId && (nextRunId !== runId || nextStatus !== "countdown")) {
        const previousRunId = runId;
        resetCountdown();
        runId = null;
        playback = "idle";
        onChange({ runId: nextRunId || previousRunId, status: nextStatus || "idle", playback: "idle", error: "" });
      }
    }

    async function poll() {
      if (polling) return false;
      polling = true;
      try {
        observe(await readState());
        return true;
      } catch {
        return false;
      } finally {
        polling = false;
      }
    }

    function retry() {
      if (!runId) return false;
      if (goDue) {
        void completeAtGo(runId);
        return true;
      }
      return play(runId);
    }

    return Object.freeze({ observe, poll, retry });
  }

  const api = Object.freeze({
    COUNTDOWN_TRACK_DURATION_MS,
    COUNTDOWN_GO_LEAD_MS,
    COUNTDOWN_GO_AT_MS,
    createCountdownCoordinator
  });
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  if (typeof window !== "undefined") window.GarageGamesCountdown = api;
})();
