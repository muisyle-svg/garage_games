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
    ["Blair", "official", 2, 45, 5_000],
    ["Alex", "official", 3, 45, 10_000]
  ], "one ranking across run types: points, then the faster time");
  assert.deepEqual(boards[1].rows.map((row) => [row.competitorName, row.category, row.status, row.rank, row.points, row.durationMs]), [
    ["Alex", "official", "dnf", null, null, null],
    ["Blair", "official", "dnf", null, null, null],
    ["Casey", "playoff", "dnf", null, null, null]
  ], "visible competitors without a completed event appear as DNF, by name");
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
    ["Fast", "official", "completed", 1, 50, 10_000],
    ["Slow", "official", "completed", 2, 50, 15_000],
    ["Manual", "official", "completed", 3, 50, null],
    ["Lower", "official", "completed", 4, 45, 5_000],
    ["Exhibition", "exhibition", "dnf", null, null, null],
    ["Playoff", "playoff", "dnf", null, null, null],
    ["Unfinished", "official", "dnf", null, null, null]
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
    ["Blair", "official", "completed"],
    ["Alex", "official", "dnf"],
    ["Casey", "official", "dnf"],
    ["Drew", "playoff", "dnf"]
  ]);
  assert.deepEqual([rows[1], rows[2], rows[3]].map((row) => [row.rank, row.points, row.durationMs]), [
    [null, null, null],
    [null, null, null],
    [null, null, null]
  ]);
  assert.deepEqual(eventLeaderboardDisplayRow(rows[3]), { rank: "DNF", durationMs: null, points: "—", category: "playoff" });
  assert.deepEqual(eventLeaderboardDisplayRow(rows[0]), { rank: "1", durationMs: 10_000, points: "40", category: "official" });
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

test("the player scorecard offers every recorded run, numbering several of one type", () => {
  const { scorecardRunChoices } = require("../../src/GarageGames.V2/wwwroot/mvp-leaderboards.js");
  const run = (id, competitorId, category, recordedAt, extra = {}) =>
    ({ id, competitorId, category, status: "completed", isRecorded: true, recordedAt, editionId: "2026", ...extra });
  const history = [
    run("ex2", "p1", "exhibition", "2026-09-28T20:00:00Z"),
    run("off", "p1", "official", "2026-09-28T18:00:00Z"),
    run("ex1", "p1", "exhibition", "2026-09-28T19:00:00Z"),
    run("play", "p1", "playoff", "2026-09-29T18:00:00Z"),
    run("old", "p1", "official", "2026-09-27T18:00:00Z", { status: "superseded", supersededByRunId: "off" }),
    run("gone", "p1", "exhibition", "2026-09-28T21:00:00Z", { deletedAt: "2026-09-28T22:00:00Z" }),
    run("live", "p1", "exhibition", null, { isRecorded: false, status: "finished" }),
    run("tossed", "p1", "exhibition", null, { isRecorded: false, status: "aborted" }),
    run("timeout", "p2", "official", "2026-09-28T18:30:00Z", { status: "timedOut" }),
    run("solo-ex", "p2", "exhibition", "2026-09-28T19:30:00Z"),
    // Earlier editions (including an older version of this one) are not listed.
    run("last-year", "p2", "official", "2025-09-28T19:30:00Z", { editionId: "2025" }),
    run("only-old", "p3", "official", "2025-09-28T19:30:00Z", { editionId: "2025" })
  ];
  const groups = scorecardRunChoices(history, [{ id: "p1", name: "Zed" }, { id: "p2", name: "Amy" }, { id: "p3", name: "Old" }], "2026");
  assert.deepEqual(groups.map((group) => [group.name, group.runs.map((choice) => [choice.runId, choice.label])]), [
    ["Amy", [["timeout", "Official"], ["solo-ex", "Exhibition"]]],
    ["Zed", [["play", "Playoff"], ["off", "Official"], ["ex1", "Exhibition 1"], ["ex2", "Exhibition 2"]]]
  ]);
});

test("an exhibition or playoff that beats an official result ranks above it", () => {
  const events = [{ eventId: "pour", name: "Perfect Pour" }];
  const leaderboard = [
    { runId: "official", competitorName: "Olive", category: "official" },
    { runId: "exhibition", competitorName: "Ezra", category: "exhibition" },
    { runId: "playoff", competitorName: "Pat", category: "playoff" }
  ];
  const history = [
    run("official", "o", "official", [event("pour", 0, 12_000, 45)]),
    run("exhibition", "e", "exhibition", [event("pour", 0, 4_000, 50)]),
    run("playoff", "p", "playoff", [event("pour", 0, 12_000, 45)])
  ];
  const rows = buildEventLeaderboards(events, leaderboard, history)[0].rows;
  assert.deepEqual(rows.map((row) => [row.competitorName, row.category, row.rank]), [
    ["Ezra", "exhibition", 1],
    ["Olive", "official", 2],
    ["Pat", "playoff", 2] // Same points and time as Olive: a shared rank, whatever the type.
  ]);
});