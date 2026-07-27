using GarageGames.Core.Domain;

namespace GarageGames.Core.Scoring;

public interface IScoringPolicy
{
    string Id { get; }
    ScoreResult Score(RunSnapshot run, SeasonDefinition season);
}

public sealed class ScoringPolicyRegistry(IEnumerable<IScoringPolicy> policies)
{
    private readonly IReadOnlyDictionary<string, IScoringPolicy> _policies =
        policies.ToDictionary(policy => policy.Id, StringComparer.OrdinalIgnoreCase);

    public IScoringPolicy Get(string id) =>
        _policies.TryGetValue(id, out var policy)
            ? policy
            : throw new InvalidOperationException($"Unknown scoring policy '{id}'.");
}

public sealed class Baseline2025ScoringPolicy : IScoringPolicy
{
    public string Id => "2025-baseline";

    public ScoreResult Score(RunSnapshot run, SeasonDefinition season)
    {
        var definitions = season.EnabledGames.ToDictionary(game => game.Id);
        var gameScores = run.Games.Select(game =>
        {
            var points = game.Completed ? definitions[game.GameId].Weight : 0m;
            return new GameScore(
                game.GameId,
                game.Name,
                points,
                game.Completed
                    ? $"Completed: {points:0.##} configured points."
                    : "Not completed: 0 points.");
        }).ToArray();

        var eventPoints = gameScores.Sum(game => game.Points);
        var bonusPoints = run.BonusCount * season.BonusPointsPerGame;
        var attemptBonus = run.AttemptCount >= season.AttemptBonusThreshold
            ? season.AttemptBonusPoints
            : 0;
        var timeBonus = run.CompletionCount >= season.TimeBonusCompletionThreshold
            ? run.RemainingSeconds
            : 0;
        var total = eventPoints + bonusPoints + attemptBonus + timeBonus;

        return new(
            Id,
            gameScores,
            eventPoints,
            bonusPoints,
            attemptBonus,
            timeBonus,
            total,
            [
                $"{run.CompletionCount} completed games produced {eventPoints:0.##} event points.",
                $"{run.BonusCount} bonuses produced {bonusPoints:0.##} points.",
                $"Attempt bonus: {attemptBonus:0.##}.",
                $"Time bonus: {timeBonus:0.##}."
            ]);
    }
}

public sealed class TimeDecay2026ScoringPolicy : IScoringPolicy
{
    public string Id => "2026-time-decay";

    public ScoreResult Score(RunSnapshot run, SeasonDefinition season)
    {
        var settings = season.ScoringSettings ??
            throw new InvalidOperationException("2026 scoring settings are missing.");
        var basePoints = settings.GetValueOrDefault("basePoints", 100m);
        var minimumPoints = settings.GetValueOrDefault("minimumPoints", 50m);
        var intervalSeconds = (int)settings.GetValueOrDefault("decayIntervalSeconds", 5m);
        var intervalPenalty = settings.GetValueOrDefault("decayPointsPerInterval", 5m);

        if (intervalSeconds <= 0)
        {
            throw new InvalidOperationException("Decay interval must be positive.");
        }

        var gameScores = run.Games.Select(game =>
        {
            if (!game.Completed || !game.DurationSeconds.HasValue)
            {
                return new GameScore(game.GameId, game.Name, 0, "Not completed: 0 points.");
            }

            var intervals = game.DurationSeconds.Value / intervalSeconds;
            var points = Math.Max(minimumPoints, basePoints - intervals * intervalPenalty);
            return new GameScore(
                game.GameId,
                game.Name,
                points,
                $"{game.DurationSeconds}s duration: {basePoints:0.##} - " +
                $"{intervals} × {intervalPenalty:0.##}, floor {minimumPoints:0.##}.");
        }).ToArray();

        var eventPoints = gameScores.Sum(game => game.Points);
        var bonusPoints = run.BonusCount * season.BonusPointsPerGame;
        var attemptBonus = run.AttemptCount >= season.AttemptBonusThreshold
            ? season.AttemptBonusPoints
            : 0;
        var timeBonus = run.CompletionCount >= season.TimeBonusCompletionThreshold
            ? run.RemainingSeconds
            : 0;
        var total = eventPoints + bonusPoints + attemptBonus + timeBonus;

        return new(
            Id,
            gameScores,
            eventPoints,
            bonusPoints,
            attemptBonus,
            timeBonus,
            total,
            [
                $"Time-decay game points: {eventPoints:0.##}.",
                $"Bonus points: {bonusPoints:0.##}.",
                $"Attempt bonus: {attemptBonus:0.##}.",
                $"Time bonus: {timeBonus:0.##}."
            ]);
    }
}

