using GarageGames.V2;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});
var simulationMode = !args.Contains("--hardware-mode", StringComparer.OrdinalIgnoreCase);
var dataPath = GetOption(args, "--data-path") ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GarageGamesV2");
var editionPath = GetOption(args, "--edition-config") ?? Path.Combine(AppContext.BaseDirectory, "config", "edition-2026.json");
if (!File.Exists(editionPath))
{
    editionPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "config", "edition-2026.json"));
}

var edition = EditionDefinition.FromJson(editionPath);
var store = new RunStore(dataPath);
var clock = new SimulationClock();
var service = new RunService(store, edition, clock);
var urls = GetOption(args, "--urls") ?? "http://127.0.0.1:5187";
ValidateLoopbackUrls(urls);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.PropertyNameCaseInsensitive = true;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddSingleton(store);
builder.Services.AddSingleton<IMonotonicClock>(clock);
builder.Services.AddSingleton(service);
builder.Services.AddSingleton<PhysicalMasterSerialService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<PhysicalMasterSerialService>());
builder.Services.AddHostedService<RunCheckpointHostedService>();
builder.WebHost.UseUrls(urls);

var app = builder.Build();
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var exception = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = exception switch
        {
            CommandException => StatusCodes.Status409Conflict,
            InvalidDataException => StatusCodes.Status500InternalServerError,
            _ => StatusCodes.Status500InternalServerError
        };
        var message = exception switch
        {
            CommandException command => command.Message,
            InvalidDataException => "Persisted v2 data is invalid; the application did not reset it.",
            _ => "Garage Games v2 encountered an unexpected error."
        };
        await context.Response.WriteAsJsonAsync(new { error = message });
    });
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", simulationMode }));
app.MapGet("/api/master", (PhysicalMasterSerialService master) => Results.Ok(master.GetSnapshot()));
app.MapPost("/api/master/scan", async (PhysicalMasterSerialService master, CancellationToken cancellationToken) =>
    Results.Ok(await master.ScanDevicesAsync(cancellationToken)));
app.MapPost("/api/master/connect", (ConnectMasterRequest request, PhysicalMasterSerialService master) =>
    Results.Ok(master.Connect(request.Port)));
app.MapPost("/api/master/disconnect", (PhysicalMasterSerialService master) =>
    Results.Ok(master.Disconnect()));
var trayShutdownToken = Environment.GetEnvironmentVariable("GARAGE_GAMES_V2_SHUTDOWN_TOKEN");
if (!string.IsNullOrWhiteSpace(trayShutdownToken))
{
    var expectedTrayToken = Encoding.UTF8.GetBytes(trayShutdownToken);

    bool IsTrayControlAuthorized(HttpContext context)
    {
        var remoteAddress = context.Connection.RemoteIpAddress;
        if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress))
        {
            return false;
        }

        var suppliedToken = Encoding.UTF8.GetBytes(context.Request.Headers["X-Garage-Games-Shutdown"].ToString());
        return suppliedToken.Length == expectedTrayToken.Length &&
            CryptographicOperations.FixedTimeEquals(suppliedToken, expectedTrayToken);
    }

    app.MapGet("/api/internal/tray-health", (HttpContext context) =>
        IsTrayControlAuthorized(context)
            ? Results.Ok(new { status = "ok" })
            : Results.NotFound());

    app.MapPost("/api/internal/shutdown", async (HttpContext context, IHostApplicationLifetime lifetime) =>
    {
        if (!IsTrayControlAuthorized(context))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status202Accepted;
        context.Response.OnCompleted(() =>
        {
            lifetime.StopApplication();
            return Task.CompletedTask;
        });
        await context.Response.WriteAsJsonAsync(new { status = "shuttingDown" });
    });
}
app.MapGet("/api/operator", (RunService runs) => Results.Ok(runs.GetOperatorSnapshot(simulationMode)));
app.MapGet("/api/scoreboard", (RunService runs) => Results.Ok(runs.GetScoreboard(simulationMode)));
app.MapGet("/api/setup", (RunService runs) => Results.Ok(runs.GetSetup()));
app.MapPut("/api/setup", (EditionSetup request, RunService runs) => Results.Ok(runs.UpdateSetup(request)));
app.MapGet("/api/run/countdown-state", (RunService runs) => Results.Ok(runs.GetCountdownState()));
app.MapGet("/api/export", (RunService runs) => Results.Json(runs.GetOperatorSnapshot(simulationMode), JsonDefaults.Options));
app.MapGet("/scoreboard", () => Results.File(Path.Combine(AppContext.BaseDirectory, "wwwroot", "scoreboard.html"), "text/html"));
app.MapGet("/advanced", () => Results.File(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html"), "text/html"));

app.MapPost("/api/competitors", (AddCompetitorRequest request, RunService runs) =>
    Results.Ok(runs.AddCompetitor(request.Name)));
app.MapPost("/api/queue", (AddQueueRequest request, RunService runs) =>
    Results.Ok(runs.AddToQueue(request.CompetitorId, request.Category, request.ReplaceExistingOfficial, request.Reason)));
app.MapDelete("/api/queue/{queueId}", (string queueId, RunService runs) =>
{
    runs.RemoveFromQueue(queueId);
    return Results.NoContent();
});
app.MapPost("/api/queue/reorder", (ReorderQueueRequest request, RunService runs) =>
{
    runs.ReorderQueue(request.QueueIds);
    return Results.Ok(runs.GetOperatorSnapshot(simulationMode));
});
app.MapPost("/api/queue/{queueId}/arm", async (string queueId, ArmRequest request, RunService runs,
    PhysicalMasterSerialService master, CancellationToken cancellationToken) =>
    Results.Ok(await master.ArmQueueAsync(runs, queueId, request.ManualOfflineOverride, cancellationToken)));

app.MapPost("/api/run/start", (RunService runs, PhysicalMasterSerialService master) => Results.Ok(master.StartVirtual(runs)));
app.MapPost("/api/run/countdown-finished", (CountdownFinishedRequest request, RunService runs) =>
    Results.Ok(runs.CompleteCountdown(request.RunId)));
app.MapPost("/api/run/arm", async (StartCompetitorRunRequest request, RunService runs,
    PhysicalMasterSerialService master, CancellationToken cancellationToken) =>
    Results.Ok(await master.ArmCompetitorAsync(runs, request.CompetitorId, request.Category, cancellationToken)));
app.MapPost("/api/run/pause", (RunService runs) => Results.Ok(runs.Pause()));
app.MapPost("/api/run/resume", (RunService runs) => Results.Ok(runs.Resume()));
app.MapPost("/api/run/finish", (RunService runs) => Results.Ok(runs.Finish()));
app.MapPost("/api/run/record", (RunService runs) => Results.Ok(runs.Record()));
app.MapPost("/api/run/abort", (ActionReasonRequest request, RunService runs) => Results.Ok(runs.Abort(request.Reason)));
app.MapPut("/api/run/edit", (EditRunRequest request, RunService runs) =>
    Results.Ok(runs.EditCurrentRun(request)));
app.MapPost("/api/run/undo", (UndoRequest request, RunService runs) =>
    Results.Ok(runs.UndoCurrentEdit(request.EditId, request.ExpectedRevision, request.Reason)));
app.MapPost("/api/runs/{runId}/restart", (string runId, ActionReasonRequest request, RunService runs) =>
    Results.Ok(runs.Restart(runId, request.Reason)));
app.MapPut("/api/runs/{runId}/edit", (string runId, EditRunRequest request, RunService runs) =>
    Results.Ok(runs.EditHistoricalRun(runId, request)));
app.MapPost("/api/runs/{runId}/record", (string runId, RunService runs) =>
    Results.Ok(runs.RecordHistoricalRun(runId)));
app.MapPost("/api/runs/{runId}/events/{eventId}/press", (string runId, string eventId, RunService runs) =>
    Results.Ok(runs.PressEvent(runId, eventId)));
app.MapPost("/api/runs/{runId}/undo", (string runId, UndoRequest request, RunService runs) =>
    Results.Ok(runs.IsCurrentRun(runId)
        ? runs.UndoCurrentEdit(request.EditId, request.ExpectedRevision, request.Reason)
        : runs.UndoHistoricalEdit(runId, request.EditId, request.ExpectedRevision, request.Reason)));

app.MapPost("/api/devices/preflight", (RunService runs) => Results.Ok(runs.Preflight()));
app.MapPost("/api/devices/{deviceId}/availability", (string deviceId, AvailabilityRequest request, RunService runs) =>
{
    if (!simulationMode)
    {
        return Results.Problem("Simulated device availability is disabled in hardware mode.", statusCode: StatusCodes.Status501NotImplemented);
    }
    runs.SetDeviceAvailability(deviceId, request.Availability, request.Error);
    return Results.Ok(runs.GetOperatorSnapshot(simulationMode).Devices.Single(d => d.DeviceId == deviceId));
});

app.MapPost("/api/backup", (RunStore data) => Results.Ok(new { path = data.CreateBackup() }));
app.MapPost("/api/testing/clear-database", (ClearDatabaseRequest request, HttpContext context, RunService runs) =>
{
    if (!IsLoopbackClearRequest(context))
    {
        return Results.NotFound();
    }

    var backupPath = runs.ClearAllData(request.ConfirmationPhrase);
    return Results.Ok(new { backupPath });
});

app.MapPost("/api/simulator/input", (InputEnvelope envelope, RunService runs) =>
{
    if (!simulationMode)
    {
        return Results.NotFound();
    }
    return Results.Ok(runs.Receive(envelope));
});
app.MapPost("/api/simulator/advance-clock", (AdvanceClockRequest request) =>
{
    if (!simulationMode)
    {
        return Results.NotFound();
    }
    if (request.Milliseconds < 0)
    {
        throw new CommandException("Simulation clock cannot move backwards.");
    }
    clock.Advance(TimeSpan.FromMilliseconds(request.Milliseconds));
    return Results.Ok(new { advancedMilliseconds = request.Milliseconds });
});

app.Run();

static string? GetOption(string[] arguments, string name)
{
    var index = Array.FindIndex(arguments, argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
}

static void ValidateLoopbackUrls(string urls)
{
    foreach (var rawUrl in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) ||
            (uri.Host is not "127.0.0.1" and not "localhost" and not "[::1]" and not "::1"))
        {
            throw new InvalidOperationException("Garage Games v2 is loopback-only. Use an http://127.0.0.1:<port> URL.");
        }
    }
}

static bool IsLoopbackClearRequest(HttpContext context)
{
    var remoteAddress = context.Connection.RemoteIpAddress;
    var requestHost = context.Request.Host.Host;
    if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress) || !IsLoopbackHost(requestHost))
    {
        return false;
    }

    var originHeader = context.Request.Headers.Origin.ToString();
    if (string.IsNullOrEmpty(originHeader))
    {
        return true;
    }

    if (!Uri.TryCreate(originHeader, UriKind.Absolute, out var origin) || !IsLoopbackHost(origin.Host))
    {
        return false;
    }

    var requestPort = context.Request.Host.Port ?? DefaultPort(context.Request.Scheme);
    var originPort = origin.IsDefaultPort ? DefaultPort(origin.Scheme) : origin.Port;
    return string.Equals(origin.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(origin.Host, requestHost, StringComparison.OrdinalIgnoreCase) &&
        originPort == requestPort;
}

static bool IsLoopbackHost(string host) =>
    string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
    IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

static int DefaultPort(string scheme) =>
    string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80;

public partial class Program { }
