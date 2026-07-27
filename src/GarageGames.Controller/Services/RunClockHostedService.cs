using GarageGames.Core.Domain;

namespace GarageGames.Controller.Services;

public sealed class RunClockHostedService(
    RunService runs,
    ILogger<RunClockHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var state = await runs.GetStateAsync(stoppingToken);
                if (state.Run is { Status: RunStatus.Active or RunStatus.Bonus, RemainingSeconds: 0 })
                {
                    await runs.TimeoutAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Run clock update failed.");
            }
        }
    }
}
