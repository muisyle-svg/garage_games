using GarageGames.V2;

internal static class StaticAssetVersioningTests
{
    public static void ContentDerivedVersionAndStamping()
    {
        var webRoot = Path.Combine(Path.GetTempPath(), "GarageGamesV2Tests", $"assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(webRoot, "nested"));

        try
        {
            File.WriteAllText(Path.Combine(webRoot, "app.js"), "window.value = 1;");
            File.WriteAllText(Path.Combine(webRoot, "nested", "app.css"), "body { color: red; }");

            var initialVersion = StaticAssetVersioning.ComputeVersion(webRoot);
            AssertVersionHash(initialVersion);
            AssertEqual(initialVersion, StaticAssetVersioning.ComputeVersion(webRoot), "An unchanged asset set must produce a stable version.");

            File.WriteAllText(Path.Combine(webRoot, "index.html"), "HTML is stamped separately.");
            AssertEqual(initialVersion, StaticAssetVersioning.ComputeVersion(webRoot), "Non-JS/CSS files must not affect the asset version.");

            File.WriteAllText(Path.Combine(webRoot, "nested", "app.css"), "body { color: blue; }");
            var updatedVersion = StaticAssetVersioning.ComputeVersion(webRoot);
            AssertVersionHash(updatedVersion);
            if (updatedVersion == initialVersion)
            {
                throw new InvalidOperationException("Changing static asset content must change the version.");
            }

            const string html = "<script src=\"app.js?v=__GG_ASSET_VERSION__\"></script><!-- __GG_ASSET_VERSION__ -->";
            const string expected = "<script src=\"app.js?v=abc123\"></script><!-- abc123 -->";
            AssertEqual(expected, StaticAssetVersioning.StampHtml(html, "abc123"), "StampHtml must replace every version placeholder and preserve the rest of the HTML.");
        }
        finally
        {
            if (Directory.Exists(webRoot))
            {
                Directory.Delete(webRoot, recursive: true);
            }
        }
    }

    private static void AssertVersionHash(string version)
    {
        if (version.Length != 64 || version.Any(character => !Uri.IsHexDigit(character) || char.IsUpper(character)))
        {
            throw new InvalidOperationException("The asset version must be a lowercase SHA-256 hex digest.");
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected '{expected}', got '{actual}'.");
        }
    }
}
