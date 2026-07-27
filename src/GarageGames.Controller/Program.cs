using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using GarageGames.Controller.Infrastructure;
using GarageGames.Controller.Services;
using GarageGames.Core.Domain;
using GarageGames.Core.Protocol;
using GarageGames.Core.Scoring;
using GarageGames.Core.State;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = "wwwroot"
});
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});
var port = builder.Configuration.GetValue("Controller:Port", Protocol.DefaultControllerPort);
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddSingleton<AppPaths>();
builder.Services.AddSingleton<ControllerStore>();
builder.Services.AddSingleton<SeasonCatalog>();
builder.Services.AddSingleton<RunProjector>();
builder.Services.AddSingleton<IScoringPolicy, Baseline2025ScoringPolicy>();
builder.Services.AddSingleton<IScoringPolicy, TimeDecay2026ScoringPolicy>();
builder.Services.AddSingleton<ScoringPolicyRegistry>();
builder.Services.AddSingleton<BridgeConnectionManager>();
builder.Services.AddSingleton<RunService>();
builder.Services.AddSingleton<ExportService>();
builder.Services.AddSingleton<BackupHostedService>();
builder.Services.AddHostedService(service => service.GetRequiredService<BackupHostedService>());
builder.Services.AddHostedService<DiscoveryHostedService>();
builder.Services.AddHostedService<RunClockHostedService>();
builder.Services.AddHostedService<GoogleSyncHostedService>();
builder.Services.AddHttpClient("google-sheet", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});

var app = builder.Build();
var store = app.Services.GetRequiredService<ControllerStore>();
await store.InitializeAsync();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(20)
});

app.MapGet("/api/health", () => Results.Ok(new
{
    ok = true,
    protocolVersion = Protocol.Version,
    serverTime = DateTimeOffset.UtcNow
}));

app.MapGet("/api/state", async (RunService runs, CancellationToken cancellationToken) =>
    Results.Ok(await runs.GetStateAsync(cancellationToken)));

app.MapGet("/api/seasons", (SeasonCatalog seasons) => Results.Ok(seasons.All));

app.MapPost("/api/setup", async (
    SetupRequest request,
    ControllerStore database,
    RunService runs,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.EventName))
    {
        return Results.BadRequest(new { error = "Event name is required." });
    }

    await database.SetSettingAsync("eventName", request.EventName.Trim(), cancellationToken);
    await database.SetSettingAsync("googleEndpoint", request.GoogleEndpoint?.Trim() ?? "", cancellationToken);
    await database.SetSettingAsync("googleSecret", request.GoogleSecret?.Trim() ?? "", cancellationToken);
    await runs.SelectSeasonAsync(request.SeasonId, cancellationToken);
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/setup", async (ControllerStore database, CancellationToken cancellationToken) =>
    Results.Ok(new
    {
        eventName = await database.GetSettingAsync("eventName", cancellationToken),
        googleEndpoint = await database.GetSettingAsync("googleEndpoint", cancellationToken),
        googleConfigured = !string.IsNullOrWhiteSpace(
            await database.GetSettingAsync("googleSecret", cancellationToken))
    }));

app.MapPost("/api/competitors", async (
    AddCompetitorRequest request,
    ControllerStore database,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Name))
    {
        return Results.BadRequest(new { error = "Competitor name is required." });
    }
    return Results.Ok(await database.AddCompetitorAsync(request.Name, request.Notes ?? "", cancellationToken));
});

app.MapPost("/api/runs/start", async (
    StartRunRequest request,
    RunService runs,
    CancellationToken cancellationToken) =>
    Results.Ok(await runs.StartAsync(request.CompetitorId, cancellationToken)));

app.MapPost("/api/runs/pause", async (RunService runs, CancellationToken cancellationToken) =>
    Results.Ok(await runs.PauseAsync(cancellationToken)));

app.MapPost("/api/runs/resume", async (RunService runs, CancellationToken cancellationToken) =>
    Results.Ok(await runs.ResumeAsync(cancellationToken)));

app.MapPost("/api/runs/abort", async (
    ReasonRequest request,
    RunService runs,
    CancellationToken cancellationToken) =>
    Results.Ok(await runs.AbortAsync(request.Reason, cancellationToken)));

app.MapPost("/api/runs/undo", async (
    ReasonRequest request,
    RunService runs,
    CancellationToken cancellationToken) =>
    Results.Ok(await runs.UndoAsync(request.Reason, cancellationToken)));

app.MapPost("/api/runs/event", async (
    ManualEventRequest request,
    RunService runs,
    CancellationToken cancellationToken) =>
    Results.Ok(await runs.AppendManualGameEventAsync(
        request.Type,
        request.GameId,
        request.RemainingSeconds,
        request.Note ?? "",
        cancellationToken)));

app.MapPost("/api/runs/correction", async (
    CorrectionRequest request,
    RunService runs,
    CancellationToken cancellationToken) =>
    Results.Ok(await runs.CorrectAsync(
        new(
            request.GameId,
            request.Field,
            request.NumericValue,
            request.BooleanValue,
            request.Reason,
            request.ExpectedRevision),
        "operator",
        cancellationToken: cancellationToken)));

app.MapGet("/api/devices", async (ControllerStore database, CancellationToken cancellationToken) =>
    Results.Ok(await database.GetDevicesAsync(cancellationToken)));

app.MapPost("/api/devices/{deviceId}/assignment", async (
    string deviceId,
    AssignmentRequest request,
    ControllerStore database,
    RunService runs,
    CancellationToken cancellationToken) =>
{
    var state = await runs.GetStateAsync(cancellationToken);
    var game = state.Season.EnabledGames.FirstOrDefault(item => item.Id == request.GameId);
    if (game is null)
    {
        return Results.NotFound(new { error = "Game not found in selected season." });
    }
    await database.AssignDeviceAsync(deviceId, game, cancellationToken);
    return Results.Ok(new { ok = true });
});

app.MapGet("/api/leaderboard", async (RunService runs, CancellationToken cancellationToken) =>
    Results.Ok(await runs.GetLeaderboardAsync(cancellationToken)));

app.MapGet("/api/participants", async (
    string? query,
    RunService runs,
    CancellationToken cancellationToken) =>
    Results.Ok(await runs.SearchParticipantsAsync(query ?? "", cancellationToken)));

app.MapGet("/api/audit", async (
    int? limit,
    ControllerStore database,
    CancellationToken cancellationToken) =>
    Results.Ok(await database.GetAuditAsync(Math.Clamp(limit ?? 100, 1, 500), cancellationToken)));

app.MapPost("/api/backup", async (
    BackupHostedService backups,
    CancellationToken cancellationToken) =>
    Results.Ok(new { path = await backups.CreateNowAsync(cancellationToken) }));

app.MapGet("/api/export/leaderboard.csv", async (
    ExportService exports,
    CancellationToken cancellationToken) =>
    Results.File(
        await exports.CreateCsvAsync(cancellationToken),
        "text/csv",
        "garage-games-2026-leaderboard.csv"));

app.MapGet("/api/export/leaderboard.xlsx", async (
    ExportService exports,
    CancellationToken cancellationToken) =>
    Results.File(
        await exports.CreateXlsxAsync(cancellationToken),
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "garage-games-2026-leaderboard.xlsx"));

app.Map("/bridge", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var deviceId = context.Request.Query["deviceId"].ToString();
    if (string.IsNullOrWhiteSpace(deviceId))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    var bridge = context.RequestServices.GetRequiredService<BridgeConnectionManager>();
    var runs = context.RequestServices.GetRequiredService<RunService>();
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    bridge.Register(deviceId, socket);
    var buffer = new byte[8192];

    try
    {
        while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
        {
            var count = 0;
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer, count, buffer.Length - count),
                    context.RequestAborted);
                count += result.Count;
                if (count == buffer.Length && !result.EndOfMessage)
                {
                    throw new InvalidDataException("Bridge message exceeds 8 KiB.");
                }
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            try
            {
                var envelope = JsonSerializer.Deserialize<ProtocolEnvelope>(
                    Encoding.UTF8.GetString(buffer, 0, count),
                    JsonDefaults.Options) ?? throw new InvalidDataException("Empty bridge message.");
                await runs.HandleEnvelopeAsync(envelope, context.RequestAborted);
                var acknowledgement = Encoding.UTF8.GetBytes(JsonDefaults.Serialize(ProtocolEnvelope.Ack(envelope)));
                await socket.SendAsync(
                    acknowledgement,
                    WebSocketMessageType.Text,
                    true,
                    context.RequestAborted);
            }
            catch (Exception exception)
            {
                var error = Encoding.UTF8.GetBytes(JsonDefaults.Serialize(new
                {
                    type = "error",
                    message = exception.Message
                }));
                await socket.SendAsync(error, WebSocketMessageType.Text, true, context.RequestAborted);
            }
        }
    }
    finally
    {
        bridge.Remove(deviceId, socket);
    }
});

app.MapFallbackToFile("index.html");

app.Logger.LogInformation("Garage Games controller ready at http://localhost:{Port}", port);
await app.RunAsync();

public sealed record SetupRequest(
    string EventName,
    string SeasonId,
    string? GoogleEndpoint,
    string? GoogleSecret);
public sealed record AddCompetitorRequest(string Name, string? Notes);
public sealed record StartRunRequest(string CompetitorId);
public sealed record ReasonRequest(string Reason);
public sealed record ManualEventRequest(
    string Type,
    string GameId,
    int? RemainingSeconds,
    string? Note);
public sealed record CorrectionRequest(
    string GameId,
    string Field,
    decimal? NumericValue,
    bool? BooleanValue,
    string Reason,
    long ExpectedRevision);
public sealed record AssignmentRequest(string GameId);
