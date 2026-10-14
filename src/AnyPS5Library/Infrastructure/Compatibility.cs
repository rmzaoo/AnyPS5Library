using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnyPS5Library.Infrastructure;

// Subset of roadmap.json (schema_version 2) from https://alxcbtw.github.io/anyps5-roadmap/ (MIT, AlxcBTW and
// contributors). Only the fields the Compatibility tab shows are mapped; everything else is ignored.

public sealed class RoadmapStageInfo
{
    public string Name { get; set; } = string.Empty;
    public int Rank { get; set; }
    public string Meaning { get; set; } = string.Empty;
}

public sealed class RoadmapImage
{
    public string? Square { get; set; }
    public string? Wide { get; set; }
    public string? Portrait { get; set; }
}

public sealed class RoadmapTitle
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Region { get; set; }
    public string? Platform { get; set; }
    public string? Stage { get; set; }
}

/// <summary>A stage report: the verified stage, an unverified claim, or the stage reached on PR branches.</summary>
public sealed class RoadmapStage
{
    public string? Stage { get; set; }
    public string? Os { get; set; }
    public string? Gpu { get; set; }
    public string? Build { get; set; }
    public string? Url { get; set; }
    public string? Date { get; set; }
    public string? Note { get; set; }
    public string? Quote { get; set; }
}

public sealed class RoadmapConfidence
{
    public string? Level { get; set; }
    public string? Label { get; set; }
    [JsonPropertyName("reports_on_main")] public int ReportsOnMain { get; set; }
}

public sealed class RoadmapGameBlocker
{
    public string Id { get; set; } = string.Empty;
    public string? Why { get; set; }
    public string? Title { get; set; }
}

public sealed class RoadmapEvidence
{
    public string? Url { get; set; }
    public string? Date { get; set; }
}

public sealed class RoadmapGame
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public List<string> Aliases { get; set; } = [];
    [JsonPropertyName("title_ids")] public List<string> TitleIds { get; set; } = [];
    public List<RoadmapTitle> Titles { get; set; } = [];
    [JsonPropertyName("store_url")] public string? StoreUrl { get; set; }
    public string? Kind { get; set; }
    public string? Engine { get; set; }
    public List<string> Tags { get; set; } = [];
    public RoadmapImage? Image { get; set; }
    public RoadmapStage? Stage { get; set; }
    public RoadmapConfidence? Confidence { get; set; }
    [JsonPropertyName("unverified_claim")] public RoadmapStage? UnverifiedClaim { get; set; }
    [JsonPropertyName("stage_on_branches")] public RoadmapStage? StageOnBranches { get; set; }
    [JsonPropertyName("open_blockers")] public List<RoadmapGameBlocker> OpenBlockers { get; set; } = [];
    [JsonPropertyName("last_evidence")] public RoadmapEvidence? LastEvidence { get; set; }
}

public sealed class RoadmapBlocker
{
    public string Id { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string? Title { get; set; }
    public string? Area { get; set; }
    public string? Status { get; set; }
}

public sealed class Roadmap
{
    [JsonPropertyName("generated_at")] public string? GeneratedAt { get; set; }
    public List<RoadmapStageInfo> Stages { get; set; } = [];
    public List<RoadmapGame> Games { get; set; } = [];
    public List<RoadmapBlocker> Blockers { get; set; } = [];
}

/// <summary>
/// AnyPS5 compatibility from the community blocker map. Downloaded on launch and on demand; the last copy is kept on
/// disk so the tab works offline.
/// </summary>
public static class Compatibility
{
    public const string SiteUrl = "https://alxcbtw.github.io/anyps5-roadmap/";
    private const string DataUrl = SiteUrl + "roadmap.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly string CachePath = Path.Combine(AppState.DataDirectory, "roadmap.json");

    private static Dictionary<string, RoadmapGame> _byTitleId = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, RoadmapBlocker> _blockers = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, RoadmapStageInfo> _stages = new(StringComparer.OrdinalIgnoreCase);

    static Compatibility() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("AnyPS5Library/1.0");

    public static Roadmap? Current { get; private set; }

    /// <summary>Raised (possibly on a worker thread) after new data is loaded.</summary>
    public static event Action? Changed;

    public static string GameUrl(RoadmapGame game) => SiteUrl + "#/games/" + Uri.EscapeDataString(game.Id);

    public static RoadmapGame? Find(string? titleId) =>
        titleId is { Length: > 0 } && _byTitleId.TryGetValue(titleId, out RoadmapGame? game) ? game : null;

    public static RoadmapBlocker? Blocker(string id) => _blockers.GetValueOrDefault(id);

    public static RoadmapStageInfo? StageInfo(string? name) => name is null ? null : _stages.GetValueOrDefault(name);

    /// <summary>Stage rank for sorting: -1 when unknown.</summary>
    public static int Rank(string? stage) => StageInfo(stage)?.Rank ?? -1;

    /// <summary>The verified stage of a title; a title-specific stage wins over the game's best stage.</summary>
    public static string StageOf(string? titleId)
    {
        if (Find(titleId) is not { } game) return string.Empty;
        string? own = game.Titles.FirstOrDefault(t => t.Id.Equals(titleId, StringComparison.OrdinalIgnoreCase))?.Stage;
        return own ?? game.Stage?.Stage ?? "Not reported";
    }

    /// <summary>Shows the cached copy, if any. Call <see cref="RefreshAsync"/> afterwards for fresh data.</summary>
    public static async Task LoadCachedAsync()
    {
        try
        {
            if (!File.Exists(CachePath)) return;
            await using FileStream stream = File.OpenRead(CachePath);
            Apply(await JsonSerializer.DeserializeAsync<Roadmap>(stream, Options).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error("Could not read the cached compatibility data: " + ex.Message);
        }
    }

    /// <summary>Downloads the latest roadmap.json. Returns false (and keeps the current data) when that fails.</summary>
    public static async Task<bool> RefreshAsync(CancellationToken token = default)
    {
        try
        {
            byte[] bytes = await Http.GetByteArrayAsync(DataUrl, token).ConfigureAwait(false);
            Roadmap? roadmap = JsonSerializer.Deserialize<Roadmap>(bytes, Options);
            if (roadmap is null || roadmap.Games.Count == 0) throw new JsonException("no games in the file");
            Directory.CreateDirectory(AppState.DataDirectory);
            await File.WriteAllBytesAsync(CachePath, bytes, token).ConfigureAwait(false);
            Apply(roadmap);
            Log.Info($"Compatibility data: {roadmap.Games.Count:N0} games, snapshot {roadmap.GeneratedAt}.");
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                       or JsonException or UnauthorizedAccessException)
        {
            if (!token.IsCancellationRequested) Log.Error("Could not download compatibility data: " + ex.Message);
            return false;
        }
    }

    private static void Apply(Roadmap? roadmap)
    {
        if (roadmap is null) return;
        var byTitle = new Dictionary<string, RoadmapGame>(StringComparer.OrdinalIgnoreCase);
        foreach (RoadmapGame game in roadmap.Games)
        foreach (string id in game.TitleIds.Concat(game.Titles.Select(t => t.Id)))
            byTitle.TryAdd(id, game);
        var blockers = new Dictionary<string, RoadmapBlocker>(StringComparer.OrdinalIgnoreCase);
        foreach (RoadmapBlocker blocker in roadmap.Blockers) blockers.TryAdd(blocker.Id, blocker);
        var stages = new Dictionary<string, RoadmapStageInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (RoadmapStageInfo stage in roadmap.Stages) stages.TryAdd(stage.Name, stage);

        _byTitleId = byTitle;
        _blockers = blockers;
        _stages = stages;
        Current = roadmap;
        Changed?.Invoke();
    }
}
