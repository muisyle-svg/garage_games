const state = { value: null, setup: null, seasons: [] };
const $ = id => document.getElementById(id);

async function api(path, options = {}) {
  const response = await fetch(path, {
    headers: { "Content-Type": "application/json", ...(options.headers || {}) },
    ...options
  });
  if (!response.ok) {
    let message = `Request failed (${response.status})`;
    try { message = (await response.json()).error || message; } catch {}
    throw new Error(message);
  }
  const type = response.headers.get("content-type") || "";
  return type.includes("json") ? response.json() : response.text();
}

function toast(message, error = false) {
  const el = $("toast");
  el.textContent = message;
  el.className = error ? "show error" : "show";
  setTimeout(() => { el.className = ""; }, 3500);
}

function formatTime(seconds) {
  const safe = Math.max(0, Math.floor(seconds || 0));
  return `${Math.floor(safe / 60)}:${String(safe % 60).padStart(2, "0")}`;
}

async function refresh() {
  try {
    state.value = await api("/api/state");
    render();
  } catch (error) {
    toast(error.message, true);
  }
}

function render() {
  const value = state.value;
  if (!value) return;
  $("connectionBadge").textContent = value.masterConnected ? "Master connected" : "Master offline";
  $("connectionBadge").className = `badge ${value.masterConnected ? "good" : "bad"}`;
  $("currentCompetitor").textContent = value.currentCompetitor?.name || "No active run";
  $("onDeck").textContent = value.onDeck ? `On deck: ${value.onDeck.name}` : "No competitor on deck";
  const remaining = value.run?.remainingSeconds ?? value.season.timerSeconds;
  $("timer").textContent = formatTime(remaining);
  $("timer").className = `timer ${remaining === 0 ? "expired" : remaining <= 30 ? "warning" : ""}`;

  const active = value.run && ["active", "paused", "bonus"].includes(value.run.status);
  $("startButton").disabled = active || value.queue.length === 0;
  $("pauseButton").disabled = !value.run || value.run.status !== "active";
  $("resumeButton").disabled = !value.run || value.run.status !== "paused";
  $("undoButton").disabled = !active;
  $("abortButton").disabled = !active;

  const score = value.score;
  $("scoreStrip").innerHTML = score ? `
    <span>Score <strong>${Number(score.total).toFixed(1)}</strong></span>
    <span>Completed <strong>${value.run.completionCount}/${value.season.requiredCompletions}</strong></span>
    <span>Attempted <strong>${value.run.attemptCount}</strong></span>
    <span>Bonuses <strong>${value.run.bonusCount}</strong></span>` : "<span>No active score</span>";

  const progress = new Map((value.run?.games || []).map(game => [game.gameId, game]));
  $("games").innerHTML = value.season.games.filter(game => game.enabled).sort((a, b) => a.order - b.order).map(game => {
    const item = progress.get(game.id);
    const css = item?.completed ? "completed" : item?.attempted ? "attempted" : "";
    const meta = item?.completed
      ? `Completed with ${formatTime(item.completionRemainingSeconds)} left · ${item.durationSeconds ?? 0}s`
      : item?.attempted
        ? `Attempt started with ${formatTime(item.attemptRemainingSeconds)} left`
        : "Waiting";
    return `<article class="game-card ${css}">
      <h3>${game.order}. ${escapeHtml(game.name)} ${item?.bonus ? "★" : ""}</h3>
      <div class="game-meta">${meta}</div>
      <div class="game-actions">
        <button onclick="gameEvent('attempt_started','${game.id}')">Attempt</button>
        <button onclick="gameEvent('game_completed','${game.id}')">Complete</button>
        <button onclick="gameEvent('bonus_hit','${game.id}')">Bonus</button>
      </div>
    </article>`;
  }).join("");

  $("queueList").innerHTML = value.queue.map((competitor, index) =>
    `<li><strong>${escapeHtml(competitor.name)}</strong>${index === 0 ? " · next" : ""}<br><span class="muted">${escapeHtml(competitor.notes || "")}</span></li>`
  ).join("");

  $("deviceSummary").textContent = `${value.devices.length} device${value.devices.length === 1 ? "" : "s"} discovered`;
  $("deviceGrid").innerHTML = value.devices.map(device => `
    <article class="device-card">
      <h3>${escapeHtml(device.id)}</h3>
      <div class="device-stats">
        <span>Firmware ${escapeHtml(device.firmwareVersion)}</span><span>${escapeHtml(device.stationModule)}</span>
        <span>${device.batteryMillivolts} mV</span><span>RSSI ${device.rssi}</span>
        <span>Channel ${device.channel}</span><span>${device.radioFailures} failures</span>
      </div>
      <select onchange="assignDevice('${device.id}', this.value)">
        <option value="">Assign station…</option>
        ${value.season.games.filter(g => g.enabled).map(g =>
          `<option value="${g.id}" ${device.gameId === g.id ? "selected" : ""}>${escapeHtml(g.name)}</option>`
        ).join("")}
      </select>
    </article>`).join("");

  refreshLeaderboard();
}

async function gameEvent(type, gameId) {
  try {
    await api("/api/runs/event", { method: "POST", body: JSON.stringify({ type, gameId, note: "Virtual station" }) });
    await refresh();
  } catch (error) { toast(error.message, true); }
}
window.gameEvent = gameEvent;

async function assignDevice(deviceId, gameId) {
  if (!gameId) return;
  try {
    await api(`/api/devices/${encodeURIComponent(deviceId)}/assignment`, {
      method: "POST", body: JSON.stringify({ gameId })
    });
    toast("Station assignment saved.");
    await refresh();
  } catch (error) { toast(error.message, true); }
}
window.assignDevice = assignDevice;

async function refreshLeaderboard() {
  const entries = await api("/api/leaderboard");
  $("leaderboard").innerHTML = entries.map(item => `<tr>
    <td>${item.rank}</td><td>${escapeHtml(item.competitorName)}</td>
    <td><strong>${Number(item.score).toFixed(1)}</strong></td>
    <td>${formatTime(item.remainingSeconds)}</td><td>${item.completed}</td><td>${item.bonuses}</td>
  </tr>`).join("");
}

function escapeHtml(value) {
  return String(value ?? "").replace(/[&<>"']/g, ch => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#039;"
  })[ch]);
}

$("competitorForm").addEventListener("submit", async event => {
  event.preventDefault();
  try {
    await api("/api/competitors", {
      method: "POST",
      body: JSON.stringify({ name: $("competitorName").value, notes: $("competitorNotes").value })
    });
    $("competitorName").value = "";
    $("competitorNotes").value = "";
    await refresh();
  } catch (error) { toast(error.message, true); }
});

$("startButton").addEventListener("click", async () => {
  const next = state.value?.queue?.[0];
  if (!next) return;
  try {
    await api("/api/runs/start", { method: "POST", body: JSON.stringify({ competitorId: next.id }) });
    await refresh();
  } catch (error) { toast(error.message, true); }
});
$("pauseButton").addEventListener("click", () => action("/api/runs/pause", {}));
$("resumeButton").addEventListener("click", () => action("/api/runs/resume", {}));
$("undoButton").addEventListener("click", () => action("/api/runs/undo", { reason: "Operator undo" }));
$("abortButton").addEventListener("click", () => {
  if (confirm("Abort the current run? The partial event history will be preserved.")) {
    action("/api/runs/abort", { reason: "Operator aborted run" });
  }
});
$("backupButton").addEventListener("click", async () => {
  try {
    await api("/api/backup", { method: "POST", body: "{}" });
    toast("Database backup created.");
  } catch (error) { toast(error.message, true); }
});

async function action(path, payload) {
  try {
    await api(path, { method: "POST", body: JSON.stringify(payload) });
    await refresh();
  } catch (error) { toast(error.message, true); }
}

$("participantSearch").addEventListener("input", async event => {
  const result = await api(`/api/participants?query=${encodeURIComponent(event.target.value)}`);
  $("participantResults").innerHTML = event.target.value ? result.map(item =>
    `<p><strong>${escapeHtml(item.competitorName)}</strong> · ${item.status} · ${item.score ? Number(item.score.total).toFixed(1) : "unscored"}</p>`
  ).join("") : "";
});

async function initialize() {
  state.seasons = await api("/api/seasons");
  state.setup = await api("/api/setup");
  $("seasonSelect").innerHTML = state.seasons.map(item => `<option value="${item.id}">${escapeHtml(item.name)}${item.draft ? " (draft)" : ""}</option>`).join("");
  if (!state.setup.eventName) {
    $("setupPanel").classList.remove("hidden");
  }
  $("setupForm").addEventListener("submit", async event => {
    event.preventDefault();
    try {
      await api("/api/setup", { method: "POST", body: JSON.stringify({
        eventName: $("eventName").value,
        seasonId: $("seasonSelect").value,
        googleEndpoint: $("googleEndpoint").value,
        googleSecret: $("googleSecret").value
      }) });
      $("setupPanel").classList.add("hidden");
      toast("Setup saved.");
      await refresh();
    } catch (error) { toast(error.message, true); }
  });
  await refresh();
  setInterval(refresh, 500);
}

initialize().catch(error => toast(error.message, true));
