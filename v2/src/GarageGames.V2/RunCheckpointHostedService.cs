namespace GarageGames.V2;

public sealed class RunCheckpointHostedService : BackgroundService
{
    private readonly RunService _runs;
    private readonly ILogger<RunCheckpointHostedService> _logger;

    public RunCheckpointHostedService(RunService runs, ILogger<RunCheckpointHostedService> logger)
    {
        _runs = runs;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                _runs.Checkpoint();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError(exception, "Garage Games v2 checkpoint failed; the application remains visible for operator recovery.");
            }
        }
    }
}
