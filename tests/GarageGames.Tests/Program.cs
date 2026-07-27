using GarageGames.Core.Domain;
using GarageGames.Core.Scoring;
using GarageGames.Core.State;

var tests = new (string Name, Action Run)[]
{
    ("2025 full run matches known 839.9 total", BaselineFullRun),
    ("2026 decay boundaries and minimum", TimeDecayBoundaries),
    ("Duplicate first attempts project once", DuplicateAttempt),
    ("Voided event disappears from projection", VoidedCompletion),
    ("Incomplete run remains active", IncompleteRun),
    ("Displayed timeout is persisted by the run clock", DisplayedTimeoutIsPersisted)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
return failures == 0 ? 0 : 1;

static void BaselineFullRun()
{
    var season = BaselineSeason();
    var events = new List<DeviceEvent>();
    long sequence = 0;
    foreach (var game in season.Games)
    {
        events.Add(Event(++sequence, EventTypes.AttemptStarted, game.Id, 250, sequence * 1000));
        events.Add(Event(++sequence, EventTypes.GameCompleted, game.Id, 100, 200_000));
        events.Add(Event(++sequence, EventTypes.BonusHit, game.Id, 100, 200_000));
    }
    var snapshot = Project(season, events);
    var result = new Baseline2025ScoringPolicy().Score(snapshot, season);
    Equal(13, snapshot.CompletionCount);
    Equal(100, snapshot.RemainingSeconds);
    Equal(839.9m, result.Total);
}

static void TimeDecayBoundaries()
{
    var games = new[]
    {
        new GameDefinition("instant", "Instant", "instant", 1, 100),
        new GameDefinition("four", "Four seconds", "four", 2, 100),
        new GameDefinition("five", "Five seconds", "five", 3, 100),
        new GameDefinition("slow", "Slow", "slow", 4, 100)
    };
    var season = new SeasonDefinition(
        "2026", "Test", 300, 4, "2026-time-decay", 0, 99, 0, 99, games,
        new Dictionary<string, decimal>
        {
            ["basePoints"] = 100,
            ["minimumPoints"] = 50,
            ["decayIntervalSeconds"] = 5,
            ["decayPointsPerInterval"] = 5
        });
    var events = new List<DeviceEvent>();
    long sequence = 0;
    foreach (var item in new[] { ("instant", 0), ("four", 4), ("five", 5), ("slow", 99) })
    {
        events.Add(Event(++sequence, EventTypes.AttemptStarted, item.Item1, 250, sequence * 1000));
        events.Add(Event(++sequence, EventTypes.GameCompleted, item.Item1, 250 - item.Item2, sequence * 1000));
    }
    var result = new TimeDecay2026ScoringPolicy().Score(Project(season, events), season);
    Equal(100m, result.Games[0].Points);
    Equal(100m, result.Games[1].Points);
    Equal(95m, result.Games[2].Points);
    Equal(50m, result.Games[3].Points);
}

static void DuplicateAttempt()
{
    var season = BaselineSeason();
    var events = new[]
    {
        Event(1, EventTypes.AttemptStarted, season.Games[0].Id, 290, 10_000),
        Event(2, EventTypes.AttemptStarted, season.Games[0].Id, 250, 50_000)
    };
    var snapshot = Project(season, events);
    Equal(1, snapshot.AttemptCount);
    Equal(290, snapshot.Games[0].AttemptRemainingSeconds);
}

static void VoidedCompletion()
{
    var season = BaselineSeason();
    var completion = Event(1, EventTypes.GameCompleted, season.Games[0].Id, 200, 100_000);
    var voidEvent = new DeviceEvent(
        "void-1", "run", "controller", "boot", 2, EventTypes.EventVoided, 101_000,
        DateTimeOffset.UnixEpoch.AddMilliseconds(101_000),
        JsonDefaults.Serialize(new VoidEventPayload(completion.EventId, "test")), "undo");
    var snapshot = Project(season, [completion, voidEvent]);
    Equal(0, snapshot.CompletionCount);
}

static void IncompleteRun()
{
    var season = BaselineSeason();
    var snapshot = Project(season, [Event(1, EventTypes.AttemptStarted, season.Games[0].Id, 290, 10_000)]);
    Equal(RunStatus.Active, snapshot.Status);
}

static void DisplayedTimeoutIsPersisted()
{
    var season = BaselineSeason();
    var snapshot = Project(
        season,
        [Event(1, EventTypes.AttemptStarted, season.Games[0].Id, 0, 300_000)]);

    Equal(RunStatus.TimedOut, snapshot.Status);
    Equal(true, RunLifecycle.RequiresPersistedTimeout(snapshot));
    Equal(
        false,
        RunLifecycle.RequiresPersistedTimeout(snapshot with
        {
            Status = RunStatus.Paused,
            RemainingSeconds = 1
        }));
}

static RunSnapshot Project(SeasonDefinition season, IReadOnlyList<DeviceEvent> events) =>
    new RunProjector().Project(
        "run", "competitor", "Test Competitor", DateTimeOffset.UnixEpoch, season, events);

static DeviceEvent Event(long sequence, string type, string gameId, int remaining, long elapsed) =>
    new(
        $"event-{sequence}", "run", $"station-{sequence % 20}", "boot", sequence, type, elapsed,
        DateTimeOffset.UnixEpoch.AddMilliseconds(elapsed),
        JsonDefaults.Serialize(new GameEventPayload(gameId, remaining)),
        "test");

static SeasonDefinition BaselineSeason()
{
    var weights = new[] { 37.9m, 37.9m, 14.6m, 28.6m, 56.4m, 47.1m, 47.1m, 65.7m, 75m, 42.5m, 65.7m, 56.4m, 10m };
    var games = weights.Select((weight, index) =>
        new GameDefinition($"game-{index + 1}", $"Game {index + 1}", $"g{index + 1}", index + 1, weight)).ToArray();
    return new("2025", "Baseline", 300, 13, "2025-baseline", 10, 13, 25, 13, games);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}
