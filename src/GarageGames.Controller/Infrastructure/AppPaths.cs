namespace GarageGames.Controller.Infrastructure;

public sealed class AppPaths
{
    public AppPaths(IHostEnvironment environment)
    {
        var overrideDirectory = Environment.GetEnvironmentVariable("GARAGE_GAMES_DATA");
        DataDirectory = string.IsNullOrWhiteSpace(overrideDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GarageGames")
            : Path.GetFullPath(overrideDirectory);
        BackupDirectory = Path.Combine(DataDirectory, "backups");
        ExportDirectory = Path.Combine(DataDirectory, "exports");
        DatabasePath = Path.Combine(DataDirectory, "garage-games.db");
        var packagedSeasons = Path.Combine(environment.ContentRootPath, "config", "seasons");
        var repositorySeasons = Path.GetFullPath(
            Path.Combine(environment.ContentRootPath, "..", "..", "config", "seasons"));
        SeasonDirectory = Directory.Exists(packagedSeasons)
            ? packagedSeasons
            : repositorySeasons;

        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(BackupDirectory);
        Directory.CreateDirectory(ExportDirectory);
    }

    public string DataDirectory { get; }
    public string BackupDirectory { get; }
    public string ExportDirectory { get; }
    public string DatabasePath { get; }
    public string SeasonDirectory { get; }
}
