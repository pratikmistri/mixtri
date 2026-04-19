using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
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

    /// <summary>Raised when the user requests to stop recording (Stop button or closing the overlay).</summary>
    public event EventHandler? StopRequested;

    public RecordingOverlayWindow(RecordingViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

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

        // Size the overlay (device pixels, scaled for DPI)
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi / 96.0;
        int width = (int)(240 * scale);
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

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        StopRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnOverlayClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_isClosingProgrammatically)
        {
            // User manually closed the overlay (Alt+F4, etc.) — stop recording
            StopRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Closes the overlay window without firing <see cref="StopRequested"/>.
    /// Use this when the recording has already been stopped and the overlay should just go away.
    /// </summary>
    public void CloseOverlay()
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _isClosingProgrammatically = true;
        try { Close(); }
        catch { /* window may already be closed */ }
    }

    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hwnd);
}
