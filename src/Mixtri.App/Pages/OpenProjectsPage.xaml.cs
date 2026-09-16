using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Mixtri.Core.Projects;
using Mixtri_App.Helpers;
using Mixtri_App.Services;
using Windows.Storage.Streams;

namespace Mixtri_App.Pages;

/// <summary>
/// One saved project as shown in the Gallery.
/// </summary>
public sealed class ProjectCard
{
    public string Path { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;

    /// <summary>
    /// When the project was last saved, in local time. Drives both the day grouping and
    /// the order within a day, and is the same value the card's date shows.
    /// </summary>
    public DateTime ModifiedAt { get; init; }
}

/// <summary>
/// One day's worth of projects, as shown under a single header in the Gallery.
/// </summary>
public sealed class ProjectGroup
{
    public string Header { get; init; } = string.Empty;
    public List<ProjectCard> Items { get; init; } = new();
}

/// <summary>
/// Lists saved <c>.mixtri</c> projects so they can be reopened without hunting for the
/// file.
/// </summary>
/// <remarks>
/// Cards use package manifests; posters are loaded only for realized image controls.
/// No recording media is extracted, and cards do not retain decoded images offscreen.
/// </remarks>
public sealed partial class OpenProjectsPage : Page
{
    private bool _isLoading;
    private bool _pageLoaded;
    private bool _postersVisible;
    private bool _refreshNeeded = true;
    private int _refreshGeneration;
    private CancellationTokenSource? _refreshCts;
    private readonly HashSet<Image> _realizedPosters = [];
    private readonly Dictionary<Image, PosterRequest> _posterRequests = [];
    private readonly SemaphoreSlim _posterGate = new(1, 1);

    private sealed class PosterRequest(ProjectCard card)
    {
        internal ProjectCard Card { get; } = card;
        internal CancellationTokenSource? Cancellation = new();

        internal void Cancel()
        {
            var cancellation = Cancellation;
            Cancellation = null;
            cancellation?.Cancel();
            cancellation?.Dispose();
        }
    }

    public OpenProjectsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _pageLoaded = true;
        SetImageResourceVisibility(App.Current.MainAppWindow is not MainWindow main || main.IsForegroundVisible);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _pageLoaded = false;
        SetImageResourceVisibility(false);
        _realizedPosters.Clear();
        ProjectsGrid.ItemsSource = null;
        ProjectsSource.Source = null;
        _refreshNeeded = true;
    }

    public void SetImageResourceVisibility(bool visible)
    {
        visible &= _pageLoaded;
        if (_postersVisible == visible) return;
        _postersVisible = visible;
        if (!_postersVisible)
        {
            _refreshGeneration++;
            _refreshCts?.Cancel();
            _refreshCts?.Dispose();
            _refreshCts = null;
            _refreshNeeded |= _isLoading;
            _isLoading = false;
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            ClearPosters();
            return;
        }
        if (_refreshNeeded) _ = RefreshAsync();
        else foreach (var image in _realizedPosters) StartPosterLoad(image);
    }

    private async Task RefreshAsync()
    {
        _refreshNeeded = true;
        if (!_pageLoaded || !_postersVisible) return;
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        using var cancellation = new CancellationTokenSource();
        _refreshCts = cancellation;
        var ct = cancellation.Token;
        int generation = ++_refreshGeneration;
        _isLoading = true;

        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;

        try
        {
            var entries = await Task.Run(() => DiscoverProjects(ct), ct);

            var cards = new List<ProjectCard>(entries.Count);
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                var (name, subtitle, modified) = await Task.Run(() => ReadCardData(entry), ct);
                ct.ThrowIfCancellationRequested();

                cards.Add(new ProjectCard
                {
                    Path = entry.Path,
                    Name = name,
                    Subtitle = subtitle,
                    ModifiedAt = modified,
                });
            }

            if (!_pageLoaded || !_postersVisible || generation != _refreshGeneration) return;
            ClearPosters();
            ProjectsSource.Source = GroupByDay(cards);
            ProjectsGrid.ItemsSource = ProjectsSource.View;
            EmptyState.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _refreshNeeded = false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Mixtri.Core.Diagnostics.DiagLog.Write("OpenProjects", $"Refresh failed: {ex}");
            if (_pageLoaded && generation == _refreshGeneration) EmptyState.Visibility = Visibility.Visible;
        }
        finally
        {
            if (generation == _refreshGeneration)
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
                _isLoading = false;
            }
            if (ReferenceEquals(_refreshCts, cancellation)) _refreshCts = null;
        }
    }

    /// <summary>
    /// Builds the project list from the recent-projects index, plus any <c>.mixtri</c>
    /// files sitting in the user's save folder.
    /// </summary>
    /// <remarks>
    /// The index alone would miss projects saved before it existed, and projects created
    /// on another machine and copied in. Scanning the save folder makes the page show
    /// what the user actually has, not just what this install happens to have recorded.
    /// </remarks>
    private static List<RecentProject> DiscoverProjects(CancellationToken ct)
    {
        var byPath = new Dictionary<string, RecentProject>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in RecentProjectsStore.Load())
        {
            ct.ThrowIfCancellationRequested();
            byPath[entry.Path] = entry;
        }

        foreach (var folder in SaveFolders())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(folder))
                    continue;

                // Enumerate everything and filter through the shared predicate rather than
                // globbing per extension: Win32 extension patterns match 8.3 short names too,
                // so "*.musio" can return files the app does not consider packages. This also
                // keeps "is this a project file?" answered in exactly one place.
                foreach (var file in Directory.EnumerateFiles(
                             folder, "*", SearchOption.TopDirectoryOnly))
                {
                    if (!MixtriPackage.IsPackagePath(file))
                        continue;

                    if (byPath.ContainsKey(file))
                        continue;

                    byPath[file] = new RecentProject
                    {
                        Path = file,
                        Name = System.IO.Path.GetFileNameWithoutExtension(file),
                        LastUsedUtc = File.GetLastWriteTimeUtc(file),
                    };
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Mixtri.Core.Diagnostics.DiagLog.Write("OpenProjects", $"Scan '{folder}' failed: {ex.Message}");
            }
        }

        // Ordering is left to GroupByDay, which re-sorts everything on ModifiedAt.
        return byPath.Values.ToList();
    }

    private static IEnumerable<string> SaveFolders()
    {
        var configured = Mixtri.Core.Settings.AppSettings.Instance.DefaultSavePath;
        if (!string.IsNullOrWhiteSpace(configured))
            yield return configured;

        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

        // Both the current and pre-rename Videos folders: projects the user saved into
        // Videos\Musio before the rename must keep showing up here.
        foreach (var folder in Mixtri.Core.AppDataPaths.AllVideosFolders)
            yield return folder;
    }

    /// <summary>
    /// Buckets cards into one group per calendar day, newest day first and newest
    /// project first within each day.
    /// </summary>
    /// <remarks>
    /// Keyed on when the project was last saved, which is also the date printed on the
    /// card — so a group header can never disagree with the cards under it.
    /// </remarks>
    private static List<ProjectGroup> GroupByDay(IEnumerable<ProjectCard> cards) => cards
        .GroupBy(c => c.ModifiedAt.Date)
        .OrderByDescending(g => g.Key)
        .Select(g => new ProjectGroup
        {
            Header = FormatDayHeader(g.Key),
            // Name breaks ties so the order does not depend on the order projects were
            // discovered in, which is dictionary order and not stable.
            Items = g
                .OrderByDescending(c => c.ModifiedAt)
                .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
        })
        .ToList();

    /// <summary>Names a day group: "Today", "Yesterday", or the full date.</summary>
    private static string FormatDayHeader(DateTime day)
    {
        var today = DateTime.Now.Date;
        if (day == today) return "Today";
        if (day == today.AddDays(-1)) return "Yesterday";

        // The year is noise for anything recorded this year, and the weekday is the part
        // people actually recognise when scanning a recent list.
        return day.Year == today.Year
            ? day.ToString("dddd, d MMMM")
            : day.ToString("d MMMM yyyy");
    }

    private static (string Name, string Subtitle, DateTime Modified) ReadCardData(
        RecentProject entry)
    {
        var manifest = MixtriPackageService.ReadManifest(entry.Path);

        // The file name wins: it is what the user chose, what Explorer shows, and it stays
        // right even if the file is renamed outside the app. The stored project name is
        // only a fallback for a package whose file name is somehow unusable.
        var name = Path.GetFileNameWithoutExtension(entry.Path);
        if (string.IsNullOrWhiteSpace(name))
            name = manifest?.Project.Name;
        if (string.IsNullOrWhiteSpace(name))
            name = entry.Name;

        var duration = manifest?.Project.Duration ?? entry.Duration;

        // One probe serves both the size and the modified date. FileInfo caches its
        // metadata when Exists is read, so a file that vanishes mid-refresh still yields
        // the values captured here rather than throwing.
        FileInfo? file = null;
        try
        {
            var info = new FileInfo(entry.Path);
            if (info.Exists) file = info;
        }
        catch { }

        var size = file?.Length ?? 0;
        var modified = ResolveModifiedAt(entry, manifest, file);

        var subtitle = $"{FormatDuration(duration)}  ·  {FormatBytes(size)}  ·  {modified:d MMM yyyy}";
        return (name!, subtitle, modified);
    }

    /// <summary>
    /// When a package was last modified, in local time.
    /// </summary>
    /// <remarks>
    /// The file's own write time is preferred: it is the "date modified" Explorer shows,
    /// it is always present for a file that exists, and a Windows copy carries it to
    /// another machine. It is taken from a probed <see cref="FileInfo"/> rather than from
    /// <see cref="File.GetLastWriteTime"/>, which for a missing or unreachable path
    /// neither throws nor returns <c>default</c> — it returns 1601-01-01, which would
    /// park the card under a phantom "31 December 1600" header.
    /// </remarks>
    private static DateTime ResolveModifiedAt(
        RecentProject entry, MixtriManifest? manifest, FileInfo? file)
    {
        if (file is not null) return file.LastWriteTime;

        // The manifest travels inside the package, so it still dates a project whose file
        // could not be stat'd. It cannot be range-checked the way the write time can:
        // MixtriManifest.SavedAt is initialised to DateTimeOffset.UtcNow, so a manifest
        // that omits the field deserialises to the moment it was read, not to default.
        if (manifest is not null) return manifest.SavedAt.ToLocalTime().DateTime;

        return entry.LastUsedUtc.ToLocalTime().DateTime;
    }

    private void Poster_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image) return;
        _realizedPosters.Add(image);
        StartPosterLoad(image);
    }

    private void Poster_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Image image) return;
        _realizedPosters.Remove(image);
        CancelPoster(image);
    }

    private void Poster_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is Image image && _realizedPosters.Contains(image)) StartPosterLoad(image);
    }

    private void CancelPoster(Image image)
    {
        if (_posterRequests.Remove(image, out var request)) request.Cancel();
        image.Source = null;
    }

    private void ClearPosters()
    {
        foreach (var request in _posterRequests.Values) request.Cancel();
        _posterRequests.Clear();
        foreach (var image in _realizedPosters) image.Source = null;
    }

    private void StartPosterLoad(Image image)
    {
        if (!_pageLoaded || !_postersVisible || !_realizedPosters.Contains(image)) return;
        if (image.DataContext is not ProjectCard card)
        {
            CancelPoster(image);
            return;
        }
        if (_posterRequests.TryGetValue(image, out var current) && ReferenceEquals(current.Card, card)) return;
        CancelPoster(image);
        var request = new PosterRequest(card);
        _posterRequests[image] = request;
        _ = LoadPosterAsync(image, request);
    }

    private async Task LoadPosterAsync(Image image, PosterRequest request)
    {
        var cancellation = request.Cancellation!;
        var ct = cancellation.Token;
        bool entered = false;
        try
        {
            await _posterGate.WaitAsync(ct);
            entered = true;
            var bytes = await Task.Run(() => MixtriPackageService.ReadPoster(request.Card.Path), ct);
            ct.ThrowIfCancellationRequested();
            if (bytes is not { Length: > 0 }) return;
            var bitmap = await CreateBitmapAsync(bytes, ct);
            if (!ct.IsCancellationRequested && _pageLoaded && _postersVisible
                && _realizedPosters.Contains(image) && ReferenceEquals(image.DataContext, request.Card)
                && _posterRequests.TryGetValue(image, out var current) && ReferenceEquals(current, request))
                image.Source = bitmap;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Mixtri.Core.Diagnostics.DiagLog.Write("OpenProjects", $"Poster load failed: {ex}");
        }
        finally
        {
            if (entered) _posterGate.Release();
            if (ReferenceEquals(request.Cancellation, cancellation)) request.Cancellation = null;
            cancellation.Dispose();
        }
    }

    private static async Task<BitmapImage?> CreateBitmapAsync(byte[] bytes, CancellationToken ct)
    {
        try
        {
            var image = new BitmapImage();
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync().AsTask(ct);
                await writer.FlushAsync().AsTask(ct);
                writer.DetachStream();
            }

            stream.Seek(0);
            await image.SetSourceAsync(stream).AsTask(ct);
            return image;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Mixtri.Core.Diagnostics.DiagLog.Write("OpenProjects", $"Poster decode failed: {ex.Message}");
            return null;
        }
    }

    private static string FormatDuration(TimeSpan duration) => duration.TotalHours >= 1
        ? $"{(int)duration.TotalHours}:{duration.Minutes:D2}:{duration.Seconds:D2}"
        : $"{duration.Minutes}:{duration.Seconds:D2}";

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F0} KB",
        _ => $"{bytes} B",
    };

    private async void ProjectsGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ProjectCard card)
            await OpenAsync(card.Path);
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Legacy extensions included: the open picker must still reach projects the user
            // saved before the rename. The SAVE picker deliberately offers only the current
            // extension, so re-saving migrates them.
            var file = await PickerHelper.PickSingleFileAsync(
                Windows.Storage.Pickers.PickerLocationId.VideosLibrary,
                MixtriPackage.AllExtensions);
            if (file is not null)
                await OpenAsync(file.Path);
        }
        catch (Exception ex)
        {
            Mixtri.Core.Diagnostics.DiagLog.Write("OpenProjects", $"Open picker failed: {ex.Message}");
        }
    }

    private async Task OpenAsync(string packagePath)
    {
        // Opening REPLACES whatever this window is holding. A never-saved recording is the
        // dangerous case — it is clean, so the dirty flag says nothing, yet there is no file
        // to reopen it from. The cross-process route (App.ServeRedirectedOpen) already
        // refuses on the same predicate; this one can actually ask.
        if (!await ProjectSaveCoordinator.ConfirmDiscardCurrentProjectAsync(
                XamlRoot, App.Current.MainAppWindow, "before opening another one?"))
        {
            return;
        }

        try
        {
            if (App.Current.EditorProcesses is { IsRecorder: true } processes)
            {
                await processes.OpenPackageAsync(packagePath);
                return;
            }
            await ProjectService.Instance.OpenPackageAsync(packagePath);
            (App.Current.MainAppWindow as MainWindow)?.ShowEditor();
        }
        catch (Exception ex)
        {
            Mixtri.Core.Diagnostics.DiagLog.Write("OpenProjects", $"Open '{packagePath}' failed: {ex}");

            // A project that cannot be opened should not keep occupying the list.
            if (!File.Exists(packagePath))
                RecentProjectsStore.Forget(packagePath);

            try
            {
                await DialogHelper.ShowErrorAsync(XamlRoot, "Could not open project", ex.Message);
            }
            catch { }

            await RefreshAsync();
        }
    }
}
