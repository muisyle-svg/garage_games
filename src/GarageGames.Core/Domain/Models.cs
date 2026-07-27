using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GarageGames.Core.Domain;

public static class EventTypes
{
    public const string RunStarted = "run_started";
    public const string RunPaused = "run_paused";
    public const string RunResumed = "run_resumed";
    public const string RunAborted = "run_aborted";
    public const string RunTimedOut = "run_timed_out";
    public const string AttemptStarted = "attempt_started";
    public const string GameCompleted = "game_completed";
    public const string BonusHit = "bonus_hit";
    public const string BonusStarted = "bonus_started";
    public const string BonusCue = "bonus_cue";
    public const string BonusAllDone = "bonus_all_done";
    public const string EventVoided = "event_voided";
    public const string CorrectionApplied = "correction_applied";
    public const string DeviceRegistered = "device_registered";
    public const string DeviceHealth = "device_health";
    public const string DeviceFault = "device_fault";
    public const string MasterStartRequested = "master_start_requested";

    public static bool IsRunEvent(string value) => value is
        RunStarted or RunPaused or RunResumed or RunAborted or RunTimedOut or
        AttemptStarted or GameCompleted or BonusHit or BonusStarted or BonusCue or
        BonusAllDone or EventVoided or CorrectionApplied;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunStatus
{
    Queued,
    Active,
    Paused,
    Bonus,
    Completed,
    TimedOut,
    Aborted,
    Interrupted
}

public sealed record GameDefinition(
    string Id,
    string Name,
    string ShortName,
    int Order,
    decimal Weight,
    bool Enabled = true,
    string StationModule = "standard");

public sealed record SeasonDefinition(
    string Id,
    string Name,
    int TimerSeconds,
    int RequiredCompletions,
    string ScoringPolicy,
    int BonusPointsPerGame,
    int AttemptBonusThreshold,
    int AttemptBonusPoints,
    int TimeBonusCompletionThreshold,
    IReadOnlyList<GameDefinition> Games,
    IReadOnlyDictionary<string, decimal>? ScoringSettings = null,
    bool Draft = false)
{
    public IReadOnlyList<GameDefinition> EnabledGames =>
        Games.Where(game => game.Enabled).OrderBy(game => game.Order).ToArray();
}

public sealed record Competitor(
    string Id,
    string Name,
    int QueuePosition,
    DateTimeOffset CreatedAt,
    string Notes = "");

public sealed record DeviceEvent(
    string EventId,
    string RunId,
    string DeviceId,
    string BootId,
    long Sequence,
    string Type,
    long ElapsedMilliseconds,
    DateTimeOffset ReceivedAt,
    string PayloadJson,
    string Source = "device")
{
    public T? Payload<T>() => JsonSerializer.Deserialize<T>(PayloadJson, JsonDefaults.Options);
}

public sealed record GameEventPayload(
    string GameId,
    int? RemainingSeconds = null,
    bool? Bonus = null,
    string? Note = null);

public sealed record VoidEventPayload(string TargetEventId, string Reason);

public sealed record CorrectionPayload(
    string GameId,
    string Field,
    decimal? NumericValue,
    bool? BooleanValue,
    string Reason,
    long ExpectedRevision);

public sealed record GameProgress(
    string GameId,
    string Name,
    int Order,
    int? AttemptRemainingSeconds,
    int? CompletionRemainingSeconds,
    bool Bonus,
    string? AttemptEventId,
    string? CompletionEventId,
    string? BonusEventId)
{
    public bool Attempted => AttemptRemainingSeconds.HasValue;
    public bool Completed => CompletionRemainingSeconds.HasValue;
    public int? DurationSeconds =>
        AttemptRemainingSeconds.HasValue && CompletionRemainingSeconds.HasValue
            ? Math.Max(0, AttemptRemainingSeconds.Value - CompletionRemainingSeconds.Value)
            : null;
}

public sealed record RunSnapshot(
    string RunId,
    string SeasonId,
    string CompetitorId,
    string CompetitorName,
    RunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int TimerSeconds,
    int RemainingSeconds,
    int Revision,
    IReadOnlyList<GameProgress> Games,
    IReadOnlyList<DeviceEvent> Events,
    string ScoringPolicy)
{
    public int AttemptCount => Games.Count(game => game.Attempted);
    public int CompletionCount => Games.Count(game => game.Completed);
    public int BonusCount => Games.Count(game => game.Bonus);
}

public sealed record GameScore(
    string GameId,
    string Name,
    decimal Points,
    string Explanation);

public sealed record ScoreResult(
    string Policy,
    IReadOnlyList<GameScore> Games,
    decimal EventPoints,
    decimal BonusPoints,
    decimal AttemptBonus,
    decimal TimeBonus,
    decimal Total,
    IReadOnlyList<string> Explanations);

public sealed record LeaderboardEntry(
    int Rank,
    string CompetitorId,
    string CompetitorName,
    string RunId,
    decimal Score,
    int RemainingSeconds,
    int Completed,
    int Attempted,
    int Bonuses,
    DateTimeOffset FinishedAt);

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
