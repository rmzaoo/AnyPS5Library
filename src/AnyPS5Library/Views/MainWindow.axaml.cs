using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AnyPS5Library.Infrastructure;
using PS5PKGTool.Core.Builders;
using PS5PKGTool.Core.Models;
using PS5PKGTool.Core.Services;
using PS5PKGTool.Core.Tasks;
using PS5PKGTool.Ffpfsc;

namespace AnyPS5Library.Views;

public partial class MainWindow : Window
{
    private const string ActionConvert = "Create / Convert";
    private const string ActionExtract = "Extract";
    private const string ActionVerify = "Verify";
    private const string ActionRepair = "Repair exFAT";
    private const string ActionAmpr = "Refresh AMPR index";
    private const string ActionRebuild = "Rebuild FFPKG";

    private const string TargetExfat = "exFAT image (.exfat)";
    private const string TargetFfpkg = "FFPKG image (.ffpkg)";
    private const string TargetFfpfsc = "FFPFSC image (.ffpfsc)";
    private const string TargetPkg = "Debug package (FPKG .pkg)";

    private readonly AppSettings _settings;
    private readonly PackageTaskQueue _queue = new();
    private readonly List<GameRow> _allGames = [];
    private readonly ObservableCollection<GameRow> _visibleGames = [];
    private readonly List<CatalogRow> _allCatalog = [];
    private readonly ObservableCollection<CatalogRow> _visibleCatalog = [];
    private readonly ObservableCollection<CompatRow> _visibleCompat = [];
    private CompatRow? _compatSelected;
    private CancellationTokenSource? _compatCts;
    private readonly ObservableCollection<TaskRow> _taskRows = [];
    private readonly ObservableCollection<string> _logLines = [];
    private readonly ObservableCollection<string> _folders = [];
    private readonly HashSet<string> _finishedTasks = [];
    private List<FileRow> _allFiles = [];
    private GameRow? _selected;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _detailsCts;
    private CatalogRow? _catSelected;
    private CancellationTokenSource? _catCts;
    private bool _tasksDirty = true;

    // Tools state
    private string? _toolSource;
    private Ps5ImageFormat _toolFormat;
    private bool _toolIsPackage;
    private SonyPkgKind? _toolPackageKind;
    private string _toolContentId = string.Empty;

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppState.LoadSettings();

        gridLibrary.ItemsSource = _visibleGames;
        gridCatalog.ItemsSource = _visibleCatalog;
        gridCompat.ItemsSource = _visibleCompat;
        gridTasks.ItemsSource = _taskRows;
        listLog.ItemsSource = _logLines;
        listFolders.ItemsSource = _folders;

        Log.LineAdded += line => Dispatcher.UIThread.Post(() =>
        {
            _logLines.Add(line);
            if (_logLines.Count > 3000) _logLines.RemoveAt(0);
        });

        WireCatalog();
        WireCompat();
        WireLibrary();
        WireTools();
        WireAnyPs5();
        WireTasks();
        WireSettings();

        foreach (Ps5GameInfo game in AppState.LoadManifest()) _allGames.Add(new GameRow(game));
        ApplyFilter();
        foreach (CatalogGame game in GameMetadata.All) _allCatalog.Add(new CatalogRow(game));
        ApplyCatalogFilter();
        SelectCatalogGame(null);
        InitializeQueue();
        LoadSettingsIntoUi();
        UpdateToolSource(null);
        UpdateAnyPs5Info();

        Compatibility.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            ApplyCompatFilter();
            ApplyCatalogFilter();
            ApplyFilter();
        });
        _ = LoadCompatibilityAsync();

        InitializeLocalFeatures();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) => RefreshTasks();
        timer.Start();

        Closing += (_, _) =>
        {
            _queue.Persist();
            _scanCts?.Cancel();
        };
        Log.Info("AnyPS5 Library started.");
        if (_allGames.Count == 0 && _settings.LibraryFolders.Count > 0) _ = ScanAsync();
    }

    // =================================================================== optional local features
    // Hooks for features compiled in only when their files exist under Local/ (git-ignored). Without an
    // implementation the compiler removes the calls.

    partial void InitializeLocalFeatures();
    partial void OnLibrarySelected(GameRow? row);
    partial void OnCatalogSelected(CatalogRow? row);
    partial void FilterCatalog(ref IEnumerable<CatalogRow> rows);
    partial void OnSaveSettings();

    // =================================================================== helpers

    private void SetStatus(string text) => txtHeaderStatus.Text = text;

    private async Task<string?> PickFolderAsync(string title)
    {
        IReadOnlyList<IStorageFolder> result = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickFileAsync(string title)
    {
        IReadOnlyList<IStorageFile> result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("PS5 packages and images") { Patterns = ["*.pkg", "*.exfat", "*.ffpkg", "*.ffpfsc", "*.img", "*.bin"] },
                FilePickerFileTypes.All
            ]
        });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickSaveAsync(string title, string suggested, string extension)
    {
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggested,
            DefaultExtension = extension.TrimStart('.'),
            ShowOverwritePrompt = true
        });
        return file?.TryGetLocalPath();
    }

    private static void Reveal(string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start("open", ["-R", path]);
            else if (Directory.Exists(path)) Process.Start("open", [path]);
            else if (Path.GetDirectoryName(path) is { } parent && Directory.Exists(parent)) Process.Start("open", [parent]);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("Could not open Finder: " + ex.Message);
        }
    }

    private Task<bool> ConfirmAsync(string title, string message) => Dialog.ShowAsync(this, title, message, true);
    private Task AlertAsync(string title, string message) => Dialog.ShowAsync(this, title, message, false);

    private string DefaultPasscode() => _settings.DebugPasscode.Length > 0
        ? _settings.DebugPasscode
        : SonyDebugPackageCredentials.DefaultPasscode;

    private string? Workspace() => _settings.WorkspaceFolder.Length > 0 && Directory.Exists(_settings.WorkspaceFolder)
        ? _settings.WorkspaceFolder
        : null;

    // =================================================================== catalog

    private void WireCatalog()
    {
        txtCatSearch.TextChanged += (_, _) => ApplyCatalogFilter();
        gridCatalog.SelectionChanged += (_, _) => SelectCatalogGame(gridCatalog.SelectedItem as CatalogRow);
        btnCatShowLibrary.Click += (_, _) =>
        {
            if (_catSelected is null || FindOwned(_catSelected.Id) is not { } owned) return;
            txtSearch.Text = string.Empty;
            gridLibrary.SelectedItem = owned;
            gridLibrary.ScrollIntoView(owned, null);
            tabsMain.SelectedItem = tabLibrary;
        };
    }

    private GameRow? FindOwned(string titleId) =>
        _allGames.FirstOrDefault(g => g.TitleId.Equals(titleId, StringComparison.OrdinalIgnoreCase) ||
                                      g.Info?.Id.Equals(titleId, StringComparison.OrdinalIgnoreCase) == true);

    private void ApplyCatalogFilter()
    {
        string query = txtCatSearch.Text?.Trim() ?? string.Empty;
        CatalogRow? keep = _catSelected;
        _visibleCatalog.Clear();
        IEnumerable<CatalogRow> rows = _allCatalog.Where(row => row.Matches(query));
        FilterCatalog(ref rows);
        foreach (CatalogRow row in rows.OrderBy(row => row.Title, StringComparer.OrdinalIgnoreCase))
            _visibleCatalog.Add(row);
        txtCatCount.Text = _visibleCatalog.Count == _allCatalog.Count
            ? $"{_allCatalog.Count:N0} game(s)"
            : $"{_visibleCatalog.Count:N0} of {_allCatalog.Count:N0}";
        if (keep is not null && _visibleCatalog.Contains(keep)) gridCatalog.SelectedItem = keep;
    }

    private void SelectCatalogGame(CatalogRow? row)
    {
        _catSelected = row;
        _catCts?.Cancel();
        imgCatIcon.Source = null;
        btnCatShowLibrary.IsVisible = row is not null && FindOwned(row.Id) is not null;
        if (row is null)
        {
            ClearCatalogInfo(CatalogInfo);
            txtCatTitle.Text = "No game selected";
            txtCatSub.Text = string.Empty;
            txtCatMissing.IsVisible = true;
            OnCatalogSelected(null);
            return;
        }
        txtCatTitle.Text = row.Title;
        txtCatSub.Text = string.Join("  ·  ", new[] { row.Id, row.Platform, row.Year }.Where(part => part.Length > 0));
        OnCatalogSelected(row);
        var cts = _catCts = new CancellationTokenSource();
        _ = ShowCatalogInfoAsync(row.Game, row.Title, CatalogInfo, cts.Token);
    }

    // =================================================================== compatibility

    private void WireCompat()
    {
        cboCompatStage.ItemsSource = new[] { "All stages" };
        cboCompatStage.SelectedIndex = 0;
        txtCompatSearch.TextChanged += (_, _) => ApplyCompatFilter();
        cboCompatStage.SelectionChanged += (_, _) => ApplyCompatFilter();
        chkCompatOwned.IsCheckedChanged += (_, _) => ApplyCompatFilter();
        gridCompat.SelectionChanged += (_, _) => SelectCompatGame(gridCompat.SelectedItem as CompatRow);
        btnCompatRefresh.Click += async (_, _) => await RefreshCompatibilityAsync();
        btnCompatOpenSite.Click += (_, _) =>
        {
            if (_compatSelected is { } row) OpenLink(Compatibility.GameUrl(row.Game));
        };
        btnCompatStore.Click += (_, _) => OpenLink(_compatSelected?.Game.StoreUrl);
        btnCompatShowLibrary.Click += (_, _) =>
        {
            if (_compatSelected?.Game.TitleIds.Select(FindOwned).FirstOrDefault(g => g is not null) is not { } owned) return;
            txtSearch.Text = string.Empty;
            gridLibrary.SelectedItem = owned;
            gridLibrary.ScrollIntoView(owned, null);
            tabsMain.SelectedItem = tabLibrary;
        };
    }

    private async Task LoadCompatibilityAsync()
    {
        await Compatibility.LoadCachedAsync();
        await RefreshCompatibilityAsync();
    }

    private async Task RefreshCompatibilityAsync()
    {
        btnCompatRefresh.IsEnabled = false;
        txtCompatCount.Text = "Updating…";
        bool ok = await Compatibility.RefreshAsync();
        btnCompatRefresh.IsEnabled = true;
        ApplyCompatFilter();
        if (!ok)
            txtCompatSource.Text = (Compatibility.Current is null ? "Could not download the compatibility data. "
                                       : "Could not update; showing the last downloaded copy. ") + txtCompatSource.Text;
    }

    private void ApplyCompatFilter()
    {
        Roadmap? roadmap = Compatibility.Current;
        string? keepId = _compatSelected?.Game.Id;
        string? stage = cboCompatStage.SelectedIndex > 0 ? cboCompatStage.SelectedItem as string : null;

        // Stage filter choices follow the data: All, each stage highest first, then Not reported.
        if (roadmap is not null && cboCompatStage.ItemCount != roadmap.Stages.Count + 2)
        {
            cboCompatStage.ItemsSource = new[] { "All stages" }
                .Concat(roadmap.Stages.OrderByDescending(s => s.Rank).Select(s => s.Name))
                .Append("Not reported").ToList();
            cboCompatStage.SelectedIndex = 0;
            return; // SelectionChanged runs this again
        }

        var owned = new HashSet<string>(_allGames.Select(g => g.TitleId).Where(id => id.Length > 0), StringComparer.OrdinalIgnoreCase);
        string query = txtCompatSearch.Text?.Trim() ?? string.Empty;
        bool ownedOnly = chkCompatOwned.IsChecked == true;
        _visibleCompat.Clear();
        IEnumerable<CompatRow> rows = (roadmap?.Games ?? [])
            .Select(game => new CompatRow(game, game.TitleIds.Any(owned.Contains)))
            .Where(row => (stage is null || row.Stage == stage) && (!ownedOnly || row.Owned) && row.Matches(query))
            .OrderByDescending(row => row.StageRank)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase);
        foreach (CompatRow row in rows) _visibleCompat.Add(row);

        int total = roadmap?.Games.Count ?? 0;
        txtCompatCount.Text = _visibleCompat.Count == total ? $"{total:N0} game(s)" : $"{_visibleCompat.Count:N0} of {total:N0}";
        txtCompatSource.Text = roadmap is null
            ? "Loading compatibility data…"
            : $"Source: AnyPS5 blocker map ({Compatibility.SiteUrl}), community-made, not official. Snapshot {roadmap.GeneratedAt}.";
        if (keepId is not null && _visibleCompat.FirstOrDefault(r => r.Game.Id == keepId) is { } keep)
            gridCompat.SelectedItem = keep;
    }

    private void SelectCompatGame(CompatRow? row)
    {
        _compatSelected = row;
        _compatCts?.Cancel();
        panelCompatDetail.Children.Clear();
        imgCompat.Source = null;
        borderCompatImage.IsVisible = false;
        btnCompatOpenSite.IsEnabled = row is not null;
        btnCompatStore.IsVisible = row?.Game.StoreUrl is { Length: > 0 };
        btnCompatShowLibrary.IsVisible = row?.Owned == true;
        if (row is null)
        {
            txtCompatTitle.Text = "No game selected";
            txtCompatSub.Text = string.Empty;
            return;
        }

        RoadmapGame game = row.Game;
        txtCompatTitle.Text = row.Name;
        txtCompatSub.Text = string.Join("  ·  ", new[] { row.TitleIds, game.Kind, game.Engine, row.Tags }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

        // Verified stage
        RoadmapStageInfo? info = Compatibility.StageInfo(game.Stage?.Stage);
        var chips = new WrapPanel();
        chips.Children.Add(Chip(row.Stage, StageColor(info?.Rank ?? -1)));
        if (row.Confidence.Length > 0) chips.Children.Add(Chip(row.Confidence, "#2A3550"));
        panelCompatDetail.Children.Add(chips);
        if (info is not null) panelCompatDetail.Children.Add(Muted(info.Meaning));
        if (game.Stage is { } stage) panelCompatDetail.Children.Add(StageReport(null, stage, null));
        else panelCompatDetail.Children.Add(Muted("No verified stage reported yet."));

        if (game.UnverifiedClaim is { Stage: not null } claim)
            panelCompatDetail.Children.Add(StageReport($"Unverified claim: {claim.Stage}", claim, "#E5A33B"));
        if (game.StageOnBranches is { Stage: not null } branches)
            panelCompatDetail.Children.Add(StageReport($"On PR branches: {branches.Stage}", branches, "#7FA7F0"));

        // Open blockers: the first one is where the game stops now.
        if (game.OpenBlockers.Count > 0)
        {
            panelCompatDetail.Children.Add(Heading($"Open blockers ({game.OpenBlockers.Count})"));
            foreach (RoadmapGameBlocker open in game.OpenBlockers)
            {
                RoadmapBlocker? blocker = Compatibility.Blocker(open.Id);
                var item = new StackPanel { Spacing = 2 };
                item.Children.Add(LinkButton($"#{open.Id}  {blocker?.Title ?? ""}".Trim(), blocker?.Url));
                string detail = string.Join("  ·  ", new[] { open.Title, blocker?.Area, open.Why }.Where(p => !string.IsNullOrWhiteSpace(p)));
                if (detail.Length > 0) item.Children.Add(Muted(detail));
                panelCompatDetail.Children.Add(item);
            }
        }

        // Regions / title IDs
        if (game.Titles.Count > 0)
        {
            panelCompatDetail.Children.Add(Heading("Title IDs"));
            foreach (RoadmapTitle title in game.Titles)
                panelCompatDetail.Children.Add(new SelectableTextBlock
                {
                    Text = string.Join("  ·  ", new[] { title.Id, title.Region, title.Platform, title.Name, title.Stage ?? "no stage" }
                        .Where(p => !string.IsNullOrWhiteSpace(p))),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    FontSize = 12
                });
        }
        if (game.LastEvidence?.Url is { } lastUrl)
            panelCompatDetail.Children.Add(LinkButton($"Last activity {row.LastActivity}", lastUrl));

        string? imageUrl = game.Image?.Wide ?? game.Image?.Square;
        var cts = _compatCts = new CancellationTokenSource();
        _ = LoadCompatImageAsync(imageUrl, cts.Token);
    }

    private async Task LoadCompatImageAsync(string? url, CancellationToken token)
    {
        if (await ArtCache.LoadAsync(url, token) is not { } bitmap || token.IsCancellationRequested) return;
        imgCompat.Source = bitmap;
        borderCompatImage.IsVisible = true;
    }

    private static Control StageReport(string? heading, RoadmapStage report, string? color)
    {
        var panel = new StackPanel { Spacing = 3 };
        if (heading is not null)
        {
            TextBlock title = Heading(heading);
            if (color is not null) title.Foreground = Avalonia.Media.Brush.Parse(color);
            panel.Children.Add(title);
        }
        string where = string.Join("  ·  ", new[] { report.Os, report.Gpu, report.Build, report.Date is { Length: >= 10 } d ? d[..10] : null }
            .Where(p => !string.IsNullOrWhiteSpace(p)));
        if (where.Length > 0) panel.Children.Add(Muted(where));
        if (!string.IsNullOrWhiteSpace(report.Quote))
            panel.Children.Add(new Border
            {
                BorderBrush = Avalonia.Media.Brush.Parse("#2F3A50"),
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(8, 2),
                Child = new SelectableTextBlock { Text = report.Quote, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12, Opacity = 0.85 }
            });
        if (report.Url is { Length: > 0 } url) panel.Children.Add(LinkButton("Source", url));
        return panel;
    }

    private static TextBlock Heading(string text) =>
        new() { Text = text, FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) };

    private static TextBlock Muted(string text) =>
        new() { Text = text, Opacity = 0.65, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

    private static Control LinkButton(string text, string? url)
    {
        if (url is not { Length: > 0 })
            return new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var button = new Button
        {
            Content = new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Foreground = Avalonia.Media.Brush.Parse("#7FA7F0") },
            Background = Avalonia.Media.Brushes.Transparent,
            Padding = new Thickness(0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left
        };
        ToolTip.SetTip(button, url);
        button.Click += (_, _) => OpenLink(url);
        return button;
    }

    private static string StageColor(int rank) => rank switch
    {
        >= 6 => "#1E6B3A",
        5 => "#3D6B1E",
        4 => "#6B5A1E",
        >= 2 => "#7A4A1E",
        >= 0 => "#7A2A2A",
        _ => "#3A3F4A"
    };

    private static void OpenLink(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            OpenUrl(uri);
    }

    // =================================================================== library

    private void WireLibrary()
    {
        btnAddFolder.Click += async (_, _) =>
        {
            if (await PickFolderAsync("Add a library folder") is not { } folder) return;
            if (!_settings.LibraryFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                _settings.LibraryFolders.Add(folder);
                AppState.SaveSettings(_settings);
                _folders.Add(folder);
            }
            await ScanAsync();
        };
        btnOpenFile.Click += async (_, _) =>
        {
            if (await PickFileAsync("Open a package or image") is not { } file) return;
            await ScanPathsAsync([file], recursive: false, merge: true);
        };
        btnScan.Click += async (_, _) => await ScanAsync();
        btnCancelScan.Click += (_, _) => _scanCts?.Cancel();
        txtSearch.TextChanged += (_, _) => ApplyFilter();
        gridLibrary.SelectionChanged += (_, _) => SelectGame(gridLibrary.SelectedItem as GameRow);

        btnDetailReveal.Click += (_, _) => { if (_selected is not null) Reveal(_selected.Path); };
        btnDetailTools.Click += (_, _) =>
        {
            if (_selected is null) return;
            UpdateToolSource(_selected.Path);
            tabsMain.SelectedItem = tabTools;
        };
        btnDetailAnyPs5.Click += (_, _) =>
        {
            if (_selected is null) return;
            SetAnySource(_selected.Path, _selected.Game);
            tabsMain.SelectedItem = tabAnyPs5;
        };
        txtFileFilter.TextChanged += (_, _) => ApplyFileFilter();
        btnExtractFile.Click += async (_, _) => await ExtractSelectedFilesAsync();
    }

    private async Task ScanAsync()
    {
        if (_settings.LibraryFolders.Count == 0)
        {
            await AlertAsync("No library folders", "Add a folder that contains dumps, .pkg files or images first.");
            return;
        }
        await ScanPathsAsync(_settings.LibraryFolders, _settings.RecursiveScan, merge: false);
    }

    private async Task ScanPathsAsync(IReadOnlyList<string> paths, bool recursive, bool merge)
    {
        _scanCts?.Cancel();
        var cts = _scanCts = new CancellationTokenSource();
        btnScan.IsEnabled = false;
        btnCancelScan.IsVisible = true;
        progressScan.IsVisible = true;
        progressScan.IsIndeterminate = true;
        SetStatus("Scanning…");
        var progress = new Progress<Ps5ScanProgress>(value =>
        {
            if (value.IsDiscovery || value.Total == 0)
            {
                progressScan.IsIndeterminate = true;
                SetStatus("Looking in " + value.CurrentPath);
            }
            else
            {
                progressScan.IsIndeterminate = false;
                progressScan.Maximum = value.Total;
                progressScan.Value = value.Processed;
                SetStatus($"Reading {value.Processed:N0}/{value.Total:N0}: {Path.GetFileName(value.CurrentPath)}");
            }
        });
        try
        {
            // A single file is scanned through its folder, then filtered back to that file.
            List<string> roots = paths.Select(p => File.Exists(p) ? Path.GetDirectoryName(p)! : p).Distinct().ToList();
            List<Ps5GameInfo> cached = _allGames.Select(row => row.Game).ToList();
            Ps5ScanResult result = await new Ps5LibraryScanner().ScanAsync(roots, recursive, cached, progress, cts.Token);
            IEnumerable<Ps5GameInfo> found = result.Games;
            if (paths.All(File.Exists))
                found = found.Where(g => paths.Contains(g.RootPath, StringComparer.OrdinalIgnoreCase));
            if (!merge) _allGames.Clear();
            foreach (Ps5GameInfo game in found)
            {
                _allGames.RemoveAll(row => string.Equals(row.Path, game.RootPath, StringComparison.OrdinalIgnoreCase) &&
                                           string.Equals(row.Game.VirtualRoot, game.VirtualRoot, StringComparison.Ordinal));
                _allGames.Add(new GameRow(game));
            }
            foreach (string error in result.Errors) Log.Error("Scan: " + error);
            AppState.SaveManifest(_allGames.Select(row => row.Game));
            SetStatus($"Scan finished: {result.Games.Count:N0} item(s)" +
                      (result.Errors.Count > 0 ? $", {result.Errors.Count} error(s) (see Log)" : ""));
            if (merge && found.FirstOrDefault() is null)
                await AlertAsync("Nothing found", "No PS5 game could be read from that file.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Scan cancelled.");
        }
        catch (Exception ex)
        {
            Log.Exception("Scan", ex);
            SetStatus("Scan failed: " + ex.Message);
        }
        finally
        {
            btnScan.IsEnabled = true;
            btnCancelScan.IsVisible = false;
            progressScan.IsVisible = false;
            ApplyFilter();
            ApplyCompatFilter();
        }
    }

    private void ApplyFilter()
    {
        string query = txtSearch.Text?.Trim() ?? string.Empty;
        GameRow? keep = _selected;
        _visibleGames.Clear();
        foreach (GameRow row in _allGames.Where(row => row.Matches(query))
                     .OrderBy(row => row.Title, StringComparer.OrdinalIgnoreCase))
            _visibleGames.Add(row);
        txtLibraryCount.Text = _visibleGames.Count == _allGames.Count
            ? $"{_allGames.Count:N0} item(s)"
            : $"{_visibleGames.Count:N0} of {_allGames.Count:N0}";
        if (keep is not null && _visibleGames.Contains(keep)) gridLibrary.SelectedItem = keep;
    }

    private void SelectGame(GameRow? row)
    {
        _selected = row;
        _detailsCts?.Cancel();
        btnDetailAnyPs5.IsEnabled = btnDetailTools.IsEnabled = btnDetailReveal.IsEnabled = row is not null;
        OnLibrarySelected(row);
        imgIcon.Source = null;
        panelArtwork.Children.Clear();
        gridTrophies.ItemsSource = null;
        gridExecutable.ItemsSource = null;
        _allFiles = [];
        ApplyFileFilter();
        ClearCatalogInfo(LibraryInfo);
        if (row is null)
        {
            txtDetailTitle.Text = "No game selected";
            txtDetailSub.Text = string.Empty;
            gridOverview.ItemsSource = null;
            txtRaw.Text = string.Empty;
            return;
        }

        Ps5GameInfo game = row.Game;
        txtDetailTitle.Text = row.Title;
        txtDetailSub.Text = $"{game.TitleId}  ·  v{game.DisplayVersion}  ·  {game.SourceDescription}  ·  {game.Platform}";
        gridOverview.ItemsSource = OverviewRows(game);
        txtRaw.Text = game.RawParamJson;
        var cts = _detailsCts = new CancellationTokenSource();
        _ = LoadDetailsAsync(game, cts);
        _ = ShowCatalogInfoAsync(row.Info, row.Title, LibraryInfo, cts.Token);
    }

    internal static void OpenUrl(Uri uri)
    {
        try
        {
            Process.Start("open", [uri.AbsoluteUri]);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error("Could not open URL: " + ex.Message);
        }
    }

    /// <summary>The controls one catalog info panel is drawn into (My Library details and the Catalog tab each have one).</summary>
    private sealed record InfoView(Image Icon, Border Hero, Image HeroImage, Image Logo, WrapPanel Chips, Grid Facts,
                                   TextBlock About, WrapPanel Shots, TextBlock Missing);

    private InfoView LibraryInfo => new(imgIcon, borderHero, imgHero, imgLogo, panelInfoChips, gridInfoFacts, txtInfoAbout,
                                        panelShots, txtInfoMissing);

    private InfoView CatalogInfo => new(imgCatIcon, borderCatHero, imgCatHero, imgCatLogo, panelCatChips, gridCatFacts,
                                        txtCatAbout, panelCatShots, txtCatMissing);

    private static void ClearCatalogInfo(InfoView v)
    {
        v.Hero.IsVisible = false;
        v.HeroImage.Source = v.Logo.Source = null;
        v.Chips.Children.Clear();
        v.Facts.Children.Clear();
        v.Facts.RowDefinitions.Clear();
        v.Shots.Children.Clear();
        v.About.Text = string.Empty;
        v.Missing.IsVisible = false;
    }

    private async Task ShowCatalogInfoAsync(CatalogGame? info, string title, InfoView v, CancellationToken token)
    {
        ClearCatalogInfo(v);
        if (info is null)
        {
            v.Missing.IsVisible = true;
            return;
        }

        foreach (string genre in info.Genres)
            v.Chips.Children.Add(Chip(genre, "#2A3550"));
        if (info.Metacritic is { } score)
            v.Chips.Children.Add(Chip($"Metacritic {score}", score >= 75 ? "#1E6B3A" : score >= 50 ? "#7A6A1E" : "#7A2A2A"));

        void Fact(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            int index = v.Facts.RowDefinitions.Count;
            v.Facts.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = key, Opacity = 0.65, Margin = new Thickness(0, 2) };
            var text = new SelectableTextBlock { Text = value, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 2) };
            Grid.SetRow(label, index);
            Grid.SetRow(text, index);
            Grid.SetColumn(text, 1);
            v.Facts.Children.Add(label);
            v.Facts.Children.Add(text);
        }
        Fact("Developer", info.Developer);
        Fact("Publisher", info.Publisher);
        Fact("Released", info.ReleaseDate.Length > 0 ? info.ReleaseDate : info.Year?.ToString());
        Fact("Features", string.Join(", ", info.Features));
        Fact("Platform", info.Platform.ToUpperInvariant());

        CatalogPage? page = GameMetadata.Page(info.Id);
        if (page is not null)
        {
            List<string> paragraphs = page.About.Count > 0 ? page.About : [page.Summary];
            v.About.Text = string.Join("\n\n", paragraphs.Where(p => p.Length > 0));
        }

        if (info.Art?.Tint is { } tint && Avalonia.Media.Color.TryParse(tint, out var color))
            v.Hero.Background = new Avalonia.Media.SolidColorBrush(color);
        if (info.Art?.Hero is { } heroUrl && await ArtCache.LoadAsync(heroUrl, token) is { } hero && !token.IsCancellationRequested)
        {
            v.HeroImage.Source = hero;
            v.Hero.IsVisible = true;
            if (info.Art.Logo is { } logoUrl && await ArtCache.LoadAsync(logoUrl, token) is { } logo && !token.IsCancellationRequested)
                v.Logo.Source = logo;
        }
        // The catalog cover stands in when the game itself has no icon0.png.
        if (v.Icon.Source is null && info.Art?.Cover is { } coverUrl &&
            await ArtCache.LoadAsync(coverUrl, token) is { } cover && !token.IsCancellationRequested && v.Icon.Source is null)
            v.Icon.Source = cover;

        if (page is null) return;
        foreach (CatalogShot shot in page.Shots.Take(8))
        {
            if (token.IsCancellationRequested) return;
            if (await ArtCache.LoadAsync(shot.Thumb, token) is not { } thumb || token.IsCancellationRequested) continue;
            var button = new Button
            {
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 6, 6),
                Background = Avalonia.Media.Brushes.Transparent,
                Content = new Image { Source = thumb, Width = 200, Stretch = Avalonia.Media.Stretch.Uniform },
            };
            string full = shot.Url;
            button.Click += async (_, _) => await ShowImageAsync(full, title);
            v.Shots.Children.Add(button);
        }
    }

    private static Border Chip(string text, string background) => new()
    {
        Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(background)),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(9, 3),
        Margin = new Thickness(0, 0, 6, 6),
        Child = new TextBlock { Text = text, FontSize = 12 }
    };

    private async Task ShowImageAsync(string url, string title)
    {
        if (await ArtCache.LoadAsync(url) is not { } bitmap) return;
        var window = new Window
        {
            Title = title,
            Width = Math.Min(1280, bitmap.PixelSize.Width),
            Height = Math.Min(720, bitmap.PixelSize.Height),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Avalonia.Media.Brushes.Black,
            Content = new Image { Source = bitmap, Stretch = Avalonia.Media.Stretch.Uniform }
        };
        window.Show(this);
    }

    private static List<KeyValueRow> OverviewRows(Ps5GameInfo g)
    {
        var rows = new List<KeyValueRow>();
        void Add(string key, string? value) { if (!string.IsNullOrWhiteSpace(value)) rows.Add(new KeyValueRow(key, value)); }
        Add("Title", g.Title);
        Add("Title ID", g.TitleId);
        Add("Content ID", g.ContentId);
        Add("Concept ID", g.ConceptId);
        Add("Content version", g.ContentVersion);
        Add("Master version", g.MasterVersion);
        Add("Target version", g.TargetContentVersion);
        Add("Category", g.ApplicationCategory);
        Add("Required firmware", g.RequiredSystemSoftware);
        Add("SDK version", g.SdkVersion);
        Add("DRM type", g.DrmType);
        Add("Default language", g.DefaultLanguage);
        Add("Format", g.SourceDescription);
        Add("Size", (g.SourceSize > 0 ? g.SourceSize : g.GameRootBytes) is > 0 and var size ? Operations.FormatBytes(size) : null);
        Add("Download size", g.DownloadDataSize > 0 ? Operations.FormatBytes(g.DownloadDataSize) : null);
        Add("Features", string.Join(", ", g.DeclaredFeatures));
        Add("Created", g.CreationDate);
        Add("Package", g.Package?.KindDisplayName);
        Add("Path", g.RootPath);
        if (g.VirtualRoot.Length > 0) Add("Inner root", g.VirtualRoot);
        foreach (string warning in g.DataWarnings) Add("Warning", warning);
        return rows;
    }

    private async Task LoadDetailsAsync(Ps5GameInfo game, CancellationTokenSource cts)
    {
        var artwork = new Progress<Ps5Artwork>(art =>
        {
            if (cts.IsCancellationRequested) return;
            if (ToBitmap(art.Icon) is { } icon) imgIcon.Source = icon;
            ShowArtwork(art);
        });
        try
        {
            txtFilesInfo.Text = "Loading…";
            Ps5GameDetails details = await new Ps5DetailsLoader().LoadAsync(game, cts.Token, artwork);
            if (cts.IsCancellationRequested) return;
            _allFiles = details.Files.Files.Select(f => new FileRow(f.RelativePath, f.Size, f.Origin)).ToList();
            ApplyFileFilter();
            txtFilesInfo.Text = $"{details.Files.FileCount:N0} file(s), {Operations.FormatBytes(details.Files.TotalSize)}";
            gridTrophies.ItemsSource = details.TrophySet?.Trophies.Select(t => new TrophyRow(t)).ToList();
            gridExecutable.ItemsSource = ExecutableRows(details.Executable);
            foreach (string error in details.Errors) Log.Info($"{game.TitleId}: {error}");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
            {
                txtFilesInfo.Text = "Could not load details: " + ex.Message;
                Log.Exception("Details", ex);
            }
        }
    }

    private static List<KeyValueRow>? ExecutableRows(Ps5SelfInfo? self)
    {
        if (self is null) return null;
        var rows = new List<KeyValueRow>
        {
            new("Format", self.IsSelf ? $"SELF ({self.SelfMagic})" : "ELF"),
            new("File size", Operations.FormatBytes(self.FileSize)),
            new("ELF type", $"0x{self.ElfType:X4}"),
            new("Machine", $"0x{self.Machine:X4}"),
            new("Entry point", $"0x{self.EntryPoint:X}"),
            new("Program headers", self.ProgramHeaderCount.ToString()),
            new("Section headers", self.SectionHeaderCount.ToString())
        };
        if (self.IsSelf) rows.Add(new("SELF segments", self.SelfSegmentCount.ToString()));
        foreach (Ps5ModuleInfo module in self.Modules)
            rows.Add(new($"Module ({module.Kind})", $"{module.RelativePath}  ·  {Operations.FormatBytes(module.Size)}"));
        return rows;
    }

    private void ShowArtwork(Ps5Artwork art)
    {
        panelArtwork.Children.Clear();
        foreach ((string name, Ps5ImageData? data) in new[] { ("icon0", art.Icon), ("pic0", art.Background),
                     ("pic1", art.Background1), ("pic2", art.Background2) })
        {
            if (ToBitmap(data) is not { } bitmap) continue;
            panelArtwork.Children.Add(new TextBlock { Text = $"{name}  ({bitmap.PixelSize.Width}×{bitmap.PixelSize.Height})", Opacity = 0.7 });
            panelArtwork.Children.Add(new Image { Source = bitmap, MaxHeight = 260, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left });
        }
    }

    private static Bitmap? ToBitmap(Ps5ImageData? data)
    {
        if (data is null || data.IsEmpty) return null;
        try
        {
            if (!data.IsRgba) return new Bitmap(new MemoryStream(data.Bytes));
            var bitmap = new WriteableBitmap(new PixelSize(data.Width, data.Height), new Vector(96, 96),
                PixelFormat.Rgba8888, AlphaFormat.Unpremul);
            using ILockedFramebuffer buffer = bitmap.Lock();
            int rowBytes = data.Width * 4;
            for (int y = 0; y < data.Height; y++)
                Marshal.Copy(data.Bytes, y * rowBytes, buffer.Address + y * buffer.RowBytes, rowBytes);
            return bitmap;
        }
        catch (Exception ex)
        {
            Log.Info("Artwork decode failed: " + ex.Message);
            return null;
        }
    }

    private void ApplyFileFilter()
    {
        string filter = txtFileFilter.Text?.Trim() ?? string.Empty;
        gridFiles.ItemsSource = filter.Length == 0
            ? _allFiles
            : _allFiles.Where(f => f.RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private async Task ExtractSelectedFilesAsync()
    {
        if (_selected is null) return;
        List<FileRow> files = gridFiles.SelectedItems.OfType<FileRow>().ToList();
        if (files.Count == 0)
        {
            await AlertAsync("Extract", "Select one or more files first.");
            return;
        }
        if (await PickFolderAsync("Extract to folder") is not { } folder) return;
        Ps5GameInfo game = _selected.Game;
        long total = files.Sum(f => f.SizeBytes);
        Enqueue(_queue.Enqueue(PackageTaskTypes.PackageExtract, $"Extract {files.Count} file(s) from {_selected.Title}",
            async (progress, token) =>
            {
                long before = 0;
                foreach (FileRow file in files)
                {
                    string output = Path.Combine(folder, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    long offset = before;
                    await GameFileSystem.ExtractFileAsync(game, file.RelativePath, output,
                        new Progress<long>(done => progress.Report(new PackageTaskProgress("Extracting",
                            CurrentBytes: offset + done, TotalBytes: total, CurrentFile: file.RelativePath))), token);
                    before += file.SizeBytes;
                }
            }, game.RootPath, folder, operation: "Extract files", sourceFormat: game.SourceDescription,
            targetFormat: "folder", stagePlan: PackageTaskPlans.Extract));
    }

    // =================================================================== tools

    private void WireTools()
    {
        btnToolFromLibrary.Click += (_, _) => { if (_selected is not null) UpdateToolSource(_selected.Path); };
        btnToolPickFile.Click += async (_, _) => { if (await PickFileAsync("Choose an image or package") is { } p) UpdateToolSource(p); };
        btnToolPickFolder.Click += async (_, _) => { if (await PickFolderAsync("Choose a dump folder") is { } p) UpdateToolSource(p); };
        txtToolSource.LostFocus += (_, _) =>
        {
            string text = txtToolSource.Text?.Trim() ?? string.Empty;
            if (text != (_toolSource ?? string.Empty)) UpdateToolSource(text.Length > 0 ? text : null);
        };
        cboToolAction.SelectionChanged += (_, _) => UpdateToolTargets();
        cboToolTarget.SelectionChanged += (_, _) => UpdateToolOptions(suggest: true);
        btnToolPickOutput.Click += async (_, _) => await PickToolOutputAsync();
        btnToolRun.Click += async (_, _) => await RunToolAsync();
        txtToolPasscode.Text = DefaultPasscode();
    }

    private void UpdateToolSource(string? path)
    {
        if (path is not null && !File.Exists(path) && !Directory.Exists(path)) path = null;
        _toolSource = path;
        txtToolSource.Text = path ?? string.Empty;
        _toolContentId = string.Empty;
        _toolIsPackage = path is not null && File.Exists(path) &&
                         Path.GetExtension(path).Equals(".pkg", StringComparison.OrdinalIgnoreCase);
        _toolFormat = path is null || Directory.Exists(path) || _toolIsPackage
            ? Ps5ImageFormat.Unknown
            : Ps5ImageFormatProbe.Detect(path);
        _toolPackageKind = _toolIsPackage ? Operations.TryReadPackageKind(path!) : null;
        txtToolDetected.Text = "Detected: " + SourceLabel();

        var actions = new List<string>();
        if (path is not null)
        {
            if (Directory.Exists(path)) actions.Add(ActionConvert);
            else if (_toolIsPackage)
            {
                if (_toolPackageKind == SonyPkgKind.FinalizedDebug) actions.AddRange([ActionConvert, ActionExtract, ActionVerify]);
            }
            else if (_toolFormat is Ps5ImageFormat.Exfat or Ps5ImageFormat.Ufs2 or Ps5ImageFormat.Pfs)
            {
                actions.AddRange([ActionConvert, ActionExtract, ActionVerify]);
                if (_toolFormat == Ps5ImageFormat.Exfat) actions.AddRange([ActionRepair, ActionAmpr]);
                if (_toolFormat == Ps5ImageFormat.Ufs2) actions.Add(ActionRebuild);
            }
        }
        cboToolAction.ItemsSource = actions;
        cboToolAction.SelectedIndex = actions.Count > 0 ? 0 : -1;
        txtToolStatus.Text = path is null
            ? "Choose a source."
            : actions.Count == 0
                ? _toolIsPackage
                    ? "Only finalized debug packages (FPKG) can be converted, extracted or verified."
                    : "This file is not a recognized PS5 image."
                : string.Empty;
        UpdateToolTargets();
    }

    private string SourceLabel()
    {
        if (_toolSource is null) return "-";
        if (Directory.Exists(_toolSource)) return "dump folder";
        if (_toolIsPackage)
            return _toolPackageKind switch
            {
                SonyPkgKind.FinalizedDebug => "debug package (FPKG)",
                SonyPkgKind.FinalizedPatch => "patch package",
                SonyPkgKind.FinalizedRetail => "retail package",
                SonyPkgKind.MetadataContainer => "CNT metadata",
                _ => "unreadable package"
            };
        return FormatLabel(_toolFormat);
    }

    private static string FormatLabel(Ps5ImageFormat format) => format switch
    {
        Ps5ImageFormat.Exfat => "exFAT",
        Ps5ImageFormat.Ufs2 => "FFPKG",
        Ps5ImageFormat.Pfs => "FFPFSC",
        _ => "unknown"
    };

    private string? ToolAction => cboToolAction.SelectedItem as string;
    private string? ToolTarget => cboToolTarget.SelectedItem as string;

    private void UpdateToolTargets()
    {
        var targets = new List<string>();
        if (ToolAction == ActionConvert && _toolSource is not null)
        {
            if (_toolIsPackage) targets.AddRange([TargetExfat, TargetFfpkg, TargetFfpfsc]);
            else if (Directory.Exists(_toolSource)) targets.AddRange([TargetPkg, TargetExfat, TargetFfpkg, TargetFfpfsc]);
            else
            {
                if (Ps5ImageConversionService.IsSupported(_toolFormat, Ps5ImageConversionTarget.Exfat)) targets.Add(TargetExfat);
                if (Ps5ImageConversionService.IsSupported(_toolFormat, Ps5ImageConversionTarget.Ffpkg)) targets.Add(TargetFfpkg);
                if (Ps5ImageConversionService.IsSupported(_toolFormat, Ps5ImageConversionTarget.Ffpfsc)) targets.Add(TargetFfpfsc);
                targets.Add(TargetPkg);
            }
        }
        cboToolTarget.ItemsSource = targets;
        cboToolTarget.SelectedIndex = targets.Count > 0 ? 0 : -1;
        UpdateToolOptions(suggest: true);
    }

    private void UpdateToolOptions(bool suggest)
    {
        string? action = ToolAction;
        bool convert = action == ActionConvert;
        bool needsOutput = convert || action == ActionExtract;
        lblToolTarget.IsVisible = cboToolTarget.IsVisible = convert;
        lblToolOutput.IsVisible = txtToolOutput.IsVisible = btnToolPickOutput.IsVisible = needsOutput;
        bool pkgTarget = convert && ToolTarget == TargetPkg;
        bool passcode = pkgTarget || (_toolIsPackage && action is ActionConvert or ActionExtract or ActionVerify);
        lblToolPasscode.IsVisible = txtToolPasscode.IsVisible = passcode;
        panelToolOptions.IsVisible = convert;
        panelExfatOpts.IsVisible = convert && ToolTarget is TargetExfat or TargetFfpfsc;
        panelPfscOpts.IsVisible = convert && ToolTarget == TargetFfpfsc;
        panelPkgOpts.IsVisible = pkgTarget;
        if (pkgTarget && _toolSource is not null && _toolContentId.Length == 0)
            _toolContentId = Operations.TryReadParamFields(_toolSource).ContentId;
        txtToolContentId.Text = pkgTarget
            ? _toolContentId.Length > 0 ? "Content ID: " + _toolContentId : "No content ID found in sce_sys/param.json — cannot build."
            : string.Empty;
        btnToolRun.Content = action switch
        {
            ActionConvert => pkgTarget ? "Queue Package Build" : "Queue Conversion",
            ActionExtract => "Queue Extraction",
            ActionVerify => "Queue Verification",
            null => "Queue",
            _ => "Queue " + action
        };
        btnToolRun.IsEnabled = action is not null;

        if (!suggest || _toolSource is null || !needsOutput) return;
        string baseName = Directory.Exists(_toolSource)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(_toolSource))
            : Path.GetFileNameWithoutExtension(_toolSource);
        string parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(_toolSource)) ?? "/";
        txtToolOutput.Text = action == ActionExtract
            ? Path.Combine(parent, baseName + "-extracted")
            : Path.Combine(parent, baseName + TargetExtension(ToolTarget));
    }

    private static string TargetExtension(string? target) => target switch
    {
        TargetExfat => ".exfat",
        TargetFfpkg => ".ffpkg",
        TargetFfpfsc => ".ffpfsc",
        _ => ".pkg"
    };

    private async Task PickToolOutputAsync()
    {
        if (_toolSource is null) return;
        string? path = ToolAction == ActionExtract
            ? await PickFolderAsync("Extract into folder (a new sub-folder is created)") is { } folder
                ? Path.Combine(folder, Path.GetFileNameWithoutExtension(_toolSource) + "-extracted")
                : null
            : await PickSaveAsync("Output file", Path.GetFileName(txtToolOutput.Text ?? "output"), TargetExtension(ToolTarget));
        if (path is not null) txtToolOutput.Text = path;
    }

    private async Task RunToolAsync()
    {
        if (_toolSource is null || ToolAction is not { } action) return;
        string source = _toolSource;
        string output = txtToolOutput.Text?.Trim() ?? string.Empty;
        string passcode = (txtToolPasscode.Text ?? string.Empty).Trim();
        if (passcode.Length == 0) passcode = DefaultPasscode();
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        string sourceFormat = Directory.Exists(source) ? "dump" : _toolIsPackage ? "FPKG" : FormatLabel(_toolFormat);
        var fields = new Dictionary<string, string> { ["source"] = source, ["passcode"] = passcode };
        TaskSpec spec;

        switch (action)
        {
            case ActionConvert when ToolTarget == TargetPkg:
            {
                if (output.Length == 0) { await AlertAsync("Build package", "Choose an output .pkg file."); return; }
                if (_toolContentId.Length == 0)
                {
                    await AlertAsync("Build package", "The source has no content ID in sce_sys/param.json, so a package cannot be built from it.");
                    return;
                }
                fields["output"] = output;
                fields["contentId"] = _toolContentId;
                fields["overwrite"] = (chkToolOverwrite.IsChecked == true).ToString();
                fields["compression"] = (cboToolCompression.SelectedIndex switch
                {
                    1 => Ps5InnerCompression.Kraken,
                    2 => Ps5InnerCompression.Stored,
                    _ => Ps5InnerCompression.Auto
                }).ToString();
                fields["playgo"] = ((int)(nudToolPlayGo.Value ?? 1)).ToString();
                fields["fakeSign"] = (chkToolFakeSign.IsChecked == true).ToString();
                fields["rightSprx"] = (chkToolRightSprx.IsChecked == true).ToString();
                fields["deterministic"] = (chkToolDeterministic.IsChecked == true).ToString();
                if (Workspace() is { } ws) fields["temp"] = ws;
                spec = new TaskSpec(PackageTaskTypes.ImageBuildPackage, $"Build package from {name}", "Build package",
                    sourceFormat, "FPKG", source, output, fields);
                break;
            }
            case ActionConvert:
            {
                if (output.Length == 0) { await AlertAsync("Convert", "Choose an output file."); return; }
                Ps5ImageConversionTarget target = ToolTarget switch
                {
                    TargetExfat => Ps5ImageConversionTarget.Exfat,
                    TargetFfpkg => Ps5ImageConversionTarget.Ffpkg,
                    _ => Ps5ImageConversionTarget.Ffpfsc
                };
                fields["output"] = output;
                fields["target"] = target.ToString();
                fields["overwrite"] = (chkToolOverwrite.IsChecked == true).ToString();
                fields["package"] = _toolIsPackage.ToString();
                fields["ampr"] = (chkToolAmpr.IsChecked == true).ToString();
                fields["cluster"] = (cboToolCluster.SelectedIndex switch { 1 => 32768, 2 => 65536, _ => 0 }).ToString();
                fields["level"] = ((int)(nudToolLevel.Value ?? 6)).ToString();
                fields["gain"] = ((int)(nudToolGain.Value ?? 2)).ToString();
                if (Workspace() is { } ws) fields["workspace"] = ws;
                spec = new TaskSpec(PackageTaskTypes.ImageConvert, $"Convert {name}", "Convert", sourceFormat,
                    TargetExtension(ToolTarget).TrimStart('.').ToUpperInvariant(), source, output, fields);
                break;
            }
            case ActionExtract:
            {
                if (output.Length == 0) { await AlertAsync("Extract", "Choose an output folder."); return; }
                output = Operations.AvailableDirectory(output);
                fields["output"] = output;
                fields["kind"] = _toolIsPackage ? "sony" : _toolFormat switch
                {
                    Ps5ImageFormat.Exfat => "exfat",
                    Ps5ImageFormat.Ufs2 => "ffpkg",
                    _ => "ffpfsc"
                };
                spec = new TaskSpec(PackageTaskTypes.PackageExtract, $"Extract {name}", "Extract", sourceFormat,
                    "folder", source, output, fields);
                break;
            }
            case ActionVerify when _toolIsPackage:
                spec = new TaskSpec(PackageTaskTypes.PackageVerify, $"Verify {name}", "Verify package", "FPKG", "",
                    source, source, fields);
                break;
            case ActionVerify:
                fields["format"] = _toolFormat.ToString();
                spec = new TaskSpec(PackageTaskTypes.ImageVerify, $"Verify {name}", "Verify", sourceFormat, "",
                    source, source, fields);
                break;
            case ActionRepair:
                if (!await ConfirmAsync("Repair exFAT image?",
                        "Repair restores a damaged boot region from the valid copy, extracts every readable file, rebuilds " +
                        "the filesystem beside the original, verifies it, and only then replaces the original.\n\n" + source))
                    return;
                spec = new TaskSpec(PackageTaskTypes.ExfatRepair, $"Repair {name}", "Repair", "exFAT", "exFAT", source, source, fields);
                break;
            case ActionAmpr:
                if (!await ConfirmAsync("Refresh AMPR index?",
                        "This creates or refreshes the root ampr_emu.index. Changes are rolled back if verification fails.\n\n" + source))
                    return;
                spec = new TaskSpec(PackageTaskTypes.ExfatAmpr, $"AMPR index for {name}", "Refresh AMPR", "exFAT", "exFAT", source, source, fields);
                break;
            case ActionRebuild:
                if (!await ConfirmAsync("Rebuild FFPKG image?",
                        "This extracts every readable file, rebuilds all FFPKG metadata beside the original, verifies it, " +
                        "then swaps it into place. It needs a lot of free disk space.\n\n" + source))
                    return;
                spec = new TaskSpec(PackageTaskTypes.FfpkgRebuild, $"Rebuild {name}", "Rebuild", "FFPKG", "FFPKG", source, source, fields);
                break;
            default:
                return;
        }
        QueueSpec(spec);
        txtToolStatus.Text = "Queued. See the Tasks tab.";
    }

    // =================================================================== AnyPS5

    private void WireAnyPs5()
    {
        btnAnyFromLibrary.Click += (_, _) => { if (_selected is not null) SetAnySource(_selected.Path, _selected.Game); };
        btnAnyPickFile.Click += async (_, _) => { if (await PickFileAsync("Choose a debug package or image") is { } p) SetAnySource(p, null); };
        btnAnyPickFolder.Click += async (_, _) => { if (await PickFolderAsync("Choose a dump folder") is { } p) SetAnySource(p, null); };
        btnAnyPickOutput.Click += async (_, _) =>
        {
            if (await PickFolderAsync("Output parent folder") is not { } folder) return;
            string name = Path.GetFileName(txtAnyOutput.Text ?? string.Empty);
            txtAnyOutput.Text = Path.Combine(folder, name.Length > 0 ? name : "anyps5");
        };
        btnAnyPickRelinker.Click += async (_, _) =>
        {
            if (await PickFileAsync("Choose the AnyPS5 relinker (build/core/relinker/relinker)") is not { } file) return;
            txtAnyRelinker.Text = file;
            // The system libraries sit next to the relinker's build tree: build/core/libs/libs.
            string? core = Path.GetDirectoryName(Path.GetDirectoryName(file));
            string libs = core is null ? string.Empty : Path.Combine(core, "libs", "libs");
            if (string.IsNullOrWhiteSpace(txtAnyLibs.Text) && Directory.Exists(libs)) txtAnyLibs.Text = libs;
            chkAnyRelink.IsChecked = true;
            SaveAnyPs5Settings();
        };
        btnAnyPickLibs.Click += async (_, _) =>
        {
            if (await PickFolderAsync("AnyPS5 system libraries folder (*.prx)") is { } folder) txtAnyLibs.Text = folder;
            SaveAnyPs5Settings();
        };
        chkAnyRelink.IsCheckedChanged += (_, _) => UpdateAnyPs5Info();
        txtAnyRelinker.TextChanged += (_, _) => UpdateAnyPs5Info();
        btnAnyRun.Click += async (_, _) => await RunAnyPs5Async();
    }

    private void SetAnySource(string path, Ps5GameInfo? game)
    {
        txtAnySource.Text = path;
        string parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) ?? "/";
        string id = game?.TitleId is { Length: > 0 } titleId
            ? titleId
            : Operations.TryReadParamFields(path).TitleId is { Length: > 0 } fromParam
                ? fromParam
                : Directory.Exists(path) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(path)) : Path.GetFileNameWithoutExtension(path);
        txtAnyOutput.Text = Path.Combine(parent, id + "-anyps5");
        txtAnyDetected.Text = Directory.Exists(path)
            ? "Dump folder"
            : Path.GetExtension(path).Equals(".pkg", StringComparison.OrdinalIgnoreCase)
                ? Operations.TryReadPackageKind(path) == SonyPkgKind.FinalizedDebug
                    ? "Debug package (FPKG)"
                    : "Not a debug package — pkg-to-anyps5 only reads unencrypted debug packages"
                : FormatLabel(Ps5ImageFormatProbe.Detect(path)) + " image (it is extracted to a staging folder first)";
    }

    private void UpdateAnyPs5Info()
    {
        string? tool = AnyPs5Exporter.FindTool();
        txtAnyTool.Text = tool is null
            ? "⚠ pkg-to-anyps5 not found. It should be inside the app bundle (Contents/MacOS)."
            : $"Using pkg-to-anyps5 at {tool}";
        string relinker = txtAnyRelinker.Text?.Trim() ?? string.Empty;
        txtAnyRelinkHint.Text = chkAnyRelink.IsChecked != true
            ? "Off: only source/ and app0/ are written. Run the relinker yourself on <output>/source/eboot.bin."
            : relinker.Length == 0
                ? "Choose the relinker from your AnyPS5 build (build/core/relinker/relinker)."
                : File.Exists(relinker) ? "Relinker found. app.elf will be built automatically." : "⚠ Relinker file not found.";
    }

    private void SaveAnyPs5Settings()
    {
        _settings.AnyPs5Relinker = txtAnyRelinker.Text?.Trim() ?? string.Empty;
        _settings.AnyPs5Libs = txtAnyLibs.Text?.Trim() ?? string.Empty;
        _settings.AnyPs5AutoRelink = chkAnyRelink.IsChecked == true;
        _settings.AnyPs5Jobs = (int)(nudAnyJobs.Value ?? 4);
        _settings.AnyPs5ExecutablesOnly = chkAnyExecOnly.IsChecked == true;
        AppState.SaveSettings(_settings);
        UpdateAnyPs5Info();
    }

    private async Task RunAnyPs5Async()
    {
        string source = txtAnySource.Text?.Trim() ?? string.Empty;
        string output = txtAnyOutput.Text?.Trim() ?? string.Empty;
        if (source.Length == 0 || output.Length == 0)
        {
            await AlertAsync("AnyPS5 export", "Choose a source and an output folder.");
            return;
        }
        if (AnyPs5Exporter.FindTool() is null)
        {
            await AlertAsync("AnyPS5 export", "pkg-to-anyps5 was not found inside the app bundle.");
            return;
        }
        if (!AnyPs5Exporter.CanExport(source))
        {
            await AlertAsync("AnyPS5 export", "The source must be a debug (FPKG) package, a dump folder, or an exFAT / FFPKG / FFPFSC image.");
            return;
        }
        bool relink = chkAnyRelink.IsChecked == true;
        string relinker = txtAnyRelinker.Text?.Trim() ?? string.Empty;
        if (relink && !File.Exists(relinker))
        {
            await AlertAsync("AnyPS5 export", "The AnyPS5 relinker was not found. Choose it, or turn off the relink step.");
            return;
        }
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any() &&
            !await ConfirmAsync("Output folder is not empty",
                $"{output}\n\nalready has files. pkg-to-anyps5 writes source/ and app0/ into it and replaces files with the same name. Continue?"))
            return;
        SaveAnyPs5Settings();

        var fields = new Dictionary<string, string>
        {
            ["source"] = source,
            ["output"] = output,
            ["passcode"] = DefaultPasscode(),
            ["jobs"] = ((int)(nudAnyJobs.Value ?? 4)).ToString(),
            ["executablesOnly"] = (chkAnyExecOnly.IsChecked == true).ToString()
        };
        if (relink)
        {
            fields["relinker"] = relinker;
            if (txtAnyLibs.Text?.Trim() is { Length: > 0 } libs) fields["libs"] = libs;
        }
        if (Workspace() is { } ws) fields["workspace"] = ws;
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        string sourceFormat = Directory.Exists(source) ? "dump" : Path.GetExtension(source).TrimStart('.').ToUpperInvariant();
        QueueSpec(new TaskSpec(AnyPs5TaskTypes.Export, $"AnyPS5: {name}", "AnyPS5 export", sourceFormat,
            relink ? "app.elf" : "AnyPS5 layout", source, output, fields));
        txtAnyStatus.Text = "Queued. See the Tasks tab.";
    }

    // =================================================================== tasks

    private void InitializeQueue()
    {
        _queue.SetPersistencePath(AppState.TasksPath);
        int restored = _queue.RestoreFromDisk(entry =>
        {
            QueuedPackageTask? task = Operations.Restore(entry);
            if (task is not null) _finishedTasks.Add(task.Id); // never re-notify history
            return task;
        });
        if (restored > 0) Log.Info($"Restored {restored} task(s) from the last session.");
        _queue.TasksChanged += (_, _) => _tasksDirty = true;
        _queue.AutoStart = true;
        _tasksDirty = true;
    }

    private void WireTasks()
    {
        chkAutoStart.IsCheckedChanged += (_, _) => _queue.AutoStart = chkAutoStart.IsChecked == true;
        btnTaskStart.Click += (_, _) => _queue.StartNext();
        btnTaskCancel.Click += (_, _) => { if (SelectedTask() is { } t) _queue.Cancel(t.Id); };
        btnTaskRetry.Click += (_, _) => { if (SelectedTask() is { } t) { _finishedTasks.Remove(t.Id); _queue.Retry(t.Id); } };
        btnTaskRemove.Click += (_, _) => { if (SelectedTask() is { } t) _queue.Remove(t.Id); };
        btnTaskClear.Click += (_, _) => _queue.ClearCompleted();
        btnTaskReveal.Click += (_, _) => { if (SelectedTask() is { } t && t.OutputPath.Length > 0) Reveal(t.OutputPath); };
        gridTasks.SelectionChanged += (_, _) => UpdateTaskDetail();
        gridTasks.DoubleTapped += (_, _) => { if (SelectedTask() is { } t && t.OutputPath.Length > 0) Reveal(t.OutputPath); };
    }

    private QueuedPackageTask? SelectedTask() => (gridTasks.SelectedItem as TaskRow)?.Task;

    private void QueueSpec(TaskSpec spec)
    {
        QueuedPackageTask? task = Operations.CreateTask(spec);
        if (task is null)
        {
            Log.Error($"Could not create task {spec.Type}");
            return;
        }
        Enqueue(_queue.Enqueue(task));
    }

    private void Enqueue(QueuedPackageTask task)
    {
        Log.Info($"Queued: {task.DisplayName}");
        _tasksDirty = true;
        RefreshTasks();
        tabsMain.SelectedItem = tabTasks;
        gridTasks.SelectedItem = _taskRows.FirstOrDefault(row => row.Task == task);
    }

    private void RefreshTasks()
    {
        IReadOnlyList<QueuedPackageTask> tasks = _queue.Tasks;
        if (_tasksDirty || tasks.Count != _taskRows.Count)
        {
            _tasksDirty = false;
            QueuedPackageTask? selected = SelectedTask();
            var existing = _taskRows.ToDictionary(row => row.Task);
            _taskRows.Clear();
            foreach (QueuedPackageTask task in tasks.Reverse())
                _taskRows.Add(existing.TryGetValue(task, out TaskRow? row) ? row : new TaskRow(task));
            if (selected is not null) gridTasks.SelectedItem = _taskRows.FirstOrDefault(row => row.Task == selected);
        }
        foreach (TaskRow row in _taskRows)
        {
            if (!row.Task.IsTerminal || !_finishedTasks.Contains(row.Task.Id)) row.Refresh();
            if (row.Task.IsTerminal && _finishedTasks.Add(row.Task.Id)) OnTaskFinished(row.Task);
        }
        int running = tasks.Count(t => t.Status == PackageTaskStatus.Running);
        int queued = tasks.Count(t => t.Status == PackageTaskStatus.Queued);
        txtTaskSummary.Text = $"{running} running · {queued} queued · {tasks.Count} total";
        if (running > 0 && tasks.FirstOrDefault(t => t.Status == PackageTaskStatus.Running) is { } current)
            SetStatus($"{current.DisplayName}: {current.Progress.TaskPercent * 100:N0}%  {current.Progress.Stage}");
        UpdateTaskDetail();
    }

    private void OnTaskFinished(QueuedPackageTask task)
    {
        string status = task.Status.ToString().ToLowerInvariant();
        if (task.Status == PackageTaskStatus.Failed)
            Log.Error($"Task failed: {task.DisplayName}: {task.Failure?.Message ?? task.Message}");
        else
            Log.Info($"Task {status}: {task.DisplayName}");
        SetStatus($"{task.DisplayName}: {status}");
        if (task.Status == PackageTaskStatus.Completed && _settings.RevealOutputWhenDone && task.OutputPath.Length > 0 &&
            task.Type is not (PackageTaskTypes.PackageVerify or PackageTaskTypes.ImageVerify))
            Reveal(task.OutputPath);
    }

    private void UpdateTaskDetail()
    {
        if (SelectedTask() is not { } task)
        {
            txtTaskDetail.Text = string.Empty;
            return;
        }
        string detail = $"{task.DisplayName}\nSource: {task.SourcePath}\nOutput: {task.OutputPath}\n" +
                        $"Status: {task.Status}   Stage: {task.Progress.Stage}";
        if (task.Progress.CurrentFile.Length > 0) detail += $"\nCurrent: {task.Progress.CurrentFile}";
        if (task.Failure is not null) detail += "\n\n" + task.Failure.Message;
        if (txtTaskDetail.Text != detail) txtTaskDetail.Text = detail;
    }

    // =================================================================== settings

    private void WireSettings()
    {
        btnSettingsAddFolder.Click += async (_, _) =>
        {
            if (await PickFolderAsync("Add a library folder") is { } folder &&
                !_folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                _folders.Add(folder);
        };
        btnSettingsRemoveFolder.Click += (_, _) =>
        {
            if (listFolders.SelectedItem is string folder) _folders.Remove(folder);
        };
        btnSettingsWorkspace.Click += async (_, _) =>
        {
            if (await PickFolderAsync("Workspace folder for temporary files") is { } folder) txtSettingsWorkspace.Text = folder;
        };
        btnSettingsSave.Click += (_, _) =>
        {
            string passcode = txtSettingsPasscode.Text?.Trim() ?? string.Empty;
            if (passcode.Length > 0)
            {
                try { SonyDebugPackageCredentials.ValidatePasscode(passcode); }
                catch (Exception ex) when (ex is ArgumentException or FormatException)
                {
                    txtSettingsStatus.Text = "Invalid passcode: " + ex.Message;
                    return;
                }
            }
            _settings.LibraryFolders = _folders.ToList();
            _settings.RecursiveScan = chkRecursive.IsChecked == true;
            _settings.DebugPasscode = passcode;
            _settings.WorkspaceFolder = txtSettingsWorkspace.Text?.Trim() ?? string.Empty;
            OnSaveSettings();
            _settings.RevealOutputWhenDone = chkRevealOutput.IsChecked == true;
            AppState.SaveSettings(_settings);
            txtToolPasscode.Text = DefaultPasscode();
            txtSettingsStatus.Text = "Saved.";
        };
        btnLogOpen.Click += (_, _) => Reveal(AppState.LogPath);
        btnLogClear.Click += (_, _) => _logLines.Clear();
    }

    private void LoadSettingsIntoUi()
    {
        foreach (string folder in _settings.LibraryFolders) _folders.Add(folder);
        chkRecursive.IsChecked = _settings.RecursiveScan;
        txtSettingsPasscode.Text = _settings.DebugPasscode;
        txtSettingsWorkspace.Text = _settings.WorkspaceFolder;
        chkRevealOutput.IsChecked = _settings.RevealOutputWhenDone;
        txtAnyRelinker.Text = _settings.AnyPs5Relinker;
        txtAnyLibs.Text = _settings.AnyPs5Libs;
        chkAnyRelink.IsChecked = _settings.AnyPs5AutoRelink && _settings.AnyPs5Relinker.Length > 0;
        nudAnyJobs.Value = _settings.AnyPs5Jobs;
        chkAnyExecOnly.IsChecked = _settings.AnyPs5ExecutablesOnly;
        txtAbout.Text =
            "AnyPS5 Library is an unofficial macOS companion for AnyPS5. It is built on the open-source engine of " +
            "PS5 PKG Tool by pearlxcore (exFAT, UFS2/FFPKG, PFSC, PFS and the ProsperoPkgTool package engine), and it bundles " +
            "pkg-to-anyps5 by awake-devel (GPL-3.0-or-later, run as a separate process). Not affiliated with Sony or the AnyPS5 project. PlayStation and PS5 are trademarks of Sony Interactive Entertainment Inc. " +
            "The app ships no game content or links; use it only with content you are entitled to use.\n" +
            $"Data folder: {AppState.DataDirectory}";
    }
}
