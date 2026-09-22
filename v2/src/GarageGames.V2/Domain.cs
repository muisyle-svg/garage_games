using System.Text.Json;
using System.Text.Json.Serialization;

namespace GarageGames.V2;

public enum RunCategory
{
    Official,
    Playoff,
    Exhibition
}

public enum RunStatus
{
    Armed,
    Active,
    Paused,
    Finished,
    Completed,
    TimedOut,
    Aborted,
    Superseded
}

public enum RunPhase
{
    Normal,
    Bonus
}

public enum EventKind
{
    Standard,
    Keypad,
    MagneticArcade
}

public enum EventStatus
{
    Pending,
    Active,
    Completed
}

public enum DeviceAvailability
{
    Online,
    Offline,
    Error
}

public enum LedState
{
    Ready,
    EventAvailable,
    EventActive,
    EventCompleted,
    Bonus,
    Paused,
    OfflineError,
    RunFinished
}

public enum MessageDisposition
{
    Accepted,
    Duplicate,
    WrongRun,
    UnknownStation,
    UnknownMessageType,
    Paused,
    TimedOut,
    StaleTimestamp,
    Offline,
    AlreadyCompleted,
    InvalidSignal,
    BonusNotReady,
    InvalidEnvelope
}

public sealed class ScoringRule
{
    public bool ManualEventPoints { get; set; }
    public int BasePoints { get; set; } = 100;
    public int DecayPoints { get; set; } = 5;
    public int DecayEverySeconds { get; set; } = 5;
    public int MinimumPoints { get; set; } = 50;

    public ScoringRule Clone() => new()
    {
        ManualEventPoints = ManualEventPoints,
        BasePoints = BasePoints,
        DecayPoints = DecayPoints,
        DecayEverySeconds = DecayEverySeconds,
        MinimumPoints = MinimumPoints
    };
}

public sealed class EventDefinition
{
    public required string EventId { get; set; }
    public required string Name { get; set; }
    public required string DeviceId { get; set; }
    public EventKind Type { get; set; }
    public string? Prompt { get; set; }
    public string? Answer { get; set; }
}

public sealed class EditionDefinition
{
    public required string EditionId { get; set; }
    public required string Name { get; set; }
    public int DurationLimitSeconds { get; set; } = 300;
    public ScoringRule Scoring { get; set; } = new();
    public List<EventDefinition> Events { get; set; } = [];

    public EditionSnapshot ToSnapshot() => new()
    {
        EditionId = EditionId,
        Name = Name,
        DurationLimitSeconds = DurationLimitSeconds,
        Scoring = Scoring.Clone(),
        Events = Events.Select(e => new EventSnapshot
        {
            EventId = e.EventId,
            Name = e.Name,
            DeviceId = e.DeviceId,
            Type = e.Type,
            Prompt = e.Prompt,
            Answer = e.Answer
        }).ToList()
    };

    public static EditionDefinition FromJson(string path)
    {
        using var stream = File.OpenRead(path);
        var edition = JsonSerializer.Deserialize<EditionDefinition>(stream, JsonDefaults.Options)
            ?? throw new InvalidDataException($"Edition configuration '{path}' is empty.");
        Validate(edition, path);
        return edition;
    }

    public static void Validate(EditionDefinition edition, string source = "edition")
    {
        if (string.IsNullOrWhiteSpace(edition.EditionId) || string.IsNullOrWhiteSpace(edition.Name))
        {
            throw new InvalidDataException($"Edition '{source}' must have an id and name.");
        }

        if (edition.DurationLimitSeconds <= 0 || edition.Scoring.DecayEverySeconds <= 0 ||
            edition.Scoring.BasePoints < 0 || edition.Scoring.DecayPoints < 0 || edition.Scoring.MinimumPoints < 0)
        {
            throw new InvalidDataException($"Edition '{source}' contains an invalid duration or scoring rule.");
        }

        if (edition.Events.Count == 0 || edition.Events.GroupBy(e => e.EventId).Any(g => g.Count() > 1) ||
            edition.Events.GroupBy(e => e.DeviceId).Any(g => g.Count() > 1))
        {
            throw new InvalidDataException($"Edition '{source}' must have unique event and device identifiers.");
        }

        foreach (var eventDefinition in edition.Events)
        {
            if (string.IsNullOrWhiteSpace(eventDefinition.EventId) || string.IsNullOrWhiteSpace(eventDefinition.Name) ||
                string.IsNullOrWhiteSpace(eventDefinition.DeviceId))
            {
                throw new InvalidDataException($"Edition '{source}' contains an event with a missing identity.");
            }

            if (eventDefinition.Type == EventKind.Keypad && string.IsNullOrWhiteSpace(eventDefinition.Answer))
            {
                throw new InvalidDataException($"Keypad event '{eventDefinition.EventId}' requires an answer.");
            }
        }
    }
}

public sealed class EditionSnapshot
{
    public required string EditionId { get; set; }
    public required string Name { get; set; }
    public int DurationLimitSeconds { get; set; }
    public required ScoringRule Scoring { get; set; }
    public required List<EventSnapshot> Events { get; set; }
}

public sealed class EventSnapshot
{
    public required string EventId { get; set; }
    public required string Name { get; set; }
    public required string DeviceId { get; set; }
    public EventKind Type { get; set; }
    public string? Prompt { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Answer { get; set; }
}

public sealed class CompetitorRecord
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string EditionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class QueueItemRecord
{
    public required string Id { get; set; }
    public required string CompetitorId { get; set; }
    public RunCategory Category { get; set; }
    public bool ReplaceExistingOfficial { get; set; }
    public string? ReplacementOfRunId { get; set; }
    public string? Reason { get; set; }
    public int Position { get; set; }
}

public sealed class DeviceRecord
{
    public required string DeviceId { get; set; }
    public required string EventId { get; set; }
    public DeviceAvailability Availability { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public LedState Led { get; set; } = LedState.Ready;
    public string? LastError { get; set; }
}

public sealed class EventRecord
{
    public required string EventId { get; set; }
    public required string Name { get; set; }
    public required string DeviceId { get; set; }
    public EventKind Type { get; set; }
    public string? Prompt { get; set; }
    public EventStatus Status { get; set; }
    public long? StartElapsedMs { get; set; }
    public long? FinishElapsedMs { get; set; }
    public int Score { get; set; }
    public int? ScoreOverride { get; set; }
    public string? MeasurementJson { get; set; }
    public string? Notes { get; set; }
    public long? LastSignalElapsedMs { get; set; }

    [JsonIgnore]
    public long? DurationMs => StartElapsedMs is long start && FinishElapsedMs is long finish ? finish - start : null;
    [JsonIgnore]
    public bool HasCompleteTiming => StartElapsedMs is not null && FinishElapsedMs is not null;
}

public sealed class RunRecord
{
    public required string Id { get; set; }
    public required string CompetitorId { get; set; }
    public required string EditionId { get; set; }
    public required EditionSnapshot Edition { get; set; }
    public RunCategory Category { get; set; }
    public RunStatus Status { get; set; }
    public RunPhase Phase { get; set; } = RunPhase.Normal;
    public bool ManualOfflineOverride { get; set; }
    public long ActiveElapsedMs { get; set; }
    public long LastAcceptedInputElapsedMs { get; set; }
    public long? BonusStartedElapsedMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? RecordedAt { get; set; }
    public string? SupersedesRunId { get; set; }
    public string? SupersededByRunId { get; set; }
    public string? PausedFromPhase { get; set; }
    public string? Notes { get; set; }
    public int Revision { get; set; } = 1;
    public List<EventRecord> Events { get; set; } = [];

    [JsonIgnore]
    public string? BonusResultJson { get; set; }
    public int? BonusPointsOverride { get; set; }

    [JsonIgnore]
    public int BonusPoints => BonusPointsOverride ?? 0;
    [JsonIgnore]
    public int TotalPoints => Events.Sum(e => e.Score) + BonusPoints;
    public bool IsRecorded => RecordedAt is not null || Status == RunStatus.Completed;
    [JsonIgnore]
    public int CompletedEventCount => Events.Count(e => e.Status == EventStatus.Completed);
    [JsonIgnore]
    public bool AllEventsCompleted => Events.Count > 0 && Events.All(e => e.Status == EventStatus.Completed);
    [JsonIgnore]
    public bool IsCountedOfficial => IsRecorded && Category == RunCategory.Official && SupersededByRunId is null &&
        Status is not RunStatus.Aborted and not RunStatus.Superseded;
}

public sealed class MessageRecord
{
    public long Id { get; set; }
    public required string MessageId { get; set; }
    public string? RunId { get; set; }
    public string? SessionId { get; set; }
    public required string DeviceId { get; set; }
    public required string Type { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public string? PayloadJson { get; set; }
    public MessageDisposition Disposition { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

public sealed class EditRecord
{
    public long Id { get; set; }
    public required string RunId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string Reason { get; set; }
    public required string BeforeJson { get; set; }
    public required string AfterJson { get; set; }
    public long? UndoneEditId { get; set; }
}

public sealed class InputEnvelope
{
    public required string MessageId { get; set; }
    public required string SessionId { get; set; }
    public required string RunId { get; set; }
    public required string DeviceId { get; set; }
    public required string Type { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public JsonElement Payload { get; set; }
}

public sealed record InputResult(MessageDisposition Disposition, string Reason, RunRecord? Run);

public sealed class OperatorSnapshot
{
    public required string EditionId { get; set; }
    public required string EditionName { get; set; }
    public int DurationLimitSeconds { get; set; }
    public bool SimulationMode { get; set; }
    public RunRecord? CurrentRun { get; set; }
    public required List<EventSnapshot> Events { get; set; }
    public required List<CompetitorRecord> Competitors { get; set; }
    public required List<QueueItemRecord> Queue { get; set; }
    public required List<DeviceRecord> Devices { get; set; }
    public required List<RunRecord> History { get; set; }
    public required List<MessageRecord> Messages { get; set; }
    public required List<EditRecord> Edits { get; set; }
    public required List<LeaderboardRow> Leaderboard { get; set; }
}

public sealed class ScoreboardSnapshot
{
    public required string EditionName { get; set; }
    public int DurationLimitSeconds { get; set; }
    public bool SimulationMode { get; set; }
    public ScoreboardRun? CurrentRun { get; set; }
    public string? OnDeckName { get; set; }
    public required List<LeaderboardRow> Leaderboard { get; set; }
}

public sealed class ScoreboardRun
{
    public required string CompetitorName { get; set; }
    public RunCategory Category { get; set; }
    public RunStatus Status { get; set; }
    public RunPhase Phase { get; set; }
    public long RemainingMilliseconds { get; set; }
    public int AwardedPoints { get; set; }
    public string? BonusResultSummary { get; set; }
    public int BonusPoints { get; set; }
    public int CompletedEvents { get; set; }
    public int TotalEvents { get; set; }
    public required List<ScoreboardEvent> Events { get; set; }
}

public sealed class ScoreboardEvent
{
    public required string Name { get; set; }
    public string? Prompt { get; set; }
    public EventStatus Status { get; set; }
    public int AwardedPoints { get; set; }
}

public sealed class LeaderboardRow
{
    public int Rank { get; set; }
    public required string CompetitorName { get; set; }
    public int Points { get; set; }
    public RunCategory Category { get; set; }
    public RunStatus Status { get; set; }
    public string? RunId { get; set; }
}

public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}

public static class ScoreCalculator
{
    public static int Calculate(EventRecord result, ScoringRule rule)
    {
        if (result.ScoreOverride is int manual)
        {
            return manual;
        }

        if (rule.ManualEventPoints)
        {
            return 0;
        }

        if (result.Status != EventStatus.Completed || result.DurationMs is not long duration || duration < 0)
        {
            return 0;
        }

        var fullDecaySteps = duration / (rule.DecayEverySeconds * 1000L);
        return Math.Max(rule.MinimumPoints, rule.BasePoints - (int)fullDecaySteps * rule.DecayPoints);
    }
}
