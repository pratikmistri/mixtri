using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Musio.Core.Export;
using Musio.Core.Models;
using Musio.Core.Processing;
using Musio.Core.Services;
using Musio.Core.Settings;
using Musio_App.Services;
using Windows.Storage.Pickers;

namespace Musio_App.ViewModels;

public partial class ExportViewModel : ObservableObject
{
    private readonly PresetManager _presetManager;
    private readonly ExportEngine _exportEngine;
    private CancellationTokenSource? _exportCts;

    public ExportViewModel()
    {
        _presetManager = new PresetManager();
        _exportEngine = new ExportEngine();

        // Pull current state from the shared ProjectService
        CurrentProject = ProjectService.Instance.CurrentProject;
        CompositionConfig = ProjectService.Instance.CurrentComposition;
        PrefillOutputPath();

        ProjectService.Instance.ProjectChanged += OnProjectChanged;
    }

    private void OnProjectChanged(object? sender, EventArgs e)
    {
        PrepareForExport();
    }

    /// <summary>
    /// Resets export state and generates a fresh output path for a new export.
    /// Called when starting a new export or when the project changes.
    /// </summary>
    public void PrepareForExport()
    {
        ExportSucceeded = false;
        ExportFailed = false;
        ErrorMessage = string.Empty;
        ExportedFilePath = string.Empty;
        ProgressPercent = 0;
        ProgressStatus = string.Empty;
        EstimatedTimeRemaining = string.Empty;

        CurrentProject = ProjectService.Instance.CurrentProject;
        CompositionConfig = ProjectService.Instance.CurrentComposition;
        PrefillOutputPath();
    }

    /// <summary>
    /// Auto-generates an output path from the project name into the Videos folder.
    /// </summary>
    private void PrefillOutputPath()
    {
        if (CurrentProject is null) return;

        string videosDir = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        string musioDir = Path.Combine(videosDir, "Musio");
        Directory.CreateDirectory(musioDir);

        string safeName = SanitizeFileName(CurrentProject.Name);
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = $"recording_{DateTime.Now:yyyyMMdd_HHmmss}";

        string path = Path.Combine(musioDir, $"{safeName}.mp4");

        // Ensure unique filename
        int counter = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(musioDir, $"{safeName} ({counter}).mp4");
            counter++;
        }

        OutputPath = path;
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray());
    }

    // --- Preset ---

    [ObservableProperty]
    private ObservableCollection<ExportPreset> _availablePresets = [];

    [ObservableProperty]
    private ExportPreset? _selectedPreset;

    [ObservableProperty]
    private string _newPresetName = string.Empty;

    partial void OnSelectedPresetChanged(ExportPreset? value)
    {
        if (value is not null)
        {
            ApplyPreset(value);
        }
    }

    // --- Project & composition context ---

    /// <summary>
    /// The current project to export. Set from navigation or code-behind.
    /// </summary>
    [ObservableProperty]
    private Project? _currentProject;

    /// <summary>
    /// The composition configuration from the editor. Set from navigation or code-behind.
    /// </summary>
    [ObservableProperty]
    private CompositionConfig _compositionConfig = new();

    partial void OnCurrentProjectChanged(Project? value)
    {
        ExportCommand.NotifyCanExecuteChanged();
    }

    // --- Export settings ---

    [ObservableProperty]
    private VideoResolution _selectedResolution = VideoResolution.HD1080;

    [ObservableProperty]
    private VideoFormat _selectedFormat = VideoFormat.MP4;

    [ObservableProperty]
    private AspectRatio _selectedAspectRatio = AspectRatio.Auto;

    /// <summary>
    /// Index-based binding for the aspect ratio ComboBox.
    /// </summary>
    public int AspectRatioIndex
    {
        get => (int)SelectedAspectRatio;
        set
        {
            if (Enum.IsDefined(typeof(AspectRatio), value))
            {
                SelectedAspectRatio = (AspectRatio)value;
                OnPropertyChanged();
            }
        }
    }

    partial void OnSelectedAspectRatioChanged(AspectRatio value)
    {
        OnPropertyChanged(nameof(AspectRatioIndex));
    }

    [ObservableProperty]
    private VideoQuality _selectedQuality = VideoQuality.High;

    [ObservableProperty]
    private int _selectedFps = 30;

    [ObservableProperty]
    private string _outputPath = string.Empty;

    // --- Progress ---

    [ObservableProperty]
    private bool _isExporting;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _progressStatus = string.Empty;

    [ObservableProperty]
    private string _estimatedTimeRemaining = string.Empty;

    [ObservableProperty]
    private bool _exportSucceeded;

    [ObservableProperty]
    private string _exportedFilePath = string.Empty;

    [ObservableProperty]
    private bool _exportFailed;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    // --- Resolution helpers for RadioButtons ---

    public bool IsResolution720
    {
        get => SelectedResolution == VideoResolution.HD720;
        set { if (value) SelectedResolution = VideoResolution.HD720; }
    }

    public bool IsResolution1080
    {
        get => SelectedResolution == VideoResolution.HD1080;
        set { if (value) SelectedResolution = VideoResolution.HD1080; }
    }

    public bool IsResolution2K
    {
        get => SelectedResolution == VideoResolution.QHD;
        set { if (value) SelectedResolution = VideoResolution.QHD; }
    }

    public bool IsResolution4K
    {
        get => SelectedResolution == VideoResolution.UHD4K;
        set { if (value) SelectedResolution = VideoResolution.UHD4K; }
    }

    partial void OnSelectedResolutionChanged(VideoResolution value)
    {
        OnPropertyChanged(nameof(IsResolution720));
        OnPropertyChanged(nameof(IsResolution1080));
        OnPropertyChanged(nameof(IsResolution2K));
        OnPropertyChanged(nameof(IsResolution4K));
    }

    // --- Format helpers ---

    public bool IsFormatMP4
    {
        get => SelectedFormat == VideoFormat.MP4;
        set { if (value) SelectedFormat = VideoFormat.MP4; }
    }

    public bool IsFormatGIF
    {
        get => SelectedFormat == VideoFormat.GIF;
        set { if (value) SelectedFormat = VideoFormat.GIF; }
    }

    public bool IsFormatWebM
    {
        get => SelectedFormat == VideoFormat.WebM;
        set { if (value) SelectedFormat = VideoFormat.WebM; }
    }

    partial void OnSelectedFormatChanged(VideoFormat value)
    {
        OnPropertyChanged(nameof(IsFormatMP4));
        OnPropertyChanged(nameof(IsFormatGIF));
        OnPropertyChanged(nameof(IsFormatWebM));
    }

    // --- Quality helpers ---

    public bool IsQualityDraft
    {
        get => SelectedQuality == VideoQuality.Draft;
        set { if (value) SelectedQuality = VideoQuality.Draft; }
    }

    public bool IsQualityStandard
    {
        get => SelectedQuality == VideoQuality.Standard;
        set { if (value) SelectedQuality = VideoQuality.Standard; }
    }

    public bool IsQualityHigh
    {
        get => SelectedQuality == VideoQuality.High;
        set { if (value) SelectedQuality = VideoQuality.High; }
    }

    public bool IsQualityUltra
    {
        get => SelectedQuality == VideoQuality.Ultra;
        set { if (value) SelectedQuality = VideoQuality.Ultra; }
    }

    partial void OnSelectedQualityChanged(VideoQuality value)
    {
        OnPropertyChanged(nameof(IsQualityDraft));
        OnPropertyChanged(nameof(IsQualityStandard));
        OnPropertyChanged(nameof(IsQualityHigh));
        OnPropertyChanged(nameof(IsQualityUltra));
    }

    // --- FPS helpers ---

    public bool IsFps30
    {
        get => SelectedFps == 30;
        set { if (value) SelectedFps = 30; }
    }

    public bool IsFps60
    {
        get => SelectedFps == 60;
        set { if (value) SelectedFps = 60; }
    }

    partial void OnSelectedFpsChanged(int value)
    {
        OnPropertyChanged(nameof(IsFps30));
        OnPropertyChanged(nameof(IsFps60));
    }

    // --- Commands ---

    [RelayCommand]
    private void LoadPresets()
    {
        var presets = _presetManager.LoadExportPresets();
        AvailablePresets = new ObservableCollection<ExportPreset>(presets);
    }

    [RelayCommand]
    private void SavePreset()
    {
        var name = string.IsNullOrWhiteSpace(NewPresetName) ? "My Preset" : NewPresetName.Trim();

        var preset = new ExportPreset
        {
            Name = name,
            Resolution = SelectedResolution,
            Format = SelectedFormat,
            AspectRatio = SelectedAspectRatio,
            Quality = SelectedQuality,
            Fps = SelectedFps,
        };

        _presetManager.SaveExportPreset(preset);
        NewPresetName = string.Empty;

        LoadPresets();

        // Select the newly saved preset
        SelectedPreset = AvailablePresets.FirstOrDefault(p => p.Name == name);
    }

    [RelayCommand]
    private void DeletePreset()
    {
        if (SelectedPreset is null) return;

        _presetManager.DeleteExportPreset(SelectedPreset.Name);
        SelectedPreset = null;
        LoadPresets();
    }

    /// <summary>
    /// The window handle used to initialize file pickers. Set from code-behind.
    /// </summary>
    public IntPtr WindowHandle { get; set; }

    [RelayCommand]
    private async Task BrowseOutputPathAsync()
    {
        var picker = new FileSavePicker();
        picker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
        picker.SuggestedFileName = "export";

        switch (SelectedFormat)
        {
            case VideoFormat.MP4:
                picker.FileTypeChoices.Add("MP4 Video", [".mp4"]);
                break;
            case VideoFormat.GIF:
                picker.FileTypeChoices.Add("GIF Animation", [".gif"]);
                break;
            case VideoFormat.WebM:
                picker.FileTypeChoices.Add("WebM Video", [".webm"]);
                break;
        }

        if (WindowHandle != IntPtr.Zero)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        }

        var file = await picker.PickSaveFileAsync();
        if (file is not null)
        {
            OutputPath = file.Path;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        if (string.IsNullOrWhiteSpace(OutputPath) || CurrentProject is null) return;

        IsExporting = true;
        ExportSucceeded = false;
        ExportFailed = false;
        ExportedFilePath = string.Empty;
        ErrorMessage = string.Empty;
        ProgressPercent = 0;
        ProgressStatus = "Starting export…";
        EstimatedTimeRemaining = string.Empty;

        _exportCts = new CancellationTokenSource();

        var exportProgress = new Progress<ExportProgress>(p =>
        {
            ProgressPercent = p.PercentComplete;
            EstimatedTimeRemaining = FormatTimeSpan(p.EstimatedRemaining);
            ProgressStatus = $"{EstimatedTimeRemaining} remaining";
        });

        // Apply audio mute state: temporarily filter muted tracks
        var timeline = ProjectService.Instance.CurrentTimeline;
        var originalAudioPaths = CurrentProject.AudioFilePaths;
        if (timeline is not null)
        {
            CurrentProject.AudioFilePaths = originalAudioPaths
                .Where(p =>
                {
                    var fn = Path.GetFileName(p);
                    if (timeline.IsSystemAudioMuted
                        && fn.StartsWith("system_", StringComparison.OrdinalIgnoreCase))
                        return false;
                    if (timeline.IsMicAudioMuted
                        && fn.StartsWith("mic_", StringComparison.OrdinalIgnoreCase))
                        return false;
                    return true;
                })
                .ToList();
        }

        try
        {
            var settings = new ExportSettings
            {
                Resolution = SelectedResolution,
                Format = SelectedFormat,
                AspectRatio = SelectedAspectRatio,
                Quality = SelectedQuality,
                Fps = SelectedFps,
            };

            string outputFolder = Path.GetDirectoryName(OutputPath) ?? OutputPath;

            string exportedPath = await _exportEngine.ExportProjectAsync(
                CurrentProject,
                settings,
                CompositionConfig,
                outputFolder,
                timeline: timeline,
                progress: exportProgress,
                ct: _exportCts.Token);

            ExportedFilePath = exportedPath;
            ExportSucceeded = true;
            ProgressStatus = "Export complete!";
            ProgressPercent = 100;

            // Clean up raw frames to reclaim disk space.
            // The exported file is self-contained; .frames/ is no longer needed.
            if (CurrentProject is not null)
            {
                var sessionDir = Path.GetDirectoryName(CurrentProject.VideoFilePath);
                if (sessionDir is not null)
                {
                    SessionCleanupService.MarkSessionExported(sessionDir);
                    _ = Task.Run(() => SessionCleanupService.CleanupSession(sessionDir));
                }
            }
        }
        catch (OperationCanceledException)
        {
            ProgressStatus = "Export cancelled.";
        }
        catch (Exception ex)
        {
            ExportFailed = true;
            ErrorMessage = ex.Message;
            ProgressStatus = $"Export failed: {ex.Message}";
        }
        finally
        {
            // Restore original audio paths
            CurrentProject.AudioFilePaths = originalAudioPaths;
            IsExporting = false;
            _exportCts?.Dispose();
            _exportCts = null;
            ExportCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanExport() =>
        !IsExporting && !string.IsNullOrWhiteSpace(OutputPath) && CurrentProject is not null;

    [RelayCommand]
    private void CancelExport()
    {
        _exportCts?.Cancel();
    }

    // --- Helpers ---

    [RelayCommand]
    private void OpenOutputFolder()
    {
        if (string.IsNullOrWhiteSpace(ExportedFilePath) || !File.Exists(ExportedFilePath))
            return;

        // Open folder in Explorer with the exported file selected
        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{ExportedFilePath}\"");
    }

    private void ApplyPreset(ExportPreset preset)
    {
        SelectedResolution = preset.Resolution;
        SelectedFormat = preset.Format;
        SelectedAspectRatio = preset.AspectRatio;
        SelectedQuality = preset.Quality;
        SelectedFps = preset.Fps;
    }

    partial void OnOutputPathChanged(string value)
    {
        ExportCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsExportingChanged(bool value)
    {
        ExportCommand.NotifyCanExecuteChanged();
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{ts.Hours}h {ts.Minutes}m {ts.Seconds}s";
        if (ts.TotalMinutes >= 1)
            return $"{ts.Minutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }
}
