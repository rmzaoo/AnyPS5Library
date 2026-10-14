using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PS5PKGTool.Core.Backends;
using PS5PKGTool.Core.Builders;
using PS5PKGTool.Core.Models;
using PS5PKGTool.Core.Parsers;
using PS5PKGTool.Core.Services;
using PS5PKGTool.Core.Tasks;
using PS5PKGTool.Ffpfsc;
using UFS2Tool;

namespace AnyPS5Library.Infrastructure;

/// <summary>Task type for the pkg-to-anyps5 export (plus optional AnyPS5 relink).</summary>
public static class AnyPs5TaskTypes
{
    public const string Export = "AnyPS5 export";
}

/// <summary>A queued job fully described by its payload, so it can be persisted and retried.</summary>
public sealed record TaskSpec(string Type, string DisplayName, string Operation, string SourceFormat,
    string TargetFormat, string SourcePath, string OutputPath, Dictionary<string, string> Fields);

/// <summary>
/// The operations behind every task. Ported from the Windows app's MainForm.Tasks / MainForm.ImageTools
/// (which were already UI-free statics). The LibProsperoPkg backend wraps Sony's mixed-mode
/// libScePubTools.dll and cannot run on macOS, so every package job uses ProsperoPkgTool.
/// </summary>
public static class Operations
{
    public static IPackageBackend Backend => BackendRegistry.Get(BackendRegistry.PptId);

    // ------------------------------------------------------------------ task construction

    public static QueuedPackageTask? CreateTask(TaskSpec spec, string? id = null, DateTime? created = null) =>
        CreateTask(id ?? Guid.NewGuid().ToString("N"), spec.Type, spec.DisplayName, spec.SourcePath,
            spec.OutputPath, spec.Operation, spec.SourceFormat, spec.TargetFormat, spec.Fields, created);

    public static QueuedPackageTask? Restore(PersistedPackageTask entry) =>
        CreateTask(entry.Id, entry.Type, entry.DisplayName, entry.SourcePath, entry.OutputPath, entry.Operation,
            entry.SourceFormat, entry.TargetFormat, ReadPayload(entry.Payload), entry.CreatedUtc);

    private static QueuedPackageTask CreateTask(string id, string type, string displayName, string source,
        string output, string operation, string sourceFormat, string targetFormat,
        Dictionary<string, string> fields, DateTime? created) => new()
    {
        Id = id,
        Type = type,
        DisplayName = displayName,
        SourcePath = source,
        OutputPath = output,
        PersistencePayload = JsonSerializer.Serialize(fields),
        Operation = operation,
        SourceFormat = sourceFormat,
        TargetFormat = targetFormat,
        StagePlan = PlanForType(type, fields),
        Execute = BuildExecute(type, fields),
        CreatedUtc = created is { } value && value != default ? value : DateTime.UtcNow
    };

    private static Dictionary<string, string> ReadPayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return new(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(payload) ?? new(StringComparer.Ordinal);
        }
        catch (JsonException) { return new(StringComparer.Ordinal); }
    }

    private static string Get(Dictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out string? value) ? value : string.Empty;

    private static int GetInt(Dictionary<string, string> fields, string key, int fallback = 0) =>
        fields.TryGetValue(key, out string? value) && int.TryParse(value, out int parsed) ? parsed : fallback;

    private static bool GetBool(Dictionary<string, string> fields, string key, bool fallback = false) =>
        fields.TryGetValue(key, out string? value) && bool.TryParse(value, out bool parsed) ? parsed : fallback;

    public static readonly PackageTaskStage[] AnyPs5Plan =
    [
        new("Stage image", "staging"),
        new("Unwrap / copy", "unwrap", "copy", "list", "read", "decod", "writ", "scan"),
        new("Relink", "relink"),
        new("Libraries", "librar")
    ];

    private static PackageTaskStage[] PlanForType(string type, Dictionary<string, string> fields) => type switch
    {
        PackageTaskTypes.ImageConvert => GetBool(fields, "package")
            ? PackageTaskPlans.ConvertPackage
            : PackageTaskPlans.ConvertImage,
        PackageTaskTypes.ImageBuildPackage => PackageTaskPlans.BuildPackageFor(Get(fields, "source")),
        PackageTaskTypes.ImageVerify or PackageTaskTypes.PackageVerify => PackageTaskPlans.Verify,
        PackageTaskTypes.PackageExtract => PackageTaskPlans.Extract,
        PackageTaskTypes.ExfatAmpr => PackageTaskPlans.Ampr,
        AnyPs5TaskTypes.Export => AnyPs5Plan,
        _ => PackageTaskPlans.Single
    };

    private static Func<IProgress<PackageTaskProgress>, CancellationToken, Task>? BuildExecute(string type,
        Dictionary<string, string> fields)
    {
        string source = Get(fields, "source");
        string output = Get(fields, "output");
        if (source.Length == 0) return null;
        string passcode = Get(fields, "passcode");
        if (passcode.Length == 0) passcode = SonyDebugPackageCredentials.DefaultPasscode;

        switch (type)
        {
            case PackageTaskTypes.ImageConvert:
            {
                if (output.Length == 0 || !Enum.TryParse(Get(fields, "target"), out Ps5ImageConversionTarget target))
                    return null;
                bool overwrite = GetBool(fields, "overwrite");
                bool fromPackage = GetBool(fields, "package");
                string? workspace = NullIfEmpty(Get(fields, "workspace"));
                ExfatBuildOptions? exfat = target == Ps5ImageConversionTarget.Ffpkg ? null : new ExfatBuildOptions
                {
                    ClusterSize = GetInt(fields, "cluster") is > 0 and var cluster ? cluster : null,
                    GenerateAmprIndex = GetBool(fields, "ampr")
                };
                FfpfscBuildOptions? ffpfsc = target == Ps5ImageConversionTarget.Ffpfsc ? new FfpfscBuildOptions
                {
                    Compression = new PfscCompressionOptions
                    {
                        CompressionLevel = GetInt(fields, "level", 6),
                        MinimumGainPercent = GetInt(fields, "gain", 2)
                    }
                } : null;
                return (progress, token) =>
                {
                    var bridge = new Progress<Ps5ImageConversionProgress>(value =>
                        progress.Report(new PackageTaskProgress(value.Stage, 0, 0, value.Completed, value.Total)));
                    return fromPackage
                        ? SonyPackageImageConversion.ConvertAsync(source, output, target, overwrite, bridge, token,
                            exfat, ffpfsc, null, workspace)
                        : Ps5ImageConversionService.ConvertAsync(source, output, target, overwrite, bridge, token,
                            exfat, ffpfsc, null, workspace);
                };
            }
            case PackageTaskTypes.PackageExtract:
            {
                if (output.Length == 0) return null;
                return Get(fields, "kind") switch
                {
                    "sony" => async (progress, token) => await Backend.ExtractAsync(source, output, passcode,
                        AdaptSonyExtract(progress), token).ConfigureAwait(false),
                    "exfat" => (progress, token) => ExtractImageToAsync(source, Ps5ImageFormat.Exfat, output, progress, token),
                    "ffpkg" => (progress, token) => ExtractImageToAsync(source, Ps5ImageFormat.Ufs2, output, progress, token),
                    "ffpfsc" => (progress, token) => ExtractImageToAsync(source, Ps5ImageFormat.Pfs, output, progress, token),
                    _ => null
                };
            }
            case PackageTaskTypes.ImageVerify:
                return Enum.TryParse(Get(fields, "format"), out Ps5ImageFormat format)
                    ? (_, token) => VerifyImageAsync(source, format, token)
                    : null;
            case PackageTaskTypes.PackageVerify:
                return (_, _) =>
                {
                    SonyDebugPackageValidationResult result = SonyDebugPackageBuilder.Validate(source, passcode);
                    if (!result.IsValid) throw new InvalidDataException(result.Message);
                    Log.Info($"Package verified: {result.IndexedFiles:N0} indexed file(s).");
                    return Task.CompletedTask;
                };
            case PackageTaskTypes.ImageBuildPackage:
            {
                string contentId = Get(fields, "contentId");
                if (output.Length == 0 || contentId.Length == 0) return null;
                var settings = new BuildSettings(
                    Enum.TryParse(Get(fields, "compression"), out Ps5InnerCompression compression)
                        ? compression : Ps5InnerCompression.Auto,
                    GetInt(fields, "krakenLevel", 7), GetInt(fields, "krakenThreads"), GetInt(fields, "playgo", 1),
                    GetBool(fields, "deterministic"), GetBool(fields, "fakeSign", true),
                    GetBool(fields, "rightSprx", true));
                bool overwrite = GetBool(fields, "overwrite");
                string? temp = NullIfEmpty(Get(fields, "temp"));
                return (progress, token) => BuildPackageFromSourceAsync(source, output, contentId, passcode,
                    overwrite, temp, settings, progress, token);
            }
            case PackageTaskTypes.ExfatRepair:
                return async (progress, token) =>
                    await ExfatImageMaintenance.RepairAsync(source, AdaptFfpfsc(progress), token).ConfigureAwait(false);
            case PackageTaskTypes.ExfatAmpr:
                return async (progress, token) =>
                    await ExfatAmprPatcher.RefreshAsync(source, AdaptFfpfsc(progress), token).ConfigureAwait(false);
            case PackageTaskTypes.FfpkgRebuild:
                return async (progress, token) => await Ufs2Operations.RebuildWithEditsAsync(source,
                    static _ => { }, AdaptUfs2(progress), token).ConfigureAwait(false);
            case AnyPs5TaskTypes.Export:
            {
                if (output.Length == 0) return null;
                var options = new AnyPs5Options(
                    ExecutablesOnly: GetBool(fields, "executablesOnly"),
                    Jobs: Math.Clamp(GetInt(fields, "jobs", 4), 1, 64),
                    Relinker: NullIfEmpty(Get(fields, "relinker")),
                    LibsFolder: NullIfEmpty(Get(fields, "libs")),
                    Passcode: passcode,
                    Workspace: NullIfEmpty(Get(fields, "workspace")));
                return (progress, token) => AnyPs5Exporter.ExportAsync(source, output, options, progress, token);
            }
            default:
                return null;
        }
    }

    private static string? NullIfEmpty(string value) => value.Length > 0 ? value : null;

    // ------------------------------------------------------------------ progress adapters

    public static IProgress<FfpfscProgress> AdaptFfpfsc(IProgress<PackageTaskProgress> target) =>
        new Progress<FfpfscProgress>(value => target.Report(new PackageTaskProgress(
            value.Stage, 0, 0, value.BytesProcessed, value.TotalBytes)));

    public static IProgress<Ufs2Progress> AdaptUfs2(IProgress<PackageTaskProgress> target) =>
        new Progress<Ufs2Progress>(value => target.Report(new PackageTaskProgress(
            value.Stage, 0, 0, value.BytesProcessed, value.TotalBytes,
            (int)Math.Min(value.Completed, int.MaxValue), (int)Math.Min(value.Total, int.MaxValue), value.Unit)));

    public static IProgress<SonyPackageExtractProgress> AdaptSonyExtract(IProgress<PackageTaskProgress> target) =>
        new Progress<SonyPackageExtractProgress>(value => target.Report(new PackageTaskProgress(
            "Extracting", 0, 0, value.CompletedBytes, value.TotalBytes, 0, 0, value.CurrentPath)));

    // ------------------------------------------------------------------ image helpers

    public static async Task ExtractImageToAsync(string source, Ps5ImageFormat format, string output,
        IProgress<PackageTaskProgress> progress, CancellationToken token)
    {
        switch (format)
        {
            case Ps5ImageFormat.Exfat:
                await ExfatImage.ExtractDirectoryAsync(source, output, AdaptFfpfsc(progress), token).ConfigureAwait(false);
                break;
            case Ps5ImageFormat.Ufs2:
                await Ufs2Operations.ExtractAsync(source, output, AdaptUfs2(progress), token).ConfigureAwait(false);
                break;
            case Ps5ImageFormat.Pfs:
                await FfpfscImage.ExtractToDirectoryAsync(source, output, overwrite: true, AdaptFfpfsc(progress), token)
                    .ConfigureAwait(false);
                break;
            default:
                throw new InvalidDataException("Unsupported source image format.");
        }
    }

    public static async Task VerifyImageAsync(string source, Ps5ImageFormat format, CancellationToken token)
    {
        switch (format)
        {
            case Ps5ImageFormat.Exfat:
                await ExfatImage.VerifyAsync(source, null, token).ConfigureAwait(false);
                break;
            case Ps5ImageFormat.Ufs2:
                await Ufs2Operations.VerifyAsync(source, null, token).ConfigureAwait(false);
                break;
            case Ps5ImageFormat.Pfs:
                FfpfscVerificationResult result = await FfpfscImage.TryVerifyAsync(source, null, null, token)
                    .ConfigureAwait(false);
                if (!result.StructureValid)
                    throw new InvalidDataException("The FFPFSC structure is invalid. " + result.Error);
                if (!result.EveryPfscBlockDecodes)
                    throw new InvalidDataException("The FFPFSC block decode failed. " + result.Error);
                break;
            default:
                throw new InvalidDataException("Unsupported image format.");
        }
    }

    public static SonyPkgKind? TryReadPackageKind(string path)
    {
        try { return new SonyPkgReader().Read(path).Kind; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException or EndOfStreamException)
        { return null; }
    }

    public readonly record struct ParamFields(string ContentId, string TitleId, string ContentVersion, string TitleName);

    public static ParamFields TryReadParamFields(string source)
    {
        byte[]? bytes = TryReadParamBytes(source);
        if (bytes is null || bytes.Length == 0) return default;
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return default;
            return new ParamFields(ReadString(root, "contentId"), ReadString(root, "titleId"),
                ReadString(root, "contentVersion"), ReadString(root, "titleName"));
        }
        catch (JsonException) { return default; }
    }

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? string.Empty
            : string.Empty;

    private static byte[]? TryReadParamBytes(string source)
    {
        try
        {
            if (Directory.Exists(source))
            {
                string path = Path.Combine(source, "sce_sys", "param.json");
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            switch (Ps5ImageFormatProbe.Detect(source))
            {
                case Ps5ImageFormat.Exfat:
                    using (var volume = new ExfatVolume(File.OpenRead(source)))
                        return volume.ReadAllBytes("sce_sys/param.json", 4 * 1024 * 1024);
                case Ps5ImageFormat.Ufs2:
                    using (var volume = new Ufs2Volume(source))
                        return volume.ReadAllBytes("sce_sys/param.json", 4 * 1024 * 1024);
                case Ps5ImageFormat.Pfs:
                    using (var volume = FfpfscVolume.Open(source))
                        return volume.ReadAllBytes("sce_sys/param.json", 4 * 1024 * 1024);
                default:
                    return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                       ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ package build

    public readonly record struct BuildSettings(Ps5InnerCompression Compression, int KrakenLevel, int KrakenThreads,
        int PlayGoChunks, bool Deterministic, bool FakeSignModules, bool InjectRightSprx);

    private static byte[] DeterministicSeed(string contentId, string passcode) =>
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(contentId + "\0" + passcode))[..16];

    private static async Task BuildPackageFromSourceAsync(string source, string output, string contentId,
        string passcode, bool overwrite, string? tempDirectory, BuildSettings settings,
        IProgress<PackageTaskProgress> progress, CancellationToken token)
    {
        if (!overwrite && File.Exists(output))
            throw new IOException($"The output file already exists: {output}");
        string outputDirectory = Path.GetDirectoryName(Path.GetFullPath(output)) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outputDirectory);
        string jobDirectory = Path.Combine(outputDirectory, ".anyps5library-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(jobDirectory);
        string partial = Path.Combine(jobDirectory, Path.GetFileName(output) + ".partial");
        var options = new SonyDebugPackageBuildOptions
        {
            ContentId = contentId,
            Passcode = passcode,
            TempDirectory = tempDirectory,
            Log = Log.Info,
            Compression = settings.Compression,
            KrakenLevel = settings.KrakenLevel,
            KrakenThreads = settings.KrakenThreads,
            PlayGoChunkCount = settings.PlayGoChunks,
            Seed = settings.Deterministic ? DeterministicSeed(contentId, passcode) : null,
            FakeSignModules = settings.FakeSignModules,
            InjectRightSprx = settings.InjectRightSprx
        };
        var bridge = new Progress<SonyDebugPackageProgress>(value => progress.Report(new PackageTaskProgress(
            value.Stage, 0, 0, value.CompletedBytes, value.TotalBytes, 0, 0, value.CurrentPath)));
        try
        {
            if (Directory.Exists(source))
                await Backend.BuildFromDirectoryAsync(source, partial, options, bridge, token).ConfigureAwait(false);
            else
                await Backend.BuildFromImageAsync(source, partial, options, bridge, token).ConfigureAwait(false);

            PackageReaderVerificationResult result = PackageReaderVerification.Inspect(partial, passcode);
            Log.Info($"Verified built package: {result.FileCount:N0} file(s), eboot.bin " +
                     (result.HasEboot ? "present" : "missing") + ".");
            if (result.FileCount == 0 || !result.HasEboot)
                throw new InvalidDataException("The built package failed verification (empty or missing eboot.bin).");
            token.ThrowIfCancellationRequested();
            File.Move(partial, output, overwrite);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
        finally
        {
            try { if (Directory.Exists(jobDirectory)) Directory.Delete(jobDirectory, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static string AvailableDirectory(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path)) return path;
        for (int i = 2; ; i++)
        {
            string candidate = $"{path} ({i})";
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : value.ToString("0.##", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}

public sealed record AnyPs5Options(bool ExecutablesOnly, int Jobs, string? Relinker, string? LibsFolder, string Passcode,
    string? Workspace);

/// <summary>
/// Runs the bundled pkg-to-anyps5 (awake-devel/pkg-to-anyps5, GPL-3.0, executed as a separate
/// process) to turn a debug package or dump folder into the AnyPS5 layout, then optionally runs the
/// AnyPS5 relinker on source/eboot.bin and copies AnyPS5's system libraries into libs/. Filesystem
/// images (exFAT / FFPKG / FFPFSC) are first extracted to a staging folder, since pkg-to-anyps5 reads
/// only packages and folders.
/// </summary>
public static partial class AnyPs5Exporter
{
    public const string ToolName = "pkg-to-anyps5";

    public static string? FindTool()
    {
        string bundled = Path.Combine(AppContext.BaseDirectory, ToolName);
        if (File.Exists(bundled)) return bundled;
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(':'))
        {
            if (directory.Length == 0) continue;
            string candidate = Path.Combine(directory, ToolName);
            if (File.Exists(candidate)) return candidate;
        }
        string cargo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cargo", "bin", ToolName);
        return File.Exists(cargo) ? cargo : null;
    }

    /// <summary>True when the source is something the exporter accepts (debug pkg, dump folder or image).</summary>
    public static bool CanExport(string path) =>
        Directory.Exists(path) ||
        (File.Exists(path) && (Path.GetExtension(path).Equals(".pkg", StringComparison.OrdinalIgnoreCase)
            ? Operations.TryReadPackageKind(path) == SonyPkgKind.FinalizedDebug
            : Ps5ImageFormatProbe.Detect(path) is Ps5ImageFormat.Exfat or Ps5ImageFormat.Ufs2 or Ps5ImageFormat.Pfs));

    public static async Task ExportAsync(string source, string output, AnyPs5Options options,
        IProgress<PackageTaskProgress> progress, CancellationToken token)
    {
        string tool = FindTool() ?? throw new FileNotFoundException(
            "pkg-to-anyps5 was not found inside the app bundle or on PATH.");
        string? staging = null;
        try
        {
            string input = source;
            if (File.Exists(source) && !Path.GetExtension(source).Equals(".pkg", StringComparison.OrdinalIgnoreCase))
            {
                Ps5ImageFormat format = Ps5ImageFormatProbe.Detect(source);
                staging = Path.Combine(options.Workspace ?? Path.GetTempPath(),
                    "AnyPS5Library-stage-" + Guid.NewGuid().ToString("N"));
                Log.Info($"AnyPS5: staging {format} image {Path.GetFileName(source)} to {staging}");
                var stage = new Progress<PackageTaskProgress>(value =>
                    progress.Report(value with { Stage = "Staging: " + value.Stage }));
                await Operations.ExtractImageToAsync(source, format, staging, stage, token).ConfigureAwait(false);
                input = staging;
            }

            List<string> Arguments(string from)
            {
                List<string> list = [from, output, "--verbose", "--jobs", options.Jobs.ToString(CultureInfo.InvariantCulture)];
                if (options.ExecutablesOnly) list.Add("--executables-only");
                return list;
            }

            try
            {
                await RunAsync(tool, Arguments(input), line => ReportToolLine(line, progress), token).ConfigureAwait(false);
            }
            catch (ToolFailedException ex) when (input == source && ex.Output.Contains("encrypted", StringComparison.OrdinalIgnoreCase))
            {
                // pkg-to-anyps5 reads only plain-seed (PPRPLAIN-NOAUTH!) packages. A passcode-encrypted
                // debug package is decrypted with the PS5 PKG Tool engine instead, then converted as a folder.
                staging = Path.Combine(options.Workspace ?? Path.GetTempPath(),
                    "AnyPS5Library-stage-" + Guid.NewGuid().ToString("N"));
                Log.Info("AnyPS5: package outer PFS is encrypted; decrypting it with the passcode to " + staging);
                var stage = new Progress<SonyPackageExtractProgress>(value => progress.Report(new PackageTaskProgress(
                    "Staging: decrypting package", 0, 0, value.CompletedBytes, value.TotalBytes, 0, 0, value.CurrentPath)));
                await Operations.Backend.ExtractAsync(source, staging, options.Passcode, stage, token).ConfigureAwait(false);
                string root = StagedGameRoot(staging);
                await RunAsync(tool, Arguments(root), line => ReportToolLine(line, progress), token).ConfigureAwait(false);
            }

            string eboot = Path.Combine(output, "source", "eboot.bin");
            if (options.Relinker is { } relinker)
            {
                if (!File.Exists(relinker)) throw new FileNotFoundException("The AnyPS5 relinker was not found.", relinker);
                if (!File.Exists(eboot)) throw new FileNotFoundException("pkg-to-anyps5 produced no source/eboot.bin.", eboot);
                progress.Report(new PackageTaskProgress("Relinking eboot.bin", StagePercent: 0));
                string appElf = Path.Combine(output, "app.elf");
                await RunAsync(relinker, [eboot, appElf], line => Log.Info("relinker: " + line), token)
                    .ConfigureAwait(false);
                progress.Report(new PackageTaskProgress("Relinking eboot.bin", StagePercent: 1));
                Log.Info($"AnyPS5: relinked to {appElf}");
            }
            if (options.LibsFolder is { } libs && Directory.Exists(libs))
            {
                string libsOut = Path.Combine(output, "libs");
                Directory.CreateDirectory(libsOut);
                string[] prx = Directory.GetFiles(libs, "*.prx");
                for (int i = 0; i < prx.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    File.Copy(prx[i], Path.Combine(libsOut, Path.GetFileName(prx[i])), true);
                    progress.Report(new PackageTaskProgress("Copying AnyPS5 libraries", CurrentItem: i + 1,
                        TotalItems: prx.Length, CurrentFile: Path.GetFileName(prx[i])));
                }
                Log.Info($"AnyPS5: copied {prx.Length} system librar{(prx.Length == 1 ? "y" : "ies")} to {libsOut}");
            }
        }
        finally
        {
            if (staging is not null)
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    // Plain progress line: "  <phase>  12.3%  1.2 GiB / 4.0 GiB  210 MiB/s  ETA 00:12  3/10 files"
    [GeneratedRegex(@"^\s*(?<phase>.+?)\s{2,}(?<pct>\d+(?:\.\d+)?)%(?:\s{2,}(?<rest>.*))?$")]
    private static partial Regex ProgressLine();

    private static readonly Regex FilesPart = new(@"(?<done>[\d,]+)/(?<total>[\d,]+) files", RegexOptions.Compiled);

    private static void ReportToolLine(string line, IProgress<PackageTaskProgress> progress)
    {
        Match match = ProgressLine().Match(line);
        if (match.Success && double.TryParse(match.Groups["pct"].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double percent))
        {
            string phase = match.Groups["phase"].Value.Trim();
            int done = 0, total = 0;
            Match files = FilesPart.Match(match.Groups["rest"].Value);
            if (files.Success)
            {
                int.TryParse(files.Groups["done"].Value.Replace(",", ""), out done);
                int.TryParse(files.Groups["total"].Value.Replace(",", ""), out total);
            }
            progress.Report(new PackageTaskProgress(phase, CurrentItem: done, TotalItems: total,
                CurrentFile: match.Groups["rest"].Value.Trim(), StagePercent: percent / 100d));
            return;
        }
        if (line.Trim().Length > 0)
        {
            Log.Info("pkg-to-anyps5: " + line.Trim());
            progress.Report(new PackageTaskProgress(line.Trim().Length > 80 ? line.Trim()[..80] : line.Trim(),
                StagePercent: -1));
        }
    }

    private static async Task RunAsync(string executable, IReadOnlyList<string> arguments, Action<string> onLine,
        CancellationToken token)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["NO_COLOR"] = "1";
        Log.Info($"Running {Path.GetFileName(executable)} {string.Join(' ', arguments.Select(Quote))}");

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var errors = new List<string>();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (errors) errors.Add(e.Data);
            onLine(e.Data);
        };
        if (!process.Start()) throw new InvalidOperationException($"Could not start {executable}.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        if (process.ExitCode != 0)
        {
            string detail;
            lock (errors) detail = string.Join(Environment.NewLine, errors.TakeLast(8));
            throw new ToolFailedException(
                $"{Path.GetFileName(executable)} exited with code {process.ExitCode}." +
                (detail.Length > 0 ? Environment.NewLine + detail : string.Empty), detail);
        }
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    /// <summary>
    /// The engine's extraction writes the inner filesystem; the game tree (eboot.bin at its top) may sit
    /// in a sub-folder such as uroot/. Returns the folder that holds eboot.bin.
    /// </summary>
    private static string StagedGameRoot(string staging)
    {
        if (File.Exists(Path.Combine(staging, "eboot.bin"))) return staging;
        string? eboot = Directory.EnumerateFiles(staging, "eboot.bin", SearchOption.AllDirectories)
            .OrderBy(path => path.Count(c => c == Path.DirectorySeparatorChar))
            .FirstOrDefault();
        return eboot is null
            ? throw new InvalidDataException("The decrypted package contains no eboot.bin.")
            : Path.GetDirectoryName(eboot)!;
    }

    public sealed class ToolFailedException(string message, string output) : InvalidOperationException(message)
    {
        public string Output { get; } = output;
    }
}
