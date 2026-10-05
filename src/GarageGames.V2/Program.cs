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
DataDirectoryMigration.CopyLegacyDataIfNeeded(dataPath, GetOption(args, "--legacy-data-path"));
var editionPath = GetOption(args, "--edition-config") ?? Path.Combine(AppContext.BaseDirectory, "config", "edition-2026.json");
if (!File.Exists(editionPath))
{
    editionPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "config", "edition-2026.json"));
}

var edition = EditionDefinition.FromJson(editionPath);
// Keypad messages and their codes. A missing or invalid file is reported in Setup rather
// than stopping the app; keypad events then fall back to operator overrides.
var keypadAnswersPath = GetOption(args, "--keypad-answers") ?? Path.Combine(Path.GetDirectoryName(editionPath)!, "keypad-answers.csv");
var keypadChallenges = KeypadChallengeSet.LoadOrReportError(keypadAnswersPath);
var store = new RunStore(dataPath);
var clock = new SimulationClock();
var service = new RunService(store, edition, clock, keypadChallenges);
var urls = GetOption(args, "--urls") ?? "http://127.0.0.1:5187";
ValidateLoopbackUrls(urls);

// The scorekeeper and TV poll several times a second; per-request info logs filled
// tens of MB per hour. Keep warnings and errors, plus startup/shutdown messages.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

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
// Game sounds play through this computer's speakers, not a browser tab. --no-sound
// silences them (for example, on a machine that is only testing).
var soundsEnabled = OperatingSystem.IsWindows() && !args.Contains("--no-sound", StringComparer.OrdinalIgnoreCase);
builder.Services.AddSingleton<ISoundPlayer>(provider => soundsEnabled
    ? new WindowsSoundPlayer(Path.Combine(AppContext.BaseDirectory, "wwwroot", "sounds"),
        provider.GetRequiredService<ILogger<WindowsSoundPlayer>>())
    : SilentSoundPlayer.Instance);
builder.Services.AddHostedService<RunTimingHostedService>();
builder.WebHost.UseUrls(urls);

var app = builder.Build();
var soundPlayer = app.Services.GetRequiredService<ISoundPlayer>();
service.SoundCueRequested += cue => soundPlayer.Play(cue);
var webRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var staticAssetVersion = StaticAssetVersioning.ComputeVersion(webRootPath);
var buildId = GetOption(args, "--build-id") ??
    BuildIdentity.TryComputeForSourceProject(AppContext.BaseDirectory) ??
    System.Reflection.Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId.ToString("N");
var healthIdentity = new
{
    status = "ok",
    simulationMode,
    buildId,
    dataDirectory = store.DataDirectory,
    applicationDirectory = Path.GetFullPath(AppContext.BaseDirectory),
    processId = Environment.ProcessId
};
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
// Binding to loopback does not stop a web page open in the operator's browser from
// posting to this port, or a DNS-rebinding hostname from reaching it. The API only
// answers loopback Host headers and rejects cross-origin state changes.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") && !IsTrustedApiRequest(context))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "Cross-origin requests to the Garage Games API are not allowed." });
        return;
    }

    await next();
});
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value?.ToLowerInvariant();
    if (path is "/mvp" or "/mvp.html")
    {
        // The standalone MVP page was folded into the operator view.
        context.Response.Redirect("/");
        return;
    }

    var pageName = path switch
    {
        "/" or "/index.html" or "/advanced" => "index.html",
        "/scoreboard" or "/scoreboard.html" => "scoreboard.html",
        _ => null
    };
    if (pageName is null)
    {
        await next();
        return;
    }

    var pagePath = Path.Combine(webRootPath, pageName);
    if (!File.Exists(pagePath))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
    context.Response.Headers.Pragma = "no-cache";
    context.Response.Headers.Expires = "0";
    var html = await File.ReadAllTextAsync(pagePath, context.RequestAborted);
    await context.Response.WriteAsync(StaticAssetVersioning.StampHtml(html, staticAssetVersion), context.RequestAborted);
});
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        // The desktop shell can keep a page open across app updates; force the next
        // navigation to fetch matching HTML and scripts instead of stale controls.
        context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        context.Context.Response.Headers.Pragma = "no-cache";
        context.Context.Response.Headers.Expires = "0";
    }
});

app.MapGet("/api/health", () => Results.Ok(healthIdentity));
app.MapGet("/api/master", (PhysicalMasterSerialService master) => Results.Ok(master.GetSnapshot()));
app.MapPost("/api/master/scan", async (PhysicalMasterSerialService master, CancellationToken cancellationToken) =>
    Results.Ok(await master.ScanDevicesAsync(cancellationToken)));
app.MapPost("/api/master/connect", (ConnectMasterRequest request, PhysicalMasterSerialService master) =>
    Results.Ok(master.Connect(request.Port)));
app.MapPost("/api/master/disconnect", (PhysicalMasterSerialService master) =>
    Results.Ok(master.Disconnect()));
app.MapPost("/api/master/identify", async (IdentifyButtonRequest request, PhysicalMasterSerialService master,
    CancellationToken cancellationToken) =>
    Results.Ok(await master.IdentifyButtonAsync(request.DeviceId, cancellationToken)));
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
            ? Results.Ok(healthIdentity)
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
app.MapGet("/api/export", (RunService runs) => Results.Json(runs.GetOperatorSnapshot(simulationMode, forExport: true), JsonDefaults.Options));
app.MapPost("/api/competitors", (AddCompetitorRequest request, RunService runs) =>
    Results.Ok(runs.AddCompetitor(request.Name)));
app.MapPut("/api/competitors/{competitorId}", (string competitorId, RenameCompetitorRequest request, RunService runs) =>
    Results.Ok(runs.RenameCompetitor(competitorId, request.Name)));
app.MapPost("/api/competitors/import", (ImportCompetitorsRequest request, RunService runs) =>
    Results.Ok(runs.ImportCompetitors(request.Names ?? [])));
app.MapPost("/api/competitors/{competitorId}/archive", (string competitorId, SetCompetitorArchivedRequest request, RunService runs) =>
    Results.Ok(runs.SetCompetitorArchived(competitorId, request.IsArchived)));
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

app.MapPost("/api/run/start", async (RunService runs, PhysicalMasterSerialService master,
    CancellationToken cancellationToken) =>
{
    var run = master.StartVirtual(runs);
    await master.SendCurrentStatusAsync(cancellationToken);
    return Results.Ok(run);
});
app.MapPost("/api/run/countdown-finished", async (CountdownFinishedRequest request, RunService runs,
    PhysicalMasterSerialService master, CancellationToken cancellationToken) =>
{
    var run = runs.CompleteCountdown(request.RunId);
    await master.SendCurrentStatusAsync(cancellationToken);
    return Results.Ok(run);
});
// Shows the selected competitor as up next on the TV (full clock, no scores) without
// arming or starting anything.
app.MapPost("/api/run/prime", (StartCompetitorRunRequest request, RunService runs) =>
    Results.Ok(runs.PrimeNextCompetitor(request.CompetitorId, request.Category, request.DurationLimitSeconds)));
app.MapPost("/api/run/arm", async (StartCompetitorRunRequest request, RunService runs,
    PhysicalMasterSerialService master, CancellationToken cancellationToken) =>
{
    var durationLimitSeconds = RunService.RequireRequestedRunDuration(request.DurationLimitSeconds);
    var run = await master.ArmCompetitorAsync(runs, request.CompetitorId, request.Category,
        cancellationToken, durationLimitSeconds, request.ReplaceExistingOfficial);
    await master.SendCurrentStatusAsync(cancellationToken);
    return Results.Ok(run);
});
app.MapPost("/api/run/pause", async (RunService runs, PhysicalMasterSerialService master, CancellationToken cancellationToken) =>
{
    var run = runs.Pause();
    await master.SendCurrentStatusAsync(cancellationToken);
    return Results.Ok(run);
});
app.MapPost("/api/run/resume", async (RunService runs, PhysicalMasterSerialService master, CancellationToken cancellationToken) =>
{
    var run = runs.Resume();
    await master.SendCurrentStatusAsync(cancellationToken);
    return Results.Ok(run);
});
app.MapPost("/api/run/finish", async (RunService runs, PhysicalMasterSerialService master, CancellationToken cancellationToken) =>
{
    var run = runs.Finish();
    await master.SendCurrentStatusAsync(cancellationToken);
    return Results.Ok(run);
});
app.MapPost("/api/run/reopen", async (RunService runs, PhysicalMasterSerialService master, CancellationToken cancellationToken) =>
{
    var run = runs.ReopenFinished();
    await master.SendCurrentStatusAsync(cancellationToken);
    return Results.Ok(run);
});
app.MapPost("/api/run/record", async (RunService runs, PhysicalMasterSerialService master, CancellationToken cancellationToken) =>
{
    var run = runs.Record();
    await master.SendCurrentStatusAsync(cancellationToken);
    return Results.Ok(run);
});
app.MapPost("/api/run/abort", async (ActionReasonRequest request, RunService runs, PhysicalMasterSerialService master, CancellationToken cancellationToken) =>
{
    var run = runs.Abort(request.Reason);
    await master.SendCurrentStatusAsync(cancellationToken);
    return Results.Ok(run);
});
app.MapPost("/api/run/undo-last-press", async (RunService runs, PhysicalMasterSerialService master, CancellationToken cancellationToken) =>
{
    var run = runs.UndoLastEventPress();
    await master.SendCurrentStatusAsync(cancellationToken);
    return Results.Ok(run);
});
app.MapPost("/api/run/events/{eventId}/undo-press", async (string eventId, RunService runs, PhysicalMasterSerialService master, CancellationToken cancellationToken) =>
{
    var run = runs.UndoEventPress(eventId);
    await master.SendCurrentStatusAsync(cancellationToken);
    return Results.Ok(run);
});
app.MapPut("/api/leaderboards/preferences", (SetLeaderboardPreferencesRequest request, RunService runs) =>
{
    runs.SetLeaderboardPreference(request.ShowExhibitionsOnLeaderboard);
    return Results.Ok(runs.GetOperatorSnapshot(simulationMode));
});
app.MapPut("/api/run/edit", async (EditRunRequest request, RunService runs, PhysicalMasterSerialService master, CancellationToken cancellationToken) =>
{
    var run = runs.EditCurrentRun(request);
    await master.SendCurrentStatusAsync(cancellationToken);
    return Results.Ok(run);
});
app.MapPost("/api/run/undo", async (UndoRequest request, RunService runs, PhysicalMasterSerialService master, CancellationToken cancellationToken) =>
{
    var run = runs.UndoCurrentEdit(request.EditId, request.ExpectedRevision, request.Reason);
    await master.SendCurrentStatusAsync(cancellationToken);
    return Results.Ok(run);
});
app.MapPost("/api/runs/{runId}/restart", (string runId, ActionReasonRequest request, RunService runs) =>
    Results.Ok(runs.Restart(runId, request.Reason)));
app.MapPut("/api/runs/{runId}/edit", async (string runId, EditRunRequest request, RunService runs,
    PhysicalMasterSerialService master, CancellationToken cancellationToken) =>
{
    var run = runs.EditRun(runId, request);
    await master.SendCurrentStatusAsync(cancellationToken);
    return Results.Ok(run);
});
app.MapPost("/api/runs/{runId}/record", (string runId, RunService runs) =>
    Results.Ok(runs.RecordHistoricalRun(runId)));
app.MapPost("/api/runs/{runId}/delete", async (string runId, DeleteRunRequest request, RunService runs,
    PhysicalMasterSerialService master, CancellationToken cancellationToken) =>
{
    var run = runs.DeleteRun(runId, request.ExpectedRevision, request.Reason);
    await master.SendCurrentStatusAsync(cancellationToken);
    return Results.Ok(run);
});
app.MapPost("/api/runs/{runId}/restore", (string runId, RunService runs) =>
    Results.Ok(runs.RestoreRun(runId)));
app.MapPost("/api/runs/{runId}/events/{eventId}/press", async (string runId, string eventId, RunService runs, PhysicalMasterSerialService master, CancellationToken cancellationToken) =>
{
    var result = runs.PressEvent(runId, eventId);
    await master.SendCurrentStatusAsync(cancellationToken);
    return Results.Ok(result);
});
app.MapPost("/api/runs/{runId}/events/{eventId}/clear", async (string runId, string eventId, ClearEventRequest request, RunService runs, PhysicalMasterSerialService master, CancellationToken cancellationToken) =>
{
    var run = runs.ClearEvent(runId, eventId, request.ExpectedRevision);
    await master.SendCurrentStatusAsync(cancellationToken);
    return Results.Ok(run);
});
app.MapPost("/api/runs/{runId}/undo", async (string runId, UndoRequest request, RunService runs, PhysicalMasterSerialService master, CancellationToken cancellationToken) =>
{
    var isCurrent = runs.IsCurrentRun(runId);
    var run = isCurrent
        ? runs.UndoCurrentEdit(request.EditId, request.ExpectedRevision, request.Reason)
        : runs.UndoHistoricalEdit(runId, request.EditId, request.ExpectedRevision, request.Reason);
    if (isCurrent) await master.SendCurrentStatusAsync(cancellationToken);
    return Results.Ok(run);
});

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

app.MapPost("/api/simulator/input", async (InputEnvelope envelope, RunService runs, PhysicalMasterSerialService master, CancellationToken cancellationToken) =>
{
    if (!simulationMode)
    {
        return Results.NotFound();
    }
    var result = runs.Receive(envelope);
    await master.SendCurrentStatusAsync(cancellationToken);
    return Results.Ok(result);
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

// Keep the PC from sleeping while the app runs (sleep drops the master's USB connection).
if (!args.Contains("--allow-sleep", StringComparer.OrdinalIgnoreCase))
{
    KeepAwake.Request();
}
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

    return IsSameOriginOrNonBrowser(context);
}

static bool IsTrustedApiRequest(HttpContext context)
{
    if (!IsLoopbackHost(context.Request.Host.Host))
    {
        return false;
    }

    return HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) ||
        HttpMethods.IsOptions(context.Request.Method) || IsSameOriginOrNonBrowser(context);
}

// Browsers send Origin (and Sec-Fetch-Site) on cross-origin writes; local tools such as
// the tray launcher send neither and are allowed.
static bool IsSameOriginOrNonBrowser(HttpContext context)
{
    var originHeader = context.Request.Headers.Origin.ToString();
    if (string.IsNullOrEmpty(originHeader))
    {
        var fetchSite = context.Request.Headers["Sec-Fetch-Site"].ToString();
        return fetchSite is "" or "same-origin" or "none";
    }

    if (!Uri.TryCreate(originHeader, UriKind.Absolute, out var origin) || !IsLoopbackHost(origin.Host))
    {
        return false;
    }

    var requestPort = context.Request.Host.Port ?? DefaultPort(context.Request.Scheme);
    var originPort = origin.IsDefaultPort ? DefaultPort(origin.Scheme) : origin.Port;
    return string.Equals(origin.Scheme, context.Request.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(origin.Host, context.Request.Host.Host, StringComparison.OrdinalIgnoreCase) &&
        originPort == requestPort;
}

static bool IsLoopbackHost(string host) =>
    string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
    IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

static int DefaultPort(string scheme) =>
    string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80;

public partial class Program { }
