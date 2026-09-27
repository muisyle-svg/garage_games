using System.Security.Cryptography;
using System.Text;

namespace GarageGames.V2;

public static class BuildIdentity
{
    public static string Compute(IEnumerable<(string Name, string Path)> roots)
    {
        var records = new List<string>();
        foreach (var (name, path) in roots)
        {
            var fullRoot = System.IO.Path.GetFullPath(path);
            foreach (var file in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories))
            {
                var relative = System.IO.Path.GetRelativePath(fullRoot, file);
                var segments = relative.Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries);
                if (segments.Any(segment => segment is "bin" or "obj")) continue;
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;

                using var stream = File.OpenRead(file);
                var contentHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                records.Add($"{name}/{relative.Replace('\\', '/')}|{contentHash}");
            }
        }

        records.Sort(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", records))))
            .ToLowerInvariant();
    }

    public static string? TryComputeForSourceProject(string applicationDirectory)
    {
        var candidate = new DirectoryInfo(System.IO.Path.GetFullPath(applicationDirectory));
        while (candidate is not null)
        {
            if (File.Exists(System.IO.Path.Combine(candidate.FullName, "GarageGames.V2.csproj")))
            {
                var editionDirectory = candidate.Parent?.Parent is { } versionDirectory
                    ? System.IO.Path.Combine(versionDirectory.FullName, "config")
                    : null;
                if (editionDirectory is not null && Directory.Exists(editionDirectory))
                {
                    return Compute(
                    [
                        ("application", candidate.FullName),
                        ("edition", editionDirectory)
                    ]);
                }
            }

            candidate = candidate.Parent;
        }

        return null;
    }
}
