(() => {
  "use strict";

  const state = {
    snapshot: null,
    busy: false,
    drafts: new Map(),
    selectedHistoryId: null,
    selectedCompetitorAfterRefresh: null,
    competitorSignature: "",
    historySignature: "",
    queueSignature: "",
    currentTableKey: null,
    historyTableKey: null,
    virtualKey: null,
    initializedSelection: false,
    receivedAt: Date.now(),
    timeoutRefreshRunId: null,
    alertTimer: null
  };

  const $ = (id) => document.getElementById(id);
  const scorekeeperTime = window.GarageGamesScorekeeperTime;
  const ui = {
    edition: $("edition-name"),
    connection: $("connection-status"),
    lastUpdated: $("last-updated"),
    refresh: $("refresh-button"),
    alert: $("alert-region"),
    tabs: [$("tab-scorekeeping"), $("tab-on-deck"), $("tab-history")],
    tabPanels: [$("panel-scorekeeping"), $("panel-on-deck"), $("panel-history")],
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
    pause: $("pause-run-button"),
    finish: $("finish-run-button"),
    record: $("record-run-button"),
    countdown: $("run-countdown"),
    runCompetitor: $("run-competitor"),
    progress: $("event-progress"),
    runTotal: $("run-total"),
    banner: $("run-state-banner"),
    virtualButtons: $("virtual-event-buttons"),
    virtualNote: $("virtual-note"),
    currentBody: $("current-event-body"),
    currentTotal: $("scorecard-total"),
    currentSave: $("save-current-edits"),
    currentSaveState: $("scorecard-save-state"),
    historyCount: $("history-count"),
    historyList: $("history-list"),
    historyTitle: $("history-editor-title"),
    historyStatus: $("history-editor-status"),
    historyMeta: $("history-editor-meta"),
    historyBody: $("history-event-body"),
    historyTotal: $("history-total"),
    historySave: $("save-history-edits"),
    historySaveState: $("history-save-state"),
    historyRecord: $("record-history-run")
  };

  function isLiveLock(run) {
    return Boolean(run && ["armed", "active", "paused", "finished"].includes(run.status));
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
      const message = payload && (payload.detail || payload.title || payload.message);
      throw new Error(message || raw || `Request failed (${response.status}).`);
    }
    return payload;
  }

  function setConnection(online) {
    ui.connection.classList.toggle("is-online", online);
    ui.connection.classList.toggle("is-offline", !online);
    const label = ui.connection.querySelector("span");
    if (label) label.textContent = online ? "Connected" : "Offline";
  }

  async function loadSnapshot(silent = false) {
    try {
      const snapshot = await request("/api/operator");
      state.snapshot = snapshot;
      state.receivedAt = Date.now();
      state.timeoutRefreshRunId = null;
      setConnection(true);
      ui.lastUpdated.textContent = new Date(state.receivedAt).toLocaleTimeString([], { hour: "numeric", minute: "2-digit", second: "2-digit" });
      if (!state.initializedSelection) {
        if (isLiveLock(snapshot.currentRun)) ui.competitor.value = snapshot.currentRun.competitorId;
        state.initializedSelection = true;
      }
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
    } else if (preferred && competitors.some((item) => item.id === preferred)) {
      ui.competitor.value = preferred;
      state.selectedCompetitorAfterRefresh = null;
    } else if (competitors.some((item) => item.id === previousValue)) {
      ui.competitor.value = previousValue;
    }
    if (competitors.some((item) => item.id === previousQueueValue)) ui.queueCompetitor.value = previousQueueValue;
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

  function parsedSeconds(value) {
    if (value === "") return null;
    const number = Number(value);
    return Number.isFinite(number) && number >= 0 ? number : null;
  }

  function runDurationSeconds(run) {
    return Number(run?.edition?.durationLimitSeconds || state.snapshot?.durationLimitSeconds || 300);
  }

  function formatSeconds(milliseconds) {
    if (milliseconds === null || milliseconds === undefined) return "";
    const seconds = Number(milliseconds) / 1000;
    return Number.isFinite(seconds) ? String(Number(seconds.toFixed(3))) : "";
  }

  function formatDuration(milliseconds) {
    if (milliseconds === null || milliseconds === undefined || milliseconds < 0) return "—";
    const tenths = Math.floor(milliseconds / 100);
    const seconds = Math.floor(tenths / 10) % 60;
    const minutes = Math.floor(tenths / 600);
    return `${minutes}:${String(seconds).padStart(2, "0")}.${tenths % 10}`;
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

  function tableTotal(run) {
    if (!run) return 0;
    return runEvents(run).reduce((sum, event) => sum + displayedScore(run, event), 0);
  }

  function updateTableTotal(kind, run) {
    const total = tableTotal(run);
    if (kind === "current") ui.currentTotal.textContent = String(total);
    else ui.historyTotal.textContent = String(total);
    if (kind === "current") ui.runTotal.textContent = String(total);
  }

  function buildTimeInput(run, event, field, editable) {
    const input = make("input", "score-input");
    input.type = "number";
    input.min = "0";
    input.max = String(runDurationSeconds(run));
    input.step = "0.001";
    input.inputMode = "decimal";
    input.setAttribute("aria-label", `${event.name} ${field === "start" ? "start" : "stop"} time in seconds remaining`);
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
    const currentDirty = current && hasDrafts(current.id);
    const currentEditable = Boolean(current && isLiveLock(current));
    ui.currentSave.disabled = state.busy || !currentDirty || !currentEditable;
    ui.currentSaveState.textContent = currentDirty
      ? (currentEditable ? "Unsaved edits" : "Use the history editor to correct this saved run")
      : "No unsaved edits";

    const selected = (state.snapshot?.history || []).find((run) => run.id === state.selectedHistoryId);
    const historyDirty = selected && hasDrafts(selected.id);
    const historyEditable = Boolean(selected && !isLiveLock(selected));
    ui.historySave.disabled = state.busy || !historyDirty || !historyEditable;
    ui.historySaveState.textContent = historyDirty
      ? (historyEditable ? "Unsaved edits · save when ready" : "Live runs are edited in the current scorecard")
      : "History edits are saved explicitly.";
  }

  function renderCurrent() {
    const run = state.snapshot?.currentRun || null;
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
      ui.banner.textContent = "No run is active. Select a competitor to begin.";
      ui.caption.textContent = "Choose a competitor, then start the five-minute clock.";
    } else {
      const label = categoryLabel(run.category);
      const competitor = competitorName(run.competitorId);
      const copy = {
        armed: "Run is armed and ready to start.",
        active: "Run in progress. Event timestamps count down from the run limit.",
        paused: "Run paused. Resume when the competitor is ready.",
        finished: "All events complete. Timer stopped · finished, not recorded.",
        completed: "Run finished · recorded.",
        timedOut: run.isRecorded ? "Time expired · incomplete result recorded." : "Time expired · incomplete result not recorded.",
        aborted: "Run aborted.",
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
        hint = `Stop · ${formatSeconds(remainingMs)}s left`;
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
        const remove = make("button", "button button-quiet on-deck-remove", "Remove");
        remove.type = "button";
        remove.disabled = state.busy;
        remove.setAttribute("aria-label", `Remove ${competitorName(item.competitorId)} (${categoryLabel(item.category)}) from on deck`);
        remove.addEventListener("click", () => performAction(
          () => request(`/api/queue/${encodeURIComponent(item.id)}`, { method: "DELETE" }),
          `${competitorName(item.competitorId)} removed from on deck.`
        ));
        row.append(position, details, remove);
        ui.queueList.appendChild(row);
      });
    }
    state.queueSignature = signature;
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
          button.setAttribute("aria-pressed", String(run.id === state.selectedHistoryId));
          const primary = make("span", "history-primary");
          primary.append(
            make("strong", "", competitorName(run.competitorId)),
            make("span", "", `${categoryLabel(run.category)} · ${shortDate(run.createdAt)}`)
          );
          const points = make("strong", "history-points", String((run.events || []).reduce((sum, event) => sum + Number(event.score || 0), 0)));
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
    ui.start.disabled = state.busy || (!locked && !selectedId) || (locked && run.status !== "armed");
    ui.start.textContent = run?.status === "armed" ? "Start armed run" : "Start 5-minute run";
    ui.pause.disabled = state.busy || !run || !["active", "paused"].includes(run.status);
    ui.pause.textContent = run?.status === "paused" ? "Resume" : "Pause";
    ui.finish.disabled = state.busy || !run || !["armed", "active", "paused"].includes(run.status);
    ui.record.disabled = state.busy || !run || run.isRecorded || !["armed", "active", "paused", "finished", "timedOut"].includes(run.status);
    ui.record.textContent = run?.isRecorded ? "Already recorded" : "Record result";
    ui.refresh.disabled = state.busy;
    ui.queueCompetitor.disabled = state.busy;
    ui.queueCategory.disabled = state.busy;
    ui.queueAdd.disabled = state.busy || !ui.queueCompetitor.value;
    renderQueue();
    renderCurrent();
    renderHistory();
    setSaveStates();
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
    ui.countdown.textContent = `${String(Math.floor(totalSeconds / 60)).padStart(2, "0")}:${String(totalSeconds % 60).padStart(2, "0")}`;
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
      return;
    }
    await request("/api/run/record", { method: "POST" });
  }

  async function recordHistoricalRun() {
    const run = (state.snapshot?.history || []).find((item) => item.id === state.selectedHistoryId);
    if (!run) throw new Error("Select a saved run to record.");
    await request(`/api/runs/${encodeURIComponent(run.id)}/record`, { method: "POST" });
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

  async function startRun() {
    const current = state.snapshot?.currentRun;
    let competitorId;
    let category;
    if (current?.status === "armed") {
      competitorId = current.competitorId;
      category = current.category;
      await request("/api/run/start", { method: "POST" });
    } else {
      competitorId = ui.competitor.value;
      category = ui.category.value;
      if (!competitorId) throw new Error("Choose a competitor before starting a run.");
      await request("/api/run/arm", {
        method: "POST",
        body: JSON.stringify({ competitorId, category })
      });
      await loadSnapshot(true);
      await request("/api/run/start", { method: "POST" });
    }
    await removeMatchingQueueEntryAfterStart(competitorId, category);
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

  function createEditRequest(run) {
    const eventDrafts = state.drafts.get(run.id);
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
              if (milliseconds === null) throw new Error("Event times must be zero or more seconds.");
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
    if (!events.length) throw new Error("There are no scorecard edits to save.");
    return {
      expectedRevision: run.revision,
      reason: run.status && isLiveLock(run) ? "Operator corrected the current scorecard." : "Operator corrected a saved scorecard.",
      events
    };
  }

  async function saveCurrentEdits() {
    const run = state.snapshot?.currentRun;
    if (!run) return;
    await performAction(async () => {
      const body = createEditRequest(run);
      await request("/api/run/edit", { method: "PUT", body: JSON.stringify(body) });
      state.drafts.delete(run.id);
    }, "Current scorecard edits saved.");
  }

  async function saveHistoryEdits() {
    const run = (state.snapshot?.history || []).find((item) => item.id === state.selectedHistoryId);
    if (!run) return;
    await performAction(async () => {
      const body = createEditRequest(run);
      await request(`/api/runs/${encodeURIComponent(run.id)}/edit`, { method: "PUT", body: JSON.stringify(body) });
      state.drafts.delete(run.id);
    }, "Saved run corrections updated.");
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
    ui.start.addEventListener("click", () => performAction(startRun, "Run started."));
    ui.pause.addEventListener("click", () => {
      const path = state.snapshot?.currentRun?.status === "paused" ? "/api/run/resume" : "/api/run/pause";
      performAction(() => request(path, { method: "POST" }), path.endsWith("resume") ? "Run resumed." : "Run paused.");
    });
    ui.finish.addEventListener("click", () => performAction(
      () => request("/api/run/finish", { method: "POST" }),
      "Run finished · not recorded yet."
    ));
    ui.record.addEventListener("click", () => performAction(
      recordCurrentRun,
      "Run recorded."
    ));
    ui.historyRecord.addEventListener("click", () => performAction(recordHistoricalRun, "Saved run recorded."));
    ui.currentSave.addEventListener("click", saveCurrentEdits);
    ui.historySave.addEventListener("click", saveHistoryEdits);
    ui.currentBody.addEventListener("input", onScoreInput);
    ui.historyBody.addEventListener("input", onScoreInput);
    ui.addForm.addEventListener("submit", (event) => {
      event.preventDefault();
      const name = ui.newCompetitor.value.trim();
      if (!name) return;
      performAction(async () => {
        const competitor = await request("/api/competitors", { method: "POST", body: JSON.stringify({ name }) });
        state.selectedCompetitorAfterRefresh = competitor.id;
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
  }

  bindActions();
  loadSnapshot(false);
  window.setInterval(() => loadSnapshot(true), 2000);
  window.setInterval(tickClock, 200);
})();
