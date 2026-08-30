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

    public OpenProjectsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_isLoading) return;
        _isLoading = true;

        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;

        try
        {
            var entries = await Task.Run(DiscoverProjects);

            var cards = new List<ProjectCard>(entries.Count);
            foreach (var entry in entries)
            {
                // Manifest and poster are read off the UI thread; the BitmapImage itself
                // must be created on it.
                var (name, subtitle, modified, poster) = await Task.Run(() => ReadCardData(entry));

                BitmapImage? image = null;
                if (poster is { Length: > 0 })
                    image = await CreateBitmapAsync(poster);

                cards.Add(new ProjectCard
                {
                    Path = entry.Path,
                    Name = name,
                    Subtitle = subtitle,
                    ModifiedAt = modified,
                    Poster = image,
                });
            }

            ProjectsSource.Source = GroupByDay(cards);
            ProjectsGrid.ItemsSource = ProjectsSource.View;
            EmptyState.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Mixtri.Core.Diagnostics.DiagLog.Write("OpenProjects", $"Refresh failed: {ex}");
            EmptyState.Visibility = Visibility.Visible;
        }
        finally
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
            _isLoading = false;
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

        // Ordering is left to GroupByDay, which re-sorts everything on ModifiedAt.
        return byPath.Values.ToList();
    }

    private static IEnumerable<string> SaveFolders()
    {
        var configured = Mixtri.Core.Settings.AppSettings.Instance.DefaultSavePath;
        if (!string.IsNullOrWhiteSpace(configured))
            yield return configured;

        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        yield return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Mixtri");
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
        var manifest = MixtriPackageService.ReadManifest(entry.Path);
        var poster = MixtriPackageService.ReadPoster(entry.Path);

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
