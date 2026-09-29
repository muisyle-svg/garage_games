namespace GarageGames.V2;

// Keeps run timing in the app itself, independent of any browser tab being open or in front:
// plays the countdown voice as a countdown begins and starts the run clock at Go (the
// scorekeeper page still reports Go as a backup; completing twice is harmless), and drives
// the bonus speed round's targets and misses.
public sealed class RunTimingHostedService : BackgroundService
{
    // Matches the 3.48-second countdown track: Go is 125 ms before its end.
    public const long CountdownTrackMilliseconds = 3_480;
    public const long GoLeadMilliseconds = 125;
    public const long GoAtMilliseconds = CountdownTrackMilliseconds - GoLeadMilliseconds;
    public const long LateVoiceLimitMilliseconds = 250;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(15);

    private readonly RunService _runs;
    private readonly ISoundPlayer _sounds;
    private readonly PhysicalMasterSerialService? _master;
    private readonly ILogger<RunTimingHostedService>? _logger;
    private string? _announcedRunId;

    public RunTimingHostedService(RunService runs, ISoundPlayer sounds, PhysicalMasterSerialService? master = null,
        ILogger<RunTimingHostedService>? logger = null)
    {
        _runs = runs;
        _sounds = sounds;
        _master = master;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var changed = TickCountdown();
                changed |= _runs.TickBonusGame();
                if (changed && _master is not null)
                {
                    await _master.SendCurrentStatusAsync(stoppingToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger?.LogError(exception, "Run timing failed; the scorekeeper page can still start the run at Go.");
            }
        }
    }

    // One countdown step: announce a newly seen countdown and complete it once Go is reached.
    // Returns true when this step started the run.
    public bool TickCountdown()
    {
        var state = _runs.GetCountdownState();
        if (state.Status != RunStatus.Countdown || state.RunId is null)
        {
            return false;
        }

        if (!string.Equals(_announcedRunId, state.RunId, StringComparison.Ordinal))
        {
            _announcedRunId = state.RunId;
            // Normally seen within one tick. A countdown noticed late gets no voice rather than
            // one out of step with Go (Windows cannot seek this MP3 accurately). After an app
            // restart the countdown begins again from zero, so it still gets its voice.
            if (state.ElapsedMilliseconds <= LateVoiceLimitMilliseconds)
            {
                _sounds.Play(SoundCue.Countdown);
            }
        }

        if (state.ElapsedMilliseconds < GoAtMilliseconds)
        {
            return false;
        }

        try
        {
            _runs.CompleteCountdown(state.RunId);
            return true;
        }
        catch (CommandException)
        {
            // The run changed at the same moment (aborted, or already started by the page).
            return false;
        }
    }
}
