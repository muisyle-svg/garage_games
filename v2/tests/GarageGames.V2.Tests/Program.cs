using GarageGames.V2;
using Microsoft.Data.Sqlite;
using System.Text.Json;

var tests = new (string Name, Action Run)[]
{
    ("automatic event-score cutoffs", ScoreBoundaries),
    ("per-event scoring parameters and grace boundaries", PerEventScoringBoundaries),
    ("per-event base points flow through live scoring, edits, history, and leaderboard", PerEventBasePoints),
    ("per-event scoring snapshots persist and version editions", PerEventScoringPersistenceAndVersioning),
    ("each per-event scoring parameter versions recorded editions", PerEventScoringVersioning),
    ("legacy config and snapshots keep their persisted global scoring floors", LegacyScoringFallback),
    ("pause freezes time and timeout precedence", PauseAndTimeout),
    ("physical master protocol validates boot tokens and SPEED interlock", MasterProtocolAndSpeedInterlock),
    ("physical spoke parser and handshake session are strict", PhysicalSpokeProtocolAndSession),
    ("physical spoke presses deduplicate across master boots and mix with virtual presses", PhysicalSpokeDedupesAndMixesWithVirtual),
    ("countdown freezes time and rejects input and run actions", CountdownFreezesTimeAndRejectsActions),
    ("countdown completion is durable, idempotent, and run-scoped", CountdownCompletionIsRunScopedAndIdempotent),
    ("countdown recovery waits for replayed audio", CountdownRecovery),
    ("physical master start is durable, deduplicated, and consumes its on-deck item", PhysicalMasterStartDurabilityAndQueue),
    ("physical master status follows run countdown", PhysicalMasterStatusCountdown),
    ("timeout autosaves and releases next competitor", MvpTimeoutAndNextRun),
    ("custom run duration snapshots timeout and preserves edition leaderboard identity", PerRunDurationSnapshot),
    ("MVP roster is 13 regular events with two-press virtual buttons", MvpRosterAndVirtualPresses),
    ("MVP timing fields clear and manual score overrides add to total", MvpEditableScorecard),
    ("recorded exhibition correction extends timeline and can be undone", RecordedExhibitionCorrectionTimeline),
    ("early-finished correction extends timeline while explicit and invalid times remain enforced", FinishedCorrectionTimeline),
    ("armed and paused current corrections remain within current elapsed time", UnfinishedCurrentCorrectionTimelineLimits),
    ("completed event scores and general bonus persist through historical edits", AutomatedScoreAndBonusPersistence),
    ("recording atomically promotes and persists the next on-deck competitor", RecordPromotesNextCompetitor),
    ("reordering on-deck queue persists the requested order", QueueReorderPersists),
    ("regular events automatically finish and freeze the clock until recorded", MvpAutoFinishAndRecord),
    ("completing a current scorecard correction automatically finishes", MvpCorrectionAutoFinish),
    ("finishing and recording are distinct and recording releases the next run", FinishIsIdempotent),
    ("duplicate and per-event stale input", DuplicateAndStale),
    ("keypad and arcade completion rules", SpecialCompletion),
    ("manual offline override remains optional and station packets stay guarded", ManualOverride),
    ("setup persists safely, versions changed rosters, and preserves run snapshots", SetupPersistenceAndRosterIsolation),
    ("setup is locked during an unrecorded run and validates device mappings", SetupLockAndValidation),
    ("device scan readiness distinguishes responding, missing, and unassigned stations", DeviceScanReadiness),
    ("device scan protocol accepts only correlated 12-hex node replies", DeviceScanProtocol),
    ("trusted virtual presses work with unverified hardware while station packets stay guarded", VirtualPressReadinessFallback),
    ("bonus records signals without automatic points", BonusSignal),
    ("restart lineage and one official result", RestartAndOfficialRule),
    ("category exclusion and shared tie rank", CategoryAndTieRank),
    ("persistent recovery and exclusive data lock", RecoveryAndLock),
    ("danger-zone clear backs up first and resets persisted and runtime state", ClearDatabaseSafety),
    ("failed backup prevents database clearing", ClearDatabaseBackupFailurePreservesData),
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
    Assert.Equal(50, Score(4_990, rule));
    Assert.Equal(50, Score(4_999, rule));
    Assert.Equal(45, Score(5_000, rule));
    Assert.Equal(40, Score(10_000, rule));
    Assert.Equal(25, Score(25_000, rule));

    Assert.Equal(40, Score(0, rule, 40));
    Assert.Equal(35, Score(5_000, rule, 40));
    Assert.Equal(20, Score(30_000, rule, 40));
    Assert.Equal(26, Score(25_000, rule, 51));
    Assert.Equal(26, Score(30_000, rule, 51));
    Assert.Equal(0, Score(30_000, rule, 0));

    var manual = new EventRecord
    {
        EventId = "e",
        Name = "e",
        DeviceId = "d",
        Status = EventStatus.Completed,
        StartElapsedMs = 0,
        FinishElapsedMs = 30_000,
        ScoreOverride = 77
    };
    Assert.Equal(77, ScoreCalculator.Calculate(manual, rule, 40));

    static int Score(long duration, ScoringRule rule, int? basePoints = null) => ScoreCalculator.Calculate(new EventRecord
    {
        EventId = "e",
        Name = "e",
        DeviceId = "d",
        Status = EventStatus.Completed,
        StartElapsedMs = 0,
        FinishElapsedMs = duration
    }, rule, basePoints);
}

static void PerEventScoringBoundaries()
{
    var rule = new ScoringRule();
    var eventSnapshot = new EventSnapshot
    {
        EventId = "e",
        Name = "e",
        DeviceId = "d",
        BasePoints = 100,
        DecayPoints = 10,
        DecayEverySeconds = 5,
        GraceSeconds = 10
    };

    Assert.Equal(100, Score(9_999, eventSnapshot));
    Assert.Equal(90, Score(10_000, eventSnapshot));
    Assert.Equal(90, Score(14_999, eventSnapshot));
    Assert.Equal(80, Score(15_000, eventSnapshot));
    Assert.Equal(70, Score(20_000, eventSnapshot));
    Assert.Equal(60, Score(25_000, eventSnapshot));
    Assert.Equal(50, Score(long.MaxValue, eventSnapshot));

    eventSnapshot.MinimumPoints = 0;
    Assert.Equal(0, Score(long.MaxValue, eventSnapshot));
    eventSnapshot.BasePoints = 51;
    eventSnapshot.MinimumPoints = null;
    Assert.Equal(26, Score(long.MaxValue, eventSnapshot));
    eventSnapshot.BasePoints = 0;
    eventSnapshot.MinimumPoints = 0;
    eventSnapshot.DecayPoints = 0;
    eventSnapshot.DecayEverySeconds = 1;
    eventSnapshot.GraceSeconds = 0;
    Assert.Equal(0, Score(long.MaxValue, eventSnapshot));

    var inherited = new EventSnapshot { EventId = "e", Name = "e", DeviceId = "d" };
    Assert.Equal(45, Score(5_000, inherited));
    Assert.Equal(25, Score(30_000, inherited));
    var zeroGrace = new EventSnapshot
    {
        EventId = "e",
        Name = "e",
        DeviceId = "d",
        BasePoints = 100,
        DecayPoints = 10,
        DecayEverySeconds = 5,
        GraceSeconds = 0
    };
    Assert.Equal(100, Score(0, zeroGrace));
    Assert.Equal(100, Score(4_999, zeroGrace));
    Assert.Equal(90, Score(5_000, zeroGrace));

    int Score(long duration, EventSnapshot snapshot) => ScoreCalculator.CalculateForEvent(new EventRecord
    {
        EventId = "e",
        Name = "e",
        DeviceId = "d",
        Status = EventStatus.Completed,
        StartElapsedMs = 0,
        FinishElapsedMs = duration
    }, rule, snapshot);
}

static void PerEventBasePoints()
{
    var edition = MakeMvpEdition();
    edition.Events[0].BasePoints = 40;
    edition.Events[1].BasePoints = 51;
    using var h = new TestHarness(edition, NewPath());
    var run = h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Official);
    h.StartRun();

    h.Service.PressEvent(run.Id, "event-01");
    h.Service.PressEvent(run.Id, "event-02");
    h.Clock.Advance(TimeSpan.FromSeconds(5));
    var completed = h.Service.PressEvent(run.Id, "event-01").Run!;
    Assert.Equal(35, completed.Events.Single(e => e.EventId == "event-01").Score);

    h.Clock.Advance(TimeSpan.FromSeconds(20));
    completed = h.Service.PressEvent(run.Id, "event-02").Run!;
    Assert.Equal(26, completed.Events.Single(e => e.EventId == "event-02").Score);
    Assert.Equal(40, completed.Edition.Events.Single(e => e.EventId == "event-01").BasePoints);
    Assert.Equal(51, completed.Edition.Events.Single(e => e.EventId == "event-02").BasePoints);

    var edited = h.Service.EditCurrentRun(new EditRunRequest
    {
        ExpectedRevision = completed.Revision,
        Reason = "Apply a manual score while correcting timing",
        Events = [new EventEditRequest { EventId = "event-01", FinishElapsedMs = 15_000, ScoreOverride = 77 }]
    });
    Assert.Equal(77, edited.Events.Single(e => e.EventId == "event-01").Score);

    edited = h.Service.EditCurrentRun(new EditRunRequest
    {
        ExpectedRevision = edited.Revision,
        Reason = "Clear the manual score and correct timing",
        Events = [new EventEditRequest { EventId = "event-01", FinishElapsedMs = 10_000, ClearScoreOverride = true }]
    });
    Assert.Equal(30, edited.Events.Single(e => e.EventId == "event-01").Score);

    var finished = h.Service.Finish();
    Assert.Equal(30, finished.Events.Single(e => e.EventId == "event-01").Score);
    Assert.Equal(26, finished.Events.Single(e => e.EventId == "event-02").Score);
    var recorded = h.Service.Record();
    Assert.Equal(56, recorded.TotalPoints);
    Assert.Equal(56, h.Service.GetOperatorSnapshot().Leaderboard.Single().Points);
    var persistedEditionSnapshot = h.Store.Load().Runs.Single(item => item.Id == recorded.Id).Edition;
    Assert.Equal(40, persistedEditionSnapshot.Events.Single(e => e.EventId == "event-01").BasePoints);
    Assert.Equal(51, persistedEditionSnapshot.Events.Single(e => e.EventId == "event-02").BasePoints);

    var setup = h.Service.GetSetup();
    setup.Events[0].BasePoints = 80;
    var changedSetup = h.Service.UpdateSetup(setup);
    Assert.True(changedSetup.EditionId != recorded.EditionId);
    Assert.Equal(80, changedSetup.Events[0].BasePoints);

    var correctedHistory = h.Service.EditHistoricalRun(recorded.Id, new EditRunRequest
    {
        ExpectedRevision = recorded.Revision,
        Reason = "Correct historical timing using the saved edition rules",
        Events = [new EventEditRequest { EventId = "event-01", FinishElapsedMs = 5_000 }]
    });
    Assert.Equal(35, correctedHistory.Events.Single(e => e.EventId == "event-01").Score);
    Assert.Equal(40, correctedHistory.Edition.Events.Single(e => e.EventId == "event-01").BasePoints);
    Assert.Equal(0, h.Service.GetOperatorSnapshot().Leaderboard.Count);
}

static void PerEventScoringPersistenceAndVersioning()
{
    var edition = MakeEdition();
    edition.Events[0].BasePoints = 60;
    edition.Events[0].MinimumPoints = 11;
    edition.Events[0].DecayPoints = 7;
    edition.Events[0].DecayEverySeconds = 2;
    edition.Events[0].GraceSeconds = 3;
    using var h = new TestHarness(edition, NewPath());
    var run = h.ArmAndStart();
    h.Service.PressEvent(run.Id, "event-01");
    h.Clock.Advance(TimeSpan.FromSeconds(7));
    var completed = h.Service.PressEvent(run.Id, "event-01").Run!;
    Assert.Equal(39, completed.Events.Single(item => item.EventId == "event-01").Score);
    Assert.Equal(11, completed.Edition.Events[0].MinimumPoints);
    Assert.Equal(7, completed.Edition.Events[0].DecayPoints);
    Assert.Equal(2, completed.Edition.Events[0].DecayEverySeconds);
    Assert.Equal(3, completed.Edition.Events[0].GraceSeconds);

    var edited = h.Service.EditCurrentRun(new EditRunRequest
    {
        ExpectedRevision = completed.Revision,
        Reason = "Check event-specific grace and decay after a timing correction",
        Events = [new EventEditRequest { EventId = "event-01", FinishElapsedMs = 5_000 }]
    });
    Assert.Equal(46, edited.Events.Single(item => item.EventId == "event-01").Score);
    h.Service.Finish();
    var recorded = h.Service.Record();
    var savedRun = h.Store.Load().Runs.Single(item => item.Id == recorded.Id);
    Assert.Equal(60, savedRun.Edition.Events[0].BasePoints);
    Assert.Equal(11, savedRun.Edition.Events[0].MinimumPoints);
    Assert.Equal(7, savedRun.Edition.Events[0].DecayPoints);
    Assert.Equal(2, savedRun.Edition.Events[0].DecayEverySeconds);
    Assert.Equal(3, savedRun.Edition.Events[0].GraceSeconds);

    var setup = h.Service.GetSetup();
    var priorEditionId = setup.EditionId;
    setup.Events[0].MinimumPoints = 10;
    var changed = h.Service.UpdateSetup(setup);
    Assert.True(changed.EditionId != priorEditionId, "Changing one event scoring field after a result must create a new edition.");
    var historical = h.Service.GetOperatorSnapshot().History.Single(item => item.Id == recorded.Id);
    Assert.Equal(11, historical.Edition.Events[0].MinimumPoints);
    Assert.Equal(46, historical.Events.Single(item => item.EventId == "event-01").Score);
    var correctedHistory = h.Service.EditHistoricalRun(recorded.Id, new EditRunRequest
    {
        ExpectedRevision = historical.Revision,
        Reason = "Verify historical correction keeps the original event scoring parameters",
        Events = [new EventEditRequest { EventId = "event-01", FinishElapsedMs = 7_000 }]
    });
    Assert.Equal(39, correctedHistory.Events.Single(item => item.EventId == "event-01").Score);
    Assert.Equal(11, correctedHistory.Edition.Events[0].MinimumPoints);

    var setupJsonWithoutScoring = JsonSerializer.Deserialize<EditionSetup>("""
        {"editionId":"compat","name":"Compatibility","events":[]}
        """, JsonDefaults.Options)!;
    Assert.Equal(null, setupJsonWithoutScoring.Scoring);
    Assert.Equal(50, changed.Scoring!.BasePoints);
    Assert.Equal(25, changed.Scoring.MinimumPoints);
    var requestWithGlobalScoring = h.Service.GetSetup();
    requestWithGlobalScoring.Scoring = new ScoringRule { BasePoints = 900, MinimumPoints = 1, DecayPoints = 0, DecayEverySeconds = 1 };
    var ignoredGlobalScoring = h.Service.UpdateSetup(requestWithGlobalScoring);
    Assert.Equal(50, ignoredGlobalScoring.Scoring!.BasePoints);
    Assert.Equal(25, ignoredGlobalScoring.Scoring.MinimumPoints);
    Assert.Equal(5, ignoredGlobalScoring.Scoring.DecayPoints);
    Assert.Equal(5, ignoredGlobalScoring.Scoring.DecayEverySeconds);
}

static void PerEventScoringVersioning()
{
    var changes = new (string Name, Action<EventDefinition> Apply)[]
    {
        ("base points", item => item.BasePoints = 55),
        ("minimum points", item => item.MinimumPoints = 20),
        ("decay points", item => item.DecayPoints = 4),
        ("decay interval", item => item.DecayEverySeconds = 3),
        ("grace period", item => item.GraceSeconds = 2)
    };

    foreach (var change in changes)
    {
        using var h = NewHarness();
        var run = h.ArmAndStart();
        h.Service.PressEvent(run.Id, "event-01");
        h.Service.PressEvent(run.Id, "event-01");
        h.Service.Finish();
        var recorded = h.Service.Record();

        var setup = h.Service.GetSetup();
        change.Apply(setup.Events[0]);
        var updated = h.Service.UpdateSetup(setup);
        Assert.True(updated.EditionId != recorded.EditionId,
            $"Changing event {change.Name} after a recorded run must create a new edition.");
        Assert.Equal(recorded.EditionId, h.Service.GetOperatorSnapshot().History.Single(item => item.Id == recorded.Id).EditionId);
    }
}

static void LegacyScoringFallback()
{
    var oldConfig = JsonSerializer.Deserialize<EditionDefinition>("""
        {"editionId":"legacy-config","name":"Legacy config","events":[{"eventId":"event-01","name":"Old event","deviceId":"station-01"}]}
        """, JsonDefaults.Options)!;
    EditionDefinition.Validate(oldConfig, "legacy test config");
    var configSnapshot = oldConfig.ToSnapshot();
    Assert.Equal(null, configSnapshot.Events.Single().BasePoints);
    Assert.Equal(50, configSnapshot.Scoring.BasePoints);
    Assert.Equal(25, configSnapshot.Scoring.MinimumPoints);
    Assert.Equal(25, ScoreCalculator.Calculate(CompletedEvent(30_000), configSnapshot.Scoring,
        configSnapshot.Events.Single().BasePoints));

    var oldSnapshot = JsonSerializer.Deserialize<EditionSnapshot>("""
        {"editionId":"legacy-snapshot","name":"Legacy snapshot","durationLimitSeconds":300,"scoring":{"basePoints":43,"decayPoints":5,"decayEverySeconds":5,"minimumPoints":17},"events":[{"eventId":"event-01","name":"Old event","deviceId":"station-01","type":"standard"}]}
        """, JsonDefaults.Options)!;
    Assert.Equal(null, oldSnapshot.Events.Single().BasePoints);
    Assert.Equal(17, ScoreCalculator.Calculate(CompletedEvent(30_000), oldSnapshot.Scoring,
        oldSnapshot.Events.Single().BasePoints));

    var explicitZero = JsonSerializer.Deserialize<EventDefinition>("""
        {"eventId":"event-01","name":"Zero event","deviceId":"station-01","basePoints":0}
        """, JsonDefaults.Options)!;
    Assert.Equal(0, explicitZero.BasePoints);
    Assert.True(JsonSerializer.Serialize(explicitZero, JsonDefaults.Options).Contains("\"basePoints\":0", StringComparison.Ordinal));

    static EventRecord CompletedEvent(long duration) => new()
    {
        EventId = "event-01",
        Name = "Old event",
        DeviceId = "station-01",
        Status = EventStatus.Completed,
        StartElapsedMs = 0,
        FinishElapsedMs = duration
    };
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

static void MasterProtocolAndSpeedInterlock()
{
    var protocol = new MasterProtocolState();
    var starts = new List<(string BootToken, ulong Sequence, bool Allowed)>();
    InputResult Receive(string bootToken, ulong sequence, bool allowed)
    {
        starts.Add((bootToken, sequence, allowed));
        return new InputResult(MessageDisposition.Accepted, "test", null);
    }

    Assert.Equal(false, protocol.ProcessLine("serial debug: ready", Receive));
    Assert.Equal(false, protocol.ProcessLine("GG1 HELLO token/invalid", Receive));
    Assert.Equal(true, protocol.ProcessLine("GG1 HELLO boot_A-1", Receive));
    Assert.Equal(true, protocol.ProcessLine("GG1 START older_boot 4", Receive));
    Assert.Equal(0, starts.Count);
    Assert.Equal(true, protocol.ProcessLine("GG1 MODE SPEED", Receive));
    Assert.Equal(MasterMode.Speed, protocol.Mode);
    Assert.Throws<CommandException>(protocol.EnsureArmAllowed);
    Assert.Equal(true, protocol.ProcessLine("GG1 START boot_A-1 5", Receive));
    Assert.Equal(("boot_A-1", 5UL, false), starts.Single());
    Assert.Equal("GG1 START boot_A-1 5", protocol.LastMessage);
    Assert.Equal(false, protocol.ProcessLine($"GG1 START boot_A-1 {new string('9', 140)}", Receive));
    Assert.Equal(true, protocol.ProcessLine("GG1 MODE IDLE", Receive));
    protocol.EnsureArmAllowed();
    Assert.Equal("GG1 STATUS ACTIVE 12", MasterProtocolCodec.FormatStatus(new MasterRunStatus("ACTIVE", 12)));

    using var h = NewHarness();
    var queueId = h.Service.GetOperatorSnapshot().Queue.Single().Id;
    h.Service.Arm(queueId);
    var speedInterlock = new MasterProtocolState();
    speedInterlock.ProcessLine("GG1 HELLO speed-test", Receive);
    speedInterlock.ProcessLine("GG1 MODE SPEED", Receive);
    InputResult? physicalResult = null;
    speedInterlock.ProcessLine("GG1 START speed-test 1", (boot, sequence, allowed) =>
    {
        physicalResult = h.Service.ReceivePhysicalMasterStart(boot, sequence, allowed);
        return physicalResult;
    });
    Assert.Equal(MessageDisposition.InvalidSignal, physicalResult!.Disposition);
    Assert.Equal(RunStatus.Armed, h.Service.GetOperatorSnapshot().CurrentRun!.Status);
    Assert.Throws<CommandException>(() => speedInterlock.EnsureArmAllowed());
    speedInterlock.ProcessLine("GG1 MODE IDLE", Receive);
    speedInterlock.EnsureArmAllowed();
    Assert.Equal(RunStatus.Active, h.StartRun().Status);
}

static void PhysicalSpokeProtocolAndSession()
{
    Assert.Equal("0011223344556677",
        MasterProtocolCodec.GetGarageRunToken("run-00112233445566778899AABBCCDDEEFF"));
    Assert.True(MasterProtocolCodec.TryParsePhysicalPress(
        "GG1 PRESS boot-A_1 0011223344556677 AABBCCDDEEFF 42", out var press));
    Assert.Equal("boot-A_1", press.BootToken);
    Assert.Equal("0011223344556677", press.RunToken);
    Assert.Equal("AABBCCDDEEFF", press.DeviceId);
    Assert.Equal(42u, press.Sequence);
    var retriedAfterMasterReboot = press with { BootToken = "boot-B" };
    Assert.Equal(MasterProtocolCodec.GetPhysicalPressMessageId(press.RunToken, press.DeviceId, press.Sequence),
        MasterProtocolCodec.GetPhysicalPressMessageId(retriedAfterMasterReboot.RunToken,
            retriedAfterMasterReboot.DeviceId, retriedAfterMasterReboot.Sequence));
    Assert.True(!MasterProtocolCodec.TryParsePhysicalPress(
        "GG1  PRESS boot-A_1 0011223344556677 AABBCCDDEEFF 42", out _));
    Assert.True(!MasterProtocolCodec.TryParsePhysicalPress(
        "GG1 PRESS boot-A_1 001122334455667g AABBCCDDEEFF 42", out _));
    Assert.True(!MasterProtocolCodec.TryParsePhysicalPress(
        "GG1 PRESS boot-A_1 0011223344556677 aabbccddeeff 42", out _));
    Assert.True(!MasterProtocolCodec.TryParsePhysicalPress(
        "GG1 PRESS boot-A_1 0011223344556677 AABBCCDDEEFF 0", out _));
    Assert.True(!MasterProtocolCodec.TryParsePhysicalPress(
        "GG1 PRESS boot-A_1 0011223344556677 AABBCCDDEEFF 4294967296", out _));

    var protocol = new MasterProtocolState();
    var allowedValues = new List<bool>();
    InputResult ReceiveStart(string _, ulong __, bool ___) =>
        new(MessageDisposition.Accepted, "test", null);
    MasterPhysicalPressResult ReceivePress(MasterPhysicalPress _, bool allowed)
    {
        allowedValues.Add(allowed);
        return new MasterPhysicalPressResult("REJECTED", MessageDisposition.InvalidSignal, "test");
    }

    Assert.True(protocol.ProcessLine("GG1 HELLO boot-A_1", ReceiveStart, ReceivePress));
    Assert.True(protocol.ProcessLine("GG1 PRESS boot-A_1 0011223344556677 AABBCCDDEEFF 42", ReceiveStart, ReceivePress));
    Assert.Equal(false, allowedValues[^1]); // MODE must be explicitly IDLE.
    Assert.True(protocol.ProcessLine("GG1 MODE SPEED", ReceiveStart, ReceivePress));
    protocol.ProcessLine("GG1 PRESS boot-A_1 0011223344556677 AABBCCDDEEFF 43", ReceiveStart, ReceivePress);
    Assert.Equal(false, allowedValues[^1]);
    Assert.True(protocol.ProcessLine("GG1 MODE IDLE", ReceiveStart, ReceivePress));
    protocol.ProcessLine("GG1 PRESS old-boot 0011223344556677 AABBCCDDEEFF 44", ReceiveStart, ReceivePress);
    Assert.Equal(false, allowedValues[^1]);
    protocol.ProcessLine("GG1 HELLO boot-after-restart", ReceiveStart, ReceivePress);
    protocol.ProcessLine("GG1 PRESS boot-after-restart 0011223344556677 AABBCCDDEEFF 45", ReceiveStart, ReceivePress);
    Assert.Equal(false, allowedValues[^1]); // A new handshake must report its mode.
    protocol.ProcessLine("GG1 MODE IDLE", ReceiveStart, ReceivePress);
    protocol.ProcessLine("GG1 PRESS boot-after-restart 0011223344556677 AABBCCDDEEFF 46", ReceiveStart, ReceivePress);
    Assert.Equal(true, allowedValues[^1]);
    Assert.Equal("GG1 RESULT 0011223344556677 AABBCCDDEEFF 42 ACTIVE",
        MasterProtocolCodec.FormatPhysicalPressResult(press, "ACTIVE"));
}

static void PhysicalSpokeDedupesAndMixesWithVirtual()
{
    const string firstMac = "AABBCCDDEEFF";
    const string secondMac = "001122334455";
    using var h = new TestHarness(MakeSpokeEdition(), NewPath(), simulatedDevicesOnline: false);
    h.Service.RecordDeviceScan(true, true, [firstMac, secondMac]);
    Assert.Equal("-", h.Service.GetGarageStatus().Token);

    var run = h.Service.Arm(h.Service.GetOperatorSnapshot().Queue.Single().Id);
    var countdown = h.Service.StartMaster();
    var runToken = MasterProtocolCodec.GetGarageRunToken(run.Id)!;
    Assert.Equal(runToken, h.Service.GetGarageStatus().Token);
    Assert.Equal("COUNTDOWN", h.Service.GetGarageStatus().State);
    Assert.Equal($"GG1 GARAGE {runToken} COUNTDOWN",
        MasterProtocolCodec.FormatGarageStatus(h.Service.GetGarageStatus()));

    var protocol = new MasterProtocolState();
    InputResult ReceiveStart(string boot, ulong sequence, bool allowed) =>
        h.Service.ReceivePhysicalMasterStart(boot, sequence, allowed);
    MasterPhysicalPressResult? received = null;
    MasterPhysicalPressResult ReceivePress(MasterPhysicalPress item, bool allowed) =>
        received = h.Service.ReceivePhysicalSpokePress(item, allowed);

    protocol.ProcessLine("GG1 HELLO boot-first", ReceiveStart, ReceivePress);
    protocol.ProcessLine("GG1 MODE IDLE", ReceiveStart, ReceivePress);
    protocol.ProcessLine($"GG1 PRESS boot-first {runToken} {firstMac} 1", ReceiveStart, ReceivePress);
    Assert.Equal("REJECTED", received!.State); // Countdown is not gameplay-active.

    h.Service.CompleteCountdown(countdown.Id);
    protocol.ProcessLine($"GG1 PRESS boot-first FFFFFFFFFFFFFFFF {firstMac} 2", ReceiveStart, ReceivePress);
    Assert.Equal(MessageDisposition.WrongRun, received!.Disposition);
    h.Service.RecordDeviceScan(true, true, [secondMac]);
    protocol.ProcessLine($"GG1 PRESS boot-first {runToken} {firstMac} 3", ReceiveStart, ReceivePress);
    Assert.Equal(MessageDisposition.Offline, received!.Disposition);
    h.Service.RecordDeviceScan(true, true, [firstMac, secondMac]);
    protocol.ProcessLine($"GG1 PRESS boot-first {runToken} {firstMac} 4", ReceiveStart, ReceivePress);
    Assert.Equal(MessageDisposition.Accepted, received!.Disposition);
    Assert.Equal("ACTIVE", received.State);
    Assert.Equal<long?>(0L, h.Service.GetOperatorSnapshot().CurrentRun!.Events.Single(e => e.DeviceId == firstMac).StartElapsedMs);

    h.Clock.Advance(TimeSpan.FromMilliseconds(1_300));
    protocol.Reset();
    protocol.ProcessLine("GG1 HELLO boot-after-restart", ReceiveStart, ReceivePress);
    protocol.ProcessLine("GG1 MODE IDLE", ReceiveStart, ReceivePress);
    protocol.ProcessLine($"GG1 PRESS boot-after-restart {runToken} {firstMac} 4", ReceiveStart, ReceivePress);
    Assert.Equal(MessageDisposition.Duplicate, received!.Disposition);
    Assert.Equal("ACTIVE", received.State); // The retransmission did not finish the event.
    Assert.Equal<long?>(null, h.Service.GetOperatorSnapshot().CurrentRun!.Events.Single(e => e.DeviceId == firstMac).FinishElapsedMs);

    h.Service.Pause();
    protocol.ProcessLine($"GG1 PRESS boot-after-restart {runToken} {firstMac} 5", ReceiveStart, ReceivePress);
    Assert.Equal(MessageDisposition.Paused, received!.Disposition);
    h.Service.Resume();

    protocol.ProcessLine($"GG1 PRESS boot-after-restart {runToken} FFFFFFFFFFFF 6", ReceiveStart, ReceivePress);
    Assert.Equal(MessageDisposition.UnknownStation, received!.Disposition);
    h.Service.PressEvent(run.Id, "spoke-event-02");
    h.Service.PressEvent(run.Id, "spoke-event-02");
    h.Clock.Advance(TimeSpan.FromMilliseconds(700));
    protocol.ProcessLine($"GG1 PRESS boot-after-restart {runToken} {firstMac} 7", ReceiveStart, ReceivePress);
    Assert.Equal(MessageDisposition.Accepted, received!.Disposition);
    Assert.Equal("COMPLETED", received.State);

    var finished = h.Service.GetOperatorSnapshot().CurrentRun!;
    Assert.Equal(RunStatus.Finished, finished.Status);
    Assert.Equal<long?>(2_000L, finished.Events.Single(e => e.DeviceId == firstMac).FinishElapsedMs);
    Assert.Equal("FINISHED", h.Service.GetGarageStatus().State);
    Assert.Equal(runToken, h.Service.GetGarageStatus().Token);
    protocol.ProcessLine($"GG1 PRESS boot-after-restart {runToken} {firstMac} 4", ReceiveStart, ReceivePress);
    Assert.Equal(MessageDisposition.Duplicate, received!.Disposition);
    Assert.Equal("COMPLETED", received.State);
}

static EditionDefinition MakeSpokeEdition() => new()
{
    EditionId = "spoke-test-edition",
    Name = "Physical spoke test edition",
    DurationLimitSeconds = 30,
    Scoring = new ScoringRule(),
    Events =
    [
        new EventDefinition { EventId = "spoke-event-01", Name = "Spoke event 1", DeviceId = "AABBCCDDEEFF", Type = EventKind.Standard },
        new EventDefinition { EventId = "spoke-event-02", Name = "Spoke event 2", DeviceId = "001122334455", Type = EventKind.Standard }
    ]
};

static void CountdownFreezesTimeAndRejectsActions()
{
    using var h = NewHarness(durationSeconds: 20);
    h.Service.Arm(h.Service.GetOperatorSnapshot().Queue.Single().Id);
    var countdown = h.Service.StartMaster();
    Assert.Equal(RunStatus.Countdown, countdown.Status);
    Assert.Equal(null, countdown.StartedAt);
    Assert.Equal(RunStatus.Countdown, h.Service.GetCountdownState().Status);
    Assert.Equal($"{{\"runId\":\"{countdown.Id}\",\"status\":\"countdown\"}}",
        JsonSerializer.Serialize(h.Service.GetCountdownState(), JsonDefaults.Options));
    Assert.True(h.Service.GetOperatorSnapshot().Devices.All(device => device.Led == LedState.Countdown));

    h.Clock.Advance(TimeSpan.FromSeconds(40));
    var stillCounting = h.Service.GetOperatorSnapshot().CurrentRun!;
    Assert.Equal(RunStatus.Countdown, stillCounting.Status);
    Assert.Equal(0L, stillCounting.ActiveElapsedMs);
    Assert.Equal(null, stillCounting.StartedAt);
    Assert.Equal(new MasterRunStatus("COUNTDOWN", 20), h.Service.GetMasterStatus());
    Assert.Equal(MessageDisposition.InvalidSignal, h.Service.PressEvent(countdown.Id, "event-01").Disposition);
    Assert.Equal(MessageDisposition.InvalidSignal, h.Send(countdown, "station-02", "keypad-success", "countdown-keypad").Disposition);
    Assert.Equal(MessageDisposition.InvalidSignal, h.Send(countdown, "station-03", "arcade-start", "countdown-arcade").Disposition);
    Assert.True(h.Service.GetOperatorSnapshot().CurrentRun!.Events.All(result => result.Status == EventStatus.Pending));

    Assert.Throws<CommandException>(() => h.Service.Finish());
    Assert.Throws<CommandException>(() => h.Service.Record());
    Assert.Throws<CommandException>(() => h.Service.EditCurrentRun(new EditRunRequest
    {
        ExpectedRevision = stillCounting.Revision,
        Reason = "Must wait until countdown ends",
        Notes = "Not allowed yet"
    }));
    Assert.Equal(RunStatus.Aborted, h.Service.Abort().Status);
    Assert.Equal("{\"runId\":null,\"status\":null}",
        JsonSerializer.Serialize(h.Service.GetCountdownState(), JsonDefaults.Options));
}

static void CountdownCompletionIsRunScopedAndIdempotent()
{
    using var h = NewHarness(durationSeconds: 20);
    h.Service.Arm(h.Service.GetOperatorSnapshot().Queue.Single().Id);
    var countdown = h.Service.StartMaster();
    h.Clock.Advance(TimeSpan.FromSeconds(8));
    Assert.Throws<CommandException>(() => h.Service.CompleteCountdown("another-run"));
    Assert.Equal(RunStatus.Countdown, h.Service.GetOperatorSnapshot().CurrentRun!.Status);

    var active = h.Service.CompleteCountdown(countdown.Id);
    Assert.Equal(RunStatus.Active, active.Status);
    Assert.Equal(h.Clock.UtcNow, active.StartedAt);
    Assert.Equal(0L, active.ActiveElapsedMs);
    var revision = active.Revision;
    var repeated = h.Service.CompleteCountdown(countdown.Id);
    Assert.Equal(RunStatus.Active, repeated.Status);
    Assert.Equal(revision, repeated.Revision);
    Assert.Equal(active.StartedAt, repeated.StartedAt);
    Assert.Throws<CommandException>(() => h.Service.StartMaster());

    h.Clock.Advance(TimeSpan.FromSeconds(2));
    Assert.Equal(2_000L, h.Service.GetOperatorSnapshot().CurrentRun!.ActiveElapsedMs);
}

static void CountdownRecovery()
{
    var path = NewPath();
    var edition = MakeEdition();
    RunStore? firstStore = null;
    try
    {
        var firstClock = new TestClock();
        firstStore = new RunStore(path);
        var firstService = new RunService(firstStore, edition, firstClock);
        var competitor = firstService.AddCompetitor("Countdown recovery competitor");
        var queue = firstService.AddToQueue(competitor.Id, RunCategory.Official);
        firstService.Arm(queue.Id);
        var countdown = firstService.StartMaster();
        firstClock.Advance(TimeSpan.FromSeconds(37));
        firstService.Checkpoint();
        firstStore.Dispose();
        firstStore = null;

        var recoveredClock = new TestClock();
        using var recoveredStore = new RunStore(path);
        var recovered = new RunService(recoveredStore, edition, recoveredClock);
        var state = recovered.GetCountdownState();
        Assert.Equal(countdown.Id, state.RunId);
        Assert.Equal(RunStatus.Countdown, state.Status);
        var recoveredRun = recovered.GetOperatorSnapshot().CurrentRun!;
        Assert.Equal(0L, recoveredRun.ActiveElapsedMs);
        Assert.Equal(null, recoveredRun.StartedAt);
        Assert.DoesNotContain(recovered.GetOperatorSnapshot().Messages, message => message.Type == "recovery-paused");
        Assert.Equal(RunStatus.Active, recovered.CompleteCountdown(countdown.Id).Status);
        Assert.Equal(recoveredClock.UtcNow, recovered.GetOperatorSnapshot().CurrentRun!.StartedAt);
    }
    finally
    {
        firstStore?.Dispose();
        Cleanup(path);
    }
}

static void PhysicalMasterStartDurabilityAndQueue()
{
    var path = NewPath();
    var queueId = "";
    try
    {
        using (var store = new RunStore(path))
        {
            var clock = new TestClock();
            var service = new RunService(store, MakeEdition(), clock);
            var competitor = service.AddCompetitor("Physical master competitor");
            queueId = service.AddToQueue(competitor.Id, RunCategory.Official).Id;
            var nextCompetitor = service.AddCompetitor("Next on-deck competitor");
            service.AddToQueue(nextCompetitor.Id, RunCategory.Official);
            service.ArmCompetitor(competitor.Id, RunCategory.Official);

            var accepted = service.ReceivePhysicalMasterStart("boot-token-1", 8, startAllowed: true);
            Assert.Equal(MessageDisposition.Accepted, accepted.Disposition);
            Assert.Equal(RunStatus.Countdown, accepted.Run!.Status);
            Assert.Equal(null, accepted.Run.StartedAt);
            Assert.Equal(new MasterRunStatus("COUNTDOWN", 300), service.GetMasterStatus());
            Assert.Equal(1, service.GetOperatorSnapshot().Queue.Count);
            Assert.Equal(nextCompetitor.Id, service.GetOperatorSnapshot().Queue.Single().CompetitorId);
            Assert.Equal(RunStatus.Active, service.CompleteCountdown(accepted.Run.Id).Status);
            service.RemoveFromQueue(queueId); // Existing virtual clients may still issue their post-start cleanup.

            Assert.Equal(MessageDisposition.Duplicate,
                service.ReceivePhysicalMasterStart("boot-token-1", 8, startAllowed: true).Disposition);
            Assert.Equal(MessageDisposition.StaleSequence,
                service.ReceivePhysicalMasterStart("boot-token-1", 7, startAllowed: true).Disposition);
            Assert.Equal(RunStatus.Active, service.GetOperatorSnapshot().CurrentRun!.Status);
        }

        using (var store = new RunStore(path))
        {
            var service = new RunService(store, MakeEdition(), new TestClock());
            Assert.Equal(RunStatus.Paused, service.GetOperatorSnapshot().CurrentRun!.Status);
            Assert.Equal(1, service.GetOperatorSnapshot().Queue.Count);
            Assert.Equal(MessageDisposition.Duplicate,
                service.ReceivePhysicalMasterStart("boot-token-1", 8, startAllowed: true).Disposition);
            Assert.Equal(MessageDisposition.StaleSequence,
                service.ReceivePhysicalMasterStart("boot-token-1", 6, startAllowed: true).Disposition);
        }
    }
    finally
    {
        Cleanup(path);
    }
}

static void PhysicalMasterStatusCountdown()
{
    using var h = NewHarness(durationSeconds: 20);
    h.Service.Arm(h.Service.GetOperatorSnapshot().Queue.Single().Id);
    Assert.Equal(new MasterRunStatus("ARMED", 20), h.Service.GetMasterStatus());
    var countdown = h.Service.StartMaster();
    Assert.Equal(RunStatus.Countdown, countdown.Status);
    Assert.Equal(new MasterRunStatus("COUNTDOWN", 20), h.Service.GetMasterStatus());
    h.Clock.Advance(TimeSpan.FromSeconds(12));
    Assert.Equal(new MasterRunStatus("COUNTDOWN", 20), h.Service.GetMasterStatus());
    h.Service.CompleteCountdown(countdown.Id);
    h.Clock.Advance(TimeSpan.FromMilliseconds(1_100));
    Assert.Equal(new MasterRunStatus("ACTIVE", 19), h.Service.GetMasterStatus());
    h.Service.Pause();
    Assert.Equal(new MasterRunStatus("PAUSED", 19), h.Service.GetMasterStatus());
    h.Service.Finish();
    Assert.Equal(new MasterRunStatus("FINISHED", 0), h.Service.GetMasterStatus());
    var finishedStatuses = h.Service.GetMasterStatuses();
    Assert.Equal("FINISHED", finishedStatuses.GarageStatus.State);
    Assert.Equal($"GG1 GARAGE {finishedStatuses.GarageStatus.Token} FINISHED",
        MasterProtocolCodec.FormatGarageStatus(finishedStatuses.GarageStatus));
    Assert.Equal("GG1 STATUS FINISHED 0", MasterProtocolCodec.FormatStatus(finishedStatuses.Status));
    Assert.Equal(RunStatus.Finished, h.Service.GetOperatorSnapshot().CurrentRun!.Status);
}

static void MvpTimeoutAndNextRun()
{
    using var h = new TestHarness(MakeMvpEdition(durationSeconds: 2), NewPath());
    var first = h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Official);
    h.StartRun();
    Assert.Equal(MessageDisposition.Accepted, h.Service.PressEvent(first.Id, "event-01").Disposition);
    h.Clock.Advance(TimeSpan.FromSeconds(2));

    var timedOut = h.Service.GetOperatorSnapshot().CurrentRun!;
    Assert.Equal(RunStatus.TimedOut, timedOut.Status);
    var timeoutStatuses = h.Service.GetMasterStatuses();
    Assert.Equal(new MasterRunStatus("FINISHED", 0), timeoutStatuses.Status);
    Assert.Equal("TIMED_OUT", timeoutStatuses.GarageStatus.State);
    Assert.Equal(MasterProtocolCodec.GetGarageRunToken(first.Id), timeoutStatuses.GarageStatus.Token);
    Assert.Equal($"GG1 GARAGE {timeoutStatuses.GarageStatus.Token} TIMED_OUT",
        MasterProtocolCodec.FormatGarageStatus(timeoutStatuses.GarageStatus));
    Assert.Equal("GG1 STATUS FINISHED 0", MasterProtocolCodec.FormatStatus(timeoutStatuses.Status));
    Assert.Equal(1, h.Service.GetOperatorSnapshot().History.Count(r => r.Id == first.Id));
    Assert.Equal(MessageDisposition.TimedOut, h.Service.PressEvent(first.Id, "event-01").Disposition);

    var nextCompetitor = h.AddCompetitor("Next competitor");
    var nextRun = h.Service.ArmCompetitor(nextCompetitor.Id, RunCategory.Official);
    Assert.Equal(RunStatus.Armed, nextRun.Status);
    Assert.Equal(RunStatus.TimedOut, h.Service.GetOperatorSnapshot().History.Single(r => r.Id == first.Id).Status);
    Assert.Equal(nextRun.Id, h.StartRun().Id);
    Assert.Equal(RunStatus.Active, h.Service.GetOperatorSnapshot().CurrentRun!.Status);
}

static void PerRunDurationSnapshot()
{
    var edition = MakeMvpEdition(durationSeconds: 300);
    using var h = new TestHarness(edition, NewPath());
    var nextCompetitor = h.AddCompetitor("Default duration competitor");

    Assert.Throws<CommandException>(() => h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Official, 0));
    Assert.Throws<CommandException>(() => h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Official, -1));
    Assert.Throws<CommandException>(() => h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Official, 6_000));
    Assert.Equal(0, h.Store.Load().Runs.Count);

    var customRun = h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Official, 2);
    Assert.Equal(2, customRun.Edition.DurationLimitSeconds);
    Assert.Equal(edition.EditionId, customRun.EditionId);
    Assert.Equal(edition.EditionId, customRun.Edition.EditionId);
    Assert.Equal(300, edition.DurationLimitSeconds);
    Assert.Equal(new MasterRunStatus("ARMED", 2), h.Service.GetMasterStatus());

    var armedScoreboard = h.Service.GetScoreboard();
    Assert.Equal(2, armedScoreboard.DurationLimitSeconds);
    Assert.Equal(2_000L, armedScoreboard.CurrentRun!.RemainingMilliseconds);

    h.StartRun();
    h.Clock.Advance(TimeSpan.FromSeconds(2));
    var timedOut = h.Service.GetOperatorSnapshot().History.Single(run => run.Id == customRun.Id);
    Assert.Equal(RunStatus.TimedOut, timedOut.Status);
    Assert.Equal(2, timedOut.Edition.DurationLimitSeconds);
    Assert.Equal(edition.EditionId, timedOut.EditionId);
    Assert.Equal(300, h.Service.GetOperatorSnapshot().DurationLimitSeconds);
    Assert.Equal(2, h.Service.GetScoreboard().DurationLimitSeconds);
    Assert.Equal(0L, h.Service.GetScoreboard().CurrentRun!.RemainingMilliseconds);

    var persistedCustomRun = h.Store.Load().Runs.Single(run => run.Id == customRun.Id);
    Assert.Equal(RunStatus.TimedOut, persistedCustomRun.Status);
    Assert.Equal(2, persistedCustomRun.Edition.DurationLimitSeconds);
    Assert.Equal(edition.EditionId, persistedCustomRun.EditionId);

    var defaultRun = h.Service.ArmCompetitor(nextCompetitor.Id, RunCategory.Official);
    Assert.Equal(300, defaultRun.Edition.DurationLimitSeconds);
    Assert.Equal(edition.EditionId, defaultRun.EditionId);
    Assert.Equal(new MasterRunStatus("ARMED", 300), h.Service.GetMasterStatus());
    Assert.Equal(300, h.Service.GetScoreboard().DurationLimitSeconds);
    h.StartRun();
    h.Service.Finish();
    var recorded = h.Service.Record();

    Assert.Equal(defaultRun.Id, recorded.Id);
    Assert.Equal(edition.EditionId, recorded.EditionId);
    var leaderboard = h.Service.GetScoreboard().Leaderboard;
    Assert.Equal(1, leaderboard.Count);
    Assert.Equal(defaultRun.Id, leaderboard.Single().RunId);
    Assert.Equal(2, h.Service.GetOperatorSnapshot().History.Count);
    Assert.Equal(2, h.Service.GetOperatorSnapshot().History.Single(run => run.Id == customRun.Id).Edition.DurationLimitSeconds);
    Assert.Equal(300, h.Store.Load().Runs.Single(run => run.Id == defaultRun.Id).Edition.DurationLimitSeconds);

    var maximumDurationRun = h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Exhibition, 5_999);
    Assert.Equal(5_999, maximumDurationRun.Edition.DurationLimitSeconds);
    Assert.Equal(edition.EditionId, maximumDurationRun.EditionId);
    h.Service.Abort("Clean up maximum-duration test run");
}

static void MvpRosterAndVirtualPresses()
{
    var editionPath = Path.Combine(Environment.CurrentDirectory, "v2", "config", "edition-2026.json");
    var edition = EditionDefinition.FromJson(editionPath);
    Assert.Equal(13, edition.Events.Count);
    Assert.Equal(false, edition.Scoring.ManualEventPoints);
    Assert.Equal(50, edition.Scoring.BasePoints);
    Assert.Equal(25, edition.Scoring.MinimumPoints);
    Assert.True(edition.Events.All(e => e.Type == EventKind.Standard), "Every MVP event must use standard two-press behavior.");
    Assert.Equal("Perfect Pour", edition.Events[0].Name);
    Assert.Equal("Hammer Head", edition.Events[^1].Name);

    using var h = new TestHarness(edition, NewPath());
    var operatorEvents = h.Service.GetOperatorSnapshot().Events;
    Assert.Equal(edition.Events.Count, operatorEvents.Count);
    Assert.True(operatorEvents.Select(e => e.Name).SequenceEqual(edition.Events.Select(e => e.Name)),
        "The operator roster must follow the configured edition event names and order.");
    var run = h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Official);
    h.StartRun();
    h.Clock.Advance(TimeSpan.FromSeconds(2));
    var started = h.Service.PressEvent(run.Id, "event-01");
    Assert.Equal(MessageDisposition.Accepted, started.Disposition);
    Assert.Equal(EventStatus.Active, started.Run!.Events.Single(e => e.EventId == "event-01").Status);
    h.Clock.Advance(TimeSpan.FromSeconds(3));
    var completed = h.Service.PressEvent(run.Id, "event-01");
    var result = completed.Run!.Events.Single(e => e.EventId == "event-01");
    Assert.Equal(EventStatus.Completed, result.Status);
    Assert.Equal(3_000L, result.DurationMs);
    Assert.Equal(50, result.Score);
    Assert.Equal(MessageDisposition.AlreadyCompleted, h.Service.PressEvent(run.Id, "event-01").Disposition);
}

static void MvpEditableScorecard()
{
    using var h = new TestHarness(MakeMvpEdition(durationSeconds: 20), NewPath());
    var run = h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Official);
    h.StartRun();
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
    Assert.Equal(RunStatus.Finished, finished.Status);
    Assert.Equal(null, finished.RecordedAt);
    finished = h.Service.Record();
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

static void RecordedExhibitionCorrectionTimeline()
{
    using var h = NewHarness();
    var run = h.ArmAndStart(RunCategory.Exhibition);
    var finished = h.Service.Finish();
    Assert.Equal(0L, finished.ActiveElapsedMs);
    var recorded = h.Service.Record();

    var corrected = h.Service.EditHistoricalRun(recorded.Id, new EditRunRequest
    {
        ExpectedRevision = recorded.Revision,
        Reason = "Add the missing exhibition event timing",
        Events = [new EventEditRequest { EventId = "event-01", StartElapsedMs = 90_000, FinishElapsedMs = 120_000 }]
    });

    var result = corrected.Events.Single(e => e.EventId == "event-01");
    Assert.Equal(120_000L, corrected.ActiveElapsedMs);
    Assert.Equal(30_000L, result.DurationMs);
    Assert.Equal(25, result.Score);
    Assert.Equal(120_000L, h.Store.Load().Runs.Single(r => r.Id == run.Id).ActiveElapsedMs);
    Assert.Equal(1, h.Store.Load().Edits.Count(e => e.RunId == run.Id));

    var edit = h.Service.GetOperatorSnapshot().Edits.Single(e => e.RunId == run.Id);
    var undone = h.Service.UndoHistoricalEdit(run.Id, edit.Id, corrected.Revision, "Undo missing event timing");
    Assert.Equal(0L, undone.ActiveElapsedMs);
    Assert.Equal(EventStatus.Pending, undone.Events.Single(e => e.EventId == "event-01").Status);
    Assert.Equal(null, undone.Events.Single(e => e.EventId == "event-01").StartElapsedMs);
    Assert.Equal(0L, h.Store.Load().Runs.Single(r => r.Id == run.Id).ActiveElapsedMs);
    Assert.Equal(edit.Id, h.Store.Load().Edits.Single(e => e.UndoneEditId == edit.Id).UndoneEditId);
}

static void FinishedCorrectionTimeline()
{
    using var h = NewHarness();
    var run = h.ArmAndStart(RunCategory.Exhibition);

    var live = h.Service.GetOperatorSnapshot().CurrentRun!;
    Assert.Throws<CommandException>(() => h.Service.EditCurrentRun(new EditRunRequest
    {
        ExpectedRevision = live.Revision,
        Reason = "Active runs cannot extend their current timeline",
        Events = [new EventEditRequest { EventId = "event-01", StartElapsedMs = 90_000, FinishElapsedMs = 120_000 }]
    }));
    Assert.Equal(0L, h.Service.GetOperatorSnapshot().CurrentRun!.ActiveElapsedMs);

    var finished = h.Service.Finish();
    var corrected = h.Service.EditCurrentRun(new EditRunRequest
    {
        ExpectedRevision = finished.Revision,
        Reason = "Correct event timing after early finish",
        Events = [new EventEditRequest { EventId = "event-01", StartElapsedMs = 90_000, FinishElapsedMs = 120_000 }]
    });
    Assert.Equal(RunStatus.Finished, corrected.Status);
    Assert.Equal(120_000L, corrected.ActiveElapsedMs);
    Assert.Equal(25, corrected.Events.Single(e => e.EventId == "event-01").Score);
    Assert.Equal(120_000L, h.Store.Load().Runs.Single(r => r.Id == run.Id).ActiveElapsedMs);
    Assert.Equal(1, h.Store.Load().Edits.Count(e => e.RunId == run.Id));

    var edit = h.Service.GetOperatorSnapshot().Edits.Single(e => e.RunId == run.Id);
    var undone = h.Service.UndoCurrentEdit(edit.Id, corrected.Revision, "Undo finished-run timing correction");
    Assert.Equal(0L, undone.ActiveElapsedMs);
    Assert.Equal(EventStatus.Pending, undone.Events.Single(e => e.EventId == "event-01").Status);
    Assert.Equal(0L, h.Store.Load().Runs.Single(r => r.Id == run.Id).ActiveElapsedMs);
    Assert.Equal(2, h.Store.Load().Edits.Count(e => e.RunId == run.Id));

    var afterUndo = h.Service.GetOperatorSnapshot().CurrentRun!;
    Assert.Throws<CommandException>(() => h.Service.EditCurrentRun(new EditRunRequest
    {
        ExpectedRevision = afterUndo.Revision,
        Reason = "Explicit elapsed time shorter than event finish must fail",
        ActiveElapsedMs = 110_000,
        Events = [new EventEditRequest { EventId = "event-01", StartElapsedMs = 90_000, FinishElapsedMs = 120_000 }]
    }));
    Assert.Equal(0L, h.Service.GetOperatorSnapshot().CurrentRun!.ActiveElapsedMs);

    var explicitDuration = h.Service.EditCurrentRun(new EditRunRequest
    {
        ExpectedRevision = afterUndo.Revision,
        Reason = "Set elapsed time explicitly",
        ActiveElapsedMs = 150_000,
        Events = [new EventEditRequest { EventId = "event-01", StartElapsedMs = 90_000, FinishElapsedMs = 120_000 }]
    });
    Assert.Equal(150_000L, explicitDuration.ActiveElapsedMs);
    Assert.Equal(25, explicitDuration.Events.Single(e => e.EventId == "event-01").Score);

    Assert.Throws<CommandException>(() => h.Service.EditCurrentRun(new EditRunRequest
    {
        ExpectedRevision = explicitDuration.Revision,
        Reason = "Reject event timing beyond edition duration",
        Events = [new EventEditRequest { EventId = "event-02", StartElapsedMs = 299_000, FinishElapsedMs = 300_001 }]
    }));
    Assert.Throws<CommandException>(() => h.Service.EditCurrentRun(new EditRunRequest
    {
        ExpectedRevision = explicitDuration.Revision,
        Reason = "Reject reversed event timing",
        Events = [new EventEditRequest { EventId = "event-02", StartElapsedMs = 170_000, FinishElapsedMs = 160_000 }]
    }));
    Assert.Equal(150_000L, h.Service.GetOperatorSnapshot().CurrentRun!.ActiveElapsedMs);
}

static void UnfinishedCurrentCorrectionTimelineLimits()
{
    using (var h = NewHarness())
    {
        var armed = h.Service.Arm(h.Service.GetOperatorSnapshot().Queue.Single().Id);
        Assert.Throws<CommandException>(() => h.Service.EditCurrentRun(new EditRunRequest
        {
            ExpectedRevision = armed.Revision,
            Reason = "Armed run timestamps cannot advance the timeline",
            Events = [new EventEditRequest { EventId = "event-01", StartElapsedMs = 90_000, FinishElapsedMs = 120_000 }]
        }));
        Assert.Equal(0L, h.Service.GetOperatorSnapshot().CurrentRun!.ActiveElapsedMs);
    }

    using (var h = NewHarness())
    {
        h.ArmAndStart();
        var paused = h.Service.Pause();
        Assert.Throws<CommandException>(() => h.Service.EditCurrentRun(new EditRunRequest
        {
            ExpectedRevision = paused.Revision,
            Reason = "Paused run timestamps cannot advance the timeline",
            Events = [new EventEditRequest { EventId = "event-01", StartElapsedMs = 90_000, FinishElapsedMs = 120_000 }]
        }));
        Assert.Equal(0L, h.Service.GetOperatorSnapshot().CurrentRun!.ActiveElapsedMs);
    }
}

static void AutomatedScoreAndBonusPersistence()
{
    using var h = new TestHarness(MakeMvpEdition(), NewPath());
    var run = h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Official);
    h.StartRun();

    h.Service.PressEvent(run.Id, "event-01");
    h.Clock.Advance(TimeSpan.FromMilliseconds(4_990));
    var completed = h.Service.PressEvent(run.Id, "event-01").Run!;
    Assert.Equal(50, completed.Events.Single(e => e.EventId == "event-01").Score);

    h.Clock.Advance(TimeSpan.FromMilliseconds(10));
    var live = h.Service.GetOperatorSnapshot().CurrentRun!;
    var overridden = h.Service.EditCurrentRun(new EditRunRequest
    {
        ExpectedRevision = live.Revision,
        Reason = "Set a manual event score and general bonus",
        BonusPointsOverride = 7,
        Events = [new EventEditRequest
        {
            EventId = "event-01",
            FinishElapsedMs = 5_000,
            ScoreOverride = 81
        }]
    });
    Assert.Equal(81, overridden.Events.Single(e => e.EventId == "event-01").Score);

    h.Clock.Advance(TimeSpan.FromSeconds(20));
    live = h.Service.GetOperatorSnapshot().CurrentRun!;
    var changedTime = h.Service.EditCurrentRun(new EditRunRequest
    {
        ExpectedRevision = live.Revision,
        Reason = "Correct the event finish timestamp",
        Events = [new EventEditRequest { EventId = "event-01", FinishElapsedMs = 25_000 }]
    });
    Assert.Equal(81, changedTime.Events.Single(e => e.EventId == "event-01").Score);

    h.Service.Finish();
    var recorded = h.Service.Record();
    var persisted = h.Store.Load().Runs.Single(r => r.Id == recorded.Id);
    Assert.Equal(81, persisted.Events.Single(e => e.EventId == "event-01").Score);
    Assert.Equal(81, persisted.Events.Single(e => e.EventId == "event-01").ScoreOverride);
    Assert.Equal(7, persisted.BonusPointsOverride);
    Assert.Equal(88, persisted.TotalPoints);
    Assert.Equal(88, h.Service.GetOperatorSnapshot().Leaderboard.Single().Points);

    var historical = h.Service.EditHistoricalRun(recorded.Id, new EditRunRequest
    {
        ExpectedRevision = recorded.Revision,
        Reason = "Clear the manual score and correct the event time",
        BonusPointsOverride = 9,
        Events = [new EventEditRequest
        {
            EventId = "event-01",
            FinishElapsedMs = 10_000,
            ClearScoreOverride = true
        }]
    });
    Assert.Equal(40, historical.Events.Single(e => e.EventId == "event-01").Score);
    Assert.Equal(null, historical.Events.Single(e => e.EventId == "event-01").ScoreOverride);
    Assert.Equal(9, historical.BonusPointsOverride);
    Assert.Equal(49, historical.TotalPoints);

    persisted = h.Store.Load().Runs.Single(r => r.Id == recorded.Id);
    Assert.Equal(40, persisted.Events.Single(e => e.EventId == "event-01").Score);
    Assert.Equal(9, persisted.BonusPointsOverride);
    Assert.Equal(49, persisted.TotalPoints);
    Assert.Equal(49, h.Service.GetOperatorSnapshot().History.Single(r => r.Id == recorded.Id).TotalPoints);
    Assert.Equal(49, h.Service.GetOperatorSnapshot().Leaderboard.Single().Points);

    var withoutBonus = h.Service.EditHistoricalRun(recorded.Id, new EditRunRequest
    {
        ExpectedRevision = historical.Revision,
        Reason = "Clear the general bonus",
        ClearBonusPointsOverride = true
    });
    Assert.Equal(null, withoutBonus.BonusPointsOverride);
    Assert.Equal(40, withoutBonus.TotalPoints);
    Assert.Equal(null, h.Store.Load().Runs.Single(r => r.Id == recorded.Id).BonusPointsOverride);
}

static void RecordPromotesNextCompetitor()
{
    using var h = new TestHarness(MakeMvpEdition(), NewPath());
    var nextCompetitor = h.AddCompetitor("Next competitor");
    h.Service.AddToQueue(nextCompetitor.Id, RunCategory.Playoff);

    var firstQueueItem = h.Service.GetOperatorSnapshot().Queue.First();
    var firstRun = h.Service.Arm(firstQueueItem.Id);
    h.StartRun();
    h.Service.Finish();

    using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={h.Store.DatabasePath}"))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TRIGGER fail_selected_competitor_insert BEFORE INSERT ON meta
            WHEN NEW.key = 'selected_competitor_id'
            BEGIN SELECT RAISE(ABORT, 'injected selection-write failure'); END;
            """;
        command.ExecuteNonQuery();
    }

    Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => h.Service.Record());
    var afterRollback = h.Service.GetOperatorSnapshot();
    Assert.Equal(RunStatus.Finished, afterRollback.CurrentRun!.Status);
    Assert.Equal(1, afterRollback.Queue.Count);
    Assert.Equal("", afterRollback.SelectedCompetitorId);
    Assert.Equal(1, h.Store.Load().Queue.Count);

    using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={h.Store.DatabasePath}"))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DROP TRIGGER fail_selected_competitor_insert";
        command.ExecuteNonQuery();
    }

    var recorded = h.Service.Record();
    var selected = h.Service.GetOperatorSnapshot();
    Assert.Equal(RunStatus.Completed, recorded.Status);
    Assert.Equal(nextCompetitor.Id, selected.SelectedCompetitorId);
    Assert.Equal(RunCategory.Playoff, selected.SelectedRunCategory);
    Assert.Equal(0, selected.Queue.Count);
    Assert.True(!h.Service.IsCurrentRun(firstRun.Id), "Promotion must select the next competitor without starting their run.");

    var persisted = h.Store.Load();
    Assert.Equal(nextCompetitor.Id, persisted.SelectedCompetitorId);
    Assert.Equal(RunCategory.Playoff, persisted.SelectedRunCategory);
    Assert.Equal(0, persisted.Queue.Count);

    h.Store.Dispose();
    using var reopenedStore = new RunStore(h.Path);
    var reopenedService = new RunService(reopenedStore, MakeMvpEdition(), new TestClock());
    selected = reopenedService.GetOperatorSnapshot();
    Assert.Equal(nextCompetitor.Id, selected.SelectedCompetitorId);
    Assert.Equal(RunCategory.Playoff, selected.SelectedRunCategory);
    Assert.True(!reopenedService.IsCurrentRun(firstRun.Id), "Reloading the selected competitor must not start a run.");

    var nextRun = reopenedService.ArmCompetitor(nextCompetitor.Id, RunCategory.Playoff);
    reopenedService.CompleteCountdown(reopenedService.StartMaster().Id);
    reopenedService.Finish();
    reopenedService.Record();
    selected = reopenedService.GetOperatorSnapshot();
    Assert.Equal("", selected.SelectedCompetitorId);
    Assert.Equal(null, selected.SelectedRunCategory);
    persisted = reopenedStore.Load();
    Assert.Equal(null, persisted.SelectedCompetitorId);
    Assert.Equal(null, persisted.SelectedRunCategory);
    Assert.Equal(0, persisted.Queue.Count);
    Assert.True(!reopenedService.IsCurrentRun(nextRun.Id), "An empty queue should leave no selected competitor or start a run.");
}

static void FinishIsIdempotent()
{
    using var h = new TestHarness(MakeMvpEdition(), NewPath());
    var run = h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Official);
    h.StartRun();
    var finished = h.Service.Finish();
    Assert.Equal(RunStatus.Finished, finished.Status);
    Assert.Equal(null, finished.RecordedAt);
    Assert.Equal(RunStatus.Finished, h.Service.GetOperatorSnapshot().CurrentRun!.Status);
    Assert.Throws<CommandException>(() => h.Service.ArmCompetitor(h.AddCompetitor("Blocked until recorded").Id, RunCategory.Official));
    var recorded = h.Service.Record();
    var secondRecord = h.Service.Record();
    Assert.Equal(run.Id, recorded.Id);
    Assert.Equal(recorded.Id, secondRecord.Id);
    Assert.Equal(RunStatus.Completed, recorded.Status);
    Assert.True(recorded.RecordedAt is not null);
    Assert.Equal(1, h.Service.GetOperatorSnapshot().History.Count(r => r.Id == run.Id));
    var next = h.AddCompetitor("Next after recording");
    Assert.Equal(RunStatus.Armed, h.Service.ArmCompetitor(next.Id, RunCategory.Official).Status);
}

static void MvpAutoFinishAndRecord()
{
    using var h = new TestHarness(MakeMvpEdition(durationSeconds: 90), NewPath());
    var run = h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Official);
    h.StartRun();
    for (var index = 0; index < run.Events.Count; index++)
    {
        h.Service.PressEvent(run.Id, run.Events[index].EventId);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        var stopped = h.Service.PressEvent(run.Id, run.Events[index].EventId);
        if (index == run.Events.Count - 1)
        {
            Assert.Equal(RunStatus.Finished, stopped.Run!.Status);
        }
        h.Clock.Advance(TimeSpan.FromSeconds(1));
    }

    var finished = h.Service.GetOperatorSnapshot().CurrentRun!;
    Assert.Equal(RunStatus.Finished, finished.Status);
    Assert.Equal(null, finished.RecordedAt);
    Assert.True(finished.AllEventsCompleted);
    var frozenAt = finished.ActiveElapsedMs;
    h.Clock.Advance(TimeSpan.FromSeconds(20));
    var stillFinished = h.Service.GetOperatorSnapshot().CurrentRun!;
    Assert.Equal(RunStatus.Finished, stillFinished.Status);
    Assert.Equal(frozenAt, stillFinished.ActiveElapsedMs);
    Assert.Equal(null, stillFinished.RecordedAt);

    var recorded = h.Service.Record();
    Assert.Equal(RunStatus.Completed, recorded.Status);
    Assert.True(recorded.RecordedAt is not null);
    Assert.Equal(RunStatus.Completed, h.Service.GetOperatorSnapshot().CurrentRun!.Status);
}

static void MvpCorrectionAutoFinish()
{
    using var h = new TestHarness(MakeMvpEdition(durationSeconds: 90), NewPath());
    h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Official);
    h.StartRun();
    h.Clock.Advance(TimeSpan.FromSeconds(5));
    var live = h.Service.GetOperatorSnapshot().CurrentRun!;
    var corrected = h.Service.EditCurrentRun(new EditRunRequest
    {
        ExpectedRevision = live.Revision,
        Reason = "Enter final completed scorecard",
        Events = live.Events.Select(e => new EventEditRequest
        {
            EventId = e.EventId,
            StartElapsedMs = 1_000,
            FinishElapsedMs = 2_000,
            ScoreOverride = 1
        }).ToList()
    });
    Assert.Equal(RunStatus.Finished, corrected.Status);
    Assert.Equal(null, corrected.RecordedAt);
    Assert.True(corrected.AllEventsCompleted);
    h.Clock.Advance(TimeSpan.FromSeconds(10));
    Assert.Equal(5_000L, h.Service.GetOperatorSnapshot().CurrentRun!.ActiveElapsedMs);
    Assert.Equal(RunStatus.Completed, h.Service.Record().Status);
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
    var item = h.Service.GetOperatorSnapshot().Queue.Single();
    var run = h.Service.Arm(item.Id, manualOfflineOverride: true);
    Assert.True(run.ManualOfflineOverride);
    Assert.True(run.Events.Select(e => e.EventId).Contains("event-01"));
    h.StartRun();
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

static void SetupPersistenceAndRosterIsolation()
{
    using var h = NewHarness();
    var run = h.ArmAndStart();
    h.Service.PressEvent(run.Id, "event-01");
    h.Clock.Advance(TimeSpan.FromSeconds(5));
    h.Service.PressEvent(run.Id, "event-01");
    h.Service.Finish();
    var recorded = h.Service.Record();
    var priorEditionId = recorded.EditionId;

    var setup = h.Service.GetSetup();
    setup.Events[0].BasePoints = 40;
    setup.Events[0].MinimumPoints = 15;
    setup.Events[0].DecayPoints = 0;
    setup.Events[0].DecayEverySeconds = 1;
    setup.Events[0].GraceSeconds = 0;
    var saved = h.Service.UpdateSetup(setup);

    Assert.True(saved.EditionId != priorEditionId, "Changing event points after recorded runs must start a separate leaderboard edition.");
    Assert.Equal(40, saved.Events[0].BasePoints);
    Assert.Equal(null, h.Service.GetOperatorSnapshot().History.Single(item => item.Id == run.Id).Edition.Events[0].BasePoints);
    Assert.Equal(45, h.Service.GetOperatorSnapshot().History.Single(item => item.Id == run.Id).Events[0].Score);
    Assert.Equal(saved.EditionId, h.Service.GetOperatorSnapshot().EditionId);
    Assert.Equal(0, h.Service.GetOperatorSnapshot().Leaderboard.Count);
    Assert.True(Directory.GetFiles(System.IO.Path.Combine(h.Path, "backups"), "*.db").Length >= 1,
        "Changing setup should create a pre-change database backup.");

    var path = h.Path;
    h.Store.Dispose();
    using var reopenedStore = new RunStore(path);
    var reopened = new RunService(reopenedStore, MakeEdition(), new TestClock());
    var persisted = reopened.GetSetup();
    Assert.Equal(saved.EditionId, persisted.EditionId);
    Assert.Equal(40, persisted.Events[0].BasePoints);
    Assert.Equal(15, persisted.Events[0].MinimumPoints);
    Assert.Equal(0, persisted.Events[0].DecayPoints);
    Assert.Equal(1, persisted.Events[0].DecayEverySeconds);
    Assert.Equal(0, persisted.Events[0].GraceSeconds);
    Assert.True(JsonSerializer.Serialize(persisted, JsonDefaults.Options).Contains("\"basePoints\":40", StringComparison.Ordinal));
    Assert.Equal(null, reopened.GetOperatorSnapshot().History.Single(item => item.Id == run.Id).Edition.Events[0].BasePoints);
    Assert.Equal(45, reopened.GetOperatorSnapshot().History.Single(item => item.Id == run.Id).Events[0].Score);
    Assert.True(saved.Events.Select(item => item.DeviceId).SequenceEqual(
        h.Service.GetOperatorSnapshot().Devices.Select(item => item.DeviceId)));
}

static void SetupLockAndValidation()
{
    using var h = NewHarness();
    var current = h.Service.GetSetup();
    var queueId = h.Service.GetOperatorSnapshot().Queue.Single().Id;
    h.Service.Arm(queueId);
    Assert.Throws<CommandException>(() => h.Service.UpdateSetup(current));

    h.Service.Abort("End setup-lock test run");
    current = h.Service.GetSetup();
    current.Events[0].BasePoints = -1;
    Assert.Throws<CommandException>(() => h.Service.UpdateSetup(current));

    current = h.Service.GetSetup();
    current.Events[0].BasePoints = EditionDefinition.MaximumEventBasePoints + 1;
    Assert.Throws<CommandException>(() => h.Service.UpdateSetup(current));

    current = h.Service.GetSetup();
    current.Events[1].DeviceId = current.Events[0].DeviceId;
    Assert.Throws<CommandException>(() => h.Service.UpdateSetup(current));

    current = h.Service.GetSetup();
    current.Events[1].EventId = current.Events[0].EventId;
    Assert.Throws<CommandException>(() => h.Service.UpdateSetup(current));

    var invalidEventScoring = new Action<EventDefinition>[]
    {
        item => item.MinimumPoints = -1,
        item => item.MinimumPoints = 51,
        item => { item.BasePoints = 10; item.MinimumPoints = 11; },
        item => item.DecayPoints = -1,
        item => item.DecayPoints = EditionDefinition.MaximumScoringPoints + 1,
        item => item.DecayEverySeconds = 0,
        item => item.DecayEverySeconds = EditionDefinition.MaximumScoringSeconds + 1,
        item => item.GraceSeconds = -1,
        item => item.GraceSeconds = EditionDefinition.MaximumScoringSeconds + 1
    };
    foreach (var setInvalidScoring in invalidEventScoring)
    {
        current = h.Service.GetSetup();
        setInvalidScoring(current.Events[0]);
        Assert.Throws<CommandException>(() => h.Service.UpdateSetup(current));
    }

    current = h.Service.GetSetup();
    current.Events[0].BasePoints = 0;
    current.Events[0].MinimumPoints = 0;
    current.Events[0].DecayPoints = 0;
    current.Events[0].DecayEverySeconds = 1;
    current.Events[0].GraceSeconds = 0;
    var explicitZeros = h.Service.UpdateSetup(current).Events[0];
    Assert.Equal(0, explicitZeros.BasePoints);
    Assert.Equal(0, explicitZeros.MinimumPoints);
    Assert.Equal(0, explicitZeros.DecayPoints);
    Assert.Equal(1, explicitZeros.DecayEverySeconds);
    Assert.Equal(0, explicitZeros.GraceSeconds);
}

static void DeviceScanReadiness()
{
    using var h = NewHarness(simulatedDevicesOnline: false);
    Assert.True(h.Service.GetOperatorSnapshot().Devices.All(device => device.Availability == DeviceAvailability.Unverified));
    Assert.True(h.Service.Preflight().All(result => !result.Passed));

    var setup = h.Service.GetSetup();
    setup.Events[0].DeviceId = "aabbccddeeff";
    setup.Events[1].DeviceId = "112233445566";
    h.Service.UpdateSetup(setup);
    var result = h.Service.RecordDeviceScan(true, true, ["AABBCCDDEEFF"]);

    Assert.True(result.Connected && result.Completed);
    Assert.True(result.DetectedDeviceIds.SequenceEqual(["AABBCCDDEEFF"]));
    Assert.Equal("Responding", result.Devices.Single(device => device.EventId == "event-01").Status);
    Assert.Equal("NotResponding", result.Devices.Single(device => device.EventId == "event-02").Status);
    Assert.Equal("NotScanned", result.Devices.Single(device => device.EventId == "event-03").Status);
    Assert.Equal(DeviceAvailability.Online, h.Service.GetOperatorSnapshot().Devices.Single(device => device.EventId == "event-01").Availability);
    Assert.Equal(DeviceAvailability.Offline, h.Service.GetOperatorSnapshot().Devices.Single(device => device.EventId == "event-02").Availability);
    Assert.Equal(DeviceAvailability.Unverified, h.Service.GetOperatorSnapshot().Devices.Single(device => device.EventId == "event-03").Availability);

    var disconnected = h.Service.RecordDeviceScan(false, false, ["AABBCCDDEEFF"]);
    Assert.True(!disconnected.Connected && !disconnected.Completed);
    Assert.True(h.Service.GetOperatorSnapshot().Devices.All(device =>
        device.Availability == DeviceAvailability.Unverified && device.LastSeenAt is null));
}

static void DeviceScanProtocol()
{
    Assert.True(MasterProtocolCodec.TryParseScanReply("GG1 SCAN scan-123 NODE aabbccddeeff", out var node));
    Assert.Equal("scan-123", node.ScanId);
    Assert.Equal("NODE", node.Kind);
    Assert.Equal("AABBCCDDEEFF", node.DeviceId);
    Assert.True(MasterProtocolCodec.TryParseScanReply("GG1 SCAN scan-123 DONE 1", out var done));
    Assert.Equal(1, done.Count);
    Assert.True(MasterProtocolCodec.TryParseScanReply("GG1 SCAN scan-123 BUSY", out var busy));
    Assert.Equal("BUSY", busy.Kind);
    Assert.True(!MasterProtocolCodec.TryParseScanReply("GG1 SCAN scan-123 NODE aabbccddeefg", out _));
    Assert.True(!MasterProtocolCodec.TryParseScanReply("GG1 SCAN scan-123 DONE -1", out _));
    Assert.True(!MasterProtocolCodec.TryParseScanReply("GG1 SCAN scan-123 NODE aabbccddeeff EXTRA", out _));
}

static void VirtualPressReadinessFallback()
{
    using var h = NewHarness(simulatedDevicesOnline: false);
    var queueId = h.Service.GetOperatorSnapshot().Queue.Single().Id;
    var armed = h.Service.Arm(queueId);
    Assert.True(!armed.ManualOfflineOverride, "Unverified readiness alone should not require or imply manual override.");
    var run = h.StartRun();

    Assert.Equal(MessageDisposition.Offline, h.Send(run, "station-01", "event-press", "unverified-packet").Disposition);
    Assert.Equal(MessageDisposition.Accepted, h.Service.PressEvent(run.Id, "event-01").Disposition);
    Assert.Equal(MessageDisposition.Accepted, h.Service.PressEvent(run.Id, "event-01").Disposition);
    Assert.Equal(DeviceAvailability.Unverified, h.Service.GetOperatorSnapshot().Devices
        .Single(device => device.DeviceId == "station-01").Availability);
}

static void QueueReorderPersists()
{
    using var h = NewHarness();
    var secondCompetitor = h.AddCompetitor("Second competitor");
    var thirdCompetitor = h.AddCompetitor("Third competitor");
    h.Service.AddToQueue(secondCompetitor.Id, RunCategory.Playoff);
    h.Service.AddToQueue(thirdCompetitor.Id, RunCategory.Exhibition);

    var original = h.Service.GetOperatorSnapshot().Queue.OrderBy(item => item.Position).ToArray();
    var requestedOrder = new[] { original[2].Id, original[0].Id, original[1].Id };
    h.Service.ReorderQueue(requestedOrder);

    var liveOrder = h.Service.GetOperatorSnapshot().Queue.OrderBy(item => item.Position).Select(item => item.Id);
    Assert.True(liveOrder.SequenceEqual(requestedOrder), "The operator snapshot should reflect the requested queue order.");
    var savedOrder = h.Store.Load().Queue.OrderBy(item => item.Position).Select(item => item.Id);
    Assert.True(savedOrder.SequenceEqual(requestedOrder), "The requested queue order should survive persistence.");
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
    Assert.Equal(RunStatus.Finished, finished.Status);
    h.Service.Record();
    var restartQueue = h.Service.Restart(first.Id);
    var replacement = h.Service.Arm(restartQueue.Id);
    Assert.Equal(first.Id, replacement.SupersedesRunId);
    var history = h.Service.GetOperatorSnapshot().History;
    Assert.Equal(RunStatus.Superseded, history.Single(r => r.Id == first.Id).Status);
    Assert.Equal(0, h.Service.GetOperatorSnapshot().Leaderboard.Count);

    h.Service.Finish();
    h.Service.Record();
    Assert.Equal(1, h.Service.GetOperatorSnapshot().Leaderboard.Count);
    var secondOfficial = h.Service.AddToQueue(h.CompetitorId, RunCategory.Official);
    Assert.Throws<CommandException>(() => h.Service.Arm(secondOfficial.Id));
}

static void CategoryAndTieRank()
{
    using var h = NewHarness();
    var exhibition = h.ArmAndStart(RunCategory.Exhibition);
    h.Service.Finish();
    h.Service.Record();
    Assert.DoesNotContain(h.Service.GetScoreboard().Leaderboard, row => row.CompetitorName == h.CompetitorName);
    Assert.Equal(RunCategory.Exhibition, h.Service.GetScoreboard().CurrentRun!.Category);

    var second = h.AddCompetitor("Second competitor");
    var officialOne = h.Service.AddToQueue(h.CompetitorId, RunCategory.Official);
    var officialTwo = h.Service.AddToQueue(second.Id, RunCategory.Official);
    h.Service.Arm(officialOne.Id);
    h.StartRun();
    h.Service.Finish();
    h.Service.Record();
    var promoted = h.Service.GetOperatorSnapshot();
    Assert.Equal(second.Id, promoted.SelectedCompetitorId);
    Assert.Equal(0, promoted.Queue.Count);
    Assert.DoesNotContain(promoted.Queue, item => item.Id == officialTwo.Id);
    h.Service.ArmCompetitor(second.Id, RunCategory.Official);
    h.StartRun();
    h.Service.Finish();
    h.Service.Record();
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
    service.CompleteCountdown(service.StartMaster().Id);
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

static void ClearDatabaseSafety()
{
    var path = NewPath();
    var edition = MakeMvpEdition();
    var clock = new TestClock();
    var store = new RunStore(path);
    try
    {
        var service = new RunService(store, edition, clock);
        var competitor = service.AddCompetitor("Backup competitor");
        var queuedCompetitor = service.AddCompetitor("Queued competitor");
        service.AddToQueue(competitor.Id, RunCategory.Official);
        service.AddToQueue(queuedCompetitor.Id, RunCategory.Playoff);
        var run = service.ArmCompetitor(competitor.Id, RunCategory.Official);
        service.CompleteCountdown(service.StartMaster().Id);
        service.PressEvent(run.Id, "event-01");
        var liveRun = service.GetOperatorSnapshot().CurrentRun!;
        service.EditCurrentRun(new EditRunRequest
        {
            ExpectedRevision = liveRun.Revision,
            Reason = "Seed an edit for the clear test",
            Notes = "Pre-clear note"
        });
        service.SetDeviceAvailability("station-01", DeviceAvailability.Offline, "Seed offline device state");

        var beforeClear = store.Load();
        store.SaveRunsAndQueue(beforeClear.Runs, beforeClear.Queue, competitor.Id, RunCategory.Official);

        Assert.Throws<CommandException>(() => service.ClearAllData("clear all data"));
        Assert.True(!Directory.Exists(Path.Combine(path, "backups")), "Rejected confirmation must not create a backup or clear data.");
        Assert.Equal(2, store.Load().Competitors.Count);

        var backupPath = service.ClearAllData(RunService.DatabaseClearConfirmationPhrase);
        Assert.True(File.Exists(backupPath), "Clear must return an existing backup path.");
        Assert.True(backupPath.StartsWith(Path.Combine(path, "backups") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "The backup must remain under the isolated test database's backup directory.");

        using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString()))
        {
            backup.Open();
            Assert.Equal(2L, CountRows(backup, "competitors"));
            // Accepted master start consumes the current competitor's on-deck entry; the other entry remains queued.
            Assert.Equal(1L, CountRows(backup, "queue_items"));
            using var remainingQueue = backup.CreateCommand();
            remainingQueue.CommandText = "SELECT competitor_id FROM queue_items";
            Assert.Equal(queuedCompetitor.Id, remainingQueue.ExecuteScalar() as string);
            Assert.Equal(1L, CountRows(backup, "runs"));
            Assert.Equal(edition.Events.Count, (int)CountRows(backup, "run_events"));
            Assert.True(CountRows(backup, "messages") >= 2, "The backup should retain device/master messages.");
            Assert.Equal(1L, CountRows(backup, "edits"));
            Assert.Equal(edition.Events.Count, (int)CountRows(backup, "devices"));
            using var selection = backup.CreateCommand();
            selection.CommandText = "SELECT value FROM meta WHERE key = 'selected_competitor_id'";
            Assert.Equal(competitor.Id, selection.ExecuteScalar() as string);
        }

        var cleared = service.GetOperatorSnapshot();
        Assert.Equal(0, cleared.Competitors.Count);
        Assert.Equal(0, cleared.Queue.Count);
        Assert.Equal(0, cleared.History.Count);
        Assert.Equal(0, cleared.Messages.Count);
        Assert.Equal(0, cleared.Edits.Count);
        Assert.Equal("", cleared.SelectedCompetitorId);
        Assert.Equal(null, cleared.SelectedRunCategory);
        Assert.Equal(null, cleared.CurrentRun);
        Assert.Equal(edition.Events.Count, cleared.Devices.Count);
        Assert.True(cleared.Devices.All(device =>
            device.Availability == DeviceAvailability.Unverified && device.LastSeenAt is null &&
            device.Led == LedState.OfflineError && device.LastError is null), "Configured devices should be restored as unverified and not represented as online.");

        var afterClear = store.Load();
        Assert.Equal(null, afterClear.SelectedCompetitorId);
        Assert.Equal(null, afterClear.SelectedRunCategory);
        Assert.Equal(0, afterClear.Runs.Count);
        Assert.True(File.Exists(backupPath), "The automatically created backup must remain available after clearing.");

        var newCompetitor = service.AddCompetitor("After clear");
        var newQueueItem = service.AddToQueue(newCompetitor.Id, RunCategory.Official);
        var newRun = service.Arm(newQueueItem.Id);
        Assert.Equal(RunStatus.Armed, newRun.Status);
        Assert.Equal(RunStatus.Active, service.CompleteCountdown(service.StartMaster().Id).Status);
    }
    finally
    {
        // Keep this isolated test database and its generated backup; backup files are never removed by this test.
        store.Dispose();
    }

    static long CountRows(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}

static void ClearDatabaseBackupFailurePreservesData()
{
    var path = NewPath();
    var store = new RunStore(path);
    try
    {
        var service = new RunService(store, MakeMvpEdition(), new TestClock());
        service.AddCompetitor("Must survive failed backup");
        File.WriteAllText(Path.Combine(path, "backups"), "Isolated test fixture blocking backup folder creation.");

        Assert.Throws<IOException>(() => service.ClearAllData(RunService.DatabaseClearConfirmationPhrase));
        var afterFailure = service.GetOperatorSnapshot();
        Assert.Equal(1, afterFailure.Competitors.Count);
        Assert.Equal("Must survive failed backup", afterFailure.Competitors[0].Name);
        Assert.Equal(0, afterFailure.History.Count);
    }
    finally
    {
        // Keep the isolated test fixture in place; it is not a database backup and no backup file is removed.
        store.Dispose();
    }
}

static void EditsAndIsolation()
{
    using var h = NewHarness();
    var first = h.ArmAndStart();
    Assert.Equal(MessageDisposition.Accepted, h.Send(first, "station-01", "event-press", "history-start").Disposition);
    Assert.Equal(MessageDisposition.Accepted, h.Send(first, "station-01", "event-press", "history-finish").Disposition);
    var historical = h.Service.Finish();
    h.Service.Record();
    var second = h.AddCompetitor("Live competitor");
    var liveQueue = h.Service.AddToQueue(second.Id, RunCategory.Playoff);
    h.Service.Arm(liveQueue.Id);
    h.StartRun();
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
    Assert.Equal(50, undone.Events.Single(e => e.EventId == "event-01").Score);
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

static TestHarness NewHarness(int durationSeconds = 300, bool simulatedDevicesOnline = true) =>
    new(MakeEdition(durationSeconds), NewPath(), simulatedDevicesOnline);

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
    Scoring = new ScoringRule(),
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
    public TestHarness(EditionDefinition edition, string path, bool simulatedDevicesOnline = true)
    {
        Path = path;
        Clock = new TestClock();
        Store = new RunStore(path);
        Service = new RunService(Store, edition, Clock);
        if (simulatedDevicesOnline)
        {
            foreach (var device in Service.GetOperatorSnapshot().Devices)
            {
                Service.SetDeviceAvailability(device.DeviceId, DeviceAvailability.Online, "Explicit simulated-test fixture response.");
            }
        }
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

    public RunRecord StartRun()
    {
        var countdown = Service.StartMaster();
        return Service.CompleteCountdown(countdown.Id);
    }

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
        Service.Arm(itemToArm.Id);
        return StartRun();
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
