using System;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mixtri_App.Pages;
using Mixtri_App.Services;
using Mixtri.Core.Diagnostics;
using Mixtri.Core.Interop;
using Mixtri.Core.Projects;
using Mixtri.Core.Services;
using Mixtri.Core.Settings;
using Mixtri.Core.Shell;
using Windows.ApplicationModel.ExtendedExecution;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Mixtri_App;

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
    private bool _promptingUnsavedChanges;
    private bool _dismissalConfirmed;
    private System.Threading.Timer? _quiesceTimer;

    /// <summary>The main application window, accessible for minimize/restore operations.</summary>
    public Window? MainAppWindow => _window;
    public bool IsMiniHotkeyRegistered { get; private set; }

    public static new App Current => (App)Application.Current;

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    private static readonly string LogPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Mixtri", "crash.log");

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
                $"[{DateTime.Now:O}] ({source}) {DescribeException(ex)}\n");
        }
        catch { }
    }

    /// <summary>
    /// Renders the whole inner-exception chain with HRESULTs. A terminating fault gets one
    /// shot at leaving evidence behind, and the useful detail is usually two wrappers down.
    /// </summary>
    private static string DescribeException(Exception? ex)
    {
        if (ex is null) return "(no exception object)\n";

        var text = new System.Text.StringBuilder();
        var current = ex;
        for (var depth = 0; current is not null && depth < 5; depth++)
        {
            if (depth > 0) text.Append("--> inner: ");
            text.Append(current.GetType().FullName)
                .Append($" (HR=0x{(uint)current.HResult:X8}): ")
                .Append(current.Message).Append('\n')
                .Append(current.StackTrace).Append('\n');

            current = current is AggregateException aggregate
                ? aggregate.Flatten().InnerExceptions.FirstOrDefault()
                : current.InnerException;
        }

        return text.ToString();
    }

    /// <summary>
    /// Last-chance handler for exceptions that escaped to the XAML framework.
    /// </summary>
    /// <remarks>
    /// Only failures <see cref="UnhandledExceptionPolicy"/> explicitly recognises as recoverable
    /// are marked handled. Anything else is logged and then left unhandled on purpose, so the
    /// runtime terminates the process here rather than letting it run on top of half-mutated
    /// navigation, project or native state and die later as a context-free stowed failfast.
    /// </remarks>
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Exception? exception;
        try { exception = e.Exception; }
        catch { exception = null; }

        var decision = UnhandledExceptionPolicy.Classify(exception, _isExiting);
        LogCrash(
            $"XAML {(decision.ShouldRecover ? "recovered" : "fatal")} — {decision.Reason}",
            exception);

        e.Handled = decision.ShouldRecover;
    }

    /// <summary>
    /// Pulls the <c>.mixtri</c> path out of an activation, or <c>null</c> if the
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
            .FirstOrDefault(MixtriPackage.IsPackagePath);
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
    /// The <c>.mixtri</c> path the app was launched with, when it was started by
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
    /// Opens the <c>.mixtri</c> project the app was launched with and shows it in
    /// the editor tab of the full window.
    /// </summary>
    /// <remarks>
    /// Failures are surfaced on the shell InfoBar rather than thrown: a bad file
    /// association should not prevent the app from starting.
    /// </remarks>
    private async System.Threading.Tasks.Task OpenActivationProjectAsync(MainWindow window, string path)
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
        return "mixtri-project-" + Convert.ToHexString(hash);
    }

    /// <summary>
    /// Ensures at most one window per project file <em>for files opened by activation</em>.
    /// Returns <c>true</c> when this process handed its activation to the instance that
    /// already has <paramref name="packagePath"/> open and should stop launching.
    /// </summary>
    /// <remarks>
    /// Opening one project in two windows is a correctness problem, not just
    /// clutter: <c>MixtriPackageService.OpenAsync</c> extracts media to a cache keyed
    /// by project id, so a second open re-extracts over files the first window still
    /// holds handles to, and both windows then save over the same <c>.mixtri</c>,
    /// silently discarding one set of edits.
    /// <para>
    /// Only file activations register a key. A plain launch stays un-keyed, so the
    /// recorder can still be started as many times as the user likes.
    /// </para>
    /// <para>
    /// <b>Known limits.</b> The key is fixed for the process lifetime — it cannot be
    /// moved, because <c>AppInstance.UnregisterKey</c> has open platform bugs (an
    /// invalid key after re-registration, and a failfast when called off the
    /// registering thread). So a window repointed at another project through the
    /// in-app Open dialog still holds the key for the file it launched with, and
    /// projects opened that way — including anything opened in the un-keyed recorder
    /// instance — are not covered. <see cref="ServeRedirectedOpen"/> reconciles the
    /// request when a redirect does arrive, but it cannot stop a double-click on an
    /// unkeyed project from opening a second window.
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
            // The blocking wait is on the STA UI thread, which the CLR keeps pumping COM
            // for, so the cross-process call can still complete. A Task carries the
            // outcome; an event would only report that the worker finished, not whether
            // the hand-off actually succeeded.
            var redirect = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await keyInstance.RedirectActivationToAsync(activationArgs);
                    return true;
                }
                catch (Exception ex)
                {
                    // Most likely the owner died between FindOrRegisterForKey and the
                    // call, leaving a stale registration. Falls through to opening here.
                    System.Diagnostics.Debug.WriteLine($"[App] Redirect failed: {ex}");
                    return false;
                }
            });

            // Bounded so a wedged owner can't hang the launch forever.
            if (!redirect.Wait(TimeSpan.FromSeconds(5)))
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

            // Completing is not the same as succeeding. Reporting a failed hand-off as
            // success would exit this process without ever creating a window, so the
            // double-click would do nothing at all — and the failure path returns
            // faster than the timeout above, so it is the likelier of the two.
            return redirect.Result;
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
    /// if that is safe, then brings the window forward.
    /// </summary>
    private void HandleRedirectedActivation(string? requestedPath)
    {
        // A recording owns both the screen and the project state. Swapping the project
        // under a live capture would repoint the save target and spin the editor up
        // mid-capture, and raising the window would film it — so nothing here is safe,
        // not just the window manipulation.
        if (_shell?.CurrentState == Mixtri.Core.Shell.AppShellState.Recording) return;

        if (requestedPath is not null && _window is MainWindow mainWindow)
            ServeRedirectedOpen(mainWindow, requestedPath);

        BringToForeground();
    }

    /// <summary>
    /// Loads a redirected project request into this window, unless doing so would
    /// race an open already running or destroy work that was never saved.
    /// </summary>
    private void ServeRedirectedOpen(MainWindow window, string requestedPath)
    {
        var projects = ProjectService.Instance;

        // Already showing it, or already loading it — the ordinary case.
        if (IsSameProjectPath(requestedPath, projects.CurrentPackagePath)
            || IsSameProjectPath(requestedPath, projects.OpenInFlightPath))
        {
            return;
        }

        // Another project is mid-open or mid-save. Both publish their result only when
        // they finish, so swapping now would let the loser overwrite the winner's state:
        // a save landing after the swap rebinds CurrentPackagePath to the file it was
        // writing, leaving the newly opened project pointed at the old project's package
        // and silently overwriting it on the next save.
        if (projects.OpenInFlightPath is not null || projects.IsSaveInFlight)
        {
            window.ShowShellMessage(
                "This window is busy with another project, so the file you just opened "
                + "was not loaded here. Try opening it again in a moment.",
                Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational);
            return;
        }

        // Work that was never saved has no package path to compare against — a fresh
        // recording is the usual case, since ProjectService.SetProject clears
        // CurrentPackagePath. Opening over it would discard it with no prompt, on a
        // window the user may not even be looking at, triggered from another process.
        // Unsaved EDITS to an already-saved project are exactly as unrecoverable, and this
        // route has no way to ask: a window repointed at another project, then activated
        // for the file it was originally keyed to, hit precisely that.
        if (projects.HasUnrecoverableWork)
        {
            window.ShowShellMessage(
                "This window has unsaved work, so the project you just opened was not "
                + "loaded here. Save or close this window, then open the file again.",
                Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning);
            return;
        }

        _ = OpenActivationProjectAsync(window, requestedPath);
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
        if (_shell?.CurrentState == Mixtri.Core.Shell.AppShellState.Recording) return;

        // Goes through the coordinator so the shell doesn't still believe Mini is the
        // current surface and hide the window again on the next transition.
        _shell?.ShowFullWindow();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        NativeMethods.ShowWindow(hwnd, NativeMethods.IsIconic(hwnd) ? SW_RESTORE : SW_SHOW);
        _window.Activate();
        NativeMethods.SetForegroundWindow(hwnd);
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // Resolved before anything is constructed: launching by double-clicking a
        // .mixtri forces the full window, and may hand the whole activation to an
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
            SessionCleanupService.CleanupExportedSessions(Mixtri.Core.Capture.SessionPaths.SessionsRoot);
            if (!string.IsNullOrWhiteSpace(savePath))
                SessionCleanupService.CleanupExportedSessions(savePath);

            // Aimed only at the app-owned imports root, never at savePath: the sweep matches
            // import_* by name, and the user's save folder is theirs to put anything in.
            SessionCleanupService.CleanupOrphanedImports(Mixtri.Core.Capture.SessionPaths.ImportsRoot);
        });

        if (activationPath is not null)
            _ = OpenActivationProjectAsync(mainWindow, activationPath);

        // Document instances deliberately claim none of the app-global, singleton-ish
        // affordances below. Each .mixtri activation spawns its own process, so one tray
        // icon per open file would spam a notification area that is usually collapsed,
        // and the first process to call RegisterHotKey wins outright — a document window
        // opened before the recorder would take the Mini-mode hotkey with it.
        // Everything downstream already copes with both being absent: window close is
        // allowed to proceed when _trayService is null (OnWindowClosing), and
        // ShellCoordinator.IsTrayAvailable stays false so hide-to-tray falls back to
        // showing the full window rather than stranding an unreachable process.
        if (activationPath is null)
        {
            InitializeTray();
        }

        // Subscribed unconditionally, and deliberately NOT inside InitializeTray: a document
        // instance has no tray, so if this rode along with it, opening a .mixtri from Explorer,
        // editing it and clicking X would discard every change with no prompt — the single
        // most common edit flow. The handler already copes with there being no tray (it lets
        // the close proceed rather than stranding an unreachable process).
        mainWindow.AppWindow.Closing += OnWindowClosing;

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
    /// Sets up the system tray icon and the global Mini-mode hotkey. Both are
    /// best-effort and are skipped for file-activated document instances
    /// (see <see cref="OnLaunched"/>).
    /// </summary>
    private void InitializeTray()
    {
        if (_window is null) return;
        IsMiniHotkeyRegistered = false;

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

            bool registered = _hotkeyService.RegisterHotkey(
                GlobalHotkeyService.ShowMini,
                ModifierKeys.Win | ModifierKeys.Shift | ModifierKeys.NoRepeat,
                0x58); // X

            if (!registered)
            {
                DiagLog.Write("Hotkeys", "Win+Shift+X could not be registered.");
                _hotkeyService.Dispose();
                _hotkeyService = null;
            }
            else
            {
                IsMiniHotkeyRegistered = true;
                _hotkeyService.HotkeyPressed += OnHotkeyPressed;
            }
        }
        catch (Exception ex)
        {
            IsMiniHotkeyRegistered = false;
            DiagLog.Write("Hotkeys", $"Mini-mode hotkey initialization failed: {ex}");
            _hotkeyService?.Dispose();
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
        NativeMethods.ShowWindow(hwnd, SW_SHOW);
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
        IsMiniHotkeyRegistered = false;
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
                Description = "Mixtri background tray operation",
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
        if (e.HotkeyId != GlobalHotkeyService.ShowMini || _window is null) return;

        _window.DispatcherQueue.TryEnqueue(() =>
        {
            _shell?.ActivateFromTray();
            ReleaseExtendedExecution();
        });
    }

    private void OnExitRequested(object? sender, EventArgs e)
    {
        // Exiting from the tray skips the window entirely, so it is the one shutdown route
        // that can discard edits without the user ever seeing the window again.
        if (TryPromptUnsavedChanges(afterSaveDecision: () => BeginQuiesce(timeoutMs: 2000)))
            return;

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

        // A save prompt is on screen and still owns the decision. The X stays clickable
        // behind a ContentDialog, so dismissing here would park the window WITH the dialog
        // on it — invisible, unanswerable, and blocking every later exit on the guard below.
        // Cancel the dismissal and leave the question where the user can answer it.
        if (_promptingUnsavedChanges || ProjectSaveCoordinator.IsPromptActive)
        {
            args.Cancel = true;
            return;
        }

        // Closing the window ENDS THE EDITING SESSION: the project is closed with it (see
        // DismissMainWindow), so unsaved edits are genuinely about to be lost and this has to
        // ask first. The close is cancelled outright because AppWindowClosingEventArgs is
        // evaluated synchronously — there is no deferral, so the dialog cannot be awaited
        // "inside" the close. The prompt re-drives the dismissal once the user answers.
        if (TryPromptUnsavedChanges(afterSaveDecision: DismissMainWindow))
        {
            args.Cancel = true;
            return;
        }

        // Nothing to save. With a tray the window is parked rather than closed, so cancel the
        // close and dismiss by hand; without one, close the project and let the close through.
        if (_trayService is null)
        {
            CloseProjectForDismissal();
            return;
        }

        args.Cancel = true;
        DismissMainWindow();
    }

    /// <summary>
    /// Closes the open project as part of dismissing the window, so the app comes back to the
    /// empty editor rather than to a session the user thought they had ended.
    /// </summary>
    /// <remarks>
    /// Routed through the editor page when it is loaded, because the preview pipeline has to
    /// be torn down BEFORE the project is cleared — publishing a null project on its own
    /// leaves the last frame, the filmstrip and the audio players on screen (recorded
    /// previously against <c>ResetPreviewToEmptyState</c>). With no editor page there is no
    /// preview to tear down and clearing the service is enough. Skipped outright during a
    /// recording, which owns the project it is about to produce.
    /// </remarks>
    private void CloseProjectForDismissal()
    {
        if (_shell?.CurrentState == AppShellState.Recording) return;

        if (_window is MainWindow mainWindow
            && mainWindow.ContentFrame.Content is EditorPage editor)
        {
            editor.TryCloseProjectForShellDismissal();
            return;
        }

        if (ProjectService.Instance.CurrentProject is not null)
            ProjectService.Instance.ClearProject();
    }

    /// <summary>
    /// Shows the "save your changes?" prompt when the project is dirty, and returns true when
    /// it took ownership of the dismissal — in which case the caller must abort its own
    /// shutdown and let <paramref name="afterSaveDecision"/> resume it.
    /// </summary>
    /// <remarks>
    /// Returns false (and does nothing) when there is nothing to lose, so the ordinary
    /// close/exit paths stay exactly as they were. <c>_promptingUnsavedChanges</c> guards
    /// against a second prompt while one is already on screen: the X remains clickable behind
    /// a <see cref="ContentDialog"/>, and the tray's Exit item certainly is.
    /// </remarks>
    private bool TryPromptUnsavedChanges(Action afterSaveDecision)
    {
        if (_isExiting || _dismissalConfirmed) return false;
        if (_promptingUnsavedChanges) return true;

        // NOT HasUnsavedChanges: a freshly captured recording is deliberately clean, but it
        // has never been written anywhere, so closing over it destroys the whole take with
        // nothing to reopen. See ProjectService.HasUnrecoverableWork — the same predicate
        // ServeRedirectedOpen uses to refuse an open, which is the identical question.
        if (!ProjectService.Instance.HasUnrecoverableWork) return false;
        if (_window?.Content?.XamlRoot is null) return false;

        // A ContentDialog on a hidden window renders nowhere. The tray's Exit item runs with
        // the window parked in the tray, so without this the app would appear to ignore Exit
        // entirely while an invisible prompt waited for an answer — and the only way to see
        // it was to open the app again, where it looked like an unprompted save dialog. A
        // question the user cannot see is worse than no question.
        EnsureMainWindowVisible();

        _promptingUnsavedChanges = true;
        _ = RunUnsavedChangesPromptAsync(afterSaveDecision);
        return true;
    }

    /// <summary>
    /// Brings the full window back on screen if it is parked in the tray, so a modal prompt
    /// raised from a windowless route (the tray menu) is actually visible.
    /// </summary>
    /// <remarks>
    /// Routed through <see cref="ShellCoordinator.ShowFullWindow"/> rather than a raw
    /// <c>ShowWindow</c>: poking the HWND leaves the shell state machine believing another
    /// surface is current, which is the recorded cause of the app later hiding the window out
    /// from under the user.
    /// </remarks>
    private void EnsureMainWindowVisible()
    {
        if (_window is null) return;

        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);

            // IsWindowVisible is TRUE for a minimized window, so both states have to be
            // asked about: parked in the tray (hidden) and minimized to the taskbar. Either
            // one puts the dialog somewhere the user cannot answer it.
            if (!NativeMethods.IsWindowVisible(hwnd))
            {
                _shell?.ShowFullWindow();

                // The coordinator declines while a recording owns the screen, and a document
                // instance has no coordinator at all — so verify, and fall back to showing
                // the HWND directly rather than leaving the prompt invisible.
                if (!NativeMethods.IsWindowVisible(hwnd))
                    NativeMethods.ShowWindow(hwnd, SW_SHOW);
            }
            else if (NativeMethods.IsIconic(hwnd))
            {
                NativeMethods.ShowWindow(hwnd, SW_RESTORE);
            }

            _window.Activate();
        }
        catch (Exception ex)
        {
            // Best effort — the prompt is still shown, and the caller's guard against
            // proceeding without an answer is what keeps the edits safe either way.
            DiagLog.Write("Shell", $"Could not surface the window for the save prompt: {ex.Message}");
        }
    }

    private async Task RunUnsavedChangesPromptAsync(Action afterSaveDecision)
    {
        try
        {
            var root = _window?.Content?.XamlRoot;
            if (root is null) return;

            var decision = await ProjectSaveCoordinator.PromptUnsavedChangesAsync(
                root, _window, "before closing?");

            // Cancel (and Esc, which reports the same) means "keep working" — the window
            // stays exactly as it was. A failed or abandoned save reports Cancel too: the
            // edits are still unsaved and this is the last moment they can be rescued.
            if (decision == ProjectSaveCoordinator.UnsavedChangesDecision.Cancel) return;

            // Cleared BEFORE the dismissal runs, and replaced by a one-shot "already asked"
            // flag: closing the window re-raises Closing synchronously, and a still-set
            // prompting flag would cancel that close outright — leaving a window that can
            // never be shut.
            _promptingUnsavedChanges = false;
            _dismissalConfirmed = true;
            try
            {
                afterSaveDecision();
            }
            finally
            {
                // Only meaningful for the hide-to-tray route, where the window outlives the
                // dismissal and must ask again the next time it is closed.
                _dismissalConfirmed = false;
            }
        }
        catch (Exception ex)
        {
            // Most likely another ContentDialog is already open (export/import progress),
            // which WinUI rejects outright. Abandon the dismissal rather than proceeding:
            // continuing here would discard the edit session precisely because the prompt
            // that exists to protect it failed. The window stays open and the user can try
            // again once the other dialog is gone.
            DiagLog.Write("Shell", $"Unsaved-changes prompt failed; dismissal abandoned: {ex}");
        }
        finally
        {
            _promptingUnsavedChanges = false;
        }
    }

    /// <summary>
    /// Performs the dismissal the user asked for, now that the save question has been
    /// answered: the project is closed with the window, then the window is parked in the
    /// tray (or closed outright when there is no tray to come back from).
    /// </summary>
    /// <remarks>
    /// Closing the project is the point of the dismissal, not a side effect. Parking the
    /// window with the project still loaded — and still dirty — is what made the tray's Exit
    /// re-ask on a window nobody could see, and left the user with no way back to an empty
    /// editor short of deleting every clip.
    /// </remarks>
    private void DismissMainWindow()
    {
        CloseProjectForDismissal();

        if (_trayService is not null)
        {
            HideMainWindowToTray();
            return;
        }

        try { _window?.Close(); } catch { BeginQuiesce(); }
    }

    private void HideMainWindowToTray()
    {
        if (_window is null) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        NativeMethods.ShowWindow(hwnd, SW_HIDE);
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
}
