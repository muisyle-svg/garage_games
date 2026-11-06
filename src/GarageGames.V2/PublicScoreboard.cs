using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GarageGames.V2;

// What the public scoreboard on the event guide website shows: the Leaderboards tab's
// standings plus each listed run's event results, so the site can build the event
// leaderboards and scorecards the same way the tab does. Names, times, and points only;
// nothing about devices, the queue, or keypad answers.
public sealed class PublicScoreboardSnapshot
{
    public int Version { get; set; } = 1;
    // Set when the file is uploaded; left out when comparing for changes.
    public DateTimeOffset? PublishedAt { get; set; }
    public required string EditionName { get; set; }
    // The edition's events in scorecard order, then the bonus round when it is played.
    public required List<PublicScoreboardEvent> Events { get; set; }
    public required List<LeaderboardRow> Leaderboard { get; set; }
    public required List<PublicScoreboardRun> Runs { get; set; }
}

// Type is "event" or "bonusRound" (ranked by points alone).
public sealed record PublicScoreboardEvent(string EventId, string Name, string Type);

public sealed class PublicScoreboardRun
{
    public required string Id { get; set; }
    public RunCategory Category { get; set; }
    public DateTimeOffset? RecordedAt { get; set; }
    public int DurationLimitSeconds { get; set; }
    public int TotalPoints { get; set; }
    public required List<PublicScoreboardResult> Events { get; set; }
}

// Status is "pending", "active", or "completed". Times are run active-elapsed milliseconds.
public sealed class PublicScoreboardResult
{
    public required string EventId { get; set; }
    public required string Status { get; set; }
    public long? StartElapsedMs { get; set; }
    public long? FinishElapsedMs { get; set; }
    public int Points { get; set; }
}

// Read from public-scoreboard.json in the app's data folder (never committed to Git).
// Only the token is required; the rest default to the event guide's repository.
public sealed class PublicScoreboardSettings
{
    public bool Enabled { get; set; } = true;
    public string? Token { get; set; }
    public string Repository { get; set; } = "muisyle-svg/garage-games-guide";
    public string Branch { get; set; } = "live-scores";
    public string Path { get; set; } = "scores.json";
    public string SiteUrl { get; set; } = "https://muisyle-svg.github.io/garage-games-guide/scoreboard/";
    public int MinimumSecondsBetweenUploads { get; set; } = 60;
}

// State is "off", "waiting", "published", or "error".
public sealed record PublicScoreboardStatus(string State, string Message, DateTimeOffset? LastPublishedAt, string? SiteUrl);

// Uploads the public scoreboard to the event guide's GitHub repository when the standings
// change, at most once a minute. The website rebuilds from it in a minute or two. No
// internet only means the site falls behind; the scorekeeper itself never waits on this.
public sealed class PublicScoreboardPublisher : BackgroundService
{
    public const string SettingsFileName = "public-scoreboard.json";

    private static readonly HttpClient Http = CreateClient();
    private static readonly JsonSerializerOptions SettingsJson = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly RunService _runs;
    private readonly ILogger<PublicScoreboardPublisher> _logger;
    private readonly string _settingsPath;
    private readonly object _statusGate = new();
    private PublicScoreboardStatus _status;
    private string? _target;
    private string? _remoteSha;
    private string? _publishedContent;
    private DateTimeOffset? _lastPublishedAt;
    private DateTimeOffset _lastAttemptAt = DateTimeOffset.MinValue;
    private string? _lastLoggedProblem;

    public PublicScoreboardPublisher(RunService runs, RunStore store, ILogger<PublicScoreboardPublisher> logger)
    {
        _runs = runs;
        _logger = logger;
        _settingsPath = System.IO.Path.Combine(store.DataDirectory, SettingsFileName);
        _status = NotSetUp();
    }

    public string SettingsPath => _settingsPath;

    public PublicScoreboardStatus GetStatus()
    {
        lock (_statusGate) return _status;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            try
            {
                await PublishIfChangedAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                var message = exception switch
                {
                    PublishException publish => publish.Message,
                    HttpRequestException => "No internet connection to GitHub. Retrying.",
                    TaskCanceledException => "GitHub didn't answer in time. Retrying.",
                    _ => "Publishing failed unexpectedly. Retrying."
                };
                SetStatus("error", message);
                if (message != _lastLoggedProblem)
                {
                    _lastLoggedProblem = message;
                    _logger.LogWarning("Public scoreboard: {Message} ({Error})", message, exception.GetType().Name);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task PublishIfChangedAsync(CancellationToken cancellationToken)
    {
        var settings = LoadSettings();
        if (settings is null)
        {
            SetStatus(NotSetUp());
            return;
        }
        if (!settings.Enabled)
        {
            SetStatus("off", "Turned off in public-scoreboard.json.", settings.SiteUrl);
            return;
        }
        if (string.IsNullOrWhiteSpace(settings.Token))
        {
            SetStatus("off", "Add a GitHub token to public-scoreboard.json to publish.", settings.SiteUrl);
            return;
        }

        var target = $"{settings.Repository}|{settings.Branch}|{settings.Path}";
        if (target != _target)
        {
            _target = target;
            _remoteSha = null;
            _publishedContent = null;
        }

        var snapshot = _runs.GetPublicScoreboard();
        var content = JsonSerializer.Serialize(snapshot, JsonDefaults.Options);
        if (content == _publishedContent)
        {
            SetStatus("published", "The website has the latest results.", settings.SiteUrl);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastAttemptAt < TimeSpan.FromSeconds(Math.Max(15, settings.MinimumSecondsBetweenUploads)))
        {
            SetStatus("waiting", "New results go to the website within a minute.", settings.SiteUrl);
            return;
        }
        _lastAttemptAt = now;

        snapshot.PublishedAt = now;
        var upload = JsonSerializer.Serialize(snapshot, JsonDefaults.Options);
        var contentsUri = $"repos/{settings.Repository.Trim()}/contents/{string.Join('/', settings.Path.Trim('/').Split('/').Select(Uri.EscapeDataString))}";
        _remoteSha ??= await GetRemoteShaAsync(settings, contentsUri, cancellationToken);
        _remoteSha = await PutAsync(settings, contentsUri, upload, cancellationToken);
        _publishedContent = content;
        _lastPublishedAt = now;
        _lastLoggedProblem = null;
        SetStatus("published", "The website has the latest results.", settings.SiteUrl);
    }

    private PublicScoreboardSettings? LoadSettings()
    {
        if (!File.Exists(_settingsPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<PublicScoreboardSettings>(File.ReadAllText(_settingsPath), SettingsJson)
                ?? throw new JsonException("empty");
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new PublishException("public-scoreboard.json can't be read. Check its format.");
        }
    }

    private static async Task<string?> GetRemoteShaAsync(PublicScoreboardSettings settings, string contentsUri, CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Get, $"{contentsUri}?ref={Uri.EscapeDataString(settings.Branch)}", settings);
        using var response = await Http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null; // A new file (or no access; the upload says which).
        await ThrowIfFailedAsync(response, settings);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return body?["sha"]?.GetValue<string>();
    }

    private async Task<string?> PutAsync(PublicScoreboardSettings settings, string contentsUri, string upload, CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["message"] = "Update live scores",
            ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(upload)),
            ["branch"] = settings.Branch
        };
        if (_remoteSha is not null) payload["sha"] = _remoteSha;
        using var request = NewRequest(HttpMethod.Put, contentsUri, settings);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, cancellationToken);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
        {
            // The file changed on GitHub since we last saw it; look it up again next time.
            _remoteSha = null;
            _lastAttemptAt = DateTimeOffset.MinValue;
            throw new PublishException("The scores file changed on GitHub. Retrying.");
        }
        await ThrowIfFailedAsync(response, settings);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return body?["content"]?["sha"]?.GetValue<string>();
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, PublicScoreboardSettings settings)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = "";
        try
        {
            detail = JsonNode.Parse(await response.Content.ReadAsStringAsync())?["message"]?.GetValue<string>() ?? "";
        }
        catch (JsonException)
        {
        }
        throw new PublishException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "GitHub rejected the token (expired or mistyped). Update public-scoreboard.json.",
            HttpStatusCode.Forbidden or HttpStatusCode.NotFound =>
                $"The token can't write to {settings.Repository} (branch {settings.Branch}). It needs Contents: read and write.",
            _ => $"GitHub answered {(int)response.StatusCode}{(detail.Length > 0 ? $": {detail}" : "")}. Retrying."
        });
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string uri, PublicScoreboardSettings settings)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token!.Trim());
        return request;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GarageGames-Scorekeeper");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private PublicScoreboardStatus NotSetUp() =>
        new("off", $"Not set up. Create {SettingsFileName} in {System.IO.Path.GetDirectoryName(_settingsPath)} to publish.", null, null);

    private void SetStatus(string state, string message, string? siteUrl = null)
    {
        lock (_statusGate)
        {
            _status = new PublicScoreboardStatus(state, message, _lastPublishedAt, siteUrl ?? _status.SiteUrl);
        }
    }

    private void SetStatus(PublicScoreboardStatus status)
    {
        lock (_statusGate) _status = status;
    }

    private sealed class PublishException(string message) : Exception(message);
}
