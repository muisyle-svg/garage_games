using Microsoft.Data.Sqlite;

namespace GarageGames.V2;

public static class DataDirectoryMigration
{
    private const string DatabaseFileName = "garage-games-v2.db";
    private const string LifetimeLockFileName = ".garage-games-v2.lock";

    public static bool CopyLegacyDataIfNeeded(string dataDirectory, string? legacyDataDirectory)
    {
        if (string.IsNullOrWhiteSpace(legacyDataDirectory)) return false;

        var target = Path.GetFullPath(dataDirectory);
        var source = Path.GetFullPath(legacyDataDirectory);
        if (string.Equals(target, source, StringComparison.OrdinalIgnoreCase)) return false;

        var sourceDatabase = Path.Combine(source, DatabaseFileName);
        if (!File.Exists(sourceDatabase)) return false;

        var targetDatabase = Path.Combine(target, DatabaseFileName);
        if (File.Exists(targetDatabase))
        {
            // The standard application-data location is authoritative when it
            // already has a database. Keep the legacy copy intact, but don't
            // block startup or overwrite either store.
            return false;
        }
        if (Directory.Exists(target))
        {
            throw new InvalidOperationException(
                $"The new Garage Games data folder already exists without a database, so the older history was not copied over it. Neither store was changed. Review '{target}' and '{source}' before starting again.");
        }

        var sourceLockPath = Path.Combine(source, LifetimeLockFileName);
        if (!File.Exists(sourceLockPath))
        {
            throw new InvalidOperationException(
                $"The older Garage Games data folder could not be safely checked for an active app. Close any older Garage Games instance and try again. No files were changed. Older data: '{source}'.");
        }

        FileStream sourceLock;
        try
        {
            sourceLock = new FileStream(sourceLockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                $"The older Garage Games data folder is currently in use. Close that instance before moving its history. No files were changed. Older data: '{source}'.", exception);
        }

        using (sourceLock)
        {
            SqliteConnection.ClearAllPools();
            var parent = Path.GetDirectoryName(target)
                ?? throw new InvalidOperationException("The new Garage Games data folder has no parent directory.");
            Directory.CreateDirectory(parent);
            var staging = Path.Combine(parent, $".GarageGamesV2-migration-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileName(file), LifetimeLockFileName, StringComparison.OrdinalIgnoreCase)) continue;
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;

                var relativePath = Path.GetRelativePath(source, file);
                var destination = Path.Combine(staging, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: false);
            }

            if (!File.Exists(Path.Combine(staging, DatabaseFileName)))
            {
                throw new InvalidOperationException(
                    $"The older Garage Games database could not be copied completely. The original remains untouched at '{source}', and the partial copy was preserved at '{staging}'.");
            }

            try
            {
                Directory.Move(staging, target);
            }
            catch (IOException exception)
            {
                throw new InvalidOperationException(
                    $"The older Garage Games data was copied to '{staging}', but the new data folder could not be activated. The original remains untouched at '{source}'. No existing folder was overwritten.", exception);
            }
        }

        return true;
    }
}
