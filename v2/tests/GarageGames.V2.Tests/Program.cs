using GarageGames.V2;
using System.Text.Json;

var tests = new (string Name, Action Run)[]
{
    ("score boundaries", ScoreBoundaries),
    ("pause freezes time and timeout precedence", PauseAndTimeout),
    ("timeout autosaves and releases next competitor", MvpTimeoutAndNextRun),
    ("MVP roster is 13 regular events with two-press virtual buttons", MvpRosterAndVirtualPresses),
    ("MVP timing fields clear and manual points total", MvpEditableScorecard),
    ("recording the same run twice is idempotent", FinishIsIdempotent),
    ("duplicate and per-event stale input", DuplicateAndStale),
    ("keypad and arcade completion rules", SpecialCompletion),
    ("manual preflight override retains roster and rejects offline packets", ManualOverride),
    ("bonus records signals without automatic points", BonusSignal),
    ("restart lineage and one official result", RestartAndOfficialRule),
    ("category exclusion and shared tie rank", CategoryAndTieRank),
    ("persistent recovery and exclusive data lock", RecoveryAndLock),
    ("live edit isolation, history edit, undo, and stale undo", EditsAndIsolation),
    ("unknown database is rejected", UnknownDatabase)
};

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"FAIL {test.Name}: {exception.Message}\n{exception.StackTrace}");
        Console.WriteLine(failures[^1]);
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"{failures.Count} test(s) failed.");
    return 1;
}

Console.WriteLine($"PASS all {tests.Length} v2 tests");
return 0;

static void ScoreBoundaries()
{
    var rule = new ScoringRule();
    Assert.Equal(100, Score(0, rule));
    Assert.Equal(100, Score(4_999, rule));
    Assert.Equal(95, Score(5_000, rule));
    Assert.Equal(95, Score(9_999, rule));
    Assert.Equal(90, Score(10_000, rule));
    Assert.Equal(50, Score(50_000, rule));
    Assert.Equal(50, Score(55_000, rule));

    static int Score(long duration, ScoringRule rule) => ScoreCalculator.Calculate(new EventRecord
    {
        EventId = "e",
        Name = "e",
        DeviceId = "d",
        Status = EventStatus.Completed,
        StartElapsedMs = 0,
        FinishElapsedMs = duration
    }, rule);
}

static void PauseAndTimeout()
{
    using var h = NewHarness(durationSeconds: 10);
    var run = h.ArmAndStart(RunCategory.Official);
    h.Clock.Advance(TimeSpan.FromSeconds(3));
    var paused = h.Service.Pause();
    Assert.Equal(RunStatus.Paused, paused.Status);
    Assert.Equal(3_000L, paused.ActiveElapsedMs);

    h.Clock.Advance(TimeSpan.FromSeconds(30));
    var ignored = h.Send(run, "station-01", "event-press", "pause-input");
    Assert.Equal(MessageDisposition.Paused, ignored.Disposition);
    Assert.Equal(RunStatus.Paused, h.Service.GetOperatorSnapshot().CurrentRun!.Status);

    var resumed = h.Service.Resume();
    Assert.Equal(3_000L, resumed.ActiveElapsedMs);
    h.Clock.Advance(TimeSpan.FromSeconds(7));
    var afterTimeout = h.Service.GetOperatorSnapshot().CurrentRun!;
    Assert.Equal(RunStatus.TimedOut, afterTimeout.Status);
    Assert.Equal(10_000L, afterTimeout.ActiveElapsedMs);

    var late = h.Send(run, "station-01", "event-press", "after-timeout");
    Assert.Equal(MessageDisposition.TimedOut, late.Disposition);
}

static void MvpTimeoutAndNextRun()
{
    using var h = new TestHarness(MakeMvpEdition(durationSeconds: 2), NewPath());
    var first = h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Official);
    h.Service.StartMaster();
    Assert.Equal(MessageDisposition.Accepted, h.Service.PressEvent(first.Id, "event-01").Disposition);
    h.Clock.Advance(TimeSpan.FromSeconds(2));

    var timedOut = h.Service.GetOperatorSnapshot().CurrentRun!;
    Assert.Equal(RunStatus.TimedOut, timedOut.Status);
    Assert.Equal(1, h.Service.GetOperatorSnapshot().History.Count(r => r.Id == first.Id));
    Assert.Equal(MessageDisposition.TimedOut, h.Service.PressEvent(first.Id, "event-01").Disposition);

    var nextCompetitor = h.AddCompetitor("Next competitor");
    var nextRun = h.Service.ArmCompetitor(nextCompetitor.Id, RunCategory.Official);
    Assert.Equal(RunStatus.Armed, nextRun.Status);
    Assert.Equal(RunStatus.TimedOut, h.Service.GetOperatorSnapshot().History.Single(r => r.Id == first.Id).Status);
    Assert.Equal(nextRun.Id, h.Service.StartMaster().Id);
    Assert.Equal(RunStatus.Active, h.Service.GetOperatorSnapshot().CurrentRun!.Status);
}

static void MvpRosterAndVirtualPresses()
{
    var editionPath = Path.Combine(Environment.CurrentDirectory, "v2", "config", "edition-2026.json");
    var edition = EditionDefinition.FromJson(editionPath);
    Assert.Equal(13, edition.Events.Count);
    Assert.True(edition.Events.All(e => e.Type == EventKind.Standard), "Every MVP event must use standard two-press behavior.");
    Assert.Equal("Perfect Pour", edition.Events[0].Name);
    Assert.Equal("Hammer Head", edition.Events[^1].Name);

    using var h = new TestHarness(edition, NewPath());
    var run = h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Official);
    h.Service.StartMaster();
    h.Clock.Advance(TimeSpan.FromSeconds(2));
    var started = h.Service.PressEvent(run.Id, "event-01");
    Assert.Equal(MessageDisposition.Accepted, started.Disposition);
    Assert.Equal(EventStatus.Active, started.Run!.Events.Single(e => e.EventId == "event-01").Status);
    h.Clock.Advance(TimeSpan.FromSeconds(3));
    var completed = h.Service.PressEvent(run.Id, "event-01");
    var result = completed.Run!.Events.Single(e => e.EventId == "event-01");
    Assert.Equal(EventStatus.Completed, result.Status);
    Assert.Equal(3_000L, result.DurationMs);
    Assert.Equal(0, result.Score);
    Assert.Equal(MessageDisposition.AlreadyCompleted, h.Service.PressEvent(run.Id, "event-01").Disposition);
}

static void MvpEditableScorecard()
{
    using var h = new TestHarness(MakeMvpEdition(durationSeconds: 20), NewPath());
    var run = h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Official);
    h.Service.StartMaster();
    h.Clock.Advance(TimeSpan.FromSeconds(4));
    var live = h.Service.GetOperatorSnapshot().CurrentRun!;
    var edited = h.Service.EditCurrentRun(new EditRunRequest
    {
        ExpectedRevision = live.Revision,
        Reason = "Enter scorecard values",
        BonusPointsOverride = 3,
        Events =
        [
            new EventEditRequest { EventId = "event-01", StartElapsedMs = 1_000, FinishElapsedMs = 2_500, ScoreOverride = 12 },
            new EventEditRequest { EventId = "event-02", ScoreOverride = 0 }
        ]
    });
    Assert.Equal(EventStatus.Completed, edited.Events.Single(e => e.EventId == "event-01").Status);
    Assert.Equal(15, edited.TotalPoints);

    var clearFinish = h.Service.EditCurrentRun(new EditRunRequest
    {
        ExpectedRevision = edited.Revision,
        Reason = "Clear mistaken finish",
        Events = [new EventEditRequest { EventId = "event-01", ClearFinishElapsedMs = true }]
    });
    Assert.Equal(1_000L, clearFinish.Events.Single(e => e.EventId == "event-01").StartElapsedMs);
    Assert.Equal(null, clearFinish.Events.Single(e => e.EventId == "event-01").FinishElapsedMs);
    Assert.Equal(EventStatus.Active, clearFinish.Events.Single(e => e.EventId == "event-01").Status);

    var finished = h.Service.Finish();
    var historical = h.Service.EditHistoricalRun(finished.Id, new EditRunRequest
    {
        ExpectedRevision = finished.Revision,
        Reason = "Clear mistaken start and replace points",
        Events = [new EventEditRequest
        {
            EventId = "event-01",
            ClearStartElapsedMs = true,
            ClearFinishElapsedMs = true,
            ScoreOverride = 9
        }]
    });
    Assert.Equal(null, historical.Events.Single(e => e.EventId == "event-01").StartElapsedMs);
    Assert.Equal(null, historical.Events.Single(e => e.EventId == "event-01").FinishElapsedMs);
    Assert.Equal(EventStatus.Pending, historical.Events.Single(e => e.EventId == "event-01").Status);
    Assert.Equal(12, historical.TotalPoints);
}

static void FinishIsIdempotent()
{
    using var h = new TestHarness(MakeMvpEdition(), NewPath());
    var run = h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Official);
    h.Service.StartMaster();
    var recorded = h.Service.Finish();
    var secondRecord = h.Service.Finish();
    Assert.Equal(recorded.Id, secondRecord.Id);
    Assert.Equal(1, h.Service.GetOperatorSnapshot().History.Count(r => r.Id == run.Id));
}

static void DuplicateAndStale()
{
    using var h = NewHarness();
    var run = h.ArmAndStart();
    h.Clock.Advance(TimeSpan.FromSeconds(5));
    var first = h.Send(run, "station-01", "event-press", "same-message", 5_000);
    Assert.Equal(MessageDisposition.Accepted, first.Disposition);
    var duplicate = h.Send(run, "station-01", "event-press", "same-message", 5_000);
    Assert.Equal(MessageDisposition.Duplicate, duplicate.Disposition);
    Assert.Equal(2, h.Service.GetOperatorSnapshot().Messages.Count(m => m.MessageId == "same-message"));

    h.Clock.Advance(TimeSpan.FromSeconds(5));
    var otherStation = h.Send(run, "station-02", "event-press", "station-two", 10_000);
    Assert.Equal(MessageDisposition.Accepted, otherStation.Disposition);
    var staleEventOne = h.Send(run, "station-01", "event-press", "event-one-stale", 4_000);
    Assert.Equal(MessageDisposition.StaleTimestamp, staleEventOne.Disposition);
    var eventOneCompletion = h.Send(run, "station-01", "event-press", "event-one-finish", 5_000);
    Assert.Equal(MessageDisposition.Accepted, eventOneCompletion.Disposition);
}

static void SpecialCompletion()
{
    using var h = NewHarness();
    var run = h.ArmAndStart();
    Assert.Equal(MessageDisposition.InvalidSignal, h.Send(run, "station-03", "arcade-finish", "arcade-before-start").Disposition);
    Assert.Equal(MessageDisposition.Accepted, h.Send(run, "station-03", "arcade-start", "arcade-start").Disposition);
    h.Clock.Advance(TimeSpan.FromSeconds(1));
    Assert.Equal(MessageDisposition.InvalidSignal, h.Send(run, "station-03", "arcade-start", "arcade-repeat", 1_000).Disposition);
    h.Clock.Advance(TimeSpan.FromSeconds(1));
    Assert.Equal(MessageDisposition.Accepted, h.Send(run, "station-03", "arcade-finish", "arcade-finish", 2_000).Disposition);
    Assert.Equal(MessageDisposition.AlreadyCompleted, h.Send(run, "station-03", "arcade-finish", "arcade-repeat-finish", 2_000).Disposition);

    h.Clock.Advance(TimeSpan.FromSeconds(1));
    Assert.Equal(MessageDisposition.Accepted, h.Send(run, "station-02", "event-press", "keypad-start", 3_000).Disposition);
    h.Clock.Advance(TimeSpan.FromSeconds(1));
    Assert.Equal(MessageDisposition.InvalidSignal, h.Send(run, "station-02", "event-press", "keypad-bypass", 4_000).Disposition);
    Assert.Equal(MessageDisposition.Accepted, h.Send(run, "station-02", "keypad-incorrect", "keypad-wrong", 4_000).Disposition);
    using (var wrong = JsonDocument.Parse("{\"answer\":\"no\"}"))
    {
        Assert.Equal(MessageDisposition.Accepted, h.Send(run, "station-02", "keypad-response", "keypad-response-wrong", 4_000, wrong.RootElement.GetRawText()).Disposition);
    }
    h.Clock.Advance(TimeSpan.FromSeconds(1));
    using (var correct = JsonDocument.Parse("{\"answer\":\"ok\"}"))
    {
        Assert.Equal(MessageDisposition.Accepted, h.Send(run, "station-02", "keypad-response", "keypad-response-correct", 5_000, correct.RootElement.GetRawText()).Disposition);
    }
}

static void ManualOverride()
{
    using var h = NewHarness();
    h.Service.SetDeviceAvailability("station-01", DeviceAvailability.Offline);
    Assert.Throws<CommandException>(() => h.ArmAndStart());
    var item = h.Service.GetOperatorSnapshot().Queue.Single();
    var run = h.Service.Arm(item.Id, manualOfflineOverride: true);
    Assert.True(run.ManualOfflineOverride);
    Assert.True(run.Events.Select(e => e.EventId).Contains("event-01"));
    h.Service.StartMaster();
    Assert.Equal(MessageDisposition.Offline, h.Send(run, "station-01", "event-press", "offline-packet").Disposition);

    var current = h.Service.GetOperatorSnapshot().CurrentRun!;
    var edited = h.Service.EditCurrentRun(new EditRunRequest
    {
        ExpectedRevision = current.Revision,
        Reason = "Manual station scoring",
        Events = [new EventEditRequest
        {
            EventId = "event-01",
            Status = EventStatus.Completed,
            StartElapsedMs = 0,
            FinishElapsedMs = 0,
            ScoreOverride = 77
        }]
    });
    Assert.Equal(77, edited.Events.Single(e => e.EventId == "event-01").Score);
}

static void BonusSignal()
{
    using var h = NewHarness();
    var run = h.ArmAndStart();
    CompleteAllEvents(h, run);
    var bonus = h.Service.GetOperatorSnapshot().CurrentRun!;
    Assert.Equal(RunPhase.Bonus, bonus.Phase);
    var result = h.Send(run, "master", "bonus-signal", "bonus-signal", bonus.ActiveElapsedMs, "{\"lights\":3}");
    Assert.Equal(MessageDisposition.Accepted, result.Disposition);
    Assert.Equal(0, result.Run!.BonusPoints);
    Assert.Equal(result.Run.Events.Sum(e => e.Score), result.Run.TotalPoints);
    Assert.Contains(h.Service.GetOperatorSnapshot().Messages, message => message.Type == "bonus-signal" && message.Disposition == MessageDisposition.Accepted);
}

static void RestartAndOfficialRule()
{
    using var h = NewHarness();
    var first = h.ArmAndStart();
    var finished = h.Service.Finish();
    Assert.Equal(RunStatus.Completed, finished.Status);
    var restartQueue = h.Service.Restart(first.Id);
    var replacement = h.Service.Arm(restartQueue.Id);
    Assert.Equal(first.Id, replacement.SupersedesRunId);
    var history = h.Service.GetOperatorSnapshot().History;
    Assert.Equal(RunStatus.Superseded, history.Single(r => r.Id == first.Id).Status);
    Assert.Equal(1, h.Service.GetOperatorSnapshot().Leaderboard.Count);

    h.Service.Finish();
    var secondOfficial = h.Service.AddToQueue(h.CompetitorId, RunCategory.Official);
    Assert.Throws<CommandException>(() => h.Service.Arm(secondOfficial.Id));
}

static void CategoryAndTieRank()
{
    using var h = NewHarness();
    var exhibition = h.ArmAndStart(RunCategory.Exhibition);
    h.Service.Finish();
    Assert.DoesNotContain(h.Service.GetScoreboard().Leaderboard, row => row.CompetitorName == h.CompetitorName);
    Assert.Equal(RunCategory.Exhibition, h.Service.GetScoreboard().CurrentRun!.Category);

    var second = h.AddCompetitor("Second competitor");
    var officialOne = h.Service.AddToQueue(h.CompetitorId, RunCategory.Official);
    var officialTwo = h.Service.AddToQueue(second.Id, RunCategory.Official);
    h.Service.Arm(officialOne.Id);
    h.Service.StartMaster();
    h.Service.Finish();
    h.Service.Arm(officialTwo.Id);
    h.Service.StartMaster();
    h.Service.Finish();
    var leaderboard = h.Service.GetScoreboard().Leaderboard;
    Assert.Equal(2, leaderboard.Count);
    Assert.True(leaderboard.All(row => row.Rank == 1));
}

static void RecoveryAndLock()
{
    var path = NewPath();
    var edition = MakeEdition();
    var clock = new TestClock();
    var store = new RunStore(path);
    var service = new RunService(store, edition, clock);
    var competitor = service.AddCompetitor("Recovery competitor");
    var queue = service.AddToQueue(competitor.Id, RunCategory.Official);
    service.Arm(queue.Id);
    service.StartMaster();
    clock.Advance(TimeSpan.FromSeconds(4));
    service.Checkpoint();
    Assert.Throws<InvalidOperationException>(() => new RunStore(path));
    Assert.Equal(RunStatus.Active, service.GetOperatorSnapshot().CurrentRun!.Status);
    store.Dispose();

    var recoveredStore = new RunStore(path);
    var recovered = new RunService(recoveredStore, edition, new TestClock());
    var recoveredRun = recovered.GetOperatorSnapshot().CurrentRun!;
    Assert.Equal(RunStatus.Paused, recoveredRun.Status);
    Assert.Equal(4_000L, recoveredRun.ActiveElapsedMs);
    Assert.Contains(recovered.GetOperatorSnapshot().Messages, message => message.Type == "recovery-paused");
    recoveredStore.Dispose();
    Cleanup(path);
}

static void EditsAndIsolation()
{
    using var h = NewHarness();
    var first = h.ArmAndStart();
    Assert.Equal(MessageDisposition.Accepted, h.Send(first, "station-01", "event-press", "history-start").Disposition);
    Assert.Equal(MessageDisposition.Accepted, h.Send(first, "station-01", "event-press", "history-finish").Disposition);
    var historical = h.Service.Finish();
    var second = h.AddCompetitor("Live competitor");
    var liveQueue = h.Service.AddToQueue(second.Id, RunCategory.Playoff);
    h.Service.Arm(liveQueue.Id);
    h.Service.StartMaster();
    var liveBeforeTick = h.Service.GetOperatorSnapshot().CurrentRun!;
    h.Clock.Advance(TimeSpan.FromSeconds(2));
    var liveAfterEdit = h.Service.EditCurrentRun(new EditRunRequest
    {
        ExpectedRevision = liveBeforeTick.Revision,
        Reason = "Manual live result",
        Events = [new EventEditRequest
        {
            EventId = "event-01",
            Status = EventStatus.Completed,
            StartElapsedMs = 0,
            FinishElapsedMs = 0,
            ScoreOverride = 66
        }]
    });
    Assert.Equal(liveAfterEdit.Id, h.Service.GetOperatorSnapshot().CurrentRun!.Id);
    Assert.Equal(2_000L, liveAfterEdit.ActiveElapsedMs);
    var liveEdit = h.Service.GetOperatorSnapshot().Edits.Single(e => e.RunId == liveAfterEdit.Id);
    h.Clock.Advance(TimeSpan.FromSeconds(1));
    var liveUndone = h.Service.UndoCurrentEdit(liveEdit.Id, liveAfterEdit.Revision, "Undo live correction");
    Assert.Equal(3_000L, liveUndone.ActiveElapsedMs);
    Assert.Equal(EventStatus.Pending, liveUndone.Events.Single(e => e.EventId == "event-01").Status);
    Assert.Equal(MessageDisposition.Accepted, h.Send(liveUndone, "station-01", "event-press", "live-device-after-undo", 3_000).Disposition);
    var liveUndoEdit = h.Service.GetOperatorSnapshot().Edits.Where(e => e.RunId == liveAfterEdit.Id).OrderByDescending(e => e.Id).First();
    Assert.Throws<CommandException>(() => h.Service.UndoCurrentEdit(liveUndoEdit.Id, h.Service.GetOperatorSnapshot().CurrentRun!.Revision, "Reject after device result"));

    var historyOpen = h.Service.GetOperatorSnapshot().History.Single(r => r.Id == historical.Id);
    var corrected = h.Service.EditHistoricalRun(historical.Id, new EditRunRequest
    {
        ExpectedRevision = historyOpen.Revision,
        Reason = "Corrected score",
        Events = [new EventEditRequest { EventId = "event-01", ScoreOverride = 55 }]
    });
    Assert.Equal(55, corrected.Events.Single(e => e.EventId == "event-01").Score);
    Assert.Equal(liveAfterEdit.Id, h.Service.GetOperatorSnapshot().CurrentRun!.Id);
    var edit = h.Service.GetOperatorSnapshot().Edits.Single(e => e.RunId == historical.Id);
    var undone = h.Service.UndoHistoricalEdit(historical.Id, edit.Id, corrected.Revision, "Undo score correction");
    Assert.Equal(100, undone.Events.Single(e => e.EventId == "event-01").Score);
    Assert.True(undone.Revision > corrected.Revision);

    var historyAgain = h.Service.GetOperatorSnapshot().History.Single(r => r.Id == historical.Id);
    var later = h.Service.EditHistoricalRun(historical.Id, new EditRunRequest
    {
        ExpectedRevision = historyAgain.Revision,
        Reason = "Another correction",
        Events = [new EventEditRequest { EventId = "event-01", ScoreOverride = 44 }]
    });
    var firstEdit = h.Service.GetOperatorSnapshot().Edits.Where(e => e.RunId == historical.Id).OrderBy(e => e.Id).First();
    Assert.Throws<CommandException>(() => h.Service.UndoHistoricalEdit(historical.Id, firstEdit.Id, later.Revision, "Must reject stale inverse"));
}

static void UnknownDatabase()
{
    var path = NewPath();
    Directory.CreateDirectory(path);
    using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(path, "garage-games-v2.db")}"))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE unrelated(value TEXT)";
        command.ExecuteNonQuery();
    }

    Assert.Throws<InvalidDataException>(() => new RunStore(path));
    Cleanup(path);
}

static void CompleteAllEvents(TestHarness h, RunRecord run)
{
    h.Send(run, "station-01", "event-press", "bonus-e1-start", 0);
    h.Send(run, "station-01", "event-press", "bonus-e1-finish", 0);
    h.Send(run, "station-02", "event-press", "bonus-key-start", 0);
    h.Send(run, "station-02", "keypad-success", "bonus-key-success", 0);
    h.Send(run, "station-03", "arcade-start", "bonus-arcade-start", 0);
    h.Send(run, "station-03", "arcade-finish", "bonus-arcade-finish", 0);
    h.Send(run, "station-04", "event-press", "bonus-e4-start", 0);
    h.Send(run, "station-04", "event-press", "bonus-e4-finish", 0);
}

static TestHarness NewHarness(int durationSeconds = 300) => new(MakeEdition(durationSeconds), NewPath());

static EditionDefinition MakeEdition(int durationSeconds = 300, string editionId = "test-edition") => new()
{
    EditionId = editionId,
    Name = "Test edition",
    DurationLimitSeconds = durationSeconds,
    Scoring = new ScoringRule(),
    Events =
    [
        new EventDefinition { EventId = "event-01", Name = "Event 1", DeviceId = "station-01", Type = EventKind.Standard },
        new EventDefinition { EventId = "event-02", Name = "Keypad", DeviceId = "station-02", Type = EventKind.Keypad, Prompt = "Demo prompt", Answer = "ok" },
        new EventDefinition { EventId = "event-03", Name = "Magnetic Arcade", DeviceId = "station-03", Type = EventKind.MagneticArcade },
        new EventDefinition { EventId = "event-04", Name = "Event 4", DeviceId = "station-04", Type = EventKind.Standard }
    ]
};

static EditionDefinition MakeMvpEdition(int durationSeconds = 300) => new()
{
    EditionId = "mvp-test-edition",
    Name = "MVP test edition",
    DurationLimitSeconds = durationSeconds,
    Scoring = new ScoringRule { ManualEventPoints = true },
    Events =
    [
        new EventDefinition { EventId = "event-01", Name = "Perfect Pour", DeviceId = "station-01", Type = EventKind.Standard },
        new EventDefinition { EventId = "event-02", Name = "Row Darts Redux", DeviceId = "station-02", Type = EventKind.Standard },
        new EventDefinition { EventId = "event-03", Name = "Monkey Business", DeviceId = "station-03", Type = EventKind.Standard },
        new EventDefinition { EventId = "event-04", Name = "PGA Jam", DeviceId = "station-04", Type = EventKind.Standard },
        new EventDefinition { EventId = "event-05", Name = "Measure Up", DeviceId = "station-05", Type = EventKind.Standard },
        new EventDefinition { EventId = "event-06", Name = "Paddle Peril", DeviceId = "station-06", Type = EventKind.Standard },
        new EventDefinition { EventId = "event-07", Name = "A Few Good Pints", DeviceId = "station-07", Type = EventKind.Standard },
        new EventDefinition { EventId = "event-08", Name = "Spice Slide", DeviceId = "station-08", Type = EventKind.Standard },
        new EventDefinition { EventId = "event-09", Name = "Hook, Line, and Winner", DeviceId = "station-09", Type = EventKind.Standard },
        new EventDefinition { EventId = "event-10", Name = "Top Shelf Shot", DeviceId = "station-10", Type = EventKind.Standard },
        new EventDefinition { EventId = "event-11", Name = "Bocce the Moon", DeviceId = "station-11", Type = EventKind.Standard },
        new EventDefinition { EventId = "event-12", Name = "Uphole Battle", DeviceId = "station-12", Type = EventKind.Standard },
        new EventDefinition { EventId = "event-13", Name = "Hammer Head", DeviceId = "station-13", Type = EventKind.Standard }
    ]
};

static string NewPath() => Path.Combine(Path.GetTempPath(), "GarageGamesV2Tests", Guid.NewGuid().ToString("N"));
static void Cleanup(string path)
{
    if (Directory.Exists(path))
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // SQLite may release a native handle after the test scope returns. The path is unique test data.
        }
    }
}

sealed class TestHarness : IDisposable
{
    public TestHarness(EditionDefinition edition, string path)
    {
        Path = path;
        Clock = new TestClock();
        Store = new RunStore(path);
        Service = new RunService(Store, edition, Clock);
        var competitor = Service.AddCompetitor("Primary competitor");
        CompetitorId = competitor.Id;
        CompetitorName = competitor.Name;
        Service.AddToQueue(competitor.Id, RunCategory.Official);
    }

    public string Path { get; }
    public TestClock Clock { get; }
    public RunStore Store { get; }
    public RunService Service { get; }
    public string CompetitorId { get; }
    public string CompetitorName { get; }

    public CompetitorRecord AddCompetitor(string name) => Service.AddCompetitor(name);

    public RunRecord ArmAndStart(RunCategory category = RunCategory.Official)
    {
        if (category != RunCategory.Official)
        {
            var currentQueue = Service.GetOperatorSnapshot().Queue;
            foreach (var item in currentQueue)
            {
                Service.RemoveFromQueue(item.Id);
            }
            Service.AddToQueue(CompetitorId, category);
        }
        var itemToArm = Service.GetOperatorSnapshot().Queue.First();
        var armed = Service.Arm(itemToArm.Id);
        return Service.StartMaster();
    }

    public InputResult Send(RunRecord run, string deviceId, string type, string messageId, long? elapsed = null, string payload = "{}")
    {
        using var document = JsonDocument.Parse(payload);
        return Service.Receive(new InputEnvelope
        {
            MessageId = messageId,
            SessionId = run.Id,
            RunId = run.Id,
            DeviceId = deviceId,
            Type = type,
            ElapsedMilliseconds = elapsed ?? Clock.MonotonicMilliseconds,
            Payload = document.RootElement.Clone()
        });
    }

    public void Dispose()
    {
        Store.Dispose();
        if (Directory.Exists(Path))
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // SQLite may release a native handle after the test scope returns. The path is unique test data.
            }
        }
    }
}

static class Assert
{
    public static void True(bool value, string? message = null)
    {
        if (!value) throw new InvalidOperationException(message ?? "Expected true.");
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    public static void Contains<T>(IEnumerable<T> values, Func<T, bool> predicate)
    {
        if (!values.Any(predicate)) throw new InvalidOperationException("Expected a matching item.");
    }

    public static void Contains<T>(IEnumerable<T> values, T expected)
    {
        if (!values.Contains(expected)) throw new InvalidOperationException($"Expected collection to contain '{expected}'.");
    }

    public static void DoesNotContain<T>(IEnumerable<T> values, Func<T, bool> predicate)
    {
        if (values.Any(predicate)) throw new InvalidOperationException("Expected no matching item.");
    }

    public static void Single<T>(IEnumerable<T> values)
    {
        if (values.Count() != 1) throw new InvalidOperationException("Expected exactly one item.");
    }

    public static TException Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
