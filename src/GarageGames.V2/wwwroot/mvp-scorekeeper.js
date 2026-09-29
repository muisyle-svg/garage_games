(() => {
  "use strict";

  const state = {
    snapshot: null,
    connected: false,
    displayError: "",
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
    selectedLeaderboardRunId: "",
    leaderboardSelectionInitialized: false,
    selectPromotedAfterRecord: false,
    competitorSignature: "",
    showArchivedCompetitors: false,
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
    alertTimer: null,
    setupDraft: null,
    setupDirty: false,
    setupLoading: false,
    setupSaving: false,
    setupScanLoading: false,
    setupScan: null,
    setupScanFresh: false,
    setupScanAt: 0,
    setupScanError: "",
    setupLoadError: "",
    setupSelectedEventId: null,
    durationRunId: null,
    durationResetRunId: null,
    snapshotRequestId: 0,
    appliedSnapshotRequestId: 0,
    armScanPending: false,
    armScanAfterSnapshotRequestId: null,
    armScanRequestVersion: 0,
    lastIdentifyTestAt: null,
    exhibitionPreferenceDraft: null,
    latestEventPressMessageId: null,
    buttonHighlightDeviceId: null,
    buttonHighlightUntil: 0,
    tileUndoArmedEventId: null,
    tileUndoTimer: null,
    deletedRunsSignature: "",
    currentSaveError: ""
  };

  const clearDatabasePhrase = "CLEAR ALL DATA";
  const MAXIMUM_MANUAL_POINTS = 1000000;
  const $ = (id) => document.getElementById(id);
  const scorekeeperTime = window.GarageGamesScorekeeperTime;
  const loadedMasterActions = window.GarageGamesMasterActions || {};
  const isRunDurationLocked = typeof loadedMasterActions.isRunDurationLocked === "function"
    ? loadedMasterActions.isRunDurationLocked
    : (run) => Boolean(run && ["armed", "countdown", "active", "paused", "finished"].includes(run.status));
  const masterActions = {
    ...loadedMasterActions,
    isRunDurationLocked,
    shouldResetRunDuration: typeof loadedMasterActions.shouldResetRunDuration === "function"
      ? loadedMasterActions.shouldResetRunDuration
      : (run, lastResetRunId) => Boolean(run?.id && !isRunDurationLocked(run) && run.id !== lastResetRunId)
  };
  const runActions = window.GarageGamesRunActions;
  const setupTools = window.GarageGamesScorekeeperSetup;
  const scorecardOrder = window.GarageGamesScorecardOrder;
  const leaderboardTools = window.GarageGamesLeaderboards;
  const ui = {
    edition: $("edition-name"),
    connection: $("connection-status"),
    lastUpdated: $("last-updated"),
    refresh: $("refresh-button"),
    alert: $("alert-region"),
    tabAlert: $("tab-alert-region"),
    tabs: [$("tab-scorekeeping"), $("tab-on-deck"), $("tab-history"), $("tab-leaderboards"), $("tab-setup")],
    tabPanels: [$("panel-scorekeeping"), $("panel-on-deck"), $("panel-history"), $("panel-leaderboards"), $("panel-setup")],
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
    competitorDuplicateWarning: $("competitor-duplicate-warning"),
    showArchivedCompetitors: $("show-archived-competitors"),
    competitorRosterList: $("competitor-roster-list"),
    competitorImportForm: $("competitor-import-form"),
    competitorImportText: $("competitor-import-text"),
    competitorImportFile: $("competitor-import-file"),
    competitorImportResult: $("competitor-import-result"),
    start: $("start-run-button"),
    prime: $("prime-next-button"),
    armPhysical: $("arm-physical-button"),
    durationInput: $("run-duration-input"),
    durationHelp: $("run-duration-help"),
    masterPort: $("master-port-select"),
    masterConnect: $("master-connect-button"),
    masterDisconnect: $("master-disconnect-button"),
    masterRefresh: $("master-refresh-button"),
    masterConnectionLabel: $("master-connection-label"),
    masterModeLabel: $("master-mode-label"),
    masterLastMessage: $("master-last-message"),
    physicalStartHelp: $("physical-start-help"),
    pause: $("pause-run-button"),
    undoPress: $("undo-last-event-press"),
    undoDetail: $("undo-last-event-detail"),
    hardwarePanel: $("hardware-panel"),
    hardwarePanelSummary: $("hardware-panel-summary"),
    scorecardPanel: $("scorecard-panel"),
    scorecardSummaryTotal: $("scorecard-summary-total"),
    unsavedBar: $("unsaved-edits-bar"),
    unsavedMessage: $("unsaved-edits-message"),
    unsavedSave: $("unsaved-edits-save"),
    unsavedDiscard: $("unsaved-edits-discard"),
    currentDiscard: $("discard-current-edits"),
    finish: $("finish-run-button"),
    record: $("record-run-button"),
    recordActions: $("record-actions"),
    reopen: $("reopen-run-button"),
    discard: $("discard-run-button"),
    countdown: $("run-countdown"),
    progress: $("event-progress"),
    runTotal: $("run-total"),
    banner: $("run-state-banner"),
    physicalReadinessSummary: $("physical-readiness-summary"),
    physicalReadinessList: $("physical-readiness-list"),
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
    historyDelete: $("delete-history-run"),
    historySearch: $("history-search"),
    historyCategoryFilter: $("history-category-filter"),
    historyStatusFilter: $("history-status-filter"),
    historyClearFilters: $("history-clear-filters"),
    deletedRunsPanel: $("deleted-runs-panel"),
    deletedRunsCount: $("deleted-runs-count"),
    deletedRunsList: $("deleted-runs-list"),
    leaderboardPlayerSelect: $("leaderboard-player-select"),
    overallLeaderboardCount: $("overall-leaderboard-count"),
    overallLeaderboardBody: $("overall-leaderboard-body"),
    playerLeaderboardCaption: $("player-leaderboard-caption"),
    playerLeaderboardBody: $("player-leaderboard-body"),
    playerLeaderboardTotal: $("player-leaderboard-total"),
    eventLeaderboardsGrid: $("event-leaderboards-grid"),
    showExhibitionsOnLeaderboard: $("show-exhibitions-on-leaderboard"),
    clearDatabaseConfirmation: $("clear-database-confirmation"),
    clearDatabaseButton: $("clear-database-button"),
    clearDatabaseResult: $("clear-database-result"),
    setupLoadState: $("setup-load-state"),
    setupRetryLoad: $("setup-retry-load"),
    setupContent: $("setup-content"),
    setupEditionName: $("setup-edition-name"),
    setupBonus: $("setup-bonus"),
    setupScanAll: $("setup-scan-all"),
    setupSave: $("setup-save"),
    setupSaveBottom: $("setup-save-bottom"),
    setupScanSummary: $("setup-scan-summary"),
    setupSelectedEventLabel: $("setup-selected-event-label"),
    setupDiscoveredDevices: $("setup-discovered-devices"),
    setupEventCount: $("setup-event-count"),
    setupAddEvent: $("setup-add-event"),
    setupValidation: $("setup-validation"),
    setupEventList: $("setup-event-list"),
    setupPreviewSummary: $("setup-preview-summary"),
    setupPreviewList: $("setup-preview-list"),
    setupSaveState: $("setup-save-state")
  };

  function isLiveLock(run) {
    return masterActions.isRunDurationLocked(run);
  }

  function make(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined && text !== null) node.textContent = String(text);
    return node;
  }

  // Messages go in a fixed-size slot so they never shift the page: the scorekeeper desk's
  // header on the Scorekeeping tab, and the space beside the tabs elsewhere.
  function alertSlot() {
    return (state.activeTab ?? 0) === 0 ? ui.alert : ui.tabAlert;
  }

  function showAlert(message, kind = "error") {
    window.clearTimeout(state.alertTimer);
    ui.alert.replaceChildren();
    ui.tabAlert.replaceChildren();
    const alert = make("div", `alert${kind === "success" ? " success" : ""}`, message);
    alert.setAttribute("role", kind === "success" ? "status" : "alert");
    alert.title = String(message);
    alertSlot().appendChild(alert);
    state.alertTimer = window.setTimeout(() => {
      ui.alert.replaceChildren();
      ui.tabAlert.replaceChildren();
    }, 6500);
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

  // The app plays the countdown voice through the computer's speakers and starts the run at
  // Go itself, so neither depends on this tab being in front. This page shows the countdown
  // and reports Go as a backup.
  const countdownCoordinator = window.GarageGamesCountdown.createCountdownCoordinator({
    readState: () => request("/api/run/countdown-state", { cache: "no-store" }),
    finish: (runId) => request("/api/run/countdown-finished", { method: "POST", body: JSON.stringify({ runId }) }),
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
        ? update.playback === "finishFailed"
          ? `Go was reached, but the run could not be activated. ${update.error || ""}`.trim()
          : `Countdown audio is unavailable; the run will still start on schedule. ${update.error || ""}`.trim()
        : update.playback === "finishing"
          ? "Go · starting the run…"
          : "Countdown playing on this computer's speakers. The timer starts at Go.";
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
    if (label) label.textContent = online
      ? (state.displayError ? "Connected · display issue" : "Connected")
      : "Offline";
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
    renderHardwarePanelSummary();
  }

  async function loadMaster(silent = false) {
    if (state.masterLoading) return false;
    state.masterLoading = true;
    try {
      const master = await request("/api/master");
      if (master.lastTestAt && master.lastTestAt !== state.lastIdentifyTestAt) {
        state.lastIdentifyTestAt = master.lastTestAt;
        state.buttonHighlightDeviceId = String(master.lastTestDeviceId || "").toUpperCase();
        state.buttonHighlightUntil = Date.now() + 1400;
        window.setTimeout(() => {
          if (Date.now() >= state.buttonHighlightUntil) {
            document.querySelectorAll(".virtual-button.is-physical-press").forEach((button) => button.classList.remove("is-physical-press"));
          }
        }, 1450);
      }
      state.master = master;
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
    const requestId = ++state.snapshotRequestId;
    let snapshot;
    try {
      snapshot = await request("/api/operator");
    } catch (error) {
      setConnection(false);
      if (!silent) showAlert(`Could not load scorekeeper data: ${error.message}`);
      return false;
    }

    if (requestId < state.appliedSnapshotRequestId) return true;

    const latestEventPress = (snapshot.messages || [])
      .filter((message) => message.type === "event-press" && String(message.messageId || "").startsWith("spoke-press:"))
      .sort((left, right) => Number(right.id) - Number(left.id))[0];
    const latestEventPressId = latestEventPress ? String(latestEventPress.id) : null;
    if (state.snapshot && latestEventPress && latestEventPressId !== state.latestEventPressMessageId &&
        String(latestEventPress.disposition).toLowerCase() === "accepted") {
      state.buttonHighlightDeviceId = String(latestEventPress.deviceId || "").toUpperCase();
      state.buttonHighlightUntil = Date.now() + 1400;
      window.setTimeout(() => {
        if (Date.now() >= state.buttonHighlightUntil) {
          document.querySelectorAll(".virtual-button.is-physical-press").forEach((button) => button.classList.remove("is-physical-press"));
        }
      }, 1450);
    }
    state.latestEventPressMessageId = latestEventPressId;

    const previous = {
      snapshot: state.snapshot,
      receivedAt: state.receivedAt,
      appliedSnapshotRequestId: state.appliedSnapshotRequestId,
      timeoutRefreshRunId: state.timeoutRefreshRunId,
      armScanPending: state.armScanPending,
      armScanAfterSnapshotRequestId: state.armScanAfterSnapshotRequestId,
      virtualKey: state.virtualKey,
      lastUpdated: ui.lastUpdated.textContent
    };
    state.appliedSnapshotRequestId = requestId;
    state.snapshot = snapshot;
    state.receivedAt = Date.now();
    if (state.armScanPending && state.armScanAfterSnapshotRequestId !== null && requestId > state.armScanAfterSnapshotRequestId) {
      state.armScanPending = false;
      state.armScanAfterSnapshotRequestId = null;
      state.virtualKey = null;
    }
    state.timeoutRefreshRunId = null;
    setConnection(true);
    ui.lastUpdated.textContent = new Date(state.receivedAt).toLocaleTimeString([], { hour: "numeric", minute: "2-digit", second: "2-digit" });
    try {
      render();
      state.displayError = "";
      setConnection(true);
      return true;
    } catch (error) {
      state.displayError = error?.message || "An unexpected display error occurred.";
      if (previous.snapshot) {
        state.snapshot = previous.snapshot;
        state.receivedAt = previous.receivedAt;
        state.appliedSnapshotRequestId = previous.appliedSnapshotRequestId;
        state.timeoutRefreshRunId = previous.timeoutRefreshRunId;
        state.armScanPending = previous.armScanPending;
        state.armScanAfterSnapshotRequestId = previous.armScanAfterSnapshotRequestId;
        state.virtualKey = previous.virtualKey;
        ui.lastUpdated.textContent = previous.lastUpdated;
        try { render(); } catch (restoreError) {
          console.error("The previous scorekeeper screen could not be redrawn after a display error.", restoreError);
        }
      }
      setConnection(true);
      console.error("Scorekeeper data was received, but the screen could not be rendered.", error);
      if (!silent) showAlert(`Scorekeeper data is connected, but the screen could not refresh: ${state.displayError}`);
      return false;
    }
  }

  async function loadSetup() {
    if (state.setupLoading) return false;
    state.setupLoading = true;
    state.setupLoadError = "";
    renderSetup();
    try {
      const payload = await request("/api/setup", { cache: "no-store" });
      state.setupDraft = setupTools.normalizeSetup(payload);
      state.setupDirty = false;
      state.setupScan = null;
      state.setupScanFresh = false;
      state.setupScanAt = 0;
      state.setupScanError = "";
      state.setupSelectedEventId = state.setupDraft.events[0]?.eventId || null;
      renderSetup();
      if (state.snapshot) renderVirtualButtons(state.snapshot.currentRun);
      return true;
    } catch (error) {
      state.setupLoadError = error.message || "Setup could not be loaded.";
      renderSetup();
      return false;
    } finally {
      state.setupLoading = false;
      renderSetupControls();
    }
  }

  function setupEvent(eventId) {
    return state.setupDraft?.events.find((event) => event.eventId === eventId) || null;
  }

  function updateSetupValidation() {
    if (!state.setupDraft) return;
    try {
      setupTools.buildSetupPayload(state.setupDraft);
      ui.setupValidation.textContent = "";
    } catch (error) {
      ui.setupValidation.textContent = error.message || "Review setup before saving.";
    }
  }

  function renderSetupControls() {
    if (!state.setupDraft) return;
    const busy = state.setupLoading || state.setupSaving || state.setupScanLoading || state.busy;
    ui.setupEventCount.textContent = `${state.setupDraft.events.length} ${state.setupDraft.events.length === 1 ? "event" : "events"}`;
    const checkedAt = setupCheckedAt();
    ui.setupScanSummary.textContent = `${setupTools.scanSummary(state.setupScan, state.setupScanFresh)}${checkedAt ? ` Most recent check: ${formatLocalDateTime(checkedAt)}; this is a point-in-time check, not live monitoring.` : ""}`;
    if (state.setupScanError) ui.setupScanSummary.textContent += ` ${state.setupScanError}`;
    if (state.setupDirty && !state.setupScanFresh) ui.setupScanSummary.textContent += " Save setup before scanning the current assignments.";
    ui.setupSaveState.textContent = state.setupSaving
      ? "Saving setup…"
      : state.setupDirty ? "Unsaved setup changes." : "Setup is saved on this computer.";
    ui.setupSave.disabled = busy || !state.setupDirty;
    ui.setupSaveBottom.disabled = busy || !state.setupDirty;
    ui.setupAddEvent.disabled = busy;
    ui.setupScanAll.disabled = busy || state.setupScanLoading || state.setupDirty;
    ui.setupEditionName.disabled = busy;
    ui.setupEventList.querySelectorAll("input").forEach((input) => { input.disabled = busy; });
    ui.setupEventList.querySelectorAll("button[data-setup-action]").forEach((button) => {
      const action = button.dataset.setupAction;
      button.disabled = busy || action === "check" && (state.setupScanLoading || state.setupDirty);
    });
    if (state.setupScanLoading) {
      ui.setupScanSummary.textContent = "Scanning physical devices… availability remains unverified until the scan completes.";
    }
    renderSetupReadiness();
    updateSetupValidation();
  }

  function renderSetupSelection() {
    const selected = setupEvent(state.setupSelectedEventId);
    ui.setupEventList.querySelectorAll(".setup-event-row").forEach((row) => {
      const isSelected = row.dataset.eventId === state.setupSelectedEventId;
      row.classList.toggle("is-selected", isSelected);
      const button = row.querySelector('[data-setup-action="select"]');
      if (button) {
        button.setAttribute("aria-pressed", String(isSelected));
        const label = button.querySelector("[data-setup-select-label]");
        if (label) label.textContent = isSelected ? "Selected" : "Select event";
      }
    });
    ui.setupSelectedEventLabel.textContent = selected
      ? `Assigning to: ${selected.name || selected.eventId}`
      : "Select an event to assign a device.";
  }

  function renderSetupDiscovery() {
    ui.setupDiscoveredDevices.replaceChildren();
    const scan = state.setupScan;
    if (!scan?.connected || !scan.completed || !scan.detectedDeviceIds.length) {
      ui.setupDiscoveredDevices.appendChild(make("span", "small-note", scan?.connected && scan?.completed
        ? "No hardware IDs were discovered by the last completed scan."
        : "No completed scan results."));
      return;
    }

    const selectedEventId = state.setupSelectedEventId;
    const assignedOwners = new Map();
    state.setupDraft.events.forEach((event) => {
      const mac = setupTools.hardwareId(event.assignmentValue);
      if (mac) assignedOwners.set(mac, event);
    });
    scan.detectedDeviceIds.forEach((mac) => {
      const owner = assignedOwners.get(mac);
      const button = make("button", "setup-device-chip", owner ? `${mac} · ${owner.name}` : mac);
      button.type = "button";
      button.dataset.setupDevice = mac;
      button.disabled = state.setupSaving || state.setupScanLoading || !selectedEventId || Boolean(owner && owner.eventId !== selectedEventId);
      button.setAttribute("aria-label", owner && owner.eventId === selectedEventId
        ? `${mac} is assigned to ${owner.name}`
        : owner ? `${mac} is already assigned to ${owner.name}` : `Assign ${mac} to selected event`);
      if (owner?.eventId === selectedEventId) button.setAttribute("aria-pressed", "true");
      ui.setupDiscoveredDevices.appendChild(button);
    });
  }

  function renderSetupReadiness() {
    const checkedAt = setupCheckedAt();
    ui.setupEventList.querySelectorAll(".setup-event-row").forEach((row) => {
      const event = setupEvent(row.dataset.eventId);
      const status = row.querySelector("[data-setup-readiness]");
      if (!event || !status) return;
      const readiness = physicalReadiness(event);
      status.className = `setup-readiness ${readiness.key}`;
      status.textContent = readiness.label;
      const checked = row.querySelector("[data-setup-readiness-checked]");
      if (checked) checked.textContent = checkedAt
        ? `Last checked ${formatLocalDateTime(checkedAt)}`
        : "No completed scan yet";
      const check = row.querySelector('[data-setup-action="check"]');
      if (check) check.disabled = state.setupLoading || state.setupSaving || state.setupScanLoading || state.setupDirty || state.busy;
    });
  }

  function setupScoringField(event, field, title, minimum, maximum) {
    const label = make("label", "setup-event-points");
    const caption = make("span", "", title);
    caption.dataset.setupScoringLabel = field;
    caption.dataset.setupScoringTitle = title;
    label.appendChild(caption);
    const input = make("input");
    input.type = "number";
    input.min = String(minimum);
    input.max = String(maximum);
    input.step = "1";
    input.inputMode = "numeric";
    input.value = event[field] ?? "";
    input.dataset.setupField = field;
    input.dataset.setupEventId = event.eventId;
    input.setAttribute("aria-label", `${event.name} ${title.toLowerCase()}`);
    label.appendChild(input);
    updateSetupScoringLabel(event, field, caption);
    return label;
  }

  function updateSetupScoringLabel(event, field, caption) {
    if (!caption) return;
    const inherited = Boolean(event[`${field}Inherited`]);
    const derivedMinimum = field === "minimumPoints" && inherited && !event.basePointsInherited;
    const title = caption.dataset.setupScoringTitle;
    caption.textContent = `${title}${inherited ? derivedMinimum ? " · derived" : " · default" : ""}`;
  }

  function refreshSetupScoringLabels(row, event) {
    row?.querySelectorAll("[data-setup-scoring-label]").forEach((caption) => {
      updateSetupScoringLabel(event, caption.dataset.setupScoringLabel, caption);
    });
  }

  // A keypad event's button starts it and shows the message on the TV; typing the code on
  // the button's keypad and pressing * finishes it.
  // Messages and codes come from the keypad answer file; Setup chooses how many a player
  // must answer to pass.
  function setupKeypadFields(event) {
    const wrap = make("div", "setup-event-keypad-row");
    const countLabel = make("label", "setup-event-name setup-event-keypad-count");
    countLabel.append(make("span", "", "Codes to pass"));
    const count = make("input");
    count.type = "number";
    count.min = "1";
    count.max = "20";
    count.step = "1";
    count.inputMode = "numeric";
    count.value = event.requiredSuccesses ?? 1;
    count.dataset.setupField = "requiredSuccesses";
    count.dataset.setupEventId = event.eventId;
    count.setAttribute("aria-label", `${event.name}: number of correct codes needed to pass`);
    countLabel.appendChild(count);

    const pool = state.setupDraft?.keypadMessages || { count: 0, source: "", error: "" };
    const poolNote = pool.error
      ? `Keypad answer file problem: ${pool.error} Until it is fixed, finish this event with the scorekeeper tile.`
      : `${pool.count} messages from ${pool.source ? pool.source.split(/[\\/]/).pop() : "the keypad answer file"}. Each start and each correct code draws a message not yet shown this run; its code is the message's row letter then column number (## for the ## row).`;
    const note = make("p", `setup-event-keypad-help${pool.error ? " is-error" : ""}`, poolNote);
    wrap.append(countLabel, note,
      make("p", "setup-event-keypad-help", "Pressing the button starts the event and shows a message on the TV. The player types its code and presses * — a wrong code flashes the button red; a right one shows the next message until enough are done."));
    return wrap;
  }

  function renderSetupEvents() {
    ui.setupEventList.replaceChildren();
    const events = state.setupDraft?.events || [];
    if (!events.length) {
      ui.setupEventList.appendChild(make("p", "empty-state", "No events configured. Add at least one event to build the scorecard."));
      return;
    }
    events.forEach((event, index) => {
      const row = make("article", "setup-event-row");
      row.dataset.eventId = event.eventId;
      const top = make("div", "setup-event-top");
      const select = make("button", "setup-event-pick");
      select.type = "button";
      select.dataset.setupAction = "select";
      select.dataset.setupEventId = event.eventId;
      select.setAttribute("aria-pressed", String(event.eventId === state.setupSelectedEventId));
      select.append(make("span", "setup-event-number", String(index + 1).padStart(2, "0")), make("span", "", event.eventId === state.setupSelectedEventId ? "Selected" : "Select event"));
      select.lastElementChild.dataset.setupSelectLabel = "true";

      const nameLabel = make("label", "setup-event-name");
      nameLabel.append(make("span", "", "Event name"));
      const nameInput = make("input");
      nameInput.type = "text";
      nameInput.maxLength = 120;
      nameInput.autocomplete = "off";
      nameInput.value = event.name;
      nameInput.dataset.setupField = "name";
      nameInput.dataset.setupEventId = event.eventId;
      nameInput.setAttribute("aria-label", `Event ${index + 1} name`);
      nameLabel.appendChild(nameInput);

      const scoring = make("div", "setup-event-scoring-row");
      scoring.append(
        setupScoringField(event, "basePoints", "Starting points", 0, 1_000_000),
        setupScoringField(event, "minimumPoints", "Minimum points", 0, 1_000_000),
        setupScoringField(event, "decayPoints", "Points lost / step", 0, 1_000_000),
        setupScoringField(event, "decayEverySeconds", "Seconds / step", 1, 86_400),
        setupScoringField(event, "graceSeconds", "Initial grace seconds", 0, 86_400)
      );

      const kind = make("label", "setup-event-kind");
      kind.append(make("span", "", "Event type"));
      const kindSelect = make("select");
      const kindOptions = [["standard", "Regular"], ["keypad", "Keypad code"]];
      if (!kindOptions.some(([value]) => value === event.type)) kindOptions.push([event.type, event.type === "magneticArcade" ? "Magnetic arcade" : titleCase(event.type)]);
      kindOptions.forEach(([value, label]) => {
        const option = make("option", "", label);
        option.value = value;
        kindSelect.appendChild(option);
      });
      kindSelect.value = event.type;
      kindSelect.dataset.setupField = "type";
      kindSelect.dataset.setupEventId = event.eventId;
      kindSelect.setAttribute("aria-label", `${event.name} event type`);
      kind.appendChild(kindSelect);
      const remove = make("button", "button button-quiet setup-event-remove", "Remove");
      remove.type = "button";
      remove.dataset.setupAction = "remove";
      remove.dataset.setupEventId = event.eventId;
      remove.setAttribute("aria-label", `Remove ${event.name || event.eventId}`);
      top.append(select, nameLabel, kind, remove);

      const deviceRow = make("div", "setup-event-device-row");
      const assignmentLabel = make("label", "setup-event-assignment");
      assignmentLabel.append(make("span", "", "Physical device MAC (optional)"));
      const assignment = make("input");
      assignment.type = "text";
      assignment.maxLength = 17;
      assignment.inputMode = "text";
      assignment.autocomplete = "off";
      assignment.spellcheck = false;
      assignment.placeholder = "Unassigned · 12 hex digits";
      assignment.value = event.assignmentValue;
      assignment.dataset.setupField = "assignment";
      assignment.dataset.setupEventId = event.eventId;
      assignment.setAttribute("aria-label", `${event.name} physical device MAC; blank means unassigned`);
      assignmentLabel.appendChild(assignment);
      const unassign = make("button", "button button-quiet setup-event-clear", "Use virtual");
      unassign.type = "button";
      unassign.dataset.setupAction = "unassign";
      unassign.dataset.setupEventId = event.eventId;
      unassign.setAttribute("aria-label", `Unassign physical hardware for ${event.name}; keep its virtual event button`);

      const readiness = make("div", "setup-event-readiness");
      const readinessBadge = make("span", "setup-readiness", "Unverified");
      readinessBadge.dataset.setupReadiness = "true";
      const check = make("button", "button button-secondary setup-event-check", "Check");
      check.type = "button";
      check.dataset.setupAction = "check";
      check.dataset.setupEventId = event.eventId;
      check.setAttribute("aria-label", `Run a fresh physical scan for ${event.name}`);
      readiness.append(readinessBadge, check);
      const checkedLabel = make("small", "setup-readiness-checked", "No completed scan yet");
      checkedLabel.dataset.setupReadinessChecked = "true";
      readiness.appendChild(checkedLabel);
      deviceRow.append(assignmentLabel, unassign, readiness);

      const internalId = make("p", "setup-event-id", `Event ID · ${event.eventId}`);
      row.append(top, scoring, deviceRow);
      if (event.type === "keypad") row.appendChild(setupKeypadFields(event));
      row.appendChild(internalId);
      ui.setupEventList.appendChild(row);
    });
    renderSetupSelection();
    renderSetupReadiness();
    renderSetupPreview();
  }

  function setupCheckedAt() {
    if (state.setupScanFresh && state.setupScanAt) return state.setupScanAt;
    return state.snapshot?.deviceScanCheckedAt || null;
  }

  function renderSetupPreview() {
    if (!ui.setupPreviewList) return;
    const events = state.setupDraft?.events || [];
    ui.setupPreviewList.replaceChildren();
    ui.setupPreviewSummary.textContent = `${events.length} ${events.length === 1 ? "event" : "events"} · order shown is the scorekeeper button order. Changes here are previews until you save setup.`;
    events.forEach((event, index) => {
      const effectiveNumber = (value, fallback) => {
        const candidate = value ?? fallback;
        return candidate === "" || !Number.isFinite(Number(candidate)) ? null : Number(candidate);
      };
      const starting = effectiveNumber(event.basePoints, state.setupDraft.scoringDefaults.basePoints);
      const minimum = effectiveNumber(event.minimumPoints, state.setupDraft.scoringDefaults.minimumPoints);
      const decay = effectiveNumber(event.decayPoints, state.setupDraft.scoringDefaults.decayPoints);
      const interval = effectiveNumber(event.decayEverySeconds, state.setupDraft.scoringDefaults.decayEverySeconds);
      const grace = effectiveNumber(event.graceSeconds, 0);
      const firstDrop = grace > 0 ? grace : interval;
      const deviceId = setupTools.hardwareId(event.assignmentValue || "");
      const kind = ({ standard: "Regular", keypad: "Keypad", magneticArcade: "Arcade" })[event.type] || titleCase(event.type);
      const details = `${kind} · ${starting ?? "—"} start · −${decay ?? "—"} at ${firstDrop ?? "—"}s, then every ${interval ?? "—"}s · min ${minimum ?? "—"} · ${deviceId ? `Button ${deviceId}` : "Virtual only"}`;
      const row = make("li", "setup-preview-item");
      row.append(make("span", "setup-preview-order", String(index + 1)), make("span", "setup-preview-event"));
      row.querySelector(".setup-preview-event").append(make("strong", "", event.name || `Event ${index + 1}`), make("small", "", details));
      ui.setupPreviewList.appendChild(row);
    });
  }

  function renderSetup() {
    const hasSetup = Boolean(state.setupDraft);
    ui.setupContent.hidden = !hasSetup;
    ui.setupLoadState.hidden = hasSetup;
    ui.setupRetryLoad.hidden = hasSetup || !state.setupLoadError;
    if (!hasSetup) {
      ui.setupLoadState.textContent = state.setupLoadError
        ? `Setup could not be loaded: ${state.setupLoadError}`
        : state.setupLoading ? "Loading setup…" : "Setup is unavailable. Retry to load the edition configuration.";
      return;
    }
    if (document.activeElement !== ui.setupEditionName) ui.setupEditionName.value = state.setupDraft.name;
    renderSetupBonus();
    renderSetupEvents();
    renderSetupDiscovery();
    renderSetupControls();
    renderSetupSelection();
    renderSetupPreview();
  }

  function markSetupChanged() {
    state.setupDirty = true;
    state.setupScanFresh = false;
    state.virtualKey = null;
    renderSetupControls();
    renderSetupDiscovery();
    renderSetupReadiness();
    renderSetupPreview();
    if (state.snapshot) renderVirtualButtons(state.snapshot.currentRun);
  }

  function renderSetupBonus() {
    const bonus = state.setupDraft?.bonusGame;
    if (!ui.setupBonus || !bonus) return;
    ui.setupBonus.querySelectorAll("input[data-bonus-field]").forEach((input) => {
      if (document.activeElement === input) return;
      if (input.type === "checkbox") input.checked = bonus.enabled;
      else input.value = bonus[input.dataset.bonusField] ?? "";
      if (input.type !== "checkbox") input.disabled = !bonus.enabled;
    });
  }

  function onSetupBonusInput(event) {
    const input = event.target.closest("input[data-bonus-field]");
    const bonus = state.setupDraft?.bonusGame;
    if (!input || !bonus) return;
    bonus[input.dataset.bonusField] = input.type === "checkbox" ? input.checked : input.value;
    if (input.type === "checkbox") renderSetupBonus();
    markSetupChanged();
  }

  function onSetupInput(event) {
    if (!state.setupDraft) return;
    if (event.target === ui.setupEditionName) {
      state.setupDraft.name = event.target.value;
      markSetupChanged();
      return;
    }
    const input = event.target.closest("input[data-setup-field], select[data-setup-field]");
    if (!input) return;
    const target = setupEvent(input.dataset.setupEventId);
    if (!target) return;
    if (input.dataset.setupField === "name") target.name = input.value;
    else if (input.dataset.setupField === "assignment") target.assignmentValue = input.value;
    else if (input.dataset.setupField === "requiredSuccesses") target.requiredSuccesses = input.value;
    else if (input.dataset.setupField === "type") {
      if (target.type === input.value) return;
      target.type = input.value;
      markSetupChanged();
      // The keypad message/code fields appear or disappear with the type.
      renderSetupEvents();
      return;
    }
    else if (["basePoints", "minimumPoints", "decayPoints", "decayEverySeconds", "graceSeconds"].includes(input.dataset.setupField)) {
      setupTools.updateEventScoring(target, input.dataset.setupField, input.value);
      const row = input.closest(".setup-event-row");
      if (input.dataset.setupField === "basePoints" && target.minimumPointsInherited) {
        const minimumInput = row?.querySelector('input[data-setup-field="minimumPoints"]');
        if (minimumInput) minimumInput.value = target.minimumPoints;
      }
      refreshSetupScoringLabels(row, target);
    }
    markSetupChanged();
  }

  function onSetupClick(event) {
    const deviceButton = event.target.closest("button[data-setup-device]");
    if (deviceButton) {
      const target = setupEvent(state.setupSelectedEventId);
      if (!target || deviceButton.disabled) return;
      target.assignmentValue = deviceButton.dataset.setupDevice;
      const row = Array.from(ui.setupEventList.querySelectorAll(".setup-event-row")).find((item) => item.dataset.eventId === target.eventId);
      const input = row?.querySelector('input[data-setup-field="assignment"]');
      if (input) input.value = target.assignmentValue;
      markSetupChanged();
      return;
    }
    const actionButton = event.target.closest("button[data-setup-action]");
    if (!actionButton || actionButton.disabled) return;
    const action = actionButton.dataset.setupAction;
    const eventId = actionButton.dataset.setupEventId;
    const target = setupEvent(eventId);
    if (action === "select" && target) {
      state.setupSelectedEventId = eventId;
      renderSetupSelection();
      renderSetupDiscovery();
    } else if (action === "unassign" && target) {
      target.assignmentValue = "";
      const row = actionButton.closest(".setup-event-row");
      const input = row?.querySelector('input[data-setup-field="assignment"]');
      if (input) input.value = "";
      markSetupChanged();
    } else if (action === "remove" && target) {
      state.setupDraft.events = state.setupDraft.events.filter((item) => item.eventId !== eventId);
      if (state.setupSelectedEventId === eventId) state.setupSelectedEventId = state.setupDraft.events[0]?.eventId || null;
      markSetupChanged();
      renderSetupEvents();
      renderSetupControls();
      renderSetupDiscovery();
    } else if (action === "check") {
      void runSetupScan(eventId);
    }
  }

  async function runSetupScan(eventId = null) {
    if (state.setupScanLoading || state.setupDirty) {
      if (state.setupDirty) ui.setupValidation.textContent = "Save setup changes before scanning the current event assignments.";
      return;
    }
    const armRequestVersionAtStart = state.armScanRequestVersion;
    state.setupScanLoading = true;
    state.setupScanError = "";
    renderSetupControls();
    renderSetupReadiness();
    try {
      state.setupScan = setupTools.normalizeScanResponse(await request("/api/master/scan", { method: "POST" }));
      state.setupScanFresh = state.setupScan.connected && state.setupScan.completed;
      state.setupScanAt = Date.now();
      if (eventId) state.setupSelectedEventId = eventId;
    } catch (error) {
      state.setupScan = { connected: false, completed: false, devices: [], detectedDeviceIds: [] };
      state.setupScanFresh = false;
      state.setupScanAt = 0;
      state.setupScanError = `Scan failed: ${error.message || "the master scan could not be completed."}`;
    } finally {
      state.setupScanLoading = false;
      if (armRequestVersionAtStart !== state.armScanRequestVersion) {
        state.setupScanFresh = false;
        state.setupScanAt = 0;
      } else if (state.setupScanFresh) {
        state.armScanPending = false;
        state.armScanAfterSnapshotRequestId = null;
      }
      state.virtualKey = null;
      renderSetupControls();
      renderSetupDiscovery();
      renderSetupReadiness();
      renderSetupSelection();
      if (state.snapshot) renderVirtualButtons(state.snapshot.currentRun);
    }
  }

  async function saveSetup() {
    if (!state.setupDraft || state.setupSaving || !state.setupDirty) return;
    let payload;
    try {
      payload = setupTools.buildSetupPayload(state.setupDraft);
    } catch (error) {
      ui.setupValidation.textContent = error.message || "Review setup before saving.";
      return;
    }
    state.setupSaving = true;
    renderSetupControls();
    renderSetupReadiness();
    try {
      const response = await request("/api/setup", { method: "PUT", body: JSON.stringify(payload) });
      const effective = response && (Array.isArray(response.events) || Array.isArray(response.setup?.events)) ? response : payload;
      state.setupDraft = setupTools.normalizeSetup(effective, state.setupDraft.scoringDefaults);
      state.setupDirty = false;
      state.setupScanFresh = false;
      state.setupScanAt = 0;
      state.setupScanError = "";
      if (!setupEvent(state.setupSelectedEventId)) state.setupSelectedEventId = state.setupDraft.events[0]?.eventId || null;
      renderSetup();
      await loadSnapshot(true);
      showAlert("Setup saved. Run a fresh scan to check physical availability.", "success");
    } catch (error) {
      ui.setupValidation.textContent = error.message || "Setup could not be saved.";
    } finally {
      state.setupSaving = false;
      renderSetupControls();
    }
  }

  function renderCompetitors() {
    const allCompetitors = state.snapshot?.competitors || [];
    const competitors = allCompetitors.filter((item) => !item.isArchived);
    const signature = allCompetitors.map((item) => `${item.id}:${item.name}:${item.isArchived ? 1 : 0}`).join("|");
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
    renderCompetitorRoster();
    updateDuplicateCompetitorWarning();
  }

  function normalizedCompetitorName(value) {
    return String(value || "").trim().replace(/\s+/g, " ").toLowerCase();
  }

  function updateDuplicateCompetitorWarning() {
    const value = normalizedCompetitorName(ui.newCompetitor.value);
    const match = (state.snapshot?.competitors || []).find((item) => normalizedCompetitorName(item.name) === value);
    ui.competitorDuplicateWarning.hidden = !value || !match;
    ui.competitorDuplicateWarning.textContent = match
      ? `A player named “${match.name}” already exists${match.isArchived ? " (archived)" : ""}. Add anyway only if these are different people.`
      : "";
  }

  function renderCompetitorRoster() {
    const all = state.snapshot?.competitors || [];
    const filtered = all
      .filter((item) => state.showArchivedCompetitors || !item.isArchived)
      .slice().sort((left, right) => left.name.localeCompare(right.name));
    ui.competitorRosterList.replaceChildren();
    if (!filtered.length) {
      ui.competitorRosterList.appendChild(make("li", "empty-state", all.length
        ? "No active players. Turn on “Show archived players” to include archived names."
        : "No players yet."));
      return;
    }
    filtered.forEach((competitor) => {
      const row = make("li", `competitor-roster-row${competitor.isArchived ? " is-archived" : ""}`);
      const identity = make("span", "competitor-roster-identity");
      identity.append(make("strong", "", competitor.name), make("small", "", competitor.isArchived ? "Archived · history retained" : "Active player"));
      const actions = make("span", "competitor-roster-actions");
      const rename = make("button", "button button-quiet", "Rename");
      rename.type = "button";
      rename.dataset.rosterAction = "rename";
      rename.dataset.competitorId = competitor.id;
      rename.setAttribute("aria-label", `Rename ${competitor.name}`);
      actions.appendChild(rename);
      const archive = make("button", "button button-quiet", competitor.isArchived ? "Restore" : "Archive");
      archive.type = "button";
      archive.dataset.rosterAction = competitor.isArchived ? "restore" : "archive";
      archive.dataset.competitorId = competitor.id;
      archive.setAttribute("aria-label", `${competitor.isArchived ? "Restore" : "Archive"} ${competitor.name}`);
      actions.appendChild(archive);
      row.append(identity, actions);
      ui.competitorRosterList.appendChild(row);
    });
  }

  function parseCompetitorCsv(text) {
    text = String(text || "").replace(/^\uFEFF/, "");
    const rows = [];
    let row = [];
    let field = "";
    let quoted = false;
    for (let index = 0; index < text.length; index += 1) {
      const char = text[index];
      if (quoted) {
        if (char === '"' && text[index + 1] === '"') { field += '"'; index += 1; }
        else if (char === '"') quoted = false;
        else field += char;
      } else if (char === '"' && field.length === 0) quoted = true;
      else if (char === ",") { row.push(field); field = ""; }
      else if (char === "\n" || char === "\r") {
        row.push(field);
        if (row.some((cell) => cell.trim())) rows.push(row);
        row = []; field = "";
        if (char === "\r" && text[index + 1] === "\n") index += 1;
      } else field += char;
    }
    row.push(field);
    if (row.some((cell) => cell.trim())) rows.push(row);
    if (rows.length && /^(name|player|competitor)( name)?$/i.test(rows[0][0]?.trim() || "")) rows.shift();
    return rows.map((cells) => cells[0]?.trim() || "").filter(Boolean);
  }

  async function handleRosterAction(event) {
    const button = event.target.closest("button[data-roster-action]");
    if (!button || button.disabled) return;
    const competitor = (state.snapshot?.competitors || []).find((item) => item.id === button.dataset.competitorId);
    if (!competitor) return;
    if (button.dataset.rosterAction === "rename") {
      const value = window.prompt(`Rename ${competitor.name}. Their existing run history will stay linked and will display the new name.`, competitor.name);
      if (value === null || !value.trim() || value.trim() === competitor.name) return;
      await performAction(() => request(`/api/competitors/${encodeURIComponent(competitor.id)}`, {
        method: "PUT", body: JSON.stringify({ name: value.trim() })
      }), "Player renamed; past runs remain linked to this player.");
      return;
    }
    const restoring = button.dataset.rosterAction === "restore";
    const prompt = restoring
      ? `Restore ${competitor.name} to the active player list? Their saved history is unchanged.`
      : `Archive ${competitor.name}? This hides them from new runs but keeps all of their history. Nothing will be deleted.`;
    if (!window.confirm(prompt)) return;
    await performAction(() => request(`/api/competitors/${encodeURIComponent(competitor.id)}/archive`, {
      method: "POST", body: JSON.stringify({ isArchived: !restoring })
    }), restoring ? "Player restored." : "Player archived; saved history was kept.");
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

  // The bonus speed round appears on scorecards, run history, and leaderboards as one more
  // event row (id "bonus-round"): start and end times, hits, and points (hits x points per
  // press unless overridden). The app stores it on the run rather than in its event list.
  const BONUS_EVENT_ID = "bonus-round";
  const DEFAULT_BONUS_NAME = "Bonus round";

  // The round's name from Setup: a run keeps the name it was played under; the current
  // edition's name labels the leaderboards and pages without a run.
  function bonusName(run) {
    const name = run?.edition?.bonusGame?.name || state.snapshot?.bonusGame?.name || DEFAULT_BONUS_NAME;
    return String(name).trim() || DEFAULT_BONUS_NAME;
  }

  function bonusEventDefinition(run) {
    return { eventId: BONUS_EVENT_ID, name: bonusName(run), type: "bonusRound" };
  }

  // The edition's events plus the bonus round when it is played (or some run has a result).
  function leaderboardEventDefinitions(snapshot) {
    const events = snapshot?.events || [];
    const bonusInUse = (snapshot?.history || []).some((run) => run.bonusGame) ||
      Boolean(snapshot?.currentRun?.edition?.bonusGame?.enabled) || Boolean(state.setupDraft?.bonusGame?.enabled);
    return bonusInUse ? [...events, bonusEventDefinition(null)] : events;
  }

  function isBonusScorecardEvent(event) {
    return event?.eventId === BONUS_EVENT_ID;
  }

  function bonusScorecardEvent(run) {
    if (!run) return null;
    const bonus = run.bonusGame;
    if (!bonus && !run.edition?.bonusGame?.enabled) return null;
    const ended = String(bonus?.phase || "").toLowerCase() === "ended";
    const perPress = Number(bonus?.pointsPerPress ?? run.edition?.bonusGame?.pointsPerPress ?? 0);
    const hits = Number(bonus?.hits || 0);
    return {
      ...bonusEventDefinition(run),
      status: !bonus ? "pending" : ended ? "completed" : "active",
      running: Boolean(bonus) && !ended,
      startElapsedMs: bonus ? bonus.startedElapsedMs ?? null : null,
      finishElapsedMs: ended ? bonus.endedElapsedMs ?? null : null,
      hits,
      pointsPerPress: perPress,
      score: ended ? Number(bonus.awardedPoints ?? hits * perPress) : 0,
      scoreOverride: bonus?.scoreOverride ?? null
    };
  }

  function scorecardEvents(run, events = runEvents(run)) {
    const bonus = bonusScorecardEvent(run);
    return bonus ? [...events, bonus] : events;
  }

  function bonusDraftHits(run, event) {
    const draft = state.drafts.get(run.id)?.get(event.eventId);
    if (draft?.touched.has("hits")) {
      const typed = Number(draft.hits);
      return draft.hits !== "" && Number.isInteger(typed) && typed >= 0 ? typed : 0;
    }
    return Number(event.hits || 0);
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
    const seconds = Number(run?.edition?.durationLimitSeconds);
    return Number.isInteger(seconds) && seconds > 0 && seconds <= 5999 ? seconds : 300;
  }

  function selectedRunDurationSeconds() {
    return masterActions.parseRunDuration(ui.durationInput.value);
  }

  function syncRunDurationControl(run) {
    if (masterActions.isRunDurationLocked(run)) {
      ui.durationInput.value = masterActions.formatRunDuration(runDurationSeconds(run));
      state.durationRunId = run.id;
      state.durationResetRunId = null;
      ui.durationInput.disabled = true;
      ui.durationInput.removeAttribute("aria-invalid");
      ui.durationHelp.textContent = "Actual run length · locked for this run.";
      return;
    }

    if (run && masterActions.shouldResetRunDuration(run, state.durationResetRunId)) {
      ui.durationInput.value = "5:00";
      state.durationResetRunId = run.id;
    } else if (!run && state.durationRunId !== null) {
      ui.durationInput.value = "5:00";
    }
    state.durationRunId = null;
    ui.durationInput.disabled = state.busy || state.masterBusy;
    const valid = selectedRunDurationSeconds() !== null;
    ui.durationInput.setAttribute("aria-invalid", String(!valid));
    ui.durationHelp.textContent = valid
      ? "Default 5:00 · change before arming or starting."
      : "Enter a positive run length as M:SS, from 0:01 to 99:59.";
  }

  function formatSeconds(milliseconds) {
    return scorekeeperTime.formatClockMs(milliseconds);
  }

  function formatDuration(milliseconds) {
    if (milliseconds === null || milliseconds === undefined || milliseconds < 0) return "—";
    return scorekeeperTime.formatClockMs(milliseconds);
  }

  function formatLocalDateTime(value) {
    const date = new Date(value);
    if (!Number.isFinite(date.getTime())) return "time unavailable";
    return new Intl.DateTimeFormat(undefined, {
      month: "short", day: "numeric", hour: "numeric", minute: "2-digit", second: "2-digit"
    }).format(date);
  }

  function displayedValue(run, event, field) {
    const draft = state.drafts.get(run.id)?.get(event.eventId);
    if (field === "score") return eventScoreDraftView(run, event).inputValue;
    if (draft?.touched.has(field)) return draft[field];
    if (field === "hits") return String(event.hits ?? 0);
    if (field === "start" || field === "finish") {
      const elapsed = field === "start" ? event.startElapsedMs : event.finishElapsedMs;
      const remaining = scorekeeperTime.remainingSecondsFromElapsedMs(elapsed, runDurationSeconds(run));
      return remaining === null ? "" : formatSeconds(remaining * 1000);
    }
    return "";
  }

  function currentElapsedMs(run) {
    const base = Number(run?.activeElapsedMs || 0);
    if (run?.status === "active") return base + Math.max(0, Date.now() - state.receivedAt);
    return base;
  }

  function rowTiming(row, run) {
    const durationCell = row.querySelector(".duration-cell");
    const statusCell = row.querySelector(".event-status");
    if (!run) {
      if (durationCell) durationCell.textContent = "—";
      if (statusCell) {
        statusCell.textContent = "Pending";
        statusCell.className = "event-status pending";
      }
      return;
    }
    const event = scorecardEvents(run).find((item) => item.eventId === row.dataset.eventId);
    const durationSeconds = runDurationSeconds(run);
    const startMs = event
      ? eventElapsedMsForDraft(run, event, "start")
      : scorekeeperTime.elapsedMsFromRemainingSeconds(parsedSeconds(row.querySelector('[data-field="start"]')?.value), durationSeconds);
    const finishMs = event
      ? eventElapsedMsForDraft(run, event, "finish")
      : scorekeeperTime.elapsedMsFromRemainingSeconds(parsedSeconds(row.querySelector('[data-field="finish"]')?.value), durationSeconds);
    let duration = null;
    if (startMs !== null && finishMs !== null) duration = finishMs - startMs;
    else if (startMs !== null && run?.status === "active") duration = currentElapsedMs(run) - startMs;
    if (durationCell) durationCell.textContent = formatDuration(duration);
    const derivedStatus = event?.running ? "active"
      : startMs !== null && finishMs !== null ? "completed" : startMs !== null ? "active" : "pending";
    if (statusCell) {
      statusCell.textContent = derivedStatus === "completed" ? "Complete"
        : derivedStatus === "active" ? "In progress"
          : isBonusScorecardEvent(event) ? "Not reached" : "Pending";
      statusCell.className = `event-status ${derivedStatus}`;
    }
  }

  function eventElapsedMsForDraft(run, event, field) {
    if (!run || !event?.eventId) return null;
    const draft = state.drafts.get(run.id)?.get(event.eventId);
    const elapsedField = field === "start" ? "startElapsedMs" : "finishElapsedMs";
    return scorekeeperTime.elapsedMsForDraft(
      event[elapsedField],
      Boolean(draft?.touched.has(field)),
      draft?.[field],
      runDurationSeconds(run)
    );
  }

  function previewEventScore(run, event) {
    const startElapsedMs = eventElapsedMsForDraft(run, event, "start");
    const finishElapsedMs = eventElapsedMsForDraft(run, event, "finish");
    const draft = state.drafts.get(run.id)?.get(event.eventId);
    const timingTouched = draft?.touched.has("start") || draft?.touched.has("finish");
    let scoreOverride = event.scoreOverride;
    if (draft?.touched.has("score")) scoreOverride = draft.score === "" ? null : Number(draft.score);
    let status = event.status;
    if (timingTouched) {
      status = startElapsedMs !== null && finishElapsedMs !== null
        ? "completed"
        : startElapsedMs !== null ? "active" : "pending";
    }
    const eventDefinition = run.edition?.events?.find((item) => item.eventId === event.eventId) || null;
    return scorekeeperTime.previewEventScore({
      status,
      startElapsedMs,
      finishElapsedMs,
      scoreOverride
    }, run.edition?.scoring, eventDefinition);
  }

  function eventScoreDraftView(run, event) {
    const draft = state.drafts.get(run.id)?.get(event.eventId);
    const scoreTouched = Boolean(draft?.touched.has("score"));
    if (isBonusScorecardEvent(event)) {
      // Bonus points come from hits, not time; they count once the round has ended.
      const hitsTouched = Boolean(draft?.touched.has("hits"));
      const override = event.scoreOverride;
      const fromHits = bonusDraftHits(run, event) * event.pointsPerPress;
      const clearingOverride = scoreTouched && draft.score === "";
      return scorekeeperTime.eventScoreDraftView({
        scoreTouched,
        scoreValue: draft?.score,
        timingTouched: hitsTouched,
        // A manual override stays in place when hits change, as it does in the app.
        previewScore: clearingOverride ? fromHits : override ?? fromHits,
        persistedScore: event.status === "completed" ? override ?? event.score ?? 0 : 0
      });
    }
    const timingTouched = Boolean(draft?.touched.has("start") || draft?.touched.has("finish"));
    const needsPreview = timingTouched || (scoreTouched && draft.score === "");
    return scorekeeperTime.eventScoreDraftView({
      scoreTouched,
      scoreValue: draft?.score,
      timingTouched,
      previewScore: needsPreview ? previewEventScore(run, event) : 0,
      persistedScore: event.scoreOverride ?? event.score ?? 0
    });
  }

  function displayedScore(run, event) {
    return eventScoreDraftView(run, event).totalPoints;
  }

  function displayedBonus(run) {
    if (!run) return 0;
    const draft = state.bonusDrafts.get(run.id);
    if (draft?.touched) {
      const value = Number(draft.value);
      return draft.value !== "" && Number.isFinite(value) ? value : 0;
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
    // The bonus speed round is one of the rows; its points count once the round has ended.
    return scorecardEvents(run).reduce((sum, event) => sum + displayedScore(run, event), displayedBonus(run));
  }

  function updateTableTotal(kind, run) {
    const total = tableTotal(run);
    if (kind === "current") ui.currentTotal.textContent = String(total);
    else ui.historyTotal.textContent = String(total);
    if (kind === "current") ui.runTotal.textContent = String(total);
    if (kind === "current") ui.scorecardSummaryTotal.textContent = `${total} pts`;
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

  function buildHitsInput(run, event, editable) {
    const input = make("input", "score-input hits-input");
    input.type = "number";
    input.min = "0";
    input.step = "1";
    input.inputMode = "numeric";
    input.setAttribute("aria-label", `${event.name} hits (${event.pointsPerPress} points each)`);
    input.title = `Hits · ${event.pointsPerPress} points each`;
    input.dataset.field = "hits";
    input.dataset.eventId = event.eventId;
    input.dataset.runId = run?.id || "";
    input.value = run ? displayedValue(run, event, "hits") : "";
    input.disabled = !editable;
    return input;
  }

  function renderScoreTable(run, tbody, kind, editable, events = scorecardEvents(run)) {
    tbody.replaceChildren();
    events.forEach((event, index) => {
      const bonusRow = isBonusScorecardEvent(event);
      // The bonus round is corrected once it has ended (the app refuses edits mid-round).
      const rowEditable = editable && !(bonusRow && event.running);
      const row = make("tr", `score-row${bonusRow ? " bonus-score-row" : ""}`);
      const nameCell = make("td", "event-name-cell");
      const indexBadge = make("span", "event-index", bonusRow ? "★" : String(index + 1).padStart(2, "0"));
      nameCell.append(indexBadge, document.createTextNode(event.name));
      if (bonusRow) {
        const hits = make("label", "bonus-hits-field");
        hits.append(buildHitsInput(run, event, rowEditable), make("span", "", ` hits × ${event.pointsPerPress}`));
        nameCell.appendChild(hits);
      }
      const startCell = make("td");
      const finishCell = make("td");
      const durationCell = make("td", "duration-cell", "—");
      const pointsCell = make("td");
      const statusCell = make("td", "event-status", "Pending");
      const actionsCell = make("td", "event-actions-cell");
      durationCell.dataset.eventId = event.eventId;
      statusCell.dataset.eventId = event.eventId;
      startCell.appendChild(buildTimeInput(run, event, "start", rowEditable));
      finishCell.appendChild(buildTimeInput(run, event, "finish", rowEditable));
      pointsCell.appendChild(buildScoreInput(run, event, rowEditable));
      if (run && (!bonusRow || (run.bonusGame && !event.running))) {
        const clearButton = make("button", "button button-quiet event-clear-button", "Clear");
        clearButton.type = "button";
        clearButton.dataset.clearEvent = "true";
        clearButton.dataset.eventId = event.eventId;
        clearButton.dataset.runId = run.id;
        clearButton.dataset.revision = String(run.revision);
        clearButton.setAttribute("aria-label", `Clear all results for ${event.name}`);
        clearButton.disabled = state.busy;
        actionsCell.appendChild(clearButton);
      }
      row.append(nameCell, startCell, finishCell, durationCell, pointsCell, statusCell, actionsCell);
      row.dataset.eventId = event.eventId;
      tbody.appendChild(row);
      rowTiming(row, run);
    });
    updateTableTotal(kind, run);
  }

  function undoableEventPress(run) {
    // Presses can't be undone once a bonus round has started (the app refuses).
    if (!run || run.bonusGame || !["active", "paused", "finished", "timedOut"].includes(run.status)) return null;
    const eventsByDevice = new Map((run.events || [])
      .filter((event) => isUndoableEventType(event))
      .map((event) => [String(event.deviceId || "").toUpperCase(), event]));
    return (state.snapshot?.messages || [])
      .filter((message) => message.runId === run.id && String(message.disposition).toLowerCase() === "accepted")
      .map((message) => ({ message, event: eventsByDevice.get(String(message.deviceId || "").toUpperCase()) }))
      .filter(({ message, event }) => event && isUndoablePress(event, message))
      .sort((left, right) => Number(right.message.id) - Number(left.message.id))[0] || null;
  }

  function keypadProgress(run, event) {
    const challenges = event.keypad?.challenges || [];
    const definition = (run?.edition?.events || []).find((item) => item.eventId === event.eventId);
    return {
      solved: challenges.filter((challenge) => challenge.solvedElapsedMs != null).length,
      required: Math.max(1, Number(definition?.requiredSuccesses) || 1)
    };
  }

  function isUndoableEventType(event) {
    return ["standard", "keypad"].includes(String(event.type || "standard").toLowerCase());
  }

  // Mirrors RunService.IsUndoablePress: the start press while running, or the finishing
  // press once complete. Keypad events step back their last solved code (the correct code or
  // operator credit that solved it) before their start; wrong codes are never undone.
  function isUndoablePress(event, message) {
    const status = String(event.status).toLowerCase();
    const elapsed = message.elapsedMilliseconds;
    if (String(event.type || "").toLowerCase() === "keypad") {
      const solved = (event.keypad?.challenges || []).filter((challenge) => challenge.solvedElapsedMs != null);
      const lastSolved = solved[solved.length - 1];
      if (lastSolved) return ["active", "completed"].includes(status) && lastSolved.solvedByMessageId === message.messageId;
      return status === "active" && message.type === "event-press" && event.startElapsedMs === elapsed && event.finishElapsedMs == null;
    }
    if (status === "completed") {
      return message.type === "event-press" && event.finishElapsedMs === elapsed && event.lastSignalElapsedMs === elapsed;
    }
    return status === "active" && message.type === "event-press" && event.startElapsedMs === elapsed &&
      event.finishElapsedMs == null && event.lastSignalElapsedMs === elapsed;
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
    const currentDirty = Boolean(current && (hasDrafts(current.id) || hasBonusDraft(current.id)));
    // The scorecard stays editable for the run on screen after it finishes or times out,
    // so edits typed during the run can always be saved (or discarded).
    const currentEditable = Boolean(current);
    ui.currentSave.disabled = state.busy || !currentDirty || !currentEditable;
    ui.currentSaveState.textContent = currentDirty ? "Unsaved edits" : "No unsaved edits";
    // Record (and Reopen) appear beside the run status only once the run is over.
    const recordable = runActions.isRecordableRun(current);
    const reopenable = runActions.canReopenRun(current);
    ui.recordActions.hidden = !recordable && !reopenable;
    ui.record.hidden = !recordable;
    ui.record.disabled = state.busy || !recordable;
    ui.reopen.hidden = !reopenable;
    ui.reopen.disabled = state.busy || !reopenable;
    ui.record.textContent = currentDirty ? "Save edits & record" : "Record result";
    ui.record.title = currentDirty ? "Saves your scorecard edits first, so the recorded result matches the scorecard." : "";
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
    syncBonusInput(ui.currentBonus, run, Boolean(run));
    const key = run ? `${run.id}:${run.revision}:${run.status}` : `no-run:${eventRosterSignature()}`;
    if (key !== state.currentTableKey) {
      const focus = captureFocus();
      renderScoreTable(run, ui.currentBody, "current", Boolean(run), scorecardEvents(run, currentEditionEvents(run)));
      state.currentTableKey = key;
      restoreFocus(focus);
    } else {
      updateTableTotal("current", run);
    }
    const events = currentEditionEvents(run);
    const complete = run ? events.filter((event) => event.status === "completed").length : 0;
    ui.progress.textContent = `${complete} / ${events.length}`;
    if (!run) {
      ui.banner.textContent = state.discardedRunNotice || "No run is active. Select a competitor to begin.";
      ui.banner.classList.toggle("is-discarded", Boolean(state.discardedRunNotice));
      const selectedDuration = selectedRunDurationSeconds();
      ui.caption.textContent = state.discardedRunNotice
        ? "Discarded run · not recorded"
        : selectedDuration === null
          ? "Enter a positive run length as M:SS, from 0:01 to 99:59."
          : `Choose a competitor, then start the ${masterActions.formatRunDuration(selectedDuration)} clock.`;
    } else {
      ui.banner.classList.remove("is-discarded");
      const label = categoryLabel(run.category);
      const competitor = competitorName(run.competitorId);
      const copy = {
        armed: "Run is armed and waiting for a physical Start press, or use Start armed run here.",
        countdown: "Countdown audio is playing. The run timer and event buttons start when it ends.",
        active: "Run in progress. Event timestamps count down from the run limit.",
        paused: "Run paused. Resume when the competitor is ready.",
        finished: (run.events || []).every((event) => event.status === "completed")
          ? "All events complete. Timer stopped · finished, not recorded."
          : "Run finished early. Timer stopped · unfinished events remain; not recorded. Finished by mistake? Reopen it.",
        completed: "Run finished · recorded.",
        timedOut: run.isRecorded
          ? "Time expired · incomplete result recorded."
          : "Time expired · not recorded yet. Record or discard this result before the next run.",
        aborted: "Run discarded · retained in history · not recorded or counted toward results.",
        superseded: "Run replaced by a newer result."
      }[run.status] || `Run status: ${titleCase(run.status)}.`;
      ui.banner.textContent = `${bonusBannerText(run) || copy} ${competitor} · ${label}`;
      ui.caption.textContent = `${competitor} · ${label} · ${titleCase(run.status)} · ${masterActions.formatRunDuration(runDurationSeconds(run))} run`;
    }
    // The status line is capped at two lines so it never grows; the full text is on hover.
    ui.banner.title = ui.banner.textContent;
    renderVirtualButtons(run);
  }

  function renderVirtualButtons(run) {
    const physicalSignature = currentEditionEvents(run)
      .map((event) => `${event.eventId}:${physicalReadinessKey(event)}`)
      .join("|");
    const events = currentEditionEvents(run);
    const physicalSummary = physicalAvailabilitySummary(events);
    renderPhysicalReadiness(events);
    const note = !run
      ? "No run is underway. Tap an assigned event tile to flash its physical button; press a physical spoke to highlight its matching tile."
      : isBonusRunning(run)
        ? `${bonusName(run)}: the lit button's tile is highlighted. Tap it if the player presses that button and it doesn't register; other tiles don't count.`
      : run.status === "active"
        ? "Times count down from the run limit. Virtual presses remain available even when physical hardware is unassigned or unverified. Press once to start an event and again to finish it."
        : run.status === "countdown"
          ? "The fixed countdown is in progress; events unlock at Go whether or not audio plays."
        : run.status === "paused"
          ? "Resume the run before recording event presses."
        : run.status === "armed"
            ? "Start the run to enable event presses."
            : "No run is underway. Tap an assigned event tile to flash its physical button; press a physical spoke to highlight its matching tile.";
    ui.virtualNote.textContent = `${note} ${physicalSummary.text}`;
    ui.virtualNote.title = ui.virtualNote.textContent;
    const draftSignature = run
      ? Array.from(state.drafts.get(run.id)?.entries() || [])
        .map(([eventId, draft]) => `${eventId}:${Array.from(draft.touched).map((field) => `${field}=${draft[field]}`).join(",")}`)
        .join("|")
      : "";
    const key = `${run ? `${run.id}:${run.revision}:${run.status}:${tableTotal(run)}:${draftSignature}` : `no-run:${eventRosterSignature()}`}:${state.busy}:${state.tileUndoArmedEventId}:${physicalSignature}:${state.master?.connected}:${state.master?.mode}:${state.masterBusy}:${state.buttonHighlightDeviceId}:${state.buttonHighlightUntil}`;
    if (key === state.virtualKey) return;
    ui.virtualButtons.replaceChildren();
    const canPress = Boolean(run && run.status === "active" && !state.busy);
    const identificationMode = !run || !["armed", "countdown", "active", "paused"].includes(run.status);
    const bonusRunning = isBonusRunning(run);
    events.forEach((event, index) => {
      const button = make("button", "virtual-button");
      button.type = "button";
      const configured = physicalConfiguration(event);
      const deviceId = setupTools.hardwareId(configured.assignmentValue || configured.deviceId || event.deviceId || "");
      const canIdentify = Boolean(deviceId && state.master?.connected && state.master?.mode === "IDLE" && !state.masterBusy);
      button.dataset.eventId = event.eventId;
      button.dataset.deviceId = deviceId || "";
      button.disabled = state.busy || (identificationMode ? !canIdentify : !canPress || (event.status === "completed" && !bonusRunning));
      if (identificationMode) button.classList.add("is-identification-mode");
      if (run?.status === "active" && event.status === "active") button.classList.add("is-started");
      if (deviceId && deviceId === state.buttonHighlightDeviceId && Date.now() < state.buttonHighlightUntil) button.classList.add("is-physical-press");
      const actionLabel = event.status === "active"
        ? (run?.status === "active" ? "finish event" : "resume the run before finishing")
        : event.status === "completed" ? "complete" : "start event";
      const readiness = physicalReadiness(event);
      const status = virtualDeviceStatus(readiness);
      const useVirtual = readiness.key !== "responding" && readiness.key !== "unassigned";
      button.classList.add(`device-${status.key}`);
      let actionHint = "Start";
      if (identificationMode) {
        actionHint = !deviceId ? "No physical button assigned"
          : !state.master?.connected ? "Connect the master to identify"
            : state.master?.mode !== "IDLE" ? "Master must be in Garage Games idle mode"
              : "Identify physical button";
      } else if (event.status === "active" && run?.status === "active" && event.type === "keypad") {
        const progress = keypadProgress(run, event);
        actionHint = progress.required > 1
          ? `Code ${progress.solved + 1} of ${progress.required} · tap to credit it`
          : "Waiting for code · tap to override";
      } else if (event.status === "active" && run?.status === "active") {
        const remainingMs = Math.max(0, runDurationSeconds(run) * 1000 - currentElapsedMs(run));
        actionHint = `Stop · ${formatSeconds(remainingMs)} left`;
      } else if (event.status === "active") {
        actionHint = "Started · run not active";
      } else if (event.status === "completed") {
        actionHint = `Done · ${formatDuration(event.finishElapsedMs - event.startElapsedMs)}`;
      }
      if (!identificationMode && useVirtual && event.status !== "completed") actionHint = `Use virtual · ${actionHint}`;
      if (bonusRunning) actionHint = bonusName(run);
      button.setAttribute("aria-label", identificationMode
        ? `${event.name}: ${actionHint}.`
        : `${event.name}: physical button ${status.label}. ${actionHint}. Press to ${actionLabel}.`);
      button.title = identificationMode ? `${event.name} · ${actionHint}` : `${event.name} · ${status.label} · ${actionHint}`;
      const name = make("strong", "virtual-button-name");
      name.append(make("span", "virtual-button-number", String(index + 1).padStart(2, "0")), make("span", "", event.name));
      const stateLabel = make("small", "virtual-device-status", status.label);
      const action = make("small", "virtual-action-hint", actionHint);
      const metadata = make("span", "virtual-button-meta");
      metadata.append(stateLabel, action);
      button.append(name);
      const unsaved = Boolean(run && state.drafts.get(run.id)?.get(event.eventId)?.touched.size);
      // Every tile carries a result line, even before its event starts, so tiles keep one
      // height and a start press never shifts the rows below.
      button.append(virtualTileResult(run, event, unsaved));
      if (event.status === "completed") button.classList.add("is-complete");
      if (unsaved) button.classList.add("is-unsaved");
      button.append(metadata);
      button.addEventListener("click", () => identificationMode
        ? identifyPhysicalButton(event, deviceId)
        : pressEvent(run, event));
      const tile = make("div", "virtual-tile");
      tile.appendChild(button);
      if (canUndoEventOnTile(run, event)) {
        tile.classList.add("has-undo");
        tile.appendChild(tileUndoButton(event));
      }
      ui.virtualButtons.appendChild(tile);
    });
    state.virtualKey = key;
    renderBonusLive();
  }

  // ---- Bonus speed round (live) ----
  const BONUS_END_TEXT = { miss: "missed a button", timeout: "time ran out", operator: "ended by the scorekeeper" };

  function bonusGamePoints(run) {
    const bonus = run?.bonusGame;
    if (!bonus || String(bonus.phase).toLowerCase() !== "ended") return 0;
    return Number(bonus.scoreOverride ?? bonus.awardedPoints ?? 0);
  }

  function bonusBannerText(run) {
    const bonus = run?.bonusGame;
    if (!bonus) return "";
    const hits = Number(bonus.hits || 0);
    const hitText = `${hits} hit${hits === 1 ? "" : "s"}`;
    if (String(bonus.phase).toLowerCase() !== "ended") {
      return run.status === "paused"
        ? `${bonusName(run)} paused · ${hitText} so far. Resume when the competitor is ready.`
        : `${bonusName(run)} in progress · ${hitText} so far · ${Number(bonus.pointsPerPress || 0)} points each.`;
    }
    const recorded = run.isRecorded ? "recorded" : "not recorded";
    return `${bonusName(run)} over (${BONUS_END_TEXT[bonus.endReason] || "ended"}) · ${hitText} · +${bonusGamePoints(run)} points · ${recorded}.`;
  }

  function isBonusRunning(run) {
    return Boolean(run?.bonusGame && String(run.bonusGame.phase).toLowerCase() !== "ended" &&
      ["active", "paused"].includes(run.status));
  }

  // The full snapshot refreshes every 2 s; during the bonus round the light scoreboard feed is
  // polled several times a second so the lit tile and its time stay current.
  async function pollBonusLive() {
    const run = state.snapshot?.currentRun;
    if (!isBonusRunning(run) || state.bonusPollInFlight) {
      if (!isBonusRunning(run) && state.bonusLive) {
        state.bonusLive = null;
        renderBonusLive();
      }
      return;
    }
    state.bonusPollInFlight = true;
    try {
      const board = await request("/api/scoreboard", { cache: "no-store" });
      const bonus = board?.currentRun?.bonusGame || null;
      state.bonusLive = bonus ? { ...bonus, receivedAt: performance.now(), running: board.currentRun.status === "active" } : null;
      renderBonusLive();
      // The round ended (a miss or the time ran out): fetch the finished run right away.
      if (!bonus || String(bonus.phase).toLowerCase() === "ended") void loadSnapshot(true);
    } catch {
      // The next poll retries; the 2-second snapshot also keeps the page current.
    } finally {
      state.bonusPollInFlight = false;
    }
  }

  function renderBonusLive() {
    const live = state.bonusLive;
    const phase = String(live?.phase || "").toLowerCase();
    ui.virtualButtons.querySelectorAll(".virtual-button").forEach((button) => {
      const isTarget = phase === "target" && button.dataset.eventId === live.targetEventId;
      button.classList.toggle("is-bonus-target", isTarget);
      const hint = button.querySelector(".virtual-action-hint");
      if (!hint || !isBonusRunning(state.snapshot?.currentRun)) return;
      if (phase === "intro") hint.textContent = `${bonusName(state.snapshot?.currentRun)} · get ready`;
      else if (isTarget) {
        const remaining = Math.max(0, Number(live.targetRemainingMs || 0) - (live.running ? performance.now() - live.receivedAt : 0));
        hint.textContent = `LIT · ${(remaining / 1000).toFixed(1)} s · ${live.hits} hit${live.hits === 1 ? "" : "s"}`;
      } else hint.textContent = bonusName(state.snapshot?.currentRun);
    });
  }

  function canUndoEventOnTile(run, event) {
    return Boolean(run && !run.isRecorded && !run.bonusGame && ["active", "paused", "finished", "timedOut"].includes(run.status) &&
      isUndoableEventType(event) &&
      (event.status === "active" || event.status === "completed"));
  }

  // Undoes only this event's latest press. The first tap arms it ("Undo?") and a second
  // tap within a few seconds confirms, so a stray click never changes a live run.
  function tileUndoButton(event) {
    const armed = state.tileUndoArmedEventId === event.eventId;
    const stepLabel = event.status === "completed" ? "finish" : "start";
    const undo = make("button", `virtual-tile-undo${armed ? " is-armed" : ""}`, armed ? "Undo?" : "↶");
    undo.type = "button";
    undo.disabled = state.busy;
    undo.title = armed
      ? `Tap again to undo the ${stepLabel} of ${event.name}`
      : `Undo the ${stepLabel} of ${event.name} only`;
    undo.setAttribute("aria-label", undo.title);
    undo.addEventListener("click", () => {
      if (state.tileUndoArmedEventId !== event.eventId) {
        state.tileUndoArmedEventId = event.eventId;
        window.clearTimeout(state.tileUndoTimer);
        state.tileUndoTimer = window.setTimeout(() => {
          state.tileUndoArmedEventId = null;
          renderVirtualButtons(state.snapshot?.currentRun || null);
        }, 3500);
        renderVirtualButtons(state.snapshot?.currentRun || null);
        return;
      }
      window.clearTimeout(state.tileUndoTimer);
      state.tileUndoArmedEventId = null;
      performAction(
        () => request(`/api/run/events/${encodeURIComponent(event.eventId)}/undo-press`, { method: "POST" }),
        `${event.name}: ${stepLabel} undone. Other events were not changed.`
      );
    });
    return undo;
  }

  // Each tile carries its event's result so times and points are readable without
  // opening the scorecard: points plus start → stop (M:SS remaining) and duration when
  // finished, a live running timer while in progress.
  function virtualTileResult(run, event, unsaved = false) {
    const result = make("span", "virtual-tile-result");
    if (run && unsaved) {
      // Unsaved edits show here so the operator sees them, clearly marked as not yet counted.
      const points = make("span", "virtual-tile-points");
      points.append(String(displayedScore(run, event)), make("small", "", " pts"));
      result.append(points, make("span", "virtual-tile-times virtual-tile-unsaved", "Unsaved"));
      result.classList.add("is-unsaved");
      return result;
    }
    if (!run || event.status === "pending") {
      result.append(make("span", "virtual-tile-points is-empty", "—"), make("span", "virtual-tile-times", "Not started"));
      result.classList.add("is-pending");
      return result;
    }
    if (event.status === "completed") {
      const points = make("span", "virtual-tile-points");
      points.append(String(displayedScore(run, event)), make("small", "", event.scoreOverride != null ? " pts · manual" : " pts"));
      result.append(points, make("span", "virtual-tile-times",
        `${runEventTimestamp(run, event.startElapsedMs)} → ${runEventTimestamp(run, event.finishElapsedMs)} · ${formatDuration(event.finishElapsedMs - event.startElapsedMs)}`));
      result.classList.add("is-complete");
    } else if (event.status === "active" && Number.isFinite(event.startElapsedMs)) {
      const live = make("span", "virtual-tile-points virtual-tile-live", formatDuration(Math.max(0, currentElapsedMs(run) - event.startElapsedMs)));
      live.dataset.liveStartMs = String(event.startElapsedMs);
      result.append(live, make("span", "virtual-tile-times", `Started ${runEventTimestamp(run, event.startElapsedMs)} · running`));
      result.classList.add("is-running");
    }
    return result;
  }

  function physicalReadinessKey(event) {
    return physicalReadiness(event).key;
  }

  function physicalReadiness(event) {
    const configured = physicalConfiguration(event);
    if (!setupTools.hardwareId(configured.assignmentValue || configured.deviceId || "")) {
      return { key: "unassigned", label: "Unassigned" };
    }
    if (state.armScanPending) return { key: "unverified", label: "Unverified" };
    return setupTools.currentReadiness(configured, {
      snapshot: state.snapshot,
      scan: state.setupScan,
      scanFresh: state.setupScanFresh,
      scanIsNewer: state.setupScanAt > state.receivedAt,
      masterConnected: state.master?.connected,
      masterUnavailable: Boolean(state.masterError),
      dirty: state.setupDirty && Boolean(setupEvent(event?.eventId)),
      scanLoading: state.setupScanLoading
    });
  }

  function physicalConfiguration(event) {
    const activeRun = state.snapshot?.currentRun;
    const runEvent = activeRun?.events?.find((item) => item.eventId === event?.eventId);
    if (runEvent && masterActions.isRunDurationLocked(activeRun)) {
      return { eventId: runEvent.eventId, assignmentValue: runEvent.deviceId, deviceId: runEvent.deviceId };
    }
    return setupEvent(event?.eventId) || {
      eventId: event?.eventId || "",
      assignmentValue: event?.deviceId || ""
    };
  }

  function virtualDeviceStatus(readiness) {
    if (readiness.key === "unassigned") return { key: "virtual", label: "Virtual" };
    if (readiness.key === "responding") return { key: "responding", label: "Responding" };
    if (["not-responding", "not-seen"].includes(readiness.key)) return { key: "not-responding", label: "Not responding" };
    return { key: "unverified", label: "Unverified" };
  }

  function physicalAvailabilitySummary(events) {
    const assigned = events.filter((event) => {
      const configured = physicalConfiguration(event);
      return setupTools.hardwareId(configured.assignmentValue || configured.deviceId || event.deviceId || "");
    });
    if (!assigned.length) {
      return { text: "No assigned physical buttons · virtual event buttons remain available.", entries: [] };
    }

    const entries = assigned.map((event) => {
      const configured = physicalConfiguration(event);
      const deviceId = setupTools.hardwareId(configured.assignmentValue || configured.deviceId || event.deviceId || "");
      return { event, deviceId, status: virtualDeviceStatus(physicalReadiness(event)) };
    });
    const responding = entries.filter((item) => item.status.key === "responding").length;
    const missing = entries.filter((item) => item.status.key === "not-responding").length;
    const unverified = entries.filter((item) => item.status.key === "unverified").length;
    const checking = state.armScanPending || state.setupScanLoading;
    const prefix = checking
      ? "Checking assigned physical buttons…"
      : `${responding} responding · ${missing} not responding · ${unverified} unverified`;
    const checkedAt = state.snapshot?.deviceScanCheckedAt;
    const checkedLabel = checkedAt ? ` Last checked ${formatLocalDateTime(checkedAt)} (point-in-time, not live).` : " No completed scan is recorded yet.";
    return { text: `${prefix}.${checkedLabel} Virtual event buttons remain available.`, entries, checkedAt };
  }

  // The hardware section is collapsed by default, so its summary line carries the
  // essentials and is highlighted when the master or a button needs attention.
  function renderHardwarePanelSummary() {
    const master = state.master;
    const entries = state.hardwareEntries || [];
    const responding = entries.filter((item) => item.status.key === "responding").length;
    const missing = entries.filter((item) => item.status.key === "not-responding").length;
    const masterText = state.masterError
      ? "Master status unavailable"
      : master?.connected
        ? `Master connected · ${master.mode ? master.mode.toUpperCase() : "waiting for handshake"}`
        : "Master not connected";
    const buttonsText = entries.length
      ? `${responding} of ${entries.length} buttons responding${missing ? ` · ${missing} not responding` : ""}`
      : "No physical buttons assigned";
    ui.hardwarePanelSummary.textContent = `${masterText} · ${buttonsText}`;
    const attention = Boolean(state.masterError || missing || masterActions.isSpeedMode(master) || masterActions.handshakeGuidance(master));
    ui.hardwarePanel.classList.toggle("needs-attention", attention);
  }

  function renderPhysicalReadiness(events) {
    const summary = physicalAvailabilitySummary(events);
    state.hardwareEntries = summary.entries;
    renderHardwarePanelSummary();
    ui.physicalReadinessSummary.textContent = summary.text;
    ui.physicalReadinessList.replaceChildren();
    if (!summary.entries.length) {
      ui.physicalReadinessList.appendChild(make("li", "physical-readiness-empty", summary.text));
      return;
    }

    summary.entries.forEach(({ event, deviceId, status }) => {
      const item = make("li", `physical-readiness-item status-${status.key}`);
      const identity = make("span", "physical-readiness-identity");
      identity.append(
        make("strong", "physical-readiness-event", event.name),
        make("small", "physical-readiness-device", `Button ${deviceId}`),
        make("small", "physical-readiness-checked", summary.checkedAt ? `Checked ${formatLocalDateTime(summary.checkedAt)}` : "Not checked yet")
      );
      item.append(identity, make("strong", "physical-readiness-label", status.label));
      ui.physicalReadinessList.appendChild(item);
    });
  }

  function isPhysicalEventResponding(event) {
    return physicalReadinessKey(event) === "responding";
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
      appendEmptyTableRow(ui.overallLeaderboardBody, 4, "No visible results for this edition yet.");
      return;
    }
    const fragment = document.createDocumentFragment();
    rows.forEach((item) => {
      const row = make("tr");
      row.append(
        make("td", "leaderboard-rank", String(item.rank)),
        make("td", "leaderboard-player", item.displayName || item.competitorName),
        make("td", `leaderboard-category category-${String(item.category).toLowerCase()}`, categoryLabel(item.category)),
        make("td", "leaderboard-points", String(item.points))
      );
      fragment.appendChild(row);
    });
    ui.overallLeaderboardBody.replaceChildren(fragment);
  }

  // A saved run's total as recorded: event points (or overrides), the bonus round, and the
  // general run bonus.
  function recordedRunTotal(run) {
    return scorecardEvents(run).reduce((sum, event) =>
      sum + Number(event.scoreOverride ?? event.score ?? 0), Number(run?.bonusPointsOverride || 0));
  }

  // Every recorded result a player has, grouped by player: official, playoffs, and each
  // exhibition. The top official result is shown first.
  function renderLeaderboardPlayerOptions(officialRuns) {
    const history = state.snapshot?.history || [];
    const runsById = new Map(history.map((run) => [run.id, run]));
    const groups = leaderboardTools.scorecardRunChoices(history, state.snapshot?.competitors || [], state.snapshot?.editionId);
    const optionText = (choice) => {
      const run = runsById.get(choice.runId);
      const when = choice.label.match(/ \d+$/) ? ` · ${shortDate(run.recordedAt || run.finishedAt || run.createdAt)}` : "";
      return `${choice.label} · ${recordedRunTotal(run)} pts${when}`;
    };
    const signature = JSON.stringify(groups.map((group) => [group.name, group.runs.map((choice) => [choice.runId, optionText(choice)])]));
    if (signature !== state.leaderboardCompetitorSignature) {
      ui.leaderboardPlayerSelect.replaceChildren(new Option("Select a player's run…", ""));
      groups.forEach((group) => {
        const optgroup = document.createElement("optgroup");
        optgroup.label = group.name;
        group.runs.forEach((choice) => optgroup.appendChild(new Option(`${group.name} · ${optionText(choice)}`, choice.runId)));
        ui.leaderboardPlayerSelect.appendChild(optgroup);
      });
      state.leaderboardCompetitorSignature = signature;
    }
    const validRuns = new Set(groups.flatMap((group) => group.runs.map((choice) => choice.runId)));
    if (!validRuns.has(state.selectedLeaderboardRunId)) state.selectedLeaderboardRunId = "";
    if (!state.selectedLeaderboardRunId && !state.leaderboardSelectionInitialized) {
      state.selectedLeaderboardRunId = officialRuns.find(({ run }) => validRuns.has(run.id))?.run.id ||
        groups[0]?.runs[0]?.runId || "";
      state.leaderboardSelectionInitialized = true;
    }
    ui.leaderboardPlayerSelect.value = state.selectedLeaderboardRunId;
  }

  function runEventTimestamp(run, elapsedMs) {
    if (!Number.isFinite(elapsedMs)) return "—";
    const durationSeconds = runDurationSeconds(run);
    const remaining = scorekeeperTime.remainingSecondsFromElapsedMs(elapsedMs, durationSeconds);
    return remaining === null ? "—" : formatSeconds(remaining * 1000);
  }

  function renderSelectedPlayerLeaderboard() {
    const selectedId = state.selectedLeaderboardRunId;
    const run = selectedId ? (state.snapshot?.history || []).find((item) => item.id === selectedId) : null;
    if (!run) {
      ui.playerLeaderboardCaption.textContent = "Select a player's run to review their event times and points.";
      ui.playerLeaderboardTotal.textContent = "—";
      appendEmptyTableRow(ui.playerLeaderboardBody, 5, "Select a player's run to see its scorecard.");
      return;
    }

    const total = recordedRunTotal(run);
    ui.playerLeaderboardCaption.textContent = `${competitorName(run.competitorId)} · ${categoryLabel(run.category)} result · ${total} total points · ${shortDate(run.recordedAt || run.finishedAt || run.createdAt)}`;
    ui.playerLeaderboardTotal.textContent = String(total);
    const eventResults = new Map(scorecardEvents(run).map((event) => [event.eventId, event]));
    const configuredEvents = leaderboardEventDefinitions(state.snapshot);
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
      ["Rank", "Player", "Type", "Time", "Points"].forEach((label) => {
        const cell = make("th", "", label);
        cell.scope = "col";
        headerRow.appendChild(cell);
      });
      thead.appendChild(headerRow);
      const tbody = document.createElement("tbody");
      if (!board.rows.length) {
        appendEmptyTableRow(tbody, 5, "No visible results for this event yet.");
      } else {
        board.rows.forEach((item) => {
          const row = make("tr");
          if (item.status === "dnf") row.classList.add("leaderboard-dnf-row");
          const display = leaderboardTools.eventLeaderboardDisplayRow(item);
          row.append(
            make("td", "leaderboard-rank", display.rank),
            make("td", "leaderboard-player", item.displayName || item.competitorName),
            make("td", `leaderboard-category category-${String(item.category).toLowerCase()}`, categoryLabel(item.category)),
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
    const leaderboardRows = snapshot.leaderboard || [];
    const officialRuns = officialLeaderboardRuns();
    ui.showExhibitionsOnLeaderboard.checked = state.exhibitionPreferenceDraft ?? Boolean(snapshot.showExhibitionsOnLeaderboard);
    renderLeaderboardPlayerOptions(officialRuns);

    const overallKey = JSON.stringify(leaderboardRows);
    if (overallKey !== state.overallLeaderboardKey) {
      renderOverallLeaderboard(leaderboardRows);
      state.overallLeaderboardKey = overallKey;
    }

    const selectedRun = (snapshot.history || []).find((run) => run.id === state.selectedLeaderboardRunId);
    const playerKey = `${state.selectedLeaderboardRunId}:${JSON.stringify(selectedRun ? [selectedRun.revision, selectedRun.events, selectedRun.bonusGame, selectedRun.bonusPointsOverride] : null)}:${JSON.stringify(snapshot.events)}`;
    if (playerKey !== state.playerLeaderboardKey) {
      renderSelectedPlayerLeaderboard();
      state.playerLeaderboardKey = playerKey;
    }

    // The bonus round ranks like an event, from each run's bonus result.
    const boards = leaderboardTools.buildEventLeaderboards(
      leaderboardEventDefinitions(snapshot),
      leaderboardRows,
      (snapshot.history || []).map((run) => ({ ...run, events: scorecardEvents(run) })));
    const eventKey = JSON.stringify(boards);
    if (eventKey !== state.eventLeaderboardsKey) {
      renderEventLeaderboards(boards);
      state.eventLeaderboardsKey = eventKey;
    }
  }

  function renderDeletedRuns() {
    const deleted = state.snapshot?.deletedRuns || [];
    ui.deletedRunsPanel.hidden = deleted.length === 0;
    ui.deletedRunsCount.textContent = String(deleted.length);
    const signature = `${state.busy}:${deleted.map((run) => `${run.id}:${run.revision}`).join("|")}`;
    if (signature === state.deletedRunsSignature) return;
    state.deletedRunsSignature = signature;
    ui.deletedRunsList.replaceChildren();
    deleted.forEach((run) => {
      const item = make("li", "deleted-run-row");
      const details = make("span", "deleted-run-details");
      details.append(
        make("strong", "", `${competitorName(run.competitorId)} · ${categoryLabel(run.category)}`),
        make("span", "", `${tableTotal(run)} pts · run ${shortDate(run.createdAt)} · deleted ${shortDate(run.deletedAt)}`)
      );
      const restore = make("button", "button button-quiet", "Restore");
      restore.type = "button";
      restore.disabled = state.busy;
      restore.addEventListener("click", () => performAction(
        () => request(`/api/runs/${encodeURIComponent(run.id)}/restore`, { method: "POST" }),
        `${competitorName(run.competitorId)}'s ${categoryLabel(run.category)} run was restored.`
      ));
      item.append(details, restore);
      ui.deletedRunsList.appendChild(item);
    });
  }

  async function deleteHistoricalRun() {
    const run = (state.snapshot?.history || []).find((item) => item.id === state.selectedHistoryId);
    if (!run) return false;
    const replaced = run.supersedesRunId && !run.supersededByRunId && run.isRecorded
      ? "\n\nThis run replaced an earlier result; that earlier result will count again."
      : "";
    const confirmed = window.confirm(
      `Delete ${competitorName(run.competitorId)}'s ${categoryLabel(run.category)} run (${tableTotal(run)} pts)?\n\n` +
      `It will be removed from run history, the leaderboards, and the TV scoreboard. It stays in Deleted runs at the bottom of this tab so it can be restored.${replaced}`
    );
    if (!confirmed) return false;
    await request(`/api/runs/${encodeURIComponent(run.id)}/delete`, {
      method: "POST",
      body: JSON.stringify({ expectedRevision: run.revision })
    });
    state.drafts.delete(run.id);
    state.bonusDrafts.delete(run.id);
    state.selectedHistoryId = null;
    state.historySignature = "";
    state.historyTableKey = null;
  }

  // Search matches player names the way duplicate detection does (ignoring case and
  // extra spaces); the type and result filters narrow the list further.
  function historyFilter() {
    return {
      query: normalizedCompetitorName(ui.historySearch.value).toLowerCase(),
      category: ui.historyCategoryFilter.value,
      status: ui.historyStatusFilter.value
    };
  }

  function filterHistory(history, filter) {
    return history.filter((run) =>
      (!filter.query || normalizedCompetitorName(competitorName(run.competitorId)).toLowerCase().includes(filter.query)) &&
      (!filter.category || run.category === filter.category) &&
      (!filter.status || (filter.status === "recorded") === Boolean(run.isRecorded)));
  }

  function renderHistory() {
    renderDeletedRuns();
    const allHistory = state.snapshot?.history || [];
    const filter = historyFilter();
    const filtering = Boolean(filter.query || filter.category || filter.status);
    const history = filterHistory(allHistory, filter);
    ui.historyCount.textContent = filtering
      ? `${history.length} of ${allHistory.length} ${allHistory.length === 1 ? "run" : "runs"}`
      : `${allHistory.length} ${allHistory.length === 1 ? "run" : "runs"}`;
    ui.historyClearFilters.hidden = !filtering;
    if (!history.some((run) => run.id === state.selectedHistoryId)) {
      state.selectedHistoryId = history[0]?.id || null;
    }
    const signature = `${filter.query}|${filter.category}|${filter.status}|` +
      history.map((run) => `${run.id}:${run.revision}:${run.status}`).join("|");
    if (signature !== state.historySignature) {
      ui.historyList.replaceChildren();
      if (!history.length) {
        ui.historyList.appendChild(make("p", "empty-state", filtering
          ? "No saved runs match these filters."
          : "Saved runs will appear here."));
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
      ui.historyDelete.disabled = true;
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
    // The run in progress is discarded from the scorekeeping tab instead.
    ui.historyDelete.disabled = state.busy || isLiveLock(selected);
    ui.historyDelete.title = isLiveLock(selected) ? "The run in progress can't be deleted; use Discard on the scorekeeping tab." : "";
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
    syncRunDurationControl(run);
    const durationSeconds = selectedRunDurationSeconds();
    ui.competitor.disabled = state.busy || locked;
    ui.category.disabled = state.busy || locked;
    // An unrecorded timeout must be recorded or discarded before anyone else goes.
    const waitingOnTimeout = runActions.blocksNextRun(run);
    const waitingTitle = waitingOnTimeout
      ? `Record or discard ${competitorName(run.competitorId)}'s timed-out run first.`
      : "";
    ui.start.disabled = state.busy || state.masterBusy || waitingOnTimeout ||
      !masterActions.canStartVirtual(state.master, run, selectedId) || (!locked && durationSeconds === null);
    ui.start.title = waitingTitle;
    ui.start.textContent = run?.status === "armed"
      ? "Start armed run"
      : `Start ${masterActions.formatRunDuration(durationSeconds || 300)} run`;
    // Start controls live beside the event buttons and only show while a run can start.
    ui.start.hidden = locked && run?.status !== "armed";
    // "Up Next" only changes the TV between runs; it shows once the last run is out of the way.
    const primed = snapshot?.primed || null;
    const primedMatches = Boolean(primed && primed.competitorId === selectedId &&
      primed.category === ui.category.value && primed.durationLimitSeconds === durationSeconds);
    ui.prime.hidden = locked;
    ui.prime.disabled = state.busy || waitingOnTimeout || !selectedId || durationSeconds === null || primedMatches;
    ui.prime.textContent = primedMatches ? "Up Next · on the TV" : "Up Next";
    ui.prime.title = waitingTitle || "Show the selected competitor on the TV as up next, with the full clock and no scores. Nothing starts.";
    ui.armPhysical.hidden = ui.start.hidden || !(state.master?.connected || run?.status === "armed");
    ui.armPhysical.disabled = state.busy || state.masterBusy || waitingOnTimeout ||
      !masterActions.canArmPhysical(state.master, run, selectedId) || (!locked && durationSeconds === null);
    ui.armPhysical.title = waitingTitle;
    ui.armPhysical.textContent = run?.status === "armed"
      ? "Waiting for physical Start"
      : `Arm ${masterActions.formatRunDuration(durationSeconds || 300)} run`;
    ui.pause.disabled = state.busy || !run || !["active", "paused"].includes(run.status);
    ui.pause.textContent = run?.status === "paused" ? "Resume" : "Pause";
    const undoable = undoableEventPress(run);
    ui.undoPress.disabled = state.busy || !undoable;
    const undoneKeypadCode = undoable && undoable.message.type !== "event-press";
    ui.undoDetail.textContent = !undoable
      ? "No event press to undo."
      : undoneKeypadCode
        ? `Will undo: ${undoable.event.name} · code ${keypadProgress(run, undoable.event).solved}`
        : `Will undo: ${undoable.event.name} · ${String(undoable.event.status).toLowerCase() === "completed" ? "finish" : "start"} press`;
    ui.undoDetail.title = ui.undoDetail.textContent;
    // An armed run hasn't started, so there is nothing to finish; it can only be discarded.
    ui.finish.disabled = state.busy || !run || !["active", "paused"].includes(run.status);
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
    renderUnsavedState();
    renderMasterControls();
    renderSetupControls();
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
    const liveRun = masterActions.isRunDurationLocked(run) ? run : null;
    const idleDuration = selectedRunDurationSeconds();
    const limitMs = (liveRun ? runDurationSeconds(liveRun) : idleDuration || 300) * 1000;
    const elapsedMs = liveRun ? currentElapsedMs(liveRun) : 0;
    const remainingMs = Math.max(0, limitMs - elapsedMs);
    const totalSeconds = Math.ceil(remainingMs / 1000);
    ui.countdown.textContent = !liveRun && idleDuration === null ? "—" : scorekeeperTime.formatClockMs(totalSeconds * 1000);
    ui.countdown.classList.toggle("is-expired", Boolean(liveRun && remainingMs === 0));
    if (liveRun?.status === "active" && remainingMs === 0 && state.timeoutRefreshRunId !== liveRun.id) {
      state.timeoutRefreshRunId = liveRun.id;
      loadSnapshot(true);
    }
    if (run) {
      ui.runTotal.textContent = String(tableTotal(run));
      const activeRows = ui.currentBody.querySelectorAll("tr");
      activeRows.forEach((row) => rowTiming(row, run));
      const elapsedNow = currentElapsedMs(run);
      ui.virtualButtons.querySelectorAll("[data-live-start-ms]").forEach((live) => {
        live.textContent = formatDuration(Math.max(0, elapsedNow - Number(live.dataset.liveStartMs)));
      });
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
    // A message still showing follows to the slot for the tab now in view.
    const showing = ui.alert.firstElementChild || ui.tabAlert.firstElementChild;
    if (showing && showing.parentElement !== alertSlot()) alertSlot().appendChild(showing);
    if (moveFocus) ui.tabs[index].focus();
  }

  async function recordCurrentRun() {
    let run = state.snapshot?.currentRun;
    if (!run) throw new Error("There is no run to record.");
    if (hasDrafts(run.id) || hasBonusDraft(run.id)) {
      // Save first so the recorded result is exactly what the scorecard shows.
      await saveRunDrafts(run);
      await loadSnapshot(true);
      if (state.snapshot?.currentRun?.id === run.id) run = state.snapshot.currentRun;
    }
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
      // An action returns false when the operator cancelled it at a confirmation.
      const result = await action();
      if (successMessage && result !== false) showAlert(successMessage, "success");
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

  // Returns null when no redo is involved, true to arm an official redo, or false if
  // the operator declined to replace the competitor's recorded official result.
  function confirmOfficialRedo(competitorId, category) {
    if (category !== "official") return null;
    const existing = masterActions.findRecordedOfficial(state.snapshot, competitorId);
    if (!existing) return null;
    const points = (state.snapshot?.leaderboard || []).find((row) => row.runId === existing.id)?.points;
    const name = competitorName(competitorId);
    return window.confirm(
      `${name} already has an official result${points == null ? "" : ` (${points} pts)`}. Start an official redo?\n\n` +
      "The redo replaces that result when it is recorded, even if it scores lower. If the redo is discarded, the original result stays."
    );
  }

  async function startRun() {
    const current = state.snapshot?.currentRun;
    const redo = current?.status === "armed" ? null : confirmOfficialRedo(ui.competitor.value, ui.category.value);
    if (redo === false) return false;
    const started = await masterActions.startVirtually(request, {
      master: state.master,
      currentRun: current,
      competitorId: ui.competitor.value,
      category: ui.category.value,
      durationLimitSeconds: selectedRunDurationSeconds(),
      replaceExistingOfficial: redo === true
    }, () => loadSnapshot(true));
    state.discardedRunNotice = null;
    if (started.run) countdownCoordinator.observe(started.run);
    else await countdownCoordinator.poll();
    await removeMatchingQueueEntryAfterStart(started.competitorId, started.category);
  }

  async function primeNextCompetitor() {
    await request("/api/run/prime", {
      method: "POST",
      body: JSON.stringify({
        competitorId: ui.competitor.value,
        category: ui.category.value,
        durationLimitSeconds: selectedRunDurationSeconds()
      })
    });
  }

  async function armPhysicalRun() {
    const redo = confirmOfficialRedo(ui.competitor.value, ui.category.value);
    if (redo === false) return false;
    state.armScanRequestVersion += 1;
    state.armScanPending = true;
    state.armScanAfterSnapshotRequestId = null;
    state.setupScanFresh = false;
    state.setupScanAt = 0;
    state.virtualKey = null;
    if (state.snapshot) renderVirtualButtons(state.snapshot.currentRun);
    try {
      await masterActions.armForPhysicalStart(request, {
        master: state.master,
        currentRun: state.snapshot?.currentRun || null,
        competitorId: ui.competitor.value,
        category: ui.category.value,
        durationLimitSeconds: selectedRunDurationSeconds(),
        replaceExistingOfficial: redo === true
      });
      state.discardedRunNotice = null;
    } finally {
      state.armScanAfterSnapshotRequestId = state.snapshotRequestId;
      await loadSnapshot(true);
    }
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
    if (isBonusRunning(run)) {
      // A bonus tap counts only on the lit button; report what the app decided.
      await performAction(async () => {
        const result = await request(`/api/runs/${encodeURIComponent(run.id)}/events/${encodeURIComponent(event.eventId)}/press`, { method: "POST" });
        if (String(result?.disposition || "").toLowerCase() !== "accepted") throw new Error(result?.reason || "That bonus press did not count.");
      }, `${bonusName(run)} hit on ${event.name}.`);
      void pollBonusLive();
      return;
    }
    await performAction(
      () => request(`/api/runs/${encodeURIComponent(run.id)}/events/${encodeURIComponent(event.eventId)}/press`, { method: "POST" }),
      event.status === "active"
        ? event.type === "keypad" ? `${event.name} finished by operator override.` : `${event.name} finished.`
        : `${event.name} started.`
    );
  }

  async function identifyPhysicalButton(event, deviceId) {
    if (!deviceId || state.busy || state.masterBusy) return;
    state.masterBusy = true;
    updateControls();
    try {
      state.master = await request("/api/master/identify", {
        method: "POST",
        body: JSON.stringify({ deviceId })
      });
      state.masterError = "";
      showAlert(`Identification flash requested for ${event.name} (${deviceId}).`, "success");
    } catch (error) {
      showAlert(error.message || "The physical button could not be identified.");
    } finally {
      state.masterBusy = false;
      await loadMaster(true);
      updateControls();
    }
  }

  function onClearEventClick(event) {
    const button = event.target.closest("button[data-clear-event]");
    if (!button || state.busy) return;
    const runId = button.dataset.runId;
    const eventId = button.dataset.eventId;
    const run = state.snapshot?.currentRun?.id === runId
      ? state.snapshot.currentRun
      : (state.snapshot?.history || []).find((item) => item.id === runId);
    const eventResult = run?.events?.find((item) => item.eventId === eventId);
    if (!run || !eventResult) return;
    if (!window.confirm(`Reset “${eventResult.name}” in ${competitorName(run.competitorId)}’s ${categoryLabel(run.category)} run? Its times, points (including manual points), and notes are cleared so it can be started again. Other events are not changed. This takes effect immediately and is kept in the run’s audit trail.\n\nTo take back just the last press on this event, use its ↶ on the event button instead.`)) return;
    performAction(
      async () => {
        await request(`/api/runs/${encodeURIComponent(runId)}/events/${encodeURIComponent(eventId)}/clear`, {
          method: "POST",
          body: JSON.stringify({ expectedRevision: Number(button.dataset.revision) })
        });
        const runDrafts = state.drafts.get(runId);
        if (runDrafts) {
          runDrafts.delete(eventId);
          if (runDrafts.size === 0) state.drafts.delete(runId);
        }
      },
      `${eventResult.name} cleared. It is available to start again.`
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
    if (row && run) {
      rowTiming(row, run);
      const draft = state.drafts.get(runId)?.get(eventId);
      if ((field === "start" || field === "finish" || field === "hits") && !draft?.touched.has("score")) {
        const eventResult = scorecardEvents(run).find((item) => item.eventId === eventId);
        const scoreInput = row.querySelector('input[data-field="score"]');
        if (eventResult && scoreInput) {
          const value = displayedValue(run, eventResult, "score");
          if (scoreInput.value !== value) scoreInput.value = value;
        }
      }
    }
    const isCurrentScorecard = Boolean(input.closest("#current-event-body"));
    if (run) updateTableTotal(isCurrentScorecard ? "current" : "history", run);
    setSaveStates();
    if (isCurrentScorecard) afterCurrentDraftChange(run);
  }

  function afterCurrentDraftChange(run) {
    state.currentSaveError = "";
    renderUnsavedState();
    if (run && state.snapshot?.currentRun?.id === run.id) renderVirtualButtons(run);
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
    if (input === ui.currentBonus) afterCurrentDraftChange(run);
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
              if (!Number.isInteger(score) || Math.abs(score) > MAXIMUM_MANUAL_POINTS) {
                const name = scorecardEvents(run).find((item) => item.eventId === eventId)?.name || "an event";
                throw new Error(`Points for ${name} must be a whole number (negative for a penalty), or blank for automatic points.`);
              }
              edit.scoreOverride = score;
            }
          } else if (field === "hits") {
            const hits = Number(value);
            if (value === "" || !Number.isInteger(hits) || hits < 0 || hits > 100_000) {
              throw new Error("Bonus round hits must be a whole number from 0 to 100,000.");
            }
            edit.hits = hits;
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
        if (!Number.isInteger(points) || Math.abs(points) > MAXIMUM_MANUAL_POINTS) {
          throw new Error("General run bonus must be a whole number (negative for a penalty), or blank for zero.");
        }
        request.bonusPointsOverride = points;
      }
    }
    return request;
  }

  // Saves a run's scorecard drafts through the run's own address, which works whether the
  // run is live, finished, or timed out (the server applies the edit to its latest state).
  async function saveRunDrafts(run) {
    try {
      const body = createEditRequest(run);
      await request(`/api/runs/${encodeURIComponent(run.id)}/edit`, { method: "PUT", body: JSON.stringify(body) });
    } catch (error) {
      // Keep the reason next to the unsaved-edits notice, not only in the transient alert.
      state.currentSaveError = error.message || "The scorecard edits could not be saved.";
      renderUnsavedState();
      throw error;
    }
    state.drafts.delete(run.id);
    state.bonusDrafts.delete(run.id);
    state.currentSaveError = "";
    state.currentTableKey = null;
    state.virtualKey = null;
  }

  async function saveCurrentEdits() {
    const run = state.snapshot?.currentRun;
    if (!run) return;
    await performAction(() => saveRunDrafts(run), "Scorecard edits saved. The TV and results now include them.");
  }

  function discardCurrentEdits() {
    const run = state.snapshot?.currentRun;
    if (!run || !(hasDrafts(run.id) || hasBonusDraft(run.id))) return;
    if (!window.confirm("Discard your unsaved scorecard edits for this run? The saved times and points are kept.")) return;
    state.drafts.delete(run.id);
    state.bonusDrafts.delete(run.id);
    state.currentSaveError = "";
    state.currentTableKey = null;
    state.virtualKey = null;
    updateControls();
    showAlert("Unsaved scorecard edits discarded.", "success");
  }

  // Unsaved scorecard edits change what this page shows but not the TV or results, so
  // make that state impossible to miss, even with the scorecard collapsed.
  function renderUnsavedState() {
    const run = state.snapshot?.currentRun;
    const dirty = Boolean(run && (hasDrafts(run.id) || hasBonusDraft(run.id)));
    ui.unsavedBar.hidden = !dirty;
    ui.unsavedBar.classList.toggle("has-error", Boolean(dirty && state.currentSaveError));
    // The bar keeps its space while hidden, so its text stays set; long errors are cut to one
    // line with the full text on hover.
    ui.unsavedMessage.textContent = dirty && state.currentSaveError
      ? `Not saved: ${state.currentSaveError}`
      : "Unsaved edits · not on the TV or in results until saved.";
    ui.unsavedMessage.title = ui.unsavedMessage.textContent;
    ui.unsavedSave.disabled = state.busy;
    ui.unsavedDiscard.disabled = state.busy;
    ui.currentDiscard.disabled = state.busy || !dirty;
    [ui.runTotal, ui.scorecardSummaryTotal].forEach((element) => {
      element.classList.toggle("is-unsaved", dirty);
      element.title = dirty ? "Includes unsaved scorecard edits" : "";
    });
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
      state.selectedLeaderboardRunId = "";
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
    ui.setupRetryLoad.addEventListener("click", () => loadSetup());
    ui.setupEditionName.addEventListener("input", onSetupInput);
    ui.setupBonus?.addEventListener("input", onSetupBonusInput);
    ui.setupBonus?.addEventListener("change", onSetupBonusInput);
    ui.setupEventList.addEventListener("input", onSetupInput);
    ui.setupEventList.addEventListener("focusin", (event) => {
      const input = event.target.closest("[data-setup-event-id]");
      if (!input || !setupEvent(input.dataset.setupEventId)) return;
      state.setupSelectedEventId = input.dataset.setupEventId;
      renderSetupSelection();
      renderSetupDiscovery();
    });
    ui.setupEventList.addEventListener("click", onSetupClick);
    ui.setupDiscoveredDevices.addEventListener("click", onSetupClick);
    ui.setupScanAll.addEventListener("click", () => runSetupScan());
    ui.setupSave.addEventListener("click", saveSetup);
    ui.setupSaveBottom.addEventListener("click", saveSetup);
    ui.setupAddEvent.addEventListener("click", () => {
      if (!state.setupDraft || state.setupSaving) return;
      const added = setupTools.addEvent(state.setupDraft);
      state.setupSelectedEventId = added.eventId;
      markSetupChanged();
      renderSetupEvents();
      renderSetupSelection();
      renderSetupControls();
    });
    ui.countdownRetry.addEventListener("click", () => countdownCoordinator.retry());
    try { ui.hardwarePanel.open = window.localStorage.getItem("gg.hardwarePanelOpen") === "true"; } catch { /* Storage may be unavailable. */ }
    ui.hardwarePanel.addEventListener("toggle", () => {
      try { window.localStorage.setItem("gg.hardwarePanelOpen", String(ui.hardwarePanel.open)); } catch { /* Storage may be unavailable. */ }
    });
    try { ui.scorecardPanel.open = window.localStorage.getItem("gg.scorecardPanelOpen") === "true"; } catch { /* Storage may be unavailable. */ }
    ui.scorecardPanel.addEventListener("toggle", () => {
      try { window.localStorage.setItem("gg.scorecardPanelOpen", String(ui.scorecardPanel.open)); } catch { /* Storage may be unavailable. */ }
    });
    ui.armPhysical.addEventListener("click", () => performAction(armPhysicalRun, "Run armed · waiting for the physical Start button."));
    ui.start.addEventListener("click", () => performAction(startRun, "Countdown started. Run begins at Go."));
    ui.prime.addEventListener("click", () => performAction(primeNextCompetitor,
      `The TV now shows ${competitorName(ui.competitor.value)} as up next.`));
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
    ui.undoPress.addEventListener("click", () => performAction(
      () => request("/api/run/undo-last-press", { method: "POST" }),
      "The last event-button press was undone."
    ));
    ui.finish.addEventListener("click", () => {
      const run = state.snapshot?.currentRun;
      if (!run || !["active", "paused"].includes(run.status)) return;
      // Ending a run with time on the clock is rarely intended; the usual finish is a timeout.
      const leftMs = runActions.remainingMs(run);
      if (leftMs >= 1000) {
        const left = masterActions.formatRunDuration(Math.floor(leftMs / 1000));
        const confirmed = window.confirm(
          `Finish ${competitorName(run.competitorId)}'s run with ${left} still on the clock?\n\n` +
          "The clock stops and any unfinished events stay unfinished. Until the result is recorded, you can Reopen the run to continue."
        );
        if (!confirmed) return;
      }
      performAction(
        () => request("/api/run/finish", { method: "POST" }),
        "Run finished · not recorded yet."
      );
    });
    ui.reopen.addEventListener("click", () => performAction(
      () => request("/api/run/reopen", { method: "POST" }),
      "Run reopened and paused with the clock where it stopped. Press Resume to continue."
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
    ui.historyDelete.addEventListener("click", () => performAction(deleteHistoricalRun, "Run deleted. It can be restored from Deleted runs."));
    const refilterHistory = () => { state.historyTableKey = null; renderHistory(); setSaveStates(); };
    ui.historySearch.addEventListener("input", refilterHistory);
    ui.historyCategoryFilter.addEventListener("change", refilterHistory);
    ui.historyStatusFilter.addEventListener("change", refilterHistory);
    ui.historyClearFilters.addEventListener("click", () => {
      ui.historySearch.value = "";
      ui.historyCategoryFilter.value = "";
      ui.historyStatusFilter.value = "";
      refilterHistory();
      ui.historySearch.focus();
    });
    ui.currentSave.addEventListener("click", saveCurrentEdits);
    ui.unsavedSave.addEventListener("click", saveCurrentEdits);
    ui.currentDiscard.addEventListener("click", discardCurrentEdits);
    ui.unsavedDiscard.addEventListener("click", discardCurrentEdits);
    ui.historySave.addEventListener("click", saveHistoryEdits);
    ui.currentBody.addEventListener("input", onScoreInput);
    ui.historyBody.addEventListener("input", onScoreInput);
    ui.currentBody.addEventListener("click", onClearEventClick);
    ui.historyBody.addEventListener("click", onClearEventClick);
    ui.currentBonus.addEventListener("input", onBonusInput);
    ui.historyBonus.addEventListener("input", onBonusInput);
    ui.addForm.addEventListener("submit", (event) => {
      event.preventDefault();
      const name = ui.newCompetitor.value.trim();
      if (!name) return;
      const duplicate = (state.snapshot?.competitors || []).find((item) => normalizedCompetitorName(item.name) === normalizedCompetitorName(name));
      if (duplicate && !window.confirm(`“${duplicate.name}” already exists${duplicate.isArchived ? " in the archived list" : ""}. Add another player with the same name anyway?`)) return;
      performAction(async () => {
        const competitor = await request("/api/competitors", { method: "POST", body: JSON.stringify({ name }) });
        state.selectedCompetitorAfterRefresh = competitor.id;
        state.queueCompetitorAfterRefresh = competitor.id;
        ui.newCompetitor.value = "";
        ui.competitorDuplicateWarning.hidden = true;
      }, "Competitor added.");
    });
    ui.newCompetitor.addEventListener("input", updateDuplicateCompetitorWarning);
    ui.showArchivedCompetitors.addEventListener("change", () => {
      state.showArchivedCompetitors = ui.showArchivedCompetitors.checked;
      renderCompetitorRoster();
    });
    ui.competitorRosterList.addEventListener("click", handleRosterAction);
    ui.competitorImportFile.addEventListener("change", async () => {
      const file = ui.competitorImportFile.files?.[0];
      if (!file) return;
      try {
        ui.competitorImportText.value = await file.text();
        ui.competitorImportResult.textContent = `${file.name} loaded. Review the names, then choose Import names.`;
      } catch (error) {
        ui.competitorImportResult.textContent = `Could not read that file: ${error.message || "unknown error"}`;
      }
    });
    ui.competitorImportForm.addEventListener("submit", (event) => {
      event.preventDefault();
      const names = parseCompetitorCsv(ui.competitorImportText.value);
      if (!names.length) {
        ui.competitorImportResult.textContent = "Paste or choose a list containing at least one name first.";
        return;
      }
      if (names.length > 500) {
        ui.competitorImportResult.textContent = "Import is limited to 500 rows at a time.";
        return;
      }
      performAction(async () => {
        const result = await request("/api/competitors/import", { method: "POST", body: JSON.stringify({ names }) });
        ui.competitorImportText.value = "";
        ui.competitorImportFile.value = "";
        const skipped = result.skipped || [];
        ui.competitorImportResult.textContent = `${result.added?.length || 0} added · ${skipped.length} skipped${skipped.length ? `: ${skipped.slice(0, 5).join("; ")}${skipped.length > 5 ? `; and ${skipped.length - 5} more` : ""}` : ""}`;
      }, "Player list imported. Existing and repeated names were skipped.");
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
    ui.durationInput.addEventListener("input", () => {
      updateControls();
      tickClock();
    });
    ui.queueCompetitor.addEventListener("change", updateControls);
    ui.leaderboardPlayerSelect.addEventListener("change", () => {
      state.selectedLeaderboardRunId = ui.leaderboardPlayerSelect.value;
      state.playerLeaderboardKey = null;
      renderLeaderboards();
    });
    ui.showExhibitionsOnLeaderboard.addEventListener("change", () => {
      const showExhibitionsOnLeaderboard = ui.showExhibitionsOnLeaderboard.checked;
      state.exhibitionPreferenceDraft = showExhibitionsOnLeaderboard;
      performAction(async () => {
        try {
          await request("/api/leaderboards/preferences", {
            method: "PUT",
            body: JSON.stringify({ showExhibitionsOnLeaderboard })
          });
          state.exhibitionPreferenceDraft = null;
        } catch (error) {
          state.exhibitionPreferenceDraft = null;
          throw error;
        }
      }, showExhibitionsOnLeaderboard ? "Exhibition runs are now included on the leaderboards." : "Exhibition runs are hidden from the leaderboards.");
    });
    ui.clearDatabaseConfirmation.addEventListener("input", updateControls);
    ui.clearDatabaseButton.addEventListener("click", clearDatabaseForTesting);
  }

  bindActions();
  loadSnapshot(false);
  loadMaster(false);
  void loadSetup();
  void countdownCoordinator.poll();
  window.setInterval(() => loadSnapshot(true), 2000);
  window.setInterval(() => loadMaster(true), 2000);
  // The master reports physical START immediately; short local polling keeps the
  // browser cue aligned with that hardware countdown instead of adding 250ms skew.
  window.setInterval(() => countdownCoordinator.poll(), 50);
  window.setInterval(tickClock, 200);
  window.setInterval(() => void pollBonusLive(), 250);
  window.setInterval(renderBonusLive, 100);
})();
