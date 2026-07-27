using GarageGames.Core.Domain;

namespace GarageGames.Core.State;

public sealed class RunProjector
{
    public RunSnapshot Project(
        string runId,
        string competitorId,
        string competitorName,
        DateTimeOffset startedAt,
        SeasonDefinition season,
        IReadOnlyList<DeviceEvent> orderedEvents)
    {
        var voided = orderedEvents
            .Where(item => item.Type == EventTypes.EventVoided)
            .Select(item => item.Payload<VoidEventPayload>()?.TargetEventId)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToHashSet(StringComparer.Ordinal);

        var progress = season.EnabledGames.ToDictionary(
            game => game.Id,
            game => new MutableProgress(game),
            StringComparer.OrdinalIgnoreCase);

        var status = RunStatus.Active;
        DateTimeOffset? finishedAt = null;
        var pausedAt = (long?)null;
        long totalPausedMilliseconds = 0;

        foreach (var item in orderedEvents.OrderBy(item => item.ReceivedAt).ThenBy(item => item.Sequence))
        {
            if (voided.Contains(item.EventId) || item.Type == EventTypes.EventVoided)
            {
                continue;
            }

            switch (item.Type)
            {
                case EventTypes.RunPaused:
                    status = RunStatus.Paused;
                    pausedAt = item.ElapsedMilliseconds;
                    break;
                case EventTypes.RunResumed:
                    if (pausedAt.HasValue)
                    {
                        totalPausedMilliseconds += Math.Max(0, item.ElapsedMilliseconds - pausedAt.Value);
                    }
                    pausedAt = null;
                    status = RunStatus.Active;
                    break;
                case EventTypes.RunAborted:
                    status = RunStatus.Aborted;
                    finishedAt = item.ReceivedAt;
                    break;
                case EventTypes.RunTimedOut:
                    status = RunStatus.TimedOut;
                    finishedAt = item.ReceivedAt;
                    break;
                case EventTypes.AttemptStarted:
                case EventTypes.GameCompleted:
                case EventTypes.BonusHit:
                    ApplyGameEvent(progress, item, season.TimerSeconds, totalPausedMilliseconds);
                    break;
                case EventTypes.CorrectionApplied:
                    ApplyCorrection(progress, item);
                    break;
            }
        }

        var latestElapsed = orderedEvents.Count == 0
            ? 0
            : orderedEvents.Max(item => item.ElapsedMilliseconds);
        var effectiveElapsed = Math.Max(0, latestElapsed - totalPausedMilliseconds);
        var remaining = Math.Max(0, season.TimerSeconds - (int)(effectiveElapsed / 1000));

        var completionCount = progress.Values.Count(value => value.CompletionRemainingSeconds.HasValue);
        var bonusCount = progress.Values.Count(value => value.Bonus);
        if (status is RunStatus.Active or RunStatus.Paused &&
            completionCount >= season.RequiredCompletions)
        {
            status = bonusCount >= season.RequiredCompletions
                ? RunStatus.Completed
                : RunStatus.Bonus;
            if (status == RunStatus.Completed)
            {
                finishedAt = orderedEvents
                    .Where(item => item.Type is EventTypes.BonusHit or EventTypes.CorrectionApplied)
                    .MaxBy(item => item.ReceivedAt)?.ReceivedAt;
            }
        }

        if (status is RunStatus.Active or RunStatus.Paused or RunStatus.Bonus && remaining == 0)
        {
            status = RunStatus.TimedOut;
        }

        return new(
            runId,
            season.Id,
            competitorId,
            competitorName,
            status,
            startedAt,
            finishedAt,
            season.TimerSeconds,
            remaining,
            orderedEvents.Count,
            progress.Values
                .OrderBy(value => value.Definition.Order)
                .Select(value => value.ToImmutable())
                .ToArray(),
            orderedEvents.ToArray(),
            season.ScoringPolicy);
    }

    private static void ApplyGameEvent(
        IDictionary<string, MutableProgress> progress,
        DeviceEvent item,
        int timerSeconds,
        long pausedMilliseconds)
    {
        var payload = item.Payload<GameEventPayload>();
        if (payload is null || !progress.TryGetValue(payload.GameId, out var game))
        {
            return;
        }

        var remaining = payload.RemainingSeconds ??
            Math.Max(0, timerSeconds - (int)((item.ElapsedMilliseconds - pausedMilliseconds) / 1000));

        if (item.Type == EventTypes.AttemptStarted && !game.AttemptRemainingSeconds.HasValue)
        {
            game.AttemptRemainingSeconds = remaining;
            game.AttemptEventId = item.EventId;
        }
        else if (item.Type == EventTypes.GameCompleted && !game.CompletionRemainingSeconds.HasValue)
        {
            game.AttemptRemainingSeconds ??= remaining;
            game.CompletionRemainingSeconds = remaining;
            game.CompletionEventId = item.EventId;
        }
        else if (item.Type == EventTypes.BonusHit)
        {
            game.Bonus = true;
            game.BonusEventId = item.EventId;
        }
    }

    private static void ApplyCorrection(
        IDictionary<string, MutableProgress> progress,
        DeviceEvent item)
    {
        var correction = item.Payload<CorrectionPayload>();
        if (correction is null || !progress.TryGetValue(correction.GameId, out var game))
        {
            return;
        }

        switch (correction.Field)
        {
            case "attemptRemainingSeconds":
                game.AttemptRemainingSeconds = correction.NumericValue.HasValue
                    ? (int)correction.NumericValue.Value
                    : null;
                game.AttemptEventId = item.EventId;
                break;
            case "completionRemainingSeconds":
                game.CompletionRemainingSeconds = correction.NumericValue.HasValue
                    ? (int)correction.NumericValue.Value
                    : null;
                game.CompletionEventId = item.EventId;
                break;
            case "bonus":
                game.Bonus = correction.BooleanValue ?? false;
                game.BonusEventId = item.EventId;
                break;
        }
    }

    private sealed class MutableProgress(GameDefinition definition)
    {
        public GameDefinition Definition { get; } = definition;
        public int? AttemptRemainingSeconds { get; set; }
        public int? CompletionRemainingSeconds { get; set; }
        public bool Bonus { get; set; }
        public string? AttemptEventId { get; set; }
        public string? CompletionEventId { get; set; }
        public string? BonusEventId { get; set; }

        public GameProgress ToImmutable() =>
            new(
                Definition.Id,
                Definition.Name,
                Definition.Order,
                AttemptRemainingSeconds,
                CompletionRemainingSeconds,
                Bonus,
                AttemptEventId,
                CompletionEventId,
                BonusEventId);
    }
}
