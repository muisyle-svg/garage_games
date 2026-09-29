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
    ("physical button state snapshots follow virtual presses, undo, and finish", PhysicalButtonStateSnapshotTracksMixedInputs),
    ("countdown freezes time and rejects input and run actions", CountdownFreezesTimeAndRejectsActions),
    ("countdown completion is durable, idempotent, and run-scoped", CountdownCompletionIsRunScopedAndIdempotent),
    ("countdown recovery waits for replayed audio", CountdownRecovery),
    ("physical master start is durable, deduplicated, and consumes its on-deck item", PhysicalMasterStartDurabilityAndQueue),
    ("physical master status follows run countdown", PhysicalMasterStatusCountdown),
    ("timeout autosaves and releases next competitor", MvpTimeoutAndNextRun),
    ("HTTP arm request duration flows into the saved run and ten-second timeout", ArmRequestDurationHandoff),
    ("custom run duration snapshots timeout and preserves edition leaderboard identity", PerRunDurationSnapshot),
    ("MVP roster is 13 regular events with two-press virtual buttons", MvpRosterAndVirtualPresses),
    ("competitor rename, archive, restore, and import preserve identity", CompetitorRosterManagement),
    ("MVP timing fields clear and manual score overrides add to total", MvpEditableScorecard),
    ("recorded exhibition correction extends timeline and can be undone", RecordedExhibitionCorrectionTimeline),
    ("early-finished correction extends timeline while explicit and invalid times remain enforced", FinishedCorrectionTimeline),
    ("armed and paused current corrections remain within current elapsed time", UnfinishedCurrentCorrectionTimelineLimits),
    ("completed event scores and general bonus persist through historical edits", AutomatedScoreAndBonusPersistence),
    ("recording atomically promotes and persists the next on-deck competitor", RecordPromotesNextCompetitor),
    ("reordering on-deck queue persists the requested order", QueueReorderPersists),
    ("regular events automatically finish and freeze the clock until recorded", MvpAutoFinishAndRecord),
    ("undo button presses restores the previous event state and reopens the run", UndoLastButtonPress),
    ("clearing an event resets all event data and permits a new press", ClearEventResult),
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
    ("an official redo replaces the original only when recorded", OfficialRedoReplacesOnlyWhenRecorded),
    ("per-event undo steps back one event without touching others", TargetedEventUndoLeavesOtherEvents),
    ("live scorecard edits reach the TV, allow penalties, and survive presses and timeouts", LiveScorecardEditsReachTheTvIncludingPenalties),
    ("timed-out runs can be undone and their recording survives restart", TimedOutRunUndoAndRecordingPersist),
    ("recording an older run from history keeps the on-deck queue", HistoricalRecordKeepsOnDeckQueue),
    ("discarding a replacement attempt keeps the original official result", DiscardedReplacementKeepsOriginalOfficial),
    ("a second official result cannot be recorded from history", SecondOfficialCannotBeRecordedFromHistory),
    ("corrections cannot create unfinished history or count paused time", CorrectionsCannotCorruptRunLifecycle),
    ("physical press age back-dates presses within the current active stretch", PhysicalPressAgeBackdatesWithinActiveTime),
    ("keypad protocol lines parse strictly and '*' is never part of an entry", KeypadProtocolParsing),
    ("physical keypad spoke starts, rejects wrong codes, shows typing on the TV, and finishes on the code", PhysicalKeypadCodeFlow),
    ("operator tap overrides a running keypad event and keypad runs auto-finish", KeypadOperatorOverrideAndAutoFinish),
    ("keypad events undo their code finish and their start like regular events", KeypadEventUndo),
    ("keypad answer CSV maps each message to its row and column code", KeypadAnswerCsv),
    ("keypad events draw unrepeated messages, need several codes, and undo one code at a time", KeypadMultipleCodes),
    ("the app plays the countdown and starts the run at Go without a browser", ServerCountdownAndSounds),
    ("an unrecorded timed-out run can be discarded; a recorded one cannot", DiscardTimedOutRun),
    ("the bonus round polls, lights targets, counts hits, shrinks windows, and ends the run on a miss", BonusRoundMissEndsRun),
    ("the bonus round ends on timeout, on operator finish, and uses every tile when nothing answers", BonusRoundOtherEndings),
    ("bonus round settings save through setup, validate, and version recorded editions", BonusRoundSetup),
    ("the bonus round is corrected like an event: hits, points override, times, clear, and history", BonusRoundCorrections),
    ("Up Next shows the next competitor on the TV between runs until a run is armed", UpNextOnTheTv),
    ("the TV gets each finished event's elapsed time", ScoreboardEventDurations),
    ("schema version 1 databases upgrade once after a backup", SchemaVersion1UpgradesWithBackup),
    ("deleted runs leave history and standings, persist, and can be restored", DeletedRunsHideAndRestore),
    ("deleting a recorded redo restores the result it replaced", DeletingRedoRestoresOriginal),
    ("category exclusion and shared tie rank", CategoryAndTieRank),
    ("leaderboard preference persists and playoffs precede official and exhibition rows", LeaderboardPreferenceAndCategories),
    ("persistent recovery and exclusive data lock", RecoveryAndLock),
    ("danger-zone clear backs up first and resets persisted and runtime state", ClearDatabaseSafety),
    ("failed backup prevents database clearing", ClearDatabaseBackupFailurePreservesData),
    ("legacy migration preserves old data and prefers an existing current store", LegacyDataMigrationSafety),
    ("build identity follows source content and ignores build output", BuildIdentityFingerprint),
    ("live edit isolation, history edit, undo, and stale undo", EditsAndIsolation),
    ("unknown database is rejected", UnknownDatabase),
    ("static asset versions track content and stamp HTML references", StaticAssetVersioningTests.ContentDerivedVersionAndStamping)
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
    var testPress = new MasterButtonTestPress("boot-A_1", "AABBCCDDEEFF", 12);
    Assert.Equal("GG1 TEST boot-A_1 AABBCCDDEEFF 12", MasterProtocolCodec.FormatButtonTest(testPress));
    Assert.True(MasterProtocolCodec.TryParseButtonTest("GG1 TEST boot-A_1 AABBCCDDEEFF 12", out var parsedTest));
    Assert.Equal(testPress, parsedTest);
    Assert.True(!MasterProtocolCodec.TryParseButtonTest("GG1 TEST boot-A_1 aabbccddeeff 12", out _));
    Assert.True(!MasterProtocolCodec.TryParseButtonTest("GG1 TEST boot-A_1 AABBCCDDEEFF 0", out _));
    Assert.Equal("GG1 IDENTIFY AABBCCDDEEFF 9", MasterProtocolCodec.FormatIdentifyCommand("aabbccddeeff", 9));
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
    Assert.Equal(0u, press.AgeMilliseconds);
    Assert.True(MasterProtocolCodec.TryParsePhysicalPress(
        "GG1 PRESS boot-A_1 0011223344556677 AABBCCDDEEFF 42 850", out var agedPress));
    Assert.Equal(850u, agedPress.AgeMilliseconds);
    Assert.Equal(42u, agedPress.Sequence);
    Assert.True(!MasterProtocolCodec.TryParsePhysicalPress(
        "GG1 PRESS boot-A_1 0011223344556677 AABBCCDDEEFF 42 -5", out _));
    Assert.True(!MasterProtocolCodec.TryParsePhysicalPress(
        "GG1 PRESS boot-A_1 0011223344556677 AABBCCDDEEFF 42 850 1", out _));

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
    Assert.Equal("GG1 EVENT 0011223344556677 17 AABBCCDDEEFF PENDING",
        MasterProtocolCodec.FormatGarageEventStatus(new MasterGarageEventStatus(
            "0011223344556677", 17, "aabbccddeeff", "PENDING")));
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
    Assert.Equal(DeviceAvailability.Offline,
        h.Service.GetOperatorSnapshot().Devices.Single(d => d.DeviceId == firstMac).Availability);
    // A press from the MAC assigned to this event proves the spoke is alive even though it
    // missed the latest scan, so it is accepted and the spoke is marked online.
    protocol.ProcessLine($"GG1 PRESS boot-first {runToken} {firstMac} 3", ReceiveStart, ReceivePress);
    Assert.Equal(MessageDisposition.Accepted, received!.Disposition);
    Assert.Equal("ACTIVE", received.State);
    Assert.Equal(DeviceAvailability.Online,
        h.Service.GetOperatorSnapshot().Devices.Single(d => d.DeviceId == firstMac).Availability);
    Assert.Equal<long?>(0L, h.Service.GetOperatorSnapshot().CurrentRun!.Events.Single(e => e.DeviceId == firstMac).StartElapsedMs);

    h.Clock.Advance(TimeSpan.FromMilliseconds(1_300));
    protocol.Reset();
    protocol.ProcessLine("GG1 HELLO boot-after-restart", ReceiveStart, ReceivePress);
    protocol.ProcessLine("GG1 MODE IDLE", ReceiveStart, ReceivePress);
    protocol.ProcessLine($"GG1 PRESS boot-after-restart {runToken} {firstMac} 3", ReceiveStart, ReceivePress);
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
    protocol.ProcessLine($"GG1 PRESS boot-after-restart {runToken} {firstMac} 3", ReceiveStart, ReceivePress);
    Assert.Equal(MessageDisposition.Duplicate, received!.Disposition);
    Assert.Equal("COMPLETED", received.State);
}

static void PhysicalButtonStateSnapshotTracksMixedInputs()
{
    const string firstMac = "AABBCCDDEEFF";
    const string secondMac = "001122334455";
    using var h = new TestHarness(MakeSpokeEdition(), NewPath(), simulatedDevicesOnline: false);
    h.Service.RecordDeviceScan(true, true, [firstMac, secondMac]);
    var run = h.Service.Arm(h.Service.GetOperatorSnapshot().Queue.Single().Id);
    h.StartRun();
    var runToken = MasterProtocolCodec.GetGarageRunToken(run.Id)!;

    var snapshot = h.Service.GetMasterStatuses().EventSnapshot;
    Assert.True(snapshot.Version is not null);
    Assert.Equal(2, snapshot.Events.Count);
    Assert.True(snapshot.Events.All(item => item.State == "PENDING"));

    var physicalStart = h.Service.ReceivePhysicalSpokePress(
        new MasterPhysicalPress("boot-test", runToken, firstMac, 1), sessionAllowed: true);
    Assert.Equal(MessageDisposition.Accepted, physicalStart.Disposition);
    Assert.Equal("ACTIVE", h.Service.GetMasterStatuses().EventSnapshot.Events.Single(item => item.DeviceId == firstMac).State);

    Assert.Equal(MessageDisposition.Accepted, h.Service.PressEvent(run.Id, "spoke-event-01").Disposition);
    Assert.Equal("COMPLETED", h.Service.GetMasterStatuses().EventSnapshot.Events.Single(item => item.DeviceId == firstMac).State);

    var undoFinish = h.Service.UndoLastEventPress();
    Assert.Equal(EventStatus.Active, undoFinish.Events.Single(item => item.DeviceId == firstMac).Status);
    Assert.Equal("ACTIVE", h.Service.GetMasterStatuses().EventSnapshot.Events.Single(item => item.DeviceId == firstMac).State);

    var undoStart = h.Service.UndoLastEventPress();
    Assert.Equal(EventStatus.Pending, undoStart.Events.Single(item => item.DeviceId == firstMac).Status);
    Assert.Equal("PENDING", h.Service.GetMasterStatuses().EventSnapshot.Events.Single(item => item.DeviceId == firstMac).State);

    Assert.Equal(MessageDisposition.Accepted, h.Service.PressEvent(run.Id, "spoke-event-01").Disposition);
    var physicalFinish = h.Service.ReceivePhysicalSpokePress(
        new MasterPhysicalPress("boot-test", runToken, firstMac, 2), sessionAllowed: true);
    Assert.Equal(MessageDisposition.Accepted, physicalFinish.Disposition);
    Assert.Equal("COMPLETED", h.Service.GetMasterStatuses().EventSnapshot.Events.Single(item => item.DeviceId == firstMac).State);

    h.Service.Finish();
    var finishedSnapshot = h.Service.GetMasterStatuses().EventSnapshot;
    Assert.Equal(null, finishedSnapshot.Version);
    Assert.Equal(0, finishedSnapshot.Events.Count);
}

static EditionDefinition MakeSpokeEdition() => new()
{
    EditionId = "spoke-test-edition",
    Name = "Physical spoke test edition",
    DurationLimitSeconds = 30,
    Scoring = new ScoringRule(),
    BonusGame = new BonusGameSettings { Enabled = false },
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
    Assert.Equal(0L, h.Service.GetCountdownState().ElapsedMilliseconds);
    Assert.Equal($"{{\"runId\":\"{countdown.Id}\",\"status\":\"countdown\",\"elapsedMilliseconds\":0}}",
        JsonSerializer.Serialize(h.Service.GetCountdownState(), JsonDefaults.Options));
    Assert.True(h.Service.GetOperatorSnapshot().Devices.All(device => device.Led == LedState.Countdown));

    h.Clock.Advance(TimeSpan.FromSeconds(40));
    Assert.Equal(40_000L, h.Service.GetCountdownState().ElapsedMilliseconds);
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
    Assert.Equal("{\"runId\":null,\"status\":null,\"elapsedMilliseconds\":0}",
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

static void ArmRequestDurationHandoff()
{
    const string json = """{"competitorId":"player-1","category":"exhibition","durationLimitSeconds":10}""";
    var request = JsonSerializer.Deserialize<StartCompetitorRunRequest>(json, JsonDefaults.Options)!;
    Assert.Equal("player-1", request.CompetitorId);
    Assert.Equal(RunCategory.Exhibition, request.Category);
    Assert.Equal(10, RunService.RequireRequestedRunDuration(request.DurationLimitSeconds));

    var missingDuration = JsonSerializer.Deserialize<StartCompetitorRunRequest>(
        """{"competitorId":"player-1","category":"exhibition"}""", JsonDefaults.Options)!;
    Assert.Throws<CommandException>(() => RunService.RequireRequestedRunDuration(missingDuration.DurationLimitSeconds));

    using var h = new TestHarness(MakeMvpEdition(durationSeconds: 300), NewPath());
    var run = h.Service.ArmCompetitor(h.CompetitorId, request.Category,
        RunService.RequireRequestedRunDuration(request.DurationLimitSeconds));
    Assert.Equal(10, run.Edition.DurationLimitSeconds);
    Assert.Equal(new MasterRunStatus("ARMED", 10), h.Service.GetMasterStatus());
    Assert.Equal(10_000L, h.Service.GetScoreboard().CurrentRun!.RemainingMilliseconds);

    h.StartRun();
    h.Clock.Advance(TimeSpan.FromMilliseconds(9_999));
    Assert.Equal(RunStatus.Active, h.Service.GetOperatorSnapshot().CurrentRun!.Status);
    h.Clock.Advance(TimeSpan.FromMilliseconds(1));
    var timedOut = h.Service.GetOperatorSnapshot().History.Single(item => item.Id == run.Id);
    Assert.Equal(RunStatus.TimedOut, timedOut.Status);
    Assert.Equal(10, timedOut.Edition.DurationLimitSeconds);
    Assert.Equal(300, h.Service.GetOperatorSnapshot().DurationLimitSeconds);
}

static void MvpRosterAndVirtualPresses()
{
    var editionPath = Path.Combine(Environment.CurrentDirectory, "config", "edition-2026.json");
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

static void CompetitorRosterManagement()
{
    using var h = new TestHarness(MakeMvpEdition(), NewPath());
    var imported = h.Service.ImportCompetitors(["Alex", "  alex   ", "", "Taylor"]);
    Assert.Equal(2, imported.Added.Count);
    Assert.Equal(2, imported.Skipped.Count);
    Assert.True(imported.Skipped.Any(item => item.Contains("duplicate", StringComparison.OrdinalIgnoreCase)));

    var alex = imported.Added.Single(item => item.Name == "Alex");
    var taylor = imported.Added.Single(item => item.Name == "Taylor");
    Assert.Throws<CommandException>(() => h.Service.RenameCompetitor(taylor.Id, "  aLeX "));
    var renamed = h.Service.RenameCompetitor(taylor.Id, "Taylor New");
    Assert.Equal(taylor.Id, renamed.Id);
    Assert.Equal("Taylor New", renamed.Name);

    h.Service.SetCompetitorArchived(alex.Id, true);
    Assert.True(h.Service.GetOperatorSnapshot().Competitors.Single(item => item.Id == alex.Id).IsArchived);
    Assert.Throws<CommandException>(() => h.Service.AddToQueue(alex.Id, RunCategory.Exhibition));
    h.Service.SetCompetitorArchived(alex.Id, false);
    Assert.True(!h.Service.GetOperatorSnapshot().Competitors.Single(item => item.Id == alex.Id).IsArchived);

    h.Service.SetCompetitorArchived(alex.Id, true);
    h.Store.Dispose();
    using var reopened = new RunStore(h.Path);
    var persisted = reopened.Load().Competitors.Single(item => item.Id == alex.Id);
    Assert.True(persisted.IsArchived);
    Assert.Equal("Taylor New", reopened.Load().Competitors.Single(item => item.Id == taylor.Id).Name);
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

static void UndoLastButtonPress()
{
    var edition = new EditionDefinition
    {
        EditionId = "undo-button-edition",
        Name = "Undo button test",
        DurationLimitSeconds = 60,
        Scoring = new ScoringRule(),
        BonusGame = new BonusGameSettings { Enabled = false },
        Events = [new EventDefinition { EventId = "one", Name = "One event", DeviceId = "station-01", Type = EventKind.Standard }]
    };
    using var h = new TestHarness(edition, NewPath());
    var run = h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Official);
    h.StartRun();
    Assert.Equal(MessageDisposition.Accepted, h.Service.PressEvent(run.Id, "one").Disposition);
    h.Clock.Advance(TimeSpan.FromSeconds(2));
    Assert.Equal(RunStatus.Finished, h.Service.PressEvent(run.Id, "one").Run!.Status);

    var undoneFinish = h.Service.UndoLastEventPress();
    Assert.Equal(RunStatus.Active, undoneFinish.Status);
    Assert.Equal(EventStatus.Active, undoneFinish.Events.Single().Status);
    Assert.Equal(0L, undoneFinish.Events.Single().StartElapsedMs);
    Assert.Equal(null, undoneFinish.Events.Single().FinishElapsedMs);
    Assert.Contains(h.Service.GetOperatorSnapshot().Messages, message => message.Type == "event-press" &&
        message.ElapsedMilliseconds == 2_000 && message.Disposition == MessageDisposition.Undone);

    var undoneStart = h.Service.UndoLastEventPress();
    Assert.Equal(RunStatus.Active, undoneStart.Status);
    Assert.Equal(EventStatus.Pending, undoneStart.Events.Single().Status);
    Assert.Equal(null, undoneStart.Events.Single().StartElapsedMs);
    Assert.Equal(2, h.Service.GetOperatorSnapshot().Edits.Count(edit => edit.RunId == run.Id));
    Assert.Equal(0, h.Service.GetOperatorSnapshot().Messages.Count(message => message.Type == "event-press" && message.Disposition == MessageDisposition.Accepted));
    Assert.Equal(MessageDisposition.Accepted, h.Service.PressEvent(run.Id, "one").Disposition);
}

static void ClearEventResult()
{
    var edition = new EditionDefinition
    {
        EditionId = "clear-event-edition",
        Name = "Clear event test",
        DurationLimitSeconds = 60,
        Scoring = new ScoringRule(),
        BonusGame = new BonusGameSettings { Enabled = false },
        Events = [new EventDefinition { EventId = "one", Name = "One event", DeviceId = "station-01", Type = EventKind.Standard }]
    };
    using var h = new TestHarness(edition, NewPath());
    var run = h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Official);
    h.StartRun();
    h.Service.PressEvent(run.Id, "one");
    h.Clock.Advance(TimeSpan.FromSeconds(2));
    var finished = h.Service.PressEvent(run.Id, "one").Run!;
    Assert.Equal(RunStatus.Finished, finished.Status);

    var cleared = h.Service.ClearEvent(run.Id, "one", finished.Revision);
    Assert.Equal(RunStatus.Active, cleared.Status);
    Assert.Equal(EventStatus.Pending, cleared.Events.Single().Status);
    Assert.Equal(null, cleared.Events.Single().StartElapsedMs);
    Assert.Equal(null, cleared.Events.Single().FinishElapsedMs);
    Assert.Equal(1, h.Service.GetOperatorSnapshot().Edits.Count(edit => edit.RunId == run.Id));
    Assert.Equal(2, h.Service.GetOperatorSnapshot().Messages.Count(message => message.Type == "event-press" && message.Disposition == MessageDisposition.Accepted));

    Assert.Equal(MessageDisposition.Accepted, h.Service.PressEvent(run.Id, "one").Disposition);
    h.Clock.Advance(TimeSpan.FromSeconds(1));
    h.Service.PressEvent(run.Id, "one");
    var recorded = h.Service.Record();
    var historicallyCleared = h.Service.ClearEvent(run.Id, "one", recorded.Revision);
    Assert.True(historicallyCleared.IsRecorded, "Clearing a recorded result must not erase its recorded status.");
    Assert.Equal(EventStatus.Pending, historicallyCleared.Events.Single().Status);
    Assert.Equal(2, h.Service.GetOperatorSnapshot().Edits.Count(edit => edit.RunId == run.Id));
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
    var completedCheckTime = h.Service.GetOperatorSnapshot().DeviceScanCheckedAt;
    Assert.Equal(h.Clock.UtcNow, completedCheckTime);

    var disconnected = h.Service.RecordDeviceScan(false, false, ["AABBCCDDEEFF"]);
    Assert.True(!disconnected.Connected && !disconnected.Completed);
    Assert.True(h.Service.GetOperatorSnapshot().Devices.All(device =>
        device.Availability == DeviceAvailability.Unverified && device.LastSeenAt is null));
    Assert.Equal(completedCheckTime, h.Service.GetOperatorSnapshot().DeviceScanCheckedAt);
    var databasePath = h.Path;
    h.Store.Dispose();
    using var reopened = new RunStore(databasePath);
    Assert.Equal(completedCheckTime, reopened.Load().DeviceScanCheckedAt);
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
    // The original result stands until the replacement is actually recorded.
    var history = h.Service.GetOperatorSnapshot().History;
    Assert.Equal(RunStatus.Completed, history.Single(r => r.Id == first.Id).Status);
    Assert.Equal(first.Id, h.Service.GetOperatorSnapshot().Leaderboard.Single().RunId);

    h.Service.Finish();
    h.Service.Record();
    Assert.Equal(RunStatus.Superseded, h.Service.GetOperatorSnapshot().History.Single(r => r.Id == first.Id).Status);
    Assert.Equal(replacement.Id, h.Service.GetOperatorSnapshot().Leaderboard.Single().RunId);
    Assert.Equal(RunStatus.Superseded, h.Store.Load().Runs.Single(r => r.Id == first.Id).Status);
    var secondOfficial = h.Service.AddToQueue(h.CompetitorId, RunCategory.Official);
    Assert.Throws<CommandException>(() => h.Service.Arm(secondOfficial.Id));
}

static void LiveScorecardEditsReachTheTvIncludingPenalties()
{
    var path = NewPath();
    var edition = MakeMvpEdition(durationSeconds: 60);
    var clock = new TestClock();
    var store = new RunStore(path);
    var service = new RunService(store, edition, clock);
    var competitor = service.AddCompetitor("Penalty tester");
    var run = service.ArmCompetitor(competitor.Id, RunCategory.Official, 60);
    service.CompleteCountdown(service.StartMaster().Id);
    service.PressEvent(run.Id, "event-01");
    clock.Advance(TimeSpan.FromSeconds(2));
    service.PressEvent(run.Id, "event-01");
    var staleRevision = service.GetOperatorSnapshot().CurrentRun!.Revision;
    service.PressEvent(run.Id, "event-02"); // A press lands while the operator is typing.

    // Negative points and a negative bonus subtract, and the save applies despite the press.
    var edited = service.EditRun(run.Id, new EditRunRequest
    {
        ExpectedRevision = staleRevision,
        Reason = "Penalties",
        BonusPointsOverride = -5,
        Events = [new EventEditRequest { EventId = "event-01", ScoreOverride = -10 }]
    });
    Assert.Equal(-10, edited.Events.Single(e => e.EventId == "event-01").Score);
    Assert.Equal(EventStatus.Active, edited.Events.Single(e => e.EventId == "event-02").Status); // The press survived.
    Assert.Equal(-15, service.GetScoreboard().CurrentRun!.AwardedPoints);
    Assert.Equal(-10, service.GetScoreboard().CurrentRun!.Events.Single(e => e.Name == "Perfect Pour").AwardedPoints);
    Assert.Throws<CommandException>(() => service.EditRun(run.Id, new EditRunRequest
    {
        ExpectedRevision = edited.Revision,
        Reason = "Too large",
        Events = [new EventEditRequest { EventId = "event-01", ScoreOverride = -(RunService.MaximumManualPoints + 1) }]
    }));

    // After a timeout the same scorecard edits still save, then the run records.
    var beforeTimeout = service.GetOperatorSnapshot().CurrentRun!.Revision;
    clock.Advance(TimeSpan.FromSeconds(70));
    Assert.Equal(RunStatus.TimedOut, service.GetScoreboard().CurrentRun!.Status);
    var afterTimeout = service.EditRun(run.Id, new EditRunRequest
    {
        ExpectedRevision = beforeTimeout,
        Reason = "Saved after time ran out",
        BonusPointsOverride = 25
    });
    Assert.Equal(25, afterTimeout.BonusPointsOverride);
    Assert.Equal(15, service.GetScoreboard().CurrentRun!.AwardedPoints); // -10 + 25
    service.RecordHistoricalRun(run.Id);
    Assert.Equal(15, service.GetScoreboard().Leaderboard.Single().Points);

    // Runs that are no longer on screen keep the stale-revision check.
    var next = service.ArmCompetitor(competitor.Id, RunCategory.Exhibition, 60);
    Assert.Throws<CommandException>(() => service.EditRun(run.Id, new EditRunRequest
    {
        ExpectedRevision = afterTimeout.Revision - 1,
        Reason = "Stale history edit",
        BonusPointsOverride = 0
    }));
    service.Abort("Cleanup");
    store.Dispose();

    var reopenedStore = new RunStore(path);
    var reopened = new RunService(reopenedStore, edition, new TestClock());
    var saved = reopened.GetOperatorSnapshot().History.Single(r => r.Id == run.Id);
    Assert.Equal(-10, saved.Events.Single(e => e.EventId == "event-01").Score);
    Assert.Equal(15, reopened.GetScoreboard().Leaderboard.Single().Points);
    reopenedStore.Dispose();
    Cleanup(path);
}

static void TargetedEventUndoLeavesOtherEvents()
{
    using var h = new TestHarness(MakeMvpEdition(), NewPath());
    var run = h.ArmAndStart();
    foreach (var eventId in new[] { "event-01", "event-02" })
    {
        h.Service.PressEvent(run.Id, eventId);
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        h.Service.PressEvent(run.Id, eventId);
    }
    h.Service.PressEvent(run.Id, "event-03");
    var staleRevision = h.Service.GetOperatorSnapshot().CurrentRun!.Revision;

    EventStatus StatusOf(RunRecord current, string eventId) => current.Events.Single(e => e.EventId == eventId).Status;

    // Undoing event 1 steps back only its finish; later presses on events 2 and 3 stay.
    var afterFinishUndo = h.Service.UndoEventPress("event-01");
    Assert.Equal(EventStatus.Active, StatusOf(afterFinishUndo, "event-01"));
    Assert.Equal(EventStatus.Completed, StatusOf(afterFinishUndo, "event-02"));
    Assert.Equal(EventStatus.Active, StatusOf(afterFinishUndo, "event-03"));
    var afterStartUndo = h.Service.UndoEventPress("event-01");
    Assert.Equal(EventStatus.Pending, StatusOf(afterStartUndo, "event-01"));
    Assert.Throws<CommandException>(() => h.Service.UndoEventPress("event-01"));

    // The global undo still takes back the latest effective press (event 3's start).
    var globalUndo = h.Service.UndoLastEventPress();
    Assert.Equal(EventStatus.Pending, StatusOf(globalUndo, "event-03"));
    Assert.Equal(EventStatus.Completed, StatusOf(globalUndo, "event-02"));
    // Event 1's finish and start, then event 3's start, are marked undone in the ledger.
    Assert.Equal(3, h.Service.GetOperatorSnapshot().Messages.Count(m => m.Disposition == MessageDisposition.Undone));

    // An event whose times came from a scorecard edit can still be stepped back.
    var edited = h.Service.EditCurrentRun(new EditRunRequest
    {
        ExpectedRevision = globalUndo.Revision,
        Reason = "Entered by hand",
        Events = [new EventEditRequest { EventId = "event-04", StartElapsedMs = 1_000, FinishElapsedMs = 3_000 }]
    });
    Assert.Equal(EventStatus.Completed, StatusOf(edited, "event-04"));
    Assert.Equal(EventStatus.Active, StatusOf(h.Service.UndoEventPress("event-04"), "event-04"));

    // Clearing an event on the live run no longer fails on a revision that is seconds old.
    var cleared = h.Service.ClearEvent(run.Id, "event-02", staleRevision);
    Assert.Equal(EventStatus.Pending, StatusOf(cleared, "event-02"));
    Assert.Equal(EventStatus.Pending, h.Store.Load().Runs.Single(r => r.Id == run.Id).Events.Single(e => e.EventId == "event-02").Status);
}

static void OfficialRedoReplacesOnlyWhenRecorded()
{
    using var h = new TestHarness(MakeMvpEdition(), NewPath());
    foreach (var item in h.Service.GetOperatorSnapshot().Queue) h.Service.RemoveFromQueue(item.Id);
    var original = h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Official, 300);
    h.StartRun();
    h.Service.Finish();
    h.Service.Record();

    // Without an explicit redo, a second official run is still refused.
    Assert.Throws<CommandException>(() => h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Official, 300));

    // A discarded redo leaves the original official result untouched.
    var discarded = h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Official, 300, replaceExistingOfficial: true);
    Assert.Equal(original.Id, discarded.SupersedesRunId);
    Assert.Equal(original.Id, h.Service.GetScoreboard().Leaderboard.Single().RunId);
    h.Service.Abort("Redo discarded");
    Assert.Equal(RunStatus.Completed, h.Service.GetOperatorSnapshot().History.Single(r => r.Id == original.Id).Status);
    Assert.Equal(original.Id, h.Service.GetScoreboard().Leaderboard.Single().RunId);

    // A recorded redo replaces the original, and both changes persist.
    var redo = h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Official, 300, replaceExistingOfficial: true);
    h.StartRun();
    Assert.Equal(original.Id, h.Service.GetScoreboard().Leaderboard.Single().RunId); // Still the original mid-redo.
    h.Service.Finish();
    h.Service.Record();
    Assert.Equal(redo.Id, h.Service.GetScoreboard().Leaderboard.Single().RunId);
    var persisted = h.Store.Load().Runs;
    Assert.Equal(RunStatus.Superseded, persisted.Single(r => r.Id == original.Id).Status);
    Assert.Equal(redo.Id, persisted.Single(r => r.Id == original.Id).SupersededByRunId);
    Assert.True(persisted.Single(r => r.Id == redo.Id).IsCountedOfficial);

    // The flag is ignored for competitors without an official result and for other categories.
    var newcomer = h.AddCompetitor("Newcomer");
    var first = h.Service.ArmCompetitor(newcomer.Id, RunCategory.Official, 300, replaceExistingOfficial: true);
    Assert.Equal(null, first.SupersedesRunId);
    h.Service.Abort("Cleanup");
    var exhibition = h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Exhibition, 300, replaceExistingOfficial: true);
    Assert.Equal(null, exhibition.SupersedesRunId);
}

static void TimedOutRunUndoAndRecordingPersist()
{
    var path = NewPath();
    var edition = MakeMvpEdition(durationSeconds: 2);
    var clock = new TestClock();
    var store = new RunStore(path);
    var service = new RunService(store, edition, clock);
    var competitor = service.AddCompetitor("Timed-out competitor");
    var run = service.ArmCompetitor(competitor.Id, RunCategory.Official, 2);
    service.CompleteCountdown(service.StartMaster().Id);
    service.PressEvent(run.Id, "event-01");
    clock.Advance(TimeSpan.FromMilliseconds(500));
    service.PressEvent(run.Id, "event-01");
    clock.Advance(TimeSpan.FromSeconds(2));
    Assert.Equal(RunStatus.TimedOut, service.GetOperatorSnapshot().CurrentRun!.Status);

    // The displayed timed-out run used to be a detached copy, so undo threw.
    var undone = service.UndoLastEventPress();
    Assert.Equal(EventStatus.Active, undone.Events.Single(e => e.EventId == "event-01").Status);
    Assert.Equal(EventStatus.Active,
        service.GetOperatorSnapshot().History.Single(r => r.Id == run.Id).Events.Single(e => e.EventId == "event-01").Status);

    Assert.True(service.Record().IsRecorded);
    Assert.True(service.GetOperatorSnapshot().History.Single(r => r.Id == run.Id).IsRecorded);
    Assert.Equal(run.Id, service.GetScoreboard().Leaderboard.Single().RunId);
    store.Dispose();

    // RecordedAt used to live only in memory, so the result vanished on restart.
    var reopenedStore = new RunStore(path);
    var reopened = new RunService(reopenedStore, edition, new TestClock());
    Assert.True(reopened.GetOperatorSnapshot().History.Single(r => r.Id == run.Id).IsRecorded);
    Assert.Equal(run.Id, reopened.GetScoreboard().Leaderboard.Single().RunId);
    reopenedStore.Dispose();
    Cleanup(path);
}

static void HistoricalRecordKeepsOnDeckQueue()
{
    using var h = new TestHarness(MakeMvpEdition(durationSeconds: 2), NewPath());
    foreach (var item in h.Service.GetOperatorSnapshot().Queue) h.Service.RemoveFromQueue(item.Id);
    var old = h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Official, 2);
    h.StartRun();
    h.Clock.Advance(TimeSpan.FromSeconds(3));
    Assert.Equal(RunStatus.TimedOut, h.Service.GetOperatorSnapshot().CurrentRun!.Status);

    var next = h.AddCompetitor("Next competitor");
    var onDeckCompetitor = h.AddCompetitor("On-deck competitor");
    h.Service.ArmCompetitor(next.Id, RunCategory.Official, 60);
    var onDeck = h.Service.AddToQueue(onDeckCompetitor.Id, RunCategory.Official);

    h.Service.RecordHistoricalRun(old.Id);
    Assert.Equal(onDeck.Id, h.Service.GetOperatorSnapshot().Queue.Single().Id);
    Assert.Equal(onDeck.Id, h.Store.Load().Queue.Single().Id);
}

static void DiscardedReplacementKeepsOriginalOfficial()
{
    using var h = NewHarness();
    var first = h.ArmAndStart();
    h.Service.Finish();
    h.Service.Record();
    var restart = h.Service.Restart(first.Id, "Retry requested");
    h.Service.Arm(restart.Id);
    h.Service.Abort("Competitor declined the retry");

    Assert.Equal(first.Id, h.Service.GetOperatorSnapshot().Leaderboard.Single().RunId);
    Assert.Equal(RunStatus.Completed, h.Store.Load().Runs.Single(r => r.Id == first.Id).Status);
}

static void SecondOfficialCannotBeRecordedFromHistory()
{
    using var h = new TestHarness(MakeMvpEdition(durationSeconds: 2), NewPath());
    foreach (var item in h.Service.GetOperatorSnapshot().Queue) h.Service.RemoveFromQueue(item.Id);
    var timedOut = h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Official, 2);
    h.StartRun();
    h.Clock.Advance(TimeSpan.FromSeconds(3));
    Assert.Equal(RunStatus.TimedOut, h.Service.GetOperatorSnapshot().CurrentRun!.Status);

    var second = h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Official, 60);
    h.StartRun();
    h.Service.Finish();
    h.Service.Record();

    Assert.Throws<CommandException>(() => h.Service.RecordHistoricalRun(timedOut.Id));
    Assert.Equal(second.Id, h.Service.GetScoreboard().Leaderboard.Single(r => r.Category == RunCategory.Official).RunId);
    Assert.True(!h.Store.Load().Runs.Single(r => r.Id == timedOut.Id).IsRecorded);
}

static void CorrectionsCannotCorruptRunLifecycle()
{
    var path = NewPath();
    var edition = MakeMvpEdition();
    var clock = new TestClock();
    var store = new RunStore(path);
    var service = new RunService(store, edition, clock);
    var competitor = service.AddCompetitor("Correction competitor");
    service.ArmCompetitor(competitor.Id, RunCategory.Exhibition, 60);
    service.CompleteCountdown(service.StartMaster().Id);
    service.Finish();
    var recorded = service.Record();
    foreach (var status in new[] { RunStatus.Finished, RunStatus.Countdown, RunStatus.Active, RunStatus.Armed, RunStatus.Paused })
    {
        Assert.Throws<CommandException>(() => service.EditHistoricalRun(recorded.Id,
            new EditRunRequest { ExpectedRevision = recorded.Revision, Reason = "Invalid status", Status = status }));
    }

    service.ArmCompetitor(competitor.Id, RunCategory.Exhibition, 300);
    service.CompleteCountdown(service.StartMaster().Id);
    clock.Advance(TimeSpan.FromSeconds(10));
    var paused = service.Pause();
    clock.Advance(TimeSpan.FromSeconds(120));
    var resumed = service.EditCurrentRun(new EditRunRequest
    {
        ExpectedRevision = paused.Revision,
        Reason = "Resume through a correction",
        Status = RunStatus.Active
    });
    Assert.Equal(RunStatus.Active, resumed.Status);
    clock.Advance(TimeSpan.FromMilliseconds(1));
    Assert.Equal(10_001L, service.GetOperatorSnapshot().CurrentRun!.ActiveElapsedMs); // Pause time is not counted.
    Assert.Throws<CommandException>(() => service.EditCurrentRun(new EditRunRequest
    {
        ExpectedRevision = service.GetOperatorSnapshot().CurrentRun!.Revision,
        Reason = "Too long",
        DurationLimitSeconds = RunService.MaximumRunDurationSeconds + 1
    }));
    store.Dispose();

    var reopenedStore = new RunStore(path);
    _ = new RunService(reopenedStore, edition, new TestClock());
    reopenedStore.Dispose();
    Cleanup(path);
}

static void PhysicalPressAgeBackdatesWithinActiveTime()
{
    const string firstMac = "AABBCCDDEEFF";
    const string secondMac = "001122334455";
    using var h = new TestHarness(MakeSpokeEdition(), NewPath(), simulatedDevicesOnline: false);
    h.Service.RecordDeviceScan(true, true, [firstMac, secondMac]);
    var run = h.Service.Arm(h.Service.GetOperatorSnapshot().Queue.Single().Id);
    h.StartRun();
    var token = MasterProtocolCodec.GetGarageRunToken(run.Id)!;

    h.Clock.Advance(TimeSpan.FromSeconds(5));
    var start = h.Service.ReceivePhysicalSpokePress(new MasterPhysicalPress("boot", token, firstMac, 1, 1_200), sessionAllowed: true);
    Assert.Equal(MessageDisposition.Accepted, start.Disposition);
    Assert.Equal<long?>(3_800L, h.Service.GetOperatorSnapshot().CurrentRun!.Events.Single(e => e.DeviceId == firstMac).StartElapsedMs);

    h.Clock.Advance(TimeSpan.FromSeconds(2));
    h.Service.Pause();
    h.Clock.Advance(TimeSpan.FromSeconds(30));
    h.Service.Resume();
    h.Clock.Advance(TimeSpan.FromMilliseconds(300));
    // An old age cannot reach back into the pause: the press lands at the resume point.
    var finish = h.Service.ReceivePhysicalSpokePress(new MasterPhysicalPress("boot", token, firstMac, 2, 5_000), sessionAllowed: true);
    Assert.Equal(MessageDisposition.Accepted, finish.Disposition);
    Assert.Equal<long?>(7_000L, h.Service.GetOperatorSnapshot().CurrentRun!.Events.Single(e => e.DeviceId == firstMac).FinishElapsedMs);

    // Ages are capped, so a bogus value never lands before the active stretch either.
    var capped = h.Service.ReceivePhysicalSpokePress(new MasterPhysicalPress("boot", token, secondMac, 1, uint.MaxValue), sessionAllowed: true);
    Assert.Equal(MessageDisposition.Accepted, capped.Disposition);
    Assert.Equal<long?>(7_000L, h.Service.GetOperatorSnapshot().CurrentRun!.Events.Single(e => e.DeviceId == secondMac).StartElapsedMs);
}

static EditionDefinition MakeKeypadSpokeEdition() => new()
{
    EditionId = "keypad-spoke-test-edition",
    Name = "Keypad spoke test edition",
    DurationLimitSeconds = 60,
    Scoring = new ScoringRule(),
    BonusGame = new BonusGameSettings { Enabled = false },
    Events =
    [
        new EventDefinition { EventId = "regular", Name = "Regular", DeviceId = "AABBCCDDEEFF", Type = EventKind.Standard },
        new EventDefinition { EventId = "keypad", Name = "Code Breaker", DeviceId = "001122334455", Type = EventKind.Keypad,
            Prompt = "Summer League 2009", Answer = "D5*" }
    ]
};

static void KeypadProtocolParsing()
{
    const string token = "0123456789ABCDEF";
    Assert.True(MasterProtocolCodec.TryParseKeypadInput($"GG1 KEYPAD boot {token} 001122334455 7 S D5 40", out var submit));
    Assert.True(submit.Submit);
    Assert.Equal("D5", submit.Entry);
    Assert.Equal(7u, submit.Sequence);
    Assert.Equal(40u, submit.AgeMilliseconds);
    Assert.True(MasterProtocolCodec.TryParseKeypadInput($"GG1 KEYPAD boot {token} 001122334455 8 K - 0", out var typing));
    Assert.True(!typing.Submit);
    Assert.Equal("", typing.Entry);
    Assert.True(MasterProtocolCodec.TryParseKeypadInput($"GG1 KEYPAD boot {token} 001122334455 9 K 0123456789#A 0", out _));

    Assert.True(!MasterProtocolCodec.TryParseKeypadInput($"GG1 KEYPAD boot {token} 001122334455 9 S D5* 0", out _));
    Assert.True(!MasterProtocolCodec.TryParseKeypadInput($"GG1 KEYPAD boot {token} 001122334455 9 S d5 0", out _));
    Assert.True(!MasterProtocolCodec.TryParseKeypadInput($"GG1 KEYPAD boot {token} 001122334455 9 S 0123456789ABC 0", out _));
    Assert.True(!MasterProtocolCodec.TryParseKeypadInput($"GG1 KEYPAD boot {token} 001122334455 0 S D5 0", out _));
    Assert.True(!MasterProtocolCodec.TryParseKeypadInput($"GG1 KEYPAD boot {token} 001122334455 9 X D5 0", out _));
    Assert.True(!MasterProtocolCodec.TryParseKeypadInput($"GG1 KEYPAD boot {token} 001122334455 9 S D5", out _));
    Assert.True(!MasterProtocolCodec.TryParseKeypadInput($"GG1 KEYPAD boot {token} 001122334455 9 S  0", out _));
    Assert.Equal($"GG1 RESULT {token} 001122334455 7 ACTIVE", MasterProtocolCodec.FormatKeypadResult(submit, "ACTIVE"));
}

static void PhysicalKeypadCodeFlow()
{
    const string regularMac = "AABBCCDDEEFF";
    const string keypadMac = "001122334455";
    using var h = new TestHarness(MakeKeypadSpokeEdition(), NewPath(), simulatedDevicesOnline: false);
    h.Service.RecordDeviceScan(true, true, [regularMac, keypadMac]);
    var run = h.Service.Arm(h.Service.GetOperatorSnapshot().Queue.Single().Id);
    h.StartRun();
    var token = MasterProtocolCodec.GetGarageRunToken(run.Id)!;

    var protocol = new MasterProtocolState();
    InputResult ReceiveStart(string boot, ulong sequence, bool allowed) => h.Service.ReceivePhysicalMasterStart(boot, sequence, allowed);
    MasterPhysicalPressResult? pressResult = null;
    MasterPhysicalPressResult? keypadResult = null;
    MasterPhysicalPressResult ReceivePress(MasterPhysicalPress item, bool allowed) => pressResult = h.Service.ReceivePhysicalSpokePress(item, allowed);
    MasterPhysicalPressResult? ReceiveKeypad(MasterKeypadInput item, bool allowed) => keypadResult = h.Service.ReceivePhysicalKeypadInput(item, allowed);
    void Line(string line) => protocol.ProcessLine(line, ReceiveStart, ReceivePress, ReceiveKeypad);
    Line("GG1 HELLO boot");
    Line("GG1 MODE IDLE");

    // The message stays hidden until the event is started, and typing before then is ignored.
    Assert.Equal<ScoreboardKeypadChallenge?>(null, h.Service.GetScoreboard().CurrentRun!.KeypadChallenge);
    Assert.Equal<string?>(null, h.Service.GetScoreboard().CurrentRun!.Events.Single(e => e.Name == "Code Breaker").Prompt);
    Line($"GG1 KEYPAD boot {token} {keypadMac} 1 S D5 0");
    Assert.Equal("REJECTED", keypadResult!.State);
    Assert.Equal(EventStatus.Pending, h.Service.GetOperatorSnapshot().CurrentRun!.Events.Single(e => e.DeviceId == keypadMac).Status);

    h.Clock.Advance(TimeSpan.FromSeconds(2));
    Line($"GG1 PRESS boot {token} {keypadMac} 2 0");
    Assert.Equal(MessageDisposition.Accepted, pressResult!.Disposition);
    Assert.Equal("ACTIVE", pressResult.State);
    Assert.Contains(h.Service.GetMasterStatuses().EventSnapshot.Events, item => item.DeviceId == keypadMac && item.State == "ACTIVE");
    var challenge = h.Service.GetScoreboard().CurrentRun!.KeypadChallenge!;
    Assert.Equal("Summer League 2009", challenge.Prompt);
    Assert.Equal("", challenge.Entry);
    Assert.True(!challenge.ShowWrong);

    // A second button press cannot finish it, and the spoke is told the event is still running.
    Line($"GG1 PRESS boot {token} {keypadMac} 3 0");
    Assert.Equal(MessageDisposition.InvalidSignal, pressResult!.Disposition);
    Assert.Equal("ACTIVE", pressResult.State);

    // Typing updates reach the TV without an acknowledgement; stale updates are ignored.
    keypadResult = null;
    Line($"GG1 KEYPAD boot {token} {keypadMac} 5 K D55 0");
    Assert.Equal<MasterPhysicalPressResult?>(null, keypadResult);
    Line($"GG1 KEYPAD boot {token} {keypadMac} 4 K D 0");
    Assert.Equal("D55", h.Service.GetScoreboard().CurrentRun!.KeypadChallenge!.Entry);

    h.Clock.Advance(TimeSpan.FromSeconds(1));
    Line($"GG1 KEYPAD boot {token} {keypadMac} 6 S D55 0");
    Assert.Equal(MessageDisposition.Accepted, keypadResult!.Disposition);
    Assert.Equal("ACTIVE", keypadResult.State);
    challenge = h.Service.GetScoreboard().CurrentRun!.KeypadChallenge!;
    Assert.Equal("", challenge.Entry);
    Assert.True(challenge.ShowWrong);
    h.Clock.Advance(TimeSpan.FromMilliseconds(RunService.KeypadWrongDisplayMilliseconds));
    Assert.True(!h.Service.GetScoreboard().CurrentRun!.KeypadChallenge!.ShowWrong);

    // Keys are not accepted while paused, and the message stays up until the run resumes.
    h.Service.Pause();
    Line($"GG1 KEYPAD boot {token} {keypadMac} 7 S D5 0");
    Assert.Equal(MessageDisposition.Paused, keypadResult!.Disposition);
    Assert.True(h.Service.GetScoreboard().CurrentRun!.KeypadChallenge is not null);
    h.Service.Resume();

    h.Clock.Advance(TimeSpan.FromSeconds(3));
    Line($"GG1 KEYPAD boot {token} {keypadMac} 8 S D5 250");
    Assert.Equal(MessageDisposition.Accepted, keypadResult!.Disposition);
    Assert.Equal("COMPLETED", keypadResult.State);
    var completed = h.Service.GetOperatorSnapshot().CurrentRun!.Events.Single(e => e.DeviceId == keypadMac);
    Assert.Equal(EventStatus.Completed, completed.Status);
    Assert.Equal<long?>(2_000L, completed.StartElapsedMs);
    Assert.Equal<long?>(7_750L, completed.FinishElapsedMs); // The 250 ms press age is back-dated.
    Assert.Equal<ScoreboardKeypadChallenge?>(null, h.Service.GetScoreboard().CurrentRun!.KeypadChallenge);

    // A retransmitted submission is a duplicate that still reports the completed state.
    Line($"GG1 KEYPAD boot {token} {keypadMac} 8 S D5 250");
    Assert.Equal(MessageDisposition.Duplicate, keypadResult!.Disposition);
    Assert.Equal("COMPLETED", keypadResult.State);

    // Codes sent to a regular event are refused.
    Line($"GG1 PRESS boot {token} {regularMac} 1 0");
    Line($"GG1 KEYPAD boot {token} {regularMac} 2 S D5 0");
    Assert.Equal("REJECTED", keypadResult!.State);
    Assert.Equal(EventStatus.Active, h.Service.GetOperatorSnapshot().CurrentRun!.Events.Single(e => e.DeviceId == regularMac).Status);
}

static void KeypadOperatorOverrideAndAutoFinish()
{
    const string keypadMac = "001122334455";
    using var h = new TestHarness(MakeKeypadSpokeEdition(), NewPath());
    var run = h.Service.Arm(h.Service.GetOperatorSnapshot().Queue.Single().Id);
    h.StartRun();
    var token = MasterProtocolCodec.GetGarageRunToken(run.Id)!;

    Assert.Equal(MessageDisposition.Accepted, h.Service.PressEvent(run.Id, "regular").Disposition);
    Assert.Equal(MessageDisposition.Accepted, h.Service.PressEvent(run.Id, "regular").Disposition);
    Assert.Equal(MessageDisposition.Accepted, h.Service.PressEvent(run.Id, "keypad").Disposition);
    Assert.Equal("Summer League 2009", h.Service.GetScoreboard().CurrentRun!.KeypadChallenge!.Prompt);

    // A physical wrong code followed by the operator's override finishes the event.
    var wrong = h.Service.ReceivePhysicalKeypadInput(new MasterKeypadInput("boot", token, keypadMac, 1, true, "1234"), sessionAllowed: true);
    Assert.Equal("ACTIVE", wrong!.State);
    h.Clock.Advance(TimeSpan.FromSeconds(1));
    var overridden = h.Service.PressEvent(run.Id, "keypad");
    Assert.Equal(MessageDisposition.Accepted, overridden.Disposition);
    Assert.Equal(EventStatus.Completed, overridden.Run!.Events.Single(e => e.EventId == "keypad").Status);
    Assert.Contains(h.Service.GetOperatorSnapshot().Messages, message =>
        message.Type == "keypad-success" && message.MessageId.StartsWith("virtual-keypad-override", StringComparison.Ordinal));

    // Regular and keypad events together finish the run like an all-regular roster.
    var finished = h.Service.GetOperatorSnapshot().CurrentRun!;
    Assert.Equal(RunStatus.Finished, finished.Status);
    Assert.Equal(RunPhase.Normal, finished.Phase);
    Assert.Equal<ScoreboardKeypadChallenge?>(null, h.Service.GetScoreboard().CurrentRun!.KeypadChallenge);
}

static void KeypadEventUndo()
{
    const string keypadMac = "001122334455";
    using var h = new TestHarness(MakeKeypadSpokeEdition(), NewPath());
    var run = h.Service.Arm(h.Service.GetOperatorSnapshot().Queue.Single().Id);
    h.StartRun();
    var token = MasterProtocolCodec.GetGarageRunToken(run.Id)!;
    EventRecord Keypad() => h.Service.GetOperatorSnapshot().CurrentRun!.Events.Single(e => e.EventId == "keypad");

    h.Clock.Advance(TimeSpan.FromSeconds(1));
    h.Service.PressEvent(run.Id, "keypad");
    h.Clock.Advance(TimeSpan.FromSeconds(1));
    Assert.Equal("ACTIVE", h.Service.ReceivePhysicalKeypadInput(new MasterKeypadInput("boot", token, keypadMac, 1, true, "99"), true)!.State);
    h.Clock.Advance(TimeSpan.FromSeconds(1));
    Assert.Equal("COMPLETED", h.Service.ReceivePhysicalKeypadInput(new MasterKeypadInput("boot", token, keypadMac, 2, true, "D5"), true)!.State);

    // The run-wide undo takes back the code that finished it; the TV message returns.
    var undoneFinish = h.Service.UndoLastEventPress();
    Assert.Equal(EventStatus.Active, undoneFinish.Events.Single(e => e.EventId == "keypad").Status);
    Assert.Equal<long?>(null, Keypad().FinishElapsedMs);
    Assert.Equal(0, Keypad().Score);
    Assert.Equal("Summer League 2009", h.Service.GetScoreboard().CurrentRun!.KeypadChallenge!.Prompt);
    Assert.Contains(h.Service.GetMasterStatuses().EventSnapshot.Events, item => item.DeviceId == keypadMac && item.State == "ACTIVE");
    Assert.Contains(h.Service.GetOperatorSnapshot().Messages, message =>
        message.MessageId == MasterProtocolCodec.GetKeypadSubmitMessageId(token, keypadMac, 2) && message.Disposition == MessageDisposition.Undone);

    // The event's own undo then takes back its start, even after a wrong code was entered.
    h.Service.UndoEventPress("keypad");
    Assert.Equal(EventStatus.Pending, Keypad().Status);
    Assert.Equal<ScoreboardKeypadChallenge?>(null, h.Service.GetScoreboard().CurrentRun!.KeypadChallenge);
    Assert.Throws<CommandException>(() => h.Service.UndoEventPress("keypad"));

    // An operator override finishing the whole run is undone too, reopening the run.
    h.Service.PressEvent(run.Id, "regular");
    h.Service.PressEvent(run.Id, "regular");
    h.Service.PressEvent(run.Id, "keypad");
    h.Clock.Advance(TimeSpan.FromSeconds(1));
    h.Service.PressEvent(run.Id, "keypad");
    Assert.Equal(RunStatus.Finished, h.Service.GetOperatorSnapshot().CurrentRun!.Status);
    var reopened = h.Service.UndoEventPress("keypad");
    Assert.Equal(RunStatus.Active, reopened.Status);
    Assert.Equal(EventStatus.Active, reopened.Events.Single(e => e.EventId == "keypad").Status);
    Assert.Equal(EventStatus.Completed, reopened.Events.Single(e => e.EventId == "regular").Status);
}

static void KeypadAnswerCsv()
{
    // The shipped answer file: rows A-D by columns 1-16, plus a "##" row.
    var shipped = KeypadChallengeSet.LoadOrReportError(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "config", "keypad-answers.csv")));
    if (!File.Exists(shipped.Source))
    {
        shipped = KeypadChallengeSet.LoadOrReportError(Path.Combine(AppContext.BaseDirectory, "config", "keypad-answers.csv"));
    }
    Assert.Equal<string?>(null, shipped.Error);
    Assert.Equal(66, shipped.Challenges.Count);
    string AnswerFor(string prompt) => shipped.Challenges.Single(c => string.Equals(c.Prompt, prompt, StringComparison.OrdinalIgnoreCase)).Answer;
    Assert.Equal("A2", AnswerFor("Rocket Pepper 1819"));
    Assert.Equal("A1", AnswerFor("maple piano 1453"));
    Assert.Equal("B16", AnswerFor("willow amber 8331"));
    Assert.Equal("D16", AnswerFor("crystal ember 4734"));
    Assert.Equal("##", AnswerFor("wall label 9202"));
    Assert.Equal("##", AnswerFor("zion bark 1809"));

    var quoted = KeypadChallengeCsv.Parse(",1,2\r\nA,\"comma, here\",\"say \"\"hi\"\"\"\r\n,,\r\n##,any\r\n");
    Assert.Equal("A1", quoted.Single(c => c.Prompt == "comma, here").Answer);
    Assert.Equal("A2", quoted.Single(c => c.Prompt == "say \"hi\"").Answer);
    Assert.Equal("##", quoted.Single(c => c.Prompt == "any").Answer);

    Assert.Throws<InvalidDataException>(() => KeypadChallengeCsv.Parse(",1\nA,same\nB,Same\n"));
    Assert.Throws<InvalidDataException>(() => KeypadChallengeCsv.Parse(",1\nE,untypable\n"));
    Assert.Throws<InvalidDataException>(() => KeypadChallengeCsv.Parse(",1\n,orphan\n"));
    Assert.Throws<InvalidDataException>(() => KeypadChallengeCsv.Parse(",1\n"));
    Assert.True(KeypadChallengeSet.LoadOrReportError(Path.Combine(NewPath(), "missing.csv")).Error is not null);
}

static void KeypadMultipleCodes()
{
    const string keypadMac = "001122334455";
    var edition = MakeKeypadSpokeEdition();
    var keypadDefinition = edition.Events.Single(e => e.EventId == "keypad");
    keypadDefinition.Prompt = null;
    keypadDefinition.Answer = null;
    keypadDefinition.RequiredSuccesses = 3;
    var pool = new KeypadChallengeSet(
        Enumerable.Range(1, 5).Select(i => new KeypadChallengeDefinition { Prompt = $"message {i}", Answer = $"A{i}" }).ToList(),
        "test", null);
    var path = NewPath();
    var h = new TestHarness(edition, path, keypadChallenges: pool);
    var run = h.Service.Arm(h.Service.GetOperatorSnapshot().Queue.Single().Id);
    h.StartRun();
    var token = MasterProtocolCodec.GetGarageRunToken(run.Id)!;
    uint sequence = 0;
    EventRecord Keypad() => h.Service.GetOperatorSnapshot().CurrentRun!.Events.Single(e => e.EventId == "keypad");
    string CurrentAnswer() => Keypad().Keypad!.Current!.Answer;
    MasterPhysicalPressResult Submit(string code) =>
        h.Service.ReceivePhysicalKeypadInput(new MasterKeypadInput("boot", token, keypadMac, ++sequence, true, code), true)!;

    h.Service.PressEvent(run.Id, "keypad");
    var challenge = h.Service.GetScoreboard().CurrentRun!.KeypadChallenge!;
    Assert.Equal(0, challenge.Successes);
    Assert.Equal(3, challenge.RequiredSuccesses);
    Assert.Equal(Keypad().Keypad!.Current!.Prompt, challenge.Prompt);

    h.Clock.Advance(TimeSpan.FromSeconds(1));
    var first = Keypad().Keypad!.Current!.Prompt;
    Assert.Equal("ACTIVE", Submit("99").State);
    var next = Submit(CurrentAnswer());
    Assert.Equal("NEXT", next.State);
    Assert.Equal(EventStatus.Active, Keypad().Status);
    challenge = h.Service.GetScoreboard().CurrentRun!.KeypadChallenge!;
    Assert.Equal(1, challenge.Successes);
    Assert.True(challenge.ShowCorrect);
    Assert.True(challenge.Prompt != first, "a solved message is never shown again in the run");

    // Persisted mid-event: a restart keeps the drawn messages and the count.
    var secondPrompt = Keypad().Keypad!.Current!.Prompt;
    h.Store.Dispose();
    var reopenedStore = new RunStore(path);
    var service = new RunService(reopenedStore, edition, h.Clock, pool);
    var recovered = service.GetOperatorSnapshot().CurrentRun!;
    Assert.Equal(1, recovered.Events.Single(e => e.EventId == "keypad").Keypad!.SolvedCount);
    Assert.Equal(secondPrompt, recovered.Events.Single(e => e.EventId == "keypad").Keypad!.Current!.Prompt);
    Assert.Equal(5, recovered.Edition.KeypadChallenges!.Count);
    if (recovered.Status == RunStatus.Paused) service.Resume();

    EventRecord K() => service.GetOperatorSnapshot().CurrentRun!.Events.Single(e => e.EventId == "keypad");
    MasterPhysicalPressResult Send(string code) =>
        service.ReceivePhysicalKeypadInput(new MasterKeypadInput("boot", token, keypadMac, ++sequence, true, code), true)!;

    h.Clock.Advance(TimeSpan.FromSeconds(1));
    var secondCode = K().Keypad!.Current!.Answer;
    var secondMessageSequence = sequence + 1;
    Assert.Equal("NEXT", Send(secondCode).State);
    // A retransmitted correct submission still reports NEXT, never a wrong code.
    Assert.Equal("NEXT", service.ReceivePhysicalKeypadInput(
        new MasterKeypadInput("boot", token, keypadMac, secondMessageSequence, true, secondCode), true)!.State);
    // The operator's tap credits one message, finishing on the third.
    h.Clock.Advance(TimeSpan.FromSeconds(1));
    var third = K().Keypad!.Current!.Prompt;
    Assert.Equal(MessageDisposition.Accepted, service.PressEvent(run.Id, "keypad").Disposition);
    Assert.Equal(EventStatus.Completed, K().Status);
    Assert.Equal(3, K().Keypad!.Challenges.Select(c => c.Prompt).Distinct().Count());
    Assert.Equal<ScoreboardKeypadChallenge?>(null, service.GetScoreboard().CurrentRun!.KeypadChallenge);

    // Undo steps back one code at a time, putting that message back on the TV.
    service.UndoEventPress("keypad");
    Assert.Equal(EventStatus.Active, K().Status);
    Assert.Equal(2, K().Keypad!.SolvedCount);
    Assert.Equal(third, service.GetScoreboard().CurrentRun!.KeypadChallenge!.Prompt);
    service.UndoLastEventPress();
    Assert.Equal(1, K().Keypad!.SolvedCount);
    Assert.Equal(secondPrompt, K().Keypad!.Current!.Prompt);
    Assert.Equal(2, K().Keypad!.Challenges.Count);
    service.UndoEventPress("keypad");
    Assert.Equal(0, K().Keypad!.SolvedCount);
    Assert.Equal(first, K().Keypad!.Current!.Prompt);
    service.UndoEventPress("keypad");
    Assert.Equal(EventStatus.Pending, K().Status);
    Assert.Equal<KeypadProgress?>(null, K().Keypad);
    Assert.Contains(service.GetOperatorSnapshot().Edits, edit => edit.Reason == "Undid the latest code 3 of 3 for 'Code Breaker'.");

    // A scorecard reset forgets the messages; restarting draws afresh.
    service.PressEvent(run.Id, "keypad");
    Assert.True(K().Keypad!.Current is not null);
    service.ClearEvent(run.Id, "keypad", 0);
    Assert.Equal<KeypadProgress?>(null, K().Keypad);
    reopenedStore.Dispose();
    Cleanup(path);
}

static void ServerCountdownAndSounds()
{
    using var h = new TestHarness(MakeKeypadSpokeEdition(), NewPath());
    var sounds = new RecordingSoundPlayer();
    h.Service.SoundCueRequested += cue => sounds.Play(cue);
    var countdown = new RunTimingHostedService(h.Service, sounds);

    Assert.True(!countdown.TickCountdown());
    var run = h.Service.Arm(h.Service.GetOperatorSnapshot().Queue.Single().Id);
    Assert.True(!countdown.TickCountdown());
    Assert.Equal(0, sounds.Played.Count);

    h.Service.StartMaster();
    Assert.True(!countdown.TickCountdown());
    Assert.Equal(SoundCue.Countdown, sounds.Played.Single());
    h.Clock.Advance(TimeSpan.FromMilliseconds(RunTimingHostedService.GoAtMilliseconds - 1));
    Assert.True(!countdown.TickCountdown());
    Assert.Equal(1, sounds.Played.Count); // The voice plays once per countdown.
    Assert.Equal(RunStatus.Countdown, h.Service.GetCountdownState().Status);
    h.Clock.Advance(TimeSpan.FromMilliseconds(1));
    Assert.True(countdown.TickCountdown());
    Assert.Equal(RunStatus.Active, h.Service.GetOperatorSnapshot().CurrentRun!.Status);
    Assert.True(!countdown.TickCountdown());
    // The page's backup Go afterwards is harmless.
    Assert.Equal(RunStatus.Active, h.Service.CompleteCountdown(run.Id).Status);

    // Each keypad message drawn plays the chime.
    h.Service.PressEvent(run.Id, "keypad");
    Assert.Equal(SoundCue.KeypadMessage, sounds.Played.Last());
    Assert.Equal(2, sounds.Played.Count);

    // A countdown first noticed well after it began still starts on time, but without a
    // voice that would be out of step with Go.
    using var late = new TestHarness(MakeKeypadSpokeEdition(), NewPath());
    var lateSounds = new RecordingSoundPlayer();
    var lateCountdown = new RunTimingHostedService(late.Service, lateSounds);
    late.Service.Arm(late.Service.GetOperatorSnapshot().Queue.Single().Id);
    late.Service.StartMaster();
    late.Clock.Advance(TimeSpan.FromMilliseconds(1_200));
    Assert.True(!lateCountdown.TickCountdown());
    Assert.Equal(0, lateSounds.Played.Count);
    late.Clock.Advance(TimeSpan.FromMilliseconds(RunTimingHostedService.GoAtMilliseconds));
    Assert.True(lateCountdown.TickCountdown());
}

static void DiscardTimedOutRun()
{
    using var h = NewHarness(durationSeconds: 10);
    var run = h.ArmAndStart();
    h.Send(run, "station-01", "event-press", "timeout-discard-start", 1_000);
    h.Clock.Advance(TimeSpan.FromSeconds(11));
    var timedOut = h.Service.GetOperatorSnapshot().CurrentRun!;
    Assert.Equal(RunStatus.TimedOut, timedOut.Status);
    Assert.True(!timedOut.IsRecorded);
    var timedOutAt = timedOut.FinishedAt;

    var discarded = h.Service.Abort("Operator discarded the timed-out run.");
    Assert.Equal(run.Id, discarded.Id);
    Assert.Equal(RunStatus.Aborted, discarded.Status);
    Assert.Equal(timedOutAt, discarded.FinishedAt); // It keeps the moment it timed out.
    Assert.Equal(10_000L, discarded.ActiveElapsedMs);
    Assert.DoesNotContain(h.Service.GetScoreboard().Leaderboard, row => row.RunId == run.Id);
    Assert.Throws<CommandException>(() => h.Service.Record());
    Assert.Throws<CommandException>(() => h.Service.Abort());
    Assert.Contains(h.Service.GetOperatorSnapshot().Messages, message => message.Type == "operator-abort" && message.RunId == run.Id);

    // Once recorded, a timed-out run is a result and is changed only from history.
    h.Service.AddToQueue(h.CompetitorId, RunCategory.Exhibition);
    var second = h.ArmAndStart(RunCategory.Exhibition);
    h.Clock.Advance(TimeSpan.FromSeconds(11));
    Assert.Equal(RunStatus.TimedOut, h.Service.GetOperatorSnapshot().CurrentRun!.Status);
    h.Service.Record();
    Assert.Throws<CommandException>(() => h.Service.Abort());
    Assert.Equal(RunStatus.TimedOut, h.Service.GetOperatorSnapshot().History.Single(r => r.Id == second.Id).Status);
}

static EditionDefinition MakeBonusEdition(int durationSeconds = 60) => new()
{
    EditionId = "bonus-test-edition",
    Name = "Bonus test edition",
    DurationLimitSeconds = durationSeconds,
    Scoring = new ScoringRule(),
    BonusGame = new BonusGameSettings
    {
        PointsPerPress = 5, InitialWindowMs = 4_000, StepMs = 1_000, StepEveryMs = 3_000, MinimumWindowMs = 2_000
    },
    Events =
    [
        new EventDefinition { EventId = "first", Name = "First", DeviceId = "AABBCCDDEEFF", Type = EventKind.Standard },
        new EventDefinition { EventId = "second", Name = "Second", DeviceId = "001122334455", Type = EventKind.Standard },
        new EventDefinition { EventId = "third", Name = "Third", DeviceId = "0A0B0C0D0E0F", Type = EventKind.Standard }
    ]
};

static void FinishAllEvents(TestHarness h, RunRecord run)
{
    foreach (var eventId in new[] { "first", "second", "third" })
    {
        h.Service.PressEvent(run.Id, eventId);
        h.Clock.Advance(TimeSpan.FromMilliseconds(500));
        h.Service.PressEvent(run.Id, eventId);
    }
}

static void BonusRoundMissEndsRun()
{
    using var h = new TestHarness(MakeBonusEdition(), NewPath());
    var sounds = new List<SoundCue>();
    h.Service.SoundCueRequested += sounds.Add;
    var run = h.ArmAndStart();
    var token = MasterProtocolCodec.GetGarageRunToken(run.Id)!;
    FinishAllEvents(h, run);
    RunRecord Current() => h.Service.GetOperatorSnapshot().CurrentRun!;

    // The last event starts the bonus intro; the clock keeps running.
    Assert.Equal(RunStatus.Active, Current().Status);
    Assert.Equal(RunPhase.Bonus, Current().Phase);
    Assert.Equal(BonusGamePhase.Intro, Current().BonusGame!.Phase);
    Assert.Equal("INTRO", h.Service.GetMasterBonusStatus().Phase);
    Assert.Equal(BonusGamePhase.Intro, h.Service.GetScoreboard().CurrentRun!.BonusGame!.Phase);
    Assert.True(!h.Service.TickBonusGame());

    // Two buttons answer the poll; the third (and unknown buttons) are never lit.
    h.Service.ReceiveBonusPollReply(token, "AABBCCDDEEFF");
    h.Service.ReceiveBonusPollReply(token, "001122334455");
    h.Service.ReceiveBonusPollReply(token, "FFFFFFFFFFFF");
    h.Service.ReceiveBonusPollReply("0000000000000000", "0A0B0C0D0E0F");
    Assert.Equal(2, Current().BonusGame!.RespondingDeviceIds.Count);

    // After the intro, the chime plays and the first target lights.
    h.Clock.Advance(TimeSpan.FromMilliseconds(RunService.BonusIntroMilliseconds));
    Assert.True(h.Service.TickBonusGame());
    Assert.Equal(SoundCue.BonusStart, sounds.Single());
    var bonus = Current().BonusGame!;
    Assert.Equal(BonusGamePhase.Target, bonus.Phase);
    Assert.Equal(4_000L, bonus.TargetWindowMs);
    Assert.True(bonus.TargetEventId is "first" or "second");
    var status = h.Service.GetMasterBonusStatus();
    Assert.Equal("TARGET", status.Phase);
    Assert.Equal(bonus.TargetDeviceId, status.DeviceId);
    Assert.Equal(4_000L, status.RemainingMs);
    Assert.Equal(4_000L, h.Service.GetScoreboard().CurrentRun!.BonusGame!.TargetRemainingMs);

    // A button that is not lit does not count.
    var other = bonus.TargetEventId == "first" ? "second" : "first";
    Assert.Equal(MessageDisposition.InvalidSignal, h.Service.PressEvent(run.Id, other).Disposition);
    Assert.Equal(0, Current().BonusGame!.Hits);

    // A virtual tap on the lit button is a hit, and the next target is a different button.
    h.Clock.Advance(TimeSpan.FromMilliseconds(1_000));
    var firstTarget = bonus.TargetEventId!;
    Assert.Equal(MessageDisposition.Accepted, h.Service.PressEvent(run.Id, firstTarget).Disposition);
    bonus = Current().BonusGame!;
    Assert.Equal(1, bonus.Hits);
    Assert.True(bonus.TargetEventId != firstTarget, "the same button is never lit twice in a row");
    Assert.True(bonus.TargetEventId != "third", "only buttons that answered are lit");

    // A physical press counts from when it was pressed, and the button is told its event is
    // still complete (no red "rejected" blink) even for a press that does not count.
    h.Clock.Advance(TimeSpan.FromMilliseconds(600));
    var physical = h.Service.ReceivePhysicalSpokePress(
        new MasterPhysicalPress("boot", token, bonus.TargetDeviceId!, 1, 200), sessionAllowed: true);
    Assert.Equal(MessageDisposition.Accepted, physical.Disposition);
    Assert.Equal("COMPLETED", physical.State);
    bonus = Current().BonusGame!;
    Assert.Equal(2, bonus.Hits);
    var wrongDevice = bonus.TargetDeviceId == "AABBCCDDEEFF" ? "001122334455" : "AABBCCDDEEFF";
    var notCounted = h.Service.ReceivePhysicalSpokePress(new MasterPhysicalPress("boot", token, wrongDevice, 2), sessionAllowed: true);
    Assert.Equal(MessageDisposition.InvalidSignal, notCounted.Disposition);
    Assert.Equal("COMPLETED", notCounted.State);

    // Windows shrink every 3 s of bonus time, down to the 2 s minimum.
    h.Clock.Advance(TimeSpan.FromMilliseconds(1_500));
    h.Service.PressEvent(run.Id, bonus.TargetEventId!); // 3.1 s into the bonus.
    Assert.Equal(3_000L, Current().BonusGame!.TargetWindowMs);
    h.Clock.Advance(TimeSpan.FromMilliseconds(2_900));
    h.Service.PressEvent(run.Id, Current().BonusGame!.TargetEventId!);
    h.Clock.Advance(TimeSpan.FromMilliseconds(1_900));
    h.Service.PressEvent(run.Id, Current().BonusGame!.TargetEventId!);
    Assert.Equal(2_000L, Current().BonusGame!.TargetWindowMs);
    Assert.Equal(5, Current().BonusGame!.Hits);
    Assert.Throws<CommandException>(() => h.Service.UndoLastEventPress());

    // Pausing freezes the lit button's time.
    h.Clock.Advance(TimeSpan.FromMilliseconds(1_000));
    h.Service.Pause();
    h.Clock.Advance(TimeSpan.FromSeconds(20));
    Assert.True(!h.Service.TickBonusGame());
    Assert.Equal(RunStatus.Paused, Current().Status);
    Assert.Equal("OFF", h.Service.GetMasterBonusStatus().Phase);
    h.Service.Resume();
    Assert.Equal(1_000L, h.Service.GetMasterBonusStatus().RemainingMs);

    // A miss (after the radio grace) ends the run where the window closed, with the points.
    var deadline = Current().BonusGame!.TargetDeadlineElapsedMs!.Value;
    h.Clock.Advance(TimeSpan.FromMilliseconds(1_000 + RunService.BonusMissGraceMilliseconds));
    Assert.True(!h.Service.TickBonusGame());
    h.Clock.Advance(TimeSpan.FromMilliseconds(1));
    Assert.True(h.Service.TickBonusGame());
    var finished = Current();
    Assert.Equal(RunStatus.Finished, finished.Status);
    Assert.Equal(deadline, finished.ActiveElapsedMs);
    Assert.Equal(BonusGamePhase.Ended, finished.BonusGame!.Phase);
    Assert.Equal("miss", finished.BonusGame.EndReason);
    Assert.Equal<int?>(25, finished.BonusGame.AwardedPoints);
    Assert.Equal(finished.Events.Sum(e => e.Score) + 25, finished.TotalPoints);
    Assert.Equal("OFF", h.Service.GetMasterBonusStatus().Phase);
    Assert.Equal<int?>(25, h.Service.GetScoreboard().CurrentRun!.BonusGame!.AwardedPoints);
    Assert.Equal(finished.TotalPoints, h.Service.GetScoreboard().CurrentRun!.AwardedPoints);

    // The awarded points reach the standings once recorded, and survive a restart.
    var recorded = h.Service.Record();
    Assert.Equal(finished.TotalPoints, h.Service.GetScoreboard().Leaderboard.Single(r => r.RunId == run.Id).Points);
    var reloaded = h.Store.Load().Runs.Single(r => r.Id == run.Id);
    Assert.Equal<int?>(25, reloaded.BonusGame!.AwardedPoints);
    Assert.Equal(recorded.TotalPoints, reloaded.TotalPoints);
}

static void BonusRoundOtherEndings()
{
    // Running out of run time during the bonus is a timeout that keeps the hits.
    using (var h = new TestHarness(MakeBonusEdition(durationSeconds: 8), NewPath()))
    {
        var run = h.ArmAndStart();
        FinishAllEvents(h, run); // 1.5 s used.
        // Nothing answered the poll (no master): every event's tile can be lit.
        h.Clock.Advance(TimeSpan.FromMilliseconds(RunService.BonusIntroMilliseconds));
        h.Service.TickBonusGame();
        var seen = new HashSet<string>();
        for (var hit = 0; hit < 4; hit++)
        {
            var target = h.Service.GetOperatorSnapshot().CurrentRun!.BonusGame!.TargetEventId!;
            seen.Add(target);
            h.Clock.Advance(TimeSpan.FromMilliseconds(500));
            Assert.Equal(MessageDisposition.Accepted, h.Service.PressEvent(run.Id, target).Disposition);
        }
        Assert.True(seen.Count >= 2);
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        var timedOut = h.Service.GetOperatorSnapshot().CurrentRun!;
        Assert.Equal(RunStatus.TimedOut, timedOut.Status);
        Assert.Equal("timeout", timedOut.BonusGame!.EndReason);
        Assert.Equal<int?>(20, timedOut.BonusGame.AwardedPoints);
        Assert.True(!h.Service.TickBonusGame());
    }

    // The operator finishing during the bonus keeps the hits so far.
    using (var h = new TestHarness(MakeBonusEdition(), NewPath()))
    {
        var run = h.ArmAndStart();
        FinishAllEvents(h, run);
        h.Clock.Advance(TimeSpan.FromMilliseconds(RunService.BonusIntroMilliseconds));
        h.Service.TickBonusGame();
        h.Service.PressEvent(run.Id, h.Service.GetOperatorSnapshot().CurrentRun!.BonusGame!.TargetEventId!);
        var finished = h.Service.Finish();
        Assert.Equal("operator", finished.BonusGame!.EndReason);
        Assert.Equal<int?>(5, finished.BonusGame.AwardedPoints);
    }

    // No bonus when the last event finishes with no time left, or when it is turned off.
    var disabled = MakeBonusEdition();
    disabled.BonusGame!.Enabled = false;
    using (var h = new TestHarness(disabled, NewPath()))
    {
        var run = h.ArmAndStart();
        FinishAllEvents(h, run);
        Assert.Equal(RunStatus.Finished, h.Service.GetOperatorSnapshot().CurrentRun!.Status);
        Assert.Equal<BonusGameRecord?>(null, h.Service.GetOperatorSnapshot().CurrentRun!.BonusGame);
    }
}

static void BonusRoundCorrections()
{
    using var h = new TestHarness(MakeBonusEdition(), NewPath());
    var run = h.ArmAndStart();
    FinishAllEvents(h, run);
    RunRecord Current() => h.Service.GetOperatorSnapshot().CurrentRun!;
    EditRunRequest Edit(EventEditRequest bonusEdit) => new()
    {
        ExpectedRevision = Current().Revision,
        Reason = "Scorecard correction",
        Events = [bonusEdit]
    };
    EventEditRequest Bonus() => new() { EventId = RunService.BonusEventId };

    // Not while the round is running.
    var live = Bonus();
    live.Hits = 3;
    Assert.Throws<CommandException>(() => h.Service.EditCurrentRun(Edit(live)));

    h.Clock.Advance(TimeSpan.FromMilliseconds(RunService.BonusIntroMilliseconds));
    h.Service.TickBonusGame();
    h.Service.PressEvent(run.Id, Current().BonusGame!.TargetEventId!);
    h.Service.PressEvent(run.Id, Current().BonusGame!.TargetEventId!);
    h.Clock.Advance(TimeSpan.FromMilliseconds(4_000 + RunService.BonusMissGraceMilliseconds + 1));
    h.Service.TickBonusGame();
    var eventPoints = Current().Events.Sum(e => e.Score);
    Assert.Equal(eventPoints + 10, Current().TotalPoints);

    // Hits recalculate points; an override replaces them; clearing it restores them.
    var hits = Bonus();
    hits.Hits = 4;
    Assert.Equal(eventPoints + 20, h.Service.EditCurrentRun(Edit(hits)).TotalPoints);
    var manual = Bonus();
    manual.ScoreOverride = 50;
    Assert.Equal(eventPoints + 50, h.Service.EditCurrentRun(Edit(manual)).TotalPoints);
    Assert.Equal<int?>(50, h.Service.GetScoreboard().CurrentRun!.BonusGame!.AwardedPoints);
    var restore = Bonus();
    restore.ClearScoreOverride = true;
    Assert.Equal(eventPoints + 20, h.Service.EditCurrentRun(Edit(restore)).TotalPoints);

    // Like event times, a later end extends a finished run's timeline, but never past the
    // run limit; an end before the start is refused.
    var tooLate = Bonus();
    tooLate.FinishElapsedMs = Current().Edition.DurationLimitSeconds * 1000L + 1;
    Assert.Throws<CommandException>(() => h.Service.EditCurrentRun(Edit(tooLate)));
    var backwards = Bonus();
    backwards.FinishElapsedMs = Current().BonusGame!.StartedElapsedMs - 1;
    Assert.Throws<CommandException>(() => h.Service.EditCurrentRun(Edit(backwards)));
    var negativeHits = Bonus();
    negativeHits.Hits = -1;
    Assert.Throws<CommandException>(() => h.Service.EditCurrentRun(Edit(negativeHits)));

    // Recorded results are corrected from history and flow to the standings.
    h.Service.Record();
    var historyEdit = Bonus();
    historyEdit.Hits = 1;
    var corrected = h.Service.EditHistoricalRun(run.Id, new EditRunRequest
    {
        ExpectedRevision = h.Service.GetOperatorSnapshot().History.Single(r => r.Id == run.Id).Revision,
        Reason = "History correction",
        Events = [historyEdit]
    });
    Assert.Equal(eventPoints + 5, corrected.TotalPoints);
    Assert.Equal(eventPoints + 5, h.Service.GetScoreboard().Leaderboard.Single(r => r.RunId == run.Id).Points);

    // Clear removes the bonus result like an event's.
    var cleared = h.Service.ClearEvent(run.Id, RunService.BonusEventId, corrected.Revision);
    Assert.Equal<BonusGameRecord?>(null, cleared.BonusGame);
    Assert.Equal(eventPoints, cleared.TotalPoints);
    Assert.Throws<CommandException>(() => h.Service.ClearEvent(run.Id, RunService.BonusEventId, cleared.Revision));

    // A run that never reached the bonus can have one entered from history.
    var entered = Bonus();
    entered.StartElapsedMs = 1_000;
    entered.FinishElapsedMs = 2_000;
    entered.Hits = 2;
    var withBonus = h.Service.EditHistoricalRun(run.Id, new EditRunRequest
    {
        ExpectedRevision = cleared.Revision,
        Reason = "Bonus entered by hand",
        Events = [entered]
    });
    Assert.Equal(BonusGamePhase.Ended, withBonus.BonusGame!.Phase);
    Assert.Equal(eventPoints + 10, withBonus.TotalPoints);
    Assert.Equal(RunStatus.Completed, withBonus.Status);
}

static void ScoreboardEventDurations()
{
    using var h = NewHarness();
    var run = h.ArmAndStart();
    h.Clock.Advance(TimeSpan.FromSeconds(2));
    h.Service.PressEvent(run.Id, "event-01");
    h.Clock.Advance(TimeSpan.FromMilliseconds(34_600));
    h.Service.PressEvent(run.Id, "event-01");
    h.Service.PressEvent(run.Id, "event-04");
    var events = h.Service.GetScoreboard().CurrentRun!.Events;
    Assert.Equal<long?>(34_600L, events.Single(e => e.Name == "Event 1").DurationMs);
    Assert.Equal<long?>(null, events.Single(e => e.Name == "Event 4").DurationMs); // Still running.
    Assert.Equal<long?>(null, events.Single(e => e.Name == "Keypad").DurationMs); // Not started.
}

static void UpNextOnTheTv()
{
    using var h = NewHarness();
    var first = h.ArmAndStart();
    h.Send(first, "station-01", "event-press", "up-next-start", 0);
    // Not while a run is in progress (or awaiting recording).
    var next = h.AddCompetitor("Next Up");
    Assert.Throws<CommandException>(() => h.Service.PrimeNextCompetitor(next.Id, RunCategory.Official, 240));
    h.Service.Finish();
    h.Service.Record();
    Assert.Equal(h.CompetitorName, h.Service.GetScoreboard().CurrentRun!.CompetitorName);

    h.Service.AddToQueue(next.Id, RunCategory.Official);
    var third = h.AddCompetitor("Third");
    h.Service.AddToQueue(third.Id, RunCategory.Official);
    Assert.Throws<CommandException>(() => h.Service.PrimeNextCompetitor(next.Id, RunCategory.Official, null));
    var primed = h.Service.PrimeNextCompetitor(next.Id, RunCategory.Official, 240);
    Assert.Equal(240, primed.DurationLimitSeconds);
    Assert.Equal(primed, h.Service.GetOperatorSnapshot().Primed);

    // The TV shows them with the full clock, every event pending, and no points; the
    // on-deck list moves past them.
    var board = h.Service.GetScoreboard();
    Assert.Equal("Next Up", board.CurrentRun!.CompetitorName);
    Assert.True(board.CurrentRun.IsPrimed);
    Assert.Equal(240_000L, board.CurrentRun.RemainingMilliseconds);
    Assert.Equal(0, board.CurrentRun.AwardedPoints);
    Assert.Equal(0, board.CurrentRun.CompletedEvents);
    Assert.True(board.CurrentRun.Events.All(e => e.Status == EventStatus.Pending && e.AwardedPoints == 0));
    Assert.Equal(240, board.DurationLimitSeconds);
    Assert.Equal("Third", board.OnDeckName);

    // Arming a run replaces it; after that run, the TV shows that run, not the old prime.
    h.Service.Arm(h.Service.GetOperatorSnapshot().Queue.First(item => item.CompetitorId == next.Id).Id);
    Assert.True(!h.Service.GetScoreboard().CurrentRun!.IsPrimed);
    Assert.Equal<PrimedCompetitor?>(null, h.Service.GetOperatorSnapshot().Primed);
    h.StartRun();
    h.Service.Finish();
    h.Service.Record();
    Assert.True(!h.Service.GetScoreboard().CurrentRun!.IsPrimed);
    Assert.Equal("Next Up", h.Service.GetScoreboard().CurrentRun!.CompetitorName);
}

static void BonusRoundSetup()
{
    using var h = new TestHarness(MakeBonusEdition(), NewPath());
    var setup = h.Service.GetSetup();
    Assert.Equal(5, setup.BonusGame!.PointsPerPress);
    setup.BonusGame.PointsPerPress = 12;
    setup.BonusGame.InitialWindowMs = 6_000;
    var saved = h.Service.UpdateSetup(setup);
    Assert.Equal(12, saved.BonusGame!.PointsPerPress);
    Assert.Equal(setup.EditionId, saved.EditionId); // No recorded runs yet: same edition.

    var run = h.ArmAndStart();
    Assert.Equal(12, h.Service.GetOperatorSnapshot().CurrentRun!.Edition.BonusGame!.PointsPerPress);
    h.Service.Finish();
    h.Service.Record();

    var invalid = h.Service.GetSetup();
    invalid.BonusGame!.MinimumWindowMs = 7_000; // Longer than the starting window.
    Assert.Throws<CommandException>(() => h.Service.UpdateSetup(invalid));

    // Renaming the round is not a scoring change: same edition, new name for new runs.
    var renamed = h.Service.GetSetup();
    Assert.Equal(BonusGameSettings.DefaultName, renamed.BonusGame!.Name);
    renamed.BonusGame.Name = "  Lightning Round ";
    var renamedSaved = h.Service.UpdateSetup(renamed);
    Assert.Equal(setup.EditionId, renamedSaved.EditionId);
    Assert.Equal("Lightning Round", renamedSaved.BonusGame!.Name);
    Assert.Equal("Lightning Round", h.Service.GetOperatorSnapshot().BonusGame!.Name);
    var tooLong = h.Service.GetSetup();
    tooLong.BonusGame!.Name = new string('x', BonusGameSettings.MaximumNameLength + 1);
    Assert.Throws<CommandException>(() => h.Service.UpdateSetup(tooLong));

    var changed = h.Service.GetSetup();
    changed.BonusGame!.PointsPerPress = 3;
    var versioned = h.Service.UpdateSetup(changed);
    Assert.True(versioned.EditionId != setup.EditionId, "changing bonus scoring after recorded runs versions the edition");
    Assert.Equal(12, h.Service.GetOperatorSnapshot().History.Single(r => r.Id == run.Id).Edition.BonusGame!.PointsPerPress);

    // New runs carry the name to the TV.
    h.Service.AddToQueue(h.CompetitorId, RunCategory.Exhibition);
    var named = h.ArmAndStart(RunCategory.Exhibition);
    FinishAllEvents(h, named);
    Assert.Equal("Lightning Round", h.Service.GetScoreboard().CurrentRun!.BonusGame!.Name);
}

static void DeletedRunsHideAndRestore()
{
    var path = NewPath();
    var edition = MakeMvpEdition(durationSeconds: 2);
    var clock = new TestClock();
    var store = new RunStore(path);
    var service = new RunService(store, edition, clock);
    var alex = service.AddCompetitor("Alex");
    var sam = service.AddCompetitor("Sam");

    var official = service.ArmCompetitor(alex.Id, RunCategory.Official, 60);
    service.CompleteCountdown(service.StartMaster().Id);
    service.Finish();
    official = service.Record();
    var kept = service.ArmCompetitor(sam.Id, RunCategory.Official, 60);
    service.CompleteCountdown(service.StartMaster().Id);
    service.Finish();
    service.Record();
    Assert.Equal(2, service.GetScoreboard().Leaderboard.Count);

    // The live run and stale revisions are refused.
    var live = service.ArmCompetitor(sam.Id, RunCategory.Exhibition, 2);
    Assert.Throws<CommandException>(() => service.DeleteRun(live.Id, live.Revision));
    Assert.Throws<CommandException>(() => service.DeleteRun(official.Id, official.Revision - 1));

    var deleted = service.DeleteRun(official.Id, official.Revision, "Test entry");
    Assert.True(deleted.IsDeleted);
    var snapshot = service.GetOperatorSnapshot();
    Assert.DoesNotContain(snapshot.History, r => r.Id == official.Id);
    Assert.Equal(official.Id, snapshot.DeletedRuns.Single().Id);
    Assert.Equal(kept.Id, service.GetScoreboard().Leaderboard.Single().RunId);
    Assert.Contains(snapshot.Edits, e => e.RunId == official.Id && e.Reason.Contains("Test entry"));
    Assert.Throws<CommandException>(() => service.EditHistoricalRun(official.Id,
        new EditRunRequest { ExpectedRevision = deleted.Revision, Reason = "Edit a deleted run" }));

    // A deleted official no longer blocks a new official for the same competitor.
    service.Abort("Make room");
    var replacementOfficial = service.ArmCompetitor(alex.Id, RunCategory.Official, 60);
    service.Abort("Not needed");

    // Deleting the displayed timed-out run clears it from the TV.
    var timedOut = service.ArmCompetitor(sam.Id, RunCategory.Exhibition, 2);
    service.CompleteCountdown(service.StartMaster().Id);
    clock.Advance(TimeSpan.FromSeconds(3));
    Assert.Equal(RunStatus.TimedOut, service.GetScoreboard().CurrentRun!.Status);
    service.DeleteRun(timedOut.Id, service.GetOperatorSnapshot().History.Single(r => r.Id == timedOut.Id).Revision);
    Assert.Equal(null, service.GetScoreboard().CurrentRun);
    store.Dispose();

    // Deletion persists across a restart, and restoring brings the run back.
    var reopenedStore = new RunStore(path);
    var reopened = new RunService(reopenedStore, edition, new TestClock());
    Assert.Equal(2, reopened.GetOperatorSnapshot().DeletedRuns.Count);
    Assert.Equal(kept.Id, reopened.GetScoreboard().Leaderboard.Single().RunId);
    reopened.RestoreRun(official.Id);
    Assert.Contains(reopened.GetOperatorSnapshot().History, r => r.Id == official.Id);
    Assert.Equal(2, reopened.GetScoreboard().Leaderboard.Count);

    // Restoring can't create a second counted official for one competitor.
    var twin = reopened.ArmCompetitor(sam.Id, RunCategory.Exhibition, 60);
    reopened.Abort("Cleanup");
    reopened.DeleteRun(official.Id, reopened.GetOperatorSnapshot().History.Single(r => r.Id == official.Id).Revision);
    var second = reopened.ArmCompetitor(alex.Id, RunCategory.Official, 60);
    reopened.CompleteCountdown(reopened.StartMaster().Id);
    reopened.Finish();
    reopened.Record();
    Assert.Throws<CommandException>(() => reopened.RestoreRun(official.Id));
    Assert.Equal(second.Id, reopened.GetScoreboard().Leaderboard.Single(r => r.CompetitorName == "Alex").RunId);
    reopenedStore.Dispose();
    Cleanup(path);
}

static void DeletingRedoRestoresOriginal()
{
    using var h = new TestHarness(MakeMvpEdition(), NewPath());
    foreach (var item in h.Service.GetOperatorSnapshot().Queue) h.Service.RemoveFromQueue(item.Id);
    var original = h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Official, 300);
    h.StartRun();
    h.Service.Finish();
    h.Service.Record();
    var redo = h.Service.ArmCompetitor(h.CompetitorId, RunCategory.Official, 300, replaceExistingOfficial: true);
    h.StartRun();
    h.Service.Finish();
    redo = h.Service.Record();
    Assert.Equal(RunStatus.Superseded, h.Service.GetOperatorSnapshot().History.Single(r => r.Id == original.Id).Status);

    h.Service.DeleteRun(redo.Id, redo.Revision);
    var restoredOriginal = h.Service.GetOperatorSnapshot().History.Single(r => r.Id == original.Id);
    Assert.Equal(RunStatus.Completed, restoredOriginal.Status);
    Assert.Equal(null, restoredOriginal.SupersededByRunId);
    Assert.Equal(original.Id, h.Service.GetScoreboard().Leaderboard.Single().RunId);
    Assert.Equal(RunStatus.Completed, h.Store.Load().Runs.Single(r => r.Id == original.Id).Status);

    // Restoring the redo makes it replace the original again.
    h.Service.RestoreRun(redo.Id);
    Assert.Equal(RunStatus.Superseded, h.Service.GetOperatorSnapshot().History.Single(r => r.Id == original.Id).Status);
    Assert.Equal(redo.Id, h.Service.GetScoreboard().Leaderboard.Single().RunId);
}

static void SchemaVersion1UpgradesWithBackup()
{
    var path = NewPath();
    var edition = MakeMvpEdition();
    var store = new RunStore(path);
    var service = new RunService(store, edition, new TestClock());
    var competitor = service.AddCompetitor("Upgrade competitor");
    var run = service.ArmCompetitor(competitor.Id, RunCategory.Official, 60);
    service.CompleteCountdown(service.StartMaster().Id);
    service.Finish();
    service.Record();
    var databasePath = store.DatabasePath;
    store.Dispose();

    using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE runs DROP COLUMN recorded_at; ALTER TABLE runs DROP COLUMN deleted_at; " +
            "ALTER TABLE runs DROP COLUMN superseded_from_status; ALTER TABLE run_events DROP COLUMN keypad_json; " +
            "ALTER TABLE runs DROP COLUMN bonus_game_json; UPDATE meta SET value = '1' WHERE key = 'schema_version';";
        command.ExecuteNonQuery();
    }

    var upgradedStore = new RunStore(path);
    Assert.Equal(1, Directory.GetFiles(Path.Combine(path, "backups"), "garage-games-v2-pre-schema-5-*.db").Length);
    var upgradedRun = upgradedStore.Load().Runs.Single(r => r.Id == run.Id);
    Assert.True(upgradedRun.RecordedAt is not null);
    Assert.True(!upgradedRun.IsDeleted);
    upgradedStore.Dispose();

    using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = 'schema_version'";
        Assert.Equal("5", command.ExecuteScalar() as string);
    }

    var reopenedStore = new RunStore(path);
    Assert.Equal(1, Directory.GetFiles(Path.Combine(path, "backups"), "garage-games-v2-pre-schema-5-*.db").Length);
    reopenedStore.Dispose();

    // A schema 3 database gains the keypad and bonus round columns.
    using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE run_events DROP COLUMN keypad_json; ALTER TABLE runs DROP COLUMN bonus_game_json; UPDATE meta SET value = '3' WHERE key = 'schema_version';";
        command.ExecuteNonQuery();
    }
    var fromThree = new RunStore(path);
    Assert.Equal(run.Id, fromThree.Load().Runs.Single(r => r.Id == run.Id).Id);
    fromThree.Dispose();
    Cleanup(path);
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

static void LeaderboardPreferenceAndCategories()
{
    using var h = new TestHarness(MakeMvpEdition(), NewPath());

    h.ArmAndStart(RunCategory.Playoff);
    h.Service.Finish();
    h.Service.Record();

    var officialCompetitor = h.AddCompetitor("Official player");
    h.Service.AddToQueue(officialCompetitor.Id, RunCategory.Official);
    h.Service.ArmCompetitor(officialCompetitor.Id, RunCategory.Official);
    h.StartRun();
    h.Service.Finish();
    h.Service.Record();

    h.ArmAndStart(RunCategory.Exhibition);
    h.Service.Finish();
    h.Service.Record();
    h.ArmAndStart(RunCategory.Exhibition);
    h.Service.Finish();
    h.Service.Record();

    var hidden = h.Service.GetOperatorSnapshot();
    Assert.Equal(false, hidden.ShowExhibitionsOnLeaderboard);
    Assert.Equal(2, hidden.Leaderboard.Count);
    Assert.Equal(RunCategory.Playoff, hidden.Leaderboard[0].Category);
    Assert.Equal(RunCategory.Official, hidden.Leaderboard[1].Category);
    Assert.Equal(1, hidden.Leaderboard[0].Rank);
    Assert.Equal(1, hidden.Leaderboard[1].Rank); // Ranks restart within each category.

    foreach (var item in h.Service.GetOperatorSnapshot().Queue) h.Service.RemoveFromQueue(item.Id);
    var queuedNames = new[] { "Queue one", "Queue two", "Queue three", "Queue four", "Queue five" };
    foreach (var queuedName in queuedNames)
    {
        h.Service.AddToQueue(h.AddCompetitor(queuedName).Id, queuedName == "Queue two" ? RunCategory.Exhibition : RunCategory.Official);
    }
    var onDeck = h.Service.GetScoreboard();
    Assert.Equal("Queue one", onDeck.OnDeckName);
    Assert.True(onDeck.OnDeck.Select(entry => entry.Name).SequenceEqual(queuedNames.Take(4)));
    Assert.Equal(RunCategory.Exhibition, onDeck.OnDeck[1].Category);

    h.Service.SetLeaderboardPreference(true);
    var included = h.Service.GetOperatorSnapshot();
    Assert.Equal(true, included.ShowExhibitionsOnLeaderboard);
    Assert.True(included.Leaderboard.Select(row => row.Category).SequenceEqual(
        [RunCategory.Playoff, RunCategory.Official, RunCategory.Exhibition, RunCategory.Exhibition]));
    Assert.True(included.Leaderboard.Skip(2).Select(row => row.DisplayName).ToHashSet(StringComparer.Ordinal)
        .SetEquals(["Primary competitor (Exhibition 1)", "Primary competitor (Exhibition 2)"]));
    Assert.Equal(true, h.Store.Load().ShowExhibitionsOnLeaderboard);

    h.Service.SetLeaderboardPreference(false);
    Assert.Equal(2, h.Service.GetOperatorSnapshot().Leaderboard.Count);
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

static void LegacyDataMigrationSafety()
{
    var root = NewPath();
    var legacy = Path.Combine(root, "legacy");
    var current = Path.Combine(root, "current");
    var legacyDatabase = Path.Combine(legacy, "garage-games-v2.db");
    var currentDatabase = Path.Combine(current, "garage-games-v2.db");
    try
    {
        string backupPath;
        using (var store = new RunStore(legacy))
        {
            _ = new RunService(store, MakeMvpEdition(), new TestClock()).AddCompetitor("Preserved competitor");
            backupPath = store.CreateBackup();
        }

        Assert.True(DataDirectoryMigration.CopyLegacyDataIfNeeded(current, legacy));
        Assert.True(File.Exists(legacyDatabase), "Migration must retain the original database.");
        Assert.Equal(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(legacyDatabase))),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(currentDatabase))));
        Assert.True(File.Exists(Path.Combine(current, "backups", Path.GetFileName(backupPath))),
            "Migration must copy existing database backups too.");
        var currentHashBeforeConflict = File.ReadAllBytes(currentDatabase);

        using (var copiedStore = new RunStore(current))
        {
            Assert.Equal("Preserved competitor", copiedStore.Load().Competitors.Single().Name);
        }

        using (var legacyStore = new RunStore(legacy))
        {
            _ = new RunService(legacyStore, MakeMvpEdition(), new TestClock()).AddCompetitor("Legacy-only competitor");
        }

        SqliteConnection.ClearAllPools();
        var legacyHashBeforeConflict = File.ReadAllBytes(legacyDatabase);
        Assert.True(!DataDirectoryMigration.CopyLegacyDataIfNeeded(current, legacy));
        Assert.True(File.Exists(legacyDatabase), "Conflict handling must retain the legacy database.");
        Assert.Equal(Convert.ToHexString(currentHashBeforeConflict),
            Convert.ToHexString(File.ReadAllBytes(currentDatabase)));
        Assert.Equal(Convert.ToHexString(legacyHashBeforeConflict),
            Convert.ToHexString(File.ReadAllBytes(legacyDatabase)));

        using (var currentStore = new RunStore(current))
        {
            Assert.Equal("Preserved competitor", currentStore.Load().Competitors.Single().Name);
        }
        using (var legacyStore = new RunStore(legacy))
        {
            Assert.Contains(legacyStore.Load().Competitors, competitor => competitor.Name == "Legacy-only competitor");
        }
    }
    finally
    {
        Cleanup(root);
    }
}

static void BuildIdentityFingerprint()
{
    var root = NewPath();
    var application = Path.Combine(root, "application");
    var edition = Path.Combine(root, "edition");
    try
    {
        Directory.CreateDirectory(application);
        Directory.CreateDirectory(edition);
        File.WriteAllText(Path.Combine(application, "Program.cs"), "source-v1");
        File.WriteAllText(Path.Combine(edition, "edition.json"), "edition-v1");
        var roots = new[] { ("application", application), ("edition", edition) };
        var first = BuildIdentity.Compute(roots);
        Assert.Equal(first, BuildIdentity.Compute(roots));
        Assert.Equal(64, first.Length);

        Directory.CreateDirectory(Path.Combine(application, "bin"));
        File.WriteAllText(Path.Combine(application, "bin", "ignored.dll"), "generated-v1");
        Assert.Equal(first, BuildIdentity.Compute(roots));

        File.WriteAllText(Path.Combine(application, "Program.cs"), "source-v2");
        Assert.True(first != BuildIdentity.Compute(roots), "Changing app source must change the build identity.");
    }
    finally
    {
        Cleanup(root);
    }
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
    BonusGame = new BonusGameSettings { Enabled = false },
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
    public TestHarness(EditionDefinition edition, string path, bool simulatedDevicesOnline = true,
        KeypadChallengeSet? keypadChallenges = null)
    {
        Path = path;
        Clock = new TestClock();
        Store = new RunStore(path);
        Service = new RunService(Store, edition, Clock, keypadChallenges);
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

sealed class RecordingSoundPlayer : ISoundPlayer
{
    public List<SoundCue> Played { get; } = [];
    public void Play(SoundCue cue) => Played.Add(cue);
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
