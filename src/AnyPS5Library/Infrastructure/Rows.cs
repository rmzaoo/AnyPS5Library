using System.ComponentModel;
using System.Runtime.CompilerServices;
using PS5PKGTool.Core.Models;
using PS5PKGTool.Core.Tasks;

namespace AnyPS5Library.Infrastructure;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

/// <summary>One library entry as shown in the grid.</summary>
public sealed class GameRow(Ps5GameInfo game)
{
    public Ps5GameInfo Game { get; } = game;
    public CatalogGame? Info { get; } = GameMetadata.Find(game.TitleId, game.ContentId);
    public string Genres => Info is null ? "" : string.Join(", ", Info.Genres);
    public string Developer => Info?.Developer ?? "";
    public int Score => Info?.Metacritic ?? 0;
    public string ScoreText => Info?.Metacritic is { } score ? score.ToString() : "";
    public string Year => Info?.Year is { } year ? year.ToString() : "";
    public string Title => Game.Title.Length > 0 ? Game.Title
        : Info?.Title is { Length: > 0 } title ? title
        : System.IO.Path.GetFileName(Game.RootPath);
    public string TitleId => Game.TitleId;
    public string Version => Game.DisplayVersion;
    public string Category => Game.ApplicationCategory;
    public string Source => Game.SourceDescription;
    public string Firmware => Game.RequiredSystemSoftware;
    public long SizeBytes => Game.SourceSize > 0 ? Game.SourceSize : Game.GameRootBytes;
    public string Size => SizeBytes > 0 ? Operations.FormatBytes(SizeBytes) : "";
    public string Path => Game.RootPath;
    public string AnyPs5 => Compatibility.StageOf(TitleId.Length > 0 ? TitleId : Info?.Id);
    public int AnyPs5Rank => Compatibility.Rank(AnyPs5);

    public bool Matches(string query)
    {
        if (query.Length == 0) return true;
        foreach (string token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            bool hit = Title.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                       TitleId.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                       Game.ContentId.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                       Source.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                       Category.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                       Genres.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                       Developer.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                       (Info?.Publisher.Contains(token, StringComparison.OrdinalIgnoreCase) ?? false) ||
                       Path.Contains(token, StringComparison.OrdinalIgnoreCase);
            if (!hit) return false;
        }
        return true;
    }
}

/// <summary>One catalog.json game as shown in the Catalog tab.</summary>
public sealed partial class CatalogRow(CatalogGame game)
{
    public CatalogGame Game { get; } = game;
    public string Title => Game.Title;
    public string Id => Game.Id;
    public string Platform => Game.Platform.ToUpperInvariant();
    public string Genres => string.Join(", ", Game.Genres);
    public string Developer => Game.Developer;
    public string Publisher => Game.Publisher;
    public int Score => Game.Metacritic ?? 0;
    public string ScoreText => Game.Metacritic is { } score ? score.ToString() : "";
    public string Year => Game.Year is { } year ? year.ToString() : "";
    public string AnyPs5 => Compatibility.StageOf(Game.Id);
    public int AnyPs5Rank => Compatibility.Rank(AnyPs5);

    public bool Matches(string query)
    {
        foreach (string token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (!(Title.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                  Id.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                  Platform.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                  Genres.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                  Developer.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                  Publisher.Contains(token, StringComparison.OrdinalIgnoreCase)))
                return false;
        return true;
    }
}

/// <summary>One game of the AnyPS5 blocker map as shown in the Compatibility tab.</summary>
public sealed class CompatRow(RoadmapGame game, bool owned)
{
    public RoadmapGame Game { get; } = game;
    public string Name => Game.Name ?? Game.TitleIds.FirstOrDefault() ?? Game.Id;
    public string TitleIds => string.Join(", ", Game.TitleIds);
    public string Stage => Game.Stage?.Stage ?? "Not reported";
    public int StageRank => Compatibility.Rank(Game.Stage?.Stage);
    public string Confidence => Game.Confidence?.Label ?? Game.Confidence?.Level ?? "";
    public string Claim => Game.UnverifiedClaim?.Stage is { } claim ? claim + "?" : "";
    public string StopsAt => Game.OpenBlockers.Count > 0 && Compatibility.Blocker(Game.OpenBlockers[0].Id) is { } b
        ? $"#{b.Id} {b.Title}"
        : Game.OpenBlockers.Count > 0 ? "#" + Game.OpenBlockers[0].Id : "";
    public string Tags => string.Join(", ", Game.Tags);
    public string LastActivity => Game.LastEvidence?.Date is { Length: >= 10 } date ? date[..10] : "";
    public bool Owned { get; } = owned;
    public string OwnedText => Owned ? "✓" : "";

    public bool Matches(string query)
    {
        foreach (string token in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (!(Name.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                  TitleIds.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                  Game.Aliases.Any(a => a.Contains(token, StringComparison.OrdinalIgnoreCase)) ||
                  Tags.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                  StopsAt.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                  (Game.Engine?.Contains(token, StringComparison.OrdinalIgnoreCase) ?? false)))
                return false;
        return true;
    }
}

public sealed class FileRow(string relativePath, long size, string origin)
{
    public string RelativePath { get; } = relativePath;
    public long SizeBytes { get; } = size;
    public string Size => Operations.FormatBytes(SizeBytes);
    public string Origin { get; } = origin;
}

public sealed class KeyValueRow(string key, string value)
{
    public string Key { get; } = key;
    public string Value { get; } = value;
}

public sealed class TrophyRow(Ps5Trophy trophy)
{
    public int Id => trophy.Id;
    public string Grade => trophy.Grade;
    public string Name => trophy.Name;
    public string Description => trophy.Description;
    public string Hidden => trophy.Hidden ? "Yes" : "";
}

/// <summary>Live view of a queued task; refreshed from the queue's Changed events.</summary>
public sealed class TaskRow(QueuedPackageTask task) : Observable
{
    public QueuedPackageTask Task { get; } = task;
    public string Name => Task.DisplayName;
    public string Operation => Task.Operation.Length > 0 ? Task.Operation : Task.Type;
    public string Route => Task.FormatRoute;
    public string Status => Task.Status switch
    {
        PackageTaskStatus.Queued => "Queued",
        PackageTaskStatus.Running => "Running",
        PackageTaskStatus.Cancelling => "Cancelling",
        PackageTaskStatus.Completed => "Done",
        PackageTaskStatus.Failed => "Failed",
        PackageTaskStatus.Cancelled => "Cancelled",
        _ => "Interrupted"
    };

    public double Percent => Task.Status == PackageTaskStatus.Completed ? 100 : Task.Progress.TaskPercent * 100d;
    public string PercentText => Task.Status is PackageTaskStatus.Running or PackageTaskStatus.Cancelling
        ? $"{Percent:N0}%"
        : Status;

    public string Stage
    {
        get
        {
            if (Task.Status == PackageTaskStatus.Failed)
                return Task.Failure?.Message ?? Task.Message;
            string stage = Task.Progress.Stage;
            PackageTaskProgress p = Task.Progress;
            if (p.TotalBytes > 0)
                stage += $"  {Operations.FormatBytes(p.CurrentBytes)} / {Operations.FormatBytes(p.TotalBytes)}";
            else if (p.TotalItems > 0)
                stage += $"  {p.CurrentItem:N0} / {p.TotalItems:N0}";
            return stage;
        }
    }

    public string Elapsed
    {
        get
        {
            if (Task.StartedUtc is not { } start) return "";
            TimeSpan span = (Task.CompletedUtc ?? DateTime.UtcNow) - start;
            return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
        }
    }

    public void Refresh() => RaiseAll();
}
