using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media.Imaging;

namespace AnyPS5Library.Infrastructure;

public sealed class CatalogArt
{
    public string? Cover { get; set; }
    public string? Hero { get; set; }
    public string? Logo { get; set; }
    public string? Tint { get; set; }
}

/// <summary>Game info imported from catalog.json (metadata only; see tools/import_metadata.py).</summary>
public sealed class CatalogGame
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string Developer { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string ReleaseDate { get; set; } = string.Empty;
    public int? Year { get; set; }
    public List<string> Genres { get; set; } = [];
    public List<string> Features { get; set; } = [];
    public int? Metacritic { get; set; }
    public CatalogArt? Art { get; set; }
}

public sealed class CatalogShot
{
    public string Url { get; set; } = string.Empty;
    public string Thumb { get; set; } = string.Empty;
}

public sealed class CatalogPage
{
    public string Summary { get; set; } = string.Empty;
    public List<string> About { get; set; } = [];
    public List<CatalogShot> Shots { get; set; } = [];
}

/// <summary>Looks up imported game info by title ID. Loaded once from the bundle's Metadata folder.</summary>
public static class GameMetadata
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private static readonly Lazy<Dictionary<string, CatalogGame>> Games = new(LoadGames);
    private static readonly Lazy<Dictionary<string, CatalogPage>> Pages = new(LoadPages);

    private static string Folder => Path.Combine(AppContext.BaseDirectory, "Metadata");

    public static int Count => Games.Value.Count;
    public static IEnumerable<CatalogGame> All => Games.Value.Values;

    public static CatalogGame? Find(string titleId, string contentId = "")
    {
        if (titleId.Length > 0 && Games.Value.TryGetValue(titleId, out CatalogGame? game)) return game;
        // Content IDs look like UP0001-PPSA01234_00-..., the title ID sits at offset 7.
        return contentId.Length >= 16 && Games.Value.TryGetValue(contentId.Substring(7, 9), out game) ? game : null;
    }

    public static CatalogPage? Page(string id) => Pages.Value.GetValueOrDefault(id);

    private static Dictionary<string, CatalogGame> LoadGames()
    {
        var map = new Dictionary<string, CatalogGame>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string path = Path.Combine(Folder, "catalog.json");
            if (!File.Exists(path)) return map;
            foreach (CatalogGame game in JsonSerializer.Deserialize<List<CatalogGame>>(File.ReadAllText(path), Options) ?? [])
                map.TryAdd(game.Id, game);
            Log.Info($"Game info catalog: {map.Count:N0} titles.");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error("Could not load the game info catalog: " + ex.Message);
        }
        return map;
    }

    private static Dictionary<string, CatalogPage> LoadPages()
    {
        try
        {
            string path = Path.Combine(Folder, "pages.json");
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, CatalogPage>>(File.ReadAllText(path), Options)
                  ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error("Could not load game descriptions: " + ex.Message);
            return new();
        }
    }
}

/// <summary>Downloads catalog artwork from the publishers' CDNs and caches it on disk.</summary>
public static class ArtCache
{
    private static readonly string[] AllowedHosts = ["image.api.playstation.com", "steamstatic.com"];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly string Folder = Path.Combine(AppState.DataDirectory, "art-cache");

    static ArtCache() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("AnyPS5Library/1.0");

    public static async Task<Bitmap?> LoadAsync(string? url, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !AllowedHosts.Any(h => uri.Host == h || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase)))
            return null;
        string file = Path.Combine(Folder, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))));
        try
        {
            byte[] bytes;
            if (File.Exists(file)) bytes = await File.ReadAllBytesAsync(file, token).ConfigureAwait(false);
            else
            {
                bytes = await Http.GetByteArrayAsync(uri, token).ConfigureAwait(false);
                Directory.CreateDirectory(Folder);
                await File.WriteAllBytesAsync(file, bytes, token).ConfigureAwait(false);
            }
            return new Bitmap(new MemoryStream(bytes));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException
                                       or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (!token.IsCancellationRequested) Log.Info($"Artwork unavailable ({uri.Host}): {ex.Message}");
            return null;
        }
    }
}
