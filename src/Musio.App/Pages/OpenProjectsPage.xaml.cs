using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Musio.Core.Projects;
using Musio_App.Services;
using Windows.Storage.Streams;

namespace Musio_App.Pages;

/// <summary>
/// One saved project as shown in the Open page.
/// </summary>
public sealed class ProjectCard
{
    public string Path { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;

    /// <summary>
    /// When the recording was created, in local time. Drives both the day grouping and
    /// the order within a day.
    /// </summary>
    public DateTime CreatedAt { get; init; }

    public BitmapImage? Poster { get; init; }
}

/// <summary>
/// One day's worth of projects, as shown under a single header on the Open page.
/// </summary>
public sealed class ProjectGroup
{
    public string Header { get; init; } = string.Empty;
    public List<ProjectCard> Items { get; init; } = new();
}

/// <summary>
/// Lists saved <c>.musio</c> projects so they can be reopened without hunting for the
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
                var (name, subtitle, created, poster) = await Task.Run(() => ReadCardData(entry));

                BitmapImage? image = null;
                if (poster is { Length: > 0 })
                    image = await CreateBitmapAsync(poster);

                cards.Add(new ProjectCard
                {
                    Path = entry.Path,
                    Name = name,
                    Subtitle = subtitle,
                    CreatedAt = created,
                    Poster = image,
                });
            }

            ProjectsSource.Source = GroupByDay(cards);
            ProjectsGrid.ItemsSource = ProjectsSource.View;
            EmptyState.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Musio.Core.Diagnostics.DiagLog.Write("OpenProjects", $"Refresh failed: {ex}");
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
    /// Builds the project list from the recent-projects index, plus any <c>.musio</c>
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

                foreach (var file in Directory.EnumerateFiles(
                             folder, "*" + MusioPackage.FileExtension, SearchOption.TopDirectoryOnly))
                {
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
                Musio.Core.Diagnostics.DiagLog.Write("OpenProjects", $"Scan '{folder}' failed: {ex.Message}");
            }
        }

        return byPath.Values.OrderByDescending(e => e.LastUsedUtc).ToList();
    }

    private static IEnumerable<string> SaveFolders()
    {
        var configured = Musio.Core.Settings.AppSettings.Instance.DefaultSavePath;
        if (!string.IsNullOrWhiteSpace(configured))
            yield return configured;

        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        yield return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Musio");
    }

    /// <summary>
    /// Buckets cards into one group per calendar day, newest day first and newest
    /// project first within each day.
    /// </summary>
    /// <remarks>
    /// Keyed on creation rather than last-used so a project's position never moves just
    /// because it was opened — the list reads the same every time the page is visited.
    /// </remarks>
    private static List<ProjectGroup> GroupByDay(IEnumerable<ProjectCard> cards) => cards
        .GroupBy(c => c.CreatedAt.Date)
        .OrderByDescending(g => g.Key)
        .Select(g => new ProjectGroup
        {
            Header = FormatDayHeader(g.Key),
            Items = g.OrderByDescending(c => c.CreatedAt).ToList(),
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

    private static (string Name, string Subtitle, DateTime Created, byte[]? Poster) ReadCardData(
        RecentProject entry)
    {
        var manifest = MusioPackageService.ReadManifest(entry.Path);
        var poster = MusioPackageService.ReadPoster(entry.Path);

        // The file name wins: it is what the user chose, what Explorer shows, and it stays
        // right even if the file is renamed outside the app. The stored project name is
        // only a fallback for a package whose file name is somehow unusable.
        var name = Path.GetFileNameWithoutExtension(entry.Path);
        if (string.IsNullOrWhiteSpace(name))
            name = manifest?.Project.Name;
        if (string.IsNullOrWhiteSpace(name))
            name = entry.Name;

        var duration = manifest?.Project.Duration ?? entry.Duration;
        var saved = manifest?.SavedAt.ToLocalTime() ?? entry.LastUsedUtc.ToLocalTime();

        long size = 0;
        try { size = new FileInfo(entry.Path).Length; } catch { }

        var subtitle = $"{FormatDuration(duration)}  ·  {FormatBytes(size)}  ·  {saved:d MMM yyyy}";
        return (name!, subtitle, ResolveCreatedAt(entry, manifest), poster);
    }

    /// <summary>
    /// When the recording behind a package was made, in local time.
    /// </summary>
    /// <remarks>
    /// The manifest's project is authoritative and travels with the file, so a project
    /// copied between machines still groups under the day it was recorded. Falls back to
    /// the file's own creation stamp, then to the recents entry, so a package with an
    /// unreadable manifest still lands in a sensible group instead of at the epoch.
    /// </remarks>
    private static DateTime ResolveCreatedAt(RecentProject entry, MusioManifest? manifest)
    {
        var created = manifest?.Project.CreatedAt ?? default;
        if (created != default)
            return created.Kind == DateTimeKind.Utc ? created.ToLocalTime() : created;

        try
        {
            var fileCreated = File.GetCreationTime(entry.Path);
            if (fileCreated != default) return fileCreated;
        }
        catch { }

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
            Musio.Core.Diagnostics.DiagLog.Write("OpenProjects", $"Poster decode failed: {ex.Message}");
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
        var picker = new Windows.Storage.Pickers.FileOpenPicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.VideosLibrary,
        };
        picker.FileTypeFilter.Add(MusioPackage.FileExtension);

        var window = App.Current.MainAppWindow;
        if (window is not null)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        try
        {
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
                await OpenAsync(file.Path);
        }
        catch (Exception ex)
        {
            Musio.Core.Diagnostics.DiagLog.Write("OpenProjects", $"Open picker failed: {ex.Message}");
        }
    }

    private async Task OpenAsync(string packagePath)
    {
        try
        {
            await ProjectService.Instance.OpenPackageAsync(packagePath);
            (App.Current.MainAppWindow as MainWindow)?.ShowEditor();
        }
        catch (Exception ex)
        {
            Musio.Core.Diagnostics.DiagLog.Write("OpenProjects", $"Open '{packagePath}' failed: {ex}");

            // A project that cannot be opened should not keep occupying the list.
            if (!File.Exists(packagePath))
                RecentProjectsStore.Forget(packagePath);

            try
            {
                var dialog = new ContentDialog
                {
                    Title = "Could not open project",
                    Content = ex.Message,
                    CloseButtonText = "OK",
                    XamlRoot = XamlRoot,
                };
                await dialog.ShowAsync();
            }
            catch { }

            await RefreshAsync();
        }
    }
}
