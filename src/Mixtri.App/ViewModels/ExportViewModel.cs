using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mixtri.Core.Export;
using Mixtri.Core.Models;
using Mixtri.Core.Processing;
using Mixtri.Core.Projects;
using Mixtri.Core.Services;
using Mixtri.Core.Settings;
using Mixtri_App.Services;
using Windows.Storage.Pickers;

namespace Mixtri_App.ViewModels;

public partial class ExportViewModel : ObservableObject
{
    private readonly PresetManager _presetManager;
    private readonly ExportWorkerClient _exportWorker;
    private CancellationTokenSource? _exportCts;
    private readonly ExportEtaEstimator _eta = new();
    private readonly System.Diagnostics.Stopwatch _progressClock = new();
    // True once the user picked OutputPath in the save picker; an auto-generated path is
    // regenerated (with the right extension) when the format changes, a chosen one is not.
    private bool _outputPathChosenByUser;
    private bool _settingOutputPath;

    /// <summary>
    /// Formats the exporter really writes. WebM is not offered: the Windows media APIs
    /// cannot write it and the engine would silently produce an MP4 instead.
    /// </summary>
    public static IReadOnlyList<VideoFormat> SupportedFormats { get; } = [VideoFormat.MP4, VideoFormat.GIF];

    /// <summary>
    /// Frame rates the exporter really renders (it clamps to <see cref="ExportEngine.MaxSupportedFps"/>).
    /// 60 fps is not offered because it would silently export at 30.
    /// </summary>
    public static IReadOnlyList<int> SupportedFps { get; } = [ExportEngine.MaxSupportedFps];

    public void SetPreviewPlayback(bool playing) => _exportWorker.SetPlaybackActive(playing);

    public void ReportPreviewFrame(TimeSpan elapsed) => _exportWorker.ReportPreviewFrame(elapsed);

    public ExportViewModel()
    {
        _presetManager = new PresetManager();
        _exportWorker = new ExportWorkerClient(
            Environment.ProcessPath ?? throw new InvalidOperationException("The app executable path is unavailable."));

        // Seed defaults from AppSettings so the user's preferred export
        // resolution/quality are applied to new export sessions.
        _selectedResolution = AppSettings.Instance.DefaultExportResolution;
        _selectedQuality = AppSettings.Instance.DefaultExportQuality;

        // Pull current state from the shared ProjectService
        CurrentProject = ProjectService.Instance.CurrentProject;
        CompositionConfig = ProjectService.Instance.CurrentComposition;
        PrefillOutputPath();

        ProjectService.Instance.ProjectChanged += OnProjectChanged;
    }

    private void OnProjectChanged(object? sender, EventArgs e)
    {
        if (!IsExporting) PrepareForExport();
    }

    /// <summary>
    /// Unsubscribes from singleton event sources to prevent this VM from
    /// being kept alive after the page is unloaded.
    /// </summary>
    public void Cleanup()
    {
        ProjectService.Instance.ProjectChanged -= OnProjectChanged;
        _exportCts?.Cancel();
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
        ExportWarningText = string.Empty;
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

        string mixtriDir = Mixtri.Core.AppDataPaths.VideosFolder;
        Directory.CreateDirectory(mixtriDir);

        string safeName = SanitizeFileName(CurrentProject.Name);
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = $"recording_{DateTime.Now:yyyyMMdd_HHmmss}";

        string extension = GetExtension(SelectedFormat);
        string path = Path.Combine(mixtriDir, $"{safeName}{extension}");

        // Ensure unique filename
        int counter = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(mixtriDir, $"{safeName} ({counter}){extension}");
            counter++;
        }

        SetOutputPath(path, chosenByUser: false);
    }

    private void SetOutputPath(string path, bool chosenByUser)
    {
        _settingOutputPath = true;
        try { OutputPath = path; }
        finally { _settingOutputPath = false; }
        _outputPathChosenByUser = chosenByUser;
    }

    private static string GetExtension(VideoFormat format) =>
        ExportEngine.GetEffectiveFormat(format) == VideoFormat.GIF ? ".gif" : ".mp4";

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
        UpdateGifEstimate();
    }

    // --- Export settings ---

    [ObservableProperty]
    private VideoResolution _selectedResolution = VideoResolution.HD1080;

    [ObservableProperty]
    private VideoFormat _selectedFormat = VideoFormat.MP4;

    // No aspect-ratio option: the export renders at the project's aspect ratio, which is
    // set (and previewed) in the editor. A per-export override was never honoured.

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

    /// <summary>
    /// Non-fatal problems of a successful export, e.g. "Exported, but audio from mic.wav
    /// could not be included." Empty when the export was clean.
    /// </summary>
    [ObservableProperty]
    private string _exportWarningText = string.Empty;

    /// <summary>True from the moment Cancel is pressed until the export has stopped.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyCanExecuteChangedFor(nameof(CancelExportCommand))]
    private bool _isCancelling;

    /// <summary>Whether the Cancel button should be enabled.</summary>
    public bool CanCancel => IsExporting && !IsCancelling;

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
        UpdateGifEstimate();
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

    partial void OnSelectedFormatChanged(VideoFormat value)
    {
        // Coerce formats the exporter cannot write (e.g. WebM from an old preset).
        if (!SupportedFormats.Contains(value))
        {
            SelectedFormat = ExportEngine.GetEffectiveFormat(value);
            return;
        }

        OnPropertyChanged(nameof(IsFormatMP4));
        OnPropertyChanged(nameof(IsFormatGIF));
        UpdateGifEstimate();

        // Keep the auto-generated file name's extension in step with the format.
        if (!_outputPathChosenByUser && !IsExporting && CurrentProject is not null)
            PrefillOutputPath();
    }

    /// <summary>
    /// Warning shown when GIF is selected. GIF keeps the chosen export resolution and
    /// frame rate — the encoder's palette quantization is the cost, and it scales with
    /// pixel count — so the user is told up front roughly how long it will take instead
    /// of discovering it from a stalled-looking progress ring.
    /// </summary>
    [ObservableProperty]
    private string _gifWarningText = string.Empty;

    /// <summary>
    /// Recomputes the GIF warning. Called when the options UI is shown, because reopening
    /// the flyout re-assigns the same project instance and therefore raises no change
    /// notification of its own.
    /// </summary>
    public void RefreshGifEstimate() => UpdateGifEstimate();

    private void UpdateGifEstimate()
    {
        if (SelectedFormat != VideoFormat.GIF || CurrentProject is null)
        {
            GifWarningText = string.Empty;
            return;
        }

        int sourceWidth = CurrentProject.Width > 0 ? CurrentProject.Width : 1920;
        int sourceHeight = CurrentProject.Height > 0 ? CurrentProject.Height : 1080;

        // Match the exporter: it composites at the project's aspect ratio and derives its
        // frame count from the edited timeline, not from the raw recording.
        var (gifWidth, gifHeight) = GifExportEstimator.ResolveOutputSize(
            sourceWidth, sourceHeight, CurrentProject.AspectRatio, SelectedResolution);

        // The exporter caps compositor/encode fps, so estimate against the same value.
        int effectiveFps = Math.Min(SelectedFps, ExportEngine.MaxSupportedFps);

        var duration = GifExportEstimator.ResolveExportedDuration(
            ProjectService.Instance.CurrentTimeline, CurrentProject.Duration);

        var estimate = GifExportEstimator.Estimate(gifWidth, gifHeight, effectiveFps, duration);

        string formatted = GifExportEstimator.FormatEstimate(estimate);

        GifWarningText = string.IsNullOrEmpty(formatted)
            ? "GIF has no audio and uses a 256-colour palette."
            : $"GIF renders at {gifWidth}×{gifHeight}, {effectiveFps} fps — this takes {formatted}. " +
              "GIF has no audio and uses a 256-colour palette. Choose a lower export resolution to speed it up.";
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

    partial void OnSelectedFpsChanged(int value)
    {
        // Coerce frame rates the exporter does not render (e.g. 60 from an old preset).
        if (!SupportedFps.Contains(value))
        {
            SelectedFps = ExportEngine.MaxSupportedFps;
            return;
        }

        OnPropertyChanged(nameof(IsFps30));
        UpdateGifEstimate();
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
        string suggested = SanitizeFileName(CurrentProject?.Name ?? string.Empty);
        picker.SuggestedFileName = string.IsNullOrWhiteSpace(suggested) ? "export" : suggested;

        if (ExportEngine.GetEffectiveFormat(SelectedFormat) == VideoFormat.GIF)
            picker.FileTypeChoices.Add("GIF Animation", [".gif"]);
        else
            picker.FileTypeChoices.Add("MP4 Video", [".mp4"]);

        if (WindowHandle != IntPtr.Zero)
        {
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        }

        var file = await picker.PickSaveFileAsync();
        if (file is not null)
        {
            // The picker already confirmed any overwrite; the export writes this exact file.
            SetOutputPath(file.Path, chosenByUser: true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        var project = CurrentProject;
        if (string.IsNullOrWhiteSpace(OutputPath) || project is null) return;

        IsCancelling = false;
        IsExporting = true;
        ExportSucceeded = false;
        ExportFailed = false;
        ExportedFilePath = string.Empty;
        ErrorMessage = string.Empty;
        ExportWarningText = string.Empty;
        ProgressPercent = 0;
        ProgressStatus = "Starting export…";
        EstimatedTimeRemaining = string.Empty;

        _exportCts = new CancellationTokenSource();
        _eta.Reset();
        _progressClock.Restart();

        var exportProgress = new Progress<ExportProgress>(OnExportProgress);

        // Audio mute/volume is applied by ExportAudioPlan, which is the one place placements
        // are built. This used to temporarily strip muted files from Project.AudioFilePaths
        // instead — a filter BuildFromSegments never sees, because it reads each segment's
        // own AudioFilePaths, so muting a track had no effect on any edited project's export.
        var timeline = ProjectService.Instance.CurrentTimeline;

        try
        {
            var settings = new ExportSettings
            {
                Resolution = SelectedResolution,
                Format = SelectedFormat,
                Quality = SelectedQuality,
                Fps = SelectedFps,
            };

            // The full path is passed through: the worker writes exactly this file
            // (ExportEngine.ResolveOutputPath), the folder is only the fallback location.
            string outputFolder = Path.GetDirectoryName(OutputPath) ?? OutputPath;

            var snapshot = ProjectStateSnapshot.Capture(
                project, ProjectService.Instance.CurrentComposition, timeline);
            var result = await _exportWorker.ExportWithResultAsync(
                snapshot,
                settings,
                outputFolder,
                OutputPath,
                exportProgress,
                _exportCts.Token);

            ExportedFilePath = result.OutputPath;
            ExportSucceeded = true;
            ExportWarningText = FormatWarnings(result.Warnings);
            ProgressStatus = ExportWarningText.Length > 0 ? ExportWarningText : "Export complete!";
            ProgressPercent = 100;

            // Readers in the editor may still own these files. Mark for the next
            // startup cleanup, never delete active preview media when export completes.
            var sessionDir = Path.GetDirectoryName(snapshot.Project.VideoFilePath);
            if (sessionDir is not null)
                SessionCleanupService.MarkSessionExported(sessionDir);
        }
        catch (OperationCanceledException)
        {
            EstimatedTimeRemaining = string.Empty;
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
            _progressClock.Stop();
            IsExporting = false;
            IsCancelling = false;
            _exportCts?.Dispose();
            _exportCts = null;
            ExportCommand.NotifyCanExecuteChanged();
        }
    }

    private void OnExportProgress(ExportProgress p)
    {
        // Once Cancel is pressed the bar and ETA freeze on "Cancelling…".
        if (!IsExporting || IsCancelling) return;
        // Reports arrive from several encoder threads; drop any that would move backwards.
        if (!_eta.TryAdd(_progressClock.Elapsed, p.PercentComplete, p.Status)) return;

        ProgressPercent = p.PercentComplete;
        var remaining = _eta.EstimateRemaining();
        if (remaining is null && p.EstimatedRemaining > TimeSpan.Zero && p.Status is null)
            remaining = p.EstimatedRemaining;
        EstimatedTimeRemaining = remaining is { } r ? FormatTimeSpan(r) : string.Empty;

        string phase = p.Status ?? "Exporting";
        ProgressStatus = EstimatedTimeRemaining.Length > 0
            ? (p.Status is null ? $"{EstimatedTimeRemaining} remaining" : $"{phase} — {EstimatedTimeRemaining} remaining")
            : (p.Status ?? "Estimating time remaining…");
    }

    /// <summary>Builds "Exported, but A and B." from the worker's warnings.</summary>
    internal static string FormatWarnings(IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0) return string.Empty;
        string joined = warnings.Count == 1
            ? warnings[0]
            : string.Join("; ", warnings.Take(warnings.Count - 1)) + " and " + warnings[^1];
        return $"Exported, but {joined}.";
    }

    private bool CanExport() =>
        !IsExporting && !string.IsNullOrWhiteSpace(OutputPath) && CurrentProject is not null;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelExport()
    {
        if (!CanCancel) return;
        IsCancelling = true;
        ProgressStatus = "Cancelling…";
        EstimatedTimeRemaining = string.Empty;
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
        SelectedQuality = preset.Quality;
        SelectedFps = preset.Fps;
    }

    partial void OnOutputPathChanged(string value)
    {
        // A path set from outside (code-behind/binding) is an explicit choice.
        if (!_settingOutputPath) _outputPathChosenByUser = true;
        ExportCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsExportingChanged(bool value)
    {
        ExportCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanCancel));
        CancelExportCommand.NotifyCanExecuteChanged();
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
