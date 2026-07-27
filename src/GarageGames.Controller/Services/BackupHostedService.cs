using GarageGames.Controller.Infrastructure;

namespace GarageGames.Controller.Services;

public sealed class BackupHostedService(
    ControllerStore store,
    AppPaths paths,
    IConfiguration configuration,
    ILogger<BackupHostedService> logger) : BackgroundService
{
    public async Task<string> CreateNowAsync(CancellationToken cancellationToken = default)
    {
        var destination = Path.Combine(
            paths.BackupDirectory,
            $"garage-games-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.db");
        await store.CreateBackupAsync(destination, cancellationToken);
        logger.LogInformation("Database backup created at {Path}.", destination);
        return destination;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hours = Math.Max(1, configuration.GetValue("Controller:BackupHours", 12));
        using var timer = new PeriodicTimer(TimeSpan.FromHours(hours));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CreateNowAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Scheduled backup failed.");
            }
        }
    }
}

