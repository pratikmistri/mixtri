using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Musio_App.Pages;
using Musio_App.Services;
using Musio.Core.Services;
using Musio.Core.Settings;
using Windows.ApplicationModel.ExtendedExecution;

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
    private ExtendedExecutionSession? _extendedSession;
    private bool _isExiting;
    private System.Threading.Timer? _quiesceTimer;

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
        _window.VisibilityChanged += OnWindowVisibilityChanged;
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
        ReleaseExtendedExecution();
    }

    /// <summary>
    /// Begins a bounded shutdown. Used for both OS-initiated quiesce
    /// (logoff, shutdown, MSIX update, Task Manager end-task) and the
    /// user-initiated tray "Exit" command, so the two paths can't drift.
    /// Idempotent. Always exits the process within the OS quiesce budget
    /// via a hard <see cref="System.Threading.Timer"/> safety net.
    /// </summary>
    /// <param name="timeoutMs">
    /// Hard upper bound (ms) before <c>Environment.Exit</c> is forced.
    /// Defaults to 1500 ms to stay well under the OS quiesce window
    /// (~2 s on package update, ~5 s on shutdown).
    /// </param>
    public void BeginQuiesce(int timeoutMs = 1500)
    {
        if (_isExiting) return;
        _isExiting = true;

        try { ReleaseExtendedExecution(); } catch { }
        try { _hotkeyService?.Dispose(); } catch { }
        try { _trayService?.Dispose(); } catch { }

        _quiesceTimer = new System.Threading.Timer(
            _ => Environment.Exit(0), null, timeoutMs, System.Threading.Timeout.Infinite);

        try
        {
            _window?.DispatcherQueue.TryEnqueue(() =>
            {
                try { _window?.Close(); }
                catch { Environment.Exit(0); }
            });
        }
        catch
        {
            Environment.Exit(0);
        }
    }

    /// <summary>
    /// Back-compat shim — older call sites used this name.
    /// </summary>
    public void HandleSystemShutdown() => BeginQuiesce();

    private void OnWindowVisibilityChanged(object sender, WindowVisibilityChangedEventArgs args)
    {
        if (!args.Visible)
        {
            PauseEditorPlayback();
            _ = RequestExtendedExecutionAsync();
        }
        else
        {
            ReleaseExtendedExecution();
        }
    }

    private void PauseEditorPlayback()
    {
        if (_window is MainWindow mainWindow
            && mainWindow.ContentFrame.Content is EditorPage editor)
        {
            editor.PausePlayback();
        }
    }

    private async System.Threading.Tasks.Task RequestExtendedExecutionAsync()
    {
        if (_extendedSession is not null) return;

        try
        {
            var session = new ExtendedExecutionSession
            {
                Reason = ExtendedExecutionReason.Unspecified,
                Description = "Musio background tray operation",
            };
            session.Revoked += OnExtendedExecutionRevoked;

            var result = await session.RequestExtensionAsync();
            if (result == ExtendedExecutionResult.Allowed)
                _extendedSession = session;
            else
                session.Dispose();
        }
        catch
        {
            // ExtendedExecution not available on this platform — continue without it
        }
    }

    private void OnExtendedExecutionRevoked(object? sender, ExtendedExecutionRevokedEventArgs args)
    {
        _extendedSession?.Dispose();
        _extendedSession = null;
    }

    private void ReleaseExtendedExecution()
    {
        _extendedSession?.Dispose();
        _extendedSession = null;
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
        // User-initiated exit shares the same shutdown routine as OS
        // quiesce so the two paths can't drift; a slightly longer
        // timeout gives the dispatcher more room to drain cleanly when
        // we're not racing the OS quiesce budget.
        BeginQuiesce(timeoutMs: 2000);
    }

    private void OnWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        // Never block an OS- or user-initiated exit.
        if (_isExiting || _window is null) return;

        // If the tray isn't available we have no way to bring the app
        // back, so let the close proceed instead of stranding the process.
        if (_trayService is null) return;

        // User clicked the window's X — minimize to tray.
        args.Cancel = true;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        ShowWindow(hwnd, SW_HIDE);
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // The main window has closed. Ask the framework to shut the app
        // down cleanly, with a hard timer as a safety net in case the
        // dispatcher can't drain (e.g. during OS quiesce). If BeginQuiesce
        // already armed a timer we don't need a second one.
        if (_quiesceTimer is null)
        {
            _quiesceTimer = new System.Threading.Timer(
                _ => Environment.Exit(0), null, 1500, System.Threading.Timeout.Infinite);
        }

        try { Exit(); } catch { Environment.Exit(0); }
    }

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
