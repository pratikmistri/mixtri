using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Musio_App.Controls;

public sealed partial class PreviewCanvas : UserControl
{
    private CanvasRenderTarget? _previewFrame;
    private DispatcherTimer? _playbackTimer;
    private System.Diagnostics.Stopwatch? _playbackClock;
    private TimeSpan _playbackStartPosition;
    private bool _isPlaying;
    private bool _pointerOverPreview;
    private Color _previewClearColor = Color.FromArgb(255, 20, 20, 20);

    /// <summary>
    /// The current frame layout rect in canvas coordinates (origin + size of the
    /// aspect-ratio-fitted frame within the control). Used by overlay controls
    /// to map screen coordinates to output-space coordinates.
    /// </summary>
    public Rect FrameLayoutRect { get; private set; }

    /// <summary>
    /// Raised after each draw when <see cref="FrameLayoutRect"/> has been updated.
    /// </summary>
    public event EventHandler? FrameLayoutChanged;

    /// <summary>
    /// Raised each time the playback timer ticks so the host can supply a new composed frame.
    /// </summary>
    public event EventHandler? PlaybackTick;

    /// <summary>
    /// Raised when playback loops back to the beginning.
    /// </summary>
    public event EventHandler? PlaybackLooped;

    /// <summary>
    /// Raised when play/pause state changes.
    /// </summary>
    public event EventHandler<bool>? IsPlayingChanged;

    public static readonly DependencyProperty PlayheadPositionProperty =
        DependencyProperty.Register(nameof(PlayheadPosition), typeof(TimeSpan), typeof(PreviewCanvas),
            new PropertyMetadata(TimeSpan.Zero, OnPlayheadPositionChanged));

    public static readonly DependencyProperty DurationProperty =
        DependencyProperty.Register(nameof(Duration), typeof(TimeSpan), typeof(PreviewCanvas),
            new PropertyMetadata(TimeSpan.Zero, OnDurationChanged));

    public TimeSpan PlayheadPosition
    {
        get => (TimeSpan)GetValue(PlayheadPositionProperty);
        set => SetValue(PlayheadPositionProperty, value);
    }

    public TimeSpan Duration
    {
        get => (TimeSpan)GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (_isPlaying == value) return;
            _isPlaying = value;
            // Play icon &#xE768; / Pause icon &#xE769;
            PlayPauseIcon.Glyph = _isPlaying ? "\uE769" : "\uE768";
            IsPlayingChanged?.Invoke(this, _isPlaying);
        }
    }

    /// <summary>Preview FPS — set from project capture FPS (capped at 30).</summary>
    public int PreviewFps
    {
        get => _previewFps;
        set
        {
            _previewFps = Math.Max(1, value);
            if (_playbackTimer is not null)
                _playbackTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / _previewFps);
        }
    }
    private int _previewFps = 30;

    /// <summary>
    /// Raised when the Win2D surface built a replacement device after a GPU device loss (TDR,
    /// driver update, display change). <see cref="CanvasControl"/> handles that loss
    /// internally and silently — no exception surfaces, and
    /// <see cref="Microsoft.Graphics.Canvas.CanvasDevice.DeviceLost"/> on the shared device
    /// need never fire. Everything the app cached on the old device is dead at this point,
    /// so the host must rebuild it.
    /// </summary>
    public event EventHandler? DeviceRecreated;

    public PreviewCanvas()
    {
        InitializeComponent();
        ResolveThemeColors();
        ActualThemeChanged += (_, _) => { ResolveThemeColors(); PreviewSurface.Invalidate(); };
        PreviewSurface.CreateResources += PreviewSurface_CreateResources;
        UpdateTimeDisplay();
    }

    private void PreviewSurface_CreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        if (args.Reason != CanvasCreateResourcesReason.NewDevice) return;

        // The held frame was allocated on the device that just died. Dropping it here is what
        // keeps PreviewSurface_Draw from handing a dead-device bitmap to the new device on the
        // very next paint — which draws nothing and leaves the surface blank indefinitely,
        // since nothing else in the app is watching this control's device.
        var stale = _previewFrame;
        _previewFrame = null;
        if (stale is not null)
        {
            try { stale.Dispose(); }
            catch { /* the frame belongs to the lost device */ }
        }

        Musio.Core.Diagnostics.DiagLog.Write(
            "Editor", "preview CanvasControl recreated its device after loss; requesting rebuild");
        DeviceRecreated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Shows the active adaptive preview tier when it is below the source resolution.
    /// </summary>
    public void SetQualityIndicator(
        int maxPreviewWidth, int maxPreviewHeight, int sourceWidth, int sourceHeight)
    {
        bool downscaled = sourceWidth > maxPreviewWidth || sourceHeight > maxPreviewHeight;
        if (!downscaled)
        {
            QualityIndicator.Visibility = Visibility.Collapsed;
            return;
        }

        QualityIndicatorText.Text = $"Adaptive \u00b7 {maxPreviewHeight}p";
        QualityIndicator.Visibility = Visibility.Visible;
    }

    public void HideQualityIndicator()
    {
        QualityIndicator.Visibility = Visibility.Collapsed;
    }

    public void ClearFrame()
    {
        SetFrame(null);
    }

    public void InvalidateSurface()
    {
        PreviewSurface.Invalidate();
    }

    private void PreviewRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverPreview = true;
        AnimatePlaybackChrome(1, TimeSpan.FromMilliseconds(140));
    }

    private void PreviewRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverPreview = false;
        if (!IsFocusWithinPlaybackChrome())
            AnimatePlaybackChrome(0, TimeSpan.FromMilliseconds(280));
    }

    private void PlaybackChrome_GotFocus(object sender, RoutedEventArgs e)
    {
        AnimatePlaybackChrome(1, TimeSpan.FromMilliseconds(120));
    }

    private void PlaybackChrome_LostFocus(object sender, RoutedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_pointerOverPreview && !IsFocusWithinPlaybackChrome())
                AnimatePlaybackChrome(0, TimeSpan.FromMilliseconds(280));
        });
    }

    private void AnimatePlaybackChrome(float targetOpacity, TimeSpan duration)
    {
        PlaybackChrome.IsHitTestVisible = targetOpacity > 0;
        var visual = ElementCompositionPreview.GetElementVisual(PlaybackChrome);
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1, targetOpacity);
        animation.Duration = duration;
        visual.StartAnimation("Opacity", animation);
    }

    private bool IsFocusWithinPlaybackChrome()
    {
        if (XamlRoot is null)
            return false;

        DependencyObject? current = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (current is not null)
        {
            if (ReferenceEquals(current, PlaybackChrome))
                return true;

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void ResolveThemeColors()
    {
        try
        {
            if (Resources.TryGetValue("PreviewSurfaceClearBrush", out var val)
                && val is Microsoft.UI.Xaml.Media.SolidColorBrush brush)
                _previewClearColor = brush.Color;
        }
        catch { /* use default */ }
    }

    /// <summary>
    /// Sets the current frame to display and invalidates the canvas.
    /// The caller retains ownership of previous frames and must dispose them.
    /// </summary>
    public void SetFrame(CanvasRenderTarget? frame)
    {
        var old = _previewFrame;
        _previewFrame = frame;
        if (old is not null)
        {
            try { old.Dispose(); }
            catch { /* the frame may belong to a lost graphics device */ }
        }
        PreviewSurface.Invalidate();
    }

    /// <summary>Starts or resumes playback.</summary>
    public void Play()
    {
        if (IsPlaying) return;
        EnsureTimer();
        _playbackStartPosition = PlayheadPosition;
        _playbackClock = System.Diagnostics.Stopwatch.StartNew();
        _playbackTimer!.Start();
        IsPlaying = true;
    }

    /// <summary>Pauses playback.</summary>
    public void Pause()
    {
        _playbackTimer?.Stop();
        _playbackClock?.Stop();
        IsPlaying = false;
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (IsPlaying)
            Pause();
        else
            Play();
    }

    private void EnsureTimer()
    {
        if (_playbackTimer is not null) return;

        _playbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / _previewFps)
        };
        _playbackTimer.Tick += OnPlaybackTick;
    }

    private void OnPlaybackTick(object? sender, object e)
    {
        if (Duration.TotalSeconds <= 0 || _playbackClock is null) return;

        // Use wall-clock time for accurate sync with audio
        var newPosition = _playbackStartPosition + _playbackClock.Elapsed;
        if (newPosition >= Duration)
        {
            newPosition = TimeSpan.Zero;
            _playbackStartPosition = TimeSpan.Zero;
            _playbackClock.Restart();
            PlaybackLooped?.Invoke(this, EventArgs.Empty);
        }

        PlayheadPosition = newPosition;
        PlaybackTick?.Invoke(this, EventArgs.Empty);
    }

    private void PreviewSurface_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        ds.Clear(_previewClearColor);

        if (_previewFrame is null) return;

        float canvasW = (float)sender.ActualWidth;
        float canvasH = (float)sender.ActualHeight;
        float frameW = (float)_previewFrame.SizeInPixels.Width;
        float frameH = (float)_previewFrame.SizeInPixels.Height;

        if (frameW <= 0 || frameH <= 0) return;

        // Scale to fit while maintaining aspect ratio
        float scale = Math.Min(canvasW / frameW, canvasH / frameH);
        float destW = frameW * scale;
        float destH = frameH * scale;
        float destX = (canvasW - destW) / 2f;
        float destY = (canvasH - destH) / 2f;

        FrameLayoutRect = new Rect(destX, destY, destW, destH);

        ds.DrawImage(_previewFrame,
            new Rect(destX, destY, destW, destH),
            new Rect(0, 0, frameW, frameH));

        FrameLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void OnPlayheadPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PreviewCanvas canvas)
            canvas.UpdateTimeDisplay();
    }

    private static void OnDurationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PreviewCanvas canvas)
            canvas.UpdateTimeDisplay();
    }

    private void UpdateTimeDisplay()
    {
        if (TimeDisplay is null) return;
        TimeDisplay.Text = $"{FormatTime(PlayheadPosition)} / {FormatTime(Duration)}";
    }

    private static string FormatTime(TimeSpan t)
    {
        if (t.TotalMinutes >= 1)
            return $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
        return $"0:{t.Seconds:D2}";
    }
}
