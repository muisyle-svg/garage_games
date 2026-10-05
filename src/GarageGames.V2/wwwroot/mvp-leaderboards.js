(() => {
  "use strict";

  function buildEventLeaderboards(events, leaderboard, history) {
    const runsById = new Map((history || []).map((run) => [run.id, run]));
    return (events || []).map((event) => {
      // Events rank by points, then the faster time. The bonus round ranks by points alone:
      // a longer round is not a worse one.
      const pointsOnly = event.type === "bonusRound";
      const rows = [];
      const dnfRows = [];
      for (const overallRow of leaderboard || []) {
        if (!["playoff", "official", "exhibition"].includes(overallRow.category) || !overallRow.runId) continue;
        const run = runsById.get(overallRow.runId);
        if (!run || run.category !== overallRow.category) continue;
        const displayName = overallRow.displayName || overallRow.competitorName;
        const result = (run.events || []).find((item) => item.eventId === event.eventId);
        if (!result || result.status !== "completed") {
          dnfRows.push({
            competitorId: run.competitorId,
            competitorName: overallRow.competitorName,
            displayName,
            category: overallRow.category,
            status: "dnf",
            rank: null,
            points: null,
            durationMs: null
          });
          continue;
        }
        const hasCompleteTiming = Number.isFinite(result.startElapsedMs)
          && Number.isFinite(result.finishElapsedMs)
          && result.finishElapsedMs >= result.startElapsedMs;
        const durationMs = hasCompleteTiming ? result.finishElapsedMs - result.startElapsedMs : null;
        rows.push({
          competitorId: run.competitorId,
          competitorName: overallRow.competitorName,
          displayName,
          category: overallRow.category,
          status: "completed",
          points: Number(result.scoreOverride ?? result.score ?? 0),
          durationMs
        });
      }
      // One ranking across every run type (official, playoff, exhibition): most points first,
      // then the faster time; equal results share a rank. The type is only a label.
      rows.sort((a, b) => {
        if (a.points !== b.points) return b.points - a.points;
        if (pointsOnly) return a.displayName.localeCompare(b.displayName);
        if (a.durationMs !== null && b.durationMs !== null && a.durationMs !== b.durationMs) {
          return a.durationMs - b.durationMs;
        }
        if ((a.durationMs === null) !== (b.durationMs === null)) return a.durationMs === null ? 1 : -1;
        return a.displayName.localeCompare(b.displayName);
      });
      rows.forEach((row, index) => {
        const previous = rows[index - 1];
        const tied = previous && row.points === previous.points && (pointsOnly || row.durationMs === previous.durationMs);
        row.rank = tied ? previous.rank : index + 1;
      });
      // Everyone without a result for this event follows, by name.
      dnfRows.sort((a, b) => a.displayName.localeCompare(b.displayName));
      return { eventId: event.eventId, name: event.name, rows: [...rows, ...dnfRows] };
    });
  }

  function eventLeaderboardDisplayRow(row) {
    if (row.status === "dnf") return { rank: "DNF", durationMs: null, points: "—", category: row.category };
    return {
      rank: String(row.rank),
      durationMs: Number.isFinite(row.durationMs) ? row.durationMs : null,
      points: String(row.points),
      category: row.category
    };
  }

  // The player scorecard's choices: every recorded result per player (their counted official
  // run, playoffs, and every exhibition) in the current edition, like the standings, grouped
  // by player. Superseded, discarded, and deleted runs are left out. Several runs of one type
  // are numbered in the order played.
  function scorecardRunChoices(history, competitors, editionId) {
    const names = new Map((competitors || []).map((competitor) => [competitor.id, competitor.name]));
    const categoryOrder = { playoff: 0, official: 1, exhibition: 2 };
    const labels = { playoff: "Playoff", official: "Official", exhibition: "Exhibition" };
    const playedAt = (run) => String(run.recordedAt || run.finishedAt || run.createdAt || "");
    const byPlayer = new Map();
    for (const run of history || []) {
      if (!run?.isRecorded || run.deletedAt || run.supersededByRunId || ["aborted", "superseded"].includes(run.status)) continue;
      if (run.editionId !== editionId) continue;
      if (!byPlayer.has(run.competitorId)) byPlayer.set(run.competitorId, []);
      byPlayer.get(run.competitorId).push(run);
    }
    return Array.from(byPlayer.entries()).map(([competitorId, runs]) => {
      runs.sort((a, b) => (categoryOrder[a.category] ?? 3) - (categoryOrder[b.category] ?? 3) ||
        playedAt(a).localeCompare(playedAt(b)));
      const counts = new Map();
      runs.forEach((run) => counts.set(run.category, (counts.get(run.category) || 0) + 1));
      const seen = new Map();
      return {
        competitorId,
        name: names.get(competitorId) || "Unknown competitor",
        runs: runs.map((run) => {
          const number = (seen.get(run.category) || 0) + 1;
          seen.set(run.category, number);
          const label = labels[run.category] || String(run.category || "Run");
          return { runId: run.id, category: run.category, label: counts.get(run.category) > 1 ? `${label} ${number}` : label };
        })
      };
    }).sort((a, b) => a.name.localeCompare(b.name));
  }

  const api = Object.freeze({ buildEventLeaderboards, eventLeaderboardDisplayRow, scorecardRunChoices });
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  if (typeof window !== "undefined") window.GarageGamesLeaderboards = api;
})();
