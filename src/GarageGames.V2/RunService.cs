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
    public bool ReopenRunClock { get; set; }
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
    public bool ClearMeasurementJson { get; set; }
    public bool ClearNotes { get; set; }
    public string? MeasurementJson { get; set; }
    public string? Notes { get; set; }
    // Bonus round only (EventId = RunService.BonusEventId): the number of hits.
    public int? Hits { get; set; }
}

public sealed class RunService
{
    public const int MaximumRunDurationSeconds = 5_999;
    public const long MaximumPhysicalPressAgeMilliseconds = 10_000;
    public const int MaximumManualPoints = 1_000_000;

    public const string DatabaseClearConfirmationPhrase = "CLEAR ALL DATA";

    private readonly object _gate = new();
    private readonly RunStore _store;
    private EditionDefinition _edition;
    private readonly IMonotonicClock _clock;
    private readonly StoreSnapshot _data;
    private RunRecord? _current;
    // Always the same instance held in _data.Runs (never a clone), so commands on
    // the displayed run update history and persistence together.
    private RunRecord? _lastDisplayedRun;
    private long _clockAnchorMilliseconds;
    private long _countdownAnchorMilliseconds;
    // Active elapsed time when the current run last became Active; aged physical
    // presses are never back-dated into a pause or before the run started.
    private long _activeSegmentStartElapsedMs;
    // What each running keypad event's player has typed so far, for the TV only. It is
    // display state: not persisted, and reset whenever another run takes over.
    private readonly Dictionary<string, KeypadEntryState> _keypadEntries = new(StringComparer.Ordinal);
    // "Prime next competitor": between runs, the TV shows who is up next (full clock, no
    // scores) instead of the previous run. Display only; cleared when a run is armed.
    private PrimedCompetitor? _primed;

    public const long KeypadWrongDisplayMilliseconds = 2_000;
    private readonly KeypadChallengeSet _keypadChallenges;

    // Raised (under the service lock) when the game calls for a sound; the host plays it
    // through this computer's speakers. Handlers must only queue the sound.
    public event Action<SoundCue>? SoundCueRequested;

    private sealed class KeypadEntryState
    {
        public required string RunId { get; init; }
        public uint Sequence { get; set; }
        public string Entry { get; set; } = "";
        public long? WrongAtMonotonicMs { get; set; }
        public long? CorrectAtMonotonicMs { get; set; }
    }

    // Keypad events start and finish from the same physical spoke as regular events;
    // only their finish signal differs.
    private static bool IsButtonEvent(EventKind kind) => kind is EventKind.Standard or EventKind.Keypad;

    private KeypadEntryState KeypadEntryFor(RunRecord run, EventRecord eventResult)
    {
        foreach (var stale in _keypadEntries.Where(pair => pair.Value.RunId != run.Id).Select(pair => pair.Key).ToList())
        {
            _keypadEntries.Remove(stale);
        }

        if (!_keypadEntries.TryGetValue(eventResult.EventId, out var entry))
        {
            entry = new KeypadEntryState { RunId = run.Id };
            _keypadEntries[eventResult.EventId] = entry;
        }

        return entry;
    }

    public RunService(RunStore store, EditionDefinition edition, IMonotonicClock clock,
        KeypadChallengeSet? keypadChallenges = null)
    {
        EditionDefinition.Validate(edition);
        _store = store;
        _clock = clock;
        _keypadChallenges = keypadChallenges ?? KeypadChallengeSet.Empty;
        _edition = _store.LoadOrInitializeActiveEdition(edition);
        _store.EnsureDevices(_edition);
        _data = _store.Load();

        var unfinished = _data.Runs.Where(r => r.Status is RunStatus.Armed or RunStatus.Countdown or RunStatus.Active or RunStatus.Paused or RunStatus.Finished).ToList();
        if (unfinished.Count > 1)
        {
            throw new InvalidDataException("The database contains more than one unfinished run; manual recovery is required.");
        }

        _current = unfinished.SingleOrDefault();
        _lastDisplayedRun = _current is null
            ? _data.Runs.Where(r => !r.IsDeleted).OrderByDescending(r => r.CreatedAt).FirstOrDefault()
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

        RestartClockAnchor();
        _countdownAnchorMilliseconds = _clock.MonotonicMilliseconds;
        UpdateDeviceLeds();
    }

    public string EditionId => _edition.EditionId;

    public bool CanIdentifyPhysicalButtons()
    {
        lock (_gate)
        {
            RefreshActiveClock();
            return _current is null || _current.Status is RunStatus.Finished or RunStatus.TimedOut or RunStatus.Aborted or RunStatus.Superseded;
        }
    }

    public void SetLeaderboardPreference(bool showExhibitions)
    {
        lock (_gate)
        {
            if (_data.ShowExhibitionsOnLeaderboard == showExhibitions) return;
            _store.SaveLeaderboardPreference(showExhibitions);
            _data.ShowExhibitionsOnLeaderboard = showExhibitions;
        }
    }

    public EditionSetup GetSetup()
    {
        lock (_gate)
        {
            return ToSetup(_edition);
        }
    }

    public EditionSetup UpdateSetup(EditionSetup request)
    {
        lock (_gate)
        {
            if (_current is { Status: RunStatus.Armed or RunStatus.Countdown or RunStatus.Active or RunStatus.Paused or RunStatus.Finished })
            {
                throw new CommandException("Edition setup cannot be changed while a run is in progress or awaiting recording.");
            }

            var candidate = new EditionDefinition
            {
                EditionId = request.EditionId?.Trim() ?? "",
                Name = request.Name?.Trim() ?? "",
                DurationLimitSeconds = _edition.DurationLimitSeconds,
                Scoring = _edition.Scoring.Clone(),
                Events = (request.Events ?? []).Select(CloneEventDefinition).ToList(),
                BonusGame = (request.BonusGame ?? _edition.BonusGame)?.Clone()
            };
            foreach (var eventDefinition in candidate.Events)
            {
                eventDefinition.EventId = eventDefinition.EventId.Trim();
                eventDefinition.Name = eventDefinition.Name.Trim();
                eventDefinition.DeviceId = NormalizeDeviceId(eventDefinition.DeviceId.Trim());
            }

            try
            {
                EditionDefinition.Validate(candidate, "setup request");
            }
            catch (InvalidDataException exception)
            {
                throw new CommandException(exception.Message);
            }

            // Bonus round points and timing score runs too, so they version the edition like
            // event scoring does.
            // (Renaming the round alone does not.)
            var eventSetupChanged = !SameEventSetup(_edition.Events, candidate.Events) ||
                !(_edition.BonusGame ?? new BonusGameSettings()).SameScoringAs(candidate.BonusGame ?? new BonusGameSettings());
            if (eventSetupChanged && string.Equals(candidate.EditionId, _edition.EditionId, StringComparison.Ordinal) &&
                _data.Runs.Any(run => run.EditionId == _edition.EditionId && run.IsRecorded))
            {
                candidate.EditionId = NewId("edition");
            }

            if (string.Equals(Serialize(candidate), Serialize(_edition), StringComparison.Ordinal))
            {
                return ToSetup(_edition);
            }

            // Keep a pre-change copy so a failed backup never leaves settings partially changed.
            _store.CreateBackup();
            _store.SaveActiveEdition(candidate);
            _edition = candidate;
            var fresh = _store.Load();
            _data.Devices.Clear();
            _data.Devices.AddRange(fresh.Devices);
            UpdateDeviceLeds();
            return ToSetup(_edition);
        }
    }

    public DeviceScanResult RecordDeviceScan(bool connected, bool completed, IEnumerable<string> detectedDeviceIds)
    {
        lock (_gate)
        {
            var detected = detectedDeviceIds
                .Where(MasterProtocolCodec.IsValidDeviceId)
                .Select(NormalizeDeviceId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var now = _clock.UtcNow;
            var deviceRows = new List<DeviceRecord>(_edition.Events.Count);
            var scanEntries = new List<DeviceScanEntry>(_edition.Events.Count);

            foreach (var eventDefinition in _edition.Events)
            {
                var device = _data.Devices.Single(row => string.Equals(row.DeviceId, eventDefinition.DeviceId, StringComparison.OrdinalIgnoreCase));
                var hasPhysicalId = MasterProtocolCodec.IsValidDeviceId(eventDefinition.DeviceId);
                var responding = connected && completed && hasPhysicalId && detected.Contains(NormalizeDeviceId(eventDefinition.DeviceId));
                var missing = connected && completed && hasPhysicalId && !responding;
                device.Availability = responding
                    ? DeviceAvailability.Online
                    : missing ? DeviceAvailability.Offline : DeviceAvailability.Unverified;
                device.LastSeenAt = responding ? now : null;
                device.LastError = missing ? "No response in the most recent completed device scan." : null;
                deviceRows.Add(Clone(device));

                var status = !connected || !completed || !hasPhysicalId
                    ? "NotScanned"
                    : responding ? "Responding" : "NotResponding";
                scanEntries.Add(new DeviceScanEntry(eventDefinition.EventId, eventDefinition.Name, eventDefinition.DeviceId, status));
            }

            try
            {
                _store.SaveDevices(deviceRows, connected && completed ? now : null);
                if (connected && completed) _data.DeviceScanCheckedAt = now;
            }
            catch
            {
                ReloadInMemoryAfterPersistenceFailure();
                throw;
            }

            UpdateDeviceLeds();
            return new DeviceScanResult(connected, connected && completed,
                connected && completed ? detected.Order(StringComparer.Ordinal).ToList() : [], scanEntries);
        }
    }

    public void MarkDevicesUnverified() => RecordDeviceScan(false, false, []);

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
                Availability = DeviceAvailability.Unverified,
                Led = LedState.Ready
            }));
            _data.Runs.Clear();
            _data.Messages.Clear();
            _data.Edits.Clear();
            _data.SelectedCompetitorId = null;
            _data.SelectedRunCategory = null;
            _data.DeviceScanCheckedAt = null;
            _data.ShowExhibitionsOnLeaderboard = false;
            _current = null;
            _lastDisplayedRun = null;
            _primed = null;
            RestartClockAnchor();
            UpdateDeviceLeds();

            return backupPath;
        }
    }

    public void Checkpoint()
    {
        lock (_gate)
        {
            RefreshActiveClock(persist: true);
        }
    }

    public bool IsCurrentRun(string runId)
    {
        lock (_gate)
        {
            return _current?.Id == runId;
        }
    }

    public RunRecord ClearEvent(string runId, string eventId, int expectedRevision)
    {
        lock (_gate)
        {
            RefreshActiveClock();
            var isCurrent = _current?.Id == runId;
            var run = isCurrent ? _current! : FindHistoricalRun(runId);
            // On the live run, presses keep changing the revision; clearing an event is a
            // deliberate reset of that one event, so it applies to the latest state rather
            // than failing on a revision that is seconds old. Saved runs keep the check.
            if (isCurrent || _lastDisplayedRun?.Id == runId)
            {
                expectedRevision = run.Revision;
            }
            else if (run.Revision != expectedRevision)
            {
                throw new CommandException("This run changed before the event could be cleared. Reload it and try again.");
            }

            // The bonus round's row clears its result only; it never reopens the run.
            var isBonus = eventId == BonusEventId;
            if (isBonus && run.BonusGame is null)
            {
                throw new CommandException("This run has no bonus round result to clear.");
            }
            var eventName = isBonus
                ? run.Edition.BonusGame?.DisplayName ?? BonusGameSettings.DefaultName
                : (run.Events.SingleOrDefault(item => item.EventId == eventId)
                    ?? throw new CommandException("That event is not part of this run.")).Name;
            var reopenRun = !isBonus && isCurrent && run.Status == RunStatus.Finished &&
                run.Events.All(item => IsButtonEvent(item.Type));
            var request = new EditRunRequest
            {
                ExpectedRevision = expectedRevision,
                Reason = $"Operator cleared event '{eventName}'.",
                Status = reopenRun ? RunStatus.Active : null,
                ReopenRunClock = reopenRun,
                Events =
                [
                    new EventEditRequest
                    {
                        EventId = eventId,
                        Status = EventStatus.Pending,
                        ClearStartElapsedMs = true,
                        ClearFinishElapsedMs = true,
                        ClearScoreOverride = true,
                        ClearMeasurementJson = true,
                        ClearNotes = true
                    }
                ]
            };

            var updated = isCurrent ? EditCurrentRun(request) : EditHistoricalRun(runId, request);
            if (reopenRun) RestartClockAnchor();
            UpdateDeviceLeds();
            return Clone(updated);
        }
    }

    public RunRecord UndoLastEventPress() => UndoEventPress(eventId: null);

    // Steps back one press: the latest press of the run overall, or, with an event id,
    // that event's latest press only (finish -> running, start -> not started), leaving
    // presses on other events untouched. Every undo is saved as an audited edit.
    public RunRecord UndoEventPress(string? eventId)
    {
        lock (_gate)
        {
            RefreshActiveClock();
            var run = _current ?? (_lastDisplayedRun?.Status == RunStatus.TimedOut ? _lastDisplayedRun : null)
                ?? throw new CommandException("There is no current run with an event press to undo.");
            if (run.IsRecorded)
            {
                throw new CommandException("Recorded runs are changed through the history editor, not button-press undo.");
            }
            if (run.Status is not RunStatus.Active and not RunStatus.Paused and not RunStatus.Finished and not RunStatus.TimedOut)
            {
                throw new CommandException("Undo is available after the run has started.");
            }
            if (run.BonusGame is not null)
            {
                throw new CommandException("Presses can't be undone once the bonus round has started; correct event times on the scorecard instead.");
            }

            var requestedEvent = eventId is null
                ? null
                : run.Events.SingleOrDefault(item => item.EventId == eventId)
                    ?? throw new CommandException("That event is not part of this run.");
            if (requestedEvent is not null && !IsButtonEvent(requestedEvent.Type))
            {
                throw new CommandException("Only button and keypad events can be undone this way; use the scorecard to correct it.");
            }
            if (requestedEvent is not null && requestedEvent.Status == EventStatus.Pending)
            {
                throw new CommandException($"'{requestedEvent.Name}' has not been started, so there is nothing to undo.");
            }

            var selected = _data.Messages
                .Where(message => message.RunId == run.Id && message.Disposition == MessageDisposition.Accepted)
                .Select(message => new
                {
                    Message = message,
                    Event = run.Events.SingleOrDefault(item => IsButtonEvent(item.Type) &&
                        string.Equals(item.DeviceId, message.DeviceId, StringComparison.OrdinalIgnoreCase))
                })
                .Where(item => item.Event is not null && (requestedEvent is null || item.Event.EventId == requestedEvent.EventId) &&
                    IsUndoablePress(item.Event, item.Message))
                .OrderByDescending(item => item.Message.Id)
                .FirstOrDefault();
            if (selected is null && requestedEvent is null)
            {
                throw new CommandException("There are no remaining event-button presses to undo.");
            }

            // A targeted event whose current time came from a scorecard edit has no press
            // message to retract; it still steps back, recorded as an edit.
            var targetEvent = selected?.Event ?? requestedEvent!;
            var before = Serialize(run);
            var candidate = Clone(run);
            var candidateEvent = candidate.Events.Single(item => item.EventId == targetEvent.EventId);
            var undoneFinish = candidateEvent.Status == EventStatus.Completed;
            var undoneKeypadCode = candidateEvent.Type == EventKind.Keypad
                ? candidateEvent.Keypad?.Challenges.LastOrDefault(challenge => challenge.IsSolved)
                : null;
            var undoneStep = undoneFinish ? "finish" : "start";
            if (undoneKeypadCode is not null)
            {
                // Put the last solved message back on screen: drop any message drawn after it
                // and reopen the event if that code had finished it.
                var progress = candidateEvent.Keypad!;
                var solvedBefore = progress.SolvedCount;
                progress.Challenges.RemoveAll(challenge => !challenge.IsSolved);
                undoneKeypadCode.SolvedElapsedMs = null;
                undoneKeypadCode.SolvedByMessageId = null;
                candidateEvent.Prompt = undoneKeypadCode.Prompt;
                candidateEvent.FinishElapsedMs = null;
                candidateEvent.Status = EventStatus.Active;
                candidateEvent.LastSignalElapsedMs = progress.Challenges.LastOrDefault(challenge => challenge.IsSolved)?.SolvedElapsedMs
                    ?? candidateEvent.StartElapsedMs;
                candidateEvent.ScoreOverride = null;
                undoneStep = $"code {solvedBefore} of {RequiredKeypadSuccesses(candidate, candidateEvent)}";
            }
            else if (undoneFinish)
            {
                candidateEvent.FinishElapsedMs = null;
                candidateEvent.Status = EventStatus.Active;
                candidateEvent.LastSignalElapsedMs = candidateEvent.StartElapsedMs;
                candidateEvent.ScoreOverride = null;
            }
            else
            {
                candidateEvent.StartElapsedMs = null;
                candidateEvent.FinishElapsedMs = null;
                candidateEvent.Status = EventStatus.Pending;
                candidateEvent.LastSignalElapsedMs = null;
                candidateEvent.ScoreOverride = null;
                if (candidateEvent.Type == EventKind.Keypad)
                {
                    // Its drawn messages go back into the pool for the next start.
                    candidateEvent.Keypad = null;
                    candidateEvent.Prompt = null;
                }
            }
            candidateEvent.Score = CalculateScore(candidateEvent, candidate.Edition);
            // A keypad event stepped back to running starts a fresh entry on the TV.
            _keypadEntries.Remove(candidateEvent.EventId);

            var reopenRun = candidate.Status == RunStatus.Finished &&
                run.Events.All(item => IsButtonEvent(item.Type));
            if (reopenRun)
            {
                candidate.Status = RunStatus.Active;
                candidate.FinishedAt = null;
                candidate.Phase = RunPhase.Normal;
                candidate.BonusStartedElapsedMs = null;
                candidate.PausedFromPhase = null;
            }
            candidate.Revision = run.Revision + 1;
            var after = Serialize(candidate);
            var edit = new EditRecord
            {
                RunId = run.Id,
                CreatedAt = _clock.UtcNow,
                Reason = $"Undid the latest {undoneStep} for '{targetEvent.Name}'.",
                BeforeJson = before,
                AfterJson = after
            };

            ReplaceRun(run, candidate);
            try
            {
                if (selected is not null)
                {
                    selected.Message.Disposition = MessageDisposition.Undone;
                    selected.Message.Reason = undoneKeypadCode is not null
                        ? $"Undone by operator; removed {undoneStep} for '{targetEvent.Name}'."
                        : $"Undone by operator; removed the {undoneStep} press for '{targetEvent.Name}'.";
                    _store.AddEditAndUndoMessage(candidate, edit, selected.Message);
                }
                else
                {
                    _store.AddEdit(candidate, edit);
                }
            }
            catch
            {
                ReloadInMemoryAfterPersistenceFailure();
                throw;
            }
            _data.Edits.Add(edit);
            if (reopenRun) RestartClockAnchor();
            UpdateDeviceLeds();
            return Clone(candidate);
        }
    }

    // The message that produced an event's current state: its start press while running, or
    // its finish (a second press, or for a keypad event the correct code or the operator's
    // override) once completed. Wrong keypad codes are accepted signals that move a running
    // keypad event's last-signal time, so its start is matched on the start time alone.
    private static bool IsUndoablePress(EventRecord eventResult, MessageRecord message)
    {
        var elapsed = message.ElapsedMilliseconds;
        if (eventResult.Type == EventKind.Keypad)
        {
            // Keypad events step back one solved code at a time, then their start.
            var lastSolved = eventResult.Keypad?.Challenges.LastOrDefault(challenge => challenge.IsSolved);
            if (lastSolved is not null)
            {
                return eventResult.Status is EventStatus.Active or EventStatus.Completed &&
                    lastSolved.SolvedByMessageId == message.MessageId;
            }
            return eventResult.Status == EventStatus.Active && message.Type == "event-press" &&
                eventResult.StartElapsedMs == elapsed && eventResult.FinishElapsedMs is null;
        }

        if (eventResult.Status == EventStatus.Completed)
        {
            return message.Type == "event-press" && eventResult.FinishElapsedMs == elapsed && eventResult.LastSignalElapsedMs == elapsed;
        }

        return eventResult.Status == EventStatus.Active && message.Type == "event-press" &&
            eventResult.StartElapsedMs == elapsed && eventResult.FinishElapsedMs is null &&
            eventResult.LastSignalElapsedMs == elapsed;
    }

    public RunCountdownState GetCountdownState()
    {
        lock (_gate)
        {
            if (_current is null)
            {
                return new RunCountdownState(null, null);
            }

            var elapsed = _current.Status == RunStatus.Countdown
                ? Math.Max(0, _clock.MonotonicMilliseconds - _countdownAnchorMilliseconds)
                : 0;
            return new RunCountdownState(_current.Id, _current.Status, elapsed);
        }
    }

    public MasterRunStatus GetMasterStatus()
    {
        lock (_gate)
        {
            RefreshActiveClock();
            return BuildMasterRunStatus(_current ?? _lastDisplayedRun);
        }
    }

    public MasterGarageStatus GetGarageStatus()
    {
        lock (_gate)
        {
            RefreshActiveClock();
            return BuildGarageStatus(_current ?? _lastDisplayedRun);
        }
    }

    public (MasterRunStatus Status, MasterGarageStatus GarageStatus, MasterGarageEventSnapshot EventSnapshot) GetMasterStatuses()
    {
        lock (_gate)
        {
            RefreshActiveClock();
            var run = _current ?? _lastDisplayedRun;
            return (BuildMasterRunStatus(run), BuildGarageStatus(run), BuildGarageEventSnapshot(_current));
        }
    }

    private static MasterGarageEventSnapshot BuildGarageEventSnapshot(RunRecord? run)
    {
        if (run is null || run.Status != RunStatus.Active)
        {
            return new MasterGarageEventSnapshot(null, []);
        }

        var token = MasterProtocolCodec.GetGarageRunToken(run.Id);
        if (token is null)
        {
            return new MasterGarageEventSnapshot(null, []);
        }

        var events = run.Events
            .Where(eventResult => IsButtonEvent(eventResult.Type) && MasterProtocolCodec.IsValidDeviceId(eventResult.DeviceId))
            .OrderBy(eventResult => eventResult.DeviceId, StringComparer.OrdinalIgnoreCase)
            .Select(eventResult => new MasterGarageEventStatus(token, run.Revision, eventResult.DeviceId,
                eventResult.Status switch
                {
                    EventStatus.Active => "ACTIVE",
                    EventStatus.Completed => "COMPLETED",
                    _ => "PENDING"
                }))
            .ToList();
        return new MasterGarageEventSnapshot($"{run.Id}:{run.Revision}", events);
    }

    private static MasterRunStatus BuildMasterRunStatus(RunRecord? run)
    {
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

    private static MasterGarageStatus BuildGarageStatus(RunRecord? run)
    {
        if (run is null)
        {
            return new MasterGarageStatus("-", "NONE");
        }

        var state = run.Status switch
        {
            RunStatus.Armed => "ARMED",
            RunStatus.Countdown => "COUNTDOWN",
            RunStatus.Active => "ACTIVE",
            RunStatus.Paused => "PAUSED",
            RunStatus.Finished or RunStatus.Completed => "FINISHED",
            RunStatus.TimedOut => "TIMED_OUT",
            _ => "NONE"
        };
        if (state == "NONE")
        {
            return new MasterGarageStatus("-", state);
        }

        var token = MasterProtocolCodec.GetGarageRunToken(run.Id);
        return token is null ? new MasterGarageStatus("-", "NONE") : new MasterGarageStatus(token, state);
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
                ShowExhibitionsOnLeaderboard = _data.ShowExhibitionsOnLeaderboard,
                SelectedCompetitorId = _data.SelectedCompetitorId ?? "",
                SelectedRunCategory = _data.SelectedRunCategory,
                DeviceScanCheckedAt = _data.DeviceScanCheckedAt,
                CurrentRun = _current is null ? (_lastDisplayedRun is null ? null : Clone(_lastDisplayedRun)) : Clone(_current),
                Events = _edition.ToSnapshot().Events,
                BonusGame = (_edition.BonusGame ?? new BonusGameSettings()).Clone(),
                Primed = _current is null ? _primed : null,
                Competitors = _data.Competitors.Select(Clone).ToList(),
                Queue = _data.Queue.OrderBy(q => q.Position).Select(Clone).ToList(),
                Devices = _edition.Events
                    .Select(eventDefinition => _data.Devices.Single(device =>
                        string.Equals(device.DeviceId, eventDefinition.DeviceId, StringComparison.OrdinalIgnoreCase)))
                    .Select(Clone)
                    .ToList(),
                History = _data.Runs.Where(r => !r.IsDeleted).OrderByDescending(r => r.CreatedAt).Select(Clone).ToList(),
                DeletedRuns = _data.Runs.Where(r => r.IsDeleted).OrderByDescending(r => r.DeletedAt).Select(Clone).ToList(),
                Messages = _data.Messages.OrderByDescending(m => m.Id).Take(250).Select(Clone).ToList(),
                Edits = _data.Edits.OrderByDescending(e => e.Id).Take(250).Select(Clone).ToList(),
                Leaderboard = BuildLeaderboard()
            };
        }
    }

    public PrimedCompetitor PrimeNextCompetitor(string competitorId, RunCategory category, int? durationLimitSeconds)
    {
        lock (_gate)
        {
            if (_current is not null)
            {
                throw new CommandException("Finish and record (or discard) the current run before showing who is up next.");
            }
            RequireActiveCompetitor(competitorId);
            var duration = RequireRequestedRunDuration(durationLimitSeconds);
            _primed = new PrimedCompetitor(competitorId, category, duration);
            return _primed;
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
                    // A keypad message is revealed only once its event has been started.
                    Prompt = e.Type == EventKind.Keypad && e.Status != EventStatus.Active ? null : e.Prompt,
                    Status = e.Status,
                    AwardedPoints = e.Score,
                    DurationMs = e.Status == EventStatus.Completed ? e.DurationMs : null
                }).ToList(),
                KeypadChallenge = BuildKeypadChallenge(displayedRun),
                BonusGame = BuildScoreboardBonus(displayedRun)
            };

            // Between runs, a primed competitor replaces the previous run: full clock, the
            // edition's events all pending, no points.
            var primed = _current is null ? _primed : null;
            if (primed is not null)
            {
                current = new ScoreboardRun
                {
                    CompetitorName = competitorNames.GetValueOrDefault(primed.CompetitorId, "Unknown competitor"),
                    Category = primed.Category,
                    Status = RunStatus.Armed,
                    Phase = RunPhase.Normal,
                    IsPrimed = true,
                    RemainingMilliseconds = primed.DurationLimitSeconds * 1000L,
                    TotalEvents = _edition.Events.Count,
                    Events = _edition.Events.Select(e => new ScoreboardEvent { Name = e.Name, Status = EventStatus.Pending }).ToList()
                };
            }

            // The primed competitor is shown as competing, so not also as on deck.
            var queue = _data.Queue.OrderBy(q => q.Position).ToList();
            var primedEntry = primed is null ? null : queue.FirstOrDefault(item =>
                item.CompetitorId == primed.CompetitorId && item.Category == primed.Category);
            if (primedEntry is not null) queue.Remove(primedEntry);
            var onDeck = queue.Take(4)
                .Select(item => new ScoreboardOnDeck(
                    competitorNames.GetValueOrDefault(item.CompetitorId, "Unknown competitor"), item.Category))
                .ToList();
            return new ScoreboardSnapshot
            {
                EditionName = _edition.Name,
                DurationLimitSeconds = primed?.DurationLimitSeconds ?? displayedRun?.Edition.DurationLimitSeconds ?? _edition.DurationLimitSeconds,
                SimulationMode = simulationMode,
                ShowExhibitionsOnLeaderboard = _data.ShowExhibitionsOnLeaderboard,
                CurrentRun = current,
                OnDeckName = onDeck.FirstOrDefault()?.Name,
                OnDeck = onDeck,
                Leaderboard = BuildLeaderboard()
            };
        }
    }

    private static ScoreboardBonusGame? BuildScoreboardBonus(RunRecord run)
    {
        if (run.BonusGame is not { } bonus)
        {
            return null;
        }

        var target = bonus.Phase == BonusGamePhase.Target
            ? run.Events.SingleOrDefault(e => e.EventId == bonus.TargetEventId)
            : null;
        return new ScoreboardBonusGame
        {
            Name = run.Edition.BonusGame?.DisplayName ?? BonusGameSettings.DefaultName,
            Phase = bonus.Phase,
            TargetEventId = target?.EventId,
            TargetEventName = target?.Name,
            TargetRemainingMs = target is null ? null : Math.Max(0, (bonus.TargetDeadlineElapsedMs ?? 0) - run.ActiveElapsedMs),
            TargetWindowMs = target is null ? null : bonus.TargetWindowMs,
            Hits = bonus.Hits,
            PointsPerPress = bonus.PointsPerPress,
            AwardedPoints = bonus.Phase == BonusGamePhase.Ended ? bonus.CountedPoints : null,
            EndReason = bonus.EndReason
        };
    }

    private ScoreboardKeypadChallenge? BuildKeypadChallenge(RunRecord run)
    {
        if (run.Status is not (RunStatus.Active or RunStatus.Paused))
        {
            return null;
        }

        var eventResult = run.Events
            .Where(e => e.Type == EventKind.Keypad && e.Status == EventStatus.Active)
            .OrderByDescending(e => e.StartElapsedMs ?? 0)
            .FirstOrDefault();
        var prompt = eventResult?.Keypad?.Current?.Prompt ?? eventResult?.Prompt;
        if (eventResult is null || string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }

        _keypadEntries.TryGetValue(eventResult.EventId, out var entry);
        if (entry is not null && entry.RunId != run.Id)
        {
            entry = null;
        }

        var now = _clock.MonotonicMilliseconds;
        return new ScoreboardKeypadChallenge
        {
            EventName = eventResult.Name,
            Prompt = prompt,
            Entry = entry?.Entry ?? "",
            ShowWrong = entry?.WrongAtMonotonicMs is long wrongAt && now - wrongAt < KeypadWrongDisplayMilliseconds,
            ShowCorrect = entry?.CorrectAtMonotonicMs is long correctAt && now - correctAt < KeypadWrongDisplayMilliseconds,
            Successes = eventResult.Keypad?.SolvedCount ?? 0,
            RequiredSuccesses = RequiredKeypadSuccesses(run, eventResult)
        };
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

    public CompetitorImportResult ImportCompetitors(IEnumerable<string> names)
    {
        lock (_gate)
        {
            var incoming = names.Take(501).ToList();
            if (incoming.Count > 500)
            {
                throw new CommandException("Import is limited to 500 names at a time.");
            }

            var knownNames = _data.Competitors.Select(item => NormalizeCompetitorName(item.Name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = new List<CompetitorRecord>();
            var skipped = new List<string>();
            foreach (var rawName in incoming)
            {
                var name = rawName?.Trim() ?? "";
                if (name.Length is < 1 or > 120)
                {
                    skipped.Add($"{(name.Length == 0 ? "(blank row)" : name)} — names must be 1–120 characters.");
                    continue;
                }

                if (!knownNames.Add(NormalizeCompetitorName(name)))
                {
                    skipped.Add($"{name} — duplicate name already exists or appears earlier in this import.");
                    continue;
                }

                added.Add(new CompetitorRecord
                {
                    Id = NewId("competitor"),
                    Name = name,
                    EditionId = _edition.EditionId,
                    CreatedAt = _clock.UtcNow.AddTicks(added.Count)
                });
            }

            if (added.Count > 0)
            {
                _store.AddCompetitors(added);
                _data.Competitors.AddRange(added);
            }

            return new CompetitorImportResult(added.Select(Clone).ToList(), skipped);
        }
    }

    public CompetitorRecord RenameCompetitor(string competitorId, string name)
    {
        lock (_gate)
        {
            var competitor = _data.Competitors.SingleOrDefault(item => item.Id == competitorId)
                ?? throw new CommandException("Competitor was not found.");
            name = name.Trim();
            if (name.Length is < 1 or > 120)
            {
                throw new CommandException("Competitor name must be between 1 and 120 characters.");
            }
            if (_data.Competitors.Any(item => item.Id != competitorId &&
                string.Equals(NormalizeCompetitorName(item.Name), NormalizeCompetitorName(name), StringComparison.OrdinalIgnoreCase)))
            {
                throw new CommandException("Another competitor already has that name. Names must be unique ignoring capitalization and extra spaces.");
            }

            _store.RenameCompetitor(competitorId, name);
            competitor.Name = name;
            return Clone(competitor);
        }
    }

    public CompetitorRecord SetCompetitorArchived(string competitorId, bool archived)
    {
        lock (_gate)
        {
            var competitor = _data.Competitors.SingleOrDefault(item => item.Id == competitorId)
                ?? throw new CommandException("Competitor was not found.");
            if (archived && _data.Queue.Any(item => item.CompetitorId == competitorId))
            {
                throw new CommandException("Remove this competitor from the on-deck queue before archiving them.");
            }
            if (archived && _current?.CompetitorId == competitorId)
            {
                throw new CommandException("Finish or discard this competitor’s current run before archiving them.");
            }

            var archivedIds = _data.Competitors.Where(item => item.IsArchived && item.Id != competitorId)
                .Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            if (archived) archivedIds.Add(competitorId);
            var selectedCompetitorId = archived && _data.SelectedCompetitorId == competitorId
                ? null : _data.SelectedCompetitorId;
            _store.SaveArchivedCompetitorIds(archivedIds, selectedCompetitorId, _data.SelectedRunCategory);
            competitor.IsArchived = archived;
            _data.SelectedCompetitorId = selectedCompetitorId;
            return Clone(competitor);
        }
    }

    public QueueItemRecord AddToQueue(string competitorId, RunCategory category, bool replaceExistingOfficial = false, string? reason = null)
    {
        lock (_gate)
        {
            RequireActiveCompetitor(competitorId);
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

    public RunRecord ArmCompetitor(string competitorId, RunCategory category, int? durationLimitSeconds = null,
        bool replaceExistingOfficial = false)
    {
        ValidateRunDuration(durationLimitSeconds);
        lock (_gate)
        {
            RefreshActiveClock();
            if (_current is not null)
            {
                throw new CommandException("Record or finish the current run before starting another competitor.");
            }
            RequireActiveCompetitor(competitorId);
            if (!Enum.IsDefined(category))
            {
                throw new CommandException("Run category is invalid.");
            }
            var existingOfficial = category == RunCategory.Official
                ? FindAcceptedOfficial(competitorId, _edition.EditionId)
                : null;
            if (existingOfficial is not null && !replaceExistingOfficial)
            {
                throw new CommandException("This competitor already has an official result. Confirm an official redo to replace it, or choose Playoff or Exhibition.");
            }

            // An official redo is linked to the result it replaces; the original keeps
            // counting until the redo is recorded, so discarding the redo changes nothing.
            var run = CreateRun(new QueueItemRecord
            {
                Id = NewId("direct"),
                CompetitorId = competitorId,
                Category = category,
                Reason = existingOfficial is null ? null : "Official redo; replaces the previous official result when recorded."
            }, manualOfflineOverride: false, durationLimitSeconds: durationLimitSeconds);
            run.SupersedesRunId = existingOfficial?.Id;
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

    internal static void ValidateRunDuration(int? durationLimitSeconds)
    {
        if (durationLimitSeconds is int seconds && seconds is < 1 or > MaximumRunDurationSeconds)
        {
            throw new CommandException($"Run duration must be between 1 and {MaximumRunDurationSeconds} seconds.");
        }
    }

    public static int RequireRequestedRunDuration(int? durationLimitSeconds)
    {
        if (durationLimitSeconds is null)
        {
            throw new CommandException("The scorekeeper did not send a run length. Reload the latest app before arming so the run cannot silently use the default length.");
        }

        ValidateRunDuration(durationLimitSeconds);
        return durationLimitSeconds.Value;
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
            // Players finish a keypad event only with its code; the operator's second tap is
            // the override for a stuck player or a broken keypad.
            var keypadOverride = eventResult is { Type: EventKind.Keypad, Status: EventStatus.Active };
            using var payload = JsonDocument.Parse("{}");
            return ReceiveCore(new InputEnvelope
            {
                MessageId = NewId(keypadOverride ? "virtual-keypad-override" : "virtual-press"),
                SessionId = run.Id,
                RunId = run.Id,
                DeviceId = eventResult.DeviceId,
                Type = keypadOverride ? "keypad-success" : "event-press",
                ElapsedMilliseconds = run.ActiveElapsedMs,
                Payload = payload.RootElement.Clone()
            }, trustedVirtual: true);
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
                var device = _data.Devices.Single(d => string.Equals(d.DeviceId, eventDefinition.DeviceId, StringComparison.OrdinalIgnoreCase));
                return new PreflightResult
                {
                    DeviceId = device.DeviceId,
                    EventId = eventDefinition.EventId,
                    Availability = device.Availability,
                    LastSeenAt = device.LastSeenAt,
                    Passed = device.Availability == DeviceAvailability.Online,
                    ManualOverrideAvailable = device.Availability != DeviceAvailability.Online,
                    Message = device.Availability == DeviceAvailability.Online
                        ? "Responded in the current device scan."
                        : "Station is not verified as responding; virtual event controls remain available."
                };
            }).ToList();
        }
    }

    public void SetDeviceAvailability(string deviceId, DeviceAvailability availability, string? error = null)
    {
        lock (_gate)
        {
            var device = _data.Devices.SingleOrDefault(d => string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
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
            // Link the lineage now; the source is superseded only when this run is recorded.
            run.SupersedesRunId = replacementSource?.Id;

            _data.Queue.Remove(queueItem);
            NormalizeQueue();
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

    public RunRecord StartMaster()
    {
        lock (_gate)
        {
            var run = _current ?? throw new CommandException("Arm a queued competitor before starting the master.");
            using var payload = JsonDocument.Parse("{}");
            var result = Receive(new InputEnvelope
            {
                MessageId = NewId("message"),
                SessionId = run.Id,
                RunId = run.Id,
                DeviceId = "master",
                Type = "master-start",
                ElapsedMilliseconds = 0,
                Payload = payload.RootElement.Clone()
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
            RestartClockAnchor();
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

    public MasterPhysicalPressResult ReceivePhysicalSpokePress(MasterPhysicalPress press, bool sessionAllowed)
    {
        lock (_gate)
        {
            return ReceivePhysicalSpokeInput(press.BootToken, press.RunToken, press.DeviceId, press.Sequence,
                press.AgeMilliseconds, MasterProtocolCodec.GetPhysicalPressMessageId(press.RunToken, press.DeviceId, press.Sequence),
                "event-press", new
                {
                    bootToken = press.BootToken,
                    token = press.RunToken,
                    mac = press.DeviceId,
                    sequence = press.Sequence
                }, sessionAllowed);
        }
    }

    // Typed-entry updates only drive the TV display and are not persisted; they return null.
    // A submission ('*' on the keypad) is recorded like a press and answered with the event
    // state: COMPLETED for the right code, ACTIVE (still running) for a wrong one.
    public MasterPhysicalPressResult? ReceivePhysicalKeypadInput(MasterKeypadInput input, bool sessionAllowed)
    {
        lock (_gate)
        {
            RefreshActiveClock();
            var run = _current;
            var eventResult = run?.Events.SingleOrDefault(eventItem =>
                string.Equals(eventItem.DeviceId, input.DeviceId, StringComparison.OrdinalIgnoreCase));
            var liveKeypadEvent = sessionAllowed && run is { Status: RunStatus.Active } &&
                string.Equals(MasterProtocolCodec.GetGarageRunToken(run.Id), input.RunToken, StringComparison.Ordinal) &&
                eventResult is { Type: EventKind.Keypad, Status: EventStatus.Active };
            var entryState = liveKeypadEvent ? KeypadEntryFor(run!, eventResult!) : null;
            if (entryState is not null && input.Sequence > entryState.Sequence)
            {
                entryState.Sequence = input.Sequence;
                entryState.Entry = input.Submit ? "" : input.Entry;
            }

            if (!input.Submit)
            {
                return null;
            }

            return ReceivePhysicalSpokeInput(input.BootToken, input.RunToken, input.DeviceId, input.Sequence,
                input.AgeMilliseconds, MasterProtocolCodec.GetKeypadSubmitMessageId(input.RunToken, input.DeviceId, input.Sequence),
                "keypad-response", new
                {
                    bootToken = input.BootToken,
                    token = input.RunToken,
                    mac = input.DeviceId,
                    sequence = input.Sequence,
                    answer = input.Entry
                }, sessionAllowed);
        }
    }

    private MasterPhysicalPressResult ReceivePhysicalSpokeInput(string bootToken, string runToken, string deviceId,
        uint sequence, uint ageMilliseconds, string messageId, string type, object payload, bool sessionAllowed)
    {
        lock (_gate)
        {
            RefreshActiveClock();
            var tokenRun = _data.Runs
                .Where(run => string.Equals(MasterProtocolCodec.GetGarageRunToken(run.Id), runToken, StringComparison.Ordinal))
                .OrderByDescending(run => run.Id == _current?.Id)
                .ThenByDescending(run => run.Id == _lastDisplayedRun?.Id)
                .FirstOrDefault();
            var run = _current;
            var messageRun = tokenRun ?? run;
            var elapsed = messageRun is not null && ReferenceEquals(messageRun, run) && run.Status == RunStatus.Active
                ? PhysicalPressElapsed(run, deviceId, ageMilliseconds)
                : messageRun?.ActiveElapsedMs ?? 0;
            var envelope = new InputEnvelope
            {
                MessageId = messageId,
                SessionId = messageRun?.Id ?? $"garage-{runToken}",
                RunId = messageRun?.Id ?? $"garage-{runToken}",
                DeviceId = deviceId,
                Type = type,
                ElapsedMilliseconds = elapsed,
                Payload = JsonSerializer.SerializeToElement(payload, JsonDefaults.Options)
            };
            var payloadJson = envelope.Payload.GetRawText();

            if (_data.Messages.Any(message => string.Equals(message.MessageId, messageId, StringComparison.Ordinal)))
            {
                var duplicate = ReceiveCore(envelope, trustedVirtual: false);
                return PhysicalPressResult(duplicate, tokenRun, deviceId, messageId);
            }

            if (!sessionAllowed)
            {
                return PhysicalPressResult(RecordRejected(envelope, MessageDisposition.InvalidSignal,
                    "Physical spoke input does not match the current master handshake or IDLE mode.", payloadJson),
                    tokenRun, deviceId);
            }

            var currentToken = MasterProtocolCodec.GetGarageRunToken(run?.Id);
            if (run is null || !string.Equals(currentToken, runToken, StringComparison.Ordinal))
            {
                return PhysicalPressResult(RecordRejected(envelope, MessageDisposition.WrongRun,
                    "Physical spoke input token does not identify the current run.", payloadJson), tokenRun, deviceId);
            }

            if (run.Status != RunStatus.Active)
            {
                var disposition = run.Status switch
                {
                    RunStatus.Paused => MessageDisposition.Paused,
                    RunStatus.TimedOut => MessageDisposition.TimedOut,
                    _ => MessageDisposition.InvalidSignal
                };
                return PhysicalPressResult(RecordRejected(envelope, disposition,
                    "Physical spoke input is accepted only while the run is ACTIVE.", payloadJson), run, deviceId);
            }

            var eventResult = run.Events.SingleOrDefault(eventItem =>
                string.Equals(eventItem.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (eventResult is null || !IsButtonEvent(eventResult.Type))
            {
                return PhysicalPressResult(RecordRejected(envelope, MessageDisposition.UnknownStation,
                    "Physical spoke MAC is not assigned to a button or keypad event in this run.", payloadJson), run, deviceId, messageId);
            }

            var device = _data.Devices.SingleOrDefault(deviceItem =>
                string.Equals(deviceItem.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (device is null)
            {
                return PhysicalPressResult(RecordRejected(envelope, MessageDisposition.UnknownStation,
                    "Physical spoke MAC has no device record.", payloadJson), run, deviceId);
            }
            if (device.Availability != DeviceAvailability.Online)
            {
                // A handshake-valid press from the MAC assigned to this event proves the
                // spoke is alive, even if it was asleep or out of range during the arm-time scan.
                device.Availability = DeviceAvailability.Online;
                device.LastSeenAt = _clock.UtcNow;
                device.LastError = null;
                try
                {
                    _store.SetDevice(device);
                }
                catch
                {
                    ReloadInMemoryAfterPersistenceFailure();
                    throw;
                }
                UpdateDeviceLeds();
            }

            var received = ReceiveCore(envelope, trustedVirtual: false);
            return PhysicalPressResult(received, run, deviceId, messageId);
        }
    }

    // Time the press when the button was pushed, not when the laptop received it, so
    // radio retries and relay latency are not charged to the competitor. Bounded so a
    // press is never placed before the current active stretch or its event's last signal.
    private long PhysicalPressElapsed(RunRecord run, string deviceId, uint ageMilliseconds)
    {
        var age = Math.Min((long)ageMilliseconds, MaximumPhysicalPressAgeMilliseconds);
        var elapsed = Math.Max(run.ActiveElapsedMs - age, _activeSegmentStartElapsedMs);
        var lastSignal = run.Events.SingleOrDefault(eventItem =>
            string.Equals(eventItem.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))?.LastSignalElapsedMs;
        return Math.Max(elapsed, lastSignal ?? 0);
    }

    // NEXT tells a keypad spoke its code was right but more are needed (a new message is up),
    // so it signals success without finishing; ACTIVE after a code means it was wrong.
    private static MasterPhysicalPressResult PhysicalPressResult(InputResult result, RunRecord? run, string deviceId,
        string? messageId = null)
    {
        var state = "REJECTED";
        var eventResult = run?.Events.SingleOrDefault(eventItem =>
            string.Equals(eventItem.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        // A second button press on a running keypad event is refused (only the code finishes
        // it), but the spoke must stay in its running state so the keypad remains usable.
        var keypadStillRunning = result.Disposition == MessageDisposition.InvalidSignal &&
            eventResult is { Type: EventKind.Keypad, Status: EventStatus.Active } && run?.Status == RunStatus.Active;
        // A bonus press that did not count (wrong button, too late) leaves the button's own
        // completed event as it was.
        var bonusPressNotCounted = run?.BonusGame is not null && eventResult?.Status == EventStatus.Completed &&
            result.Disposition is MessageDisposition.InvalidSignal or MessageDisposition.StaleTimestamp;
        if (result.Disposition is MessageDisposition.Accepted or MessageDisposition.Duplicate or MessageDisposition.AlreadyCompleted ||
            keypadStillRunning || bonusPressNotCounted)
        {
            state = eventResult is null ? "REJECTED" : eventResult.Status switch
            {
                EventStatus.Pending => "PENDING",
                EventStatus.Active => "ACTIVE",
                EventStatus.Completed => "COMPLETED",
                _ => "REJECTED"
            };
            if (state == "ACTIVE" && messageId is not null &&
                eventResult!.Keypad?.Challenges.Any(challenge => challenge.SolvedByMessageId == messageId) == true)
            {
                state = "NEXT";
            }
        }

        return new MasterPhysicalPressResult(state, result.Disposition, result.Reason);
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
            RestartClockAnchor();
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
            RestartClockAnchor();
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
            EndBonusGame(run, "operator", run.ActiveElapsedMs);
            MarkFinishedUnrecorded(run);
            run.FinishedAt = _clock.UtcNow;
            run.Revision++;
            _lastDisplayedRun = run;
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
            RequireNotDeleted(run);
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

        // A replacement displaces its source only once it is recorded, so discarding
        // the retry leaves the original result standing. A non-official retry never
        // displaces a counted official result.
        var replacedSource = run.SupersedesRunId is null
            ? null
            : _data.Runs.SingleOrDefault(item => item.Id == run.SupersedesRunId && item.SupersededByRunId is null &&
                item.Status != RunStatus.Superseded && !item.IsDeleted);
        if (replacedSource is not null && replacedSource.IsCountedOfficial && run.Category != RunCategory.Official)
        {
            replacedSource = null;
        }
        if (run.Category == RunCategory.Official)
        {
            var existingOfficial = FindAcceptedOfficial(run.CompetitorId, run.EditionId);
            if (existingOfficial is not null && existingOfficial.Id != run.Id && existingOfficial.Id != replacedSource?.Id)
            {
                throw new CommandException("This competitor already has a recorded official result. Change this run to Playoff or Exhibition, or restart the official run to replace it.");
            }
        }

        if (complete)
        {
            run.Status = RunStatus.Completed;
        }
        run.RecordedAt = _clock.UtcNow;
        run.FinishedAt ??= _clock.UtcNow;
        run.Revision++;
        if (replacedSource is not null)
        {
            replacedSource.SupersededFromStatus = replacedSource.Status;
            replacedSource.Status = RunStatus.Superseded;
            replacedSource.SupersededByRunId = run.Id;
            replacedSource.Revision++;
        }

        // Only recording the run on screen advances the on-deck queue; recording an
        // older run from history must not consume the next competitor.
        var promotesQueue = _current?.Id == run.Id || (_current is null && _lastDisplayedRun?.Id == run.Id);
        var promoted = promotesQueue ? _data.Queue.OrderBy(item => item.Position).FirstOrDefault() : null;
        if (promoted is not null)
        {
            _data.Queue.Remove(promoted);
            NormalizeQueue();
        }
        var selectedCompetitorId = promotesQueue ? promoted?.CompetitorId : _data.SelectedCompetitorId;
        var selectedRunCategory = promotesQueue ? promoted?.Category : _data.SelectedRunCategory;
        var runsToSave = replacedSource is null ? new[] { run } : new[] { replacedSource, run };

        try
        {
            _store.SaveRunsAndQueue(runsToSave, _data.Queue, selectedCompetitorId, selectedRunCategory);
        }
        catch
        {
            ReloadInMemoryAfterPersistenceFailure();
            throw;
        }

        _data.SelectedCompetitorId = selectedCompetitorId;
        _data.SelectedRunCategory = selectedRunCategory;
        if (_lastDisplayedRun?.Id == run.Id || _lastDisplayedRun is null)
        {
            _lastDisplayedRun = run;
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
            RefreshActiveClock();
            // A timed-out run leaves the current slot (so the next competitor can go) but stays
            // unrecorded until the operator records it; until then it can still be discarded.
            var run = _current ?? (_lastDisplayedRun is { Status: RunStatus.TimedOut, IsRecorded: false, IsDeleted: false } timedOut
                ? timedOut
                : throw new CommandException("There is no armed, active, or unrecorded timed-out run to discard."));
            var actionReason = string.IsNullOrWhiteSpace(reason) ? "Operator aborted run." : reason.Trim();
            EndBonusGame(run, "operator", run.ActiveElapsedMs);
            run.Status = RunStatus.Aborted;
            run.Notes = actionReason;
            // A timed-out run keeps the moment it timed out.
            if (_current is not null || run.FinishedAt is null)
            {
                run.FinishedAt = _clock.UtcNow;
            }
            RecomputeScores(run);
            run.Revision++;
            _lastDisplayedRun = run;
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
            RequireNotDeleted(run);
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

    public InputResult Receive(InputEnvelope envelope) => ReceiveCore(envelope, trustedVirtual: false);

    private InputResult ReceiveCore(InputEnvelope envelope, bool trustedVirtual)
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
                _countdownAnchorMilliseconds = _clock.MonotonicMilliseconds;
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
                if (!trustedVirtual && envelope.DeviceId != "master" && _data.Devices.Single(d =>
                    string.Equals(d.DeviceId, envelope.DeviceId, StringComparison.OrdinalIgnoreCase)).Availability != DeviceAvailability.Online)
                {
                    return RecordRejected(envelope, MessageDisposition.Offline, "Bonus signal station is offline; record bonus results manually.", payloadJson);
                }

                run.BonusResultJson = payloadJson ?? "{}";
                run.Revision++;
                UpdateDeviceLeds();
                return RecordAccepted(envelope, run, "Bonus signal recorded; scoring remains deferred.", payloadJson);
            }

            var device = _data.Devices.SingleOrDefault(d => string.Equals(d.DeviceId, envelope.DeviceId, StringComparison.OrdinalIgnoreCase));
            var eventResult = run.Events.SingleOrDefault(e => string.Equals(e.DeviceId, envelope.DeviceId, StringComparison.OrdinalIgnoreCase));
            if (device is null || eventResult is null)
            {
                return RecordRejected(envelope, MessageDisposition.UnknownStation, "Device is not assigned in this run roster.", payloadJson);
            }

            if (!trustedVirtual && device.Availability != DeviceAvailability.Online)
            {
                return RecordRejected(envelope, MessageDisposition.Offline, "Station is offline; manual mode requires operator edits and does not accept simulated station packets.", payloadJson);
            }

            // During the bonus round every button press is a bid for the lit target.
            if (run.BonusGame is { Phase: not BonusGamePhase.Ended } && envelope.Type == "event-press")
            {
                var bonusDisposition = ApplyBonusPress(run, eventResult, envelope, out var bonusReason);
                if (bonusDisposition != MessageDisposition.Accepted)
                {
                    return RecordRejected(envelope, bonusDisposition, bonusReason, payloadJson);
                }

                device.LastSeenAt = _clock.UtcNow;
                device.LastError = null;
                run.LastAcceptedInputElapsedMs = Math.Max(run.LastAcceptedInputElapsedMs, envelope.ElapsedMilliseconds);
                run.Revision++;
                UpdateDeviceLeds();
                return RecordAccepted(envelope, run, bonusReason, payloadJson);
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
            if (run.AllEventsCompleted && run.Events.All(e => IsButtonEvent(e.Type)))
            {
                if (TryStartBonusGame(run))
                {
                    reason = "All events completed with time remaining; the bonus speed round is starting.";
                }
                else
                {
                    MarkFinishedUnrecorded(run);
                    reason = "All events completed; run finished and is awaiting recording.";
                }
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

    // ---------------- Bonus speed round ----------------
    // When the last event finishes with time left, the clock keeps running and a speed round
    // starts: buttons are polled while they flash an intro, then one button at a time is lit
    // and must be pressed before its window closes. A miss ends the run there; running out
    // of run time ends it as a timeout. Each hit is worth the edition's points per press,
    // added when the round ends.
    public const long BonusIntroMilliseconds = 1_500;
    // The bonus round's row on scorecards and leaderboards, edited like an event with this id.
    public const string BonusEventId = "bonus-round";
    // Presses are timed when pressed but can arrive a little later over the radio; wait this
    // long past a deadline before calling it a miss.
    public const long BonusMissGraceMilliseconds = 300;

    private bool TryStartBonusGame(RunRecord run)
    {
        if (run.Edition.BonusGame is not { Enabled: true } settings || run.Status != RunStatus.Active || run.BonusGame is not null ||
            run.ActiveElapsedMs >= run.Edition.DurationLimitSeconds * 1000L)
        {
            return false;
        }

        run.Phase = RunPhase.Bonus;
        run.BonusStartedElapsedMs = run.ActiveElapsedMs;
        run.BonusGame = new BonusGameRecord
        {
            Phase = BonusGamePhase.Intro,
            StartedElapsedMs = run.ActiveElapsedMs,
            IntroEndsElapsedMs = run.ActiveElapsedMs + BonusIntroMilliseconds,
            PointsPerPress = settings.PointsPerPress,
            Sequence = 1
        };
        return true;
    }

    // Advances the bonus round on the run clock: lights the first target once the intro is
    // over and ends the round on a miss. Called several times a second by the host; returns
    // true when something changed that the buttons need to hear about.
    public bool TickBonusGame()
    {
        lock (_gate)
        {
            RefreshActiveClock();
            var run = _current;
            if (run is not { Status: RunStatus.Active, BonusGame: { Phase: not BonusGamePhase.Ended } bonus })
            {
                return false;
            }

            var now = run.ActiveElapsedMs;
            if (bonus.Phase == BonusGamePhase.Intro)
            {
                if (now < bonus.IntroEndsElapsedMs)
                {
                    return false;
                }

                bonus.FirstTargetElapsedMs = now;
                CueNextBonusTarget(run, now);
                SoundCueRequested?.Invoke(SoundCue.BonusStart);
                run.Revision++;
                SaveCurrentRun(run);
                return true;
            }

            if (bonus.TargetDeadlineElapsedMs is not long deadline || now <= deadline + BonusMissGraceMilliseconds)
            {
                return false;
            }

            // Missed: the run ends at the moment the window closed.
            run.ActiveElapsedMs = Math.Max(deadline, run.LastAcceptedInputElapsedMs);
            EndBonusGame(run, "miss", run.ActiveElapsedMs);
            MarkFinishedUnrecorded(run);
            run.FinishedAt = _clock.UtcNow;
            run.Revision++;
            _lastDisplayedRun = run;
            UpdateDeviceLeds();
            SaveCurrentRun(run);
            return true;
        }
    }

    // A button that answered the bonus poll during the intro; only answering buttons are lit.
    public void ReceiveBonusPollReply(string runToken, string deviceId)
    {
        lock (_gate)
        {
            var run = _current;
            if (run?.BonusGame is not { Phase: BonusGamePhase.Intro } bonus ||
                !string.Equals(MasterProtocolCodec.GetGarageRunToken(run.Id), runToken, StringComparison.Ordinal))
            {
                return;
            }

            var eventResult = run.Events.SingleOrDefault(e => string.Equals(e.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (eventResult is not null && !bonus.RespondingDeviceIds.Contains(eventResult.DeviceId, StringComparer.OrdinalIgnoreCase))
            {
                bonus.RespondingDeviceIds.Add(eventResult.DeviceId);
            }
        }
    }

    private void CueNextBonusTarget(RunRecord run, long now)
    {
        var bonus = run.BonusGame!;
        var settings = run.Edition.BonusGame ?? new BonusGameSettings();
        var candidates = run.Events
            .Where(e => bonus.RespondingDeviceIds.Count == 0 ||
                bonus.RespondingDeviceIds.Contains(e.DeviceId, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count > 1)
        {
            candidates.RemoveAll(e => e.EventId == bonus.TargetEventId);
        }

        var target = candidates[Random.Shared.Next(candidates.Count)];
        bonus.Phase = BonusGamePhase.Target;
        bonus.Sequence++;
        bonus.TargetEventId = target.EventId;
        bonus.TargetDeviceId = target.DeviceId;
        bonus.TargetStartElapsedMs = now;
        bonus.TargetWindowMs = settings.WindowForBonusElapsed(now - (bonus.FirstTargetElapsedMs ?? now));
    }

    private MessageDisposition ApplyBonusPress(RunRecord run, EventRecord eventResult, InputEnvelope envelope, out string reason)
    {
        var bonus = run.BonusGame!;
        if (bonus.Phase != BonusGamePhase.Target)
        {
            reason = "The bonus round has not lit a button yet.";
            return MessageDisposition.InvalidSignal;
        }

        if (!string.Equals(bonus.TargetEventId, eventResult.EventId, StringComparison.Ordinal))
        {
            reason = $"'{eventResult.Name}' is not the lit bonus button.";
            return MessageDisposition.InvalidSignal;
        }

        var pressedAt = envelope.ElapsedMilliseconds;
        if (pressedAt < bonus.TargetStartElapsedMs)
        {
            reason = "Press came before this bonus button was lit.";
            return MessageDisposition.StaleTimestamp;
        }

        if (pressedAt > bonus.TargetDeadlineElapsedMs)
        {
            reason = "Press came after this bonus button's window closed.";
            return MessageDisposition.InvalidSignal;
        }

        bonus.Hits++;
        reason = $"Bonus hit {bonus.Hits} on '{eventResult.Name}'.";
        CueNextBonusTarget(run, run.ActiveElapsedMs);
        return MessageDisposition.Accepted;
    }

    private static void EndBonusGame(RunRecord run, string reason, long elapsedMs)
    {
        if (run.BonusGame is not { Phase: not BonusGamePhase.Ended } bonus)
        {
            return;
        }

        bonus.Phase = BonusGamePhase.Ended;
        bonus.TargetEventId = null;
        bonus.TargetDeviceId = null;
        bonus.EndReason = reason;
        bonus.EndedElapsedMs = elapsedMs;
        bonus.AwardedPoints = bonus.Hits * bonus.PointsPerPress;
    }

    private void SaveCurrentRun(RunRecord run)
    {
        try
        {
            _store.SaveRuns([run]);
        }
        catch
        {
            ReloadInMemoryAfterPersistenceFailure();
            throw;
        }
    }

    public MasterBonusStatus GetMasterBonusStatus()
    {
        lock (_gate)
        {
            RefreshActiveClock();
            var run = _current;
            var token = MasterProtocolCodec.GetGarageRunToken(run?.Id);
            if (token is null || run is not { Status: RunStatus.Active, BonusGame: { Phase: not BonusGamePhase.Ended } bonus })
            {
                return new MasterBonusStatus(token ?? "-", 0, "OFF", null, 0);
            }

            if (bonus.Phase == BonusGamePhase.Intro)
            {
                return new MasterBonusStatus(token, bonus.Sequence, "INTRO", null,
                    Math.Max(0, bonus.IntroEndsElapsedMs - run.ActiveElapsedMs));
            }

            var remaining = Math.Max(0, (bonus.TargetDeadlineElapsedMs ?? 0) - run.ActiveElapsedMs);
            var physical = MasterProtocolCodec.IsValidDeviceId(bonus.TargetDeviceId) ? bonus.TargetDeviceId!.ToUpperInvariant() : null;
            return new MasterBonusStatus(token, bonus.Sequence, "TARGET", physical, remaining);
        }
    }

    // Deleting hides a saved run from history, leaderboards, and the TV. It is kept with an
    // audit edit so it can be restored. Deleting a recorded replacement puts back the
    // result it had replaced.
    public RunRecord DeleteRun(string runId, int expectedRevision, string? reason = null)
    {
        lock (_gate)
        {
            RefreshActiveClock();
            if (_current?.Id == runId)
            {
                throw new CommandException("The run in progress can't be deleted. Discard it, or finish and record it first.");
            }

            var run = _data.Runs.SingleOrDefault(r => r.Id == runId)
                ?? throw new CommandException("Run was not found.");
            if (run.IsDeleted)
            {
                return Clone(run);
            }
            if (run.Revision != expectedRevision)
            {
                throw new CommandException("This run changed after it was opened. Reload it before deleting.");
            }

            var restoredSource = run.SupersededByRunId is null && run.SupersedesRunId is string sourceId
                ? _data.Runs.SingleOrDefault(r => r.Id == sourceId && r.SupersededByRunId == run.Id && !r.IsDeleted)
                : null;
            var candidate = Clone(run);
            candidate.DeletedAt = _clock.UtcNow;
            candidate.Revision = run.Revision + 1;
            var note = string.IsNullOrWhiteSpace(reason) ? "" : $" Reason: {reason.Trim()}";
            var edit = new EditRecord
            {
                RunId = run.Id,
                CreatedAt = _clock.UtcNow,
                Reason = restoredSource is null
                    ? $"Deleted from history.{note}"
                    : $"Deleted from history; the earlier result it replaced counts again.{note}",
                BeforeJson = Serialize(run),
                AfterJson = Serialize(candidate)
            };

            ReplaceRun(run, candidate);
            if (restoredSource is not null)
            {
                restoredSource.Status = restoredSource.SupersededFromStatus ?? InferStatusBeforeSupersede(restoredSource);
                restoredSource.SupersededByRunId = null;
                restoredSource.SupersededFromStatus = null;
                restoredSource.Revision++;
            }
            try
            {
                _store.AddEdit(candidate, edit, restoredSource is null ? null : [restoredSource]);
            }
            catch
            {
                ReloadInMemoryAfterPersistenceFailure();
                throw;
            }
            _data.Edits.Add(edit);
            if (_lastDisplayedRun?.Id == run.Id)
            {
                _lastDisplayedRun = null;
            }
            UpdateDeviceLeds();
            return Clone(candidate);
        }
    }

    public RunRecord RestoreRun(string runId)
    {
        lock (_gate)
        {
            var run = _data.Runs.SingleOrDefault(r => r.Id == runId)
                ?? throw new CommandException("Run was not found.");
            if (!run.IsDeleted)
            {
                return Clone(run);
            }

            var candidate = Clone(run);
            candidate.DeletedAt = null;
            candidate.Revision = run.Revision + 1;

            // A restored recorded replacement takes its source's place again.
            var resuperseded = candidate.SupersededByRunId is null && candidate.IsRecorded && candidate.SupersedesRunId is string sourceId
                ? _data.Runs.SingleOrDefault(r => r.Id == sourceId && !r.IsDeleted && r.SupersededByRunId is null && r.Status != RunStatus.Superseded)
                : null;
            if (resuperseded is not null && resuperseded.IsCountedOfficial && candidate.Category != RunCategory.Official)
            {
                resuperseded = null;
            }
            if (candidate.IsCountedOfficial)
            {
                var existing = FindAcceptedOfficial(candidate.CompetitorId, candidate.EditionId);
                if (existing is not null && existing.Id != candidate.Id && existing.Id != resuperseded?.Id)
                {
                    throw new CommandException("This competitor already has an official result. Delete that run or change its category before restoring this one.");
                }
            }

            var edit = new EditRecord
            {
                RunId = run.Id,
                CreatedAt = _clock.UtcNow,
                Reason = resuperseded is null ? "Restored from deleted runs." : "Restored from deleted runs; it replaces the earlier result again.",
                BeforeJson = Serialize(run),
                AfterJson = Serialize(candidate)
            };
            ReplaceRun(run, candidate);
            if (resuperseded is not null)
            {
                resuperseded.SupersededFromStatus = resuperseded.Status;
                resuperseded.Status = RunStatus.Superseded;
                resuperseded.SupersededByRunId = candidate.Id;
                resuperseded.Revision++;
            }
            try
            {
                _store.AddEdit(candidate, edit, resuperseded is null ? null : [resuperseded]);
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

    // Databases from before the superseded-from status was stored: recorded runs were
    // completed or timed out; unrecorded superseded runs were discarded or timed out.
    private static RunStatus InferStatusBeforeSupersede(RunRecord run)
    {
        var ranOutOfTime = run.ActiveElapsedMs >= run.Edition.DurationLimitSeconds * 1000L;
        return run.RecordedAt is not null
            ? ranOutOfTime ? RunStatus.TimedOut : RunStatus.Completed
            : ranOutOfTime ? RunStatus.TimedOut : RunStatus.Aborted;
    }

    // Saves scorecard edits to any run, live or not. For the run on screen (live, or just
    // finished or timed out) the edit applies to the latest state: it carries only the
    // fields the operator changed, and presses or a timeout that land while they type must
    // not make the save fail. Other saved runs still require the revision they opened.
    public RunRecord EditRun(string runId, EditRunRequest request)
    {
        lock (_gate)
        {
            RefreshActiveClock();
            if (_current?.Id == runId)
            {
                request.ExpectedRevision = _current.Revision;
                return EditCurrentRun(request);
            }
            if (_lastDisplayedRun?.Id == runId)
            {
                request.ExpectedRevision = _lastDisplayedRun.Revision;
            }
            return EditHistoricalRun(runId, request);
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
            if (candidate.Status == RunStatus.Completed)
            {
                candidate.RecordedAt ??= _clock.UtcNow;
            }
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
            ApplyLiveStatusTransition(run, candidate);
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
            if (run.Status != RunStatus.Active && candidate.Status == RunStatus.Active) RestartClockAnchor();
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
            ApplyLiveStatusTransition(run, candidate);
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
            if (run.Status != RunStatus.Active && candidate.Status == RunStatus.Active) RestartClockAnchor();
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
                eventResult.Score = CalculateScore(eventResult, run.Edition);
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
                eventResult.Keypad = new KeypadProgress();
                DrawKeypadChallenge(run, eventResult, envelope.ElapsedMilliseconds);
                _keypadEntries.Remove(eventResult.EventId);
                reason = "Keypad prompt started; a valid response is required.";
                return MessageDisposition.Accepted;
            }

            if (envelope.Type == "keypad-incorrect" && eventResult.Status == EventStatus.Active)
            {
                NoteKeypadWrong(run, eventResult);
                reason = "Incorrect keypad response recorded; event remains active.";
                return MessageDisposition.Accepted;
            }

            if (envelope.Type is "keypad-response" or "keypad-success" && eventResult.Status == EventStatus.Active)
            {
                var current = EnsureCurrentKeypadChallenge(run, eventResult, eventResult.StartElapsedMs ?? envelope.ElapsedMilliseconds);
                if (envelope.Type == "keypad-response")
                {
                    var answer = envelope.Payload.ValueKind == JsonValueKind.Object && envelope.Payload.TryGetProperty("answer", out var answerProperty) &&
                        answerProperty.ValueKind == JsonValueKind.String
                        ? answerProperty.GetString()
                        : null;
                    if (current is null || !KeypadAnswersMatch(answer, current.Answer))
                    {
                        NoteKeypadWrong(run, eventResult);
                        reason = "Incorrect keypad response recorded; event remains active.";
                        return MessageDisposition.Accepted;
                    }
                }

                if (eventResult.StartElapsedMs is not long start || envelope.ElapsedMilliseconds < start)
                {
                    reason = "Keypad completion timestamp precedes its prompt start.";
                    return MessageDisposition.StaleTimestamp;
                }

                reason = SolveKeypadChallenge(run, eventResult, current, envelope,
                    envelope.MessageId.StartsWith("virtual-keypad-override", StringComparison.Ordinal));
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
            eventResult.Score = CalculateScore(eventResult, run.Edition);
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

    // '*' is the physical keypad's Enter key, so a configured code written as "D5*" matches
    // the typed "D5". Letters compare case-insensitively.
    private static bool KeypadAnswersMatch(string? answer, string? expected)
    {
        static string Normalize(string? value) => (value ?? "").Trim().TrimEnd('*').Trim();
        var normalizedExpected = Normalize(expected);
        return normalizedExpected.Length > 0 &&
            string.Equals(Normalize(answer), normalizedExpected, StringComparison.OrdinalIgnoreCase);
    }

    private static int RequiredKeypadSuccesses(RunRecord run, EventRecord eventResult) =>
        Math.Max(1, run.Edition.Events.SingleOrDefault(e => e.EventId == eventResult.EventId)?.RequiredSuccesses ?? 1);

    // The run's snapshot of the keypad answer file, or, when it had none, the event's fixed
    // prompt/answer (older setups and tests).
    private static IReadOnlyList<KeypadChallengeDefinition> KeypadPoolFor(RunRecord run, EventRecord eventResult)
    {
        if (run.Edition.KeypadChallenges is { Count: > 0 } pool)
        {
            return pool;
        }

        var definition = run.Edition.Events.SingleOrDefault(e => e.EventId == eventResult.EventId);
        return string.IsNullOrWhiteSpace(definition?.Answer)
            ? []
            : [new KeypadChallengeDefinition { Prompt = definition.Prompt ?? "", Answer = definition.Answer }];
    }

    // Picks a random message no keypad event in this run has shown yet. Only if every
    // message has been used does a repeat become possible (never the one just shown).
    private KeypadAttemptRecord? DrawKeypadChallenge(RunRecord run, EventRecord eventResult, long elapsedMs)
    {
        var pool = KeypadPoolFor(run, eventResult);
        if (pool.Count == 0)
        {
            eventResult.Prompt = null;
            return null;
        }

        var progress = eventResult.Keypad ??= new KeypadProgress();
        var shown = run.Events
            .SelectMany(e => e.Keypad?.Challenges ?? [])
            .Select(challenge => challenge.Prompt)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = pool.Where(challenge => !shown.Contains(challenge.Prompt)).ToList();
        if (candidates.Count == 0)
        {
            var last = progress.Challenges.LastOrDefault()?.Prompt;
            candidates = pool.Where(challenge => !string.Equals(challenge.Prompt, last, StringComparison.OrdinalIgnoreCase)).ToList();
            if (candidates.Count == 0) candidates = pool.ToList();
        }

        var chosen = candidates[Random.Shared.Next(candidates.Count)];
        var attempt = new KeypadAttemptRecord { Prompt = chosen.Prompt, Answer = chosen.Answer, DrawnElapsedMs = elapsedMs };
        progress.Challenges.Add(attempt);
        eventResult.Prompt = attempt.Prompt;
        SoundCueRequested?.Invoke(SoundCue.KeypadMessage);
        return attempt;
    }

    private KeypadAttemptRecord? EnsureCurrentKeypadChallenge(RunRecord run, EventRecord eventResult, long elapsedMs) =>
        eventResult.Keypad?.Current ?? DrawKeypadChallenge(run, eventResult, elapsedMs);

    // Credits the message on screen. The event finishes once the required number is solved;
    // otherwise the next message is drawn.
    private string SolveKeypadChallenge(RunRecord run, EventRecord eventResult, KeypadAttemptRecord? current,
        InputEnvelope envelope, bool operatorOverride)
    {
        var elapsed = envelope.ElapsedMilliseconds;
        var progress = eventResult.Keypad ??= new KeypadProgress();
        if (current is null)
        {
            // No message pool at all: record the credit so undo and the count still work.
            current = new KeypadAttemptRecord { Prompt = "", Answer = "", DrawnElapsedMs = elapsed };
            progress.Challenges.Add(current);
        }

        current.SolvedElapsedMs = elapsed;
        current.SolvedByMessageId = envelope.MessageId;
        var entry = KeypadEntryFor(run, eventResult);
        entry.Entry = "";
        entry.WrongAtMonotonicMs = null;
        var solved = progress.SolvedCount;
        var required = RequiredKeypadSuccesses(run, eventResult);
        var who = operatorOverride ? "Operator credited" : "Correct code for";
        if (solved >= required)
        {
            eventResult.FinishElapsedMs = elapsed;
            eventResult.Status = EventStatus.Completed;
            eventResult.Score = CalculateScore(eventResult, run.Edition);
            _keypadEntries.Remove(eventResult.EventId);
            return required == 1
                ? $"{who} '{current.Prompt}'; the keypad event is complete."
                : $"{who} '{current.Prompt}' ({solved} of {required}); the keypad event is complete.";
        }

        entry.CorrectAtMonotonicMs = _clock.MonotonicMilliseconds;
        var next = DrawKeypadChallenge(run, eventResult, elapsed);
        return $"{who} '{current.Prompt}' ({solved} of {required}); next message '{next?.Prompt}'.";
    }

    private void NoteKeypadWrong(RunRecord run, EventRecord eventResult)
    {
        var entry = KeypadEntryFor(run, eventResult);
        entry.Entry = "";
        entry.WrongAtMonotonicMs = _clock.MonotonicMilliseconds;
        entry.CorrectAtMonotonicMs = null;
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

    // Reads advance the in-memory clock only; the checkpoint service and state-changing
    // commands persist it, so polling never turns into a synchronous disk write.
    private void RefreshActiveClock(bool persist = false)
    {
        if (_current is null || _current.Status != RunStatus.Active)
        {
            return;
        }

        var now = _clock.MonotonicMilliseconds;
        var delta = Math.Max(0, now - _clockAnchorMilliseconds);
        if (delta == 0 && !persist)
        {
            return;
        }

        _clockAnchorMilliseconds = now;
        _current.ActiveElapsedMs += delta;
        if (_current.ActiveElapsedMs >= _current.Edition.DurationLimitSeconds * 1000L)
        {
            TimeoutCurrent();
        }
        else if (persist)
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

    private void RestartClockAnchor()
    {
        _clockAnchorMilliseconds = _clock.MonotonicMilliseconds;
        _activeSegmentStartElapsedMs = _current?.ActiveElapsedMs ?? 0;
    }

    private void TimeoutCurrent()
    {
        if (_current is null)
        {
            return;
        }

        _current.ActiveElapsedMs = _current.Edition.DurationLimitSeconds * 1000L;
        EndBonusGame(_current, "timeout", _current.ActiveElapsedMs);
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
        _lastDisplayedRun = _current;
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
        if (request.ReopenRunClock)
        {
            ReopenRun(candidate);
        }

        if (request.DurationLimitSeconds is int durationLimit)
        {
            ValidateRunDuration(durationLimit);
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
        // Manual points and the run bonus may be negative (penalties); they subtract from the total.
        if (request.BonusPointsOverride is int bonusPoints)
        {
            if (bonusPoints is < -MaximumManualPoints or > MaximumManualPoints)
            {
                throw new CommandException($"The general run bonus must be between -{MaximumManualPoints:N0} and {MaximumManualPoints:N0}.");
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
            if (edit.EventId == BonusEventId)
            {
                ApplyBonusEdit(candidate, edit);
                continue;
            }

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
                if (scoreOverride is < -MaximumManualPoints or > MaximumManualPoints)
                {
                    throw new CommandException($"Points for '{result.Name}' must be between -{MaximumManualPoints:N0} and {MaximumManualPoints:N0}.");
                }
                result.ScoreOverride = scoreOverride;
            }
            if (edit.ClearScoreOverride)
            {
                result.ScoreOverride = null;
            }
            if (edit.ClearMeasurementJson)
            {
                result.MeasurementJson = null;
            }
            if (edit.MeasurementJson is not null)
            {
                result.MeasurementJson = edit.MeasurementJson;
            }
            if (edit.ClearNotes)
            {
                result.Notes = null;
            }
            if (edit.Notes is not null)
            {
                result.Notes = edit.Notes;
            }
            if (edit.ClearStartElapsedMs || edit.ClearFinishElapsedMs)
            {
                result.LastSignalElapsedMs = null;
            }
            result.Score = CalculateScore(result, candidate.Edition);
            NormalizeKeypadProgress(candidate, result);
        }
    }

    // Scorecard corrections change a keypad event's status directly. Keep its messages
    // consistent: a reset event forgets them, and a running one always has one on screen.
    private void NormalizeKeypadProgress(RunRecord run, EventRecord eventResult)
    {
        if (eventResult.Type != EventKind.Keypad)
        {
            return;
        }

        if (eventResult.Status == EventStatus.Pending)
        {
            eventResult.Keypad = null;
            eventResult.Prompt = null;
            _keypadEntries.Remove(eventResult.EventId);
        }
        else if (eventResult.Status == EventStatus.Active && run.Status is not (RunStatus.Completed or RunStatus.TimedOut
                     or RunStatus.Aborted or RunStatus.Superseded))
        {
            EnsureCurrentKeypadChallenge(run, eventResult, eventResult.StartElapsedMs ?? run.ActiveElapsedMs);
        }
    }

    private void ValidateHistoricalCandidate(RunRecord candidate)
    {
        ValidateBonusTimes(candidate);
        // Any unfinished status would make startup find a second current run and refuse to boot.
        if (candidate.Status is RunStatus.Active or RunStatus.Armed or RunStatus.Paused or RunStatus.Countdown or RunStatus.Finished)
        {
            throw new CommandException("Historical edits cannot make a run live, armed, counting down, or awaiting recording.");
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
            result.Score = CalculateScore(result, candidate.Edition);
        }
    }

    private static bool IsStoppedHistoricalStatus(RunStatus status) =>
        status is RunStatus.Finished or RunStatus.Completed or RunStatus.TimedOut or RunStatus.Aborted or RunStatus.Superseded;

    // The bonus round is corrected like an event on the scorecard: its start and end times,
    // its hits (points = hits x points per press), and a points override. Clearing it
    // (status Pending) removes the round's result; entering times or hits for a run that
    // never reached it records one.
    private static void ApplyBonusEdit(RunRecord candidate, EventEditRequest edit)
    {
        if (candidate.BonusGame is { Phase: not BonusGamePhase.Ended })
        {
            throw new CommandException("The bonus round can be corrected once it has ended.");
        }

        if (edit.Status == EventStatus.Pending)
        {
            candidate.BonusGame = null;
            return;
        }

        if (edit.ClearStartElapsedMs)
        {
            throw new CommandException("The bonus round needs a start time; use Clear to remove its result instead.");
        }

        var bonus = candidate.BonusGame;
        var start = edit.StartElapsedMs ?? bonus?.StartedElapsedMs
            ?? throw new CommandException("Enter the bonus round's start time.");
        var finish = edit.ClearFinishElapsedMs ? null : edit.FinishElapsedMs ?? bonus?.EndedElapsedMs;
        if (edit.DurationMs is long duration)
        {
            if (duration < 0)
            {
                throw new CommandException("The bonus round's duration must not be negative.");
            }
            finish = start + duration;
        }
        if (start < 0 || finish < start)
        {
            throw new CommandException("The bonus round's end must not be before its start.");
        }
        if (edit.Hits is < 0 or > BonusGameSettings.MaximumPointsPerPress)
        {
            throw new CommandException($"Bonus round hits must be a whole number from 0 to {BonusGameSettings.MaximumPointsPerPress:N0}.");
        }
        if (edit.ScoreOverride is < -MaximumManualPoints or > MaximumManualPoints)
        {
            throw new CommandException($"Bonus round points must be between -{MaximumManualPoints:N0} and {MaximumManualPoints:N0}.");
        }

        bonus ??= candidate.BonusGame = new BonusGameRecord
        {
            Phase = BonusGamePhase.Ended,
            EndReason = "operator",
            PointsPerPress = (candidate.Edition.BonusGame ?? new BonusGameSettings()).PointsPerPress
        };
        bonus.StartedElapsedMs = start;
        bonus.IntroEndsElapsedMs = Math.Max(bonus.IntroEndsElapsedMs, start);
        bonus.EndedElapsedMs = finish;
        if (edit.Hits is int hits)
        {
            bonus.Hits = hits;
        }
        if (edit.ScoreOverride is int scoreOverride)
        {
            bonus.ScoreOverride = scoreOverride;
        }
        if (edit.ClearScoreOverride)
        {
            bonus.ScoreOverride = null;
        }
        bonus.AwardedPoints = bonus.Hits * bonus.PointsPerPress;
    }

    private static void ValidateBonusTimes(RunRecord candidate)
    {
        if (candidate.BonusGame is { Phase: BonusGamePhase.Ended } bonus &&
            (bonus.StartedElapsedMs > candidate.ActiveElapsedMs || bonus.EndedElapsedMs > candidate.ActiveElapsedMs))
        {
            throw new CommandException("The bonus round's times must fall within the run's elapsed time.");
        }
    }

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
            .Concat(timedEventIds.Contains(BonusEventId) && candidate.BonusGame is { } bonus
                ? new long?[] { bonus.StartedElapsedMs, bonus.EndedElapsedMs }
                : [])
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
        ValidateBonusTimes(candidate);
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
            result.Score = CalculateScore(result, candidate.Edition);
        }

        if (candidate.AllEventsCompleted && candidate.Events.All(e => IsButtonEvent(e.Type)))
        {
            // A correction made during the bonus round leaves the round running.
            if (candidate.BonusGame is not { Phase: not BonusGamePhase.Ended } ||
                candidate.Status is not (RunStatus.Active or RunStatus.Paused))
            {
                MarkFinishedUnrecorded(candidate);
            }
        }
        else if (candidate.AllEventsCompleted)
        {
            candidate.Phase = RunPhase.Bonus;
            candidate.BonusStartedElapsedMs ??= candidate.ActiveElapsedMs;
        }
        else
        {
            // Reopening an event ends a bonus round in progress (keeping its hits); the
            // round does not start again when the event is finished once more.
            EndBonusGame(candidate, "operator", candidate.ActiveElapsedMs);
            candidate.Phase = RunPhase.Normal;
            candidate.BonusStartedElapsedMs = null;
        }
    }

    // Status changes through a correction must not skip the countdown, and entering
    // Paused needs its resume phase. Entering Active restarts the clock anchor after save.
    private static void ApplyLiveStatusTransition(RunRecord original, RunRecord candidate)
    {
        if ((original.Status == RunStatus.Armed) != (candidate.Status == RunStatus.Armed))
        {
            throw new CommandException("A correction cannot arm a run or skip its countdown; use Start or Discard instead.");
        }
        if (candidate.Status == RunStatus.Paused && original.Status != RunStatus.Paused)
        {
            candidate.PausedFromPhase = candidate.Phase.ToString();
        }
        else if (candidate.Status != RunStatus.Paused)
        {
            candidate.PausedFromPhase = null;
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
        if (replaced is not null && replaced.Id == candidate.SupersedesRunId && !candidate.IsRecorded)
        {
            // An unrecorded replacement displaces its source when it is recorded.
            replaced = null;
            return;
        }
        if (replaced is not null && replaced.Id != original.Id && !replaceExisting)
        {
            throw new CommandException("This correction would create two accepted official runs; explicitly replace the existing result first.");
        }
        if (replaced is not null && replaced.Id != original.Id)
        {
            replaced.SupersededFromStatus = replaced.Status;
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

        var run = _data.Runs.SingleOrDefault(r => r.Id == runId)
            ?? throw new CommandException("Run was not found.");
        RequireNotDeleted(run);
        return run;
    }

    private static void RequireNotDeleted(RunRecord run)
    {
        if (run.IsDeleted)
        {
            throw new CommandException("This run was deleted. Restore it from Deleted runs before changing it.");
        }
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
        _data.DeviceScanCheckedAt = fresh.DeviceScanCheckedAt;
        _current = _data.Runs.SingleOrDefault(r => r.Status is RunStatus.Armed or RunStatus.Countdown or RunStatus.Active or RunStatus.Paused or RunStatus.Finished);
        _lastDisplayedRun = _current ?? _data.Runs.Where(r => !r.IsDeleted).OrderByDescending(r => r.CreatedAt).FirstOrDefault();
        RestartClockAnchor();
    }

    private RunRecord CreateRun(QueueItemRecord queueItem, bool manualOfflineOverride, int? durationLimitSeconds = null)
    {
        // A real run replaces the primed "up next" view.
        _primed = null;
        var snapshot = _edition.ToSnapshot();
        if (durationLimitSeconds is int duration)
        {
            snapshot.DurationLimitSeconds = duration;
        }
        if (snapshot.Events.Any(e => e.Type == EventKind.Keypad) && _keypadChallenges.Challenges.Count > 0)
        {
            snapshot.KeypadChallenges = _keypadChallenges.Challenges
                .Select(challenge => new KeypadChallengeDefinition { Prompt = challenge.Prompt, Answer = challenge.Answer })
                .ToList();
        }
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
                // A keypad event's message is drawn when it starts.
                Prompt = e.Type == EventKind.Keypad ? null : e.Prompt,
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

    private void RequireActiveCompetitor(string competitorId)
    {
        var competitor = _data.Competitors.SingleOrDefault(c => c.Id == competitorId);
        if (competitor is null) throw new CommandException("Competitor was not found.");
        if (competitor.IsArchived) throw new CommandException("This competitor is archived. Restore them before adding a new run.");
    }

    private static string NormalizeCompetitorName(string name) =>
        string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

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
            result.Score = CalculateScore(result, run.Edition);
        }
    }

    private static int CalculateScore(EventRecord result, EditionSnapshot edition)
    {
        var eventSnapshot = edition.Events.Single(eventSnapshot => eventSnapshot.EventId == result.EventId);
        return ScoreCalculator.CalculateForEvent(result, edition.Scoring, eventSnapshot);
    }

    private void MarkFinishedUnrecorded(RunRecord run)
    {
        run.Status = RunStatus.Finished;
        run.FinishedAt ??= _clock.UtcNow;
        run.PausedFromPhase = null;
        run.Phase = RunPhase.Normal;
        run.BonusStartedElapsedMs = null;
    }

    private static void ReopenRun(RunRecord run)
    {
        run.Status = RunStatus.Active;
        run.FinishedAt = null;
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
            .Where(run => run.EditionId == _edition.EditionId &&
                run.SupersededByRunId is null &&
                run.Status is not RunStatus.Aborted and not RunStatus.Superseded && !run.IsDeleted &&
                (run.Category == RunCategory.Official && run.IsCountedOfficial ||
                 run.Category == RunCategory.Playoff && run.IsRecorded ||
                 run.Category == RunCategory.Exhibition && _data.ShowExhibitionsOnLeaderboard && run.IsRecorded))
            .Select(r => new LeaderboardRow
            {
                CompetitorName = competitorNames.GetValueOrDefault(r.CompetitorId, "Unknown competitor"),
                DisplayName = competitorNames.GetValueOrDefault(r.CompetitorId, "Unknown competitor"),
                Points = r.TotalPoints,
                Category = r.Category,
                Status = r.Status,
                RunId = r.Id
            })
            .ToList();

        rows = rows.OrderBy(row => row.Category switch { RunCategory.Playoff => 0, RunCategory.Official => 1, _ => 2 })
            .ThenByDescending(row => row.Points)
            .ThenBy(row => row.CompetitorName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var runById = _data.Runs.ToDictionary(run => run.Id, StringComparer.Ordinal);
        foreach (var exhibitionGroup in rows.Where(row => row.Category == RunCategory.Exhibition)
                     .GroupBy(row => row.CompetitorName, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = exhibitionGroup.OrderBy(row => runById[row.RunId!].CreatedAt).ThenBy(row => row.RunId, StringComparer.Ordinal).ToList();
            if (ordered.Count > 1)
            {
                for (var index = 0; index < ordered.Count; index++)
                {
                    ordered[index].DisplayName = $"{ordered[index].CompetitorName} (Exhibition {index + 1})";
                }
            }
        }

        // Ranks restart within each category (playoff, official, exhibition), since each
        // category is shown as its own standings; tied points share a rank.
        var lastPoints = int.MinValue;
        RunCategory? lastCategory = null;
        var lastRank = 0;
        var categoryStart = 0;
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index].Category != lastCategory)
            {
                categoryStart = index;
                lastCategory = rows[index].Category;
                lastPoints = int.MinValue;
            }
            if (rows[index].Points != lastPoints)
            {
                lastRank = index - categoryStart + 1;
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
    private static string NormalizeDeviceId(string deviceId) => MasterProtocolCodec.IsValidDeviceId(deviceId)
        ? deviceId.ToUpperInvariant()
        : deviceId;

    private static EventDefinition CloneEventDefinition(EventDefinition eventDefinition) => new()
    {
        EventId = eventDefinition.EventId ?? "",
        Name = eventDefinition.Name ?? "",
        DeviceId = eventDefinition.DeviceId ?? "",
        Type = eventDefinition.Type,
        BasePoints = eventDefinition.BasePoints,
        MinimumPoints = eventDefinition.MinimumPoints,
        DecayPoints = eventDefinition.DecayPoints,
        DecayEverySeconds = eventDefinition.DecayEverySeconds,
        GraceSeconds = eventDefinition.GraceSeconds,
        Prompt = eventDefinition.Prompt,
        Answer = eventDefinition.Answer,
        RequiredSuccesses = eventDefinition.RequiredSuccesses
    };

    private EditionSetup ToSetup(EditionDefinition edition) => new()
    {
        EditionId = edition.EditionId,
        Name = edition.Name,
        Events = edition.Events.Select(CloneEventDefinition).ToList(),
        Scoring = edition.Scoring.Clone(),
        BonusGame = (edition.BonusGame ?? new BonusGameSettings()).Clone(),
        KeypadMessageCount = _keypadChallenges.Challenges.Count,
        KeypadMessageSource = _keypadChallenges.Source,
        KeypadMessageError = _keypadChallenges.Error
    };

    private static bool SameEventSetup(IReadOnlyList<EventDefinition> left, IReadOnlyList<EventDefinition> right) =>
        left.Count == right.Count && left.Zip(right).All(pair =>
            string.Equals(pair.First.EventId, pair.Second.EventId, StringComparison.Ordinal) &&
            string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal) &&
            string.Equals(pair.First.DeviceId, pair.Second.DeviceId, StringComparison.OrdinalIgnoreCase) &&
            pair.First.Type == pair.Second.Type &&
            pair.First.BasePoints == pair.Second.BasePoints &&
            pair.First.MinimumPoints == pair.Second.MinimumPoints &&
            pair.First.DecayPoints == pair.Second.DecayPoints &&
            pair.First.DecayEverySeconds == pair.Second.DecayEverySeconds &&
            pair.First.GraceSeconds == pair.Second.GraceSeconds &&
            string.Equals(pair.First.Prompt, pair.Second.Prompt, StringComparison.Ordinal) &&
            string.Equals(pair.First.Answer, pair.Second.Answer, StringComparison.Ordinal) &&
            pair.First.RequiredSuccesses == pair.Second.RequiredSuccesses);

    private static string RequireReason(string reason) => string.IsNullOrWhiteSpace(reason) ? throw new CommandException("A correction reason is required.") : reason.Trim();
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonDefaults.Options);
    private static RunRecord DeserializeRun(string value) => JsonSerializer.Deserialize<RunRecord>(value, JsonDefaults.Options)
        ?? throw new InvalidDataException("Stored run edit snapshot is empty.");

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(Serialize(value), JsonDefaults.Options)
        ?? throw new InvalidDataException("Could not clone state.");
}
