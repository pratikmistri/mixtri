using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Musio_App.Services;
using Musio_App.ViewModels;
using Windows.Graphics;

namespace Musio_App;

/// <summary>
/// Compact always-on-top overlay shown during recording.
/// Displays elapsed time and a stop button. Excluded from screen capture.
/// </summary>
/// <remarks>
/// Phase A: visual content lives in <see cref="Controls.RecordingPillControl"/>
/// and chrome setup is delegated to <see cref="WindowChromeService"/>.
/// This window only owns sizing, positioning, and the close-to-stop semantics.
/// </remarks>
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

        // Use system desktop acrylic for the window backdrop so DWM-drawn shadow
        // follows the rounded corners of the window. Fall back to a solid theme
        // brush on systems where acrylic is unsupported (e.g., remote sessions).
        if (DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        else
        {
            Pill.SetRootBackground((Brush)Application.Current.Resources["RecordingOverlaySolidBackgroundBrush"]);
        }

        Pill.Initialize(_viewModel);
        Pill.StopRequested += OnPillStopRequested;

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

        // Strip Win32/DWM chrome (capture-exclusion, no border, rounded corners).
        WindowChromeService.ApplyTo(this, ChromeProfile.Mini);

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

    private void OnPillStopRequested(object? sender, EventArgs e)
    {
        StopRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void OnOverlayClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        try
        {
            if (!_isClosingProgrammatically)
            {
                args.Cancel = true;
                if (Pill.IsStopRequested) return;
                Pill.MarkStopRequested();
                _viewModel.NotifyStopRequested();
                Pill.ShowStoppingState();
                await System.Threading.Tasks.Task.Delay(50);
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
        Pill.StopRequested -= OnPillStopRequested;
        Pill.Teardown();
        _isClosingProgrammatically = true;
        try { Close(); }
        catch { /* window may already be closed */ }
    }

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hwnd);
}
