using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Musio_App.Pages;
using Musio_App.Services;
using Musio_App.Shell;
using Musio_App.ViewModels;
using Musio.Core.Services;
using Musio.Core.Settings;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.ExtendedExecution;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Musio_App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private AppShellWindow? _window;
    private SystemTrayService? _trayService;
    private GlobalHotkeyService? _hotkeyService;
    private ExtendedExecutionSession? _extendedSession;
    private bool _isExiting;
    private bool _isQuittingFromTray;
    private System.Threading.Timer? _quiesceTimer;

    /// <summary>The unified app shell window, or <c>null</c> before launch / after shutdown.</summary>
    public AppShellWindow? Shell => _window;

    /// <summary>
    /// Back-compat shim — older call sites (pickers, EditorPage) used this
    /// name to grab a host <see cref="Window"/> for parenting/minimizing.
    /// Points at <see cref="Shell"/> in Phase B.
    /// </summary>
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
    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // ---------------------------------------------------------------
        // Single-instance routing (spec §5.6). If another Musio process is
        // already running, forward the activation arguments to it and quit
        // this one so a second `musio --full=editor` opens the editor in
        // the existing window instead of spawning a duplicate.
        // ---------------------------------------------------------------
        try
        {
            var keyInstance = AppInstance.FindOrRegisterForKey("MusioMainInstance");
            if (!keyInstance.IsCurrent)
            {
                // Hand off our activation event to the running instance and exit.
                var activated = AppInstance.GetCurrent().GetActivatedEventArgs();
                await keyInstance.RedirectActivationToAsync(activated);
                Environment.Exit(0);
                return;
            }
            keyInstance.Activated += OnInstanceActivated;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] Single-instance setup failed: {ex.Message}");
        }

        var cli = ParseCliFlags(GetActivationArgumentString());

        // Rubber-duck Y3: ONE-TIME startup-mode migration.
        // Pre-Phase-C, the app defaulted to Full and never wrote
        // Shell.StartupMode. Existing installs must keep Full; only truly
        // fresh installs get the new Mini default.
        // Detection: HasBeenSet sentinel + presence of any stable pre-existing
        // settings key (DefaultSavePath/Theme/etc. that Phase A/B persisted).
        try
        {
            if (!ShellSettings.Instance.StartupModeHasBeenSet)
            {
                var store = Musio.Core.Settings.AppSettings.Instance;
                bool existingInstall =
                    store.HasKey("DefaultSavePath")
                    || store.HasKey("Theme")
                    || store.HasKey("DefaultFps")
                    || store.HasKey("DefaultCaptureMode")
                    || store.HasKey("IsSystemAudioEnabled")
                    || store.HasKey("IsMicEnabled")
                    || store.HasKey("IsWebcamEnabled");

                ShellSettings.Instance.StartupMode = existingInstall
                    ? StartupMode.Full
                    : StartupMode.Mini;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] StartupMode migration failed: {ex.Message}");
        }

        // Phase C: new-install default = Mini. Existing installs that ever
        // wrote a value keep it (migration above preserves Full when
        // appropriate).
        var startup = ShellSettings.Instance.StartupMode;

        // CLI flags override the persisted startup mode.
        var initialState = cli.InitialState
            ?? (startup == StartupMode.Mini ? AppShellState.MiniSetup : AppShellState.Full);

        _window = new AppShellWindow(initialState);
        _window.Closed += OnWindowClosed;
        _window.VisibilityChanged += OnWindowVisibilityChanged;
        _window.Activate();
        _window.InitializeAfterActivation();

        // Restore the user's last selection (audio toggles, capture mode,
        // region/window) onto the shared VM (spec §3.1 / §5.4).
        SelectionRestoreOutcome? restoreOutcome = null;
        try
        {
            restoreOutcome = SelectionRestoreService.RestoreOnLaunch(RecordingViewModel.Shared);
            _window.MiniSetup.SyncCaptureModeFromViewModel();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] Selection restore failed: {ex.Message}");
        }

        // If we launched directly into Full, navigate to the requested page.
        if (initialState == AppShellState.Full && _window.ContentFrame is { } frame)
        {
            try
            {
                if (frame.Content is null)
                    frame.Navigate(GetPageTypeFor(cli.FullPage ?? "record"));
            }
            catch { /* navigation optional */ }
        }

        // Clean up .frames/ from previously-exported sessions in the background
        var savePath = AppSettings.Instance.DefaultSavePath;
        _ = System.Threading.Tasks.Task.Run(() =>
            SessionCleanupService.CleanupExportedSessions(savePath));

        // System tray and hotkeys are optional — app works without them
        try
        {
            _trayService = new SystemTrayService();
            _trayService.Initialize(_window);
            _trayService.IsRecordingProbe = () => RecordingViewModel.Shared.IsRecording;
            _trayService.Show();
            _trayService.NewRecordingRequested += OnTrayNewRecordingRequested;
            _trayService.OpenFullRequested += OnTrayOpenFullRequested;
            _trayService.StopRecordingRequested += OnTrayStopRecordingRequested;
            _trayService.ShowRecordingPillRequested += OnTrayShowPillRequested;
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
            // Spec §4.7: Ctrl+Shift+R is the SUMMON hotkey (not start/stop).
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

        // Honor any CLI new-recording flag (after the window + services are up).
        if (cli.NewRecordingMode is CaptureMode initialMode)
        {
            _ = _window.SummonAsync();
            _ = HandleNewRecordingRequestAsync(initialMode);
        }
        // NOTE: we intentionally no longer auto-launch the region/window picker
        // on launch when SelectionRestoreOutcome.AutoLaunchPicker is true. The
        // picker capture is heavy (BitBlt of the virtual desktop) and running
        // it on the launch path made the app feel slow AND surfaced a stale
        // frame because the screenshot was taken before the desktop had fully
        // painted. The toolbar now appears immediately pre-selected for the
        // intended mode; the user explicitly clicks the picker affordance when
        // they're ready. CLI `--new-recording=region|window` still launches
        // the picker because the user opted in via the flag.

        if (restoreOutcome?.RegionDiscardedReason is { Length: > 0 } discardReason)
        {
            // Best-effort: fire after the page loads so the InfoBar host is alive.
            _ = ShowTransientInfoOnPageAsync(discardReason);
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
        if (_window?.ContentFrame?.Content is EditorPage editor)
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
        if (_window is null) return;

        switch (e.HotkeyId)
        {
            case GlobalHotkeyService.StartStopRecording:
                // Spec §4.7: Ctrl+Shift+R is the SUMMON hotkey.
                // - If recording: stop (same path as pill Stop button).
                // - Otherwise: summon the shell to MiniSetup and focus Record.
                _window.DispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        if (RecordingViewModel.Shared.IsRecording)
                            await StopRecordingViaSharedPathAsync();
                        else
                        {
                            await _window!.SummonAsync();
                            // Re-open the inline picker (with last selection
                            // pre-seeded by RegionSelectorOverlay) so the user
                            // sees the same setup they left behind.
                            await LaunchPickerForCurrentModeAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[App] Hotkey summon failed: {ex.Message}");
                    }
                });
                break;
            case GlobalHotkeyService.PauseResumeRecording:
                // Not yet wired in Phase C — pause/resume is out of scope.
                break;
            case GlobalHotkeyService.TakeScreenshot:
                // Not yet wired in Phase C.
                break;
        }
    }

    private void OnExitRequested(object? sender, EventArgs e)
    {
        // Tray "Quit Musio" — bypass close-to-tray and actually quit.
        _isQuittingFromTray = true;
        BeginQuiesce(timeoutMs: 2000);
    }

    private void OnWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        // Never block an OS- or user-initiated exit.
        if (_isExiting || _isQuittingFromTray || _window is null) return;

        // If the tray isn't available we have no way to bring the app
        // back, so let the close proceed instead of stranding the process.
        if (_trayService is null) return;

        // Spec §3.7: close while recording → run the normal Stop path,
        // then let close-to-tray apply on a subsequent close.
        if (RecordingViewModel.Shared.IsRecording)
        {
            args.Cancel = true;
            _ = StopRecordingViaSharedPathAsync();
            return;
        }

        // User clicked the window's X — minimize to tray + one-shot balloon.
        // Before hiding, pre-configure the shell into MiniSetup so the next
        // summon doesn't first paint a stale Full layout (eliminates the
        // flash the user sees when the hotkey brings the toolbar back).
        args.Cancel = true;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        try { _window.PrepareForSummonInBackground(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[App] PrepareForSummon failed: {ex.Message}"); }
        ShowWindow(hwnd, SW_HIDE);
        try { _trayService.ShowCloseToTrayBalloon(); } catch { }
    }

    // ---------------------------------------------------------------
    // Tray event wiring (spec §3.8 / §5.8)
    // ---------------------------------------------------------------

    private async void OnTrayNewRecordingRequested(object? sender, NewRecordingRequestedEventArgs e)
    {
        if (_window is null) return;
        try
        {
            await _window.SummonAsync();
            if (e.PreselectedMode is CaptureMode mode)
                await HandleNewRecordingRequestAsync(mode);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] Tray new-recording failed: {ex.Message}");
        }
    }

    private async void OnTrayOpenFullRequested(object? sender, OpenFullRequestedEventArgs e)
    {
        if (_window is null) return;
        try
        {
            await _window.SummonAsync(suppressMiniMorph: true);
            // Bring the window to Full and navigate to the requested page.
            if (_window.CurrentState != AppShellState.Full
                && _window.CurrentState != AppShellState.FullRecording)
            {
                await _window.TransitionToAsync(AppShellState.Full);
            }
            if (_window.ContentFrame is { } frame)
            {
                var target = GetPageTypeFor(e.Page ?? "record");
                if (frame.Content?.GetType() != target)
                    frame.Navigate(target);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] Tray open-full failed: {ex.Message}");
        }
    }

    private async void OnTrayStopRecordingRequested(object? sender, EventArgs e)
    {
        try { await StopRecordingViaSharedPathAsync(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] Tray stop failed: {ex.Message}");
        }
    }

    private async void OnTrayShowPillRequested(object? sender, EventArgs e)
    {
        if (_window is null) return;
        try { await _window.SummonAsync(suppressMiniMorph: true); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] Tray show-pill failed: {ex.Message}");
        }
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

    private void OnInstanceActivated(object? sender, AppActivationArguments args)
    {
        // Second-instance activation arrived — dispatch to the UI thread.
        if (_window is null) return;
        _window.DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var argStr = TryExtractArgumentString(args);
                var cli = ParseCliFlags(argStr);

                // Rubber-duck Y2: a no-flag second launch just focuses the
                // existing window in its current state — do NOT morph to
                // MiniSetup (that would surprise a user who's editing).
                bool hasIntent =
                    cli.InitialState is not null
                    || cli.NewRecordingMode is not null
                    || !string.IsNullOrEmpty(cli.FullPage);

                if (!hasIntent)
                {
                    BringToForegroundOnly();
                    return;
                }

                await _window.SummonAsync(suppressMiniMorph: cli.InitialState == AppShellState.Full);

                if (cli.InitialState == AppShellState.Full)
                {
                    if (_window.CurrentState != AppShellState.Full
                        && _window.CurrentState != AppShellState.FullRecording)
                    {
                        await _window.TransitionToAsync(AppShellState.Full);
                    }
                    if (_window.ContentFrame is { } frame)
                    {
                        var target = GetPageTypeFor(cli.FullPage ?? "record");
                        if (frame.Content?.GetType() != target)
                            frame.Navigate(target);
                    }
                }
                else if (cli.NewRecordingMode is CaptureMode mode)
                {
                    await HandleNewRecordingRequestAsync(mode);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] Instance activation handler failed: {ex.Message}");
            }
        });
    }

    private void BringToForegroundOnly()
    {
        if (_window is null) return;
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            ShowWindow(hwnd, SW_SHOW);
            ShowWindow(hwnd, SW_RESTORE);
            try { AllowSetForegroundWindow(unchecked((uint)Environment.ProcessId)); } catch { }
            try { SetForegroundWindow(hwnd); } catch { }
            _window.Activate();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] BringToForeground failed: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private async Task HandleNewRecordingRequestAsync(CaptureMode mode)
    {
        if (_window is null) return;
        RecordingViewModel.Shared.CaptureMode = mode;
        _window.MiniSetup.SyncCaptureModeFromViewModel();
        if (mode == CaptureMode.CustomRegion)
            await CapturePickerService.Shared.PickRegionAsync(_window);
        else if (mode == CaptureMode.Window)
            await CapturePickerService.Shared.PickWindowAsync(_window);
        _window.MiniSetup.FocusRecordButton();
    }

    private async Task LaunchPickerForCurrentModeAsync()
    {
        if (_window is null) return;
        // Defer one tick so the shell is shown before launching the picker.
        await Task.Yield();
        var mode = RecordingViewModel.Shared.CaptureMode;
        try
        {
            if (mode == CaptureMode.CustomRegion)
                await CapturePickerService.Shared.PickRegionAsync(_window);
            else if (mode == CaptureMode.Window)
                await CapturePickerService.Shared.PickWindowAsync(_window);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] Auto-picker failed: {ex.Message}");
        }
        _window.MiniSetup.FocusRecordButton();
    }

    private async Task StopRecordingViaSharedPathAsync()
    {
        // The shared VM owns the Stop semantics; same path the pill uses.
        if (!RecordingViewModel.Shared.IsRecording) return;
        try
        {
            await RecordingViewModel.Shared.StopRecordingCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] StopRecording failed: {ex.Message}");
        }
    }

    private async Task ShowTransientInfoOnPageAsync(string message)
    {
        // Surface restore-discarded etc. messages via the shell-level
        // overlay InfoBar (visible regardless of state).
        try
        {
            await Task.Delay(150);
            _window?.ShowTransientInfo(message, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] ShowTransientInfoOnPage failed: {ex.Message}");
        }
    }

    private static Type GetPageTypeFor(string page) => page?.ToLowerInvariant() switch
    {
        "editor" => typeof(EditorPage),
        "settings" => typeof(SettingsPage),
        _ => typeof(RecordingPage),
    };

    private static string GetActivationArgumentString()
    {
        try
        {
            var ea = AppInstance.GetCurrent().GetActivatedEventArgs();
            return TryExtractArgumentString(ea);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string TryExtractArgumentString(AppActivationArguments args)
    {
        try
        {
            if (args?.Data is ILaunchActivatedEventArgs launch)
                return launch.Arguments ?? string.Empty;
        }
        catch { }
        try
        {
            var raw = Environment.GetCommandLineArgs();
            if (raw is { Length: > 1 })
                return string.Join(' ', raw, 1, raw.Length - 1);
        }
        catch { }
        return string.Empty;
    }

    internal readonly record struct CliFlags(
        AppShellState? InitialState,
        string? FullPage,
        CaptureMode? NewRecordingMode);

    internal static CliFlags ParseCliFlags(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return default;
        AppShellState? state = null;
        string? fullPage = null;
        CaptureMode? newRecording = null;
        var tokens = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            var t = token.Trim();
            if (t.StartsWith("\"", StringComparison.Ordinal) && t.EndsWith("\"", StringComparison.Ordinal) && t.Length >= 2)
                t = t.Substring(1, t.Length - 2);
            string key = t;
            string? value = null;
            var eq = t.IndexOf('=');
            if (eq >= 0)
            {
                key = t.Substring(0, eq);
                value = t.Substring(eq + 1);
            }
            switch (key.ToLowerInvariant())
            {
                case "--mini":
                case "/mini":
                    state = AppShellState.MiniSetup;
                    break;
                case "--full":
                case "/full":
                    state = AppShellState.Full;
                    fullPage = value;
                    break;
                case "--new-recording":
                case "/new-recording":
                    state = AppShellState.MiniSetup;
                    // Rubber-duck Y1: bare --new-recording = just open Mini
                    // Setup, no auto-picker. A value disambiguates the mode.
                    if (string.IsNullOrEmpty(value))
                    {
                        newRecording = null;
                    }
                    else
                    {
                        newRecording = value.ToLowerInvariant() switch
                        {
                            "fullscreen" or "full" => CaptureMode.FullScreen,
                            "window" => CaptureMode.Window,
                            "region" or "customregion" => CaptureMode.CustomRegion,
                            _ => null,
                        };
                    }
                    break;
            }
        }
        return new CliFlags(state, fullPage, newRecording);
    }

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint dwProcessId);
}
