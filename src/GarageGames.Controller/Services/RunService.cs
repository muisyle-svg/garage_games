using GarageGames.Controller.Infrastructure;
using GarageGames.Core.Domain;
using GarageGames.Core.Protocol;
using GarageGames.Core.Scoring;
using GarageGames.Core.State;

namespace GarageGames.Controller.Services;

public sealed record ControllerState(
    SeasonDefinition Season,
    Competitor? CurrentCompetitor,
    Competitor? OnDeck,
    RunSnapshot? Run,
    ScoreResult? Score,
    IReadOnlyList<Competitor> Queue,
    IReadOnlyList<DeviceRecord> Devices,
    bool MasterConnected,
    DateTimeOffset ServerTime);

public sealed class RunService(
    ControllerStore store,
    SeasonCatalog seasons,
    ScoringPolicyRegistry scoring,
    RunProjector projector,
    BridgeConnectionManager bridge,
    ILogger<RunService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _operatorSequence = 1;

    public async Task<ControllerState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var season = await GetSelectedSeasonAsync(cancellationToken);
        var competitors = await store.GetCompetitorsAsync(cancellationToken);
        var runRecord = await store.GetCurrentRunAsync(cancellationToken);
        RunSnapshot? run = null;
        ScoreResult? score = null;

        if (runRecord is not null)
        {
            run = await ProjectAsync(runRecord, season, includeClock: true, cancellationToken);
            score = scoring.Get(season.ScoringPolicy).Score(run, season);
        }

        var completedCompetitorIds = (await store.GetRunsAsync(cancellationToken))
            .Where(item => item.Status is RunStatus.Completed or RunStatus.TimedOut)
            .Select(item => item.CompetitorId)
            .ToHashSet();
        var queue = competitors.Where(item => !completedCompetitorIds.Contains(item.Id)).ToArray();
        var current = runRecord is null ? null : competitors.FirstOrDefault(item => item.Id == runRecord.CompetitorId);
        var onDeck = queue.FirstOrDefault(item => current is null || item.Id != current.Id);

        return new(
            season,
            current,
            onDeck,
            run,
            score,
            queue,
            await store.GetDevicesAsync(cancellationToken),
            bridge.Connected,
            DateTimeOffset.UtcNow);
    }

    public async Task SelectSeasonAsync(string seasonId, CancellationToken cancellationToken = default)
    {
        _ = seasons.Get(seasonId);
        if (await store.GetCurrentRunAsync(cancellationToken) is not null)
        {
            throw new InvalidOperationException("Cannot change season while a run is active.");
        }
        await store.SetSettingAsync("selectedSeason", seasonId, cancellationToken);
    }

    public async Task<RunSnapshot> StartAsync(string competitorId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (await store.GetCurrentRunAsync(cancellationToken) is not null)
            {
                throw new InvalidOperationException("A run is already active.");
            }

            var competitor = (await store.GetCompetitorsAsync(cancellationToken))
                .FirstOrDefault(item => item.Id == competitorId)
                ?? throw new KeyNotFoundException("Competitor not found.");
            var season = await GetSelectedSeasonAsync(cancellationToken);
            var record = await store.CreateRunAsync(competitor, season, cancellationToken);
            var start = NewOperatorEvent(record, EventTypes.RunStarted, new { season.TimerSeconds });
            await store.AppendEventAsync(start, cancellationToken);
            await store.AuditAsync("run", "operator", $"Started run for {competitor.Name}.", JsonDefaults.Serialize(record), cancellationToken);

            await bridge.BroadcastAsync(new(
                Protocol.Version,
                Guid.NewGuid().ToString("N"),
                record.Id,
                EventTypes.RunStarted,
                "*",
                0,
                new { timerSeconds = season.TimerSeconds }), cancellationToken);

            return await ProjectAsync(record with { Revision = 1 }, season, false, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RunSnapshot> AppendManualGameEventAsync(
        string type,
        string gameId,
        int? remainingSeconds,
        string note,
        CancellationToken cancellationToken = default)
    {
        if (type is not (EventTypes.AttemptStarted or EventTypes.GameCompleted or EventTypes.BonusHit))
        {
            throw new ArgumentException("Unsupported manual event type.", nameof(type));
        }

        return await AppendToCurrentAsync(
            type,
            new GameEventPayload(gameId, remainingSeconds, type == EventTypes.BonusHit, note),
            "operator",
            cancellationToken);
    }

    public async Task<RunSnapshot> PauseAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await AppendToCurrentAsync(EventTypes.RunPaused, new { }, "operator", cancellationToken);
        await SendRunCommandAsync(snapshot, EventTypes.RunPaused, "*", null, cancellationToken);
        return snapshot;
    }

    public async Task<RunSnapshot> ResumeAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await AppendToCurrentAsync(EventTypes.RunResumed, new { }, "operator", cancellationToken);
        await SendRunCommandAsync(snapshot, EventTypes.RunResumed, "*", null, cancellationToken);
        return snapshot;
    }

    public async Task<RunSnapshot> TimeoutAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await AppendToCurrentAsync(
            EventTypes.RunTimedOut, new { reason = "timer_elapsed" }, "controller", cancellationToken);
        await SendRunCommandAsync(snapshot, EventTypes.RunTimedOut, "*", null, cancellationToken);
        return snapshot;
    }

    public async Task<RunSnapshot> AbortAsync(string reason, CancellationToken cancellationToken = default)
    {
        var snapshot = await AppendToCurrentAsync(EventTypes.RunAborted, new { reason }, "operator", cancellationToken);
        await bridge.BroadcastAsync(new(
            Protocol.Version,
            Guid.NewGuid().ToString("N"),
            snapshot.RunId,
            EventTypes.RunAborted,
            "*",
            Elapsed(snapshot.StartedAt),
            new { reason }), cancellationToken);
        return snapshot;
    }

    public async Task<RunSnapshot> UndoAsync(string reason, CancellationToken cancellationToken = default)
    {
        var current = await store.GetCurrentRunAsync(cancellationToken)
            ?? throw new InvalidOperationException("No active run.");
        var events = await store.GetEventsAsync(current.Id, cancellationToken);
        var target = events.LastOrDefault(item =>
            item.Type is EventTypes.AttemptStarted or EventTypes.GameCompleted or
            EventTypes.BonusHit or EventTypes.CorrectionApplied &&
            item.Source != "undo")
            ?? throw new InvalidOperationException("There is no event to undo.");
        return await AppendToCurrentAsync(
            EventTypes.EventVoided,
            new VoidEventPayload(target.EventId, reason),
            "undo",
            cancellationToken);
    }

    public Task<RunSnapshot> CorrectAsync(
        CorrectionPayload correction,
        string source,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default) =>
        AppendToCurrentAsync(
            EventTypes.CorrectionApplied,
            correction,
            source,
            cancellationToken,
            idempotencyKey is null ? null : $"correction:{idempotencyKey}");

    public async Task<bool> HandleEnvelopeAsync(
        ProtocolEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        if (envelope.ProtocolVersion != Protocol.Version)
        {
            throw new InvalidOperationException(
                $"Protocol {envelope.ProtocolVersion} is incompatible with controller protocol {Protocol.Version}.");
        }

        if (envelope.Type == EventTypes.DeviceHealth)
        {
            var health = System.Text.Json.JsonSerializer.Deserialize<DeviceHealthPayload>(
                JsonDefaults.Serialize(envelope.Payload),
                JsonDefaults.Options) ?? throw new InvalidDataException("Invalid health payload.");
            await store.UpsertDeviceAsync(envelope.DeviceId, health, cancellationToken);
            return true;
        }

        if (envelope.Type == EventTypes.MasterStartRequested)
        {
            var state = await GetStateAsync(cancellationToken);
            var competitor = state.Queue.FirstOrDefault()
                ?? throw new InvalidOperationException("No competitor is queued.");
            await StartAsync(competitor.Id, cancellationToken);
            return true;
        }

        if (!EventTypes.IsRunEvent(envelope.Type))
        {
            logger.LogDebug("Ignoring unsupported bridge event {Type}.", envelope.Type);
            return false;
        }

        var current = await store.GetCurrentRunAsync(cancellationToken)
            ?? throw new InvalidOperationException("No active run.");
        if (!string.IsNullOrWhiteSpace(envelope.RunId) &&
            !string.Equals(envelope.RunId, current.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Event run ID does not match the active run.");
        }

        var payloadJson = JsonDefaults.Serialize(envelope.Payload ?? new { });
        if (envelope.Type is EventTypes.AttemptStarted or EventTypes.GameCompleted or EventTypes.BonusHit)
        {
            var payload = System.Text.Json.JsonSerializer.Deserialize<GameEventPayload>(payloadJson, JsonDefaults.Options);
            if (payload is null || string.IsNullOrWhiteSpace(payload.GameId))
            {
                var gameId = await store.GetAssignedGameIdAsync(envelope.DeviceId, cancellationToken)
                    ?? throw new InvalidOperationException($"Device {envelope.DeviceId} has no game assignment.");
                payloadJson = JsonDefaults.Serialize(new GameEventPayload(gameId, payload?.RemainingSeconds, payload?.Bonus, payload?.Note));
            }
        }

        var item = new DeviceEvent(
            envelope.EventId,
            current.Id,
            envelope.DeviceId,
            envelope.BootId,
            envelope.Sequence,
            envelope.Type,
            envelope.ElapsedMilliseconds,
            DateTimeOffset.UtcNow,
            payloadJson);
        var inserted = await store.AppendEventAsync(item, cancellationToken);
        if (inserted)
        {
            await FinalizeProjectionAsync(current, cancellationToken);
        }
        return true;
    }

    public async Task<IReadOnlyList<LeaderboardEntry>> GetLeaderboardAsync(CancellationToken cancellationToken = default)
    {
        var runs = (await store.GetRunsAsync(cancellationToken))
            .Where(item => item.Status is RunStatus.Completed or RunStatus.TimedOut)
            .ToArray();
        var entries = new List<LeaderboardEntry>();
        foreach (var run in runs)
        {
            var score = await store.GetScoreAsync(run.Id, cancellationToken);
            if (score is null)
            {
                continue;
            }
            var season = seasons.Get(run.SeasonId);
            var snapshot = await ProjectAsync(run, season, false, cancellationToken);
            entries.Add(new(
                0,
                run.CompetitorId,
                run.CompetitorName,
                run.Id,
                score.Total,
                snapshot.RemainingSeconds,
                snapshot.CompletionCount,
                snapshot.AttemptCount,
                snapshot.BonusCount,
                run.FinishedAt ?? run.StartedAt));
        }

        return entries
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.RemainingSeconds)
            .ThenBy(item => item.FinishedAt)
            .Select((item, index) => item with { Rank = index + 1 })
            .ToArray();
    }

    public async Task<IReadOnlyList<object>> SearchParticipantsAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var runs = await store.GetRunsAsync(cancellationToken);
        var filtered = runs.Where(item =>
            string.IsNullOrWhiteSpace(query) ||
            item.CompetitorName.Contains(query, StringComparison.OrdinalIgnoreCase));
        var result = new List<object>();
        foreach (var run in filtered)
        {
            result.Add(new
            {
                run.Id,
                run.CompetitorName,
                run.Status,
                run.StartedAt,
                run.FinishedAt,
                score = await store.GetScoreAsync(run.Id, cancellationToken)
            });
        }
        return result;
    }

    private async Task<RunSnapshot> AppendToCurrentAsync(
        string type,
        object payload,
        string source,
        CancellationToken cancellationToken,
        string? eventId = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = await store.GetCurrentRunAsync(cancellationToken)
                ?? throw new InvalidOperationException("No active run.");
            var item = NewOperatorEvent(current, type, payload, source, eventId);
            var inserted = await store.AppendEventAsync(item, cancellationToken);
            if (!inserted)
            {
                return await ProjectAsync(
                    current,
                    seasons.Get(current.SeasonId),
                    false,
                    cancellationToken);
            }
            await store.AuditAsync("run-event", source, type, JsonDefaults.Serialize(item), cancellationToken);
            return await FinalizeProjectionAsync(current with { Revision = current.Revision + 1 }, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RunSnapshot> FinalizeProjectionAsync(
        RunRecord current,
        CancellationToken cancellationToken)
    {
        var season = seasons.Get(current.SeasonId);
        var snapshot = await ProjectAsync(current, season, false, cancellationToken);
        ScoreResult? result = null;
        DateTimeOffset? finished = null;
        if (snapshot.Status is RunStatus.Completed or RunStatus.TimedOut or RunStatus.Aborted)
        {
            result = scoring.Get(season.ScoringPolicy).Score(snapshot, season);
            finished = DateTimeOffset.UtcNow;
        }
        await store.UpdateRunAsync(current.Id, snapshot.Status, result, finished, snapshot, cancellationToken);
        await store.QueueOutboxAsync(
            $"state:{snapshot.RunId}:{snapshot.Revision}",
            "state",
            JsonDefaults.Serialize(new { snapshot, score = result }),
            cancellationToken);

        if (snapshot.Status == RunStatus.Bonus)
        {
            if (current.Status != RunStatus.Bonus)
            {
                await SendRunCommandAsync(
                    snapshot,
                    EventTypes.BonusStarted,
                    "*",
                    new { required = season.RequiredCompletions },
                    cancellationToken);
            }

            var nextGame = BonusOrder(snapshot.Events)
                .FirstOrDefault(gameId =>
                    snapshot.Games.First(game => game.GameId == gameId).Bonus == false);
            if (!string.IsNullOrWhiteSpace(nextGame))
            {
                var target = await store.GetDeviceIdForGameAsync(nextGame, cancellationToken) ?? "*";
                await SendRunCommandAsync(
                    snapshot,
                    EventTypes.BonusCue,
                    target,
                    new { gameId = nextGame },
                    cancellationToken);
            }
        }
        else if (snapshot.Status == RunStatus.Completed && current.Status == RunStatus.Bonus)
        {
            await SendRunCommandAsync(snapshot, EventTypes.BonusAllDone, "*", null, cancellationToken);
        }
        return snapshot;
    }

    private async Task<RunSnapshot> ProjectAsync(
        RunRecord record,
        SeasonDefinition season,
        bool includeClock,
        CancellationToken cancellationToken)
    {
        var events = (await store.GetEventsAsync(record.Id, cancellationToken)).ToList();
        if (includeClock && record.Status == RunStatus.Active)
        {
            events.Add(new(
                $"clock-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                record.Id,
                "controller",
                "runtime",
                long.MaxValue,
                "clock",
                Elapsed(record.StartedAt),
                DateTimeOffset.UtcNow,
                "{}",
                "clock"));
        }

        var projected = projector.Project(
            record.Id,
            record.CompetitorId,
            record.CompetitorName,
            record.StartedAt,
            season,
            events);
        return projected with
        {
            Revision = record.Revision,
            Status = record.Status is RunStatus.Interrupted ? RunStatus.Interrupted : projected.Status,
            FinishedAt = record.FinishedAt ?? projected.FinishedAt
        };
    }

    private DeviceEvent NewOperatorEvent(
        RunRecord run,
        string type,
        object payload,
        string source = "operator",
        string? eventId = null) =>
        new(
            eventId ?? Guid.NewGuid().ToString("N"),
            run.Id,
            "controller",
            Environment.MachineName,
            Interlocked.Increment(ref _operatorSequence),
            type,
            Elapsed(run.StartedAt),
            DateTimeOffset.UtcNow,
            JsonDefaults.Serialize(payload),
            source);

    private async Task<SeasonDefinition> GetSelectedSeasonAsync(CancellationToken cancellationToken)
    {
        var selected = await store.GetSettingAsync("selectedSeason", cancellationToken);
        if (string.IsNullOrWhiteSpace(selected))
        {
            selected = seasons.All.FirstOrDefault(item => item.Id == "2026")?.Id ?? seasons.All.Last().Id;
            await store.SetSettingAsync("selectedSeason", selected, cancellationToken);
        }
        return seasons.Get(selected);
    }

    private static long Elapsed(DateTimeOffset startedAt) =>
        Math.Max(0, (long)(DateTimeOffset.UtcNow - startedAt).TotalMilliseconds);

    private async Task SendRunCommandAsync(
        RunSnapshot snapshot,
        string type,
        string target,
        object? payload,
        CancellationToken cancellationToken)
    {
        await bridge.BroadcastAsync(new(
            Protocol.Version,
            Guid.NewGuid().ToString("N"),
            snapshot.RunId,
            type,
            target,
            Elapsed(snapshot.StartedAt),
            payload), cancellationToken);
    }

    private static IReadOnlyList<string> BonusOrder(IReadOnlyList<DeviceEvent> events) =>
        events
            .Where(item => item.Type == EventTypes.GameCompleted)
            .OrderBy(item => item.ReceivedAt)
            .Select(item => item.Payload<GameEventPayload>()?.GameId)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToArray();
}
