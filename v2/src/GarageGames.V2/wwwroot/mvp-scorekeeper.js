(() => {
  "use strict";

  const state = {
    snapshot: null,
    connected: false,
    busy: false,
    master: null,
    masterError: "",
    masterLoading: false,
    masterBusy: false,
    masterPortsSignature: "",
    drafts: new Map(),
    bonusDrafts: new Map(),
    selectedHistoryId: null,
    selectedCompetitorAfterRefresh: null,
    discardedRunNotice: null,
    queueCompetitorAfterRefresh: null,
    selectedLeaderboardCompetitorId: "",
    leaderboardSelectionInitialized: false,
    selectPromotedAfterRecord: false,
    competitorSignature: "",
    leaderboardCompetitorSignature: "",
    historySignature: "",
    queueSignature: "",
    overallLeaderboardKey: null,
    playerLeaderboardKey: null,
    eventLeaderboardsKey: null,
    currentTableKey: null,
    historyTableKey: null,
    virtualKey: null,
    initializedSelection: false,
    receivedAt: Date.now(),
    timeoutRefreshRunId: null,
    alertTimer: null
  };

  const clearDatabasePhrase = "CLEAR ALL DATA";
  const $ = (id) => document.getElementById(id);
  const scorekeeperTime = window.GarageGamesScorekeeperTime;
  const masterActions = window.GarageGamesMasterActions;
  const runActions = window.GarageGamesRunActions;
  const scorecardOrder = window.GarageGamesScorecardOrder;
  const leaderboardTools = window.GarageGamesLeaderboards;
  const ui = {
    edition: $("edition-name"),
    connection: $("connection-status"),
    lastUpdated: $("last-updated"),
    refresh: $("refresh-button"),
    alert: $("alert-region"),
    tabs: [$("tab-scorekeeping"), $("tab-on-deck"), $("tab-history"), $("tab-leaderboards")],
    tabPanels: [$("panel-scorekeeping"), $("panel-on-deck"), $("panel-history"), $("panel-leaderboards")],
    caption: $("run-caption"),
    competitor: $("competitor-select"),
    category: $("run-category"),
    queueCompetitor: $("on-deck-competitor-select"),
    queueCategory: $("on-deck-category-select"),
    queueForm: $("on-deck-form"),
    queueAdd: $("add-to-queue-button"),
    queueCount: $("on-deck-count"),
    queueList: $("on-deck-list"),
    addForm: $("add-competitor-form"),
    newCompetitor: $("new-competitor-name"),
    start: $("start-run-button"),
    armPhysical: $("arm-physical-button"),
    masterPort: $("master-port-select"),
    masterConnect: $("master-connect-button"),
    masterDisconnect: $("master-disconnect-button"),
    masterRefresh: $("master-refresh-button"),
    masterConnectionLabel: $("master-connection-label"),
    masterModeLabel: $("master-mode-label"),
    masterLastMessage: $("master-last-message"),
    physicalStartHelp: $("physical-start-help"),
    pause: $("pause-run-button"),
    finish: $("finish-run-button"),
    record: $("record-run-button"),
    discard: $("discard-run-button"),
    countdown: $("run-countdown"),
    runCompetitor: $("run-competitor"),
    progress: $("event-progress"),
    runTotal: $("run-total"),
    banner: $("run-state-banner"),
    countdownNotice: $("countdown-audio-notice"),
    countdownMessage: $("countdown-audio-message"),
    countdownRetry: $("countdown-audio-retry"),
    virtualButtons: $("virtual-event-buttons"),
    virtualNote: $("virtual-note"),
    currentBody: $("current-event-body"),
    currentTotal: $("scorecard-total"),
    currentBonus: $("current-bonus-points"),
    currentSave: $("save-current-edits"),
    currentSaveState: $("scorecard-save-state"),
    historyCount: $("history-count"),
    historyList: $("history-list"),
    historyTitle: $("history-editor-title"),
    historyStatus: $("history-editor-status"),
    historyMeta: $("history-editor-meta"),
    historyBody: $("history-event-body"),
    historyTotal: $("history-total"),
    historyBonus: $("history-bonus-points"),
    historySave: $("save-history-edits"),
    historySaveState: $("history-save-state"),
    historyRecord: $("record-history-run"),
    leaderboardPlayerSelect: $("leaderboard-player-select"),
    overallLeaderboardCount: $("overall-leaderboard-count"),
    overallLeaderboardBody: $("overall-leaderboard-body"),
    playerLeaderboardCaption: $("player-leaderboard-caption"),
    playerLeaderboardBody: $("player-leaderboard-body"),
    playerLeaderboardTotal: $("player-leaderboard-total"),
    eventLeaderboardsGrid: $("event-leaderboards-grid"),
    clearDatabaseConfirmation: $("clear-database-confirmation"),
    clearDatabaseButton: $("clear-database-button"),
    clearDatabaseResult: $("clear-database-result")
  };

  function isLiveLock(run) {
    return Boolean(run && ["armed", "countdown", "active", "paused", "finished"].includes(run.status));
  }

  function make(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = String(text);
    return node;
  }

  function showAlert(message, kind = "error") {
    window.clearTimeout(state.alertTimer);
    ui.alert.replaceChildren();
    const alert = make("div", `alert${kind === "success" ? " success" : ""}`, message);
    alert.setAttribute("role", kind === "success" ? "status" : "alert");
    ui.alert.appendChild(alert);
    state.alertTimer = window.setTimeout(() => ui.alert.replaceChildren(), 6500);
  }

  async function request(path, options = {}) {
    const response = await fetch(path, {
      ...options,
      headers: {
        Accept: "application/json",
        ...(options.body ? { "Content-Type": "application/json" } : {}),
        ...(options.headers || {})
      }
    });
    const raw = await response.text();
    let payload = null;
    if (raw) {
      try { payload = JSON.parse(raw); } catch { payload = null; }
    }
    if (!response.ok) {
      const message = payload && (payload.detail || payload.title || payload.message || payload.error);
      throw new Error(message || raw || `Request failed (${response.status}).`);
    }
    return payload;
  }

  const countdownCoordinator = window.GarageGamesCountdown.createCountdownCoordinator({
    readState: () => request("/api/run/countdown-state", { cache: "no-store" }),
    finish: (runId) => request("/api/run/countdown-finished", { method: "POST", body: JSON.stringify({ runId }) }),
    createAudio: () => {
      const audio = new Audio("/sounds/3-seconds-countdown-deep-voice-game.mp3");
      audio.preload = "auto";
      return audio;
    },
    onChange: handleCountdownUpdate
  });

  function handleCountdownUpdate(update) {
    const isCountdown = update.status === "countdown";
    ui.countdownNotice.hidden = !isCountdown;
    if (isCountdown) {
      const retryable = update.playback === "failed" || update.playback === "finishFailed";
      ui.countdownRetry.hidden = !retryable;
      ui.countdownRetry.textContent = update.playback === "finishFailed" ? "Retry starting run" : "Retry countdown audio";
      ui.countdownMessage.textContent = retryable
        ? `Run remains in Countdown; the timer has not started. ${update.error || ""}`.trim()
        : update.playback === "finishing"
          ? "Countdown audio ended. Starting the run…"
          : "Countdown audio playing. The timer and event buttons unlock when it ends.";
      const current = state.snapshot?.currentRun;
      if (current?.id === update.runId && current.status !== "countdown") {
        current.status = "countdown";
        render();
      }
      return;
    }

    ui.countdownRetry.hidden = true;
    if (["active", "aborted", "finished", "timedout", "completed", "idle"].includes(update.status)) {
      void loadSnapshot(true);
    }
  }

  function setConnection(online) {
    state.connected = online;
    ui.connection.classList.toggle("is-online", online);
    ui.connection.classList.toggle("is-offline", !online);
    const label = ui.connection.querySelector("span");
    if (label) label.textContent = online ? "Connected" : "Offline";
  }

  function renderMasterControls() {
    const master = state.master;
    const ports = Array.isArray(master?.availablePorts) ? master.availablePorts.slice() : [];
    if (master?.port && !ports.some((port) => port.toLowerCase() === master.port.toLowerCase())) {
      ports.push(master.port);
      ports.sort((a, b) => a.localeCompare(b, undefined, { numeric: true, sensitivity: "base" }));
    }
    const signature = `${ports.join("|")};${master?.port || ""}`;
    if (signature !== state.masterPortsSignature) {
      const previous = ui.masterPort.value;
      ui.masterPort.replaceChildren(new Option(ports.length ? "Select COM port…" : "No COM ports found", ""));
      ports.forEach((port) => ui.masterPort.appendChild(new Option(port, port)));
      const selection = master?.connected && master.port
        ? master.port
        : ports.includes(previous) ? previous : "";
      ui.masterPort.value = selection;
      state.masterPortsSignature = signature;
    }

    ui.masterConnectionLabel.textContent = state.masterError
      ? "Master status unavailable"
      : master?.connected
        ? `${master.mode === "IDLE" || master.mode === "SPEED" ? "Master connected" : "COM port open"} · ${master.port || "COM port"}`
        : "Not connected";
    ui.masterModeLabel.textContent = `Mode: ${master?.mode ? master.mode.toUpperCase() : master?.connected ? "WAITING FOR MASTER" : "—"}`;
    ui.masterLastMessage.textContent = `Last message: ${master?.lastMessage || "—"}`;

    const run = state.snapshot?.currentRun || null;
    const handshakeHelp = masterActions.handshakeGuidance(master);
    if (masterActions.isSpeedMode(master)) {
      ui.physicalStartHelp.textContent = "SPEED mode is active. Arming and Start are disabled until the master returns to IDLE.";
    } else if (handshakeHelp) {
      ui.physicalStartHelp.textContent = handshakeHelp;
    } else if (run?.status === "armed") {
      ui.physicalStartHelp.textContent = "Run is armed and waiting for a physical Start press. Virtual Start is also available.";
    } else if (!master?.connected) {
      ui.physicalStartHelp.textContent = state.masterError
        ? `Could not read physical master status: ${state.masterError}`
        : "Connect a physical master to arm for its Start button. Virtual Start works without hardware.";
    } else if (!ui.competitor.value) {
      ui.physicalStartHelp.textContent = "Choose a competitor and run type, then arm for a physical Start.";
    } else {
      ui.physicalStartHelp.textContent = "Arm the selected competitor, then use the physical master’s short press to start the timer.";
    }

    ui.masterPort.disabled = state.busy || state.masterBusy || Boolean(master?.connected);
    ui.masterConnect.disabled = state.busy || state.masterBusy || Boolean(master?.connected) || !ui.masterPort.value;
    ui.masterDisconnect.disabled = state.busy || state.masterBusy || !master?.connected;
    ui.masterRefresh.disabled = state.busy || state.masterBusy || state.masterLoading;
  }

  async function loadMaster(silent = false) {
    if (state.masterLoading) return false;
    state.masterLoading = true;
    try {
      state.master = await request("/api/master");
      state.masterError = "";
      return true;
    } catch (error) {
      state.master = null;
      state.masterError = error.message || "The physical master status could not be loaded.";
      if (!silent) showAlert(`Could not load physical master status: ${state.masterError}`);
      return false;
    } finally {
      state.masterLoading = false;
      renderMasterControls();
      if (state.snapshot) updateControls();
    }
  }

  async function loadSnapshot(silent = false) {
    try {
      const snapshot = await request("/api/operator");
      state.snapshot = snapshot;
      state.receivedAt = Date.now();
      state.timeoutRefreshRunId = null;
      setConnection(true);
      ui.lastUpdated.textContent = new Date(state.receivedAt).toLocaleTimeString([], { hour: "numeric", minute: "2-digit", second: "2-digit" });
      render();
      return true;
    } catch (error) {
      setConnection(false);
      if (!silent) showAlert(`Could not load scorekeeper data: ${error.message}`);
      return false;
    }
  }

  function renderCompetitors() {
    const competitors = state.snapshot?.competitors || [];
    const signature = competitors.map((item) => `${item.id}:${item.name}`).join("|");
    const previousValue = ui.competitor.value;
    const previousQueueValue = ui.queueCompetitor.value;
    if (signature !== state.competitorSignature) {
      const sorted = competitors.slice().sort((a, b) => a.name.localeCompare(b.name));
      ui.competitor.replaceChildren(new Option("Select competitor…", ""));
      ui.queueCompetitor.replaceChildren(new Option("Select competitor…", ""));
      sorted.forEach((competitor) => {
        ui.competitor.appendChild(new Option(competitor.name, competitor.id));
        ui.queueCompetitor.appendChild(new Option(competitor.name, competitor.id));
      });
      state.competitorSignature = signature;
    }
    const preferred = state.selectedCompetitorAfterRefresh;
    if (isLiveLock(state.snapshot?.currentRun)) {
      ui.competitor.value = state.snapshot.currentRun.competitorId;
      ui.category.value = state.snapshot.currentRun.category;
    } else if (state.selectPromotedAfterRecord) {
      ui.competitor.value = state.snapshot.selectedCompetitorId || "";
      ui.category.value = state.snapshot.selectedRunCategory || "official";
      state.selectPromotedAfterRecord = false;
      state.selectedCompetitorAfterRefresh = null;
    } else if (preferred && competitors.some((item) => item.id === preferred)) {
      ui.competitor.value = preferred;
      state.selectedCompetitorAfterRefresh = null;
    } else if (!state.initializedSelection && competitors.some((item) => item.id === state.snapshot?.selectedCompetitorId)) {
      ui.competitor.value = state.snapshot.selectedCompetitorId;
      ui.category.value = state.snapshot.selectedRunCategory || "official";
    } else if (competitors.some((item) => item.id === previousValue)) {
      ui.competitor.value = previousValue;
    } else {
      ui.competitor.value = "";
    }
    state.initializedSelection = true;
    if (state.queueCompetitorAfterRefresh && competitors.some((item) => item.id === state.queueCompetitorAfterRefresh)) {
      ui.queueCompetitor.value = state.queueCompetitorAfterRefresh;
      state.queueCompetitorAfterRefresh = null;
    } else if (competitors.some((item) => item.id === previousQueueValue)) {
      ui.queueCompetitor.value = previousQueueValue;
    }
  }

  function categoryLabel(category) {
    return ({ official: "Official", playoff: "Playoff", exhibition: "Exhibition" })[category] || titleCase(category || "run");
  }

  function titleCase(value) {
    return String(value || "").replace(/([a-z])([A-Z])/g, "$1 $2").replace(/^./, (char) => char.toUpperCase());
  }

  function competitorName(id) {
    return (state.snapshot?.competitors || []).find((item) => item.id === id)?.name || "Unknown competitor";
  }

  function emptyEvent(event) {
    return {
      ...event,
      status: "pending",
      score: 0,
      scoreOverride: null,
      startElapsedMs: null,
      finishElapsedMs: null
    };
  }

  function runEvents(run) {
    if (run) return run.events || [];
    return (state.snapshot?.events || []).map(emptyEvent);
  }

  function currentEditionEvents(run) {
    const configured = state.snapshot?.events || [];
    if (!run) return configured.map(emptyEvent);
    const byId = new Map((run.events || []).map((event) => [event.eventId, event]));
    return configured.map((definition) => ({
      ...emptyEvent(definition),
      ...(byId.get(definition.eventId) || {}),
      eventId: definition.eventId,
      name: definition.name,
      deviceId: definition.deviceId,
      type: definition.type
    }));
  }

  function eventRosterSignature() {
    return (state.snapshot?.events || []).map((event) => `${event.eventId}:${event.name}`).join("|");
  }

  function draftFor(runId, eventId) {
    if (!state.drafts.has(runId)) state.drafts.set(runId, new Map());
    const runDrafts = state.drafts.get(runId);
    if (!runDrafts.has(eventId)) runDrafts.set(eventId, { touched: new Set() });
    return runDrafts.get(eventId);
  }

  function hasDrafts(runId) {
    const rows = state.drafts.get(runId);
    return Boolean(rows && Array.from(rows.values()).some((draft) => draft.touched.size));
  }

  function hasBonusDraft(runId) {
    return Boolean(state.bonusDrafts.get(runId)?.touched);
  }

  function parsedSeconds(value) {
    return scorekeeperTime.parseClockTimeToSeconds(value);
  }

  function runDurationSeconds(run) {
    return Number(run?.edition?.durationLimitSeconds || state.snapshot?.durationLimitSeconds || 300);
  }

  function formatSeconds(milliseconds) {
    return scorekeeperTime.formatClockMs(milliseconds);
  }

  function formatDuration(milliseconds) {
    if (milliseconds === null || milliseconds === undefined || milliseconds < 0) return "—";
    return scorekeeperTime.formatClockMs(milliseconds);
  }

  function displayedValue(run, event, field) {
    const draft = state.drafts.get(run.id)?.get(event.eventId);
    if (draft?.touched.has(field)) return draft[field];
    if (field === "start" || field === "finish") {
      const elapsed = field === "start" ? event.startElapsedMs : event.finishElapsedMs;
      const remaining = scorekeeperTime.remainingSecondsFromElapsedMs(elapsed, runDurationSeconds(run));
      return remaining === null ? "" : formatSeconds(remaining * 1000);
    }
    return String(event.scoreOverride ?? event.score ?? 0);
  }

  function currentElapsedMs(run) {
    const base = Number(run?.activeElapsedMs || 0);
    if (run?.status === "active") return base + Math.max(0, Date.now() - state.receivedAt);
    return base;
  }

  function rowTiming(row, run) {
    const start = parsedSeconds(row.querySelector('[data-field="start"]')?.value);
    const finish = parsedSeconds(row.querySelector('[data-field="finish"]')?.value);
    const durationCell = row.querySelector(".duration-cell");
    const statusCell = row.querySelector(".event-status");
    const durationSeconds = runDurationSeconds(run);
    const startMs = scorekeeperTime.elapsedMsFromRemainingSeconds(start, durationSeconds);
    const finishMs = scorekeeperTime.elapsedMsFromRemainingSeconds(finish, durationSeconds);
    let duration = null;
    if (startMs !== null && finishMs !== null) duration = finishMs - startMs;
    else if (startMs !== null && run?.status === "active") duration = currentElapsedMs(run) - startMs;
    if (durationCell) durationCell.textContent = formatDuration(duration);
    const derivedStatus = startMs !== null && finishMs !== null ? "completed" : startMs !== null ? "active" : "pending";
    if (statusCell) {
      statusCell.textContent = derivedStatus === "completed" ? "Complete" : derivedStatus === "active" ? "In progress" : "Pending";
      statusCell.className = `event-status ${derivedStatus}`;
    }
  }

  function displayedScore(run, event) {
    const draft = state.drafts.get(run.id)?.get(event.eventId);
    if (draft?.touched.has("score")) {
      if (draft.score === "") return 0;
      const score = Number(draft.score);
      return Number.isFinite(score) && score >= 0 ? score : 0;
    }
    return Number(event.score || 0);
  }

  function displayedBonus(run) {
    if (!run) return 0;
    const draft = state.bonusDrafts.get(run.id);
    if (draft?.touched) {
      const value = Number(draft.value);
      return draft.value !== "" && Number.isFinite(value) && value >= 0 ? value : 0;
    }
    return Number(run.bonusPointsOverride || 0);
  }

  function bonusInputValue(run) {
    if (!run) return "";
    const draft = state.bonusDrafts.get(run.id);
    if (draft?.touched) return draft.value;
    return run.bonusPointsOverride == null ? "" : String(run.bonusPointsOverride);
  }

  function tableTotal(run) {
    if (!run) return 0;
    return runEvents(run).reduce((sum, event) => sum + displayedScore(run, event), displayedBonus(run));
  }

  function updateTableTotal(kind, run) {
    const total = tableTotal(run);
    if (kind === "current") ui.currentTotal.textContent = String(total);
    else ui.historyTotal.textContent = String(total);
    if (kind === "current") ui.runTotal.textContent = String(total);
    if (kind === "history" && run) {
      const historyRow = ui.historyList.querySelector(`[data-run-id="${CSS.escape(run.id)}"] .history-points`);
      if (historyRow) historyRow.textContent = String(total);
    }
  }

  function syncBonusInput(input, run, editable) {
    if (!input) return;
    const value = bonusInputValue(run);
    if (input.value !== value) input.value = value;
    input.dataset.runId = run?.id || "";
    input.disabled = !run || !editable;
  }

  function buildTimeInput(run, event, field, editable) {
    const input = make("input", "score-input");
    input.type = "text";
    input.placeholder = "M:SS";
    input.inputMode = "numeric";
    input.autocomplete = "off";
    input.setAttribute("aria-label", `${event.name} ${field === "start" ? "start" : "stop"} time, M:SS remaining or seconds`);
    input.dataset.field = field;
    input.dataset.eventId = event.eventId;
    input.dataset.runId = run?.id || "";
    input.value = run ? displayedValue(run, event, field) : "";
    input.disabled = !editable;
    return input;
  }

  function buildScoreInput(run, event, editable) {
    const input = make("input", "score-input points-input");
    input.type = "number";
    input.min = "0";
    input.step = "1";
    input.inputMode = "numeric";
    input.setAttribute("aria-label", `${event.name} points`);
    input.dataset.field = "score";
    input.dataset.eventId = event.eventId;
    input.dataset.runId = run?.id || "";
    input.value = run ? displayedValue(run, event, "score") : "";
    input.disabled = !editable;
    return input;
  }

  function renderScoreTable(run, tbody, kind, editable, events = runEvents(run)) {
    tbody.replaceChildren();
    events.forEach((event, index) => {
      const row = make("tr", "score-row");
      const nameCell = make("td", "event-name-cell");
      const indexBadge = make("span", "event-index", String(index + 1).padStart(2, "0"));
      nameCell.append(indexBadge, document.createTextNode(event.name));
      const startCell = make("td");
      const finishCell = make("td");
      const durationCell = make("td", "duration-cell", "—");
      const pointsCell = make("td");
      const statusCell = make("td", "event-status", "Pending");
      durationCell.dataset.eventId = event.eventId;
      statusCell.dataset.eventId = event.eventId;
      startCell.appendChild(buildTimeInput(run, event, "start", editable));
      finishCell.appendChild(buildTimeInput(run, event, "finish", editable));
      pointsCell.appendChild(buildScoreInput(run, event, editable));
      row.append(nameCell, startCell, finishCell, durationCell, pointsCell, statusCell);
      row.dataset.eventId = event.eventId;
      tbody.appendChild(row);
      rowTiming(row, run);
    });
    updateTableTotal(kind, run);
  }

  function captureFocus() {
    const active = document.activeElement;
    if (!active?.dataset?.field || !active.dataset.eventId) return null;
    let start = null;
    let end = null;
    try {
      start = active.selectionStart;
      end = active.selectionEnd;
    } catch { /* Number inputs do not expose selection ranges. */ }
    return {
      kind: active.closest("#history-event-body") ? "history" : "current",
      eventId: active.dataset.eventId,
      field: active.dataset.field,
      start,
      end
    };
  }

  function restoreFocus(focus) {
    if (!focus) return;
    const body = focus.kind === "history" ? ui.historyBody : ui.currentBody;
    const input = body.querySelector(`[data-event-id="${CSS.escape(focus.eventId)}"][data-field="${focus.field}"]`);
    if (!input || input.disabled) return;
    input.focus({ preventScroll: true });
    if (typeof input.setSelectionRange === "function" && focus.start !== null) {
      try { input.setSelectionRange(focus.start, focus.end); } catch { /* Number inputs do not expose selection ranges. */ }
    }
  }

  function setSaveStates() {
    const current = state.snapshot?.currentRun;
    const currentDirty = current && (hasDrafts(current.id) || hasBonusDraft(current.id));
    const currentEditable = Boolean(current && isLiveLock(current));
    ui.currentSave.disabled = state.busy || !currentDirty || !currentEditable;
    ui.currentSaveState.textContent = currentDirty
      ? (currentEditable ? "Unsaved edits" : "Use the history editor to correct this saved run")
      : "No unsaved edits";

    const selected = (state.snapshot?.history || []).find((run) => run.id === state.selectedHistoryId);
    const historyDirty = selected && (hasDrafts(selected.id) || hasBonusDraft(selected.id));
    const historyEditable = Boolean(selected && !isLiveLock(selected));
    ui.historySave.disabled = state.busy || !historyDirty || !historyEditable;
    ui.historySaveState.textContent = historyDirty
      ? (historyEditable ? "Unsaved edits · save when ready" : "Live runs are edited in the current scorecard")
      : "History edits are saved explicitly.";
  }

  function renderCurrent() {
    const run = state.snapshot?.currentRun || null;
    syncBonusInput(ui.currentBonus, run, Boolean(run && isLiveLock(run)));
    const key = run ? `${run.id}:${run.revision}:${run.status}` : `no-run:${eventRosterSignature()}`;
    if (key !== state.currentTableKey) {
      const focus = captureFocus();
      renderScoreTable(run, ui.currentBody, "current", Boolean(run && isLiveLock(run)), currentEditionEvents(run));
      state.currentTableKey = key;
      restoreFocus(focus);
    } else {
      updateTableTotal("current", run);
    }
    const events = currentEditionEvents(run);
    const complete = run ? events.filter((event) => event.status === "completed").length : 0;
    ui.progress.textContent = `${complete} / ${events.length}`;
    ui.runCompetitor.textContent = run ? competitorName(run.competitorId) : "—";
    if (!run) {
      ui.banner.textContent = state.discardedRunNotice || "No run is active. Select a competitor to begin.";
      ui.banner.classList.toggle("is-discarded", Boolean(state.discardedRunNotice));
      ui.caption.textContent = state.discardedRunNotice ? "Discarded run · not recorded" : "Choose a competitor, then start the five-minute clock.";
    } else {
      ui.banner.classList.remove("is-discarded");
      const label = categoryLabel(run.category);
      const competitor = competitorName(run.competitorId);
      const copy = {
        armed: "Run is armed and waiting for a physical Start press, or use Start armed run here.",
        countdown: "Countdown audio is playing. The run timer and event buttons start when it ends.",
        active: "Run in progress. Event timestamps count down from the run limit.",
        paused: "Run paused. Resume when the competitor is ready.",
        finished: "All events complete. Timer stopped · finished, not recorded.",
        completed: "Run finished · recorded.",
        timedOut: run.isRecorded ? "Time expired · incomplete result recorded." : "Time expired · incomplete result not recorded.",
        aborted: "Run discarded · retained in history · not recorded or counted toward results.",
        superseded: "Run replaced by a newer result."
      }[run.status] || `Run status: ${titleCase(run.status)}.`;
      ui.banner.textContent = `${copy} ${competitor} · ${label}`;
      ui.caption.textContent = `${competitor} · ${label} · ${titleCase(run.status)}`;
    }
    renderVirtualButtons(run);
  }

  function renderVirtualButtons(run) {
    ui.virtualNote.textContent = !run
      ? "Start a run to enable the virtual event buttons."
      : run.status === "active"
        ? "Times count down from the run limit. Press once to start an event and again to finish it."
        : run.status === "countdown"
          ? "Countdown audio must finish before event buttons become available."
        : run.status === "paused"
          ? "Resume the run before recording event presses."
          : run.status === "armed"
            ? "Start the run to enable event presses."
            : "This run no longer accepts event presses. Review or correct it in history.";
    const key = run ? `${run.id}:${run.revision}:${run.status}:${state.busy}` : `no-run:${eventRosterSignature()}:${state.busy}`;
    if (key === state.virtualKey) return;
    ui.virtualButtons.replaceChildren();
    const events = currentEditionEvents(run);
    const canPress = Boolean(run && run.status === "active" && !state.busy);
    events.forEach((event, index) => {
      const button = make("button", "virtual-button");
      button.type = "button";
      button.disabled = !canPress || event.status === "completed";
      button.setAttribute("aria-label", `${event.name}: ${event.status === "active" ? "finish event" : event.status === "completed" ? "complete" : "start event"}`);
      const name = make("strong", "", `${String(index + 1).padStart(2, "0")} · ${event.name}`);
      let hint = "Start";
      if (event.status === "active") {
        const remainingMs = Math.max(0, runDurationSeconds(run) * 1000 - currentElapsedMs(run));
        hint = `Stop · ${formatSeconds(remainingMs)} left`;
      }
      if (event.status === "completed") hint = `Done · ${formatDuration(event.finishElapsedMs - event.startElapsedMs)}`;
      const stateLabel = make("small", "", hint);
      button.append(name, stateLabel);
      button.addEventListener("click", () => pressEvent(run, event));
      ui.virtualButtons.appendChild(button);
    });
    state.virtualKey = key;
  }

  function renderQueue() {
    const queue = (state.snapshot?.queue || []).slice().sort((a, b) => Number(a.position) - Number(b.position));
    ui.queueCount.textContent = `${queue.length} queued`;
    const signature = `${state.busy}:${queue.map((item) => `${item.id}:${item.position}:${item.competitorId}:${item.category}`).join("|")}`;
    if (signature === state.queueSignature) return;
    ui.queueList.replaceChildren();
    if (!queue.length) {
      const empty = make("li", "empty-state", "On deck is empty. Add a competitor to show who is next on the TV scoreboard.");
      ui.queueList.appendChild(empty);
    } else {
      queue.forEach((item, index) => {
        const row = make("li", `on-deck-row${index === 0 ? " is-next" : ""}`);
        const position = make("span", "on-deck-position", String(index + 1));
        const details = make("span", "on-deck-primary");
        details.append(
          make("strong", "", competitorName(item.competitorId)),
          make("small", "", categoryLabel(item.category))
        );
        const actions = make("div", "on-deck-actions");
        const move = (offset) => {
          const queueIds = queue.map((queued) => queued.id);
          const destination = index + offset;
          [queueIds[index], queueIds[destination]] = [queueIds[destination], queueIds[index]];
          const direction = offset < 0 ? "up" : "down";
          return performAction(
            () => request("/api/queue/reorder", {
              method: "POST",
              body: JSON.stringify({ queueIds })
            }),
            `${competitorName(item.competitorId)} moved ${direction} in on deck.`
          );
        };
        const moveUp = make("button", "button button-quiet on-deck-move", "↑");
        moveUp.type = "button";
        moveUp.disabled = state.busy || index === 0;
        moveUp.setAttribute("aria-label", `Move ${competitorName(item.competitorId)} (${categoryLabel(item.category)}) up in on-deck queue`);
        moveUp.title = "Move up";
        moveUp.addEventListener("click", () => move(-1));
        const moveDown = make("button", "button button-quiet on-deck-move", "↓");
        moveDown.type = "button";
        moveDown.disabled = state.busy || index === queue.length - 1;
        moveDown.setAttribute("aria-label", `Move ${competitorName(item.competitorId)} (${categoryLabel(item.category)}) down in on-deck queue`);
        moveDown.title = "Move down";
        moveDown.addEventListener("click", () => move(1));
        const remove = make("button", "button button-quiet on-deck-remove", "Remove");
        remove.type = "button";
        remove.disabled = state.busy;
        remove.setAttribute("aria-label", `Remove ${competitorName(item.competitorId)} (${categoryLabel(item.category)}) from on deck`);
        remove.addEventListener("click", () => performAction(
          () => request(`/api/queue/${encodeURIComponent(item.id)}`, { method: "DELETE" }),
          `${competitorName(item.competitorId)} removed from on deck.`
        ));
        actions.append(moveUp, moveDown, remove);
        row.append(position, details, actions);
        ui.queueList.appendChild(row);
      });
    }
    state.queueSignature = signature;
  }

  function officialLeaderboardRuns() {
    const historyById = new Map((state.snapshot?.history || []).map((run) => [run.id, run]));
    return (state.snapshot?.leaderboard || [])
      .filter((row) => row.category === "official" && row.runId)
      .map((row) => ({ row, run: historyById.get(row.runId) }))
      .filter(({ run }) => run && run.category === "official");
  }

  function appendEmptyTableRow(body, columns, message) {
    const row = make("tr");
    const cell = make("td", "empty-cell", message);
    cell.colSpan = columns;
    row.appendChild(cell);
    body.replaceChildren(row);
  }

  function renderOverallLeaderboard(rows) {
    ui.overallLeaderboardCount.textContent = `${rows.length} ${rows.length === 1 ? "result" : "results"}`;
    if (!rows.length) {
      appendEmptyTableRow(ui.overallLeaderboardBody, 3, "No counted official results for this edition yet.");
      return;
    }
    const fragment = document.createDocumentFragment();
    rows.forEach((item) => {
      const row = make("tr");
      row.append(
        make("td", "leaderboard-rank", String(item.rank)),
        make("td", "leaderboard-player", item.competitorName),
        make("td", "leaderboard-points", String(item.points))
      );
      fragment.appendChild(row);
    });
    ui.overallLeaderboardBody.replaceChildren(fragment);
  }

  function renderLeaderboardPlayerOptions(officialRuns) {
    const competitors = (state.snapshot?.competitors || []).slice().sort((a, b) => a.name.localeCompare(b.name));
    const signature = competitors.map((item) => `${item.id}:${item.name}`).join("|");
    if (signature !== state.leaderboardCompetitorSignature) {
      ui.leaderboardPlayerSelect.replaceChildren(new Option("Select a player…", ""));
      competitors.forEach((competitor) => ui.leaderboardPlayerSelect.appendChild(new Option(competitor.name, competitor.id)));
      state.leaderboardCompetitorSignature = signature;
    }
    const validCompetitors = new Set(competitors.map((item) => item.id));
    if (!validCompetitors.has(state.selectedLeaderboardCompetitorId)) state.selectedLeaderboardCompetitorId = "";
    if (!state.selectedLeaderboardCompetitorId && !state.leaderboardSelectionInitialized) {
      state.selectedLeaderboardCompetitorId = officialRuns.find(({ run }) => validCompetitors.has(run.competitorId))?.run.competitorId || "";
      state.leaderboardSelectionInitialized = true;
    }
    ui.leaderboardPlayerSelect.value = state.selectedLeaderboardCompetitorId;
  }

  function runEventTimestamp(run, elapsedMs) {
    if (!Number.isFinite(elapsedMs)) return "—";
    const durationSeconds = Number(run.edition?.durationLimitSeconds || state.snapshot?.durationLimitSeconds || 300);
    const remaining = scorekeeperTime.remainingSecondsFromElapsedMs(elapsedMs, durationSeconds);
    return remaining === null ? "—" : formatSeconds(remaining * 1000);
  }

  function renderSelectedPlayerLeaderboard(officialRuns) {
    const selectedId = state.selectedLeaderboardCompetitorId;
    if (!selectedId) {
      ui.playerLeaderboardCaption.textContent = "Select a player to review their event times and points.";
      ui.playerLeaderboardTotal.textContent = "—";
      appendEmptyTableRow(ui.playerLeaderboardBody, 5, "Select a player to see their scorecard.");
      return;
    }
    const selected = officialRuns.find(({ run }) => run.competitorId === selectedId);
    if (!selected) {
      ui.playerLeaderboardCaption.textContent = "No counted official result for this player in the current edition.";
      ui.playerLeaderboardTotal.textContent = "—";
      appendEmptyTableRow(ui.playerLeaderboardBody, 5, "This player has no counted official run to display.");
      return;
    }

    const { row: overallRow, run } = selected;
    ui.playerLeaderboardCaption.textContent = `Official result · ${overallRow.points} total points · ${shortDate(run.recordedAt || run.finishedAt || run.createdAt)}`;
    ui.playerLeaderboardTotal.textContent = String(overallRow.points);
    const eventResults = new Map((run.events || []).map((event) => [event.eventId, event]));
    const configuredEvents = state.snapshot?.events || [];
    if (!configuredEvents.length) {
      appendEmptyTableRow(ui.playerLeaderboardBody, 5, "No events are configured for this edition.");
      return;
    }
    const fragment = document.createDocumentFragment();
    scorecardOrder.orderScorecardEvents(configuredEvents, eventResults).forEach(({ definition, result, configuredIndex }) => {
      const start = result?.startElapsedMs;
      const finish = result?.finishElapsedMs;
      const duration = Number.isFinite(start) && Number.isFinite(finish) && finish >= start
        ? formatDuration(finish - start)
        : "—";
      const hasPoints = result && (Number(result.score || 0) !== 0 || result.scoreOverride !== null && result.scoreOverride !== undefined || start !== null && start !== undefined || finish !== null && finish !== undefined);
      const points = hasPoints ? String(Number(result.scoreOverride ?? result.score ?? 0)) : "—";
      const rowNode = make("tr");
      const name = make("td", "event-name-cell");
      name.append(make("span", "event-index", String(configuredIndex + 1)), document.createTextNode(definition.name));
      rowNode.append(
        name,
        make("td", "duration-cell", runEventTimestamp(run, start)),
        make("td", "duration-cell", runEventTimestamp(run, finish)),
        make("td", "duration-cell", duration),
        make("td", "leaderboard-points", points)
      );
      fragment.appendChild(rowNode);
    });
    ui.playerLeaderboardBody.replaceChildren(fragment);
  }

  function renderEventLeaderboards(boards) {
    if (!boards.length) {
      ui.eventLeaderboardsGrid.replaceChildren(make("p", "empty-state", "No events are configured for this edition."));
      return;
    }
    const fragment = document.createDocumentFragment();
    boards.forEach((board, index) => {
      const card = make("section", "sheet-card event-leaderboard-card");
      const heading = make("div", "event-leaderboard-heading");
      const headingId = `event-leaderboard-title-${index}`;
      const title = make("h3", "", `${index + 1}. ${board.name}`);
      title.id = headingId;
      heading.setAttribute("aria-labelledby", headingId);
      heading.appendChild(title);
      heading.appendChild(make("span", "small-note", `${board.rows.length} ${board.rows.length === 1 ? "result" : "results"}`));
      const tableScroll = make("div", "table-scroll event-leaderboard-table-scroll");
      const table = make("table", "score-table leaderboard-table");
      const thead = document.createElement("thead");
      const headerRow = document.createElement("tr");
      ["Rank", "Player", "Time", "Points"].forEach((label) => {
        const cell = make("th", "", label);
        cell.scope = "col";
        headerRow.appendChild(cell);
      });
      thead.appendChild(headerRow);
      const tbody = document.createElement("tbody");
      if (!board.rows.length) {
        appendEmptyTableRow(tbody, 4, "No completed official results for this event yet.");
      } else {
        board.rows.forEach((item) => {
          const row = make("tr");
          if (item.status === "dnf") row.classList.add("leaderboard-dnf-row");
          const display = leaderboardTools.eventLeaderboardDisplayRow(item);
          row.append(
            make("td", "leaderboard-rank", display.rank),
            make("td", "leaderboard-player", item.competitorName),
            make("td", "duration-cell", display.durationMs === null ? "—" : formatDuration(display.durationMs)),
            make("td", "leaderboard-points", display.points)
          );
          tbody.appendChild(row);
        });
      }
      table.append(thead, tbody);
      tableScroll.appendChild(table);
      card.append(heading, tableScroll);
      card.setAttribute("aria-labelledby", headingId);
      fragment.appendChild(card);
    });
    ui.eventLeaderboardsGrid.replaceChildren(fragment);
  }

  function renderLeaderboards() {
    const snapshot = state.snapshot;
    if (!snapshot) return;
    const officialRows = snapshot.leaderboard || [];
    const officialRuns = officialLeaderboardRuns();
    renderLeaderboardPlayerOptions(officialRuns);

    const overallKey = JSON.stringify(officialRows);
    if (overallKey !== state.overallLeaderboardKey) {
      renderOverallLeaderboard(officialRows);
      state.overallLeaderboardKey = overallKey;
    }

    const playerKey = `${state.selectedLeaderboardCompetitorId}:${JSON.stringify(officialRuns.map(({ row, run }) => [row.runId, row.points, run.revision, run.events]))}:${JSON.stringify(snapshot.events)}`;
    if (playerKey !== state.playerLeaderboardKey) {
      renderSelectedPlayerLeaderboard(officialRuns);
      state.playerLeaderboardKey = playerKey;
    }

    const boards = leaderboardTools.buildEventLeaderboards(snapshot.events || [], officialRows, snapshot.history || []);
    const eventKey = JSON.stringify(boards);
    if (eventKey !== state.eventLeaderboardsKey) {
      renderEventLeaderboards(boards);
      state.eventLeaderboardsKey = eventKey;
    }
  }

  function renderHistory() {
    const history = state.snapshot?.history || [];
    ui.historyCount.textContent = `${history.length} ${history.length === 1 ? "run" : "runs"}`;
    if (!history.some((run) => run.id === state.selectedHistoryId)) {
      state.selectedHistoryId = history[0]?.id || null;
    }
    const signature = history.map((run) => `${run.id}:${run.revision}:${run.status}`).join("|");
    if (signature !== state.historySignature) {
      ui.historyList.replaceChildren();
      if (!history.length) {
        ui.historyList.appendChild(make("p", "empty-state", "Saved runs will appear here."));
      } else {
        history.forEach((run) => {
          const button = make("button", `history-row${run.id === state.selectedHistoryId ? " is-selected" : ""}`);
          button.type = "button";
          button.dataset.runId = run.id;
          button.setAttribute("aria-pressed", String(run.id === state.selectedHistoryId));
          const primary = make("span", "history-primary");
          primary.append(
            make("strong", "", competitorName(run.competitorId)),
            make("span", "", `${categoryLabel(run.category)} · ${shortDate(run.createdAt)}`)
          );
          const points = make("strong", "history-points", String(tableTotal(run)));
          const status = make("span", `history-status ${run.status}`, run.isRecorded ? "Recorded" : "Not recorded");
          button.append(primary, points, status);
          button.addEventListener("click", () => {
            state.selectedHistoryId = run.id;
            state.historySignature = "";
            state.historyTableKey = null;
            renderHistory();
            updateControls();
          });
          ui.historyList.appendChild(button);
        });
      }
      state.historySignature = signature;
    }
    const selected = history.find((run) => run.id === state.selectedHistoryId) || null;
    if (!selected) {
      syncBonusInput(ui.historyBonus, null, false);
      ui.historyTitle.textContent = "Select a saved run";
      ui.historyStatus.textContent = "—";
      ui.historyMeta.textContent = "Choose a history entry to review or correct its event times and points.";
      ui.historyRecord.disabled = true;
      if (state.historyTableKey !== "no-history") {
        renderScoreTable(null, ui.historyBody, "history", false);
        state.historyTableKey = "no-history";
      }
      return;
    }
    const key = `${selected.id}:${selected.revision}:${selected.status}`;
    syncBonusInput(ui.historyBonus, selected, !isLiveLock(selected));
    ui.historyTitle.textContent = `${competitorName(selected.competitorId)} · ${categoryLabel(selected.category)}`;
    ui.historyStatus.textContent = `${titleCase(selected.status)} · ${selected.isRecorded ? "Recorded" : "Not recorded"}`;
    ui.historyStatus.className = `status-tag status-${selected.status}`;
    ui.historyMeta.textContent = `Started ${shortDate(selected.startedAt || selected.createdAt)} · Revision ${selected.revision}. Incomplete and timed-out runs can also be corrected here.`;
    if (key !== state.historyTableKey) {
      const focus = captureFocus();
      renderScoreTable(selected, ui.historyBody, "history", !isLiveLock(selected));
      state.historyTableKey = key;
      restoreFocus(focus);
    } else {
      updateTableTotal("history", selected);
    }
    ui.historyRecord.disabled = state.busy || selected.isRecorded || isLiveLock(selected);
    ui.historyRecord.textContent = selected.isRecorded ? "Already recorded" : "Record result";
  }

  function shortDate(value) {
    if (!value) return "Date unavailable";
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? "Date unavailable" : date.toLocaleString([], { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" });
  }

  function updateControls() {
    const snapshot = state.snapshot;
    const run = snapshot?.currentRun || null;
    const locked = isLiveLock(run);
    const selectedId = ui.competitor.value;
    ui.competitor.disabled = state.busy || locked;
    ui.category.disabled = state.busy || locked;
    ui.start.disabled = state.busy || state.masterBusy || !masterActions.canStartVirtual(state.master, run, selectedId);
    ui.start.textContent = run?.status === "armed" ? "Start armed run" : "Start 5-minute run";
    ui.armPhysical.disabled = state.busy || state.masterBusy || !masterActions.canArmPhysical(state.master, run, selectedId);
    ui.armPhysical.textContent = run?.status === "armed" ? "Waiting for physical Start" : "Arm for physical Start";
    ui.pause.disabled = state.busy || !run || !["active", "paused"].includes(run.status);
    ui.pause.textContent = run?.status === "paused" ? "Resume" : "Pause";
    ui.finish.disabled = state.busy || !run || !["armed", "active", "paused"].includes(run.status);
    ui.record.disabled = state.busy || !run || run.isRecorded || !["armed", "active", "paused", "finished", "timedOut"].includes(run.status);
    ui.record.textContent = run?.isRecorded ? "Already recorded" : "Record result";
    ui.discard.hidden = !runActions.isDiscardableRun(run);
    ui.discard.disabled = state.busy || !state.connected;
    ui.clearDatabaseButton.disabled = state.busy || !state.connected || ui.clearDatabaseConfirmation.value !== clearDatabasePhrase;
    ui.refresh.disabled = state.busy;
    ui.queueCompetitor.disabled = state.busy;
    ui.queueCategory.disabled = state.busy;
    ui.queueAdd.disabled = state.busy || !ui.queueCompetitor.value;
    renderQueue();
    renderCurrent();
    renderHistory();
    renderLeaderboards();
    setSaveStates();
    renderMasterControls();
  }

  function render() {
    const snapshot = state.snapshot;
    if (!snapshot) return;
    ui.edition.textContent = snapshot.editionName || "Garage Games";
    renderCompetitors();
    renderQueue();
    updateControls();
    tickClock();
  }

  function tickClock() {
    const snapshot = state.snapshot;
    const run = snapshot?.currentRun;
    const limitMs = Number(snapshot?.durationLimitSeconds || 300) * 1000;
    const elapsedMs = run ? currentElapsedMs(run) : 0;
    const remainingMs = Math.max(0, limitMs - elapsedMs);
    const totalSeconds = Math.ceil(remainingMs / 1000);
    ui.countdown.textContent = scorekeeperTime.formatClockMs(totalSeconds * 1000);
    ui.countdown.classList.toggle("is-expired", Boolean(run && remainingMs === 0));
    if (run?.status === "active" && remainingMs === 0 && state.timeoutRefreshRunId !== run.id) {
      state.timeoutRefreshRunId = run.id;
      loadSnapshot(true);
    }
    if (run) {
      ui.runTotal.textContent = String(tableTotal(run));
      const activeRows = ui.currentBody.querySelectorAll("tr");
      activeRows.forEach((row) => rowTiming(row, run));
    }
  }

  function activateTab(index, moveFocus = false) {
    state.activeTab = index;
    ui.tabs.forEach((tab, tabIndex) => {
      const selected = tabIndex === index;
      tab.setAttribute("aria-selected", String(selected));
      tab.tabIndex = selected ? 0 : -1;
      tab.classList.toggle("is-selected", selected);
      ui.tabPanels[tabIndex].hidden = !selected;
    });
    if (moveFocus) ui.tabs[index].focus();
  }

  async function recordCurrentRun() {
    const run = state.snapshot?.currentRun;
    if (!run) throw new Error("There is no run to record.");
    if (run.status === "timedOut") {
      await request(`/api/runs/${encodeURIComponent(run.id)}/record`, { method: "POST" });
      state.selectPromotedAfterRecord = true;
      return;
    }
    await request("/api/run/record", { method: "POST" });
    state.selectPromotedAfterRecord = true;
  }

  async function recordHistoricalRun() {
    const run = (state.snapshot?.history || []).find((item) => item.id === state.selectedHistoryId);
    if (!run) throw new Error("Select a saved run to record.");
    await request(`/api/runs/${encodeURIComponent(run.id)}/record`, { method: "POST" });
    state.selectPromotedAfterRecord = true;
  }

  async function performAction(action, successMessage) {
    if (state.busy) return;
    state.busy = true;
    updateControls();
    try {
      await action();
      if (successMessage) showAlert(successMessage, "success");
    } catch (error) {
      showAlert(error.message || "The action could not be completed.");
    } finally {
      state.busy = false;
      await loadSnapshot(true);
      updateControls();
    }
  }

  async function performMasterAction(action, successMessage) {
    if (state.busy || state.masterBusy) return;
    state.masterBusy = true;
    updateControls();
    try {
      state.master = await action();
      state.masterError = "";
      if (successMessage) showAlert(successMessage, "success");
    } catch (error) {
      showAlert(error.message || "The physical master action could not be completed.");
    } finally {
      state.masterBusy = false;
      await loadMaster(true);
      updateControls();
    }
  }

  async function startRun() {
    const current = state.snapshot?.currentRun;
    const started = await masterActions.startVirtually(request, {
      master: state.master,
      currentRun: current,
      competitorId: ui.competitor.value,
      category: ui.category.value
    }, () => loadSnapshot(true));
    state.discardedRunNotice = null;
    if (started.run) countdownCoordinator.observe(started.run);
    else await countdownCoordinator.poll();
    await removeMatchingQueueEntryAfterStart(started.competitorId, started.category);
  }

  async function armPhysicalRun() {
    await masterActions.armForPhysicalStart(request, {
      master: state.master,
      currentRun: state.snapshot?.currentRun || null,
      competitorId: ui.competitor.value,
      category: ui.category.value
    });
    state.discardedRunNotice = null;
  }

  async function removeMatchingQueueEntryAfterStart(competitorId, category) {
    await loadSnapshot(true);
    const queued = (state.snapshot?.queue || [])
      .slice()
      .sort((a, b) => Number(a.position) - Number(b.position))
      .find((item) => item.competitorId === competitorId && item.category === category);
    if (!queued) return;
    try {
      await request(`/api/queue/${encodeURIComponent(queued.id)}`, { method: "DELETE" });
    } catch (error) {
      await loadSnapshot(true);
      const stillQueued = (state.snapshot?.queue || []).some((item) => item.id === queued.id);
      if (stillQueued) {
        throw new Error(`Run started, but its matching on-deck entry could not be removed: ${error.message}`);
      }
      return;
    }
    await loadSnapshot(true);
  }

  async function pressEvent(run, event) {
    await performAction(
      () => request(`/api/runs/${encodeURIComponent(run.id)}/events/${encodeURIComponent(event.eventId)}/press`, { method: "POST" }),
      event.status === "active" ? `${event.name} finished.` : `${event.name} started.`
    );
  }

  function onScoreInput(event) {
    const input = event.target.closest("input[data-field]");
    if (!input || !input.dataset.runId) return;
    const runId = input.dataset.runId;
    const eventId = input.dataset.eventId;
    const field = input.dataset.field;
    const draft = draftFor(runId, eventId);
    draft[field] = input.value;
    draft.touched.add(field);
    const run = state.snapshot?.currentRun?.id === runId
      ? state.snapshot.currentRun
      : state.snapshot?.history?.find((item) => item.id === runId);
    const row = input.closest("tr");
    if (row && run) rowTiming(row, run);
    if (run) updateTableTotal(input.closest("#current-event-body") ? "current" : "history", run);
    setSaveStates();
  }

  function onBonusInput(event) {
    const input = event.target.closest("input[data-run-id]");
    if (!input?.dataset.runId) return;
    const runId = input.dataset.runId;
    state.bonusDrafts.set(runId, { value: input.value, touched: true });
    const run = state.snapshot?.currentRun?.id === runId
      ? state.snapshot.currentRun
      : state.snapshot?.history?.find((item) => item.id === runId);
    if (run) updateTableTotal(input === ui.currentBonus ? "current" : "history", run);
    setSaveStates();
  }

  function createEditRequest(run) {
    const eventDrafts = state.drafts.get(run.id);
    const bonusDraft = state.bonusDrafts.get(run.id);
    const events = [];
    if (eventDrafts) {
      for (const [eventId, draft] of eventDrafts.entries()) {
        if (!draft.touched.size) continue;
        const edit = { eventId };
        for (const field of draft.touched) {
          const value = draft[field];
          if (field === "start" || field === "finish") {
            if (value === "") {
              edit[field === "start" ? "clearStartElapsedMs" : "clearFinishElapsedMs"] = true;
            } else {
              const milliseconds = scorekeeperTime.elapsedMsFromRemainingSeconds(parsedSeconds(value), runDurationSeconds(run));
              if (milliseconds === null) throw new Error("Enter event times as M:SS remaining or as seconds within the run limit.");
              edit[field === "start" ? "startElapsedMs" : "finishElapsedMs"] = milliseconds;
            }
          } else if (field === "score") {
            if (value === "") {
              edit.clearScoreOverride = true;
            } else {
              const score = Number(value);
              if (!Number.isInteger(score) || score < 0) throw new Error("Points must be a whole number zero or greater, or blank to clear.");
              edit.scoreOverride = score;
            }
          }
        }
        events.push(edit);
      }
    }
    if (!events.length && !bonusDraft?.touched) throw new Error("There are no scorecard edits to save.");
    const request = {
      expectedRevision: run.revision,
      reason: run.status && isLiveLock(run) ? "Operator corrected the current scorecard." : "Operator corrected a saved scorecard.",
      events
    };
    if (bonusDraft?.touched) {
      if (bonusDraft.value === "") {
        request.clearBonusPointsOverride = true;
      } else {
        const points = Number(bonusDraft.value);
        if (!Number.isInteger(points) || points < 0) throw new Error("General run bonus must be a whole number zero or greater, or blank for zero.");
        request.bonusPointsOverride = points;
      }
    }
    return request;
  }

  async function saveCurrentEdits() {
    const run = state.snapshot?.currentRun;
    if (!run) return;
    await performAction(async () => {
      const body = createEditRequest(run);
      await request("/api/run/edit", { method: "PUT", body: JSON.stringify(body) });
      state.drafts.delete(run.id);
      state.bonusDrafts.delete(run.id);
    }, "Current scorecard edits saved.");
  }

  async function saveHistoryEdits() {
    const run = (state.snapshot?.history || []).find((item) => item.id === state.selectedHistoryId);
    if (!run) return;
    await performAction(async () => {
      const body = createEditRequest(run);
      await request(`/api/runs/${encodeURIComponent(run.id)}/edit`, { method: "PUT", body: JSON.stringify(body) });
      state.drafts.delete(run.id);
      state.bonusDrafts.delete(run.id);
    }, "Saved run corrections updated.");
  }

  async function clearDatabaseForTesting() {
    if (state.busy) return;
    if (ui.clearDatabaseConfirmation.value !== clearDatabasePhrase) {
      showAlert(`Type ${clearDatabasePhrase} exactly before clearing the database.`);
      return;
    }
    const confirmed = window.confirm(
      "Clear ALL Garage Games data on this computer? A timestamped database backup will be created first and kept. Competitors, queued players, runs, event times and scores, messages, edits, selections, and device status will be cleared. This cannot be undone from the app."
    );
    if (!confirmed) return;

    state.busy = true;
    ui.clearDatabaseResult.textContent = "Creating a backup before clearing…";
    updateControls();
    try {
      const result = await request("/api/testing/clear-database", {
        method: "POST",
        body: JSON.stringify({ confirmationPhrase: ui.clearDatabaseConfirmation.value })
      });
      state.drafts.clear();
      state.bonusDrafts.clear();
      state.selectedHistoryId = null;
      state.selectedCompetitorAfterRefresh = null;
      state.queueCompetitorAfterRefresh = null;
      state.selectedLeaderboardCompetitorId = "";
      state.discardedRunNotice = null;
      state.selectPromotedAfterRecord = false;
      ui.clearDatabaseConfirmation.value = "";
      const backupPath = result?.backupPath || "Path was not returned by the server.";
      ui.clearDatabaseResult.textContent = `Database cleared. The backup was retained at: ${backupPath}`;
      await loadSnapshot(true);
      showAlert(`Test database cleared. Backup retained at: ${backupPath}`, "success");
    } catch (error) {
      ui.clearDatabaseResult.textContent = "The database was not cleared. If backup creation failed, no data was removed.";
      showAlert(error.message || "The database could not be cleared.");
    } finally {
      state.busy = false;
      updateControls();
    }
  }

  function bindActions() {
    ui.tabs.forEach((tab, index) => {
      tab.addEventListener("click", () => activateTab(index));
      tab.addEventListener("keydown", (event) => {
        let next = null;
        if (event.key === "ArrowRight") next = (index + 1) % ui.tabs.length;
        else if (event.key === "ArrowLeft") next = (index + ui.tabs.length - 1) % ui.tabs.length;
        else if (event.key === "Home") next = 0;
        else if (event.key === "End") next = ui.tabs.length - 1;
        if (next === null) return;
        event.preventDefault();
        activateTab(next, true);
      });
    });
    ui.refresh.addEventListener("click", () => loadSnapshot(false));
    ui.countdownRetry.addEventListener("click", () => countdownCoordinator.retry());
    ui.armPhysical.addEventListener("click", () => performAction(armPhysicalRun, "Run armed · waiting for the physical Start button."));
    ui.start.addEventListener("click", () => performAction(startRun, "Countdown started. Run begins at Go."));
    ui.masterConnect.addEventListener("click", () => {
      const port = ui.masterPort.value;
      if (!port) return;
      performMasterAction(
        () => masterActions.connectMaster(request, port),
        `Connected to ${port}.`
      );
    });
    ui.masterDisconnect.addEventListener("click", () => performMasterAction(
      () => masterActions.disconnectMaster(request),
      "Physical master disconnected."
    ));
    ui.masterRefresh.addEventListener("click", () => loadMaster(false));
    ui.masterPort.addEventListener("change", renderMasterControls);
    ui.pause.addEventListener("click", () => {
      const path = state.snapshot?.currentRun?.status === "paused" ? "/api/run/resume" : "/api/run/pause";
      performAction(() => request(path, { method: "POST" }), path.endsWith("resume") ? "Run resumed." : "Run paused.");
    });
    ui.finish.addEventListener("click", () => performAction(
      () => request("/api/run/finish", { method: "POST" }),
      "Run finished · not recorded yet."
    ));
    ui.discard.addEventListener("click", () => {
      const run = state.snapshot?.currentRun;
      if (!runActions.isDiscardableRun(run)) return;
      const competitor = competitorName(run.competitorId);
      const confirmation = window.confirm(
        `Discard ${competitor}'s current run? It will remain in run history as Aborted, but will not be recorded or counted as a result. ${competitor} will stay selected so you can start a fresh run.`
      );
      if (!confirmation) return;
      const notice = `${competitor}'s run was discarded. It remains in run history as Aborted, but was not recorded or counted. ${competitor} is still selected; start a fresh run when ready.`;
      const reason = `Operator discarded unrecorded run for ${competitor} from the scorekeeper.`;
      performAction(async () => {
        await request("/api/run/abort", {
          method: "POST",
          body: JSON.stringify({ reason })
        });
        countdownCoordinator.observe({ runId: run.id, status: "aborted" });
        state.selectedCompetitorAfterRefresh = run.competitorId;
        state.discardedRunNotice = notice;
        state.drafts.delete(run.id);
        state.bonusDrafts.delete(run.id);
      }, notice);
    });
    ui.record.addEventListener("click", () => performAction(
      recordCurrentRun,
      "Run recorded."
    ));
    ui.historyRecord.addEventListener("click", () => performAction(recordHistoricalRun, "Saved run recorded."));
    ui.currentSave.addEventListener("click", saveCurrentEdits);
    ui.historySave.addEventListener("click", saveHistoryEdits);
    ui.currentBody.addEventListener("input", onScoreInput);
    ui.historyBody.addEventListener("input", onScoreInput);
    ui.currentBonus.addEventListener("input", onBonusInput);
    ui.historyBonus.addEventListener("input", onBonusInput);
    ui.addForm.addEventListener("submit", (event) => {
      event.preventDefault();
      const name = ui.newCompetitor.value.trim();
      if (!name) return;
      performAction(async () => {
        const competitor = await request("/api/competitors", { method: "POST", body: JSON.stringify({ name }) });
        state.selectedCompetitorAfterRefresh = competitor.id;
        state.queueCompetitorAfterRefresh = competitor.id;
        ui.newCompetitor.value = "";
      }, "Competitor added.");
    });
    ui.queueForm.addEventListener("submit", (event) => {
      event.preventDefault();
      const competitorId = ui.queueCompetitor.value;
      const category = ui.queueCategory.value;
      if (!competitorId) return;
      performAction(
        () => request("/api/queue", {
          method: "POST",
          body: JSON.stringify({ competitorId, category })
        }),
        "Added to on deck."
      );
    });
    ui.competitor.addEventListener("change", updateControls);
    ui.queueCompetitor.addEventListener("change", updateControls);
    ui.leaderboardPlayerSelect.addEventListener("change", () => {
      state.selectedLeaderboardCompetitorId = ui.leaderboardPlayerSelect.value;
      state.playerLeaderboardKey = null;
      renderLeaderboards();
    });
    ui.clearDatabaseConfirmation.addEventListener("input", updateControls);
    ui.clearDatabaseButton.addEventListener("click", clearDatabaseForTesting);
  }

  bindActions();
  loadSnapshot(false);
  loadMaster(false);
  void countdownCoordinator.poll();
  window.setInterval(() => loadSnapshot(true), 2000);
  window.setInterval(() => loadMaster(true), 2000);
  window.setInterval(() => countdownCoordinator.poll(), 250);
  window.setInterval(tickClock, 200);
})();
