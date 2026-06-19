using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Musio_App.ViewModels;
using Windows.Graphics;

namespace Musio_App;

/// <summary>
/// Compact always-on-top overlay shown during recording.
/// Displays elapsed time and a stop button. Excluded from screen capture.
/// </summary>
public sealed partial class RecordingOverlayWindow : Window
{
    private readonly RecordingViewModel _viewModel;
    private bool _isClosingProgrammatically;
    private bool _stopRequested;
    private Microsoft.UI.Xaml.Media.Animation.Storyboard? _currentPhraseStoryboard;

    private static readonly string[] StoppingPhrases =
    [
        "Engaging…",
        "Standby…",
        "Energizing…",
        "Processing…",
        "Computing…",
        "Make it…",
        "Hold steady",
        "All stop",
    ];
    private DispatcherTimer? _phraseTimer;
    private int _phraseIndex;

    /// <summary>Raised when the user requests to stop recording (Stop button or closing the overlay).</summary>
    public event EventHandler? StopRequested;

    public RecordingOverlayWindow(RecordingViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

        // The recording overlay is a dark pill regardless of the app's light/dark
        // theme, so pin its content to the Dark theme. This keeps the acrylic
        // backdrop dark and the foreground brushes (text/spinner) light — otherwise
        // in Light app theme the text/spinner render near-invisible.
        RootGrid.RequestedTheme = ElementTheme.Dark;

        // Use system desktop acrylic for the window backdrop so DWM-drawn shadow
        // follows the rounded corners of the window. Fall back to a solid theme
        // brush on systems where acrylic is unsupported (e.g., remote sessions).
        if (DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        else
        {
            RootGrid.Background = (Brush)Application.Current.Resources["RecordingOverlaySolidBackgroundBrush"];
        }

        // Show initial elapsed time
        ElapsedText.Text = _viewModel.ElapsedTime;

        // Subscribe to VM changes for live elapsed time updates
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        ConfigureWindow();
    }

    private void ConfigureWindow()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // Remove title bar and border for a compact floating overlay
        ExtendsContentIntoTitleBar = true;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        // Exclude overlay from screen capture
        SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);

        // Remove DWM-drawn border and caption
        uint colorNone = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorNone, sizeof(uint));
        uint colorCaption = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorCaption, sizeof(uint));

        // Strip WS_BORDER and WS_DLGFRAME from the window style
        var style = GetWindowLong(hwnd, GWL_STYLE);
        style &= ~(WS_BORDER | WS_DLGFRAME);
        SetWindowLong(hwnd, GWL_STYLE, style);

        // Round the window corners at the OS level for a pill shape
        uint roundPreference = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref roundPreference, sizeof(uint));

        // Size the overlay(device pixels, scaled for DPI)
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi / 96.0;
        int width = (int)(200 * scale);
        int height = (int)(52 * scale);
        AppWindow.Resize(new SizeInt32(width, height));

        // Position at bottom-right of primary monitor work area
        PositionBottomRight(width, height);

        // If the user manually closes the overlay, stop recording
        AppWindow.Closing += OnOverlayClosing;
    }

    private void PositionBottomRight(int widthPx, int heightPx)
    {
        try
        {
            var displayArea = DisplayArea.GetFromWindowId(
                AppWindow.Id, DisplayAreaFallback.Primary);
            var workArea = displayArea.WorkArea;
            int margin = 16;
            int x = workArea.X + workArea.Width - widthPx - margin;
            int y = workArea.Y + workArea.Height - heightPx - margin;
            AppWindow.Move(new PointInt32(x, y));
        }
        catch
        {
            // Fallback: let the OS position the window
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RecordingViewModel.ElapsedTime))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                ElapsedText.Text = _viewModel.ElapsedTime;
            });
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_stopRequested) return;
            _stopRequested = true;
            _viewModel.NotifyStopRequested();
            StopButton.IsEnabled = false;
            ShowStoppingState();

            // Yield to let the UI render the stopping state before the heavy stop work begins
            await Task.Delay(50);

            StopRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RecordingOverlay] Stop_Click failed: {ex.Message}");
        }
    }

    private void ShowStoppingState()
    {
        // Swap to the stopping UI
        RecordingPanel.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Collapsed;
        StoppingTextHost.Visibility = Visibility.Visible;
        StoppingSpinner.Visibility = Visibility.Visible;

        _phraseTimer?.Stop();

        // Cycle through phrases with a vertical flip-ticker animation
        _phraseIndex = 0;
        _showingA = true;
        StoppingTextA.Text = StoppingPhrases[0];
        StoppingTextA.Opacity = 1;
        StoppingTextATranslate.Y = 0;
        StoppingTextB.Opacity = 0;
        StoppingTextBTranslate.Y = StoppingTextHost.Height;

        _phraseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _phraseTimer.Tick += (_, _) =>
        {
            _phraseIndex = (_phraseIndex + 1) % StoppingPhrases.Length;
            AnimateToPhrase(StoppingPhrases[_phraseIndex]);
        };
        _phraseTimer.Start();
    }

    private bool _showingA = true;

    private void AnimateToPhrase(string newPhrase)
    {
        double height = StoppingTextHost.Height;
        var outgoing = _showingA ? StoppingTextA : StoppingTextB;
        var incoming = _showingA ? StoppingTextB : StoppingTextA;
        var outgoingTranslate = _showingA ? StoppingTextATranslate : StoppingTextBTranslate;
        var incomingTranslate = _showingA ? StoppingTextBTranslate : StoppingTextATranslate;

        // Pre-position the incoming text below the host and set its content
        incoming.Text = newPhrase;
        incoming.Opacity = 0;
        incomingTranslate.Y = height;

        var duration = TimeSpan.FromMilliseconds(350);
        var easing = new Microsoft.UI.Xaml.Media.Animation.CubicEase
        {
            EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseInOut,
        };

        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();

        // Outgoing: slide up + fade out
        var outY = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = -height,
            Duration = duration,
            EasingFunction = easing,
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(outY, outgoingTranslate);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(outY, "Y");
        sb.Children.Add(outY);

        var outOp = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = 0,
            Duration = duration,
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(outOp, outgoing);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(outOp, "Opacity");
        sb.Children.Add(outOp);

        // Incoming: slide up from below + fade in
        var inY = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = 0,
            Duration = duration,
            EasingFunction = easing,
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(inY, incomingTranslate);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(inY, "Y");
        sb.Children.Add(inY);

        var inOp = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            To = 1,
            Duration = duration,
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(inOp, incoming);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(inOp, "Opacity");
        sb.Children.Add(inOp);

        _currentPhraseStoryboard?.Stop();
        _currentPhraseStoryboard = sb;
        sb.Completed += (_, _) =>
        {
            if (ReferenceEquals(_currentPhraseStoryboard, sb)) _currentPhraseStoryboard = null;
        };
        sb.Begin();
        _showingA = !_showingA;
    }

    private async void OnOverlayClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        try
        {
            if (!_isClosingProgrammatically)
            {
                args.Cancel = true;
                if (_stopRequested) return;
                _stopRequested = true;
                _viewModel.NotifyStopRequested();
                ShowStoppingState();
                await Task.Delay(50);
                StopRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RecordingOverlay] OnOverlayClosing failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Closes the overlay window without firing <see cref="StopRequested"/>.
    /// Use this when the recording has already been stopped and the overlay should just go away.
    /// </summary>
    public void CloseOverlay()
    {
        _phraseTimer?.Stop();
        _phraseTimer = null;
        _currentPhraseStoryboard?.Stop();
        _currentPhraseStoryboard = null;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _isClosingProgrammatically = true;
        try { Close(); }
        catch { /* window may already be closed */ }
    }

    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const uint DWMWCP_ROUND = 2;
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;
    private const int GWL_STYLE = -16;
    private const int WS_BORDER = 0x00800000;
    private const int WS_DLGFRAME = 0x00400000;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref uint pvAttribute, int cbAttribute);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern int GetWindowLong(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern int SetWindowLong(IntPtr hwnd, int nIndex, int dwNewLong);
}
