using Microsoft.Data.Sqlite;
using SQLitePCL;
using System.Globalization;
using System.Text.Json;

namespace GarageGames.V2;

public sealed class StoreSnapshot
{
    public List<CompetitorRecord> Competitors { get; } = [];
    public List<QueueItemRecord> Queue { get; } = [];
    public List<DeviceRecord> Devices { get; } = [];
    public List<RunRecord> Runs { get; } = [];
    public List<MessageRecord> Messages { get; } = [];
    public List<EditRecord> Edits { get; } = [];
}

public sealed class RunStore : IDisposable
{
    private const int SchemaVersion = 1;
    private readonly string _connectionString;
    private FileStream? _lifetimeLock;
    private bool _disposed;

    public RunStore(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            throw new ArgumentException("A data directory is required.", nameof(dataDirectory));
        }

        DataDirectory = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(DataDirectory);
        DatabasePath = Path.Combine(DataDirectory, "garage-games-v2.db");
        try
        {
            var lockPath = Path.Combine(DataDirectory, ".garage-games-v2.lock");
            _lifetimeLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Batteries_V2.Init();
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private
            }.ToString();
            Initialize();
        }
        catch (IOException exception)
        {
            _lifetimeLock?.Dispose();
            _lifetimeLock = null;
            throw new InvalidOperationException($"Garage Games v2 data directory is already in use or cannot be locked: '{DataDirectory}'. Close the other instance before starting another.", exception);
        }
        catch
        {
            _lifetimeLock?.Dispose();
            _lifetimeLock = null;
            throw;
        }
    }

    public string DataDirectory { get; }
    public string DatabasePath { get; }

    public void EnsureDevices(EditionDefinition edition)
    {
        ExecuteTransaction((connection, transaction) =>
        {
            foreach (var eventDefinition in edition.Events)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO devices(device_id, event_id, availability, last_seen_at, led, last_error)
                    VALUES($device_id, $event_id, $availability, NULL, $led, NULL)
                    ON CONFLICT(device_id) DO UPDATE SET event_id = excluded.event_id
                    """;
                command.Parameters.AddWithValue("$device_id", eventDefinition.DeviceId);
                command.Parameters.AddWithValue("$event_id", eventDefinition.EventId);
                command.Parameters.AddWithValue("$availability", DeviceAvailability.Online.ToString());
                command.Parameters.AddWithValue("$led", LedState.Ready.ToString());
                command.ExecuteNonQuery();
            }
        });
    }

    public StoreSnapshot Load()
    {
        ThrowIfDisposed();
        var snapshot = new StoreSnapshot();
        using var connection = OpenConnection();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, name, edition_id, created_at FROM competitors ORDER BY created_at, id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                snapshot.Competitors.Add(new CompetitorRecord
                {
                    Id = reader.GetString(0),
                    Name = reader.GetString(1),
                    EditionId = reader.GetString(2),
                    CreatedAt = ParseDate(reader.GetString(3))
                });
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, competitor_id, category, replace_existing_official, replacement_of_run_id, reason, position FROM queue_items ORDER BY position, id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                snapshot.Queue.Add(new QueueItemRecord
                {
                    Id = reader.GetString(0),
                    CompetitorId = reader.GetString(1),
                    Category = ParseEnum<RunCategory>(reader.GetString(2), "queue category"),
                    ReplaceExistingOfficial = reader.GetInt64(3) != 0,
                    ReplacementOfRunId = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Reason = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Position = reader.GetInt32(6)
                });
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT device_id, event_id, availability, last_seen_at, led, last_error FROM devices ORDER BY device_id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                snapshot.Devices.Add(new DeviceRecord
                {
                    DeviceId = reader.GetString(0),
                    EventId = reader.GetString(1),
                    Availability = ParseEnum<DeviceAvailability>(reader.GetString(2), "device availability"),
                    LastSeenAt = reader.IsDBNull(3) ? null : ParseDate(reader.GetString(3)),
                    Led = ParseEnum<LedState>(reader.GetString(4), "device LED state"),
                    LastError = reader.IsDBNull(5) ? null : reader.GetString(5)
                });
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, competitor_id, edition_id, category, status, phase,
                       manual_offline_override, active_elapsed_ms, last_input_elapsed_ms,
                       bonus_started_elapsed_ms, bonus_result_json, bonus_points_override,
                       created_at, started_at, finished_at,
                       supersedes_run_id, superseded_by_run_id, paused_from_phase,
                       notes, revision, edition_snapshot_json
                FROM runs ORDER BY created_at, id
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var run = new RunRecord
                {
                    Id = reader.GetString(0),
                    CompetitorId = reader.GetString(1),
                    EditionId = reader.GetString(2),
                    Category = ParseEnum<RunCategory>(reader.GetString(3), "run category"),
                    Status = ParseEnum<RunStatus>(reader.GetString(4), "run status"),
                    Phase = ParseEnum<RunPhase>(reader.GetString(5), "run phase"),
                    ManualOfflineOverride = reader.GetInt64(6) != 0,
                    ActiveElapsedMs = reader.GetInt64(7),
                    LastAcceptedInputElapsedMs = reader.GetInt64(8),
                    BonusStartedElapsedMs = reader.IsDBNull(9) ? null : reader.GetInt64(9),
                    BonusResultJson = reader.IsDBNull(10) ? null : reader.GetString(10),
                    BonusPointsOverride = reader.IsDBNull(11) ? null : reader.GetInt32(11),
                    CreatedAt = ParseDate(reader.GetString(12)),
                    StartedAt = reader.IsDBNull(13) ? null : ParseDate(reader.GetString(13)),
                    FinishedAt = reader.IsDBNull(14) ? null : ParseDate(reader.GetString(14)),
                    SupersedesRunId = reader.IsDBNull(15) ? null : reader.GetString(15),
                    SupersededByRunId = reader.IsDBNull(16) ? null : reader.GetString(16),
                    PausedFromPhase = reader.IsDBNull(17) ? null : reader.GetString(17),
                    Notes = reader.IsDBNull(18) ? null : reader.GetString(18),
                    Revision = reader.GetInt32(19),
                    Edition = Deserialize<EditionSnapshot>(reader.GetString(20), "edition snapshot"),
                    Events = []
                };
                snapshot.Runs.Add(run);
            }
        }

        foreach (var run in snapshot.Runs)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT event_id, name, device_id, type, prompt, status, start_elapsed_ms,
                       finish_elapsed_ms, score, score_override, measurement_json, notes,
                       last_signal_elapsed_ms
                FROM run_events WHERE run_id = $run_id ORDER BY event_order
                """;
            command.Parameters.AddWithValue("$run_id", run.Id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                run.Events.Add(new EventRecord
                {
                    EventId = reader.GetString(0),
                    Name = reader.GetString(1),
                    DeviceId = reader.GetString(2),
                    Type = ParseEnum<EventKind>(reader.GetString(3), "event type"),
                    Prompt = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Status = ParseEnum<EventStatus>(reader.GetString(5), "event status"),
                    StartElapsedMs = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    FinishElapsedMs = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                    Score = reader.GetInt32(8),
                    ScoreOverride = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    MeasurementJson = reader.IsDBNull(10) ? null : reader.GetString(10),
                    Notes = reader.IsDBNull(11) ? null : reader.GetString(11),
                    LastSignalElapsedMs = reader.IsDBNull(12) ? null : reader.GetInt64(12)
                });
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, message_id, run_id, session_id, device_id, type,
                       elapsed_ms, payload_json, disposition, reason, received_at
                FROM messages ORDER BY id
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                snapshot.Messages.Add(new MessageRecord
                {
                    Id = reader.GetInt64(0),
                    MessageId = reader.GetString(1),
                    RunId = reader.IsDBNull(2) ? null : reader.GetString(2),
                    SessionId = reader.IsDBNull(3) ? null : reader.GetString(3),
                    DeviceId = reader.GetString(4),
                    Type = reader.GetString(5),
                    ElapsedMilliseconds = reader.GetInt64(6),
                    PayloadJson = reader.IsDBNull(7) ? null : reader.GetString(7),
                    Disposition = ParseEnum<MessageDisposition>(reader.GetString(8), "message disposition"),
                    Reason = reader.IsDBNull(9) ? null : reader.GetString(9),
                    ReceivedAt = ParseDate(reader.GetString(10))
                });
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, run_id, created_at, reason, before_json, after_json, undone_edit_id FROM edits ORDER BY id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                snapshot.Edits.Add(new EditRecord
                {
                    Id = reader.GetInt64(0),
                    RunId = reader.GetString(1),
                    CreatedAt = ParseDate(reader.GetString(2)),
                    Reason = reader.GetString(3),
                    BeforeJson = reader.GetString(4),
                    AfterJson = reader.GetString(5),
                    UndoneEditId = reader.IsDBNull(6) ? null : reader.GetInt64(6)
                });
            }
        }

        return snapshot;
    }

    public void AddCompetitor(CompetitorRecord competitor)
    {
        ExecuteTransaction((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO competitors(id, name, edition_id, created_at) VALUES($id, $name, $edition_id, $created_at)";
            command.Parameters.AddWithValue("$id", competitor.Id);
            command.Parameters.AddWithValue("$name", competitor.Name);
            command.Parameters.AddWithValue("$edition_id", competitor.EditionId);
            command.Parameters.AddWithValue("$created_at", FormatDate(competitor.CreatedAt));
            command.ExecuteNonQuery();
        });
    }

    public void SaveQueue(IReadOnlyCollection<QueueItemRecord> queue)
    {
        ExecuteTransaction((connection, transaction) =>
        {
            using var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM queue_items";
            clear.ExecuteNonQuery();
            foreach (var item in queue.OrderBy(q => q.Position))
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO queue_items(id, competitor_id, category, replace_existing_official, replacement_of_run_id, reason, position) VALUES($id, $competitor_id, $category, $replace, $replacement, $reason, $position)";
                command.Parameters.AddWithValue("$id", item.Id);
                command.Parameters.AddWithValue("$competitor_id", item.CompetitorId);
                command.Parameters.AddWithValue("$category", item.Category.ToString());
                command.Parameters.AddWithValue("$replace", item.ReplaceExistingOfficial ? 1 : 0);
                command.Parameters.AddWithValue("$replacement", ValueOrNull(item.ReplacementOfRunId));
                command.Parameters.AddWithValue("$reason", ValueOrNull(item.Reason));
                command.Parameters.AddWithValue("$position", item.Position);
                command.ExecuteNonQuery();
            }
        });
    }

    public void SetDevice(DeviceRecord device)
    {
        ExecuteTransaction((connection, transaction) => UpsertDevice(connection, transaction, device));
    }

    public void SaveRuns(IEnumerable<RunRecord> runs, MessageRecord? message = null)
    {
        ExecuteTransaction((connection, transaction) =>
        {
            foreach (var run in runs.OrderBy(r => r.SupersedesRunId is null ? 1 : 0))
            {
                UpsertRun(connection, transaction, run);
            }

            if (message is not null)
            {
                message.Id = InsertMessage(connection, transaction, message);
            }
        });
    }

    public void SaveRunAndMessage(RunRecord run, MessageRecord message) => SaveRuns([run], message);

    public void SaveRunsAndQueue(IEnumerable<RunRecord> runs, IReadOnlyCollection<QueueItemRecord> queue)
    {
        ExecuteTransaction((connection, transaction) =>
        {
            foreach (var run in runs.OrderBy(r => r.SupersedesRunId is null ? 1 : 0))
            {
                UpsertRun(connection, transaction, run);
            }

            using var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM queue_items";
            clear.ExecuteNonQuery();
            foreach (var item in queue.OrderBy(q => q.Position))
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO queue_items(id, competitor_id, category, replace_existing_official, replacement_of_run_id, reason, position) VALUES($id, $competitor_id, $category, $replace, $replacement, $reason, $position)";
                command.Parameters.AddWithValue("$id", item.Id);
                command.Parameters.AddWithValue("$competitor_id", item.CompetitorId);
                command.Parameters.AddWithValue("$category", item.Category.ToString());
                command.Parameters.AddWithValue("$replace", item.ReplaceExistingOfficial ? 1 : 0);
                command.Parameters.AddWithValue("$replacement", ValueOrNull(item.ReplacementOfRunId));
                command.Parameters.AddWithValue("$reason", ValueOrNull(item.Reason));
                command.Parameters.AddWithValue("$position", item.Position);
                command.ExecuteNonQuery();
            }
        });
    }

    public bool HasMessage(string runId, string messageId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM messages WHERE run_id = $run_id AND message_id = $message_id LIMIT 1";
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$message_id", messageId);
        return command.ExecuteScalar() is not null;
    }

    public void AddEdit(RunRecord run, EditRecord edit, IEnumerable<RunRecord>? additionalRuns = null)
    {
        ExecuteTransaction((connection, transaction) =>
        {
            var runs = (additionalRuns ?? []).Append(run)
                .OrderBy(r => r.SupersedesRunId is null ? 1 : 0);
            foreach (var runToSave in runs)
            {
                UpsertRun(connection, transaction, runToSave);
            }
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO edits(run_id, created_at, reason, before_json, after_json, undone_edit_id) VALUES($run_id, $created_at, $reason, $before, $after, $undone)";
            command.Parameters.AddWithValue("$run_id", edit.RunId);
            command.Parameters.AddWithValue("$created_at", FormatDate(edit.CreatedAt));
            command.Parameters.AddWithValue("$reason", edit.Reason);
            command.Parameters.AddWithValue("$before", edit.BeforeJson);
            command.Parameters.AddWithValue("$after", edit.AfterJson);
            command.Parameters.AddWithValue("$undone", ValueOrNull(edit.UndoneEditId));
            command.ExecuteNonQuery();
            using var idCommand = connection.CreateCommand();
            idCommand.Transaction = transaction;
            idCommand.CommandText = "SELECT last_insert_rowid()";
            edit.Id = Convert.ToInt64(idCommand.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        });
    }

    public string CreateBackup()
    {
        ThrowIfDisposed();
        var backupDirectory = Path.Combine(DataDirectory, "backups");
        Directory.CreateDirectory(backupDirectory);
        var path = Path.Combine(backupDirectory, $"garage-games-v2-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.db");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "VACUUM INTO $path";
        command.Parameters.AddWithValue("$path", path);
        command.ExecuteNonQuery();
        return path;
    }

    public void ExecuteTransaction(Action<SqliteConnection, SqliteTransaction> action)
    {
        ThrowIfDisposed();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        action(connection, transaction);
        transaction.Commit();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeLock?.Dispose();
        _lifetimeLock = null;
    }

    internal static void UpsertRun(SqliteConnection connection, SqliteTransaction transaction, RunRecord run)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO runs(id, competitor_id, edition_id, category, status, phase,
                manual_offline_override, active_elapsed_ms, last_input_elapsed_ms,
                bonus_started_elapsed_ms, bonus_result_json, bonus_points_override,
                created_at, started_at, finished_at,
                supersedes_run_id, superseded_by_run_id, paused_from_phase, notes,
                revision, edition_snapshot_json)
            VALUES($id, $competitor_id, $edition_id, $category, $status, $phase,
                $manual_override, $active_elapsed, $last_input, $bonus_started,
                $bonus_result, $bonus_points_override,
                $created_at, $started_at, $finished_at, $supersedes, $superseded_by,
                $paused_from, $notes, $revision, $edition)
            ON CONFLICT(id) DO UPDATE SET
                competitor_id = excluded.competitor_id,
                edition_id = excluded.edition_id,
                category = excluded.category,
                status = excluded.status,
                phase = excluded.phase,
                manual_offline_override = excluded.manual_offline_override,
                active_elapsed_ms = excluded.active_elapsed_ms,
                last_input_elapsed_ms = excluded.last_input_elapsed_ms,
                bonus_started_elapsed_ms = excluded.bonus_started_elapsed_ms,
                bonus_result_json = excluded.bonus_result_json,
                bonus_points_override = excluded.bonus_points_override,
                created_at = excluded.created_at,
                started_at = excluded.started_at,
                finished_at = excluded.finished_at,
                supersedes_run_id = excluded.supersedes_run_id,
                superseded_by_run_id = excluded.superseded_by_run_id,
                paused_from_phase = excluded.paused_from_phase,
                notes = excluded.notes,
                revision = excluded.revision,
                edition_snapshot_json = excluded.edition_snapshot_json
            """;
        AddRunParameters(command, run);
        command.ExecuteNonQuery();

        foreach (var (eventResult, order) in run.Events.Select((value, index) => (value, index)))
        {
            using var eventCommand = connection.CreateCommand();
            eventCommand.Transaction = transaction;
            eventCommand.CommandText = """
                INSERT INTO run_events(run_id, event_id, event_order, name, device_id, type,
                    prompt, status, start_elapsed_ms, finish_elapsed_ms, score,
                    score_override, measurement_json, notes, last_signal_elapsed_ms)
                VALUES($run_id, $event_id, $event_order, $name, $device_id, $type,
                    $prompt, $status, $start, $finish, $score, $override, $measurement, $notes, $last_signal)
                ON CONFLICT(run_id, event_id) DO UPDATE SET
                    event_order = excluded.event_order,
                    name = excluded.name,
                    device_id = excluded.device_id,
                    type = excluded.type,
                    prompt = excluded.prompt,
                    status = excluded.status,
                    start_elapsed_ms = excluded.start_elapsed_ms,
                    finish_elapsed_ms = excluded.finish_elapsed_ms,
                    score = excluded.score,
                    score_override = excluded.score_override,
                    measurement_json = excluded.measurement_json,
                    notes = excluded.notes,
                    last_signal_elapsed_ms = excluded.last_signal_elapsed_ms
                """;
            eventCommand.Parameters.AddWithValue("$run_id", run.Id);
            eventCommand.Parameters.AddWithValue("$event_id", eventResult.EventId);
            eventCommand.Parameters.AddWithValue("$event_order", order);
            eventCommand.Parameters.AddWithValue("$name", eventResult.Name);
            eventCommand.Parameters.AddWithValue("$device_id", eventResult.DeviceId);
            eventCommand.Parameters.AddWithValue("$type", eventResult.Type.ToString());
            eventCommand.Parameters.AddWithValue("$prompt", ValueOrNull(eventResult.Prompt));
            eventCommand.Parameters.AddWithValue("$status", eventResult.Status.ToString());
            eventCommand.Parameters.AddWithValue("$start", ValueOrNull(eventResult.StartElapsedMs));
            eventCommand.Parameters.AddWithValue("$finish", ValueOrNull(eventResult.FinishElapsedMs));
            eventCommand.Parameters.AddWithValue("$score", eventResult.Score);
            eventCommand.Parameters.AddWithValue("$override", ValueOrNull(eventResult.ScoreOverride));
            eventCommand.Parameters.AddWithValue("$measurement", ValueOrNull(eventResult.MeasurementJson));
            eventCommand.Parameters.AddWithValue("$notes", ValueOrNull(eventResult.Notes));
            eventCommand.Parameters.AddWithValue("$last_signal", ValueOrNull(eventResult.LastSignalElapsedMs));
            eventCommand.ExecuteNonQuery();
        }
    }

    private static void UpsertDevice(SqliteConnection connection, SqliteTransaction transaction, DeviceRecord device)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO devices(device_id, event_id, availability, last_seen_at, led, last_error)
            VALUES($device_id, $event_id, $availability, $last_seen, $led, $last_error)
            ON CONFLICT(device_id) DO UPDATE SET
                event_id = excluded.event_id,
                availability = excluded.availability,
                last_seen_at = excluded.last_seen_at,
                led = excluded.led,
                last_error = excluded.last_error
            """;
        command.Parameters.AddWithValue("$device_id", device.DeviceId);
        command.Parameters.AddWithValue("$event_id", device.EventId);
        command.Parameters.AddWithValue("$availability", device.Availability.ToString());
        command.Parameters.AddWithValue("$last_seen", ValueOrNull(device.LastSeenAt is null ? null : FormatDate(device.LastSeenAt.Value)));
        command.Parameters.AddWithValue("$led", device.Led.ToString());
        command.Parameters.AddWithValue("$last_error", ValueOrNull(device.LastError));
        command.ExecuteNonQuery();
    }

    private static long InsertMessage(SqliteConnection connection, SqliteTransaction transaction, MessageRecord message)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO messages(message_id, run_id, session_id, device_id, type, elapsed_ms, payload_json, disposition, reason, received_at) VALUES($message_id, $run_id, $session_id, $device_id, $type, $elapsed, $payload, $disposition, $reason, $received_at)";
        command.Parameters.AddWithValue("$message_id", message.MessageId);
        command.Parameters.AddWithValue("$run_id", ValueOrNull(message.RunId));
        command.Parameters.AddWithValue("$session_id", ValueOrNull(message.SessionId));
        command.Parameters.AddWithValue("$device_id", message.DeviceId);
        command.Parameters.AddWithValue("$type", message.Type);
        command.Parameters.AddWithValue("$elapsed", message.ElapsedMilliseconds);
        command.Parameters.AddWithValue("$payload", ValueOrNull(message.PayloadJson));
        command.Parameters.AddWithValue("$disposition", message.Disposition.ToString());
        command.Parameters.AddWithValue("$reason", ValueOrNull(message.Reason));
        command.Parameters.AddWithValue("$received_at", FormatDate(message.ReceivedAt));
        command.ExecuteNonQuery();
        using var idCommand = connection.CreateCommand();
        idCommand.Transaction = transaction;
        idCommand.CommandText = "SELECT last_insert_rowid()";
        return Convert.ToInt64(idCommand.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        var hasMeta = false;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'meta'";
            hasMeta = command.ExecuteScalar() is not null;
        }

        if (!hasMeta)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
                if (Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0)
                {
                    throw new InvalidDataException("The data directory contains a nonempty database without a Garage Games v2 schema. The database was not changed.");
                }
            }

            using var transaction = connection.BeginTransaction();
            CreateSchema(connection, transaction);
            transaction.Commit();
            return;
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT value FROM meta WHERE key = 'schema_version'";
            var versionValue = command.ExecuteScalar() as string;
            if (!int.TryParse(versionValue, CultureInfo.InvariantCulture, out var version) || version != SchemaVersion)
            {
                throw new InvalidDataException($"Unsupported Garage Games v2 database schema version '{versionValue ?? "missing"}'. The database was not changed.");
            }
        }

        var requiredTables = new[] { "competitors", "queue_items", "devices", "runs", "run_events", "messages", "edits" };
        foreach (var table in requiredTables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
            command.Parameters.AddWithValue("$name", table);
            if (command.ExecuteScalar() is null)
            {
                throw new InvalidDataException($"Garage Games v2 database is missing required table '{table}'. The database was not changed.");
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA integrity_check";
            if (!string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Garage Games v2 database integrity check failed. The database was not reset.");
            }
        }
    }

    private static void CreateSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        var statements = new[]
        {
            "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL)",
            "INSERT INTO meta(key, value) VALUES('schema_version', '1')",
            "CREATE TABLE competitors(id TEXT PRIMARY KEY, name TEXT NOT NULL, edition_id TEXT NOT NULL, created_at TEXT NOT NULL)",
            "CREATE TABLE queue_items(id TEXT PRIMARY KEY, competitor_id TEXT NOT NULL REFERENCES competitors(id), category TEXT NOT NULL, replace_existing_official INTEGER NOT NULL, replacement_of_run_id TEXT NULL REFERENCES runs(id), reason TEXT NULL, position INTEGER NOT NULL)",
            "CREATE TABLE devices(device_id TEXT PRIMARY KEY, event_id TEXT NOT NULL, availability TEXT NOT NULL, last_seen_at TEXT NULL, led TEXT NOT NULL, last_error TEXT NULL)",
            "CREATE TABLE runs(id TEXT PRIMARY KEY, competitor_id TEXT NOT NULL REFERENCES competitors(id), edition_id TEXT NOT NULL, category TEXT NOT NULL, status TEXT NOT NULL, phase TEXT NOT NULL, manual_offline_override INTEGER NOT NULL, active_elapsed_ms INTEGER NOT NULL, last_input_elapsed_ms INTEGER NOT NULL, bonus_started_elapsed_ms INTEGER NULL, bonus_result_json TEXT NULL, bonus_points_override INTEGER NULL, created_at TEXT NOT NULL, started_at TEXT NULL, finished_at TEXT NULL, supersedes_run_id TEXT NULL REFERENCES runs(id), superseded_by_run_id TEXT NULL REFERENCES runs(id), paused_from_phase TEXT NULL, notes TEXT NULL, revision INTEGER NOT NULL, edition_snapshot_json TEXT NOT NULL)",
            "CREATE TABLE run_events(run_id TEXT NOT NULL REFERENCES runs(id) ON DELETE CASCADE, event_id TEXT NOT NULL, event_order INTEGER NOT NULL, name TEXT NOT NULL, device_id TEXT NOT NULL, type TEXT NOT NULL, prompt TEXT NULL, status TEXT NOT NULL, start_elapsed_ms INTEGER NULL, finish_elapsed_ms INTEGER NULL, score INTEGER NOT NULL, score_override INTEGER NULL, measurement_json TEXT NULL, notes TEXT NULL, last_signal_elapsed_ms INTEGER NULL, PRIMARY KEY(run_id, event_id))",
            "CREATE TABLE messages(id INTEGER PRIMARY KEY AUTOINCREMENT, message_id TEXT NOT NULL, run_id TEXT NULL, session_id TEXT NULL, device_id TEXT NOT NULL, type TEXT NOT NULL, elapsed_ms INTEGER NOT NULL, payload_json TEXT NULL, disposition TEXT NOT NULL, reason TEXT NULL, received_at TEXT NOT NULL)",
            "CREATE TABLE edits(id INTEGER PRIMARY KEY AUTOINCREMENT, run_id TEXT NOT NULL REFERENCES runs(id), created_at TEXT NOT NULL, reason TEXT NOT NULL, before_json TEXT NOT NULL, after_json TEXT NOT NULL, undone_edit_id INTEGER NULL REFERENCES edits(id))",
            "CREATE INDEX idx_runs_competitor_edition ON runs(competitor_id, edition_id)",
            "CREATE INDEX idx_messages_run ON messages(run_id, id)",
            "CREATE INDEX idx_messages_message_id ON messages(message_id)",
            "CREATE INDEX idx_edits_run ON edits(run_id, id)"
        };

        foreach (var statement in statements)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }
    }

    private SqliteConnection OpenConnection()
    {
        ThrowIfDisposed();
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
            command.ExecuteNonQuery();
        }
        return connection;
    }

    private static void AddRunParameters(SqliteCommand command, RunRecord run)
    {
        command.Parameters.AddWithValue("$id", run.Id);
        command.Parameters.AddWithValue("$competitor_id", run.CompetitorId);
        command.Parameters.AddWithValue("$edition_id", run.EditionId);
        command.Parameters.AddWithValue("$category", run.Category.ToString());
        command.Parameters.AddWithValue("$status", run.Status.ToString());
        command.Parameters.AddWithValue("$phase", run.Phase.ToString());
        command.Parameters.AddWithValue("$manual_override", run.ManualOfflineOverride ? 1 : 0);
        command.Parameters.AddWithValue("$active_elapsed", run.ActiveElapsedMs);
        command.Parameters.AddWithValue("$last_input", run.LastAcceptedInputElapsedMs);
        command.Parameters.AddWithValue("$bonus_started", ValueOrNull(run.BonusStartedElapsedMs));
        command.Parameters.AddWithValue("$bonus_result", ValueOrNull(run.BonusResultJson));
        command.Parameters.AddWithValue("$bonus_points_override", ValueOrNull(run.BonusPointsOverride));
        command.Parameters.AddWithValue("$created_at", FormatDate(run.CreatedAt));
        command.Parameters.AddWithValue("$started_at", ValueOrNull(run.StartedAt is null ? null : FormatDate(run.StartedAt.Value)));
        command.Parameters.AddWithValue("$finished_at", ValueOrNull(run.FinishedAt is null ? null : FormatDate(run.FinishedAt.Value)));
        command.Parameters.AddWithValue("$supersedes", ValueOrNull(run.SupersedesRunId));
        command.Parameters.AddWithValue("$superseded_by", ValueOrNull(run.SupersededByRunId));
        command.Parameters.AddWithValue("$paused_from", ValueOrNull(run.PausedFromPhase));
        command.Parameters.AddWithValue("$notes", ValueOrNull(run.Notes));
        command.Parameters.AddWithValue("$revision", run.Revision);
        command.Parameters.AddWithValue("$edition", JsonSerializer.Serialize(run.Edition, JsonDefaults.Options));
    }

    private static object ValueOrNull(object? value) => value ?? DBNull.Value;
    private static string FormatDate(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static T Deserialize<T>(string value, string description)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(value, JsonDefaults.Options)
                ?? throw new InvalidDataException($"Persisted {description} is null.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Persisted {description} is malformed.", exception);
        }
    }

    private static T ParseEnum<T>(string value, string description) where T : struct, Enum
    {
        if (!Enum.TryParse<T>(value, ignoreCase: false, out var result))
        {
            throw new InvalidDataException($"Persisted {description} '{value}' is unknown.");
        }

        return result;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RunStore));
        }
    }
}
