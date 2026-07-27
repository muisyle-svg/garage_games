using System.Text.Json;
using GarageGames.Controller.Infrastructure;
using GarageGames.Core.Domain;

namespace GarageGames.Controller.Services;

public sealed class SeasonCatalog
{
    private readonly IReadOnlyDictionary<string, SeasonDefinition> _seasons;

    public SeasonCatalog(AppPaths paths)
    {
        if (!Directory.Exists(paths.SeasonDirectory))
        {
            throw new DirectoryNotFoundException($"Season directory not found: {paths.SeasonDirectory}");
        }

        _seasons = Directory
            .EnumerateFiles(paths.SeasonDirectory, "*.json")
            .Select(path =>
            {
                var content = File.ReadAllText(path);
                return JsonSerializer.Deserialize<SeasonDefinition>(content, JsonDefaults.Options)
                    ?? throw new InvalidDataException($"Could not parse season file {path}.");
            })
            .ToDictionary(season => season.Id, StringComparer.OrdinalIgnoreCase);

        if (_seasons.Count == 0)
        {
            throw new InvalidOperationException("At least one season definition is required.");
        }
    }

    public IReadOnlyList<SeasonDefinition> All => _seasons.Values.OrderBy(item => item.Id).ToArray();

    public SeasonDefinition Get(string id) =>
        _seasons.TryGetValue(id, out var season)
            ? season
            : throw new KeyNotFoundException($"Season '{id}' was not found.");
}

