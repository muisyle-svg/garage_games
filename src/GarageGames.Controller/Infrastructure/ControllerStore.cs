using System.Globalization;
using GarageGames.Core.Domain;
using GarageGames.Core.Protocol;
using Microsoft.Data.Sqlite;

namespace GarageGames.Controller.Infrastructure;

public sealed record RunRecord(
    string Id,
    string SeasonId,
    string CompetitorId,
    string CompetitorName,
    RunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int Revision);

public sealed record DeviceRecord(
    string Id,
    string FirmwareVersion,
    string StationModule,
    int BatteryMillivolts,
    int Rssi,
    int RadioFailures,
    int Channel,
    DateTimeOffset LastSeen,
    string? GameId,
    string? GameName);

public sealed record OutboxRecord(long Id, string IdempotencyKey, string Action, string PayloadJson, int Attempts);

public sealed class ControllerStore(AppPaths paths)
{
    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = paths.DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared,
        ForeignKeys = true
    }.ToString();

    public string DatabasePath => paths.DatabasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;

            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS competitors (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                queue_position INTEGER NOT NULL,
                notes TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS runs (
                id TEXT PRIMARY KEY,
                season_id TEXT NOT NULL,
                competitor_id TEXT NOT NULL,
                competitor_name TEXT NOT NULL,
                status TEXT NOT NULL,
                started_at TEXT NOT NULL,
                finished_at TEXT NULL,
                revision INTEGER NOT NULL DEFAULT 0,
                score_json TEXT NULL,
                FOREIGN KEY (competitor_id) REFERENCES competitors(id)
            );

            CREATE TABLE IF NOT EXISTS device_events (
                event_id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL,
                device_id TEXT NOT NULL,
                boot_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                type TEXT NOT NULL,
                elapsed_ms INTEGER NOT NULL,
                received_at TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                source TEXT NOT NULL,
                FOREIGN KEY (run_id) REFERENCES runs(id)
            );
            CREATE INDEX IF NOT EXISTS ix_device_events_run
                ON device_events(run_id, sequence, received_at);

            CREATE TABLE IF NOT EXISTS devices (
                id TEXT PRIMARY KEY,
                firmware_version TEXT NOT NULL,
                station_module TEXT NOT NULL,
                battery_mv INTEGER NOT NULL,
                rssi INTEGER NOT NULL,
                radio_failures INTEGER NOT NULL,
                channel INTEGER NOT NULL,
                last_seen TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS station_assignments (
                device_id TEXT PRIMARY KEY,
                game_id TEXT NOT NULL,
                game_name TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS sync_outbox (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                idempotency_key TEXT NOT NULL UNIQUE,
                action TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0,
                next_attempt_at TEXT NOT NULL,
                completed_at TEXT NULL,
                last_error TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS audit_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                occurred_at TEXT NOT NULL,
                category TEXT NOT NULL,
                actor TEXT NOT NULL,
                message TEXT NOT NULL,
                details_json TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        var interrupted = connection.CreateCommand();
        interrupted.CommandText =
            """
            UPDATE runs
            SET status = 'Interrupted',
                finished_at = COALESCE(finished_at, $now)
            WHERE status IN ('Active', 'Paused', 'Bonus');
            """;
        interrupted.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await interrupted.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO settings(key, value) VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Competitor>> GetCompetitorsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<Competitor>();
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, name, queue_position, created_at, notes
            FROM competitors
            ORDER BY queue_position, created_at;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                reader.GetString(4)));
        }
        return result;
    }

    public async Task<Competitor> AddCompetitorAsync(
        string name,
        string notes,
        CancellationToken cancellationToken = default)
    {
        var existing = await GetCompetitorsAsync(cancellationToken);
        var competitor = new Competitor(
            Guid.NewGuid().ToString("N"),
            name.Trim(),
            existing.Count == 0 ? 1 : existing.Max(item => item.QueuePosition) + 1,
            DateTimeOffset.UtcNow,
            notes.Trim());

        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO competitors(id, name, queue_position, notes, created_at)
            VALUES ($id, $name, $position, $notes, $created);
            """;
        command.Parameters.AddWithValue("$id", competitor.Id);
        command.Parameters.AddWithValue("$name", competitor.Name);
        command.Parameters.AddWithValue("$position", competitor.QueuePosition);
        command.Parameters.AddWithValue("$notes", competitor.Notes);
        command.Parameters.AddWithValue("$created", competitor.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await AuditAsync("competitor", "operator", $"Queued {competitor.Name}.", JsonDefaults.Serialize(competitor), cancellationToken);
        return competitor;
    }

    public async Task<RunRecord> CreateRunAsync(
        Competitor competitor,
        SeasonDefinition season,
        CancellationToken cancellationToken = default)
    {
        var run = new RunRecord(
            Guid.NewGuid().ToString("N"),
            season.Id,
            competitor.Id,
            competitor.Name,
            RunStatus.Active,
            DateTimeOffset.UtcNow,
            null,
            0);

        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO runs(id, season_id, competitor_id, competitor_name, status, started_at, revision)
            VALUES ($id, $season, $competitor, $name, $status, $started, 0);
            """;
        command.Parameters.AddWithValue("$id", run.Id);
        command.Parameters.AddWithValue("$season", run.SeasonId);
        command.Parameters.AddWithValue("$competitor", run.CompetitorId);
        command.Parameters.AddWithValue("$name", run.CompetitorName);
        command.Parameters.AddWithValue("$status", run.Status.ToString());
        command.Parameters.AddWithValue("$started", run.StartedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return run;
    }

    public async Task<bool> AppendEventAsync(
        DeviceEvent item,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var insert = connection.CreateCommand();
        insert.Transaction = (SqliteTransaction)transaction;
        insert.CommandText =
            """
            INSERT OR IGNORE INTO device_events(
                event_id, run_id, device_id, boot_id, sequence, type,
                elapsed_ms, received_at, payload_json, source)
            VALUES(
                $event, $run, $device, $boot, $sequence, $type,
                $elapsed, $received, $payload, $source);
            """;
        insert.Parameters.AddWithValue("$event", item.EventId);
        insert.Parameters.AddWithValue("$run", item.RunId);
        insert.Parameters.AddWithValue("$device", item.DeviceId);
        insert.Parameters.AddWithValue("$boot", item.BootId);
        insert.Parameters.AddWithValue("$sequence", item.Sequence);
        insert.Parameters.AddWithValue("$type", item.Type);
        insert.Parameters.AddWithValue("$elapsed", item.ElapsedMilliseconds);
        insert.Parameters.AddWithValue("$received", item.ReceivedAt.ToString("O"));
        insert.Parameters.AddWithValue("$payload", item.PayloadJson);
        insert.Parameters.AddWithValue("$source", item.Source);
        var inserted = await insert.ExecuteNonQueryAsync(cancellationToken) == 1;

        if (inserted)
        {
            var revision = connection.CreateCommand();
            revision.Transaction = (SqliteTransaction)transaction;
            revision.CommandText = "UPDATE runs SET revision = revision + 1 WHERE id = $run;";
            revision.Parameters.AddWithValue("$run", item.RunId);
            await revision.ExecuteNonQueryAsync(cancellationToken);

            var outbox = connection.CreateCommand();
            outbox.Transaction = (SqliteTransaction)transaction;
            outbox.CommandText =
                """
                INSERT OR IGNORE INTO sync_outbox(
                    idempotency_key, action, payload_json, next_attempt_at)
                VALUES($key, 'event', $payload, $now);
                """;
            outbox.Parameters.AddWithValue("$key", $"event:{item.EventId}");
            outbox.Parameters.AddWithValue("$payload", JsonDefaults.Serialize(item));
            outbox.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await outbox.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return inserted;
    }

    public async Task<bool> EventExistsAsync(
        string eventId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM device_events WHERE event_id = $event);";
        command.Parameters.AddWithValue("$event", eventId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    public async Task<RunRecord?> GetCurrentRunAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, season_id, competitor_id, competitor_name, status,
                   started_at, finished_at, revision
            FROM runs
            WHERE status IN ('Active', 'Paused', 'Bonus')
            ORDER BY started_at DESC
            LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRun(reader) : null;
    }

    public async Task<IReadOnlyList<RunRecord>> GetRunsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<RunRecord>();
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, season_id, competitor_id, competitor_name, status,
                   started_at, finished_at, revision
            FROM runs
            ORDER BY started_at DESC;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadRun(reader));
        }
        return result;
    }

    public async Task<IReadOnlyList<DeviceEvent>> GetEventsAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        var result = new List<DeviceEvent>();
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT event_id, run_id, device_id, boot_id, sequence, type,
                   elapsed_ms, received_at, payload_json, source
            FROM device_events
            WHERE run_id = $run
            ORDER BY sequence, received_at, event_id;
            """;
        command.Parameters.AddWithValue("$run", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetString(5),
                reader.GetInt64(6),
                DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                reader.GetString(8),
                reader.GetString(9)));
        }
        return result;
    }

    public async Task UpdateRunAsync(
        string runId,
        RunStatus status,
        ScoreResult? score,
        DateTimeOffset? finishedAt,
        RunSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE runs
            SET status = $status,
                finished_at = $finished,
                score_json = COALESCE($score, score_json)
            WHERE id = $run;
            """;
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$finished", finishedAt?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$score", score is null ? DBNull.Value : JsonDefaults.Serialize(score));
        await command.ExecuteNonQueryAsync(cancellationToken);

        if (score is not null)
        {
            await QueueOutboxAsync(
                $"result:{runId}:{status}",
                "result",
                JsonDefaults.Serialize(new
                {
                    runId,
                    snapshot.CompetitorId,
                    snapshot.CompetitorName,
                    status,
                    finishedAt,
                    snapshot.RemainingSeconds,
                    snapshot.AttemptCount,
                    snapshot.CompletionCount,
                    snapshot.BonusCount,
                    snapshot.Games,
                    score
                }),
                cancellationToken);
        }
    }

    public async Task<ScoreResult?> GetScoreAsync(string runId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT score_json FROM runs WHERE id = $run;";
        command.Parameters.AddWithValue("$run", runId);
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        return string.IsNullOrWhiteSpace(value)
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<ScoreResult>(value, JsonDefaults.Options);
    }

    public async Task UpsertDeviceAsync(
        string deviceId,
        DeviceHealthPayload health,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO devices(
                id, firmware_version, station_module, battery_mv, rssi,
                radio_failures, channel, last_seen)
            VALUES($id, $firmware, $module, $battery, $rssi, $failures, $channel, $seen)
            ON CONFLICT(id) DO UPDATE SET
                firmware_version = excluded.firmware_version,
                station_module = excluded.station_module,
                battery_mv = excluded.battery_mv,
                rssi = excluded.rssi,
                radio_failures = excluded.radio_failures,
                channel = excluded.channel,
                last_seen = excluded.last_seen;
            """;
        command.Parameters.AddWithValue("$id", deviceId);
        command.Parameters.AddWithValue("$firmware", health.FirmwareVersion);
        command.Parameters.AddWithValue("$module", health.StationModule);
        command.Parameters.AddWithValue("$battery", health.BatteryMillivolts);
        command.Parameters.AddWithValue("$rssi", health.Rssi);
        command.Parameters.AddWithValue("$failures", health.RadioFailures);
        command.Parameters.AddWithValue("$channel", health.Channel);
        command.Parameters.AddWithValue("$seen", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DeviceRecord>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<DeviceRecord>();
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT d.id, d.firmware_version, d.station_module, d.battery_mv,
                   d.rssi, d.radio_failures, d.channel, d.last_seen,
                   a.game_id, a.game_name
            FROM devices d
            LEFT JOIN station_assignments a ON a.device_id = d.id
            ORDER BY COALESCE(a.game_name, d.id);
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }
        return result;
    }

    public async Task AssignDeviceAsync(
        string deviceId,
        GameDefinition game,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO station_assignments(device_id, game_id, game_name, updated_at)
            VALUES($device, $game, $name, $now)
            ON CONFLICT(device_id) DO UPDATE SET
                game_id = excluded.game_id,
                game_name = excluded.game_name,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$game", game.Id);
        command.Parameters.AddWithValue("$name", game.Name);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await AuditAsync("assignment", "operator", $"Assigned {deviceId} to {game.Name}.", "{}", cancellationToken);
    }

    public async Task<string?> GetAssignedGameIdAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT game_id FROM station_assignments WHERE device_id = $device;";
        command.Parameters.AddWithValue("$device", deviceId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task<string?> GetDeviceIdForGameAsync(
        string gameId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT device_id
            FROM station_assignments
            WHERE game_id = $game
            ORDER BY updated_at DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$game", gameId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    public async Task<IReadOnlyList<OutboxRecord>> GetOutboxAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        var result = new List<OutboxRecord>();
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, idempotency_key, action, payload_json, attempts
            FROM sync_outbox
            WHERE completed_at IS NULL AND next_attempt_at <= $now
            ORDER BY id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4)));
        }
        return result;
    }

    public async Task CompleteOutboxAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE sync_outbox SET completed_at = $now WHERE id = $id;";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task FailOutboxAsync(long id, int attempts, string error, CancellationToken cancellationToken = default)
    {
        var seconds = Math.Min(300, Math.Pow(2, Math.Min(attempts, 8)));
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE sync_outbox
            SET attempts = attempts + 1,
                next_attempt_at = $next,
                last_error = $error
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$next", DateTimeOffset.UtcNow.AddSeconds(seconds).ToString("O"));
        command.Parameters.AddWithValue("$error", error[..Math.Min(error.Length, 1000)]);
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task QueueOutboxAsync(
        string idempotencyKey,
        string action,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO sync_outbox(
                idempotency_key, action, payload_json, next_attempt_at)
            VALUES($key, $action, $payload, $now);
            """;
        command.Parameters.AddWithValue("$key", idempotencyKey);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$payload", payloadJson);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AuditAsync(
        string category,
        string actor,
        string message,
        string detailsJson,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO audit_log(occurred_at, category, actor, message, details_json)
            VALUES($now, $category, $actor, $message, $details);
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$category", category);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$details", detailsJson);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<object>> GetAuditAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        var result = new List<object>();
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT occurred_at, category, actor, message, details_json
            FROM audit_log
            ORDER BY id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new
            {
                occurredAt = reader.GetString(0),
                category = reader.GetString(1),
                actor = reader.GetString(2),
                message = reader.GetString(3),
                details = reader.GetString(4)
            });
        }
        return result;
    }

    public async Task CreateBackupAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("Backup destination has no directory.");
        Directory.CreateDirectory(directory);
        await using var source = await OpenAsync(cancellationToken);
        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static RunRecord ReadRun(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            Enum.Parse<RunStatus>(reader.GetString(4)),
            DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
            reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
            reader.GetInt32(7));
}
