(() => {
  "use strict";

  function createCountdownCoordinator({ readState, finish, createAudio, onChange = () => {} }) {
    let runId = null;
    let audio = null;
    let playback = "idle";
    let attempted = false;
    let audioEnded = false;
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

    function failAudio(currentAudio, error) {
      if (audio !== currentAudio || audioEnded) return;
      stopAudio();
      playback = "failed";
      notify("countdown", error?.message || "The countdown audio could not be played.");
    }

    async function completeAfterAudio(runToFinish) {
      if (runId !== runToFinish || !audioEnded || finishing) return;
      finishing = true;
      playback = "finishing";
      notify("countdown");
      try {
        await finish(runToFinish);
        if (runId !== runToFinish) return;
        const completedRunId = runId;
        audio = null;
        runId = null;
        playback = "idle";
        attempted = false;
        audioEnded = false;
        finishing = false;
        onChange({ runId: completedRunId, status: "active", playback: "complete", error: "" });
      } catch (error) {
        if (runId !== runToFinish) return;
        finishing = false;
        playback = "finishFailed";
        notify("countdown", error?.message || "The run could not be started after the countdown.");
      }
    }

    function play(runToPlay) {
      if (!runToPlay || runId !== runToPlay || audioEnded || finishing) return false;
      stopAudio();
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
      playback = "playing";
      notify("countdown");
      currentAudio.addEventListener("ended", () => {
        if (audio !== currentAudio || runId !== runToPlay) return;
        audioEnded = true;
        audio = null;
        void completeAfterAudio(runToPlay);
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

    function observe(snapshot) {
      const nextRunId = snapshot?.runId ?? snapshot?.id ?? null;
      const nextStatus = String(snapshot?.status || "").toLowerCase();
      if (nextStatus === "countdown" && nextRunId) {
        if (runId !== nextRunId) {
          stopAudio();
          runId = nextRunId;
          playback = "waiting";
          attempted = false;
          audioEnded = false;
          finishing = false;
          notify("countdown");
        }
        if (!attempted) {
          attempted = true;
          play(nextRunId);
        }
        return;
      }

      if (runId && (nextRunId !== runId || nextStatus !== "countdown")) {
        const previousRunId = runId;
        stopAudio();
        runId = null;
        playback = "idle";
        attempted = false;
        audioEnded = false;
        finishing = false;
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
      if (audioEnded) {
        void completeAfterAudio(runId);
        return true;
      }
      attempted = true;
      return play(runId);
    }

    return Object.freeze({ observe, poll, retry });
  }

  const api = Object.freeze({ createCountdownCoordinator });
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  if (typeof window !== "undefined") window.GarageGamesCountdown = api;
})();
