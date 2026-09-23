(() => {
  "use strict";

  function buildEventLeaderboards(events, leaderboard, history) {
    const runsById = new Map((history || []).map((run) => [run.id, run]));
    return (events || []).map((event) => {
      const rows = [];
      const dnfRows = [];
      for (const overallRow of leaderboard || []) {
        if (overallRow.category !== "official" || !overallRow.runId) continue;
        const run = runsById.get(overallRow.runId);
        if (!run || run.category !== "official") continue;
        const result = (run.events || []).find((item) => item.eventId === event.eventId);
        if (!result || result.status !== "completed") {
          dnfRows.push({
            competitorId: run.competitorId,
            competitorName: overallRow.competitorName,
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
          status: "completed",
          points: Number(result.scoreOverride ?? result.score ?? 0),
          durationMs
        });
      }
      rows.sort((a, b) => {
        if (a.points !== b.points) return b.points - a.points;
        if (a.durationMs !== null && b.durationMs !== null && a.durationMs !== b.durationMs) {
          return a.durationMs - b.durationMs;
        }
        if ((a.durationMs === null) !== (b.durationMs === null)) return a.durationMs === null ? 1 : -1;
        return a.competitorName.localeCompare(b.competitorName);
      });
      let rank = 0;
      rows.forEach((row, index) => {
        const previous = rows[index - 1];
        if (!previous || row.points !== previous.points || row.durationMs !== previous.durationMs) rank = index + 1;
        row.rank = rank;
      });
      dnfRows.sort((a, b) => a.competitorName.localeCompare(b.competitorName));
      return { eventId: event.eventId, name: event.name, rows: [...rows, ...dnfRows] };
    });
  }

  function eventLeaderboardDisplayRow(row) {
    if (row.status === "dnf") return { rank: "DNF", durationMs: null, points: "—" };
    return {
      rank: String(row.rank),
      durationMs: Number.isFinite(row.durationMs) ? row.durationMs : null,
      points: String(row.points)
    };
  }

  const api = Object.freeze({ buildEventLeaderboards, eventLeaderboardDisplayRow });
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  if (typeof window !== "undefined") window.GarageGamesLeaderboards = api;
})();
