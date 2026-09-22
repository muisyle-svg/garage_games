(() => {
  "use strict";

  const operatorPage = document.body.classList.contains("operator-view");
  const scoreboardPage = document.body.classList.contains("scoreboard-view");

  // Keep every write in one small route map. If the server route changes, this is the only
  // frontend contract table that needs updating; the views do not create local game state.
  const API = Object.freeze({
    operator: "/api/operator",
    scoreboard: "/api/scoreboard",
    competitor: "/api/competitors",
    queue: "/api/queue",
    queueReorder: "/api/queue/reorder",
    queueArm: (id) => `/api/queue/${encodeURIComponent(id)}/arm`,
    queueRemove: (id) => `/api/queue/${encodeURIComponent(id)}`,
    start: "/api/run/start",
    pause: "/api/run/pause",
    resume: "/api/run/resume",
    finish: "/api/run/finish",
    abort: "/api/run/abort",
    restart: (id) => `/api/runs/${encodeURIComponent(id)}/restart`,
    editCurrent: "/api/run/edit",
    edit: (id) => `/api/runs/${encodeURIComponent(id)}/edit`,
    undo: (id) => `/api/runs/${encodeURIComponent(id)}/undo`,
    preflight: "/api/devices/preflight",
    availability: (id) => `/api/devices/${encodeURIComponent(id)}/availability`,
    backup: "/api/backup",
    export: "/api/export",
    simulatorInput: "/api/simulator/input",
    simulatorAdvance: "/api/simulator/advance-clock"
  });

  const state = {
    connected: false,
    snapshot: null,
    scoreboard: null,
    lastGoodAt: null,
    pollInFlight: false,
    activeTab: "current-panel",
    editors: { active: null, history: null },
    simDeviceId: "",
    alertTimer: 0
  };

  const q = (selector, root = document) => root.querySelector(selector);
  const qa = (selector, root = document) => Array.from(root.querySelectorAll(selector));
  const text = (element, value) => { if (element) element.textContent = value == null ? "" : String(value); };
  const clear = (element) => { if (element) while (element.firstChild) element.removeChild(element.firstChild); };
  const clone = (value) => value == null ? value : JSON.parse(JSON.stringify(value));
  const get = (object, key, fallback = null) => object && object[key] !== undefined ? object[key] : fallback;
  const numberValue = (value, fallback = null) => {
    if (value === "" || value == null) return fallback;
    const result = Number(value);
    return Number.isFinite(result) ? result : fallback;
  };
  const make = (tag, className, value) => {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (value !== undefined && value !== null) element.textContent = String(value);
    return element;
  };
  const button = (label, className = "tiny-button", type = "button") => {
    const element = make("button", className, label);
    element.type = type;
    return element;
  };
  const uuid = () => {
    if (window.crypto && typeof window.crypto.randomUUID === "function") return window.crypto.randomUUID();
    return `sim-${Date.now()}-${Math.random().toString(16).slice(2)}`;
  };

  const labels = {
    official: "Official",
    playoff: "Playoff",
    exhibition: "Exhibition",
    pending: "Pending",
    active: "Active",
    completed: "Completed",
    armed: "Armed",
    paused: "Paused",
    timedOut: "Timed out",
    aborted: "Aborted",
    superseded: "Superseded",
    normal: "Normal phase",
    bonus: "Bonus phase",
    standard: "Standard",
    keypad: "Keypad",
    magneticArcade: "Arcade",
    online: "Online",
    offline: "Offline",
    error: "Error",
    ready: "Ready",
    eventAvailable: "Available",
    eventActive: "Active",
    eventCompleted: "Complete",
    bonusLed: "Bonus",
    pausedLed: "Paused",
    offlineError: "Offline / error",
    runFinished: "Run finished"
  };
  const pretty = (value) => labels[value] || String(value || "—").replace(/([a-z])([A-Z])/g, "$1 $2").replace(/^./, (match) => match.toUpperCase());
  const categoryClass = (value) => `category-${value || "unknown"}`;
  const statusClass = (value) => `status-${value || "unknown"}`;
  const eventTypeLabel = (value) => pretty(value);
  const formatMs = (milliseconds) => {
    const seconds = Math.max(0, Math.floor(Number(milliseconds || 0) / 1000));
    const minutes = Math.floor(seconds / 60);
    return `${String(minutes).padStart(2, "0")}:${String(seconds % 60).padStart(2, "0")}`;
  };
  const formatDuration = (milliseconds) => milliseconds == null ? "—" : `${(Number(milliseconds) / 1000).toFixed(1)}s`;
  const formatDate = (value) => {
    if (!value) return "Unknown time";
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? "Unknown time" : date.toLocaleString([], { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" });
  };
  const competitorMap = (snapshot) => new Map((get(snapshot, "competitors", []) || []).map((item) => [item.id, item.name]));
  const competitorName = (snapshot, id) => competitorMap(snapshot).get(id) || "Unknown competitor";
  const runTotal = (run) => (get(run, "events", []) || []).reduce((total, event) => total + Number(event.score || 0), 0) + Number(run && run.bonusPointsOverride || 0);
  const runDuration = (event) => event && event.startElapsedMs != null && event.finishElapsedMs != null ? Number(event.finishElapsedMs) - Number(event.startElapsedMs) : null;
  const eventStatus = (event) => get(event, "status", "pending");
  const currentCanReceiveInput = () => state.connected && !!state.snapshot && !!state.snapshot.currentRun && state.snapshot.currentRun.status === "active";

  async function request(url, options = {}) {
    const config = { cache: "no-store", ...options, headers: { Accept: "application/json", ...(options.headers || {}) } };
    if (config.body !== undefined && typeof config.body !== "string") {
      config.body = JSON.stringify(config.body);
      config.headers["Content-Type"] = "application/json";
    }
    const response = await fetch(url, config);
    const raw = await response.text();
    let data = null;
    if (raw) {
      try { data = JSON.parse(raw); } catch { data = raw; }
    }
    if (!response.ok) {
      const message = data && typeof data === "object" ? (data.message || data.error || data.title) : data;
      throw new Error(message || `The server returned ${response.status}.`);
    }
    return data;
  }

  function showAlert(message, kind = "error") {
    if (!operatorPage) return;
    const region = q("#alert-region");
    clear(region);
    const alert = make("div", `alert${kind === "success" ? " success" : ""}`, message);
    alert.setAttribute("role", "alert");
    region.appendChild(alert);
    window.clearTimeout(state.alertTimer);
    state.alertTimer = window.setTimeout(() => clear(region), 6500);
  }

  function setConnection(connected, lastSync) {
    state.connected = connected;
    const suffix = operatorPage ? "#connection-status" : "#scoreboard-connection";
    const element = q(suffix);
    if (element) {
      element.classList.toggle("is-stale", !connected);
      const statusText = element.querySelector("span:last-child");
      text(statusText, connected ? "Connected" : "Connection lost");
    }
    const sync = q(operatorPage ? "#last-sync" : "#scoreboard-sync");
    if (sync) text(sync, connected && lastSync ? `Updated ${lastSync.toLocaleTimeString([], { hour: "numeric", minute: "2-digit", second: "2-digit" })}` : "Waiting for server");
    const stale = q(operatorPage ? "#stale-banner" : "#scoreboard-stale");
    if (stale) stale.classList.toggle("is-hidden", connected);
  }

  async function perform(label, url, body, successMessage = `${label} complete.`, resultFeedback = null) {
    if (!state.connected) {
      showAlert("The local server is not connected. No change was made.");
      return null;
    }
    try {
      const result = await request(url, { method: "POST", body });
      const feedback = typeof resultFeedback === "function" ? resultFeedback(result) : null;
      showAlert(feedback && feedback.message || successMessage, feedback && feedback.kind || "success");
      if (operatorPage) await pollOperator();
      return result;
    } catch (error) {
      showAlert(`${label} could not be completed: ${error.message}`);
      if (operatorPage) await pollOperator();
      return null;
    }
  }

  function switchPanel(panelId) {
    state.activeTab = panelId;
    qa(".tab").forEach((tab) => tab.classList.toggle("is-active", tab.dataset.panel === panelId));
    qa(".tab-panel").forEach((panel) => {
      const visible = panel.id === panelId;
      panel.classList.toggle("is-visible", visible);
      panel.hidden = !visible;
    });
  }

  function bindTabNavigation() {
    qa(".tab").forEach((tab) => tab.addEventListener("click", () => switchPanel(tab.dataset.panel)));
    qa("[data-jump]").forEach((link) => link.addEventListener("click", () => switchPanel(link.dataset.jump)));
  }

  function setMode(mode, edition, target) {
    const element = q(target);
    if (!element) return;
    element.classList.toggle("mode-badge", true);
    text(element, mode ? "SIMULATION" : "Hardware connection unavailable");
    if (edition) {
      const footer = q(target === "#mode-badge" ? "#footer-edition" : "#scoreboard-footer-edition");
      text(footer, edition);
    }
  }

  function setSelectOptions(select, entries, selected, includeEmpty = false, emptyLabel = "Select competitor…") {
    if (!select) return;
    const desired = [];
    if (includeEmpty) desired.push({ value: "", label: emptyLabel });
    entries.forEach((entry) => desired.push({ value: entry.value, label: entry.label }));
    const current = Array.from(select.options).map((option) => ({ value: option.value, label: option.textContent }));
    const unchanged = current.length === desired.length && current.every((option, index) => option.value === desired[index].value && option.label === desired[index].label);
    const chosen = selected != null && desired.some((entry) => entry.value === selected) ? selected : select.value;
    if (unchanged) {
      if (chosen !== select.value && document.activeElement !== select) select.value = chosen;
      return;
    }
    const focused = document.activeElement === select;
    clear(select);
    desired.forEach((entry) => {
      const option = make("option", "", entry.label);
      option.value = entry.value;
      option.selected = entry.value === chosen;
      select.appendChild(option);
    });
    if (focused) select.focus();
  }

  function updateOptionValue(select, value) {
    if (!select) return;
    const option = Array.from(select.options).find((item) => item.value === value);
    if (option) select.value = value;
  }

  function runForEditor(kind) {
    const snapshot = state.snapshot;
    if (!snapshot || !state.editors[kind]) return null;
    if (kind === "active") return snapshot.currentRun;
    return (snapshot.history || []).find((run) => run.id === state.editors[kind].runId) || null;
  }

  async function pollOperator() {
    if (state.pollInFlight) return;
    state.pollInFlight = true;
    let snapshot;
    try {
      snapshot = await request(API.operator);
    } catch (error) {
      setConnection(false, state.lastGoodAt);
      if (operatorPage) showAlert(`The local server could not be reached: ${error.message}`);
      // Keep the last snapshot on screen. This prevents a poll failure from erasing a scorekeeper's context.
      state.pollInFlight = false;
      return;
    }
    state.snapshot = snapshot;
    state.lastGoodAt = new Date();
    setConnection(true, state.lastGoodAt);
    try {
      renderOperator(snapshot);
    } catch (error) {
      reportRenderFailure(error, "operator");
    } finally {
      state.pollInFlight = false;
    }
  }

  function reportRenderFailure(error, view) {
    console.error(`Garage Games ${view} display error`, error);
    const message = `The server replied, but this page could not update: ${error.message}. Reload the page after updating its files.`;
    if (view === "operator") {
      showAlert(message);
      return;
    }
    const stale = q("#scoreboard-stale");
    if (stale) {
      clear(stale);
      stale.classList.remove("is-hidden");
      stale.appendChild(make("strong", "", "Display error. "));
      stale.appendChild(document.createTextNode(message));
    }
  }

  function renderOperator(snapshot) {
    setMode(!!snapshot.simulationMode, snapshot.editionName, "#mode-badge");
    text(q("#footer-edition"), snapshot.editionName || "Edition unavailable");
    renderCurrent(snapshot);
    renderCompetitors(snapshot);
    renderQueue(snapshot);
    renderHistory(snapshot);
    renderDevices(snapshot);
    renderRaw(snapshot);
    updateEditorsAfterPoll(snapshot);
    renderSimulator(snapshot);
  }

  function renderCurrent(snapshot) {
    const run = snapshot.currentRun;
    const totalEvents = run && run.events ? run.events.length : 13;
    text(q("#current-competitor"), run ? competitorName(snapshot, run.competitorId) : "No run armed");
    const category = q("#current-category");
    text(category, run ? pretty(run.category) : "—");
    category.className = `category-pill${run ? ` ${categoryClass(run.category)}` : ""}`;
    const countdown = run ? Math.max(0, (run.edition && run.edition.durationLimitSeconds || snapshot.durationLimitSeconds) * 1000 - Number(run.activeElapsedMs || 0)) : Number(snapshot.durationLimitSeconds || 300) * 1000;
    text(q("#current-countdown"), formatMs(countdown));
    text(q("#current-status"), run ? pretty(run.status) : "Waiting for an armed run");
    text(q("#current-score"), runTotal(run));
    const completed = run ? (run.events || []).filter((event) => event.status === "completed").length : 0;
    text(q("#event-progress"), `${completed} / ${totalEvents}`);
    const phase = q("#phase-chip");
    text(phase, run ? pretty(run.phase) : "Normal phase");
    phase.className = `phase-chip${run && run.phase === "bonus" ? " phase-bonus" : " phase-normal"}`;
    const next = snapshot.queue && snapshot.queue[0];
    text(q("#on-deck-label"), `On deck: ${next ? competitorName(snapshot, next.competitorId) : "—"}`);
    renderEventGrid(snapshot, run);
    renderOnDeck(snapshot);
    renderLiveEditLog(snapshot);

    const live = state.connected && !!run;
    const start = q("#start-run-button");
    const pause = q("#pause-run-button");
    const abort = q("#abort-run-button");
    if (start) start.disabled = !state.connected || !run || run.status !== "armed";
    if (pause) { pause.disabled = !state.connected || !run || !["active", "paused"].includes(run.status); text(pause, run && run.status === "paused" ? "Resume" : "Pause"); }
    if (abort) abort.disabled = !state.connected || !run || !["armed", "active", "paused"].includes(run.status);
    const finish = q("#finish-run-button");
    if (finish) finish.disabled = !state.connected || !run || !["armed", "active", "paused"].includes(run.status);
    q("#current-tab-count").textContent = run ? `${completed}/${totalEvents}` : "—";
    if (!live && state.editors.active) closeEditor("active");
  }

  function renderEventGrid(snapshot, run) {
    const body = q("#event-grid-body");
    clear(body);
    if (!run || !run.events || run.events.length === 0) {
      const row = make("tr");
      const cell = make("td", "empty-cell", "Arm a competitor to load the configured event roster.");
      cell.colSpan = 8;
      row.appendChild(cell);
      body.appendChild(row);
      return;
    }
    run.events.forEach((event) => {
      const row = make("tr");
      const name = make("td");
      name.appendChild(make("span", "event-name", event.name));
      name.appendChild(make("span", "event-device", event.deviceId));
      row.appendChild(name);
      const type = make("td");
      type.appendChild(make("span", "event-type", eventTypeLabel(event.type)));
      row.appendChild(type);
      row.appendChild(make("td", "numeric-cell", event.startElapsedMs == null ? "—" : `${event.startElapsedMs} ms`));
      row.appendChild(make("td", "numeric-cell", event.finishElapsedMs == null ? "—" : `${event.finishElapsedMs} ms`));
      row.appendChild(make("td", "numeric-cell", formatDuration(runDuration(event))));
      const status = make("span", `status-text ${statusClass(event.status)}`, pretty(event.status));
      const statusCell = make("td"); statusCell.appendChild(status); row.appendChild(statusCell);
      const score = make("span", `score-value${event.scoreOverride != null ? " score-overridden" : ""}`, event.score || 0);
      const scoreCell = make("td"); scoreCell.appendChild(score); if (event.scoreOverride != null) scoreCell.appendChild(make("span", "event-device", "override")); row.appendChild(scoreCell);
      const actionCell = make("td");
      const edit = button("Edit", "tiny-button tiny-primary");
      edit.disabled = !state.connected;
      edit.addEventListener("click", () => openEditor("active", run));
      actionCell.appendChild(edit); row.appendChild(actionCell);
      body.appendChild(row);
    });
  }

  function renderOnDeck(snapshot) {
    const list = q("#on-deck-list");
    clear(list);
    const queue = snapshot.queue || [];
    if (!queue.length) { list.appendChild(make("p", "empty-copy", "No competitor queued.")); return; }
    queue.slice(0, 4).forEach((item, index) => {
      const row = make("div", "mini-row");
      const primary = make("div", "mini-primary");
      primary.appendChild(make("strong", "", competitorName(snapshot, item.competitorId)));
      primary.appendChild(make("span", "", `${pretty(item.category)}${index === 0 ? " · next" : ""}`));
      row.appendChild(primary); row.appendChild(make("span", "mini-rank", String(index + 1).padStart(2, "0"))); list.appendChild(row);
    });
  }

  function renderLiveEditLog(snapshot) {
    const list = q("#live-edit-log");
    clear(list);
    const runId = snapshot.currentRun && snapshot.currentRun.id;
    const edits = (snapshot.edits || []).filter((edit) => edit.runId === runId).slice(0, 3);
    if (!edits.length) { list.appendChild(make("p", "empty-copy", "No corrections loaded.")); return; }
    edits.forEach((edit) => {
      const row = make("div", "mini-row");
      const primary = make("div", "mini-primary");
      primary.appendChild(make("strong", "", edit.reason));
      primary.appendChild(make("span", "", formatDate(edit.createdAt)));
      row.appendChild(primary); row.appendChild(make("span", "mini-rank", `#${edit.id}`)); list.appendChild(row);
    });
  }

  function renderCompetitors(snapshot) {
    const list = q("#competitor-list");
    const select = q("#queue-competitor");
    clear(list);
    const competitors = snapshot.competitors || [];
    const entries = competitors.map((item) => ({ value: item.id, label: item.name }));
    const selected = select && select.value;
    setSelectOptions(select, entries, selected, true);
    if (!competitors.length) { list.appendChild(make("p", "empty-copy", "No competitors yet.")); }
    competitors.forEach((competitor) => {
      const row = make("div", "record-row");
      const primary = make("div", "record-primary"); primary.appendChild(make("strong", "", competitor.name)); primary.appendChild(make("span", "", "Competitor"));
      row.appendChild(primary); row.appendChild(make("span", "muted", formatDate(competitor.createdAt))); list.appendChild(row);
    });
    text(q("#queue-tab-count"), `${(snapshot.queue || []).length}`);
  }

  function renderQueue(snapshot) {
    const list = q("#queue-list");
    clear(list);
    const queue = snapshot.queue || [];
    text(q("#queue-summary"), `${queue.length} ${queue.length === 1 ? "entry" : "entries"}`);
    if (!queue.length) { list.appendChild(make("p", "empty-copy", "Queue is empty.")); return; }
    queue.forEach((item, index) => {
      const row = make("div", `queue-row${index === 0 ? " is-next" : ""}`);
      const primary = make("div", "queue-primary"); primary.appendChild(make("strong", "", competitorName(snapshot, item.competitorId))); primary.appendChild(make("span", "", `${pretty(item.category)}${item.replaceExistingOfficial ? " · replaces official" : ""}`)); row.appendChild(primary);
      const actions = make("div", "row-actions");
      const arm = button(index === 0 ? "Arm" : "Arm", "tiny-button tiny-primary"); arm.disabled = !state.connected || !!snapshot.currentRun; arm.addEventListener("click", () => armQueue(item.id, false)); actions.appendChild(arm);
      const manual = button("Manual", "tiny-button"); manual.title = "Arm with manual scoring for unavailable stations"; manual.disabled = !state.connected || !!snapshot.currentRun; manual.addEventListener("click", () => armQueue(item.id, true)); actions.appendChild(manual);
      const up = button("↑"); up.disabled = !state.connected || index === 0; up.addEventListener("click", () => reorderQueue(snapshot, index, index - 1)); actions.appendChild(up);
      const down = button("↓"); down.disabled = !state.connected || index === queue.length - 1; down.addEventListener("click", () => reorderQueue(snapshot, index, index + 1)); actions.appendChild(down);
      const remove = button("×", "tiny-button tiny-danger"); remove.disabled = !state.connected; remove.title = "Remove from queue"; remove.addEventListener("click", () => removeQueue(item.id)); actions.appendChild(remove);
      row.appendChild(actions); list.appendChild(row);
    });
  }

  async function armQueue(queueId, manualOfflineOverride) {
    if (manualOfflineOverride && !window.confirm("Arm this run with manual scoring for unavailable stations?")) return;
    await perform("Arm run", API.queueArm(queueId), { manualOfflineOverride }, manualOfflineOverride ? "Run armed with manual scoring." : "Run armed.");
  }

  async function removeQueue(queueId) {
    if (!window.confirm("Remove this entry from the queue?")) return;
    try { await request(API.queueRemove(queueId), { method: "DELETE" }); showAlert("Queue entry removed.", "success"); await pollOperator(); }
    catch (error) { showAlert(`Queue entry could not be removed: ${error.message}`); }
  }

  async function reorderQueue(snapshot, from, to) {
    const ids = (snapshot.queue || []).map((item) => item.id);
    [ids[from], ids[to]] = [ids[to], ids[from]];
    await perform("Reorder queue", API.queueReorder, { queueIds: ids }, "Queue order updated.");
  }

  function renderHistory(snapshot) {
    const list = q("#history-list");
    clear(list);
    const history = snapshot.history || [];
    text(q("#history-count"), `${history.length} ${history.length === 1 ? "run" : "runs"}`);
    if (!history.length) { list.appendChild(make("p", "empty-copy", "No saved runs.")); return; }
    history.forEach((run) => {
      const row = make("div", `history-row${state.editors.history && state.editors.history.runId === run.id ? " is-selected" : ""}`);
      row.addEventListener("click", () => openEditor("history", run));
      const primary = make("div", "history-primary"); primary.appendChild(make("strong", "", competitorName(snapshot, run.competitorId))); primary.appendChild(make("span", "", `${pretty(run.category)} · ${formatDate(run.createdAt)} · ${runTotal(run)} points`)); row.appendChild(primary);
      const status = make("span", `history-status ${run.status}`, pretty(run.status)); row.appendChild(status);
      const restart = button("Restart", "tiny-button"); restart.disabled = !state.connected; restart.addEventListener("click", (event) => { event.stopPropagation(); restartRun(run.id); }); row.appendChild(restart);
      list.appendChild(row);
    });
  }

  async function restartRun(runId) {
    const reason = window.prompt("Why is this run being restarted?");
    if (reason == null || !reason.trim()) return;
    await perform("Restart run", API.restart(runId), { reason: reason.trim() }, "Restart added to the front of the queue.");
  }

  function renderDevices(snapshot) {
    const list = q("#device-list");
    const select = q("#sim-device-select");
    clear(list);
    const devices = snapshot.devices || [];
    const currentSelected = select && select.value;
    setSelectOptions(select, devices.map((device) => ({ value: device.deviceId, label: `${device.eventId} · ${device.deviceId}` })), currentSelected, true, "Select a device…");
    if (!state.simDeviceId && devices[0]) state.simDeviceId = devices[0].deviceId;
    if (state.simDeviceId) updateOptionValue(select, state.simDeviceId);
    text(q("#device-summary"), `${devices.length} ${devices.length === 1 ? "device" : "devices"}`);
    if (!devices.length) { list.appendChild(make("p", "empty-copy", "Device state will appear after the server connects.")); return; }
    devices.forEach((device) => {
      const row = make("div", "device-row");
      const primary = make("div", "device-primary"); primary.appendChild(make("strong", "", device.deviceId)); primary.appendChild(make("span", "", `${device.eventId} · LED ${pretty(device.led)}`)); row.appendChild(primary);
      const right = make("div"); right.appendChild(make("span", `device-health ${String(device.availability || "").toLowerCase()}`, pretty(device.availability)));
      const actions = make("div", "device-actions");
      const online = button("Online", "tiny-button tiny-primary"); online.disabled = !state.connected; online.addEventListener("click", () => setAvailability(device.deviceId, "online"));
      const offline = button("Offline", "tiny-button tiny-danger"); offline.disabled = !state.connected; offline.addEventListener("click", () => setAvailability(device.deviceId, "offline"));
      actions.appendChild(online); actions.appendChild(offline); right.appendChild(actions); row.appendChild(right); list.appendChild(row);
    });
  }

  async function setAvailability(deviceId, availability) {
    await perform("Device status", API.availability(deviceId), { availability }, `Device marked ${pretty(availability).toLowerCase()}.`);
  }

  function renderRaw(snapshot) {
    const messages = snapshot.messages || [];
    const edits = snapshot.edits || [];
    text(q("#raw-messages"), JSON.stringify(messages, null, 2));
    text(q("#raw-edits"), JSON.stringify(edits, null, 2));
    text(q("#raw-snapshot"), JSON.stringify(snapshot, null, 2));
    text(q("#raw-message-count"), messages.length);
    text(q("#raw-edit-count"), edits.length);
  }

  function getLatestEdit(snapshot, runId) {
    if (!runId) return null;
    return (snapshot.edits || []).find((edit) => edit.runId === runId) || null;
  }

  function openEditor(kind, run) {
    if (!run) return;
    state.editors[kind] = { kind, runId: run.id, openRevision: run.revision, draft: clone(run), dirty: false, stale: false, eventFields: [] };
    if (kind === "active") {
      q("#active-editor").classList.remove("is-hidden");
      switchPanel("current-panel");
    } else {
      q("#history-editor-empty").classList.add("is-hidden");
      q("#history-editor-body").classList.remove("is-hidden");
      switchPanel("history-panel");
    }
    renderEditor(kind);
  }

  function closeEditor(kind) {
    state.editors[kind] = null;
    q(kind === "active" ? "#active-editor" : "#history-editor-body").classList.add("is-hidden");
    if (kind === "history") q("#history-editor-empty").classList.remove("is-hidden");
  }

  function editorRoot(kind) { return kind === "active" ? "#active-editor" : "#history-editor-body"; }
  function editorId(kind, suffix) { return `#${kind === "active" ? "active-editor" : "history-editor"}-${suffix}`; }

  function renderEditor(kind) {
    const editor = state.editors[kind];
    if (!editor) return;
    const run = editor.draft;
    const snapshot = state.snapshot;
    const root = q(editorRoot(kind));
    const status = q(editorId(kind, "status"));
    const competitors = (snapshot && snapshot.competitors || []).map((item) => ({ value: item.id, label: item.name }));
    setSelectOptions(q(editorId(kind, "competitor")), competitors, run.competitorId);
    updateOptionValue(q(editorId(kind, "category")), run.category);
    updateOptionValue(status, run.status);
    q(editorId(kind, "time-limit")).value = get(run.edition, "durationLimitSeconds", snapshot && snapshot.durationLimitSeconds || 300);
    q(editorId(kind, "active-elapsed")).value = Number(run.activeElapsedMs || 0);
    q(editorId(kind, "bonus")).value = run.bonusPointsOverride == null ? "" : run.bonusPointsOverride;
    q(editorId(kind, "bonus-result")).value = run.bonusResultJson || "";
    q(editorId(kind, "notes")).value = run.notes || "";
    if (kind === "history") text(q("#history-editor-revision"), `Version ${run.revision}`); else text(q("#active-editor-state"), `Run version ${run.revision} captured when opened.`);
    if (kind === "history") text(q("#history-editor-state"), `Run version ${run.revision} captured when opened.`);
    const eventBody = q(editorId(kind, "events"));
    clear(eventBody); editor.eventFields = [];
    (run.events || []).forEach((event) => {
      const row = make("tr", "editor-event-row");
      row.appendChild(make("td", "", event.name));
      const statusSelect = make("select");
      ["pending", "active", "completed"].forEach((value) => { const option = make("option", "", pretty(value)); option.value = value; option.selected = event.status === value; statusSelect.appendChild(option); });
      const statusCell = make("td"); statusCell.appendChild(statusSelect); row.appendChild(statusCell);
      const start = make("input"); start.type = "number"; start.min = "0"; start.value = event.startElapsedMs == null ? "" : event.startElapsedMs; const startCell = make("td"); startCell.appendChild(start); row.appendChild(startCell);
      const finish = make("input"); finish.type = "number"; finish.min = "0"; finish.value = event.finishElapsedMs == null ? "" : event.finishElapsedMs; const finishCell = make("td"); finishCell.appendChild(finish); row.appendChild(finishCell);
      const duration = make("input"); duration.type = "number"; duration.min = "0"; duration.value = runDuration(event) == null ? "" : runDuration(event); const durationCell = make("td"); durationCell.appendChild(duration); row.appendChild(durationCell);
      const score = make("input"); score.type = "number"; score.min = "0"; score.value = event.scoreOverride == null ? "" : event.scoreOverride; const scoreCell = make("td"); scoreCell.appendChild(score); row.appendChild(scoreCell);
      const notes = make("input"); notes.type = "text"; notes.maxLength = 1000; notes.value = event.notes || ""; const notesCell = make("td"); notesCell.appendChild(notes); row.appendChild(notesCell);
      eventBody.appendChild(row);
      const field = { eventId: event.eventId, original: clone(event), status: statusSelect, start, finish, duration, score, notes };
      editor.eventFields.push(field);
      [statusSelect, start, finish, duration, score, notes].forEach((element) => element.addEventListener("input", () => { editor.dirty = true; }));
    });
    qa("input, select, textarea", root).forEach((element) => element.addEventListener("input", () => { editor.dirty = true; }, { once: true }));
    updateEditorButtons(kind);
  }

  function updateEditorsAfterPoll(snapshot) {
    ["active", "history"].forEach((kind) => {
      const editor = state.editors[kind];
      if (!editor) return;
      const latest = runForEditor(kind);
      if (!latest) { editor.stale = true; updateEditorButtons(kind); return; }
      if (latest.revision !== editor.openRevision) editor.stale = true;
      const banner = q(editorId(kind, "conflict"));
      if (banner) banner.classList.toggle("is-hidden", !editor.stale);
      updateEditorButtons(kind);
    });
  }

  function updateEditorButtons(kind) {
    const editor = state.editors[kind];
    if (!editor) return;
    const save = q(kind === "active" ? "#save-active-editor" : "#save-history-editor");
    const reload = q(kind === "active" ? "#reload-active-editor" : "#reload-history-editor");
    if (save) save.disabled = !state.connected || editor.stale;
    if (reload) reload.disabled = !state.connected;
    const latest = getLatestEdit(state.snapshot, editor.runId);
    const undo = kind === "history" ? q("#undo-history-button") : null;
    if (undo) undo.disabled = !state.connected || editor.stale || !latest;
  }

  function readEditorRequest(kind) {
    const editor = state.editors[kind];
    const original = editor.draft;
    const bonusValue = numberValue(q(editorId(kind, "bonus")).value);
    const originalBonus = original.bonusPointsOverride == null ? null : Number(original.bonusPointsOverride);
    const originalTimeLimit = Number(get(original.edition, "durationLimitSeconds", state.snapshot && state.snapshot.durationLimitSeconds || 300));
    const currentTimeLimit = numberValue(q(editorId(kind, "time-limit")).value);
    const currentElapsed = numberValue(q(editorId(kind, "active-elapsed")).value, 0);
    const originalElapsed = Number(original.activeElapsedMs || 0);
    const request = {
      expectedRevision: editor.openRevision,
      reason: q(editorId(kind, "reason")).value.trim(),
      events: []
    };
    const competitorId = q(editorId(kind, "competitor")).value;
    const category = q(editorId(kind, "category")).value;
    const status = q(editorId(kind, "status")).value;
    const bonusResult = q(editorId(kind, "bonus-result")).value;
    const notes = q(editorId(kind, "notes")).value;
    if (competitorId !== original.competitorId) request.competitorId = competitorId;
    if (category !== original.category) request.category = category;
    if (status !== original.status) request.status = status;
    if (currentTimeLimit !== originalTimeLimit && currentTimeLimit != null) request.durationLimitSeconds = currentTimeLimit;
    if (currentElapsed !== originalElapsed && currentElapsed != null) request.activeElapsedMs = currentElapsed;
    if (bonusResult !== (original.bonusResultJson || "")) request.bonusResultJson = bonusResult;
    if (notes !== (original.notes || "")) request.notes = notes;
    if (bonusValue !== originalBonus) {
      if (bonusValue == null && originalBonus != null) request.clearBonusPointsOverride = true;
      else if (bonusValue != null) request.bonusPointsOverride = bonusValue;
    }
    editor.eventFields.forEach((field) => {
      const event = { eventId: field.eventId };
      const originalStart = field.original.startElapsedMs == null ? null : Number(field.original.startElapsedMs);
      const originalFinish = field.original.finishElapsedMs == null ? null : Number(field.original.finishElapsedMs);
      const originalDuration = runDuration(field.original);
      const start = numberValue(field.start.value);
      const finish = numberValue(field.finish.value);
      const duration = numberValue(field.duration.value);
      if (field.status.value !== field.original.status) event.status = field.status.value;
      if (start !== originalStart && start != null) event.startElapsedMs = start;
      if (finish !== originalFinish && finish != null) event.finishElapsedMs = finish;
      if (duration !== originalDuration && duration != null) event.durationMs = duration;
      const score = numberValue(field.score.value);
      const originalOverride = field.original.scoreOverride == null ? null : Number(field.original.scoreOverride);
      if (score !== originalOverride) {
        if (score == null && originalOverride != null) event.clearScoreOverride = true;
        else if (score != null) event.scoreOverride = score;
      }
      if (field.notes.value !== (field.original.notes || "")) event.notes = field.notes.value;
      if (Object.keys(event).length > 1) request.events.push(event);
    });
    return request;
  }

  async function saveEditor(kind) {
    const editor = state.editors[kind];
    if (!editor || editor.stale) return;
    const payload = readEditorRequest(kind);
    if (!payload.reason) { showAlert("Add a reason before saving the correction."); return; }
    try {
      await request(kind === "active" ? API.editCurrent : API.edit(editor.runId), { method: "PUT", body: payload });
      showAlert("Correction saved.", "success");
      await pollOperator();
      const updated = runForEditor(kind);
      if (updated) openEditor(kind, updated);
    } catch (error) {
      showAlert(`Correction could not be saved: ${error.message}`);
      await pollOperator();
    }
  }

  async function undoEditor(kind) {
    const editor = state.editors[kind];
    if (!editor || editor.stale) return;
    const latest = getLatestEdit(state.snapshot, editor.runId);
    if (!latest) { showAlert("There is no correction to undo for this run."); return; }
    const reason = window.prompt("Why is this correction being undone?");
    if (reason == null || !reason.trim()) return;
    try {
      await request(API.undo(editor.runId), { method: "POST", body: { editId: latest.id, expectedRevision: editor.openRevision, reason: reason.trim() } });
      showAlert("Correction undone.", "success");
      await pollOperator();
      const updated = runForEditor(kind);
      if (updated) openEditor(kind, updated);
    } catch (error) {
      showAlert(`Correction could not be undone: ${error.message}`);
      await pollOperator();
    }
  }

  function inputEnvelope(type, deviceId, payload = {}) {
    const run = state.snapshot && state.snapshot.currentRun;
    if (!run) return null;
    return {
      messageId: uuid(),
      sessionId: run.id,
      runId: run.id,
      deviceId,
      type,
      elapsedMilliseconds: Number(run.activeElapsedMs || 0),
      payload
    };
  }

  async function sendSimulatorInput(type, deviceId, payload = {}, successMessage = "Virtual signal sent.") {
    if (!state.snapshot || !state.snapshot.simulationMode) {
      showAlert("Simulation controls are disabled in hardware mode.");
      return null;
    }
    const envelope = inputEnvelope(type, deviceId, payload);
    if (!envelope) { showAlert("Arm a run before sending a virtual signal."); return null; }
    return perform("Virtual signal", API.simulatorInput, envelope, successMessage, (result) => {
      if (!result || !result.disposition) return null;
      const reason = result.reason || pretty(result.disposition);
      const responseRun = result.run;
      const responseEvent = responseRun && responseRun.events && responseRun.events.find((event) => event.deviceId === deviceId);
      if (type === "keypad-response" && (!responseEvent || responseEvent.status === "active")) {
        return { kind: "error", message: `Wrong keypad answer. ${reason} The keypad event is still active.` };
      }
      if (result.disposition !== "accepted") {
        return { kind: "error", message: `Virtual signal ${pretty(result.disposition).toLowerCase()}: ${reason}` };
      }
      return null;
    });
  }

  function getEventByType(type) {
    const run = state.snapshot && state.snapshot.currentRun;
    return run && (run.events || []).find((event) => event.type === type) || null;
  }

  function getEditionEvent(run, eventId) {
    return run && run.edition && (run.edition.events || []).find((event) => event.eventId === eventId) || null;
  }

  function renderSimulator(snapshot) {
    const simulation = state.connected && !!snapshot && !!snapshot.simulationMode;
    const run = snapshot.currentRun;
    const hasRun = !!run;
    const activeInput = simulation && currentCanReceiveInput();
    ["#sim-master", "#sim-pause", "#sim-keypad-submit", "#sim-emerald-start", "#sim-arcade-end", "#sim-bonus", "#sim-advance", "#sim-advance-30", "#sim-advance-custom", "#sim-timeout"].forEach((selector) => {
      const element = q(selector);
      if (!element) return;
      if (selector === "#sim-master") element.disabled = !simulation || !hasRun || run.status !== "armed";
      else if (selector === "#sim-pause") element.disabled = !simulation || !hasRun || !["active", "paused"].includes(run.status);
      else if (selector.startsWith("#sim-advance") || selector === "#sim-timeout") element.disabled = !simulation || !hasRun || !["active", "paused"].includes(run.status);
      else if (selector === "#sim-keypad-submit") element.disabled = !activeInput || !getEventByType("keypad");
      else if (selector === "#sim-emerald-start" || selector === "#sim-arcade-end") element.disabled = !activeInput || !getEventByType("magneticArcade");
      else element.disabled = !activeInput;
    });
    const onlineToggle = q("#sim-device-online");
    if (onlineToggle) onlineToggle.disabled = !simulation || !q("#sim-device-select").value;
    const buttons = q("#virtual-event-buttons");
    clear(buttons);
    if (!run || !run.events || !run.events.length) {
      buttons.appendChild(make("p", "empty-copy", "Arm a run to load virtual event buttons."));
    } else {
      run.events.forEach((event) => {
        const virtual = make("button", "virtual-button"); virtual.type = "button"; virtual.disabled = !activeInput;
        const eventName = make("strong", "", event.name); virtual.appendChild(eventName);
        const actionLabel = event.type === "magneticArcade" ? "Emeralds" : event.type === "keypad" ? "Prompt" : pretty(event.status);
        virtual.appendChild(make("small", "", actionLabel));
        virtual.addEventListener("click", () => {
          const signal = event.type === "magneticArcade" ? "arcade-start" : "event-press";
          sendSimulatorInput(signal, event.deviceId, {}, event.type === "magneticArcade" ? "Emerald-placement signal sent." : "Virtual button press sent.");
        });
        buttons.appendChild(virtual);
      });
    }
    const keypad = getEventByType("keypad");
    text(q("#sim-keypad-prompt"), keypad ? (keypad.prompt || "Prompt configured by the edition.") : "No keypad event in this run.");
    const answer = keypad && getEditionEvent(run, keypad.eventId) && getEditionEvent(run, keypad.eventId).answer;
    text(q("#sim-demo-answer strong"), answer ? answer : keypad ? "Configured by edition" : "—");
    const arcade = getEventByType("magneticArcade");
    if (q("#sim-emerald-start")) q("#sim-emerald-start").dataset.deviceId = arcade ? arcade.deviceId : "";
    if (q("#sim-arcade-end")) q("#sim-arcade-end").dataset.deviceId = arcade ? arcade.deviceId : "";
  }

  async function advanceSimulation(seconds) {
    if (!state.snapshot || !state.snapshot.simulationMode) { showAlert("Clock controls are disabled in hardware mode."); return; }
    const milliseconds = Math.max(0, Math.round(Number(seconds) * 1000));
    if (!Number.isFinite(milliseconds) || milliseconds <= 0) { showAlert("Enter a positive number of seconds."); return; }
    await perform("Advance clock", API.simulatorAdvance, { milliseconds }, `Clock advanced ${Math.round(milliseconds / 1000)} seconds.`);
  }

  async function runToTimeout() {
    const snapshot = state.snapshot;
    const run = snapshot && snapshot.currentRun;
    if (!snapshot || !snapshot.simulationMode || !run) { showAlert("Arm a simulation run before advancing it to timeout."); return; }
    const limit = Number(get(run.edition, "durationLimitSeconds", snapshot.durationLimitSeconds || 300));
    const remaining = Math.max(0, limit * 1000 - Number(run.activeElapsedMs || 0));
    if (!remaining) { showAlert("The run is already at its time limit."); return; }
    await perform("Run to timeout", API.simulatorAdvance, { milliseconds: remaining }, "Clock advanced to the run time limit.");
  }

  function bindOperatorActions() {
    bindTabNavigation();
    q("#refresh-button").addEventListener("click", () => pollOperator());
    q("#start-run-button").addEventListener("click", () => perform("Start run", API.start, {}, "Master started the run."));
    q("#finish-run-button").addEventListener("click", () => perform("Finish run", API.finish, {}, "Run finished."));
    q("#pause-run-button").addEventListener("click", () => {
      const run = state.snapshot && state.snapshot.currentRun;
      perform(run && run.status === "paused" ? "Resume run" : "Pause run", run && run.status === "paused" ? API.resume : API.pause, {}, run && run.status === "paused" ? "Run resumed." : "Run paused.");
    });
    q("#abort-run-button").addEventListener("click", async () => {
      const reason = window.prompt("Why is this run being aborted?");
      if (reason == null || !reason.trim()) return;
      await perform("Abort run", API.abort, { reason: reason.trim() }, "Run aborted.");
    });
    q("#close-active-editor").addEventListener("click", () => closeEditor("active"));
    q("#reload-active-editor").addEventListener("click", () => { const run = state.snapshot && state.snapshot.currentRun; if (run) openEditor("active", run); });
    q("#save-active-editor").addEventListener("click", () => saveEditor("active"));
    q("#reload-history-editor").addEventListener("click", () => { const run = runForEditor("history"); if (run) openEditor("history", run); });
    q("#save-history-editor").addEventListener("click", () => saveEditor("history"));
    q("#undo-history-button").addEventListener("click", () => undoEditor("history"));
    q("#competitor-form").addEventListener("submit", async (event) => {
      event.preventDefault();
      const input = q("#competitor-name");
      const name = input.value.trim();
      if (!name) return;
      const result = await perform("Add competitor", API.competitor, { name }, "Competitor added.");
      if (result) { input.value = ""; }
    });
    q("#queue-form").addEventListener("submit", async (event) => {
      event.preventDefault();
      const competitorId = q("#queue-competitor").value;
      if (!competitorId) { showAlert("Choose a competitor before adding to the queue."); return; }
      await perform("Add to queue", API.queue, { competitorId, category: q("#queue-category").value, replaceExistingOfficial: q("#replace-official").checked }, "Queue entry added.");
    });
    q("#preflight-button").addEventListener("click", async () => {
      try { await request(API.preflight, { method: "POST" }); showAlert("Preflight complete.", "success"); await pollOperator(); }
      catch (error) { showAlert(`Preflight could not be completed: ${error.message}`); }
    });
    q("#sim-device-select").addEventListener("change", (event) => { state.simDeviceId = event.target.value; const device = (state.snapshot && state.snapshot.devices || []).find((item) => item.deviceId === state.simDeviceId); if (q("#sim-device-online") && device) q("#sim-device-online").checked = device.availability === "online"; });
    q("#sim-device-online").addEventListener("change", (event) => { if (state.simDeviceId) setAvailability(state.simDeviceId, event.target.checked ? "online" : "offline"); });
    q("#sim-master").addEventListener("click", () => perform("Virtual master start", API.start, {}, "Virtual master started the run."));
    q("#sim-pause").addEventListener("click", () => { const run = state.snapshot && state.snapshot.currentRun; perform(run && run.status === "paused" ? "Resume run" : "Pause run", run && run.status === "paused" ? API.resume : API.pause, {}, run && run.status === "paused" ? "Run resumed." : "Run paused."); });
    q("#sim-keypad-submit").addEventListener("click", () => { const input = q("#sim-keypad-answer"); const keypad = getEventByType("keypad"); if (!input.value.trim() || !keypad) return; sendSimulatorInput("keypad-response", keypad.deviceId, { answer: input.value.trim() }, "Keypad response sent."); input.value = ""; });
    q("#sim-emerald-start").addEventListener("click", () => sendSimulatorInput("arcade-start", q("#sim-emerald-start").dataset.deviceId, {}, "Emerald-placement signal sent."));
    q("#sim-arcade-end").addEventListener("click", () => sendSimulatorInput("arcade-finish", q("#sim-arcade-end").dataset.deviceId, {}, "Arcade completion signal sent."));
    q("#sim-bonus").addEventListener("click", () => sendSimulatorInput("bonus-signal", "master", {}, "Bonus signal sent."));
    q("#sim-advance").addEventListener("click", () => advanceSimulation(5));
    q("#sim-advance-30").addEventListener("click", () => advanceSimulation(30));
    q("#sim-advance-custom").addEventListener("click", () => advanceSimulation(q("#sim-advance-seconds").value));
    q("#sim-timeout").addEventListener("click", () => runToTimeout());
    q("#backup-button").addEventListener("click", async () => {
      try {
        const result = await request(API.backup, { method: "POST" });
        showAlert(result && result.path ? `Backup created: ${result.path}` : "Backup created.", "success");
      } catch (error) { showAlert(`Backup could not be created: ${error.message}`); }
    });
    q("#copy-raw-button").addEventListener("click", async () => { try { await navigator.clipboard.writeText(q("#raw-snapshot").textContent); showAlert("Snapshot copied.", "success"); } catch { showAlert("Snapshot could not be copied."); } });
  }

  function initOperator() {
    bindOperatorActions();
    setConnection(false, null);
    switchPanel("current-panel");
    pollOperator();
    window.setInterval(() => pollOperator(), 1000);
  }

  function pollScoreboard() {
    request(API.scoreboard).then((snapshot) => {
      state.scoreboard = snapshot;
      state.lastGoodAt = new Date();
      setConnection(true, state.lastGoodAt);
      try { renderScoreboard(snapshot); } catch (error) { reportRenderFailure(error, "scoreboard"); }
    }).catch((error) => {
      setConnection(false, state.lastGoodAt);
    });
  }

  function renderScoreboard(snapshot) {
    setMode(!!snapshot.simulationMode, snapshot.editionName, "#scoreboard-mode");
    text(q("#scoreboard-edition"), snapshot.editionName || "Scoreboard");
    text(q("#scoreboard-footer-edition"), snapshot.editionName || "Local scoreboard");
    const run = snapshot.currentRun;
    const total = run ? Number(run.totalEvents || 0) : 13;
    const completed = run ? Number(run.completedEvents || 0) : 0;
    const remaining = run ? Number(run.remainingMilliseconds || 0) : Number(snapshot.durationLimitSeconds || 300) * 1000;
    text(q("#scoreboard-competitor"), run ? run.competitorName : "No active run");
    const category = q("#scoreboard-category"); text(category, run ? pretty(run.category) : "—"); category.className = `category-pill${run ? ` ${categoryClass(run.category)}` : ""}`;
    const phase = q("#scoreboard-phase"); text(phase, run ? pretty(run.phase) : "Waiting"); phase.className = `phase-chip${run && run.phase === "bonus" ? " phase-bonus" : " phase-normal"}`;
    text(q("#scoreboard-countdown"), formatMs(remaining));
    text(q("#scoreboard-run-status"), run ? pretty(run.status) : "Awaiting master start");
    text(q("#scoreboard-points"), run ? run.awardedPoints || 0 : 0);
    text(q("#scoreboard-event-progress"), `${completed} / ${total} complete`);
    const fill = q("#scoreboard-progress-fill"); if (fill) fill.style.width = `${total ? Math.min(100, completed / total * 100) : 0}%`;
    text(q("#scoreboard-on-deck"), snapshot.onDeckName || "—");
    renderScoreboardEvents(run);
    renderLeaderboard(snapshot.leaderboard || []);
  }

  function renderScoreboardEvents(run) {
    const container = q("#scoreboard-events"); clear(container);
    if (!run || !run.events || !run.events.length) { container.appendChild(make("div", "tv-empty", "Events will appear when a run is armed.")); return; }
    run.events.forEach((event) => {
      const card = make("article", `tv-event ${event.status || "pending"}`);
      const name = make("span", "tv-event-name", event.name); card.appendChild(name);
      if (event.prompt) card.appendChild(make("span", "event-device", event.prompt));
      const meta = make("div", "tv-event-meta"); meta.appendChild(make("span", "tv-event-status", pretty(event.status))); meta.appendChild(make("strong", "", event.awardedPoints || 0)); card.appendChild(meta);
      container.appendChild(card);
    });
  }

  function renderLeaderboard(rows) {
    const container = q("#scoreboard-leaderboard"); clear(container);
    if (!rows.length) { container.appendChild(make("div", "tv-empty", "No official results yet.")); return; }
    rows.forEach((row) => {
      const line = make("div", "tv-rank-row"); line.appendChild(make("span", "tv-rank", row.rank));
      const name = make("div"); name.appendChild(make("div", "tv-rank-name", row.competitorName)); name.appendChild(make("div", "tv-rank-meta", `${pretty(row.category)} · ${pretty(row.status)}`)); line.appendChild(name); line.appendChild(make("span", "tv-points", row.points)); container.appendChild(line);
    });
  }

  function initScoreboard() {
    setConnection(false, null);
    pollScoreboard();
    window.setInterval(pollScoreboard, 1000);
  }

  if (operatorPage) initOperator();
  if (scoreboardPage) initScoreboard();
})();
