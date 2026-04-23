using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Musio_App.Services;
using Musio.Core.Services;
using Musio.Core.Settings;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Musio_App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    private SystemTrayService? _trayService;
    private GlobalHotkeyService? _hotkeyService;
    private bool _isExiting;

    /// <summary>The main application window, accessible for minimize/restore operations.</summary>
    public Window? MainAppWindow => _window;

    public static new App Current => (App)Application.Current;

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    private static readonly string LogPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Musio", "crash.log");

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
            System.IO.File.AppendAllText(LogPath,
                $"[{DateTime.Now:O}] {e.Exception.GetType().Name}: {e.Exception.Message}\n{e.Exception.StackTrace}\n\n");
        }
        catch { }
        e.Handled = true;
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Closed += OnWindowClosed;
        _window.Activate();

        // Clean up .frames/ from previously-exported sessions in the background
        var savePath = AppSettings.Instance.DefaultSavePath;
        _ = System.Threading.Tasks.Task.Run(() =>
            SessionCleanupService.CleanupExportedSessions(savePath));

        // System tray and hotkeys are optional — app works without them
        try
        {
            _trayService = new SystemTrayService();
            _trayService.Initialize(_window);
            _trayService.Show();
            _trayService.ShowWindowRequested += OnShowWindowRequested;
            _trayService.ExitRequested += OnExitRequested;
            _window.AppWindow.Closing += OnWindowClosing;
        }
        catch (Exception)
        {
            // System tray not available — continue without it
            _trayService = null;
        }

        try
        {
            _hotkeyService = new GlobalHotkeyService();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            _hotkeyService.Initialize(hwnd);
            _hotkeyService.RegisterHotkey(
                GlobalHotkeyService.StartStopRecording,
                ModifierKeys.Ctrl | ModifierKeys.Shift, 0x52);
            _hotkeyService.RegisterHotkey(
                GlobalHotkeyService.PauseResumeRecording,
                ModifierKeys.Ctrl | ModifierKeys.Shift, 0x50);
            _hotkeyService.RegisterHotkey(
                GlobalHotkeyService.TakeScreenshot,
                ModifierKeys.Ctrl | ModifierKeys.Shift, 0x53);
            _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        }
        catch (Exception)
        {
            // Global hotkeys not available — continue without them
            _hotkeyService = null;
        }
    }

    private void OnShowWindowRequested(object? sender, EventArgs e)
    {
        if (_window is null) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        ShowWindow(hwnd, SW_SHOW);
        _window.Activate();
    }

    private void OnHotkeyPressed(object? sender, HotkeyPressedEventArgs e)
    {
        switch (e.HotkeyId)
        {
            case GlobalHotkeyService.StartStopRecording:
                // TODO: wire to recording start/stop
                break;
            case GlobalHotkeyService.PauseResumeRecording:
                // TODO: wire to pause/resume
                break;
            case GlobalHotkeyService.TakeScreenshot:
                // TODO: wire to screenshot
                break;
        }
    }

    private void OnExitRequested(object? sender, EventArgs e)
    {
        _isExiting = true;
        _hotkeyService?.Dispose();
        _trayService?.Dispose();
        _window?.Close();
        // WinUI 3 may not terminate after the last window closes, and
        // Window.Closed may not fire reliably for hidden windows.
        // Force exit so the process never lingers in the task manager.
        Environment.Exit(0);
    }

    private void OnWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_isExiting || _window is null) return;

        // Minimize to tray instead of exiting
        args.Cancel = true;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        ShowWindow(hwnd, SW_HIDE);
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // WinUI 3 does not terminate the process when the last window closes.
        // Handles the non-tray exit path (no OnExitRequested).
        // Skip service disposal here — the OS reclaims all resources on exit,
        // and Dispose during window teardown can be unreliable.
        Environment.Exit(0);
    }

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
