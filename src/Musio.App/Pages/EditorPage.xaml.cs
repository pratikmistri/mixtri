using System.ComponentModel;
using System.Globalization;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Musio.Core.Audio;
using Musio.Core.Capture;
using Musio.Core.Export;
using Musio.Core.Models;
using Musio.Core.Processing;
using Musio.Core.Settings;
using Musio.Core.Timeline;
using Musio_App.Services;
using Musio_App.ViewModels;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace Musio_App.Pages;

public sealed partial class EditorPage : Page
{
    public EditorViewModel ViewModel { get; }
    public ExportViewModel ExportVM { get; }
    private VideoFrameReader? _frameReader;
    private PreviewRenderer? _previewRenderer;
    private TimelineMapper? _timelineMapper;
    private AudioPlaybackEngine? _audioPlayer;
    private bool _compositorReady;

    // Thumbnail generation versioning — prevents stale results
    private int _thumbnailGenerationId;

    // Webcam overlay for editor preview
    private Windows.Media.Editing.MediaComposition? _webcamComposition;
    private int _webcamWidth;
    private int _webcamHeight;
    private CanvasBitmap? _lastWebcamFrame;
    private int _lastRenderedFrameIndex = -1;
    private bool _isRendering;
    private TimeSpan? _pendingRenderPosition;
    private bool _pendingRenderForce;
    private double _audioOffsetSeconds;

    // Background style editing state
    private DispatcherTimer? _styleDebounceTimer;
    private bool _suppressStyleEvents;
    private List<string>? _wallpaperPaths;

    // Cursor style editing state
    private DispatcherTimer? _cursorDebounceTimer;
    private bool _suppressCursorEvents;

    // Webcam overlay drag state
    private bool _webcamDragging;
    private Windows.Foundation.Point _webcamDragStart;
    private float _webcamNormX;
    private float _webcamNormY;
    private float _webcamNormSize;
    private float _webcamDragStartNormX;
    private float _webcamDragStartNormY;
    private bool _hasWebcamOverlay;

    public EditorPage()
    {
        ViewModel = new EditorViewModel();
        ExportVM = new ExportViewModel();
        InitializeComponent();

        Preview.Duration = GetMappedDuration();

        // Load frames and initialize compositor with cursor effects
        _ = InitializePreviewAsync();

        // Keep webcam overlay in sync with preview frame layout
        Preview.FrameLayoutChanged += (_, _) =>
        {
            if (_hasWebcamOverlay)
                UpdateWebcamOverlayPosition();
        };

        // Sync playhead: when timeline scrubs, update preview + audio
        Timeline.RegisterPropertyChangedCallback(
            Controls.TimelineControl.PlayheadPositionProperty,
            (_, _) =>
            {
                Preview.PlayheadPosition = Timeline.PlayheadPosition;
                ViewModel.Model.PlayheadPosition = Timeline.PlayheadPosition;
                _ = UpdatePreviewFrameAsync(Timeline.PlayheadPosition);
                // Play short audio burst at scrub position for editing feedback
                if (!Preview.IsPlaying)
                {
                    var audioPos = AudioPositionForVideo(Timeline.PlayheadPosition);
                    if (audioPos >= TimeSpan.Zero)
                        _audioPlayer?.ScrubTo(audioPos);
                }
            });

        // Sync playhead: when preview plays, update timeline
        Preview.PlaybackTick += (_, _) =>
        {
            Timeline.PlayheadPosition = Preview.PlayheadPosition;
            ViewModel.Model.PlayheadPosition = Preview.PlayheadPosition;
            _ = UpdatePreviewFrameAsync(Preview.PlayheadPosition);

            // Start audio when playhead reaches audio start point
            // (handles negative offset where audio started after video)
            if (_audioPlayer is not null && _audioPlayer.IsLoaded
                && Preview.IsPlaying && !_audioPlayer.IsPlaying)
            {
                var audioPos = AudioPositionForVideo(Preview.PlayheadPosition);
                if (audioPos >= TimeSpan.Zero)
                {
                    _audioPlayer.Seek(audioPos);
                    _audioPlayer.Play();
                }
            }
        };

        // Sync audio play/pause with preview
        Preview.IsPlayingChanged += (_, isPlaying) =>
        {
            if (_audioPlayer is null || !_audioPlayer.IsLoaded) return;
            if (isPlaying)
            {
                var audioPos = AudioPositionForVideo(Preview.PlayheadPosition);
                if (audioPos >= TimeSpan.Zero)
                {
                    _audioPlayer.Seek(audioPos);
                    _audioPlayer.Play();
                }
                // else: audio hasn't started yet; PlaybackTick will start it
            }
            else
            {
                _audioPlayer.Pause();
            }
        };

        // Re-seek audio when playback loops
        Preview.PlaybackLooped += (_, _) =>
        {
            if (_audioPlayer is null || !_audioPlayer.IsLoaded) return;
            var audioPos = AudioPositionForVideo(TimeSpan.Zero);
            if (audioPos >= TimeSpan.Zero)
            {
                _audioPlayer.Seek(audioPos);
                if (!_audioPlayer.IsPlaying)
                    _audioPlayer.Play();
            }
            else
            {
                // Audio starts later in the video — stop and let tick restart it
                _audioPlayer.Pause();
            }
        };

        ViewModel.UndoRedoManager.StateChanged += OnUndoRedoStateChanged;

        // Audio mute: toggle playback and sync mute state for export
        Timeline.SystemAudioMuteChanged += (_, isMuted) =>
        {
            ReloadAudioPlayer();
        };
        Timeline.MicAudioMuteChanged += (_, isMuted) =>
        {
            ReloadAudioPlayer();
        };

        ViewModel.ModelReloaded += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _timelineMapper = null;
                Timeline.ClearZoomSelection();
                Timeline.ClearClipSelection();
                UpdateSpeedPanelVisibility();
                Preview.Duration = GetMappedDuration();
                Timeline.Refresh();
                ViewModel.UndoRedoManager.StateChanged += OnUndoRedoStateChanged;
                _ = InitializePreviewAsync();
            });
        };

        // Zoom segment interaction events
        Timeline.ZoomSegmentSelected += OnZoomSegmentSelected;
        Timeline.ZoomSegmentMoved += OnZoomSegmentMoved;
        Timeline.ZoomSegmentResized += OnZoomSegmentResized;
        Timeline.ZoomSegmentCreated += OnZoomSegmentCreated;
        Timeline.ZoomSegmentRemoveRequested += OnZoomSegmentRemoveRequested;

        // Video clip selection events
        Timeline.VideoClipSelected += OnVideoClipSelected;

        // Export flyout state management
        ExportFlyout.Opened += ExportFlyout_Opened;
        ExportFlyout.Closed += ExportFlyout_Closed;
        ExportVM.PropertyChanged += ExportVM_PropertyChanged;

        // Clean up when page is unloaded to prevent leaks
        Unloaded += (_, _) =>
        {
            _styleDebounceTimer?.Stop();
            _styleDebounceTimer = null;
            _cursorDebounceTimer?.Stop();
            _cursorDebounceTimer = null;

            // Stop playback to halt timer ticks
            Preview.Pause();

            // Dispose owned resources
            _frameReader?.Dispose();
            _frameReader = null;
            _previewRenderer?.Dispose();
            _previewRenderer = null;
            _audioPlayer?.Dispose();
            _audioPlayer = null;
            _webcamComposition?.Clips.Clear();
            _webcamComposition = null;
            _lastWebcamFrame?.Dispose();
            _lastWebcamFrame = null;
            _compositorReady = false;
            _thumbnailGenerationId++; // cancel any in-flight thumbnail generation
            Timeline.ClearThumbnails();

            // Unsubscribe VMs from singleton event sources
            ViewModel.Cleanup();
            ExportVM.Cleanup();
        };
    }

    /// <summary>
    /// Pauses preview playback and audio. Called by App when the window
    /// becomes hidden (minimize-to-tray / system suspension).
    /// </summary>
    public void PausePlayback() => Preview.Pause();

    private async Task InitializePreviewAsync()
    {
        _frameReader?.Dispose();
        _previewRenderer?.Dispose();
        _audioPlayer?.Dispose();
        _styleDebounceTimer?.Stop();
        _frameReader = null;
        _previewRenderer = null;
        _audioPlayer = null;
        _compositorReady = false;
        _lastRenderedFrameIndex = -1;
        _thumbnailGenerationId++; // cancel any in-flight generation
        Timeline.ClearThumbnails();

        var project = ProjectService.Instance.CurrentProject;
        if (project is null || string.IsNullOrEmpty(project.VideoFilePath))
            return;

        int fps = project.Fps > 0 ? project.Fps : 30;
        int previewFps = Math.Min(fps, 30);
        Preview.PreviewFps = previewFps;

        _frameReader = VideoFrameReader.OpenFromVideoPath(project.VideoFilePath, fps);
        if (_frameReader is null)
            return;

        // Generate filmstrip thumbnails for timeline video track
        _ = GenerateTimelineThumbnailsAsync(_frameReader);

        // Load audio waveform data for timeline visualization
        await LoadAudioWaveformAsync(project);

        // Load mouse data for cursor smoothing + click animations
        MouseRecordingData? mouseData = null;
        if (!string.IsNullOrEmpty(project.CursorDataFilePath) && File.Exists(project.CursorDataFilePath))
        {
            try { mouseData = MouseHookRecorder.LoadFromFile(project.CursorDataFilePath); }
            catch { /* no cursor data — still show raw frames */ }
        }

        if (mouseData is null)
        {
            // No cursor data — just show raw frames
            Timeline.Refresh();
            _ = UpdatePreviewFrameAsync(TimeSpan.Zero);
            return;
        }

        // Feed cursor data to timeline for track visualization
        ViewModel.Model.CursorData = mouseData;
        ViewModel.Model.MouseToVideoOffsetSeconds = project.MouseToVideoOffsetSeconds;

        // Build zoom keyframes from click events so they appear on the timeline.
        // Subtract MouseToVideoOffsetSeconds to convert mouse-relative time to video time.
        int sourceW = project.Width > 0 ? project.Width : 1920;
        int sourceH = project.Height > 0 ? project.Height : 1080;
        // Mouse hook (WH_MOUSE_LL) in PerMonitorV2 reports physical pixels —
        // no DPI scaling needed for click coordinate transforms.
        float dpiScaleX = 1.0f;
        float dpiScaleY = 1.0f;
        int cropOffX = project.CropOffsetX;
        int cropOffY = project.CropOffsetY;
        double mouseOffset = project.MouseToVideoOffsetSeconds;
        ViewModel.Model.ZoomKeyframes.Clear();
        foreach (var click in mouseData.Clicks.Where(c => c.IsDown))
        {
            double clickTime = (click.TimestampTicks - mouseData.StartTimestampTicks) / mouseData.TickFrequency
                - mouseOffset;
            if (clickTime < 0) continue; // skip pre-roll clicks before video started
            ViewModel.Model.ZoomKeyframes.Add(new Musio.Core.Timeline.ZoomKeyframe
            {
                Timestamp = TimeSpan.FromSeconds(clickTime),
                ZoomLevel = 2.0,
                CenterX = (click.X * dpiScaleX - cropOffX) / sourceW,
                CenterY = (click.Y * dpiScaleY - cropOffY) / sourceH,
                SourceClickTicks = click.TimestampTicks,
            });
        }

        Timeline.Refresh();

        // Build composition config with cursor effects enabled
        var config = ProjectService.Instance.CurrentComposition ?? new CompositionConfig();
        config = config with
        {
            OutputFps = previewFps,
            SmoothingAlgorithm = SmoothingAlgorithm.SpringPhysics,
            SmoothingStrength = SmoothingStrength.UltraSmooth,
            Cursor = new CursorStyle
            {
                Scale = 3.0f,
                ClickAnimationEnabled = true,
                AutoHideEnabled = true,
                AutoHideDelaySeconds = 3.0f,
            },
            Zoom = new AutoZoomConfig { Enabled = true },
        };

        // Capture-type-specific style defaults (e.g. zeroed padding/shadow
        // for full-screen Monitor captures) are applied once at project load
        // time in ProjectService.SetProject. User edits via the Style menu
        // are preserved across editor re-navigation.

        // Auto-enable webcam overlay if the project has a webcam recording
        if (!string.IsNullOrWhiteSpace(project.WebcamFilePath) &&
            File.Exists(project.WebcamFilePath))
        {
            config = config with { WebcamStyle = config.WebcamStyle ?? new WebcamOverlayStyle() };
        }

        // Persist so the export pipeline uses the same config
        ProjectService.Instance.CurrentComposition = config;

        try
        {
            _previewRenderer = new PreviewRenderer();
            await _previewRenderer.InitializeAsync(
                mouseData, config,
                project.Width > 0 ? project.Width : 1920,
                project.Height > 0 ? project.Height : 1080,
                project.Duration,
                project.MouseToVideoOffsetSeconds,
                project.CropOffsetX,
                project.CropOffsetY,
                project.DpiScale);
            _compositorReady = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EditorPage] PreviewRenderer init failed: {ex.Message}");
            // Compositor init failed — fall back to raw frames
            _previewRenderer?.Dispose();
            _previewRenderer = null;
        }

        // Load webcam composition for preview overlay
        await LoadWebcamCompositionAsync(project);

        // Initialize webcam overlay editing
        InitializeWebcamOverlay(config);

        // Show style controls for Window and Region captures
        InitializeStyleControls(project, config);

        // Show cursor controls when cursor data is available
        InitializeCursorControls(project, config);

        // Aspect ratio + fit + crop anchor controls (always visible)
        InitializeAspectRatioControls();

        _ = UpdatePreviewFrameAsync(TimeSpan.Zero);
    }

    private async Task UpdatePreviewFrameAsync(TimeSpan position, bool force = false)
    {
        if (_frameReader is null) return;

        // Coalesce overlapping render requests: if a render is already in
        // flight, record the latest requested position and let the active
        // render pick it up when it finishes.
        if (_isRendering)
        {
            _pendingRenderPosition = position;
            _pendingRenderForce |= force;
            return;
        }

        _isRendering = true;
        try
        {
            // Drain loop: render the current request, then check if a newer
            // position arrived while we were rendering and handle it too.
            TimeSpan currentPos = position;
            bool currentForce = force;
            do
            {
                await RenderFrameAtAsync(currentPos, currentForce);

                // Consume any pending request that arrived during rendering
                if (_pendingRenderPosition.HasValue)
                {
                    currentPos = _pendingRenderPosition.Value;
                    currentForce = _pendingRenderForce;
                    _pendingRenderPosition = null;
                    _pendingRenderForce = false;
                }
                else
                {
                    break;
                }
            } while (true);
        }
        finally
        {
            _isRendering = false;
        }
    }

    private async Task RenderFrameAtAsync(TimeSpan position, bool force)
    {
        if (_frameReader is null) return;

        // Map output (playhead) time to source time, accounting for speed/cut/trim edits
        TimeSpan sourcePosition = MapToSourceTime(position);

        int frameIndex = _frameReader.GetFrameIndex(sourcePosition);
        if (!force && frameIndex == _lastRenderedFrameIndex) return;

        var bitmap = await _frameReader.LoadFrameAtTimeAsync(sourcePosition);
        if (bitmap is null) return;

        try
        {
            if (_compositorReady && _previewRenderer is not null && !_zoomRegionEditMode)
            {
                // Extract webcam frame for overlay
                await SetWebcamFrameForPreviewAsync(sourcePosition);

                var composed = _previewRenderer.RenderPreviewFrame(bitmap, sourcePosition);
                bitmap.Dispose();

                if (composed is not null)
                {
                    _lastRenderedFrameIndex = frameIndex;
                    Preview.SetFrame(composed);
                    return;
                }
            }

            // Fallback: show raw frame without effects
            var device = CanvasDevice.GetSharedDevice();
            var renderTarget = new CanvasRenderTarget(device,
                bitmap.SizeInPixels.Width, bitmap.SizeInPixels.Height, 96);
            using (var ds = renderTarget.CreateDrawingSession())
            {
                ds.DrawImage(bitmap);
            }
            bitmap.Dispose();
            _lastRenderedFrameIndex = frameIndex;
            Preview.SetFrame(renderTarget);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[EditorPage] Preview frame error at {position}: {ex.Message}");
            bitmap.Dispose();
        }
    }

    private async Task LoadWebcamCompositionAsync(Project project)
    {
        // Clear previous webcam state so stale resources don't persist
        // when loading a new project or one without a webcam file.
        _webcamComposition?.Clips.Clear();
        _webcamComposition = null;
        _lastWebcamFrame?.Dispose();
        _lastWebcamFrame = null;

        if (string.IsNullOrWhiteSpace(project.WebcamFilePath) || !File.Exists(project.WebcamFilePath))
            return;

        try
        {
            var webcamFile = await Windows.Storage.StorageFile.GetFileFromPathAsync(project.WebcamFilePath);
            var webcamClip = await Windows.Media.Editing.MediaClip.CreateFromFileAsync(webcamFile);
            var props = webcamClip.GetVideoEncodingProperties();
            _webcamWidth = (int)props.Width;
            _webcamHeight = (int)props.Height;
            _webcamComposition = new Windows.Media.Editing.MediaComposition();
            _webcamComposition.Clips.Add(webcamClip);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[EditorPage] Failed to load webcam video: {ex.Message}");
        }
    }

    private async Task SetWebcamFrameForPreviewAsync(TimeSpan position)
    {
        if (_webcamComposition is null || _previewRenderer is null) return;

        CanvasBitmap? webcamFrame = null;
        try
        {
            var clamped = position;
            if (_webcamComposition.Duration > TimeSpan.Zero && position > _webcamComposition.Duration)
                clamped = _webcamComposition.Duration;

            // Cap extraction size for preview — full native resolution is
            // unnecessarily heavy for a ~300px overlay during editor scrubbing.
            float previewCap = (ProjectService.Instance.CurrentComposition?.WebcamStyle?.Size ?? 300f) * 1.5f;
            int extractW = _webcamWidth;
            int extractH = _webcamHeight;
            float minDim = Math.Min(_webcamWidth, _webcamHeight);
            if (minDim > previewCap)
            {
                float scale = previewCap / minDim;
                extractW = Math.Max((int)Math.Ceiling(_webcamWidth * scale), 1);
                extractH = Math.Max((int)Math.Ceiling(_webcamHeight * scale), 1);
            }

            var thumbnail = await _webcamComposition.GetThumbnailAsync(
                clamped, extractW, extractH,
                Windows.Media.Editing.VideoFramePrecision.NearestFrame);

            var device = CanvasDevice.GetSharedDevice();
            var stream = thumbnail.AsStream();
            var ras = stream.AsRandomAccessStream();
            webcamFrame = await CanvasBitmap.LoadAsync(device, ras);

            // Dispose intermediate streams — ignore errors from WinRT stream flush
            try { ras.Dispose(); } catch { }
            try { stream.Dispose(); } catch { }
            try { thumbnail.Dispose(); } catch { }
        }
        catch { /* frame extraction failed — keep previous frame */ }

        if (webcamFrame is not null)
        {
            _lastWebcamFrame?.Dispose();
            _lastWebcamFrame = webcamFrame;
            _previewRenderer.SetWebcamFrame(_lastWebcamFrame);
        }
    }

    /// <summary>
    /// Maps an output (playhead) time to source time using the current timeline mapper.
    /// Falls back to identity mapping when no speed/cut edits are present.
    /// </summary>
    private TimeSpan MapToSourceTime(TimeSpan outputPosition)
    {
        var mapper = EnsureTimelineMapper();
        if (mapper is null) return outputPosition;

        int fps = _frameReader?.Fps ?? 30;
        int outputFrame = (int)(outputPosition.TotalSeconds * fps);
        double sourceSeconds = mapper.GetSourceTimeForOutputFrame(outputFrame);
        return TimeSpan.FromSeconds(sourceSeconds);
    }

    /// <summary>
    /// Generates pre-scaled thumbnails for the timeline video track filmstrip.
    /// Uses versioning to cancel stale generation when the project changes.
    /// </summary>
    private async Task GenerateTimelineThumbnailsAsync(VideoFrameReader reader)
    {
        var generationId = ++_thumbnailGenerationId;

        // Load first frame to determine aspect ratio
        var firstFrame = await reader.LoadFrameAsync(0);
        if (firstFrame is null || generationId != _thumbnailGenerationId)
        {
            firstFrame?.Dispose();
            return;
        }

        double aspectRatio = (double)firstFrame.SizeInPixels.Width / firstFrame.SizeInPixels.Height;
        double totalSeconds = reader.Duration.TotalSeconds;
        if (totalSeconds <= 0)
        {
            firstFrame.Dispose();
            return;
        }

        // Thumbnail size: match video track height (60px row minus padding)
        const int thumbH = 52;
        int thumbW = Math.Max(1, (int)(thumbH * aspectRatio));

        // Determine interval: aim for a reasonable density, cap total count
        double interval = Math.Max(0.5, totalSeconds / 200);
        int count = Math.Min(300, (int)(totalSeconds / interval) + 1);

        var device = CanvasDevice.GetSharedDevice();
        var thumbnails = new CanvasBitmap[count];

        for (int i = 0; i < count; i++)
        {
            if (generationId != _thumbnailGenerationId)
            {
                // Generation cancelled — clean up
                foreach (var t in thumbnails) t?.Dispose();
                firstFrame.Dispose();
                return;
            }

            CanvasBitmap? frame;
            if (i == 0)
            {
                frame = firstFrame;
            }
            else
            {
                double time = i * interval;
                frame = await reader.LoadFrameAtTimeAsync(TimeSpan.FromSeconds(time));
            }

            if (frame is null) continue;

            try
            {
                // Scale down to thumbnail size
                var renderTarget = new CanvasRenderTarget(device, thumbW, thumbH, 96);
                using (var session = renderTarget.CreateDrawingSession())
                {
                    session.DrawImage(frame,
                        new Rect(0, 0, thumbW, thumbH),
                        new Rect(0, 0, frame.SizeInPixels.Width, frame.SizeInPixels.Height));
                }
                thumbnails[i] = renderTarget;
            }
            finally
            {
                if (i > 0) frame.Dispose();
            }
        }

        firstFrame.Dispose();

        if (generationId != _thumbnailGenerationId)
        {
            foreach (var t in thumbnails) t?.Dispose();
            return;
        }

        // TimelineControl takes ownership of the bitmaps
        Timeline.SetThumbnails(thumbnails, interval, aspectRatio);
    }

    private TimelineMapper? EnsureTimelineMapper()
    {
        if (_timelineMapper is not null) return _timelineMapper;

        int fps = _frameReader?.Fps ?? ProjectService.Instance.CurrentProject?.Fps ?? 30;
        if (fps <= 0) fps = 30;

        _timelineMapper = new TimelineMapper(ViewModel.Model, fps);
        return _timelineMapper;
    }

    /// <summary>
    /// Returns the effective preview duration, accounting for speed segments.
    /// </summary>
    private TimeSpan GetMappedDuration()
    {
        var mapper = EnsureTimelineMapper();
        return mapper?.EffectiveDuration ?? ViewModel.Model.EffectiveDuration;
    }

    /// <summary>
    /// Invalidates the preview state after timeline edits (speed, zoom, cut, undo/redo).
    /// Rebuilds the timeline mapper, syncs zoom keyframes to the compositor, and forces a re-render.
    /// </summary>
    private void InvalidatePreview()
    {
        _timelineMapper = null;
        _lastRenderedFrameIndex = -1;

        Preview.Duration = GetMappedDuration();

        // Sync user-added zoom keyframes to the compositor
        if (_compositorReady && _previewRenderer is not null)
        {
            var manualKeyframes = ViewModel.Model.ZoomKeyframes
                .Where(k => k.IsManual)
                .ToList();
            _previewRenderer.UpdateZoomKeyframes(manualKeyframes);
            _previewRenderer.UpdateSuppressedClickTicks(ViewModel.Model.SuppressedClickTicks);
        }

        Timeline.Refresh();
        _ = UpdatePreviewFrameAsync(ViewModel.Model.PlayheadPosition, force: true);
    }

    private void OnUndoRedoStateChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_zoomRegionEditMode)
                ExitZoomRegionEditMode();

            // Only clear zoom selection if the selected segment was removed
            if (Timeline.SelectedZoomKeyframeId is { } id &&
                !ViewModel.Model.ZoomKeyframes.Any(k => k.Id == id))
            {
                Timeline.ClearZoomSelection();
            }

            Timeline.ClearClipSelection();
            UpdateZoomPanelVisibility();
            UpdateSpeedPanelVisibility();
            InvalidatePreview();
        });
    }

    private bool _suppressSpeedApply;

    private void OnVideoClipSelected(object? sender, int? clipIndex)
    {
        ViewModel.SelectedClipIndex = clipIndex;
        UpdateSpeedPanelVisibility();

        // Sync combo to match selected clip's current speed
        if (clipIndex is { } idx && idx >= 0 && idx < ViewModel.Model.Clips.Count)
        {
            var clip = ViewModel.Model.Clips[idx];
            _suppressSpeedApply = true;
            for (int i = 0; i < SpeedComboBox.Items.Count; i++)
            {
                if (SpeedComboBox.Items[i] is ComboBoxItem item &&
                    double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out double s) &&
                    Math.Abs(s - clip.SpeedFactor) < 0.01)
                {
                    SpeedComboBox.SelectedIndex = i;
                    break;
                }
            }
            _suppressSpeedApply = false;
        }
        else
        {
            // Reset to 1x when deselected
            _suppressSpeedApply = true;
            SpeedComboBox.SelectedIndex = 2;
            _suppressSpeedApply = false;
        }
    }

    private void UpdateSpeedPanelVisibility()
    {
        if (SpeedComboBox is null) return;
        bool hasClipSelection = ViewModel.SelectedClipIndex is not null;
        SpeedComboBox.Visibility = hasClipSelection ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SpeedComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSpeedApply) return;
        if (SpeedComboBox.SelectedItem is ComboBoxItem item &&
            double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out double speed))
        {
            ViewModel.SelectedSpeed = speed;

            if (ViewModel.SelectedClipIndex is not null)
            {
                ViewModel.ApplySpeedCommand.Execute(null);
            }
        }
    }

    private void UndoAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.CanUndo)
        {
            ViewModel.UndoCommand.Execute(null);
            args.Handled = true;
        }
    }

    private void RedoAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.CanRedo)
        {
            ViewModel.RedoCommand.Execute(null);
            args.Handled = true;
        }
    }

    private void SplitAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.SplitAtPlayheadCommand.Execute(null);
        args.Handled = true;
    }

    private void DeleteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // If a zoom segment is selected, remove it instead of deleting a clip segment
        if (Timeline.SelectedZoomKeyframeId is { } selectedId)
        {
            var operation = new RemoveZoomKeyframeOperation(selectedId);
            ViewModel.UndoRedoManager.Execute(operation);
            Timeline.ClearZoomSelection();
            UpdateZoomPanelVisibility();
            args.Handled = true;
            return;
        }

        ViewModel.DeleteSelectedCommand.Execute(null);
        args.Handled = true;
    }

    private void CutAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ViewModel.CutSelectionCommand.Execute(null);
        args.Handled = true;
    }

    private void PlayPauseAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsFocusOnInteractiveControl())
        {
            return;
        }

        if (Preview.IsPlaying)
        {
            Preview.Pause();
        }
        else
        {
            Preview.Play();
        }
        args.Handled = true;
    }

    private bool IsFocusOnInteractiveControl()
    {
        DependencyObject? node = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (node is not null)
        {
            switch (node)
            {
                case TextBox:
                case PasswordBox:
                case RichEditBox:
                case AutoSuggestBox:
                case ComboBox:
                case NumberBox:
                case Microsoft.UI.Xaml.Controls.Primitives.ButtonBase:
                case ToggleSwitch:
                case Slider:
                case ColorPicker:
                case FlyoutPresenter:
                case MenuFlyoutPresenter:
                    return true;
            }
            node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    // --- Zoom Segment Handlers ---

    private bool _suppressZoomPropertyUpdate;

    private void OnZoomSegmentSelected(object? sender, string? segmentId)
    {
        if (_zoomRegionEditMode)
            ExitZoomRegionEditMode();

        UpdateZoomPanelVisibility();

        if (segmentId is not null)
        {
            var kf = ViewModel.Model.ZoomKeyframes.FirstOrDefault(k => k.Id == segmentId);
            if (kf is not null)
            {
                _suppressZoomPropertyUpdate = true;

                bool matched = false;
                for (int i = 0; i < ZoomLevelCombo.Items.Count; i++)
                {
                    if (ZoomLevelCombo.Items[i] is ComboBoxItem item &&
                        double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out double z) &&
                        Math.Abs(z - kf.ZoomLevel) < 0.01)
                    {
                        ZoomLevelCombo.SelectedIndex = i;
                        matched = true;
                        break;
                    }
                }
                if (!matched)
                {
                    ZoomLevelCombo.SelectedIndex = -1;
                    ZoomLevelCombo.Text = kf.ZoomLevel.ToString("0.##", CultureInfo.InvariantCulture) + "x";
                }

                _suppressZoomPropertyUpdate = false;
            }
        }
    }

    private void OnZoomSegmentMoved(object? sender, (string Id, TimeSpan NewTimestamp) e)
    {
        var operation = new MoveZoomKeyframeOperation(e.Id, e.NewTimestamp);
        ViewModel.UndoRedoManager.Execute(operation);
    }

    private void OnZoomSegmentResized(object? sender, (string Id, bool IsStartEdge, TimeSpan NewEdgeTime) e)
    {
        var operation = new ResizeZoomSegmentOperation(e.Id, e.IsStartEdge, e.NewEdgeTime);
        ViewModel.UndoRedoManager.Execute(operation);
    }

    private void OnZoomSegmentCreated(object? sender, (TimeSpan Start, TimeSpan End) e)
    {
        double zoomLevel = 2.0;
        if (ZoomLevelCombo.SelectedItem is ComboBoxItem item &&
            double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out double z))
            zoomLevel = z;

        // Use cursor position at segment midpoint as zoom center
        double cx = 0.5, cy = 0.5;
        var midpoint = e.Start + (e.End - e.Start) / 2;
        if (ViewModel.Model.CursorData is { } cursorData && cursorData.Samples.Count > 0)
        {
            var project = ProjectService.Instance.CurrentProject;
            int sourceW = project?.Width > 0 ? project.Width : 1920;
            int sourceH = project?.Height > 0 ? project.Height : 1080;
            // Mouse hook coords are already physical (PerMonitorV2) — no DPI scaling
            float dpiX = 1.0f;
            float dpiY = 1.0f;
            int cropOffX = project?.CropOffsetX ?? 0;
            int cropOffY = project?.CropOffsetY ?? 0;

            double mouseOffset = project?.MouseToVideoOffsetSeconds ?? 0;
            double targetTime = midpoint.TotalSeconds + mouseOffset;
            double tickFreq = cursorData.TickFrequency;
            long startTick = cursorData.StartTimestampTicks;
            Musio.Core.Models.MouseSample closest = cursorData.Samples[0];
            double bestDist = double.MaxValue;
            foreach (var s in cursorData.Samples)
            {
                double sTime = (s.TimestampTicks - startTick) / tickFreq;
                double dist = Math.Abs(sTime - targetTime);
                if (dist < bestDist) { bestDist = dist; closest = s; }
            }
            cx = (closest.X * dpiX - cropOffX) / sourceW;
            cy = (closest.Y * dpiY - cropOffY) / sourceH;
        }

        var operation = new AddZoomSegmentOperation(e.Start, e.End, zoomLevel,
            Math.Clamp(cx, 0, 1), Math.Clamp(cy, 0, 1));
        ViewModel.UndoRedoManager.Execute(operation);

        // Select the newly created segment and enter zoom region edit mode
        Timeline.SelectedZoomKeyframeId = operation.CreatedId;
        OnZoomSegmentSelected(this, operation.CreatedId);

        var createdKf = ViewModel.Model.ZoomKeyframes.FirstOrDefault(k => k.Id == operation.CreatedId);
        if (createdKf is not null)
            EnterZoomRegionEditMode(operation.CreatedId, createdKf);
    }

    private void OnZoomSegmentRemoveRequested(object? sender, string keyframeId)
    {
        var operation = new RemoveZoomKeyframeOperation(keyframeId);
        ViewModel.UndoRedoManager.Execute(operation);
        Timeline.ClearZoomSelection();
        UpdateZoomPanelVisibility();
    }

    private void RemoveZoomSegment_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (Timeline.SelectedZoomKeyframeId is { } selectedId)
        {
            var operation = new RemoveZoomKeyframeOperation(selectedId);
            ViewModel.UndoRedoManager.Execute(operation);
            Timeline.ClearZoomSelection();
            UpdateZoomPanelVisibility();
        }
    }

    private void ZoomLevelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressZoomPropertyUpdate) return;
        if (Timeline is null) return;
        if (Timeline.SelectedZoomKeyframeId is not { } selectedId) return;
        if (ZoomLevelCombo.SelectedItem is not ComboBoxItem item) return;
        if (!double.TryParse(item.Tag?.ToString(), CultureInfo.InvariantCulture, out double zoomLevel)) return;

        if (_zoomRegionEditMode)
        {
            // Update the overlay rectangle size without committing
            _zoomRegionZoomLevel = zoomLevel;
            UpdateZoomRegionRect();
            return;
        }

        var operation = new UpdateZoomSegmentPropertiesOperation(selectedId, zoomLevel: zoomLevel);
        ViewModel.UndoRedoManager.Execute(operation);
    }

    private void ZoomLevelCombo_TextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        if (Timeline is null || Timeline.SelectedZoomKeyframeId is not { } selectedId)
        {
            args.Handled = true;
            return;
        }

        string text = (args.Text ?? string.Empty).Trim().TrimEnd('x', 'X');
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double zoom))
        {
            // Restore display from current zoom level
            SyncZoomLevelComboToFreeform(_zoomRegionEditMode ? _zoomRegionZoomLevel : GetSelectedKeyframeZoom() ?? 2.0);
            args.Handled = true;
            return;
        }

        zoom = Math.Clamp(zoom, MinZoomLevel, MaxZoomLevel);

        if (_zoomRegionEditMode)
        {
            _zoomRegionZoomLevel = zoom;
            // Re-clamp center for new zoom
            (double halfW, double halfH) = GetNormalizedHalfExtents(zoom);
            _zoomRegionCenterX = Math.Clamp(_zoomRegionCenterX, halfW, 1.0 - halfW);
            _zoomRegionCenterY = Math.Clamp(_zoomRegionCenterY, halfH, 1.0 - halfH);
            UpdateZoomRegionRect();
        }
        else
        {
            var op = new UpdateZoomSegmentPropertiesOperation(selectedId, zoomLevel: zoom);
            ViewModel.UndoRedoManager.Execute(op);
        }

        SyncZoomLevelComboToFreeform(zoom);
        args.Handled = true;
    }

    private double? GetSelectedKeyframeZoom()
    {
        if (Timeline?.SelectedZoomKeyframeId is not { } id) return null;
        return ViewModel.Model.ZoomKeyframes.FirstOrDefault(k => k.Id == id)?.ZoomLevel;
    }

    // --- Zoom Region Edit Mode ---

    private bool _zoomRegionEditMode;
    private string? _zoomRegionKeyframeId;
    private double _zoomRegionCenterX, _zoomRegionCenterY;
    private double _zoomRegionZoomLevel;
    private int _zoomRegionSourceW, _zoomRegionSourceH;
    private double _frameDisplayX, _frameDisplayY, _frameDisplayW, _frameDisplayH;
    private bool _isDraggingZoomRegion;
    private Point _dragStartPoint;
    private double _dragStartCenterX, _dragStartCenterY;

    private enum ZoomDragMode { None, Move, ResizeTL, ResizeTR, ResizeBL, ResizeBR }
    private ZoomDragMode _zoomDragMode = ZoomDragMode.None;
    private const double HandleHitRadius = 10.0;
    private const double HandleSize = 10.0;
    private const double MinZoomLevel = 1.0;
    private const double MaxZoomLevel = 4.0;
    private const double CenterSnapThreshold = 0.02; // normalized (~2% of frame)
    private double _dragAnchorDispX, _dragAnchorDispY;
    private double _dragStartRectW, _dragStartRectH;
    private double _dragStartZoomLevel;

    private void EditZoomRegion_Click(object sender, RoutedEventArgs e)
    {
        if (Timeline.SelectedZoomKeyframeId is not { } selectedId) return;
        var kf = ViewModel.Model.ZoomKeyframes.FirstOrDefault(k => k.Id == selectedId);
        if (kf is null) return;
        EnterZoomRegionEditMode(selectedId, kf);
    }

    private void EnterZoomRegionEditMode(string keyframeId, ZoomKeyframe kf)
    {
        var project = ProjectService.Instance.CurrentProject;
        if (project is null) return;

        _zoomRegionEditMode = true;
        _zoomRegionKeyframeId = keyframeId;
        _zoomRegionCenterX = kf.CenterX;
        _zoomRegionCenterY = kf.CenterY;
        _zoomRegionZoomLevel = kf.ZoomLevel;
        _zoomRegionSourceW = project.Width > 0 ? project.Width : 1920;
        _zoomRegionSourceH = project.Height > 0 ? project.Height : 1080;

        // Pause playback and move to segment timestamp for context
        Preview.Pause();
        var pos = kf.Timestamp;
        Timeline.PlayheadPosition = pos;
        Preview.PlayheadPosition = pos;
        ViewModel.Model.PlayheadPosition = pos;

        // Re-render without compositor (raw frame for positioning)
        _ = UpdatePreviewFrameAsync(pos, force: true);

        ZoomRegionOverlay.Visibility = Visibility.Visible;
        UpdateZoomRegionRect();
    }

    private void ExitZoomRegionEditMode()
    {
        _zoomRegionEditMode = false;
        _isDraggingZoomRegion = false;
        _zoomDragMode = ZoomDragMode.None;
        _zoomRegionKeyframeId = null;
        ZoomRegionOverlay.Visibility = Visibility.Collapsed;
        UpdateSnapGuides(false, false);

        // Re-render with compositor
        _lastRenderedFrameIndex = -1;
        _ = UpdatePreviewFrameAsync(ViewModel.Model.PlayheadPosition, force: true);
    }

    private void UpdateZoomRegionRect()
    {
        if (!_zoomRegionEditMode) return;

        double canvasW = ZoomRegionCanvas.ActualWidth;
        double canvasH = ZoomRegionCanvas.ActualHeight;
        if (canvasW <= 0 || canvasH <= 0) return;

        // Compute raw frame display rect (aspect-fit centered)
        double scale = Math.Min(canvasW / _zoomRegionSourceW, canvasH / _zoomRegionSourceH);
        _frameDisplayW = _zoomRegionSourceW * scale;
        _frameDisplayH = _zoomRegionSourceH * scale;
        _frameDisplayX = (canvasW - _frameDisplayW) / 2;
        _frameDisplayY = (canvasH - _frameDisplayH) / 2;

        // Compute zoom viewport in source pixels
        double vpW = _zoomRegionSourceW / _zoomRegionZoomLevel;
        double vpH = _zoomRegionSourceH / _zoomRegionZoomLevel;

        // Adjust for output aspect ratio if set
        var config = ProjectService.Instance.CurrentComposition;
        if (config is not null && config.AspectRatio != AspectRatio.Auto)
        {
            double contentRatio = GetAspectRatioValue(config.AspectRatio);
            if (contentRatio > 0)
            {
                double vpRatio = vpW / vpH;
                if (vpRatio > contentRatio)
                    vpW = vpH * contentRatio;
                else
                    vpH = vpW / contentRatio;
            }
        }

        double vpX = _zoomRegionCenterX * _zoomRegionSourceW - vpW / 2;
        double vpY = _zoomRegionCenterY * _zoomRegionSourceH - vpH / 2;

        // Clamp viewport to source bounds
        vpX = Math.Clamp(vpX, 0, Math.Max(0, _zoomRegionSourceW - vpW));
        vpY = Math.Clamp(vpY, 0, Math.Max(0, _zoomRegionSourceH - vpH));

        // Map to display coordinates
        double rectX = _frameDisplayX + (vpX / _zoomRegionSourceW) * _frameDisplayW;
        double rectY = _frameDisplayY + (vpY / _zoomRegionSourceH) * _frameDisplayH;
        double rectW = (vpW / _zoomRegionSourceW) * _frameDisplayW;
        double rectH = (vpH / _zoomRegionSourceH) * _frameDisplayH;

        Canvas.SetLeft(ZoomRegionRect, rectX);
        Canvas.SetTop(ZoomRegionRect, rectY);
        ZoomRegionRect.Width = rectW;
        ZoomRegionRect.Height = rectH;

        // Position corner handles centered on each corner
        PositionHandle(HandleTL, rectX, rectY);
        PositionHandle(HandleTR, rectX + rectW, rectY);
        PositionHandle(HandleBL, rectX, rectY + rectH);
        PositionHandle(HandleBR, rectX + rectW, rectY + rectH);

        // Dim overlays constrained to frame display area
        double fX = _frameDisplayX, fY = _frameDisplayY;
        double fW = _frameDisplayW, fH = _frameDisplayH;

        Canvas.SetLeft(DimTop, fX);
        Canvas.SetTop(DimTop, fY);
        DimTop.Width = fW;
        DimTop.Height = Math.Max(0, rectY - fY);

        Canvas.SetLeft(DimBottom, fX);
        Canvas.SetTop(DimBottom, rectY + rectH);
        DimBottom.Width = fW;
        DimBottom.Height = Math.Max(0, (fY + fH) - (rectY + rectH));

        Canvas.SetLeft(DimLeft, fX);
        Canvas.SetTop(DimLeft, rectY);
        DimLeft.Width = Math.Max(0, rectX - fX);
        DimLeft.Height = rectH;

        Canvas.SetLeft(DimRight, rectX + rectW);
        Canvas.SetTop(DimRight, rectY);
        DimRight.Width = Math.Max(0, (fX + fW) - (rectX + rectW));
        DimRight.Height = rectH;
    }

    private static double GetAspectRatioValue(AspectRatio ratio) => ratio switch
    {
        AspectRatio.Landscape16x9 => 16.0 / 9.0,
        AspectRatio.Portrait9x16 => 9.0 / 16.0,
        AspectRatio.Square1x1 => 1.0,
        AspectRatio.Classic4x3 => 4.0 / 3.0,
        AspectRatio.Tall3x4 => 3.0 / 4.0,
        _ => -1.0,
    };

    private (double halfW, double halfH) GetNormalizedHalfExtents(double zoom)
    {
        double vpW = _zoomRegionSourceW / zoom;
        double vpH = _zoomRegionSourceH / zoom;

        var config = ProjectService.Instance.CurrentComposition;
        if (config is not null && config.AspectRatio != AspectRatio.Auto)
        {
            double contentRatio = GetAspectRatioValue(config.AspectRatio);
            if (contentRatio > 0)
            {
                double vpRatio = vpW / vpH;
                if (vpRatio > contentRatio) vpW = vpH * contentRatio;
                else vpH = vpW / contentRatio;
            }
        }
        return (vpW / _zoomRegionSourceW / 2.0, vpH / _zoomRegionSourceH / 2.0);
    }

    private static void PositionHandle(Microsoft.UI.Xaml.Shapes.Rectangle handle, double cornerX, double cornerY)
    {
        Canvas.SetLeft(handle, cornerX - HandleSize / 2);
        Canvas.SetTop(handle, cornerY - HandleSize / 2);
    }

    private ZoomDragMode HitTestCorners(Point p)
    {
        double rectX = Canvas.GetLeft(ZoomRegionRect);
        double rectY = Canvas.GetTop(ZoomRegionRect);
        double rectR = rectX + ZoomRegionRect.Width;
        double rectB = rectY + ZoomRegionRect.Height;

        if (IsNear(p, rectX, rectY)) return ZoomDragMode.ResizeTL;
        if (IsNear(p, rectR, rectY)) return ZoomDragMode.ResizeTR;
        if (IsNear(p, rectX, rectB)) return ZoomDragMode.ResizeBL;
        if (IsNear(p, rectR, rectB)) return ZoomDragMode.ResizeBR;
        return ZoomDragMode.None;

        static bool IsNear(Point pt, double cx, double cy)
            => System.Math.Abs(pt.X - cx) <= HandleHitRadius && System.Math.Abs(pt.Y - cy) <= HandleHitRadius;
    }

    private void ZoomRegionCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_zoomRegionEditMode) return;

        var point = e.GetCurrentPoint(ZoomRegionCanvas);
        double rectX = Canvas.GetLeft(ZoomRegionRect);
        double rectY = Canvas.GetTop(ZoomRegionRect);
        double rectR = rectX + ZoomRegionRect.Width;
        double rectB = rectY + ZoomRegionRect.Height;

        var corner = HitTestCorners(point.Position);
        if (corner != ZoomDragMode.None)
        {
            _zoomDragMode = corner;
            _isDraggingZoomRegion = true;
            _dragStartPoint = point.Position;
            _dragStartCenterX = _zoomRegionCenterX;
            _dragStartCenterY = _zoomRegionCenterY;
            _dragStartRectW = ZoomRegionRect.Width;
            _dragStartRectH = ZoomRegionRect.Height;
            _dragStartZoomLevel = _zoomRegionZoomLevel;
            // Anchor is the opposite corner in display coords
            _dragAnchorDispX = corner is ZoomDragMode.ResizeTL or ZoomDragMode.ResizeBL ? rectR : rectX;
            _dragAnchorDispY = corner is ZoomDragMode.ResizeTL or ZoomDragMode.ResizeTR ? rectB : rectY;
            ZoomRegionCanvas.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        if (point.Position.X >= rectX && point.Position.X <= rectR &&
            point.Position.Y >= rectY && point.Position.Y <= rectB)
        {
            _zoomDragMode = ZoomDragMode.Move;
            _isDraggingZoomRegion = true;
            _dragStartPoint = point.Position;
            _dragStartCenterX = _zoomRegionCenterX;
            _dragStartCenterY = _zoomRegionCenterY;
            ZoomRegionCanvas.CapturePointer(e.Pointer);
            e.Handled = true;
        }
    }

    private void ZoomRegionCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingZoomRegion) return;
        if (_frameDisplayW <= 0 || _frameDisplayH <= 0) return;

        var point = e.GetCurrentPoint(ZoomRegionCanvas);
        bool shift = (e.KeyModifiers & VirtualKeyModifiers.Shift) == VirtualKeyModifiers.Shift;

        if (_zoomDragMode == ZoomDragMode.Move)
        {
            double deltaX = point.Position.X - _dragStartPoint.X;
            double deltaY = point.Position.Y - _dragStartPoint.Y;

            double deltaNormX = deltaX / _frameDisplayW;
            double deltaNormY = deltaY / _frameDisplayH;

            (double halfW, double halfH) = GetNormalizedHalfExtents(_zoomRegionZoomLevel);

            double newCx = System.Math.Clamp(_dragStartCenterX + deltaNormX, halfW, 1.0 - halfW);
            double newCy = System.Math.Clamp(_dragStartCenterY + deltaNormY, halfH, 1.0 - halfH);

            bool snappedX = false, snappedY = false;
            if (!shift)
            {
                if (System.Math.Abs(newCx - 0.5) < CenterSnapThreshold) { newCx = 0.5; snappedX = true; }
                if (System.Math.Abs(newCy - 0.5) < CenterSnapThreshold) { newCy = 0.5; snappedY = true; }
            }

            _zoomRegionCenterX = newCx;
            _zoomRegionCenterY = newCy;
            UpdateZoomRegionRect();
            UpdateSnapGuides(snappedX, snappedY);
        }
        else if (_zoomDragMode is ZoomDragMode.ResizeTL or ZoomDragMode.ResizeTR or ZoomDragMode.ResizeBL or ZoomDragMode.ResizeBR)
        {
            double dispDx = point.Position.X - _dragAnchorDispX;
            double dispDy = point.Position.Y - _dragAnchorDispY;

            int signX = _zoomDragMode is ZoomDragMode.ResizeTR or ZoomDragMode.ResizeBR ? 1 : -1;
            int signY = _zoomDragMode is ZoomDragMode.ResizeBL or ZoomDragMode.ResizeBR ? 1 : -1;

            // Project pointer-from-anchor onto the rect's diagonal direction
            // (signX*W0, signY*H0). This makes resize feel natural in both
            // outward and inward directions while preserving aspect.
            double diagX = signX * _dragStartRectW;
            double diagY = signY * _dragStartRectH;
            double diagLenSq = diagX * diagX + diagY * diagY;
            if (diagLenSq <= 0) { e.Handled = true; return; }
            double projLen = (dispDx * diagX + dispDy * diagY) / System.Math.Sqrt(diagLenSq);
            double diagLen = System.Math.Sqrt(diagLenSq);
            double scale = projLen / diagLen;
            if (scale < 0.0001) scale = 0.0001;

            double newZoom = System.Math.Clamp(_dragStartZoomLevel / scale, MinZoomLevel, MaxZoomLevel);
            double effectiveScale = _dragStartZoomLevel / newZoom;

            double newRectW = _dragStartRectW * effectiveScale;
            double newRectH = _dragStartRectH * effectiveScale;

            double centerDispX = _dragAnchorDispX + signX * newRectW / 2.0;
            double centerDispY = _dragAnchorDispY + signY * newRectH / 2.0;

            double newCx = (centerDispX - _frameDisplayX) / _frameDisplayW;
            double newCy = (centerDispY - _frameDisplayY) / _frameDisplayH;

            (double rHalfW, double rHalfH) = GetNormalizedHalfExtents(newZoom);
            newCx = System.Math.Clamp(newCx, rHalfW, 1.0 - rHalfW);
            newCy = System.Math.Clamp(newCy, rHalfH, 1.0 - rHalfH);

            bool snappedX = false, snappedY = false;
            if (!shift)
            {
                if (System.Math.Abs(newCx - 0.5) < CenterSnapThreshold) { newCx = 0.5; snappedX = true; }
                if (System.Math.Abs(newCy - 0.5) < CenterSnapThreshold) { newCy = 0.5; snappedY = true; }
            }

            _zoomRegionZoomLevel = newZoom;
            _zoomRegionCenterX = newCx;
            _zoomRegionCenterY = newCy;
            UpdateZoomRegionRect();
            SyncZoomLevelComboToFreeform(newZoom);
            UpdateSnapGuides(snappedX, snappedY);
        }

        e.Handled = true;
    }

    private void ZoomRegionCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_isDraggingZoomRegion)
        {
            _isDraggingZoomRegion = false;
            _zoomDragMode = ZoomDragMode.None;
            ZoomRegionCanvas.ReleasePointerCapture(e.Pointer);
            UpdateSnapGuides(false, false);
            e.Handled = true;
        }
    }

    private void SyncZoomLevelComboToFreeform(double zoom)
    {
        string text = zoom.ToString("0.##", CultureInfo.InvariantCulture) + "x";
        if (ZoomLevelCombo.Text == text) return;
        _suppressZoomPropertyUpdate = true;
        try { ZoomLevelCombo.Text = text; }
        finally { _suppressZoomPropertyUpdate = false; }
    }

    private void UpdateSnapGuides(bool snappedX, bool snappedY)
    {
        if (snappedX)
        {
            double cx = _frameDisplayX + _frameDisplayW / 2.0;
            SnapGuideV.X1 = cx; SnapGuideV.X2 = cx;
            SnapGuideV.Y1 = _frameDisplayY; SnapGuideV.Y2 = _frameDisplayY + _frameDisplayH;
            SnapGuideV.Visibility = Visibility.Visible;
        }
        else
        {
            SnapGuideV.Visibility = Visibility.Collapsed;
        }

        if (snappedY)
        {
            double cy = _frameDisplayY + _frameDisplayH / 2.0;
            SnapGuideH.Y1 = cy; SnapGuideH.Y2 = cy;
            SnapGuideH.X1 = _frameDisplayX; SnapGuideH.X2 = _frameDisplayX + _frameDisplayW;
            SnapGuideH.Visibility = Visibility.Visible;
        }
        else
        {
            SnapGuideH.Visibility = Visibility.Collapsed;
        }
    }

    private void ZoomRegionCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_zoomRegionEditMode)
            UpdateZoomRegionRect();
    }

    private void ZoomRegionApply_Click(object sender, RoutedEventArgs e)
    {
        if (_zoomRegionKeyframeId is null) return;

        var operation = new UpdateZoomSegmentPropertiesOperation(
            _zoomRegionKeyframeId,
            zoomLevel: _zoomRegionZoomLevel,
            centerX: _zoomRegionCenterX,
            centerY: _zoomRegionCenterY);
        ViewModel.UndoRedoManager.Execute(operation);

        ExitZoomRegionEditMode();
    }

    private void ZoomRegionCancel_Click(object sender, RoutedEventArgs e)
    {
        ExitZoomRegionEditMode();
    }

    private void UpdateZoomPanelVisibility()
    {
        if (Timeline is null || ZoomSegmentPanel is null || ZoomHintText is null) return;
        bool hasSelection = Timeline.SelectedZoomKeyframeId is not null;
        ZoomSegmentPanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        ZoomHintText.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private static float GetDpiScale(int capturedDimension, bool isWidth = true)
    {
        try
        {
            int logical = GetSystemMetrics(isWidth ? 0 : 1); // SM_CXSCREEN=0, SM_CYSCREEN=1
            if (logical > 0 && capturedDimension > logical)
                return (float)capturedDimension / logical;
        }
        catch { }
        return 1.0f;
    }

    // --- Audio waveform loading ---

    private async Task LoadAudioWaveformAsync(Project project)
    {
        if (project.AudioFilePaths is not { Count: > 0 })
            return;

        const int targetSamples = 2000;

        try
        {
            var validPaths = project.AudioFilePaths.Where(File.Exists).ToList();
            if (validPaths.Count == 0) return;

            // Identify system vs mic files by naming convention
            var systemPath = validPaths.FirstOrDefault(p =>
                Path.GetFileName(p).StartsWith("system_", StringComparison.OrdinalIgnoreCase));
            var micPath = validPaths.FirstOrDefault(p =>
                Path.GetFileName(p).StartsWith("mic_", StringComparison.OrdinalIgnoreCase));

            double videoDuration = project.Duration.TotalSeconds;

            // Offset between audio and video start.
            // Positive: audio pre-roll (WAV starts before video frame 0).
            // Negative: audio started late (e.g. mic permission dialog delay).
            double audioOffset = project.AudioToVideoOffsetSeconds;

            var (systemWaveform, micWaveform) = await Task.Run(() =>
            {
                float[]? sysWf = null;
                float[]? micWf = null;

                double sysDuration = 0, micDuration = 0;
                if (systemPath is not null)
                {
                    try { using var p = new NAudio.Wave.AudioFileReader(systemPath); sysDuration = p.TotalTime.TotalSeconds; }
                    catch { }
                }
                if (micPath is not null)
                {
                    try { using var p = new NAudio.Wave.AudioFileReader(micPath); micDuration = p.TotalTime.TotalSeconds; }
                    catch { }
                }

                // Waveform alignment:
                // - skipSeconds: how much of the WAV to skip (pre-roll). 0 if offset <= 0.
                // - leadTime: silence at the start of the timeline before audio begins.
                //   Non-zero when audio started after video (negative offset).
                double skipSeconds = Math.Max(0, audioOffset);
                double leadTime = Math.Max(0, -audioOffset);

                sysWf = BuildAlignedWaveform(systemPath, sysDuration, skipSeconds, leadTime, videoDuration, targetSamples);
                micWf = BuildAlignedWaveform(micPath, micDuration, skipSeconds, leadTime, videoDuration, targetSamples);

                return (sysWf, micWf);
            });

            if (systemWaveform is { Length: > 0 })
                ViewModel.Model.SystemAudioWaveformSamples = systemWaveform;
            if (micWaveform is { Length: > 0 })
                ViewModel.Model.MicAudioWaveformSamples = micWaveform;

            // At video time T, the audio file position is T + audioOffset
            _audioOffsetSeconds = audioOffset;
            _audioPlayer?.Dispose();
            _audioPlayer = new AudioPlaybackEngine();
            _audioPlayer.Load(validPaths);
        }
        catch
        {
            // Audio waveform generation failed — editor still works without it
        }
    }

    /// <summary>
    /// Converts a video playhead position to the corresponding audio file position.
    /// Positive _audioOffsetSeconds = pre-roll (skip into WAV).
    /// Negative _audioOffsetSeconds = audio started late (silence before audio).
    /// Returns negative TimeSpan when video is before audio start — callers
    /// must treat negative results as silence (no audio to play).
    /// </summary>
    private TimeSpan AudioPositionForVideo(TimeSpan videoPosition)
    {
        return videoPosition + TimeSpan.FromSeconds(_audioOffsetSeconds);
    }

    /// <summary>
    /// Builds a waveform array aligned to the video timeline, handling both
    /// pre-roll (positive offset → skip WAV start) and late audio (negative
    /// offset → leading silence on the timeline).
    /// </summary>
    private static float[]? BuildAlignedWaveform(
        string? path, double fileDuration,
        double skipSeconds, double leadTime,
        double videoDuration, int targetSamples)
    {
        if (path is null || fileDuration <= 0) return null;

        try
        {
            // How much of the WAV maps to the video timeline
            double audioForVideo = Math.Max(0, fileDuration - skipSeconds);
            // How much of the video timeline this audio covers
            double coverageDuration = Math.Min(audioForVideo, videoDuration - leadTime);
            if (coverageDuration <= 0) return null;

            int leadPeaks = (int)(targetSamples * leadTime / videoDuration);
            int audioPeaks = (int)Math.Min(
                targetSamples - leadPeaks,
                Math.Ceiling(targetSamples * coverageDuration / videoDuration));
            if (audioPeaks < 1) audioPeaks = 1;

            var raw = AudioWaveformGenerator.GenerateWaveform(
                path, audioPeaks, startSeconds: skipSeconds);
            var wf = new float[targetSamples];
            Array.Copy(raw, 0, wf, leadPeaks, Math.Min(raw.Length, audioPeaks));
            return wf;
        }
        catch { return null; }
    }

    // --- Audio mute support ---

    /// <summary>
    /// Reloads the audio player with only the unmuted audio tracks.
    /// </summary>
    private void ReloadAudioPlayer()
    {
        var project = ProjectService.Instance.CurrentProject;
        if (project is null) return;

        _audioPlayer?.Dispose();
        _audioPlayer = new AudioPlaybackEngine();

        var paths = GetUnmutedAudioPaths(project);
        if (paths.Count > 0)
            _audioPlayer.Load(paths);
    }

    /// <summary>
    /// Returns audio file paths filtered by current mute state.
    /// </summary>
    private List<string> GetUnmutedAudioPaths(Project project)
    {
        var model = ViewModel.Model;
        var paths = new List<string>();
        if (project.AudioFilePaths is null) return paths;

        foreach (var path in project.AudioFilePaths)
        {
            if (!File.Exists(path)) continue;
            var fileName = Path.GetFileName(path);
            if (model.IsSystemAudioMuted
                && fileName.StartsWith("system_", StringComparison.OrdinalIgnoreCase))
                continue;
            if (model.IsMicAudioMuted
                && fileName.StartsWith("mic_", StringComparison.OrdinalIgnoreCase))
                continue;
            paths.Add(path);
        }
        return paths;
    }

    // --- Export flyout ---

    private async void ExportFlyout_Opened(object? sender, object e)
    {
        if (ExportVM.IsExporting)
        {
            ShowExportingState();
            return;
        }

        if (ExportVM.ExportSucceeded)
        {
            ShowExportedState();
            return;
        }

        if (ExportVM.ExportFailed)
        {
            ShowErrorState();
            return;
        }

        // Pause preview playback before export. The export pipeline composites
        // frames on the shared Win2D device; running it concurrently with the
        // preview's per-frame composition can corrupt output frames and crash
        // the encoder when both pull from the same source files at once.
        Preview.Pause();
        try { _audioPlayer?.Pause(); } catch { /* best-effort */ }

        // Start new export
        ExportVM.PrepareForExport();
        if (!ExportVM.ExportCommand.CanExecute(null))
        {
            ExportFlyout.Hide();
            EditorInfoBar.Message = "No recording available to export.";
            EditorInfoBar.Severity = InfoBarSeverity.Warning;
            EditorInfoBar.IsOpen = true;
            return;
        }

        ShowExportingState();
        await ExportVM.ExportCommand.ExecuteAsync(null);
    }

    private void ExportFlyout_Closed(object? sender, object e)
    {
        // Reset terminal state (Exported / Failed) so the next open starts a fresh
        // export. Without this, dismissing the flyout via click-outside leaves
        // ExportSucceeded=true; reopening the flyout would then short-circuit and
        // show the prior result instead of running a new export — which makes
        // subsequent style/edit changes appear to never apply to the output file.
        if (ExportVM.IsExporting) return;
        if (!ExportVM.ExportSucceeded && !ExportVM.ExportFailed) return;
        ExportVM.PrepareForExport();
        ShowExportingState();
    }

    private void ExportVM_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExportViewModel.IsExporting) && !ExportVM.IsExporting)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ExportVM.ExportSucceeded)
                    ShowExportedState();
                else if (ExportVM.ExportFailed)
                    ShowErrorState();
                else
                    ExportFlyout.Hide(); // Cancelled
            });
        }
    }

    private void ShowExportingState()
    {
        ExportingPanel.Visibility = Visibility.Visible;
        ExportedPanel.Visibility = Visibility.Collapsed;
        ExportErrorPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowExportedState()
    {
        ExportingPanel.Visibility = Visibility.Collapsed;
        ExportedPanel.Visibility = Visibility.Visible;
        ExportErrorPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowErrorState()
    {
        ExportingPanel.Visibility = Visibility.Collapsed;
        ExportedPanel.Visibility = Visibility.Collapsed;
        ExportErrorPanel.Visibility = Visibility.Visible;
    }

    private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
    {
        ExportVM.OpenOutputFolderCommand.Execute(null);
    }

    private void CancelExport_Click(object sender, RoutedEventArgs e)
    {
        ExportVM.CancelExportCommand.Execute(null);
    }

    private void CloseFlyout_Click(object sender, RoutedEventArgs e)
    {
        ExportFlyout.Hide();
        // Reset state so next open starts a fresh export
        ExportVM.PrepareForExport();
        ShowExportingState();
    }

    // ─── Background Style Editing ───────────────────────────────────────

    // ─── Cursor Style Editing ───────────────────────────────────────────

    private void InitializeCursorControls(Project project, CompositionConfig config)
    {
        bool hasCursor = !string.IsNullOrEmpty(project.CursorDataFilePath) && File.Exists(project.CursorDataFilePath);
        var vis = hasCursor ? Visibility.Visible : Visibility.Collapsed;
        CursorButton.Visibility = vis;
        CursorSeparator.Visibility = vis;

        if (!hasCursor) return;

        SyncCursorControlsToConfig(config.Cursor);
    }

    private void SyncCursorControlsToConfig(CursorStyle cursor)
    {
        _suppressCursorEvents = true;
        try
        {
            // Cursor type
            CursorTypeMouse.IsChecked = cursor.Type != CursorType.Touch;
            CursorTypeTouch.IsChecked = cursor.Type == CursorType.Touch;

            // Size
            CursorSizeSlider.Value = cursor.Scale;

            // Tilt
            CursorTiltToggle.IsOn = cursor.TiltEnabled;
            CursorTiltToggle.Visibility = cursor.Type != CursorType.Touch
                ? Visibility.Visible : Visibility.Collapsed;

            // Color — find matching radio button by Tag
            string cursorColor = (cursor.Color ?? "#FFFFFF").ToUpperInvariant();
            bool found = false;
            foreach (var child in CursorColorPanel.Children)
            {
                if (child is RadioButton rb && rb.Tag is string tag)
                {
                    bool match = string.Equals(tag, cursorColor, StringComparison.OrdinalIgnoreCase);
                    rb.IsChecked = match;
                    if (match) found = true;
                }
            }
            if (!found && CursorColorPanel.Children.FirstOrDefault() is RadioButton first)
                first.IsChecked = true;
        }
        finally
        {
            _suppressCursorEvents = false;
        }
    }

    private void CursorType_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressCursorEvents) return;
        // Show/hide tilt toggle based on cursor type
        bool isMouse = CursorTypeMouse.IsChecked == true;
        CursorTiltToggle.Visibility = isMouse ? Visibility.Visible : Visibility.Collapsed;
        ApplyCursorStyleFromControls();
    }

    private void CursorSizeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressCursorEvents) return;
        ScheduleCursorUpdate();
    }

    private void CursorTiltToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressCursorEvents) return;
        ApplyCursorStyleFromControls();
    }

    private void CursorColor_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressCursorEvents) return;
        ApplyCursorStyleFromControls();
    }

    private void ScheduleCursorUpdate()
    {
        if (_cursorDebounceTimer is null)
        {
            _cursorDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _cursorDebounceTimer.Tick += (_, _) =>
            {
                _cursorDebounceTimer.Stop();
                ApplyCursorStyleFromControls();
            };
        }
        _cursorDebounceTimer.Stop();
        _cursorDebounceTimer.Start();
    }

    private void ApplyCursorStyleFromControls()
    {
        var config = ProjectService.Instance.CurrentComposition;
        if (config is null) return;

        var cursorType = CursorTypeTouch.IsChecked == true ? CursorType.Touch : CursorType.Default;

        string color = "#FFFFFF";
        foreach (var child in CursorColorPanel.Children)
        {
            if (child is RadioButton rb && rb.IsChecked == true && rb.Tag is string tag)
            {
                color = tag;
                break;
            }
        }

        var newCursor = config.Cursor with
        {
            Type = cursorType,
            Scale = (float)CursorSizeSlider.Value,
            Color = color,
            TiltEnabled = CursorTiltToggle.IsOn,
        };

        config = config with { Cursor = newCursor };
        ProjectService.Instance.CurrentComposition = config;

        _ = RebuildPreviewRendererAsync(config);
    }

    // ─── Background Style Editing (continued) ───────────────────────────

    private void InitializeStyleControls(Project project, CompositionConfig config)
    {
        // Style menu is available for all capture types. Monitor (full-screen)
        // captures start with zeroed defaults (see ProjectService.SetProject) but
        // users can still customize padding, corner radius, shadow, border, etc.
        StyleButton.Visibility = Visibility.Visible;
        StyleSeparator.Visibility = Visibility.Visible;

        // Populate preset combo with built-in presets
        PresetCombo.Items.Clear();
        PresetCombo.Items.Add(new BrandPreset { Name = "(Custom)" });
        foreach (var preset in DefaultBrandPresets.All)
            PresetCombo.Items.Add(preset);

        // Load system wallpapers (async to avoid blocking UI thread)
        _ = LoadSystemWallpapersAsync();

        // Sync controls to current config, suppressing change events
        SyncStyleControlsToConfig(config.Background);
    }

    private async Task LoadSystemWallpapersAsync()
    {
        var wallpaperDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Web", "Wallpaper");

        // Enumerate and sort files on a background thread to avoid freezing the UI
        var paths = await Task.Run(() =>
        {
            if (!Directory.Exists(wallpaperDir))
                return new List<string>();

            return Directory.GetFiles(wallpaperDir, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => new FileInfo(f).Length)
                .ToList();
        });

        _wallpaperPaths = paths;

        WallpaperGrid.Items.Clear();
        foreach (var path in _wallpaperPaths)
        {
            var img = new Microsoft.UI.Xaml.Controls.Image
            {
                Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path))
                {
                    DecodePixelHeight = 96, // small thumbnails for perf
                },
            };
            var border = new Border
            {
                Width = 72,
                Height = 48,
                CornerRadius = new CornerRadius(4),
                Child = img,
            };
            WallpaperGrid.Items.Add(border);
        }
    }

    private void SyncStyleControlsToConfig(BackgroundStyle bg)
    {
        _suppressStyleEvents = true;
        try
        {
            // Background type combo
            int typeIndex = bg.Type switch
            {
                BackgroundType.Gradient => 1,
                BackgroundType.Image => 2,
                BackgroundType.Blur => 3,
                _ => 0, // SolidColor
            };
            BgTypeCombo.SelectedIndex = typeIndex;

            // Primary color
            var primaryColor = ParseHexColor(bg.Color);
            BgColorPicker.Color = primaryColor;
            BgColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(primaryColor);
            BgColorText.Text = bg.Color;

            // Panel visibility based on type
            bool isGradient = bg.Type == BackgroundType.Gradient;
            bool isImage = bg.Type == BackgroundType.Image;
            GradientPanel.Visibility = isGradient ? Visibility.Visible : Visibility.Collapsed;
            WallpaperPanel.Visibility = isImage ? Visibility.Visible : Visibility.Collapsed;
            ColorPanel.Visibility = bg.Type is not BackgroundType.Blur and not BackgroundType.Image
                ? Visibility.Visible : Visibility.Collapsed;

            if (isGradient)
            {
                var endColor = ParseHexColor(bg.GradientEndColor);
                GradEndColorPicker.Color = endColor;
                GradEndColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(endColor);
                GradEndColorText.Text = bg.GradientEndColor;
                GradAngleSlider.Value = bg.GradientAngle;
            }

            if (isImage && _wallpaperPaths is not null && !string.IsNullOrEmpty(bg.BackgroundImagePath))
            {
                int wpIdx = _wallpaperPaths.IndexOf(bg.BackgroundImagePath);
                WallpaperGrid.SelectedIndex = wpIdx >= 0 ? wpIdx : -1;
            }

            // Sliders
            PaddingSlider.Value = bg.Padding;
            CornerRadiusSlider.Value = bg.CornerRadius;

            // Toggles
            ShadowToggle.IsOn = bg.ShadowEnabled;
            BorderToggle.IsOn = bg.BorderEnabled;

            // Select matching preset or (Custom)
            PresetCombo.SelectedIndex = FindMatchingPresetIndex(bg);
        }
        finally
        {
            _suppressStyleEvents = false;
        }
    }

    private int FindMatchingPresetIndex(BackgroundStyle bg)
    {
        var presets = DefaultBrandPresets.All;
        for (int i = 0; i < presets.Count; i++)
        {
            var p = presets[i];
            if (p.BackgroundType == bg.Type &&
                string.Equals(p.BackgroundColor, bg.Color, StringComparison.OrdinalIgnoreCase) &&
                p.Padding == bg.Padding &&
                p.CornerRadius == bg.CornerRadius)
            {
                return i + 1; // +1 for the "(Custom)" entry at index 0
            }
        }
        return 0; // Custom
    }

    private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressStyleEvents) return;
        if (PresetCombo.SelectedItem is not BrandPreset preset) return;
        if (preset.Name == "(Custom)") return;

        var bg = BrandPresetConverter.ToBackgroundStyle(preset);
        SyncStyleControlsToConfig(bg);
        ApplyBackgroundStyle(bg);
    }

    private void BgTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressStyleEvents) return;

        var selectedType = BgTypeCombo.SelectedIndex switch
        {
            1 => BackgroundType.Gradient,
            2 => BackgroundType.Image,
            3 => BackgroundType.Blur,
            _ => BackgroundType.SolidColor,
        };

        GradientPanel.Visibility = selectedType == BackgroundType.Gradient
            ? Visibility.Visible : Visibility.Collapsed;
        WallpaperPanel.Visibility = selectedType == BackgroundType.Image
            ? Visibility.Visible : Visibility.Collapsed;
        ColorPanel.Visibility = selectedType is not BackgroundType.Blur and not BackgroundType.Image
            ? Visibility.Visible : Visibility.Collapsed;

        ScheduleStyleUpdate();
    }

    private void BgColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_suppressStyleEvents) return;
        var hex = ColorToHex(args.NewColor);
        BgColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(args.NewColor);
        BgColorText.Text = hex;
        ScheduleStyleUpdate();
    }

    private void GradEndColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_suppressStyleEvents) return;
        var hex = ColorToHex(args.NewColor);
        GradEndColorSwatch.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(args.NewColor);
        GradEndColorText.Text = hex;
        ScheduleStyleUpdate();
    }

    private void StyleSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressStyleEvents) return;
        ScheduleStyleUpdate();
    }

    private void StyleToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressStyleEvents) return;
        ScheduleStyleUpdate();
    }

    private void WallpaperGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressStyleEvents) return;
        ScheduleStyleUpdate();
    }

    private void ScheduleStyleUpdate()
    {
        // Debounce rapid changes (e.g. slider drags) to avoid thrashing the renderer
        if (_styleDebounceTimer is null)
        {
            _styleDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _styleDebounceTimer.Tick += (_, _) =>
            {
                _styleDebounceTimer.Stop();
                var bg = BuildBackgroundStyleFromControls();
                ApplyBackgroundStyle(bg);
            };
        }
        _styleDebounceTimer.Stop();
        _styleDebounceTimer.Start();
    }

    private BackgroundStyle BuildBackgroundStyleFromControls()
    {
        var bgType = BgTypeCombo.SelectedIndex switch
        {
            1 => BackgroundType.Gradient,
            2 => BackgroundType.Image,
            3 => BackgroundType.Blur,
            _ => BackgroundType.SolidColor,
        };

        string? imagePath = null;
        if (bgType == BackgroundType.Image && _wallpaperPaths is not null)
        {
            int idx = WallpaperGrid.SelectedIndex;
            if (idx >= 0 && idx < _wallpaperPaths.Count)
                imagePath = _wallpaperPaths[idx];
        }

        return new BackgroundStyle
        {
            Type = bgType,
            Color = BgColorText.Text,
            GradientEndColor = GradEndColorText.Text,
            GradientAngle = GradAngleSlider.Value,
            BackgroundImagePath = imagePath,
            Padding = (int)PaddingSlider.Value,
            CornerRadius = (int)CornerRadiusSlider.Value,
            ShadowEnabled = ShadowToggle.IsOn,
            ShadowBlur = 24,
            ShadowOpacity = 0.5,
            ShadowColor = "#000000",
            BorderEnabled = BorderToggle.IsOn,
            BorderWidth = 1,
            BorderColor = "#333333",
        };

    }

    private void ApplyBackgroundStyle(BackgroundStyle bg)
    {
        var config = ProjectService.Instance.CurrentComposition;
        config = config with { Background = bg };
        ProjectService.Instance.CurrentComposition = config;

        // Mark preset as Custom if it no longer matches
        _suppressStyleEvents = true;
        PresetCombo.SelectedIndex = FindMatchingPresetIndex(bg);
        _suppressStyleEvents = false;

        _ = RebuildPreviewRendererAsync(config);
    }

    /// <summary>
    /// Recreates only the PreviewRenderer with updated config, preserving
    /// frame reader, audio, and playhead position.
    /// </summary>
    private async Task RebuildPreviewRendererAsync(CompositionConfig config)
    {
        var project = ProjectService.Instance.CurrentProject;
        if (project is null) return;

        MouseRecordingData? mouseData = null;
        if (!string.IsNullOrEmpty(project.CursorDataFilePath) && File.Exists(project.CursorDataFilePath))
        {
            try { mouseData = MouseHookRecorder.LoadFromFile(project.CursorDataFilePath); }
            catch { /* no cursor data */ }
        }

        mouseData ??= new MouseRecordingData();

        _compositorReady = false;
        _previewRenderer?.Dispose();

        try
        {
            _previewRenderer = new PreviewRenderer();
            await _previewRenderer.InitializeAsync(
                mouseData, config,
                project.Width > 0 ? project.Width : 1920,
                project.Height > 0 ? project.Height : 1080,
                project.Duration,
                project.MouseToVideoOffsetSeconds,
                project.CropOffsetX,
                project.CropOffsetY,
                project.DpiScale);

            // Re-sync zoom keyframes from the model
            if (ViewModel.Model.ZoomKeyframes.Count > 0)
                _previewRenderer.UpdateZoomKeyframes(ViewModel.Model.ZoomKeyframes);

            _compositorReady = true;
        }
        catch
        {
            _previewRenderer?.Dispose();
            _previewRenderer = null;
        }

        // Re-render at current playhead position
        _lastRenderedFrameIndex = -1;
        var position = Timeline.PlayheadPosition;
        await UpdatePreviewFrameAsync(position, force: true);
    }

    private static Color ParseHexColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 6)
        {
            byte r = byte.Parse(hex[0..2], NumberStyles.HexNumber);
            byte g = byte.Parse(hex[2..4], NumberStyles.HexNumber);
            byte b = byte.Parse(hex[4..6], NumberStyles.HexNumber);
            return Color.FromArgb(255, r, g, b);
        }
        return Color.FromArgb(255, 26, 26, 46); // fallback
    }

    private static string ColorToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // ─── Webcam Overlay Drag / Resize ──────────────────────────────────

    private void InitializeWebcamOverlay(CompositionConfig config)
    {
        _hasWebcamOverlay = _webcamComposition is not null && config.WebcamStyle is not null;
        if (!_hasWebcamOverlay)
        {
            WebcamOverlayRect.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            WebcamShapeButton.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            WebcamShapeSeparator.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            return;
        }

        var style = config.WebcamStyle!;
        int outW = _previewRenderer?.OutputWidth ?? 1920;
        int outH = _previewRenderer?.OutputHeight ?? 1080;

        // Determine initial normalized position from style
        _webcamNormSize = style.Size / outW;
        if (style.NormalizedX.HasValue && style.NormalizedY.HasValue)
        {
            _webcamNormX = style.NormalizedX.Value;
            _webcamNormY = style.NormalizedY.Value;
        }
        else
        {
            float margin = style.Margin;
            float size = style.Size;
            (float px, float py) = style.Position switch
            {
                WebcamPosition.TopLeft => (margin, margin),
                WebcamPosition.TopRight => (outW - size - margin, margin),
                WebcamPosition.BottomLeft => (margin, outH - size - margin),
                _ => (outW - size - margin, outH - size - margin),
            };
            _webcamNormX = px / outW;
            _webcamNormY = py / outH;
        }

        WebcamOverlayRect.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        WebcamShapeButton.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        WebcamShapeSeparator.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        SyncWebcamOverlayUI(style);
        UpdateWebcamOverlayPosition();
    }

    private void UpdateWebcamOverlayPosition()
    {
        var layout = Preview.FrameLayoutRect;
        if (layout.Width <= 0 || layout.Height <= 0) return;

        double screenX = layout.X + _webcamNormX * layout.Width;
        double screenY = layout.Y + _webcamNormY * layout.Height;
        double screenSize = _webcamNormSize * layout.Width;

        Canvas.SetLeft(WebcamOverlayRect, screenX);
        Canvas.SetTop(WebcamOverlayRect, screenY);
        WebcamOverlayRect.Width = screenSize;
        WebcamOverlayRect.Height = screenSize;
    }

    private void WebcamOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Layout updates are handled by Preview.FrameLayoutChanged
    }

    private void WebcamOverlay_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_hasWebcamOverlay) return;

        _webcamDragging = true;
        _webcamDragStart = e.GetCurrentPoint(WebcamOverlayCanvas).Position;
        _webcamDragStartNormX = _webcamNormX;
        _webcamDragStartNormY = _webcamNormY;

        ((UIElement)sender).CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void WebcamOverlay_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_webcamDragging) return;

        var pos = e.GetCurrentPoint(WebcamOverlayCanvas).Position;
        var layout = Preview.FrameLayoutRect;
        if (layout.Width <= 0) return;

        double dx = pos.X - _webcamDragStart.X;
        double dy = pos.Y - _webcamDragStart.Y;

        _webcamNormX = (float)Math.Clamp(
            _webcamDragStartNormX + dx / layout.Width, 0, 1 - _webcamNormSize);
        _webcamNormY = (float)Math.Clamp(
            _webcamDragStartNormY + dy / layout.Height, 0, 1 - _webcamNormSize * layout.Width / layout.Height);

        UpdateWebcamOverlayPosition();

        // Live-update the compositor so the webcam video moves in real-time
        UpdateWebcamStyleLive();

        e.Handled = true;
    }

    private void WebcamOverlay_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (!_webcamDragging) return;

        _webcamDragging = false;
        ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        e.Handled = true;

        ApplyWebcamOverlayChange();
    }

    private void UpdateWebcamStyleLive()
    {
        if (_previewRenderer is null) return;

        int outW = _previewRenderer.OutputWidth;
        float pixelSize = _webcamNormSize * outW;

        var config = ProjectService.Instance.CurrentComposition;
        if (config?.WebcamStyle is null) return;

        var newStyle = config.WebcamStyle with
        {
            NormalizedX = _webcamNormX,
            NormalizedY = _webcamNormY,
            Size = pixelSize,
        };

        // Lightweight update — just change the style, re-render current frame
        _previewRenderer.UpdateWebcamStyle(newStyle);
        _ = UpdatePreviewFrameAsync(Preview.PlayheadPosition, force: true);
    }

    private void ApplyWebcamOverlayChange()
    {
        int outW = _previewRenderer?.OutputWidth ?? 1920;
        float pixelSize = _webcamNormSize * outW;

        var config = ProjectService.Instance.CurrentComposition;
        if (config?.WebcamStyle is null) return;

        var newStyle = config.WebcamStyle with
        {
            NormalizedX = _webcamNormX,
            NormalizedY = _webcamNormY,
            Size = pixelSize,
        };
        config = config with { WebcamStyle = newStyle };
        ProjectService.Instance.CurrentComposition = config;
    }

    private void WebcamShapeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressWebcamEvents) return;
        if (WebcamShapeCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;

        var shape = tag == "RoundedRect" ? WebcamShape.RoundedRect : WebcamShape.Circle;

        var config = ProjectService.Instance.CurrentComposition;
        if (config?.WebcamStyle is null) return;

        var newStyle = config.WebcamStyle with { Shape = shape };
        config = config with { WebcamStyle = newStyle };
        ProjectService.Instance.CurrentComposition = config;

        _previewRenderer?.UpdateWebcamStyle(newStyle);
        _ = UpdatePreviewFrameAsync(Preview.PlayheadPosition, force: true);
    }

    private void WebcamBorderSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppressWebcamEvents) return;

        var config = ProjectService.Instance.CurrentComposition;
        if (config?.WebcamStyle is null) return;

        var newStyle = config.WebcamStyle with { BorderWidth = (float)e.NewValue };
        config = config with { WebcamStyle = newStyle };
        ProjectService.Instance.CurrentComposition = config;

        _previewRenderer?.UpdateWebcamStyle(newStyle);
        _ = UpdatePreviewFrameAsync(Preview.PlayheadPosition, force: true);
    }

    private bool _suppressWebcamEvents;

    private void SyncWebcamOverlayUI(WebcamOverlayStyle style)
    {
        _suppressWebcamEvents = true;
        WebcamShapeCombo.SelectedIndex = style.Shape == WebcamShape.RoundedRect ? 1 : 0;
        WebcamBorderSlider.Value = style.BorderWidth;
        WebcamMirrorToggle.IsOn = style.Mirrored;
        _suppressWebcamEvents = false;
    }

    private void WebcamMirrorToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressWebcamEvents) return;

        var config = ProjectService.Instance.CurrentComposition;
        if (config?.WebcamStyle is null) return;

        var newStyle = config.WebcamStyle with { Mirrored = WebcamMirrorToggle.IsOn };
        config = config with { WebcamStyle = newStyle };
        ProjectService.Instance.CurrentComposition = config;

        _previewRenderer?.UpdateWebcamStyle(newStyle);
        _ = UpdatePreviewFrameAsync(Preview.PlayheadPosition, force: true);
    }

    // ─── Aspect Ratio / Fit / Crop Anchor ──────────────────────────────

    private bool _suppressAspectRatioEvents;

    /// <summary>
    /// Initializes the aspect ratio flyout from the current project + composition state.
    /// Safe to call multiple times; uses a suppression flag so event handlers don't
    /// re-write state during initialization.
    /// </summary>
    private void InitializeAspectRatioControls()
    {
        var project = ProjectService.Instance.CurrentProject;
        var config = ProjectService.Instance.CurrentComposition;
        if (project is null || config is null) return;

        _suppressAspectRatioEvents = true;
        try
        {
            SelectRatioRadio(project.AspectRatio);
            SelectFitModeRadio(project.FitMode);
            SelectZoomScopeRadio(project.ZoomScope);
            SelectCropAnchorRadio(project.CropAnchorX, project.CropAnchorY);
            UpdateFitAndAnchorVisibility(project.AspectRatio);
        }
        finally
        {
            _suppressAspectRatioEvents = false;
        }
    }

    private void SelectRatioRadio(AspectRatio ratio)
    {
        var target = ratio switch
        {
            AspectRatio.Landscape16x9 => Ratio16x9,
            AspectRatio.Portrait9x16 => Ratio9x16,
            AspectRatio.Square1x1 => Ratio1x1,
            AspectRatio.Instagram4x5 => Ratio4x5,
            AspectRatio.Classic4x3 => Ratio4x3,
            AspectRatio.Tall3x4 => Ratio3x4,
            AspectRatio.Cinematic21x9 => Ratio21x9,
            _ => RatioAuto,
        };
        target.IsChecked = true;
    }

    private void SelectFitModeRadio(FitMode fit)
    {
        FitModeSegmented.SelectedIndex = fit == FitMode.Contain ? 1 : 0;
    }

    private void SelectZoomScopeRadio(ZoomScope scope)
    {
        ZoomScopeSegmented.SelectedIndex = scope == ZoomScope.Source ? 1 : 0;
    }

    private void SelectCropAnchorRadio(double anchorX, double anchorY)
    {
        // Snap to nearest 0 / 0.5 / 1 on each axis to find the matching radio.
        static double NearestSnap(double v) => v < 0.25 ? 0.0 : v > 0.75 ? 1.0 : 0.5;
        double sx = NearestSnap(anchorX);
        double sy = NearestSnap(anchorY);
        string tag = $"{sx.ToString(CultureInfo.InvariantCulture)},{sy.ToString(CultureInfo.InvariantCulture)}";
        foreach (var child in CropAnchorGrid.Children)
        {
            if (child is RadioButton rb && rb.Tag is string t && t == tag)
            {
                rb.IsChecked = true;
                return;
            }
        }
        CropAnchorCenter.IsChecked = true;
    }

    private void UpdateFitAndAnchorVisibility(AspectRatio ratio)
    {
        bool ratioActive = ratio != AspectRatio.Auto;
        FitModePanel.Visibility = ratioActive ? Visibility.Visible : Visibility.Collapsed;
        bool coverActive = ratioActive && FitModeSegmented.SelectedIndex == 0;
        CropAnchorPanel.Visibility = coverActive ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AspectRatioOption_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressAspectRatioEvents) return;
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (!Enum.TryParse<AspectRatio>(tag, out var ratio)) return;
        ApplyAspectRatio(ratio);
    }

    private void FitModeSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAspectRatioEvents) return;
        if (FitModeSegmented.SelectedItem is not CommunityToolkit.WinUI.Controls.SegmentedItem item) return;
        if (item.Tag is not string tag) return;
        if (!Enum.TryParse<FitMode>(tag, out var fit)) return;
        ApplyFitMode(fit);
    }

    private void ZoomScopeSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAspectRatioEvents) return;
        if (ZoomScopeSegmented.SelectedItem is not CommunityToolkit.WinUI.Controls.SegmentedItem item) return;
        if (item.Tag is not string tag) return;
        if (!Enum.TryParse<ZoomScope>(tag, out var scope)) return;
        ApplyZoomScope(scope);
    }

    private void CropAnchor_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressAspectRatioEvents) return;
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        var parts = tag.Split(',');
        if (parts.Length != 2) return;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var ax)) return;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ay)) return;
        ApplyCropAnchor(ax, ay);
    }

    private void ApplyAspectRatio(AspectRatio ratio)
    {
        var project = ProjectService.Instance.CurrentProject;
        var config = ProjectService.Instance.CurrentComposition;
        if (project is null || config is null) return;

        project.AspectRatio = ratio;
        config = config with { AspectRatio = ratio };
        ProjectService.Instance.CurrentComposition = config;

        UpdateFitAndAnchorVisibility(ratio);

        _ = RebuildPreviewRendererAsync(config);
    }

    private void ApplyFitMode(FitMode fit)
    {
        var project = ProjectService.Instance.CurrentProject;
        var config = ProjectService.Instance.CurrentComposition;
        if (project is null || config is null) return;

        project.FitMode = fit;
        config = config with { FitMode = fit };
        ProjectService.Instance.CurrentComposition = config;

        UpdateFitAndAnchorVisibility(project.AspectRatio);

        _ = RebuildPreviewRendererAsync(config);
    }

    private void ApplyCropAnchor(double anchorX, double anchorY)
    {
        var project = ProjectService.Instance.CurrentProject;
        var config = ProjectService.Instance.CurrentComposition;
        if (project is null || config is null) return;

        project.CropAnchorX = anchorX;
        project.CropAnchorY = anchorY;
        config = config with { CropAnchorX = anchorX, CropAnchorY = anchorY };
        ProjectService.Instance.CurrentComposition = config;

        _ = RebuildPreviewRendererAsync(config);
    }

    private void ApplyZoomScope(ZoomScope scope)
    {
        var project = ProjectService.Instance.CurrentProject;
        var config = ProjectService.Instance.CurrentComposition;
        if (project is null || config is null) return;

        project.ZoomScope = scope;
        config = config with { ZoomScope = scope };
        ProjectService.Instance.CurrentComposition = config;

        _ = RebuildPreviewRendererAsync(config);
    }
}
