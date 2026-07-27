const $ = id => document.getElementById(id);
function time(seconds) {
  const value = Math.max(0, Math.floor(seconds || 0));
  return `${Math.floor(value / 60)}:${String(value % 60).padStart(2, "0")}`;
}
function clean(value) {
  return String(value ?? "").replace(/[&<>"']/g, ch => ({
    "&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#039;"
  })[ch]);
}
async function refresh() {
  const [state, leaders] = await Promise.all([
    fetch("/api/state").then(response => response.json()),
    fetch("/api/leaderboard").then(response => response.json())
  ]);
  $("displayCompetitor").textContent = state.currentCompetitor?.name || "Garage Games 2026";
  $("displayTimer").textContent = time(state.run?.remainingSeconds ?? state.season.timerSeconds);
  $("displayProgress").textContent = state.run
    ? `${state.run.completionCount} completed · ${state.run.attemptCount} attempted · ${state.run.bonusCount} bonuses`
    : state.onDeck ? `Up next: ${state.onDeck.name}` : "Waiting for the next competitor";
  const progress = new Map((state.run?.games || []).map(game => [game.gameId, game]));
  $("displayGames").innerHTML = state.season.games.filter(game => game.enabled).map(game =>
    `<span class="display-game ${progress.get(game.id)?.completed ? "done" : ""}">${clean(game.name)}</span>`
  ).join("");
  $("displayLeaderboard").innerHTML = leaders.slice(0, 10).map(item =>
    `<li><span>${clean(item.competitorName)}</span><strong>${Number(item.score).toFixed(1)}</strong></li>`
  ).join("");
}
refresh();
setInterval(refresh, 500);

