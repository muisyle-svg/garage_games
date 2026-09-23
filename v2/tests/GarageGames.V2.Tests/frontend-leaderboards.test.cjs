const test = require("node:test");
const assert = require("node:assert/strict");
const { buildEventLeaderboards, eventLeaderboardDisplayRow } = require("../../src/GarageGames.V2/wwwroot/mvp-leaderboards.js");

function run(id, competitorId, category, events) {
  return { id, competitorId, category, events };
}

function event(
  eventId,
  startElapsedMs,
  finishElapsedMs,
  score,
  scoreOverride = null,
  status = Number.isFinite(startElapsedMs) && Number.isFinite(finishElapsedMs) ? "completed" : "pending"
) {
  return { eventId, startElapsedMs, finishElapsedMs, score, scoreOverride, status };
}

test("event leaderboards use official leaderboard run IDs and configured events", () => {
  const events = [{ eventId: "pour", name: "Perfect Pour" }, { eventId: "darts", name: "Row Darts" }];
  const leaderboard = [
    { runId: "official-a", competitorName: "Alex", category: "official" },
    { runId: "official-b", competitorName: "Blair", category: "official" },
    { runId: "playoff", competitorName: "Casey", category: "playoff" }
  ];
  const history = [
    run("official-a", "a", "official", [event("pour", 10_000, 20_000, 45), event("darts", null, null, 0)]),
    run("official-b", "b", "official", [event("pour", 30_000, 35_000, 45)]),
    run("playoff", "c", "playoff", [event("pour", 1_000, 2_000, 50)])
  ];

  const boards = buildEventLeaderboards(events, leaderboard, history);
  assert.deepEqual(boards.map((board) => board.name), ["Perfect Pour", "Row Darts"]);
  assert.deepEqual(boards[0].rows.map((row) => [row.competitorName, row.rank, row.points, row.durationMs]), [
    ["Blair", 1, 45, 5_000],
    ["Alex", 2, 45, 10_000]
  ]);
  assert.deepEqual(boards[1].rows.map((row) => [row.competitorName, row.status, row.rank, row.points, row.durationMs]), [
    ["Alex", "dnf", null, null, null],
    ["Blair", "dnf", null, null, null]
  ], "counted official competitors without a completed event appear as DNF");
});

test("event leaderboard ties require equal points and equal duration", () => {
  const events = [{ eventId: "pour", name: "Perfect Pour" }];
  const leaderboard = [
    { runId: "a", competitorName: "Alex", category: "official" },
    { runId: "b", competitorName: "Blair", category: "official" },
    { runId: "c", competitorName: "Casey", category: "official" }
  ];
  const history = [
    run("a", "a", "official", [event("pour", 5_000, 10_000, 50)]),
    run("b", "b", "official", [event("pour", 15_000, 20_000, 50)]),
    run("c", "c", "official", [event("pour", 25_000, 30_000, 50, 45)])
  ];

  const rows = buildEventLeaderboards(events, leaderboard, history)[0].rows;
  assert.deepEqual(rows.map((row) => [row.competitorName, row.rank, row.points]), [
    ["Alex", 1, 50],
    ["Blair", 1, 50],
    ["Casey", 3, 45]
  ]);
});

test("event leaderboards include completed score-only results and exclude unfinished and non-official runs", () => {
  const events = [{ eventId: "pour", name: "Perfect Pour" }];
  const leaderboard = [
    { runId: "fast", competitorName: "Fast", category: "official" },
    { runId: "slow", competitorName: "Slow", category: "official" },
    { runId: "manual", competitorName: "Manual", category: "official" },
    { runId: "lower", competitorName: "Lower", category: "official" },
    { runId: "unfinished", competitorName: "Unfinished", category: "official" },
    { runId: "playoff", competitorName: "Playoff", category: "playoff" },
    { runId: "exhibition", competitorName: "Exhibition", category: "exhibition" }
  ];
  const history = [
    run("fast", "fast", "official", [event("pour", 10_000, 20_000, 50)]),
    run("slow", "slow", "official", [event("pour", 30_000, 45_000, 50)]),
    run("manual", "manual", "official", [event("pour", null, null, 0, 50, "completed")]),
    run("lower", "lower", "official", [event("pour", 5_000, 10_000, 45)]),
    run("unfinished", "unfinished", "official", [event("pour", 5_000, null, 100, null, "active")]),
    run("playoff", "playoff", "playoff", [event("pour", null, null, 100)]),
    run("exhibition", "exhibition", "exhibition", [event("pour", null, null, 100)])
  ];

  const rows = buildEventLeaderboards(events, leaderboard, history)[0].rows;
  assert.deepEqual(rows.map((row) => [row.competitorName, row.status, row.rank, row.points, row.durationMs]), [
    ["Fast", "completed", 1, 50, 10_000],
    ["Slow", "completed", 2, 50, 15_000],
    ["Manual", "completed", 3, 50, null],
    ["Lower", "completed", 4, 45, 5_000],
    ["Unfinished", "dnf", null, null, null]
  ]);
});

test("event leaderboard puts DNF after finishers and labels absent or incomplete events without numeric results", () => {
  const events = [{ eventId: "pour", name: "Perfect Pour" }];
  const leaderboard = [
    { runId: "absent", competitorName: "Alex", category: "official" },
    { runId: "completed", competitorName: "Blair", category: "official" },
    { runId: "active", competitorName: "Casey", category: "official" },
    { runId: "playoff", competitorName: "Drew", category: "playoff" }
  ];
  const history = [
    run("absent", "absent", "official", []),
    run("completed", "completed", "official", [event("pour", 10_000, 20_000, 40)]),
    run("active", "active", "official", [event("pour", 5_000, null, 50, null, "active")]),
    run("playoff", "playoff", "playoff", [])
  ];

  const rows = buildEventLeaderboards(events, leaderboard, history)[0].rows;
  assert.deepEqual(rows.map((row) => [row.competitorName, row.status]), [
    ["Blair", "completed"],
    ["Alex", "dnf"],
    ["Casey", "dnf"]
  ]);
  assert.deepEqual(rows.slice(1).map((row) => [row.rank, row.points, row.durationMs]), [
    [null, null, null],
    [null, null, null]
  ]);
  assert.deepEqual(eventLeaderboardDisplayRow(rows[1]), { rank: "DNF", durationMs: null, points: "—" });
  assert.deepEqual(eventLeaderboardDisplayRow(rows[0]), { rank: "1", durationMs: 10_000, points: "40" });
});
