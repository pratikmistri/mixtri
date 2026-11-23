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

    public BitmapImage? Poster { get; init; }

    /// <summary>
    /// What a screen reader announces for the card's container: the GridViewItem's
    /// automation name falls back to the item's ToString, not to the template root.
    /// </summary>
    public override string ToString() => string.IsNullOrEmpty(Subtitle) ? Name : $"{Name}, {Subtitle}";
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
/// Cards are built from each package's manifest and poster entry only — no media is
/// extracted — so listing stays cheap regardless of how large the recordings are.
/// </remarks>
public sealed partial class OpenProjectsPage : Page
{
    private bool _isLoading;

    /// <summary>Set when a refresh was requested while one was running; it reruns afterwards.</summary>
    private bool _refreshPending;

    /// <summary>Cancels the running refresh when a newer one is requested or the page unloads.</summary>
    private CancellationTokenSource? _refreshCts;

    /// <summary>Upper bound on projects listed, newest first, so discovery stays bounded.</summary>
    private const int MaxProjects = 200;

    /// <summary>How many packages are read concurrently.</summary>
    private const int ReadParallelism = 4;

    /// <summary>Set while a project is opening, so a second click cannot start another.</summary>
    private bool _isOpening;

    public OpenProjectsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => _refreshCts?.Cancel();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        // Re-entry guard: only one refresh runs. A request that arrives meanwhile
        // supersedes it — the running one is cancelled and rerun from scratch.
        if (_isLoading)
        {
            _refreshPending = true;
            _refreshCts?.Cancel();
            return;
        }
        _isLoading = true;

        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;

        try
        {
            do
            {
                _refreshPending = false;
                using var cts = new CancellationTokenSource();
                _refreshCts = cts;
                try
                {
                    await RefreshCoreAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Superseded (or the page went away); the loop reruns if requested.
                }
                catch (Exception ex)
                {
                    Mixtri.Core.Diagnostics.DiagLog.Write("OpenProjects", $"Refresh failed: {ex}");
                    EmptyState.Visibility = Visibility.Visible;
                }
                finally
                {
                    if (ReferenceEquals(_refreshCts, cts)) _refreshCts = null;
                }
            }
            while (_refreshPending);
        }
        finally
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            _isLoading = false;
        }
    }

    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        // Discovery (including RecentProjectsStore.Load's File.Exists probes) runs off the
        // UI thread.
        var entries = await Task.Run(DiscoverProjects, ct);
        ct.ThrowIfCancellationRequested();

        // Readers run on the thread pool with bounded parallelism and hand finished cards
        // over through a channel; this method (on the UI thread) builds the BitmapImage and
        // publishes, so the first cards appear without waiting for the last.
        var channel = System.Threading.Channels.Channel.CreateUnbounded<(RecentProject Entry,
            (string Name, string Subtitle, DateTime Modified, byte[]? Poster) Data)>();

        var producer = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(
                    entries,
                    new ParallelOptions { MaxDegreeOfParallelism = ReadParallelism, CancellationToken = ct },
                    (entry, token) =>
                    {
                        (string, string, DateTime, byte[]?) data;
                        try
                        {
                            data = ReadCardData(entry);
                        }
                        catch (Exception ex)
                        {
                            // One unreadable package must not blank the whole gallery.
                            Mixtri.Core.Diagnostics.DiagLog.Write(
                                "OpenProjects", $"Card '{entry.Path}' unreadable: {ex.Message}");
                            return ValueTask.CompletedTask;
                        }

                        channel.Writer.TryWrite((entry, data));
                        return ValueTask.CompletedTask;
                    });
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        var cards = new List<ProjectCard>(entries.Count);
        var lastPublish = DateTime.UtcNow;
        var published = 0;

        await foreach (var (entry, data) in channel.Reader.ReadAllAsync(ct))
        {
            BitmapImage? image = null;
            if (data.Poster is { Length: > 0 })
                image = await CreateBitmapAsync(data.Poster);

            ct.ThrowIfCancellationRequested();

            cards.Add(new ProjectCard
            {
                Path = entry.Path,
                Name = data.Name,
                Subtitle = data.Subtitle,
                ModifiedAt = data.Modified,
                Poster = image,
            });

            // First card immediately, then at most ~4 republishes a second: the grouped
            // source is rebuilt on each publish, so it is batched rather than per card.
            if (published == 0 || (DateTime.UtcNow - lastPublish).TotalMilliseconds >= 250)
            {
                Publish(cards);
                published = cards.Count;
                lastPublish = DateTime.UtcNow;
            }
        }

        await producer;
        ct.ThrowIfCancellationRequested();

        if (cards.Count != published || cards.Count == 0)
            Publish(cards);

        EmptyState.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Publish(List<ProjectCard> cards)
    {
        ProjectsSource.Source = GroupByDay(cards);
        ProjectsGrid.ItemsSource = ProjectsSource.View;
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
    private static List<RecentProject> DiscoverProjects()
    {
        var byPath = new Dictionary<string, RecentProject>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in RecentProjectsStore.Load())
            byPath[entry.Path] = entry;

        foreach (var folder in SaveFolders())
        {
            try
            {
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
            catch (Exception ex)
            {
                Mixtri.Core.Diagnostics.DiagLog.Write("OpenProjects", $"Scan '{folder}' failed: {ex.Message}");
            }
        }

        // Newest first, capped: the gallery shows what the user touched lately, and an
        // unbounded scan of a big Videos folder would otherwise open every package. Display
        // order within the page is still decided by GroupByDay on ModifiedAt.
        return byPath.Values
            .OrderByDescending(e => e.LastUsedUtc)
            .Take(MaxProjects)
            .ToList();
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

    private static (string Name, string Subtitle, DateTime Modified, byte[]? Poster) ReadCardData(
        RecentProject entry)
    {
        var (manifest, poster) = MixtriPackageService.ReadManifestAndPoster(entry.Path);

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
        return (name!, subtitle, modified, poster);
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

    private static async Task<BitmapImage?> CreateBitmapAsync(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            await image.SetSourceAsync(stream);
            return image;
        }
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
        // One open at a time: a double-click, or a second card clicked while the first was
        // still unpacking, used to start an overlapping open that raced the first one.
        if (_isOpening || ProjectService.Instance.OpenInFlightPath is not null) return;
        _isOpening = true;
        try
        {
            await OpenCoreAsync(packagePath);
        }
        finally
        {
            _isOpening = false;
            SetOpeningUi(false);
        }
    }

    /// <summary>Disables the gallery and shows the progress ring while a project opens.</summary>
    private void SetOpeningUi(bool opening)
    {
        if (ProjectsGrid is not null) ProjectsGrid.IsEnabled = !opening;
        if (OpenButton is not null) OpenButton.IsEnabled = !opening;
        if (LoadingRing is not null && !_isLoading)
        {
            LoadingRing.IsActive = opening;
            LoadingRing.Visibility = opening ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async Task OpenCoreAsync(string packagePath)
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

        SetOpeningUi(true);
        try
        {
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
