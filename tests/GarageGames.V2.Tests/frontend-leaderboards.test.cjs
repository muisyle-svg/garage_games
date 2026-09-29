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

test("event leaderboards use visible category runs and configured events", () => {
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
  assert.deepEqual(boards[0].rows.map((row) => [row.competitorName, row.category, row.rank, row.points, row.durationMs]), [
    ["Casey", "playoff", 1, 50, 1_000],
    ["Blair", "official", 1, 45, 5_000],
    ["Alex", "official", 2, 45, 10_000]
  ]);
  assert.deepEqual(boards[1].rows.map((row) => [row.competitorName, row.category, row.status, row.rank, row.points, row.durationMs]), [
    ["Casey", "playoff", "dnf", null, null, null],
    ["Alex", "official", "dnf", null, null, null],
    ["Blair", "official", "dnf", null, null, null]
  ], "visible competitors without a completed event appear as DNF within their category");
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

test("event leaderboards include score-only, playoff, exhibition, and DNF results", () => {
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
  assert.deepEqual(rows.map((row) => [row.competitorName, row.category, row.status, row.rank, row.points, row.durationMs]), [
    ["Playoff", "playoff", "dnf", null, null, null],
    ["Fast", "official", "completed", 1, 50, 10_000],
    ["Slow", "official", "completed", 2, 50, 15_000],
    ["Manual", "official", "completed", 3, 50, null],
    ["Lower", "official", "completed", 4, 45, 5_000],
    ["Unfinished", "official", "dnf", null, null, null],
    ["Exhibition", "exhibition", "dnf", null, null, null]
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
  assert.deepEqual(rows.map((row) => [row.competitorName, row.category, row.status]), [
    ["Drew", "playoff", "dnf"],
    ["Blair", "official", "completed"],
    ["Alex", "official", "dnf"],
    ["Casey", "official", "dnf"]
  ]);
  assert.deepEqual([rows[0], rows[2], rows[3]].map((row) => [row.rank, row.points, row.durationMs]), [
    [null, null, null],
    [null, null, null],
    [null, null, null]
  ]);
  assert.deepEqual(eventLeaderboardDisplayRow(rows[0]), { rank: "DNF", durationMs: null, points: "—", category: "playoff" });
  assert.deepEqual(eventLeaderboardDisplayRow(rows[1]), { rank: "1", durationMs: 10_000, points: "40", category: "official" });
});

test("exhibition rows preserve server-assigned incremental names", () => {
  const events = [{ eventId: "pour", name: "Perfect Pour" }];
  const leaderboard = [
    { runId: "exhibition-1", competitorName: "Alex", displayName: "Alex (Exhibition 1)", category: "exhibition" },
    { runId: "exhibition-2", competitorName: "Alex", displayName: "Alex (Exhibition 2)", category: "exhibition" }
  ];
  const history = [
    run("exhibition-1", "a", "exhibition", [event("pour", 0, 10_000, 40)]),
    run("exhibition-2", "a", "exhibition", [event("pour", 0, 5_000, 45)])
  ];
  const rows = buildEventLeaderboards(events, leaderboard, history)[0].rows;
  assert.deepEqual(rows.map((row) => row.displayName), ["Alex (Exhibition 2)", "Alex (Exhibition 1)"]);
  assert.ok(rows.every((row) => row.category === "exhibition"));
});

test("the bonus round ranks by points alone and lists runs that never reached it as DNF", () => {
  const bonus = { eventId: "bonus-round", name: "Bonus round", type: "bonusRound" };
  const leaderboard = [
    { runId: "a", category: "official", competitorName: "Avery", points: 100 },
    { runId: "b", category: "official", competitorName: "Blake", points: 90 },
    { runId: "c", category: "official", competitorName: "Casey", points: 80 }
  ];
  const result = (id, points, durationMs) => ({
    id, category: "official", competitorId: id,
    events: [{ eventId: "bonus-round", status: "completed", score: points, startElapsedMs: 0, finishElapsedMs: durationMs }]
  });
  const history = [result("a", 20, 30000), result("b", 20, 5000), { id: "c", category: "official", competitorId: "c", events: [] }];
  const [board] = require("../../src/GarageGames.V2/wwwroot/mvp-leaderboards.js").buildEventLeaderboards([bonus], leaderboard, history);
  assert.deepEqual(board.rows.map((row) => [row.competitorName, row.rank, row.status]), [
    ["Avery", 1, "completed"],
    ["Blake", 1, "completed"], // Same points share the rank; the shorter round is not ahead.
    ["Casey", null, "dnf"]
  ]);
});
