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
      rows.sort((a, b) => {
        const categoryOrder = { playoff: 0, official: 1, exhibition: 2 };
        if (categoryOrder[a.category] !== categoryOrder[b.category]) return categoryOrder[a.category] - categoryOrder[b.category];
        if (a.points !== b.points) return b.points - a.points;
        if (pointsOnly) return a.competitorName.localeCompare(b.competitorName);
        if (a.durationMs !== null && b.durationMs !== null && a.durationMs !== b.durationMs) {
          return a.durationMs - b.durationMs;
        }
        if ((a.durationMs === null) !== (b.durationMs === null)) return a.durationMs === null ? 1 : -1;
        return a.competitorName.localeCompare(b.competitorName);
      });
      let rank = 0;
      let previousCategory = null;
      let categoryIndex = 0;
      rows.forEach((row, index) => {
        const previous = rows[index - 1];
        if (row.category !== previousCategory) {
          previousCategory = row.category;
          categoryIndex = 0;
          rank = 0;
        }
        categoryIndex++;
        if (!previous || previous.category !== row.category || row.points !== previous.points ||
            (!pointsOnly && row.durationMs !== previous.durationMs)) rank = categoryIndex;
        row.rank = rank;
      });
      dnfRows.sort((a, b) => {
        const categoryOrder = { playoff: 0, official: 1, exhibition: 2 };
        return categoryOrder[a.category] - categoryOrder[b.category] || a.displayName.localeCompare(b.displayName);
      });
      const completedByCategory = new Map();
      for (const row of rows) {
        if (!completedByCategory.has(row.category)) completedByCategory.set(row.category, []);
        completedByCategory.get(row.category).push(row);
      }
      const dnfByCategory = new Map();
      for (const row of dnfRows) {
        if (!dnfByCategory.has(row.category)) dnfByCategory.set(row.category, []);
        dnfByCategory.get(row.category).push(row);
      }
      const orderedRows = [];
      for (const category of ["playoff", "official", "exhibition"]) {
        orderedRows.push(...(completedByCategory.get(category) || []), ...(dnfByCategory.get(category) || []));
      }
      return { eventId: event.eventId, name: event.name, rows: orderedRows };
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

  const api = Object.freeze({ buildEventLeaderboards, eventLeaderboardDisplayRow });
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  if (typeof window !== "undefined") window.GarageGamesLeaderboards = api;
})();
