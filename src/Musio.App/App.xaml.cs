using System;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Musio_App.Pages;
using Musio_App.Services;
using Musio.Core.Projects;
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
    private ShellCoordinator? _shell;
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
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash("AppDomain", e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("UnobservedTask", e.Exception);
            e.SetObserved();
        };
        // First-chance logging captures the ORIGINAL exception (incl. cross-thread
        // UI_E_WRONG_THREAD, HRESULT 0x802B000A) before it becomes a stowed failfast
        // that bypasses every managed handler. Filtered to COM/threading faults to
        // keep noise down.
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            var hr = (uint)(e.Exception.HResult & 0xFFFFFFFF);
            bool wrongThread = hr is 0x802B000A or 0x8001010E /* RPC_E_WRONG_THREAD */;
            bool comFault = e.Exception is System.Runtime.InteropServices.COMException
                or System.Runtime.InteropServices.InvalidComObjectException;
            bool msgThread = e.Exception.Message?.Contains("thread", StringComparison.OrdinalIgnoreCase) == true;
            if (wrongThread || comFault || msgThread)
                LogCrash($"FirstChance HR=0x{hr:X8}", e.Exception);
        };
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);
            System.IO.File.AppendAllText(LogPath,
                $"[{DateTime.Now:O}] ({source}) {ex?.GetType().Name}: {ex?.Message}\n{ex?.StackTrace}\n\n");
        }
        catch { }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogCrash("XAML", e.Exception);
        e.Handled = true;
    }

    /// <summary>
    /// Pulls the <c>.musio</c> path out of an activation, or <c>null</c> if the
    /// activation is not a file activation for a project we can open.
    /// </summary>
    private static string? ExtractProjectPath(
        Microsoft.Windows.AppLifecycle.AppActivationArguments? activation)
    {
        if (activation?.Kind != Microsoft.Windows.AppLifecycle.ExtendedActivationKind.File)
            return null;

        if (activation.Data is not Windows.ApplicationModel.Activation.IFileActivatedEventArgs fileArgs)
            return null;

        return fileArgs.Files
            .OfType<Windows.Storage.IStorageFile>()
            .Select(f => f.Path)
            .FirstOrDefault(MusioPackage.IsPackagePath);
    }

    /// <summary>
    /// Whether two paths name the same project file. Compared case-insensitively
    /// and fully resolved, since Explorer, the recent-projects list and the Open
    /// dialog can each hand back a different spelling of one file.
    /// </summary>
    private static bool IsSameProjectPath(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;

        try
        {
            return string.Equals(
                System.IO.Path.GetFullPath(left),
                System.IO.Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The <c>.musio</c> path the app was launched with, when it was started by
    /// double-clicking one in Explorer; <c>null</c> otherwise.
    /// </summary>
    /// <remarks>
    /// Deliberately synchronous so <see cref="OnLaunched"/> can decide which shell
    /// surface to open <em>before</em> starting it — resolving this asynchronously
    /// would flash the Mini pill first and leave the project stranded behind it.
    /// </remarks>
    private static string? TryGetActivationProjectPath()
    {
        try
        {
            return ExtractProjectPath(Microsoft.Windows.AppLifecycle.AppInstance
                .GetCurrent().GetActivatedEventArgs());
        }
        catch (Exception ex)
        {
            // Unpackaged runs have no activation info at all.
            System.Diagnostics.Debug.WriteLine($"[App] No file activation: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Opens the <c>.musio</c> project the app was launched with and shows it in
    /// the editor tab of the full window.
    /// </summary>
    /// <remarks>
    /// Failures are surfaced on the shell InfoBar rather than thrown: a bad file
    /// association should not prevent the app from starting.
    /// </remarks>
    private static async System.Threading.Tasks.Task OpenActivationProjectAsync(MainWindow window, string path)
    {
        try
        {
            await ProjectService.Instance.OpenPackageAsync(path);
            window.DispatcherQueue.TryEnqueue(window.ShowEditor);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] Could not open '{path}': {ex}");
            window.DispatcherQueue.TryEnqueue(() =>
                window.ShowRecordingError($"Could not open project: {ex.Message}"));
        }
    }

    /// <summary>
    /// Activation-manager key identifying the instance that owns a given project
    /// file. Hashed rather than using the path verbatim because keys are compared
    /// as opaque strings with a length limit, and paths are neither short nor
    /// case-consistent.
    /// </summary>
    private static string BuildProjectInstanceKey(string packagePath)
    {
        // Full path first, so two different links to the same name don't collide,
        // then case-folded because Windows paths are case-insensitive and Explorer
        // hands back whatever casing the user typed.
        string normalized;
        try { normalized = System.IO.Path.GetFullPath(packagePath); }
        catch { normalized = packagePath; }

        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalized.ToLowerInvariant()));
        return "musio-project-" + Convert.ToHexString(hash);
    }

    /// <summary>
    /// Ensures at most one window per project file. Returns <c>true</c> when this
    /// process handed its activation to the instance that already has
    /// <paramref name="packagePath"/> open and should stop launching.
    /// </summary>
    /// <remarks>
    /// Opening one project in two windows is a correctness problem, not just
    /// clutter: <c>MusioPackageService.OpenAsync</c> extracts media to a cache keyed
    /// by project id, so a second open re-extracts over files the first window still
    /// holds handles to, and both windows then save over the same <c>.musio</c>,
    /// silently discarding one set of edits.
    /// <para>
    /// Only file activations register a key. A plain launch stays un-keyed, so the
    /// recorder can still be started as many times as the user likes.
    /// </para>
    /// </remarks>
    private bool TryRedirectToExistingProjectInstance(string packagePath)
    {
        try
        {
            var key = BuildProjectInstanceKey(packagePath);
            var keyInstance = Microsoft.Windows.AppLifecycle.AppInstance
                .FindOrRegisterForKey(key);

            if (keyInstance.IsCurrent)
            {
                // We own this file now. Later activations for it are redirected here.
                Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().Activated
                    += OnRedirectedActivated;
                return false;
            }

            var activationArgs = Microsoft.Windows.AppLifecycle.AppInstance
                .GetCurrent().GetActivatedEventArgs();

            // RedirectActivationToAsync deadlocks if awaited on the thread that is
            // pumping the activation, so it is driven from a worker and waited on here.
            // Blocking is safe at this point: no window or XAML content exists yet.
            var handedOver = false;
            using var redirected = new System.Threading.ManualResetEventSlim(false);
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await keyInstance.RedirectActivationToAsync(activationArgs);
                    System.Threading.Volatile.Write(ref handedOver, true);
                }
                catch (Exception ex)
                {
                    // Most likely the owner died between FindOrRegisterForKey and the
                    // call, leaving a stale registration. Falls through to opening here.
                    System.Diagnostics.Debug.WriteLine($"[App] Redirect failed: {ex}");
                }
                finally { redirected.Set(); }
            });

            // Bounded so a wedged owner can't hang the launch forever.
            if (!redirected.Wait(TimeSpan.FromSeconds(5)))
            {
                // A timeout means the call hasn't returned — NOT that it failed, and
                // it is not cancellable, so it may still land. Opening here as well
                // would put two windows on one project, which is the corruption this
                // whole path exists to prevent. Only fall back when the owner is
                // actually gone; a live-but-slow owner keeps the activation.
                if (IsKeyHeldByAnotherInstance(key))
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[App] Redirect slow but owner alive; leaving the file to it.");
                    return true;
                }

                System.Diagnostics.Debug.WriteLine("[App] Redirect timed out; opening locally.");
                return false;
            }

            // Signalled is not the same as succeeded: the worker signals on the failure
            // path too. Reporting a failed hand-off as success would exit this process
            // without ever creating a window, so the double-click would do nothing at
            // all — and that path is reached faster than the timeout above.
            return System.Threading.Volatile.Read(ref handedOver);
        }
        catch (Exception ex)
        {
            // Unpackaged runs have no activation manager. Duplicate windows are far
            // better than failing to open the file at all.
            System.Diagnostics.Debug.WriteLine($"[App] Instance redirection unavailable: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Whether some other live instance still holds <paramref name="key"/>. Used to
    /// tell "the owner is wedged" from "the owner is gone" after a redirect timeout.
    /// </summary>
    private static bool IsKeyHeldByAnotherInstance(string key)
    {
        try
        {
            return Microsoft.Windows.AppLifecycle.AppInstance.GetInstances()
                .Any(i => !i.IsCurrent && string.Equals(i.Key, key, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] Could not enumerate instances: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Another process was launched for a project this instance owns the key for;
    /// surface our window instead of letting it open a second copy.
    /// </summary>
    private void OnRedirectedActivated(
        object? sender, Microsoft.Windows.AppLifecycle.AppActivationArguments args)
    {
        // The key is bound to the file this instance launched with, but the window can
        // since have been pointed at a different project through the in-app Open
        // dialog. So the request is honoured from the activation itself rather than
        // assumed to match — otherwise a redirect would raise a window showing an
        // unrelated project and the file the user double-clicked would never open.
        string? requested = null;
        try { requested = ExtractProjectPath(args); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[App] Redirected args unreadable: {ex.Message}");
        }

        // Raised on a background thread by the activation manager, and the key is
        // registered before the window is built — so a redirect can land while
        // _window is still null. The sending process has already exited by then, so
        // dropping it would lose the activation outright; park it under the gate
        // instead and let OnLaunched drain it.
        Window? window;
        lock (_foregroundGate)
        {
            window = _window;
            if (window is null)
            {
                _pendingForegroundRequest = true;
                _pendingProjectPath = requested;
                return;
            }
        }

        window.DispatcherQueue.TryEnqueue(() => HandleRedirectedActivation(requested));
    }

    /// <summary>
    /// Serves a redirected activation on the UI thread: opens the requested project
    /// if it isn't the one already showing, then brings the window forward.
    /// </summary>
    private void HandleRedirectedActivation(string? requestedPath)
    {
        if (requestedPath is not null
            && _window is MainWindow mainWindow
            && !IsSameProjectPath(requestedPath, ProjectService.Instance.CurrentPackagePath))
        {
            _ = OpenActivationProjectAsync(mainWindow, requestedPath);
        }

        BringToForeground();
    }

    /// <summary>Guards <see cref="_window"/> publication against redirects racing startup.</summary>
    private readonly object _foregroundGate = new();

    /// <summary>A redirect arrived before the window existed and still owes a focus.</summary>
    private bool _pendingForegroundRequest;

    /// <summary>The project that pending redirect asked for, if any.</summary>
    private string? _pendingProjectPath;

    /// <summary>Restores and focuses the main window, whatever state it was left in.</summary>
    private void BringToForeground()
    {
        if (_window is null) return;

        // A recording owns the screen. The coordinator deliberately minimises the main
        // window for AppShellState.Recording, and ShowFullWindow() honours that by
        // no-opping — but the raw calls below would not, so they would both film the
        // window and leave the state machine believing it is still minimised.
        if (_shell?.CurrentState == Musio.Core.Shell.AppShellState.Recording) return;

        // Goes through the coordinator so the shell doesn't still believe Mini is the
        // current surface and hide the window again on the next transition.
        _shell?.ShowFullWindow();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        ShowWindow(hwnd, IsIconic(hwnd) ? SW_RESTORE : SW_SHOW);
        _window.Activate();
        SetForegroundWindow(hwnd);
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // Resolved before anything is constructed: launching by double-clicking a
        // .musio forces the full window, and may hand the whole activation to an
        // instance that already has that file open.
        var activationPath = TryGetActivationProjectPath();

        if (activationPath is not null && TryRedirectToExistingProjectInstance(activationPath))
        {
            // The owning instance is taking over. Exit before creating a window, so
            // no second surface for this project ever appears. Process.Kill rather
            // than Application.Exit/Environment.Exit: those run finalizers and
            // ProcessExit handlers on an STA thread whose message loop was never
            // started, which is the documented way to leave a windowless zombie here.
            // The activation is already delivered — RedirectActivationToAsync
            // completed — so there is nothing left to flush.
            System.Diagnostics.Process.GetCurrentProcess().Kill();
            return;
        }

        // The full window is always constructed (it anchors app lifetime, hotkeys
        // and the quiesce path), but only shown when the startup mode asks for it.
        var mainWindow = new MainWindow();

        // Published under the gate so a redirect racing startup either sees the window
        // or leaves a pending request for the drain below — never neither.
        bool focusOwed;
        string? pendingPath;
        lock (_foregroundGate)
        {
            _window = mainWindow;
            focusOwed = _pendingForegroundRequest;
            pendingPath = _pendingProjectPath;
            _pendingForegroundRequest = false;
            _pendingProjectPath = null;
        }

        _window.Closed += OnWindowClosed;
        _window.VisibilityChanged += OnWindowVisibilityChanged;

        _shell = new ShellCoordinator(
            mainWindow,
            ShellSettings.ResolveLaunchMode(
                ShellSettings.Instance.StartupMode,
                hasFileActivation: activationPath is not null));
        _shell.Start();

        // Release captured JPEGs from finalized sessions in the background. Both roots are
        // swept: the LocalAppData sessions folder used now, and the user's save folder,
        // which holds sessions recorded before they moved out of it.
        var savePath = AppSettings.Instance.DefaultSavePath;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            SessionCleanupService.CleanupExportedSessions(Musio.Core.Capture.SessionPaths.SessionsRoot);
            if (!string.IsNullOrWhiteSpace(savePath))
                SessionCleanupService.CleanupExportedSessions(savePath);

            // Aimed only at the app-owned imports root, never at savePath: the sweep matches
            // import_* by name, and the user's save folder is theirs to put anything in.
            SessionCleanupService.CleanupOrphanedImports(Musio.Core.Capture.SessionPaths.ImportsRoot);
        });

        if (activationPath is not null)
            _ = OpenActivationProjectAsync(mainWindow, activationPath);

        // Document instances deliberately claim none of the app-global, singleton-ish
        // affordances below. Each .musio activation spawns its own process, so one tray
        // icon per open file would spam a notification area that is usually collapsed,
        // and the first process to call RegisterHotKey wins outright — a document window
        // opened before the recorder would take the capture hotkeys with it.
        // Everything downstream already copes with both being absent: window close is
        // allowed to proceed when _trayService is null (OnWindowClosing), and
        // ShellCoordinator.IsTrayAvailable stays false so hide-to-tray falls back to
        // showing the full window rather than stranding an unreachable process.
        if (activationPath is null)
        {
            InitializeTrayAndHotkeys();
        }

        // A redirect that landed while the window was still being built. Queued rather
        // than called directly so it runs after OnLaunched has finished wiring up.
        if (focusOwed)
        {
            // This instance is already opening activationPath, so only a request for a
            // *different* project needs opening — passing the same path again would
            // race the open already in flight, since CurrentPackagePath isn't set yet.
            var reopen = IsSameProjectPath(pendingPath, activationPath) ? null : pendingPath;
            mainWindow.DispatcherQueue.TryEnqueue(() => HandleRedirectedActivation(reopen));
        }
    }

    /// <summary>
    /// Sets up the system tray icon and the global capture hotkeys. Both are
    /// best-effort: the app works without either, and each is skipped for
    /// file-activated document instances (see <see cref="OnLaunched"/>).
    /// </summary>
    private void InitializeTrayAndHotkeys()
    {
        if (_window is null) return;

        // System tray and hotkeys are optional — app works without them
        try
        {
            _trayService = new SystemTrayService();
            _trayService.Initialize(_window);
            _trayService.Show();
            _trayService.ShowMiniRequested += OnShowMiniRequested;
            _trayService.ShowWindowRequested += OnShowWindowRequested;
            _trayService.StartRecordingRequested += OnStartRecordingRequested;
            _trayService.ExitRequested += OnExitRequested;
            _window.AppWindow.Closing += OnWindowClosing;

            // Tell the shell a tray affordance exists, so hide-to-tray is safe.
            if (_shell is not null) _shell.IsTrayAvailable = true;
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
        if (_shell is not null)
        {
            _shell.ShowFullWindow();
            ReleaseExtendedExecution();
            return;
        }

        if (_window is null) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        ShowWindow(hwnd, SW_SHOW);
        _window.Activate();
        ReleaseExtendedExecution();
    }

    private void OnShowMiniRequested(object? sender, EventArgs e)
    {
        _shell?.ActivateFromTray();
        ReleaseExtendedExecution();
    }

    private void OnStartRecordingRequested(object? sender, EventArgs e)
    {
        if (_shell is null) return;
        _window?.DispatcherQueue.TryEnqueue(() => _ = _shell.StartRecordingAsync());
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
        try { _shell?.Dispose(); } catch { }

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
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
}
