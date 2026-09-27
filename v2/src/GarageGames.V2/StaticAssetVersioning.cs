using System.Security.Cryptography;
using System.Text;

namespace GarageGames.V2;

public static class StaticAssetVersioning
{
    public const string Placeholder = "__GG_ASSET_VERSION__";

    public static string ComputeVersion(string webRootPath)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var assets = Directory.EnumerateFiles(webRootPath, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".js" or ".css")
            .OrderBy(path => Path.GetRelativePath(webRootPath, path), StringComparer.OrdinalIgnoreCase);

        foreach (var path in assets)
        {
            var relativePath = Path.GetRelativePath(webRootPath, path).Replace(Path.DirectorySeparatorChar, '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
            hash.AppendData([0]);
            using var stream = File.OpenRead(path);
            var buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, bytesRead);
            }
            hash.AppendData([0]);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string StampHtml(string html, string version) =>
        html.Replace(Placeholder, version, StringComparison.Ordinal);
}
