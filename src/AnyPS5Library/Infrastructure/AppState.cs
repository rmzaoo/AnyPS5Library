using System.Collections.ObjectModel;
using System.Text.Json;
using PS5PKGTool.Core.Models;

namespace AnyPS5Library.Infrastructure;

public sealed partial class AppSettings
{
    public List<string> LibraryFolders { get; set; } = [];
    public bool RecursiveScan { get; set; } = true;
    public string DebugPasscode { get; set; } = string.Empty;
    public string WorkspaceFolder { get; set; } = string.Empty;

    /// <summary>Path to the AnyPS5 relinker executable (build/core/relinker/relinker). Optional.</summary>
    public string AnyPs5Relinker { get; set; } = string.Empty;
    /// <summary>AnyPS5 system libraries folder (build/core/libs/libs) copied into the output's libs/.</summary>
    public string AnyPs5Libs { get; set; } = string.Empty;
    public int AnyPs5Jobs { get; set; } = 4;
    public bool AnyPs5ExecutablesOnly { get; set; }
    public bool AnyPs5AutoRelink { get; set; } = true;
    public bool RevealOutputWhenDone { get; set; } = true;
}

/// <summary>Settings, library manifest and task history under ~/Library/Application Support.</summary>
public static class AppState
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Application Support", "AnyPS5Library");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public static string ManifestPath => Path.Combine(DataDirectory, "library.json");
    public static string TasksPath => Path.Combine(DataDirectory, "tasks.json");
    public static string LogPath => Path.Combine(DataDirectory, "anyps5library.log");

    public static AppSettings LoadSettings() => Load<AppSettings>(SettingsPath) ?? new AppSettings();
    public static void SaveSettings(AppSettings settings) => Save(SettingsPath, settings);

    public static List<Ps5GameInfo> LoadManifest() => Load<List<Ps5GameInfo>>(ManifestPath) ?? [];
    public static void SaveManifest(IEnumerable<Ps5GameInfo> games) => Save(ManifestPath, games.ToList());

    private static T? Load<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or NotSupportedException)
        {
            Log.Info($"Could not read {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    private static void Save<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Info($"Could not save {Path.GetFileName(path)}: {ex.Message}");
        }
    }
}

public static class Log
{
    private static readonly object Gate = new();

    /// <summary>Recent lines for the Log tab. Mutated on the UI thread only, via <see cref="LineAdded"/>.</summary>
    public static ObservableCollection<string> Lines { get; } = [];

    public static event Action<string>? LineAdded;

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Exception(string context, Exception ex) => Write("ERROR", $"{context}: {ex}");

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss} [{level}] {message}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppState.DataDirectory);
                File.AppendAllText(AppState.LogPath, $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        LineAdded?.Invoke(line);
    }
}
