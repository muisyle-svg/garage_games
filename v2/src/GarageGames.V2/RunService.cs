using System.Text.Json;

namespace GarageGames.V2;

public sealed class CommandException : InvalidOperationException
{
    public CommandException(string message) : base(message) { }
}

public sealed class PreflightResult
{
    public required string DeviceId { get; set; }
    public required string EventId { get; set; }
    public DeviceAvailability Availability { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public bool Passed { get; set; }
    public bool ManualOverrideAvailable { get; set; }
    public string? Message { get; set; }
}

public sealed class EditRunRequest
{
    public int ExpectedRevision { get; set; }
    public required string Reason { get; set; }
    public string? CompetitorId { get; set; }
    public RunCategory? Category { get; set; }
    public bool ReplaceExistingOfficial { get; set; }
    public RunStatus? Status { get; set; }
    public int? DurationLimitSeconds { get; set; }
    public long? ActiveElapsedMs { get; set; }
    public string? BonusResultJson { get; set; }
    public int? BonusPointsOverride { get; set; }
    public bool ClearBonusPointsOverride { get; set; }
    public string? Notes { get; set; }
    public List<EventEditRequest> Events { get; set; } = [];
}

public sealed class EventEditRequest
{
    public required string EventId { get; set; }
    public EventStatus? Status { get; set; }
    public long? StartElapsedMs { get; set; }
    public bool ClearStartElapsedMs { get; set; }
    public long? FinishElapsedMs { get; set; }
    public bool ClearFinishElapsedMs { get; set; }
    public long? DurationMs { get; set; }
    public int? ScoreOverride { get; set; }
    public bool ClearScoreOverride { get; set; }
    public string? MeasurementJson { get; set; }
    public string? Notes { get; set; }
}

public sealed class RunService
{
    public const string DatabaseClearConfirmationPhrase = "CLEAR ALL DATA";

    private readonly object _gate = new();
    private readonly RunStore _store;
    private readonly EditionDefinition _edition;
    private readonly IMonotonicClock _clock;
    private readonly StoreSnapshot _data;
    private RunRecord? _current;
    private RunRecord? _lastDisplayedRun;
    private long _clockAnchorMilliseconds;

    public RunService(RunStore store, EditionDefinition edition, IMonotonicClock clock)
    {
        EditionDefinition.Validate(edition);
        _store = store;
        _edition = edition;
        _clock = clock;
        _store.EnsureDevices(edition);
        _data = _store.Load();

        var unfinished = _data.Runs.Where(r => r.Status is RunStatus.Armed or RunStatus.Countdown or RunStatus.Active or RunStatus.Paused or RunStatus.Finished).ToList();
        if (unfinished.Count > 1)
        {
            throw new InvalidDataException("The database contains more than one unfinished run; manual recovery is required.");
        }

        _current = unfinished.SingleOrDefault();
        _lastDisplayedRun = _current is null
            ? _data.Runs.OrderByDescending(r => r.CreatedAt).FirstOrDefault()
            : null;
        if (_current is not null && _current.Status == RunStatus.Active)
        {
            _current.Status = RunStatus.Paused;
            _current.PausedFromPhase = _current.Phase.ToString();
            _current.Revision++;
            var recovery = CreateMessage(NewId("recovery"), _current.Id, _current.Id, "system", "recovery-paused", _current.ActiveElapsedMs,
                MessageDisposition.Paused, "Recovered unfinished run paused; process downtime was not counted.", null);
            _store.SaveRunAndMessage(_current, recovery);
            _data.Messages.Add(recovery);
        }

        _clockAnchorMilliseconds = _clock.MonotonicMilliseconds;
        UpdateDeviceLeds();
    }

    public string EditionId => _edition.EditionId;

    public string ClearAllData(string confirmationPhrase)
    {
        lock (_gate)
        {
            if (!string.Equals(confirmationPhrase, DatabaseClearConfirmationPhrase, StringComparison.Ordinal))
            {
                throw new CommandException($"Type {DatabaseClearConfirmationPhrase} exactly to clear the database.");
            }

            RefreshActiveClock();
            var backupPath = _store.CreateBackup();
            _store.ClearPersistedData(_edition);

            _data.Competitors.Clear();
            _data.Queue.Clear();
            _data.Devices.Clear();
            _data.Devices.AddRange(_edition.Events.Select(eventDefinition => new DeviceRecord
            {
                DeviceId = eventDefinition.DeviceId,
                EventId = eventDefinition.EventId,
                Availability = DeviceAvailability.Online,
                Led = LedState.Ready
            }));
            _data.Runs.Clear();
            _data.Messages.Clear();
            _data.Edits.Clear();
            _data.SelectedCompetitorId = null;
            _data.SelectedRunCategory = null;
            _current = null;
            _lastDisplayedRun = null;
            _clockAnchorMilliseconds = _clock.MonotonicMilliseconds;
            UpdateDeviceLeds();

            return backupPath;
        }
    }

    public void Checkpoint()
    {
        lock (_gate)
        {
            RefreshActiveClock();
        }
    }

    public bool IsCurrentRun(string runId)
    {
        lock (_gate)
        {
            return _current?.Id == runId;
        }
    }

    public RunCountdownState GetCountdownState()
    {
        lock (_gate)
        {
            return _current is null
                ? new RunCountdownState(null, null)
                : new RunCountdownState(_current.Id, _current.Status);
        }
    }

    public MasterRunStatus GetMasterStatus()
    {
        lock (_gate)
        {
            RefreshActiveClock();
            var run = _current ?? _lastDisplayedRun;
            if (run is null)
            {
                return new MasterRunStatus("NONE", 0);
            }

            var state = run.Status switch
            {
                RunStatus.Armed => "ARMED",
                RunStatus.Countdown => "COUNTDOWN",
                RunStatus.Active => "ACTIVE",
                RunStatus.Paused => "PAUSED",
                RunStatus.Finished or RunStatus.Completed or RunStatus.TimedOut => "FINISHED",
                _ => "NONE"
            };
            var remainingMilliseconds = Math.Max(0, run.Edition.DurationLimitSeconds * 1000L - run.ActiveElapsedMs);
            var remainingSeconds = state is "ARMED" or "COUNTDOWN" or "ACTIVE" or "PAUSED"
                ? (int)Math.Min(int.MaxValue, (remainingMilliseconds + 999) / 1000)
                : 0;
            return new MasterRunStatus(state, remainingSeconds);
        }
    }

    public OperatorSnapshot GetOperatorSnapshot(bool simulationMode = true)
    {
        lock (_gate)
        {
            RefreshActiveClock();
            return new OperatorSnapshot
            {
                EditionId = _edition.EditionId,
                EditionName = _edition.Name,
                DurationLimitSeconds = _edition.DurationLimitSeconds,
                SimulationMode = simulationMode,
                SelectedCompetitorId = _data.SelectedCompetitorId ?? "",
                SelectedRunCategory = _data.SelectedRunCategory,
                CurrentRun = _current is null ? (_lastDisplayedRun is null ? null : Clone(_lastDisplayedRun)) : Clone(_current),
                Events = _edition.ToSnapshot().Events,
                Competitors = _data.Competitors.Select(Clone).ToList(),
                Queue = _data.Queue.OrderBy(q => q.Position).Select(Clone).ToList(),
                Devices = _data.Devices.Select(Clone).ToList(),
                History = _data.Runs.OrderByDescending(r => r.CreatedAt).Select(Clone).ToList(),
                Messages = _data.Messages.OrderByDescending(m => m.Id).Take(250).Select(Clone).ToList(),
                Edits = _data.Edits.OrderByDescending(e => e.Id).Take(250).Select(Clone).ToList(),
                Leaderboard = BuildLeaderboard()
            };
        }
    }

    public ScoreboardSnapshot GetScoreboard(bool simulationMode = true)
    {
        lock (_gate)
        {
            RefreshActiveClock();
            var competitorNames = _data.Competitors.ToDictionary(c => c.Id, c => c.Name);
            var displayedRun = _current ?? _lastDisplayedRun;
            var current = displayedRun is null ? null : new ScoreboardRun
            {
                CompetitorName = competitorNames.GetValueOrDefault(displayedRun.CompetitorId, "Unknown competitor"),
                Category = displayedRun.Category,
                Status = displayedRun.Status,
                Phase = displayedRun.Phase,
                RemainingMilliseconds = Math.Max(0, displayedRun.Edition.DurationLimitSeconds * 1000L - displayedRun.ActiveElapsedMs),
                AwardedPoints = displayedRun.TotalPoints,
                BonusPoints = displayedRun.BonusPoints,
                BonusResultSummary = displayedRun.BonusResultJson is null ? null : "Recorded",
                CompletedEvents = displayedRun.CompletedEventCount,
                TotalEvents = displayedRun.Events.Count,
                Events = displayedRun.Events.Select(e => new ScoreboardEvent
                {
                    Name = e.Name,
                    Prompt = e.Prompt,
                    Status = e.Status,
                    AwardedPoints = e.Score
                }).ToList()
            };

            var next = _data.Queue.OrderBy(q => q.Position).FirstOrDefault();
            return new ScoreboardSnapshot
            {
                EditionName = _edition.Name,
                DurationLimitSeconds = _edition.DurationLimitSeconds,
                SimulationMode = simulationMode,
                CurrentRun = current,
                OnDeckName = next is null ? null : competitorNames.GetValueOrDefault(next.CompetitorId),
                Leaderboard = BuildLeaderboard()
            };
        }
    }

    public CompetitorRecord AddCompetitor(string name)
    {
        lock (_gate)
        {
            name = name.Trim();
            if (name.Length is < 1 or > 120)
            {
                throw new CommandException("Competitor name must be between 1 and 120 characters.");
            }

            var competitor = new CompetitorRecord
            {
                Id = NewId("competitor"),
                Name = name,
                EditionId = _edition.EditionId,
                CreatedAt = _clock.UtcNow
            };
            _store.AddCompetitor(competitor);
            _data.Competitors.Add(competitor);
            return Clone(competitor);
        }
    }

    public QueueItemRecord AddToQueue(string competitorId, RunCategory category, bool replaceExistingOfficial = false, string? reason = null)
    {
        lock (_gate)
        {
            RequireCompetitor(competitorId);
            if (category != RunCategory.Official)
            {
                replaceExistingOfficial = false;
            }

            var item = new QueueItemRecord
            {
                Id = NewId("queue"),
                CompetitorId = competitorId,
                Category = category,
                ReplaceExistingOfficial = replaceExistingOfficial,
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                Position = _data.Queue.Count
            };
            _data.Queue.Add(item);
            NormalizeQueue();
            try
            {
                _store.SaveQueue(_data.Queue);
            }
            catch
            {
                ReloadInMemoryAfterPersistenceFailure();
                throw;
            }
            return Clone(item);
        }
    }

    public RunRecord ArmCompetitor(string competitorId, RunCategory category)
    {
        lock (_gate)
        {
            RefreshActiveClock();
            if (_current is not null)
            {
                throw new CommandException("Record or finish the current run before starting another competitor.");
            }
            RequireCompetitor(competitorId);
            if (!Enum.IsDefined(category))
            {
                throw new CommandException("Run category is invalid.");
            }
            if (category == RunCategory.Official && FindAcceptedOfficial(competitorId, _edition.EditionId) is not null)
            {
                throw new CommandException("This competitor already has an official result. Choose Playoff or Exhibition for another run.");
            }

            var run = CreateRun(new QueueItemRecord
            {
                Id = NewId("direct"),
                CompetitorId = competitorId,
                Category = category
            }, manualOfflineOverride: false);
            try
            {
                _store.SaveRunsAndQueue([run], _data.Queue, selectedCompetitorId: null, selectedRunCategory: null);
            }
            catch
            {
                ReloadInMemoryAfterPersistenceFailure();
                throw;
            }
            _data.Runs.Add(run);
            _data.SelectedCompetitorId = null;
            _data.SelectedRunCategory = null;
            _current = run;
            _lastDisplayedRun = run;
            UpdateDeviceLeds();
            return Clone(run);
        }
    }

    public InputResult PressEvent(string runId, string eventId)
    {
        lock (_gate)
        {
            RefreshActiveClock();
            var run = _current?.Id == runId
                ? _current
                : _lastDisplayedRun?.Id == runId && _lastDisplayedRun.Status == RunStatus.TimedOut
                    ? _lastDisplayedRun
                    : throw new CommandException("That run is no longer accepting virtual event presses.");
            var eventResult = run.Events.SingleOrDefault(e => e.EventId == eventId)
                ?? throw new CommandException("Event was not found in this run.");
            using var payload = JsonDocument.Parse("{}");
            return Receive(new InputEnvelope
            {
                MessageId = NewId("virtual-press"),
                SessionId = run.Id,
                RunId = run.Id,
                DeviceId = eventResult.DeviceId,
                Type = "event-press",
                ElapsedMilliseconds = run.ActiveElapsedMs,
                Payload = payload.RootElement.Clone()
            });
        }
    }

    public void RemoveFromQueue(string queueId)
    {
        lock (_gate)
        {
            var item = _data.Queue.SingleOrDefault(q => q.Id == queueId);
            if (item is null)
            {
                return;
            }
            _data.Queue.Remove(item);
            NormalizeQueue();
            try
            {
                _store.SaveQueue(_data.Queue);
            }
            catch
            {
                ReloadInMemoryAfterPersistenceFailure();
                throw;
            }
        }
    }

    public void ReorderQueue(IReadOnlyList<string> queueIds)
    {
        lock (_gate)
        {
            if (queueIds.Count != _data.Queue.Count || queueIds.Distinct(StringComparer.Ordinal).Count() != queueIds.Count ||
                queueIds.Any(id => _data.Queue.All(q => q.Id != id)))
            {
                throw new CommandException("Queue reorder must contain every queue item exactly once.");
            }

            for (var index = 0; index < queueIds.Count; index++)
            {
                _data.Queue.Single(q => q.Id == queueIds[index]).Position = index;
            }

            try
            {
                _store.SaveQueue(_data.Queue);
            }
            catch
            {
                ReloadInMemoryAfterPersistenceFailure();
                throw;
            }
        }
    }

    public IReadOnlyList<PreflightResult> Preflight()
    {
        lock (_gate)
        {
            return _edition.Events.Select(eventDefinition =>
            {
                var device = _data.Devices.Single(d => d.DeviceId == eventDefinition.DeviceId);
                return new PreflightResult
                {
                    DeviceId = device.DeviceId,
                    EventId = eventDefinition.EventId,
                    Availability = device.Availability,
                    LastSeenAt = device.LastSeenAt,
                    Passed = device.Availability == DeviceAvailability.Online,
                    ManualOverrideAvailable = device.Availability != DeviceAvailability.Online,
                    Message = device.Availability == DeviceAvailability.Online ? "Ready" : "Station is unavailable; explicit manual scoring override required."
                };
            }).ToList();
        }
    }

    public void SetDeviceAvailability(string deviceId, DeviceAvailability availability, string? error = null)
    {
        lock (_gate)
        {
            var device = _data.Devices.SingleOrDefault(d => d.DeviceId == deviceId)
                ?? throw new CommandException($"Unknown device '{deviceId}'.");
            device.Availability = availability;
            device.LastError = availability == DeviceAvailability.Online ? null : error ?? "Marked unavailable by operator.";
            device.Led = availability == DeviceAvailability.Online ? LedState.Ready : LedState.OfflineError;
            try
            {
                _store.SetDevice(device);
            }
            catch
            {
                ReloadInMemoryAfterPersistenceFailure();
                throw;
            }
        }
    }

    public RunRecord Arm(string queueId, bool manualOfflineOverride = false)
    {
        lock (_gate)
        {
            RefreshActiveClock();
            if (_current is not null && _current.Status is RunStatus.Armed or RunStatus.Countdown or RunStatus.Active or RunStatus.Paused or RunStatus.Finished)
            {
                throw new CommandException("Finish, pause, or abort the current run before arming another.");
            }

            var queueItem = _data.Queue.SingleOrDefault(q => q.Id == queueId)
                ?? throw new CommandException("Queue item was not found.");
            var preflightFailures = Preflight().Where(p => !p.Passed).ToList();
            if (preflightFailures.Count > 0 && !manualOfflineOverride)
            {
                throw new CommandException("Preflight failed. Explicitly enable manual scoring override to arm with unavailable stations.");
            }

            var existingOfficial = queueItem.Category == RunCategory.Official
                ? FindAcceptedOfficial(queueItem.CompetitorId, _edition.EditionId)
                : null;
            if (existingOfficial is not null && !queueItem.ReplaceExistingOfficial)
            {
                throw new CommandException("This competitor already has an accepted official run. Mark the queue entry as an explicit replacement.");
            }

            var run = CreateRun(queueItem, manualOfflineOverride && preflightFailures.Count > 0);
            var replacementSource = queueItem.ReplacementOfRunId is null
                ? existingOfficial
                : _data.Runs.SingleOrDefault(r => r.Id == queueItem.ReplacementOfRunId)
                    ?? throw new CommandException("The run selected for restart no longer exists.");
            if (replacementSource is not null)
            {
                replacementSource.Status = RunStatus.Superseded;
                replacementSource.SupersededByRunId = run.Id;
                replacementSource.Revision++;
                run.SupersedesRunId = replacementSource.Id;
            }

            _data.Queue.Remove(queueItem);
            NormalizeQueue();
            var runs = replacementSource is null ? new[] { run } : new[] { replacementSource, run };
            try
            {
                _store.SaveRunsAndQueue(runs, _data.Queue, selectedCompetitorId: null, selectedRunCategory: null);
            }
            catch
            {
                ReloadInMemoryAfterPersistenceFailure();
                throw;
            }
            _data.Runs.Add(run);
            _data.SelectedCompetitorId = null;
            _data.SelectedRunCategory = null;
            _current = run;
            _lastDisplayedRun = run;
            UpdateDeviceLeds();
            return Clone(run);
        }
    }

    public RunRecord StartMaster()
    {
        lock (_gate)
        {
            var run = _current ?? throw new CommandException("Arm a queued competitor before starting the master.");
            var result = Receive(new InputEnvelope
            {
                MessageId = NewId("message"),
                SessionId = run.Id,
                RunId = run.Id,
                DeviceId = "master",
                Type = "master-start",
                ElapsedMilliseconds = 0,
                Payload = JsonDocument.Parse("{}").RootElement
            });
            if (result.Disposition != MessageDisposition.Accepted || result.Run is null)
            {
                throw new CommandException(result.Reason);
            }
            return result.Run;
        }
    }

    public RunRecord CompleteCountdown(string runId)
    {
        lock (_gate)
        {
            var run = _current;
            if (run is null || !string.Equals(run.Id, runId, StringComparison.Ordinal))
            {
                throw new CommandException("Countdown completion does not match the current run.");
            }

            if (run.Status == RunStatus.Active)
            {
                RefreshActiveClock();
                return Clone(run);
            }

            if (run.Status != RunStatus.Countdown)
            {
                throw new CommandException("Only the current countdown can be completed.");
            }

            run.Status = RunStatus.Active;
            run.StartedAt = _clock.UtcNow;
            run.Revision++;
            _clockAnchorMilliseconds = _clock.MonotonicMilliseconds;
            UpdateDeviceLeds();
            try
            {
                _store.SaveRuns([run]);
            }
            catch
            {
                ReloadInMemoryAfterPersistenceFailure();
                throw;
            }
            return Clone(run);
        }
    }

    public InputResult ReceivePhysicalMasterStart(string bootToken, ulong sequence, bool startAllowed)
    {
        lock (_gate)
        {
            if (!MasterProtocolCodec.IsValidBootToken(bootToken))
            {
                throw new CommandException("Master boot token is invalid.");
            }

            var messageId = MasterProtocolCodec.GetStartMessageId(bootToken, sequence);
            var run = _current;
            var envelope = new InputEnvelope
            {
                MessageId = messageId,
                SessionId = run?.Id ?? "no-active-run",
                RunId = run?.Id ?? "no-active-run",
                DeviceId = "master",
                Type = "master-start",
                ElapsedMilliseconds = 0,
                Payload = JsonSerializer.SerializeToElement(new { bootToken, sequence }, JsonDefaults.Options)
            };
            var payloadJson = envelope.Payload.GetRawText();

            if (_data.Messages.Any(message => string.Equals(message.MessageId, messageId, StringComparison.Ordinal)))
            {
                return RecordRejected(envelope, MessageDisposition.Duplicate,
                    "Master start sequence was already recorded; retransmission was ignored.", payloadJson);
            }

            var highestSequence = _data.Messages
                .Select(message => MasterProtocolCodec.TryParseStartMessageId(message.MessageId, out var priorBoot, out var priorSequence) &&
                    string.Equals(priorBoot, bootToken, StringComparison.Ordinal) ? priorSequence : (ulong?)null)
                .Max();
            if (highestSequence is ulong previous && sequence <= previous)
            {
                return RecordRejected(envelope, MessageDisposition.StaleSequence,
                    "Master start sequence is older than a sequence already recorded for this boot.", payloadJson);
            }

            if (run?.Status != RunStatus.Armed)
            {
                return RecordRejected(envelope, MessageDisposition.InvalidSignal,
                    "Physical master can start a run only while a competitor is armed.", payloadJson);
            }

            if (!startAllowed)
            {
                return RecordRejected(envelope, MessageDisposition.InvalidSignal,
                    "Physical master start is disabled while the controller is in SPEED mode.", payloadJson);
            }

            return Receive(envelope);
        }
    }

    public RunRecord Pause()
    {
        lock (_gate)
        {
            var run = RequireCurrent();
            RefreshActiveClock();
            if (run.Status != RunStatus.Active)
            {
                throw new CommandException("Only a running session can be paused.");
            }

            run.PausedFromPhase = run.Phase.ToString();
            run.Status = RunStatus.Paused;
            run.Revision++;
            _clockAnchorMilliseconds = _clock.MonotonicMilliseconds;
            UpdateDeviceLeds();
            try
            {
                _store.SaveRuns([run]);
            }
            catch
            {
                ReloadInMemoryAfterPersistenceFailure();
                throw;
            }
            return Clone(run);
        }
    }

    public RunRecord Resume()
    {
        lock (_gate)
        {
            var run = RequireCurrent();
            if (run.Status != RunStatus.Paused)
            {
                throw new CommandException("Only a paused session can be resumed.");
            }

            if (run.ActiveElapsedMs >= run.Edition.DurationLimitSeconds * 1000L)
            {
                TimeoutCurrent();
                return Clone(run);
            }

            if (Enum.TryParse<RunPhase>(run.PausedFromPhase, out var pausedPhase))
            {
                run.Phase = pausedPhase;
            }
            run.PausedFromPhase = null;
            run.Status = RunStatus.Active;
            run.Revision++;
            _clockAnchorMilliseconds = _clock.MonotonicMilliseconds;
            UpdateDeviceLeds();
            try
            {
                _store.SaveRuns([run]);
            }
            catch
            {
                ReloadInMemoryAfterPersistenceFailure();
                throw;
            }
            return Clone(run);
        }
    }

    public RunRecord Finish()
    {
        lock (_gate)
        {
            RefreshActiveClock();
            if (_current is null && _lastDisplayedRun is { Status: RunStatus.Completed or RunStatus.TimedOut or RunStatus.Finished })
            {
                return Clone(_lastDisplayedRun);
            }
            var run = RequireCurrent();
            if (run.Status == RunStatus.Finished)
            {
                return Clone(run);
            }
            if (run.Status is not RunStatus.Active and not RunStatus.Paused and not RunStatus.Armed)
            {
                throw new CommandException("The current session cannot be finished in its current state.");
            }

            RecomputeScores(run);
            MarkFinishedUnrecorded(run);
            run.FinishedAt = _clock.UtcNow;
            run.Revision++;
            _lastDisplayedRun = Clone(run);
            try
            {
                _store.SaveRuns([run]);
            }
            catch
            {
                ReloadInMemoryAfterPersistenceFailure();
                throw;
            }
            UpdateDeviceLeds();
            return Clone(run);
        }
    }

    public RunRecord Record()
    {
        lock (_gate)
        {
            RefreshActiveClock();
            if (_current is null)
            {
                if (_lastDisplayedRun is { Status: RunStatus.TimedOut } timedOut)
                {
                    return RecordRunAndPromoteQueue(timedOut, complete: false);
                }
                if (_lastDisplayedRun is { Status: RunStatus.Completed } completed)
                {
                    return Clone(completed);
                }
                throw new CommandException("There is no run ready to record.");
            }

            if (_current.Status == RunStatus.Countdown)
            {
                throw new CommandException("A countdown must finish before the run can be recorded.");
            }

            if (_current.Status is RunStatus.Armed or RunStatus.Active or RunStatus.Paused)
            {
                Finish();
            }
            var run = RequireCurrent();
            if (run.Status != RunStatus.Finished)
            {
                throw new CommandException("Finish the run before recording it.");
            }

            return RecordRunAndPromoteQueue(run, complete: true);
        }
    }

    public RunRecord RecordHistoricalRun(string runId)
    {
        lock (_gate)
        {
            if (_current?.Id == runId)
            {
                return Record();
            }

            var run = _data.Runs.SingleOrDefault(r => r.Id == runId)
                ?? throw new CommandException("Run was not found.");
            if (run.IsRecorded)
            {
                return Clone(run);
            }
            if (_lastDisplayedRun?.Id == run.Id && run.Status == RunStatus.TimedOut)
            {
                return RecordRunAndPromoteQueue(run, complete: false);
            }
            if (run.Status is RunStatus.Armed or RunStatus.Countdown or RunStatus.Active or RunStatus.Paused or RunStatus.Finished)
            {
                throw new CommandException("Record the current run from the scorekeeping tab.");
            }

            return RecordRunAndPromoteQueue(run, complete: false);
        }
    }

    private RunRecord RecordRunAndPromoteQueue(RunRecord run, bool complete)
    {
        if (run.IsRecorded)
        {
            return Clone(run);
        }

        if (complete)
        {
            run.Status = RunStatus.Completed;
        }
        run.RecordedAt = _clock.UtcNow;
        run.FinishedAt ??= _clock.UtcNow;
        run.Revision++;

        var promoted = _data.Queue.OrderBy(item => item.Position).FirstOrDefault();
        if (promoted is not null)
        {
            _data.Queue.Remove(promoted);
            NormalizeQueue();
        }

        try
        {
            _store.SaveRunsAndQueue([run], _data.Queue, promoted?.CompetitorId, promoted?.Category);
        }
        catch
        {
            ReloadInMemoryAfterPersistenceFailure();
            throw;
        }

        _data.SelectedCompetitorId = promoted?.CompetitorId;
        _data.SelectedRunCategory = promoted?.Category;
        if (_lastDisplayedRun?.Id == run.Id || _lastDisplayedRun is null)
        {
            _lastDisplayedRun = Clone(run);
        }
        if (_current?.Id == run.Id)
        {
            _current = null;
        }
        UpdateDeviceLeds();
        return Clone(run);
    }

    public RunRecord Abort(string? reason = null)
    {
        lock (_gate)
        {
            var run = RequireCurrent();
            RefreshActiveClock();
            var actionReason = string.IsNullOrWhiteSpace(reason) ? "Operator aborted run." : reason.Trim();
            run.Status = RunStatus.Aborted;
            run.Notes = actionReason;
            run.FinishedAt = _clock.UtcNow;
            RecomputeScores(run);
            run.Revision++;
            _lastDisplayedRun = Clone(run);
            UpdateDeviceLeds();
            try
            {
                var message = CreateMessage(NewId("abort"), run.Id, run.Id, "operator", "operator-abort", run.ActiveElapsedMs,
                    MessageDisposition.Accepted, actionReason, JsonSerializer.Serialize(new { reason = actionReason }, JsonDefaults.Options));
                _store.SaveRunAndMessage(run, message);
                _data.Messages.Add(message);
            }
            catch
            {
                ReloadInMemoryAfterPersistenceFailure();
                throw;
            }
            _current = null;
            return Clone(run);
        }
    }

    public QueueItemRecord Restart(string runId, string? reason = null)
    {
        lock (_gate)
        {
            var run = _data.Runs.SingleOrDefault(r => r.Id == runId)
                ?? throw new CommandException("Run was not found.");
            var actionReason = string.IsNullOrWhiteSpace(reason) ? "Operator requested a replacement attempt." : reason.Trim();
            if (_current?.Id == run.Id)
            {
                Abort(actionReason);
            }

            var queueItem = new QueueItemRecord
            {
                Id = NewId("queue"),
                CompetitorId = run.CompetitorId,
                Category = run.Category,
                ReplaceExistingOfficial = run.Category == RunCategory.Official,
                ReplacementOfRunId = run.Id,
                Reason = actionReason,
                Position = 0
            };
            _data.Queue.Insert(0, queueItem);
            NormalizeQueue();
            try
            {
                _store.SaveQueue(_data.Queue);
                if (_current?.Id != run.Id)
                {
                    var message = CreateMessage(NewId("restart"), run.Id, run.Id, "operator", "operator-restart", run.ActiveElapsedMs,
                        MessageDisposition.Accepted, actionReason, JsonSerializer.Serialize(new { reason = actionReason }, JsonDefaults.Options));
                    _store.SaveRuns([], message);
                    _data.Messages.Add(message);
                }
            }
            catch
            {
                ReloadInMemoryAfterPersistenceFailure();
                throw;
            }
            return Clone(queueItem);
        }
    }

    public InputResult Receive(InputEnvelope envelope)
    {
        lock (_gate)
        {
            var payloadJson = envelope.Payload.ValueKind is JsonValueKind.Undefined ? null : envelope.Payload.GetRawText();
            if (string.IsNullOrWhiteSpace(envelope.MessageId) || string.IsNullOrWhiteSpace(envelope.DeviceId) ||
                string.IsNullOrWhiteSpace(envelope.Type) || string.IsNullOrWhiteSpace(envelope.RunId) ||
                string.IsNullOrWhiteSpace(envelope.SessionId))
            {
                return RecordRejected(envelope, MessageDisposition.InvalidEnvelope, "Envelope is missing a required field or reuses a message id.", payloadJson);
            }

            if (_data.Messages.Any(m => m.MessageId == envelope.MessageId))
            {
                return RecordRejected(envelope, MessageDisposition.Duplicate, "Message id was already recorded; retransmission was ignored.", payloadJson);
            }

            var run = _current;
            if (run is null || !string.Equals(envelope.RunId, run.Id, StringComparison.Ordinal) ||
                !string.Equals(envelope.SessionId, run.Id, StringComparison.Ordinal))
            {
                if (run is null && _lastDisplayedRun is { Status: RunStatus.TimedOut } timedOut &&
                    string.Equals(envelope.RunId, timedOut.Id, StringComparison.Ordinal) &&
                    string.Equals(envelope.SessionId, timedOut.Id, StringComparison.Ordinal))
                {
                    return RecordRejected(envelope, MessageDisposition.TimedOut, "The active-time deadline has elapsed.", payloadJson);
                }
                return RecordRejected(envelope, MessageDisposition.WrongRun, "Message does not belong to the armed session.", payloadJson);
            }

            RefreshActiveClock();
            if (run.Status == RunStatus.Paused)
            {
                return RecordRejected(envelope, MessageDisposition.Paused, "Gameplay input is ignored while paused.", payloadJson);
            }

            if (run.Status == RunStatus.TimedOut)
            {
                return RecordRejected(envelope, MessageDisposition.TimedOut, "The active-time deadline has elapsed.", payloadJson);
            }

            if (string.Equals(envelope.Type, "master-start", StringComparison.Ordinal))
            {
                if (run.Status != RunStatus.Armed || envelope.DeviceId != "master" || envelope.ElapsedMilliseconds != 0)
                {
                    return RecordRejected(envelope, MessageDisposition.InvalidSignal, "Only the virtual/physical master can start an armed run.", payloadJson);
                }

                run.Status = RunStatus.Countdown;
                run.Revision++;
                UpdateDeviceLeds();
                return RecordAccepted(envelope, run, "Master started run countdown.", payloadJson);
            }

            if (run.Status != RunStatus.Active)
            {
                return RecordRejected(envelope, MessageDisposition.InvalidSignal, "The run is not accepting gameplay input.", payloadJson);
            }

            if (envelope.ElapsedMilliseconds < 0 || envelope.ElapsedMilliseconds > run.ActiveElapsedMs + 500)
            {
                return RecordRejected(envelope, MessageDisposition.StaleTimestamp, "Message timestamp is outside the current active-time window.", payloadJson);
            }

            if (envelope.Type == "bonus-signal")
            {
                if (run.Phase != RunPhase.Bonus)
                {
                    return RecordRejected(envelope, MessageDisposition.BonusNotReady, "Bonus signals are accepted only after every required event is complete.", payloadJson);
                }
                if (envelope.DeviceId != "master" && _data.Devices.All(d => d.DeviceId != envelope.DeviceId))
                {
                    return RecordRejected(envelope, MessageDisposition.UnknownStation, "Bonus signal came from an unknown station.", payloadJson);
                }
                if (envelope.DeviceId != "master" && _data.Devices.Single(d => d.DeviceId == envelope.DeviceId).Availability != DeviceAvailability.Online)
                {
                    return RecordRejected(envelope, MessageDisposition.Offline, "Bonus signal station is offline; record bonus results manually.", payloadJson);
                }

                run.BonusResultJson = payloadJson ?? "{}";
                run.Revision++;
                UpdateDeviceLeds();
                return RecordAccepted(envelope, run, "Bonus signal recorded; scoring remains deferred.", payloadJson);
            }

            var device = _data.Devices.SingleOrDefault(d => d.DeviceId == envelope.DeviceId);
            var eventResult = run.Events.SingleOrDefault(e => e.DeviceId == envelope.DeviceId);
            if (device is null || eventResult is null)
            {
                return RecordRejected(envelope, MessageDisposition.UnknownStation, "Device is not assigned in this run roster.", payloadJson);
            }

            if (device.Availability != DeviceAvailability.Online)
            {
                return RecordRejected(envelope, MessageDisposition.Offline, "Station is offline; manual mode requires operator edits and does not accept simulated station packets.", payloadJson);
            }

            if (eventResult.LastSignalElapsedMs is long lastSignal && envelope.ElapsedMilliseconds < lastSignal)
            {
                return RecordRejected(envelope, MessageDisposition.StaleTimestamp, "Message arrived older than the last signal for this event.", payloadJson);
            }

            var disposition = ApplyGameplaySignal(run, eventResult, envelope, out var reason);
            if (disposition != MessageDisposition.Accepted)
            {
                return RecordRejected(envelope, disposition, reason, payloadJson);
            }

            eventResult.LastSignalElapsedMs = envelope.ElapsedMilliseconds;
            device.LastSeenAt = _clock.UtcNow;
            device.LastError = null;
            run.LastAcceptedInputElapsedMs = Math.Max(run.LastAcceptedInputElapsedMs, envelope.ElapsedMilliseconds);
            if (run.AllEventsCompleted && run.Events.All(e => e.Type == EventKind.Standard))
            {
                MarkFinishedUnrecorded(run);
                reason = "All events completed; run finished and is awaiting recording.";
            }
            else if (run.AllEventsCompleted && run.Phase != RunPhase.Bonus)
            {
                run.Phase = RunPhase.Bonus;
                run.BonusStartedElapsedMs = run.ActiveElapsedMs;
            }

            run.Revision++;
            UpdateDeviceLeds();
            return RecordAccepted(envelope, run, reason, payloadJson);
        }
    }

    public RunRecord EditHistoricalRun(string runId, EditRunRequest request)
    {
        lock (_gate)
        {
            var run = FindHistoricalRun(runId);
            if (run.Revision != request.ExpectedRevision)
            {
                throw new CommandException("This history row changed after it was opened. Reload it before saving.");
            }

            var candidate = Clone(run);
            ApplyEdit(candidate, request);
            if (IsStoppedHistoricalStatus(run.Status))
            {
                ExtendStoppedCorrectionTimeline(candidate, request);
            }
            ValidateHistoricalCandidate(candidate);
            HandleOfficialConflict(run, candidate, request.ReplaceExistingOfficial, out var replaced);

            var before = Serialize(run);
            candidate.Revision = run.Revision + 1;
            var after = Serialize(candidate);
            var edit = new EditRecord
            {
                RunId = run.Id,
                CreatedAt = _clock.UtcNow,
                Reason = RequireReason(request.Reason),
                BeforeJson = before,
                AfterJson = after
            };
            ReplaceRun(run, candidate);
            try
            {
                _store.AddEdit(candidate, edit, replaced is not null && replaced.Id != candidate.Id ? [replaced] : null);
            }
            catch
            {
                ReloadInMemoryAfterPersistenceFailure();
                throw;
            }
            _data.Edits.Add(edit);
            return Clone(candidate);
        }
    }

    public RunRecord EditCurrentRun(EditRunRequest request)
    {
        lock (_gate)
        {
            var run = RequireCurrent();
            RefreshActiveClock();
            if (run.Status == RunStatus.Countdown)
            {
                throw new CommandException("A live run cannot be edited during its countdown.");
            }
            if (run.Revision != request.ExpectedRevision)
            {
                throw new CommandException("This live run changed after it was opened. Reload it before saving.");
            }

            var candidate = Clone(run);
            ApplyEdit(candidate, request);
            if (run.Status == RunStatus.Finished)
            {
                ExtendStoppedCorrectionTimeline(candidate, request);
            }
            ValidateLiveCandidate(candidate);
            HandleOfficialConflict(run, candidate, request.ReplaceExistingOfficial, out var replaced);

            var before = Serialize(run);
            candidate.Revision = run.Revision + 1;
            var after = Serialize(candidate);
            var edit = new EditRecord
            {
                RunId = run.Id,
                CreatedAt = _clock.UtcNow,
                Reason = RequireReason(request.Reason),
                BeforeJson = before,
                AfterJson = after
            };

            ReplaceRun(run, candidate);
            try
            {
                _store.AddEdit(candidate, edit, replaced is not null && replaced.Id != candidate.Id ? [replaced] : null);
            }
            catch
            {
                ReloadInMemoryAfterPersistenceFailure();
                throw;
            }

            _data.Edits.Add(edit);
            return Clone(candidate);
        }
    }

    public RunRecord UndoCurrentEdit(long editId, int expectedRevision, string reason)
    {
        lock (_gate)
        {
            var run = RequireCurrent();
            RefreshActiveClock();
            if (run.Status == RunStatus.Countdown)
            {
                throw new CommandException("A live run edit cannot be undone during its countdown.");
            }
            if (run.Revision != expectedRevision)
            {
                throw new CommandException("This live run changed after the edit was opened. Reload it before undoing.");
            }

            var edit = _data.Edits.Where(e => e.RunId == run.Id).OrderByDescending(e => e.Id).FirstOrDefault();
            if (edit is null || edit.Id != editId)
            {
                throw new CommandException("Only the latest live edit can be undone.");
            }

            var after = DeserializeRun(edit.AfterJson);
            if (!MatchesLiveUndoSnapshot(run, after))
            {
                throw new CommandException("This live edit cannot be undone because a device or result changed afterward.");
            }

            var before = DeserializeRun(edit.BeforeJson);
            var candidate = Clone(run);
            ApplyInverseFields(candidate, before, after);
            candidate.ActiveElapsedMs = run.Status == RunStatus.Finished
                ? before.ActiveElapsedMs
                : run.ActiveElapsedMs;
            ValidateLiveCandidate(candidate);
            candidate.LastAcceptedInputElapsedMs = run.LastAcceptedInputElapsedMs;
            candidate.SupersedesRunId = run.SupersedesRunId;
            candidate.SupersededByRunId = run.SupersededByRunId;
            candidate.Revision = run.Revision + 1;

            var undo = new EditRecord
            {
                RunId = run.Id,
                CreatedAt = _clock.UtcNow,
                Reason = RequireReason(reason),
                BeforeJson = Serialize(run),
                AfterJson = Serialize(candidate),
                UndoneEditId = edit.Id
            };
            ReplaceRun(run, candidate);
            try
            {
                _store.AddEdit(candidate, undo);
            }
            catch
            {
                ReloadInMemoryAfterPersistenceFailure();
                throw;
            }
            _data.Edits.Add(undo);
            return Clone(candidate);
        }
    }

    public RunRecord UndoHistoricalEdit(string runId, long editId, int expectedRevision, string reason)
    {
        lock (_gate)
        {
            var run = FindHistoricalRun(runId);
            if (run.Revision != expectedRevision)
            {
                throw new CommandException("This history row changed after it was opened. Reload it before undoing.");
            }

            var edit = _data.Edits.SingleOrDefault(e => e.Id == editId && e.RunId == runId)
                ?? throw new CommandException("Edit record was not found for this run.");
            if (!string.Equals(Serialize(run), edit.AfterJson, StringComparison.Ordinal))
            {
                throw new CommandException("This edit cannot be undone because the targeted run changed afterward. Create a new correction instead.");
            }
            var candidate = DeserializeRun(edit.BeforeJson);
            if (candidate.Id != run.Id || candidate.Events.Select(e => e.EventId).OrderBy(x => x).SequenceEqual(run.Events.Select(e => e.EventId).OrderBy(x => x)) is false)
            {
                throw new InvalidDataException("Edit history contains a mismatched stable identity.");
            }

            ValidateHistoricalCandidate(candidate);
            HandleOfficialConflict(run, candidate, replaceExisting: false, out var replaced);
            candidate.SupersedesRunId = run.SupersedesRunId;
            candidate.SupersededByRunId = run.SupersededByRunId;
            var before = Serialize(run);
            candidate.Revision = run.Revision + 1;
            var after = Serialize(candidate);
            var undo = new EditRecord
            {
                RunId = run.Id,
                CreatedAt = _clock.UtcNow,
                Reason = RequireReason(reason),
                BeforeJson = before,
                AfterJson = after,
                UndoneEditId = edit.Id
            };
            ReplaceRun(run, candidate);
            try
            {
                _store.AddEdit(candidate, undo, replaced is not null && replaced.Id != candidate.Id ? [replaced] : null);
            }
            catch
            {
                ReloadInMemoryAfterPersistenceFailure();
                throw;
            }
            _data.Edits.Add(undo);
            return Clone(candidate);
        }
    }

    private MessageDisposition ApplyGameplaySignal(RunRecord run, EventRecord eventResult, InputEnvelope envelope, out string reason)
    {
        reason = "Signal accepted.";
        if (eventResult.Type == EventKind.Standard)
        {
            if (envelope.Type != "event-press")
            {
                reason = "Standard events accept only their assigned event press.";
                return MessageDisposition.InvalidSignal;
            }

            if (eventResult.Status == EventStatus.Pending)
            {
                eventResult.Status = EventStatus.Active;
                eventResult.StartElapsedMs = envelope.ElapsedMilliseconds;
                reason = "Event timer started.";
                return MessageDisposition.Accepted;
            }

            if (eventResult.Status == EventStatus.Active)
            {
                if (eventResult.StartElapsedMs is not long start || envelope.ElapsedMilliseconds < start)
                {
                    reason = "Completion timestamp precedes event start.";
                    return MessageDisposition.StaleTimestamp;
                }

                eventResult.FinishElapsedMs = envelope.ElapsedMilliseconds;
                eventResult.Status = EventStatus.Completed;
                eventResult.Score = ScoreCalculator.Calculate(eventResult, run.Edition.Scoring);
                reason = "Event completed.";
                return MessageDisposition.Accepted;
            }

            reason = "Completed events ignore later presses.";
            return MessageDisposition.AlreadyCompleted;
        }

        if (eventResult.Type == EventKind.Keypad)
        {
            if (envelope.Type == "event-press" && eventResult.Status == EventStatus.Pending)
            {
                eventResult.Status = EventStatus.Active;
                eventResult.StartElapsedMs = envelope.ElapsedMilliseconds;
                reason = "Keypad prompt started; a valid response is required.";
                return MessageDisposition.Accepted;
            }

            if (envelope.Type == "keypad-incorrect" && eventResult.Status == EventStatus.Active)
            {
                reason = "Incorrect keypad response recorded; event remains active.";
                return MessageDisposition.Accepted;
            }

            if (envelope.Type == "keypad-response" && eventResult.Status == EventStatus.Active)
            {
                var answer = envelope.Payload.ValueKind == JsonValueKind.Object && envelope.Payload.TryGetProperty("answer", out var answerProperty)
                    ? answerProperty.GetString()
                    : null;
                var expected = run.Edition.Events.Single(e => e.EventId == eventResult.EventId).Answer;
                if (!string.Equals(answer?.Trim(), expected?.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    reason = "Incorrect keypad response recorded; event remains active.";
                    return MessageDisposition.Accepted;
                }

                if (eventResult.StartElapsedMs is not long responseStart || envelope.ElapsedMilliseconds < responseStart)
                {
                    reason = "Keypad completion timestamp precedes its prompt start.";
                    return MessageDisposition.StaleTimestamp;
                }

                eventResult.FinishElapsedMs = envelope.ElapsedMilliseconds;
                eventResult.Status = EventStatus.Completed;
                eventResult.Score = ScoreCalculator.Calculate(eventResult, run.Edition.Scoring);
                reason = "Correct keypad response completed the event.";
                return MessageDisposition.Accepted;
            }

            if (envelope.Type == "keypad-success" && eventResult.Status == EventStatus.Active)
            {
                if (eventResult.StartElapsedMs is not long start || envelope.ElapsedMilliseconds < start)
                {
                    reason = "Keypad completion timestamp precedes its prompt start.";
                    return MessageDisposition.StaleTimestamp;
                }

                eventResult.FinishElapsedMs = envelope.ElapsedMilliseconds;
                eventResult.Status = EventStatus.Completed;
                eventResult.Score = ScoreCalculator.Calculate(eventResult, run.Edition.Scoring);
                reason = "Keypad success completed the event.";
                return MessageDisposition.Accepted;
            }

            reason = envelope.Type == "event-press"
                ? "A second keypad press cannot bypass response validation."
                : "Keypad response is invalid before its prompt starts or after completion.";
            return eventResult.Status == EventStatus.Completed ? MessageDisposition.AlreadyCompleted : MessageDisposition.InvalidSignal;
        }

        if (envelope.Type == "arcade-start" && eventResult.Status == EventStatus.Pending)
        {
            eventResult.Status = EventStatus.Active;
            eventResult.StartElapsedMs = envelope.ElapsedMilliseconds;
            reason = "All-emeralds signal started the arcade event.";
            return MessageDisposition.Accepted;
        }

        if (envelope.Type == "arcade-finish" && eventResult.Status == EventStatus.Active)
        {
            if (eventResult.StartElapsedMs is not long start || envelope.ElapsedMilliseconds < start)
            {
                reason = "Arcade completion timestamp precedes its start.";
                return MessageDisposition.StaleTimestamp;
            }

            eventResult.FinishElapsedMs = envelope.ElapsedMilliseconds;
            eventResult.Status = EventStatus.Completed;
            eventResult.Score = ScoreCalculator.Calculate(eventResult, run.Edition.Scoring);
            reason = "Arcade completion completed the event.";
            return MessageDisposition.Accepted;
        }

        if (eventResult.Status == EventStatus.Completed)
        {
            reason = "Completed arcade events ignore repeated sensor signals.";
            return MessageDisposition.AlreadyCompleted;
        }

        reason = "Arcade completion is rejected until all-emeralds start is received.";
        return MessageDisposition.InvalidSignal;
    }

    private InputResult RecordAccepted(InputEnvelope envelope, RunRecord run, string reason, string? payloadJson)
    {
        var message = CreateMessage(envelope.MessageId, run.Id, envelope.SessionId, envelope.DeviceId, envelope.Type,
            envelope.ElapsedMilliseconds, MessageDisposition.Accepted, reason, payloadJson);
        var consumesOnDeck = string.Equals(envelope.Type, "master-start", StringComparison.Ordinal);
        var remainingQueue = consumesOnDeck
            ? _data.Queue.OrderBy(item => item.Position).Select(Clone).ToList()
            : null;
        if (remainingQueue is not null)
        {
            var matchingItem = remainingQueue.FirstOrDefault(item =>
                string.Equals(item.CompetitorId, run.CompetitorId, StringComparison.Ordinal) && item.Category == run.Category);
            if (matchingItem is not null)
            {
                remainingQueue.Remove(matchingItem);
                for (var index = 0; index < remainingQueue.Count; index++)
                {
                    remainingQueue[index].Position = index;
                }
            }
        }
        try
        {
            if (remainingQueue is null)
            {
                _store.SaveRunAndMessage(run, message);
            }
            else
            {
                _store.SaveRunsAndQueue([run], remainingQueue, selectedCompetitorId: null,
                    selectedRunCategory: null, message: message);
            }
        }
        catch
        {
            ReloadInMemoryAfterPersistenceFailure();
            throw;
        }
        if (remainingQueue is not null)
        {
            _data.Queue.Clear();
            _data.Queue.AddRange(remainingQueue);
        }
        _data.Messages.Add(message);
        return new InputResult(MessageDisposition.Accepted, reason, Clone(run));
    }

    private InputResult RecordRejected(InputEnvelope envelope, MessageDisposition disposition, string reason, string? payloadJson)
    {
        var message = CreateMessage(envelope.MessageId, envelope.RunId, envelope.SessionId, envelope.DeviceId, envelope.Type,
            envelope.ElapsedMilliseconds, disposition, reason, payloadJson);
        try
        {
            _store.SaveRuns([], message);
        }
        catch
        {
            ReloadInMemoryAfterPersistenceFailure();
            throw;
        }
        _data.Messages.Add(message);
        return new InputResult(disposition, reason, _current is null ? null : Clone(_current));
    }

    private void RefreshActiveClock()
    {
        if (_current is null || _current.Status != RunStatus.Active)
        {
            return;
        }

        var now = _clock.MonotonicMilliseconds;
        var delta = Math.Max(0, now - _clockAnchorMilliseconds);
        if (delta == 0)
        {
            return;
        }

        _clockAnchorMilliseconds = now;
        _current.ActiveElapsedMs += delta;
        if (_current.ActiveElapsedMs >= _current.Edition.DurationLimitSeconds * 1000L)
        {
            TimeoutCurrent();
        }
        else
        {
            try
            {
                _store.SaveRuns([_current]);
            }
            catch
            {
                ReloadInMemoryAfterPersistenceFailure();
                throw;
            }
        }
    }

    private void TimeoutCurrent()
    {
        if (_current is null)
        {
            return;
        }

        _current.ActiveElapsedMs = _current.Edition.DurationLimitSeconds * 1000L;
        _current.Status = RunStatus.TimedOut;
        _current.FinishedAt = _clock.UtcNow;
        RecomputeScores(_current);
        _current.Revision++;
        UpdateDeviceLeds();
        try
        {
            _store.SaveRuns([_current]);
        }
        catch
        {
            ReloadInMemoryAfterPersistenceFailure();
            throw;
        }
        _lastDisplayedRun = Clone(_current);
        _current = null;
    }

    private void ApplyEdit(RunRecord candidate, EditRunRequest request)
    {
        candidate.CompetitorId = request.CompetitorId ?? candidate.CompetitorId;
        RequireCompetitor(candidate.CompetitorId);
        if (request.Category is RunCategory category)
        {
            candidate.Category = category;
        }

        if (request.Status is RunStatus status)
        {
            candidate.Status = status;
        }

        if (request.DurationLimitSeconds is int durationLimit)
        {
            if (durationLimit <= 0)
            {
                throw new CommandException("Duration limit must be positive.");
            }
            candidate.Edition.DurationLimitSeconds = durationLimit;
        }

        if (request.ActiveElapsedMs is long activeElapsed)
        {
            candidate.ActiveElapsedMs = activeElapsed;
        }

        if (request.BonusResultJson is not null)
        {
            candidate.BonusResultJson = request.BonusResultJson;
        }
        if (request.BonusPointsOverride is int bonusPoints)
        {
            if (bonusPoints < 0)
            {
                throw new CommandException("Bonus points cannot be negative.");
            }
            candidate.BonusPointsOverride = bonusPoints;
        }
        if (request.ClearBonusPointsOverride)
        {
            candidate.BonusPointsOverride = null;
        }
        if (request.Notes is not null)
        {
            candidate.Notes = request.Notes;
        }

        foreach (var edit in request.Events)
        {
            var result = candidate.Events.SingleOrDefault(e => e.EventId == edit.EventId)
                ?? throw new CommandException($"Event '{edit.EventId}' is not in this run's roster.");
            var start = edit.ClearStartElapsedMs ? null : edit.StartElapsedMs ?? result.StartElapsedMs;
            var finish = edit.ClearFinishElapsedMs ? null : edit.FinishElapsedMs ?? result.FinishElapsedMs;
            if (edit.DurationMs is long duration)
            {
                if (duration < 0 || start is null || edit.ClearFinishElapsedMs)
                {
                    throw new CommandException($"Event '{edit.EventId}' duration requires a non-negative duration and a start time.");
                }
                finish = start.Value + duration;
            }
            result.StartElapsedMs = start;
            result.FinishElapsedMs = finish;
            if (edit.Status is EventStatus eventStatus)
            {
                result.Status = eventStatus;
            }
            else if (edit.StartElapsedMs is not null || edit.FinishElapsedMs is not null || edit.DurationMs is not null ||
                     edit.ClearStartElapsedMs || edit.ClearFinishElapsedMs)
            {
                result.Status = start is null ? EventStatus.Pending : finish is null ? EventStatus.Active : EventStatus.Completed;
            }
            if (edit.ScoreOverride is int scoreOverride)
            {
                if (scoreOverride < 0)
                {
                    throw new CommandException("Score override cannot be negative.");
                }
                result.ScoreOverride = scoreOverride;
            }
            if (edit.ClearScoreOverride)
            {
                result.ScoreOverride = null;
            }
            if (edit.MeasurementJson is not null)
            {
                result.MeasurementJson = edit.MeasurementJson;
            }
            if (edit.Notes is not null)
            {
                result.Notes = edit.Notes;
            }
            result.Score = ScoreCalculator.Calculate(result, candidate.Edition.Scoring);
        }
    }

    private void ValidateHistoricalCandidate(RunRecord candidate)
    {
        if (candidate.Status is RunStatus.Active or RunStatus.Armed or RunStatus.Paused)
        {
            throw new CommandException("Historical edits cannot create a live or armed run.");
        }
        if (candidate.ActiveElapsedMs < 0 || candidate.ActiveElapsedMs > candidate.Edition.DurationLimitSeconds * 1000L)
        {
            throw new CommandException("Run active elapsed time is outside its duration limit.");
        }
        foreach (var result in candidate.Events)
        {
            if (result.StartElapsedMs is long start && (start < 0 || start > candidate.ActiveElapsedMs))
            {
                throw new CommandException($"Event '{result.EventId}' start time is impossible for the run.");
            }
            if (result.FinishElapsedMs is long finish && (finish < 0 || finish > candidate.ActiveElapsedMs))
            {
                throw new CommandException($"Event '{result.EventId}' finish time is impossible for the run.");
            }
            if (result.StartElapsedMs is long eventStart && result.FinishElapsedMs is long eventFinish && eventFinish < eventStart)
            {
                throw new CommandException($"Event '{result.EventId}' finish precedes start.");
            }
            if (result.Status == EventStatus.Completed && (result.StartElapsedMs is null || result.FinishElapsedMs is null))
            {
                throw new CommandException($"Completed event '{result.EventId}' requires start and finish times.");
            }
            result.Score = ScoreCalculator.Calculate(result, candidate.Edition.Scoring);
        }
    }

    private static bool IsStoppedHistoricalStatus(RunStatus status) =>
        status is RunStatus.Finished or RunStatus.Completed or RunStatus.TimedOut or RunStatus.Aborted or RunStatus.Superseded;

    private static void ExtendStoppedCorrectionTimeline(RunRecord candidate, EditRunRequest request)
    {
        if (request.ActiveElapsedMs is not null)
        {
            return;
        }

        var timedEventIds = request.Events
            .Where(edit => edit.StartElapsedMs is not null || edit.ClearStartElapsedMs ||
                edit.FinishElapsedMs is not null || edit.ClearFinishElapsedMs || edit.DurationMs is not null)
            .Select(edit => edit.EventId)
            .ToHashSet(StringComparer.Ordinal);
        if (timedEventIds.Count == 0)
        {
            return;
        }

        var latestCorrectedElapsed = candidate.Events
            .Where(result => timedEventIds.Contains(result.EventId))
            .SelectMany(result => new long?[] { result.StartElapsedMs, result.FinishElapsedMs })
            .Where(timestamp => timestamp is not null)
            .Select(timestamp => timestamp!.Value)
            .DefaultIfEmpty(candidate.ActiveElapsedMs)
            .Max();
        if (latestCorrectedElapsed <= candidate.ActiveElapsedMs)
        {
            return;
        }

        var durationLimitMilliseconds = candidate.Edition.DurationLimitSeconds * 1000L;
        candidate.ActiveElapsedMs = Math.Min(latestCorrectedElapsed, durationLimitMilliseconds);
    }

    private void ValidateLiveCandidate(RunRecord candidate)
    {
        if (candidate.Status is not RunStatus.Armed and not RunStatus.Active and not RunStatus.Paused and not RunStatus.Finished)
        {
            throw new CommandException("A live correction may keep a run armed, active, paused, or finished; use the run controls to record or abort it.");
        }
        if (candidate.ActiveElapsedMs < 0 || candidate.ActiveElapsedMs > candidate.Edition.DurationLimitSeconds * 1000L)
        {
            throw new CommandException("Run active elapsed time is outside its duration limit.");
        }

        foreach (var result in candidate.Events)
        {
            if (result.StartElapsedMs is long start && (start < 0 || start > candidate.ActiveElapsedMs))
            {
                throw new CommandException($"Event '{result.EventId}' start time is impossible for the live run.");
            }
            if (result.FinishElapsedMs is long finish && (finish < 0 || finish > candidate.ActiveElapsedMs))
            {
                throw new CommandException($"Event '{result.EventId}' finish time is impossible for the live run.");
            }
            if (result.StartElapsedMs is long eventStart && result.FinishElapsedMs is long eventFinish && eventFinish < eventStart)
            {
                throw new CommandException($"Event '{result.EventId}' finish precedes start.");
            }
            if (result.Status == EventStatus.Completed && (result.StartElapsedMs is null || result.FinishElapsedMs is null))
            {
                throw new CommandException($"Completed event '{result.EventId}' requires start and finish times.");
            }
            result.Score = ScoreCalculator.Calculate(result, candidate.Edition.Scoring);
        }

        if (candidate.AllEventsCompleted && candidate.Events.All(e => e.Type == EventKind.Standard))
        {
            MarkFinishedUnrecorded(candidate);
        }
        else if (candidate.AllEventsCompleted)
        {
            candidate.Phase = RunPhase.Bonus;
            candidate.BonusStartedElapsedMs ??= candidate.ActiveElapsedMs;
        }
        else
        {
            candidate.Phase = RunPhase.Normal;
            candidate.BonusStartedElapsedMs = null;
        }
    }

    private static bool MatchesLiveUndoSnapshot(RunRecord current, RunRecord after)
    {
        var currentComparable = Clone(current);
        var afterComparable = Clone(after);
        currentComparable.ActiveElapsedMs = 0;
        afterComparable.ActiveElapsedMs = 0;
        currentComparable.Revision = 0;
        afterComparable.Revision = 0;
        return string.Equals(Serialize(currentComparable), Serialize(afterComparable), StringComparison.Ordinal);
    }

    private static void ApplyInverseFields(RunRecord target, RunRecord before, RunRecord after)
    {
        if (before.CompetitorId != after.CompetitorId) target.CompetitorId = before.CompetitorId;
        if (before.Category != after.Category) target.Category = before.Category;
        if (before.Edition.DurationLimitSeconds != after.Edition.DurationLimitSeconds) target.Edition.DurationLimitSeconds = before.Edition.DurationLimitSeconds;
        if (before.Status != after.Status) target.Status = before.Status;
        if (before.Phase != after.Phase) target.Phase = before.Phase;
        if (before.BonusStartedElapsedMs != after.BonusStartedElapsedMs) target.BonusStartedElapsedMs = before.BonusStartedElapsedMs;
        if (before.BonusResultJson != after.BonusResultJson) target.BonusResultJson = before.BonusResultJson;
        if (before.BonusPointsOverride != after.BonusPointsOverride) target.BonusPointsOverride = before.BonusPointsOverride;
        if (before.Notes != after.Notes) target.Notes = before.Notes;

        foreach (var beforeEvent in before.Events)
        {
            var afterEvent = after.Events.Single(e => e.EventId == beforeEvent.EventId);
            var targetEvent = target.Events.Single(e => e.EventId == beforeEvent.EventId);
            if (beforeEvent.Status != afterEvent.Status) targetEvent.Status = beforeEvent.Status;
            if (beforeEvent.StartElapsedMs != afterEvent.StartElapsedMs) targetEvent.StartElapsedMs = beforeEvent.StartElapsedMs;
            if (beforeEvent.FinishElapsedMs != afterEvent.FinishElapsedMs) targetEvent.FinishElapsedMs = beforeEvent.FinishElapsedMs;
            if (beforeEvent.ScoreOverride != afterEvent.ScoreOverride) targetEvent.ScoreOverride = beforeEvent.ScoreOverride;
            if (beforeEvent.MeasurementJson != afterEvent.MeasurementJson) targetEvent.MeasurementJson = beforeEvent.MeasurementJson;
            if (beforeEvent.Notes != afterEvent.Notes) targetEvent.Notes = beforeEvent.Notes;
        }
    }

    private void HandleOfficialConflict(RunRecord original, RunRecord candidate, bool replaceExisting, out RunRecord? replaced)
    {
        replaced = null;
        if (candidate.Category != RunCategory.Official)
        {
            return;
        }

        replaced = FindAcceptedOfficial(candidate.CompetitorId, candidate.EditionId);
        if (replaced is not null && replaced.Id != original.Id && !replaceExisting)
        {
            throw new CommandException("This correction would create two accepted official runs; explicitly replace the existing result first.");
        }
        if (replaced is not null && replaced.Id != original.Id)
        {
            replaced.Status = RunStatus.Superseded;
            replaced.SupersededByRunId = candidate.Id;
            replaced.Revision++;
            candidate.SupersedesRunId = replaced.Id;
        }
        else
        {
            replaced = null;
        }
    }

    private RunRecord FindHistoricalRun(string runId)
    {
        if (_current?.Id == runId)
        {
            throw new CommandException("Historical editing cannot target the active physical run.");
        }

        return _data.Runs.SingleOrDefault(r => r.Id == runId)
            ?? throw new CommandException("Run was not found.");
    }

    private void ReplaceRun(RunRecord original, RunRecord replacement)
    {
        var index = _data.Runs.IndexOf(original);
        _data.Runs[index] = replacement;
        if (_current?.Id == original.Id)
        {
            _current = replacement;
        }
        if (_lastDisplayedRun?.Id == original.Id)
        {
            _lastDisplayedRun = replacement;
        }
    }

    private void ReloadInMemoryAfterPersistenceFailure()
    {
        var fresh = _store.Load();
        _data.Competitors.Clear();
        _data.Competitors.AddRange(fresh.Competitors);
        _data.Queue.Clear();
        _data.Queue.AddRange(fresh.Queue);
        _data.Devices.Clear();
        _data.Devices.AddRange(fresh.Devices);
        _data.Runs.Clear();
        _data.Runs.AddRange(fresh.Runs);
        _data.Messages.Clear();
        _data.Messages.AddRange(fresh.Messages);
        _data.Edits.Clear();
        _data.Edits.AddRange(fresh.Edits);
        _data.SelectedCompetitorId = fresh.SelectedCompetitorId;
        _data.SelectedRunCategory = fresh.SelectedRunCategory;
        _current = _data.Runs.SingleOrDefault(r => r.Status is RunStatus.Armed or RunStatus.Countdown or RunStatus.Active or RunStatus.Paused or RunStatus.Finished);
        _lastDisplayedRun = _current ?? _data.Runs.OrderByDescending(r => r.CreatedAt).FirstOrDefault();
        _clockAnchorMilliseconds = _clock.MonotonicMilliseconds;
    }

    private RunRecord CreateRun(QueueItemRecord queueItem, bool manualOfflineOverride)
    {
        var snapshot = _edition.ToSnapshot();
        return new RunRecord
        {
            Id = NewId("run"),
            CompetitorId = queueItem.CompetitorId,
            EditionId = _edition.EditionId,
            Edition = snapshot,
            Category = queueItem.Category,
            Status = RunStatus.Armed,
            Phase = RunPhase.Normal,
            ManualOfflineOverride = manualOfflineOverride,
            ActiveElapsedMs = 0,
            CreatedAt = _clock.UtcNow,
            Notes = queueItem.Reason,
            Events = snapshot.Events.Select(e => new EventRecord
            {
                EventId = e.EventId,
                Name = e.Name,
                DeviceId = e.DeviceId,
                Type = e.Type,
                Prompt = e.Prompt,
                Status = EventStatus.Pending
            }).ToList()
        };
    }

    private RunRecord RequireCurrent() => _current ?? throw new CommandException("There is no armed or active run.");

    private void RequireCompetitor(string competitorId)
    {
        if (_data.Competitors.All(c => c.Id != competitorId))
        {
            throw new CommandException("Competitor was not found.");
        }
    }

    private RunRecord? FindAcceptedOfficial(string competitorId, string editionId) => _data.Runs
        .Where(r => r.CompetitorId == competitorId && r.EditionId == editionId && r.IsCountedOfficial)
        .OrderByDescending(r => r.CreatedAt)
        .FirstOrDefault();

    private void NormalizeQueue()
    {
        for (var i = 0; i < _data.Queue.Count; i++)
        {
            _data.Queue[i].Position = i;
        }
    }

    private void RecomputeScores(RunRecord run)
    {
        foreach (var result in run.Events)
        {
            result.Score = ScoreCalculator.Calculate(result, run.Edition.Scoring);
        }
    }

    private void MarkFinishedUnrecorded(RunRecord run)
    {
        run.Status = RunStatus.Finished;
        run.FinishedAt ??= _clock.UtcNow;
        run.PausedFromPhase = null;
        run.Phase = RunPhase.Normal;
        run.BonusStartedElapsedMs = null;
    }

    private void UpdateDeviceLeds()
    {
        foreach (var device in _data.Devices)
        {
            if (device.Availability != DeviceAvailability.Online)
            {
                device.Led = LedState.OfflineError;
                continue;
            }

            if (_current is null || _current.Status is RunStatus.Finished or RunStatus.Completed or RunStatus.TimedOut or RunStatus.Aborted or RunStatus.Superseded)
            {
                device.Led = _current is null ? LedState.Ready : LedState.RunFinished;
                continue;
            }

            if (_current.Status == RunStatus.Paused)
            {
                device.Led = LedState.Paused;
                continue;
            }

            if (_current.Status == RunStatus.Countdown)
            {
                device.Led = LedState.Countdown;
                continue;
            }

            if (_current.Phase == RunPhase.Bonus)
            {
                device.Led = LedState.Bonus;
                continue;
            }

            var eventResult = _current.Events.SingleOrDefault(e => e.DeviceId == device.DeviceId);
            device.Led = eventResult?.Status switch
            {
                EventStatus.Active => LedState.EventActive,
                EventStatus.Completed => LedState.EventCompleted,
                _ => LedState.EventAvailable
            };
        }
    }

    private List<LeaderboardRow> BuildLeaderboard()
    {
        var competitorNames = _data.Competitors.ToDictionary(c => c.Id, c => c.Name);
        var rows = _data.Runs
            .Where(r => r.EditionId == _edition.EditionId && r.IsCountedOfficial)
            .Select(r => new LeaderboardRow
            {
                CompetitorName = competitorNames.GetValueOrDefault(r.CompetitorId, "Unknown competitor"),
                Points = r.TotalPoints,
                Category = r.Category,
                Status = r.Status,
                RunId = r.Id
            })
            .OrderByDescending(row => row.Points)
            .ThenBy(row => row.CompetitorName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var lastPoints = int.MinValue;
        var lastRank = 0;
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index].Points != lastPoints)
            {
                lastRank = index + 1;
                lastPoints = rows[index].Points;
            }
            rows[index].Rank = lastRank;
        }
        return rows;
    }

    private static MessageRecord CreateMessage(string messageId, string runId, string sessionId, string deviceId, string type,
        long elapsed, MessageDisposition disposition, string reason, string? payloadJson) => new()
        {
            MessageId = messageId,
            RunId = runId,
            SessionId = sessionId,
            DeviceId = deviceId,
            Type = type,
            ElapsedMilliseconds = elapsed,
            PayloadJson = payloadJson,
            Disposition = disposition,
            Reason = reason,
            ReceivedAt = DateTimeOffset.UtcNow
        };

    private static string NewId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
    private static string RequireReason(string reason) => string.IsNullOrWhiteSpace(reason) ? throw new CommandException("A correction reason is required.") : reason.Trim();
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonDefaults.Options);
    private static RunRecord DeserializeRun(string value) => JsonSerializer.Deserialize<RunRecord>(value, JsonDefaults.Options)
        ?? throw new InvalidDataException("Stored run edit snapshot is empty.");

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(Serialize(value), JsonDefaults.Options)
        ?? throw new InvalidDataException("Could not clone state.");
}
