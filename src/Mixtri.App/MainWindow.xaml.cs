using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mixtri.Core.Interop;
using Mixtri_App.Helpers;
using Mixtri_App.Pages;
using Windows.Graphics;

namespace Mixtri_App;

public sealed partial class MainWindow : Window
{
    /// <summary>Exposes the navigation frame so App can reach the current page.</summary>
    public Frame ContentFrame => NavFrame;
    private Type? _parkedPageType;
    private string? _parkedNavigationState;
    internal string? ParkedNavigationState => _parkedNavigationState;
    internal Type? ParkedPageType => _parkedPageType;
    private bool _restoringNavigation;
    private bool? _resourceVisible;
    private bool _releasedToTray;
    private bool _usedEditorGraphics;
    private bool _navigationInProgress;
    private int _navigationGeneration;
    private bool _inactiveReleasePending;
    internal void MarkEditorGraphicsUsed() => _usedEditorGraphics = true;

    public bool IsForegroundVisible
    {
        get
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            return NativeMethods.IsWindowVisible(hwnd) && !NativeMethods.IsIconic(hwnd);
        }
    }

    public async Task UpdateResourceVisibilityAsync(bool visible)
    {
        if (!visible && _firstPresentation is null && App.Current.EditorProcesses is { IsRecorder: false }) return;
        if (_releasedToTray) return;
        if (_resourceVisible == visible) return;
        _resourceVisible = visible;
        try
        {
            if (visible) RestoreInactiveContent();
            EditorPage? editor = NavFrame.Content as EditorPage;
            bool hadEditor = editor is not null;
            if (editor is not null)
                await editor.SetPreviewVisibilityAsync(visible);
            if (_resourceVisible == visible && NavFrame.Content is OpenProjectsPage gallery)
                gallery.SetImageResourceVisibility(visible);
            editor = null; // Do not keep the unloaded page alive across the reclamation await.
            if (!visible && _resourceVisible == false) ReleaseInactiveContent();
            if (!visible)
            {
                await Task.Delay(250);
                if (_resourceVisible == false
                    && !Services.ProjectService.Instance.IsSaveInFlight
                    && Services.ShellCoordinator.Instance?.CurrentState != Mixtri.Core.Shell.AppShellState.Recording
                    && NavFrame.Content is not EditorPage { ExportVM.IsExporting: true })
                {
                    // MediaClip and other WinRT wrappers are not IDisposable. Reclaim their
                    // unreachable native buffers once after teardown, not on a timer or via
                    // working-set trimming (which would merely page out live allocations).
                    await Task.Run(() =>
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                    });
                    if ((hadEditor || _usedEditorGraphics) && _resourceVisible == false)
                    {
                        var device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
                        if (Services.ProjectService.Instance.CurrentProject is null && NavFrame.Content is null)
                        {
                            _releasedToTray = true;
                            if (Services.ShellCoordinator.Instance?.ReleaseIdleMainWindow(this, device) == true) return;
                            _releasedToTray = false;
                        }
                        device.Trim();
                    }
                }
            }
            Mixtri.Core.Diagnostics.DiagLog.Write("Shell",
                visible ? "window resources restored" : "window resources parked");
        }
        catch (Exception ex)
        {
            _releasedToTray = false;
            _resourceVisible = null;
            Mixtri.Core.Diagnostics.DiagLog.Write("Shell", $"resource visibility change failed: {ex}");
        }
    }

    public void ReleaseInactiveContent()
    {
        if (_navigationInProgress || _restoringNavigation)
        {
            _inactiveReleasePending = true;
            return;
        }
        if (Services.ProjectService.Instance.CurrentProject is not null
            || Services.ProjectService.Instance.IsSaveInFlight
            || Services.ProjectService.Instance.OpenInFlightPath is not null
            || Services.ShellCoordinator.Instance?.CurrentState == Mixtri.Core.Shell.AppShellState.Recording
            || NavFrame.Content is EditorPage { ExportVM.IsExporting: true })
            return;

        _parkedPageType = typeof(RecordingPage);
        _parkedNavigationState = null;
        NavFrame.Content = null;
        NavFrame.BackStack.Clear();
        NavFrame.ForwardStack.Clear();
        SelectNavigationItem("record");
    }

    internal void RestoreNavigation(string? state, Type? pageType)
    {
        _restoringNavigation = true;
        try
        {
            if (state is not null) NavFrame.SetNavigationState(state);
            else if (pageType is not null) NavFrame.Navigate(pageType);
            var current = NavFrame.CurrentSourcePageType;
            string tag = current == typeof(EditorPage) ? "editor"
                : current == typeof(OpenProjectsPage) ? "projects"
                : current == typeof(SettingsPage) ? "settings" : "record";
            SelectNavigationItem(tag);
        }
        finally { _restoringNavigation = false; }
    }

    private void SelectNavigationItem(string tag)
    {
        bool restoring = _restoringNavigation;
        _restoringNavigation = true;
        try
        {
            NavView.SelectedItem = null;
            NavigationViewItem? selected = null;
            foreach (var item in NavView.MenuItems.Concat(NavView.FooterMenuItems).OfType<NavigationViewItem>())
            {
                item.IsSelected = false;
                if (item.Tag as string == tag) selected = item;
            }
            NavView.SelectedItem = selected;
        }
        finally { _restoringNavigation = restoring; }
    }

    public void RestoreInactiveContent()
    {
        if (NavFrame.Content is null && _parkedPageType is { } pageType)
        {
            _parkedPageType = null;
            bool stackEnabled = NavFrame.IsNavigationStackEnabled;
            NavFrame.IsNavigationStackEnabled = false;
            try { RestoreNavigation(null, pageType); }
            finally { NavFrame.IsNavigationStackEnabled = stackEnabled; }
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        NavFrame.Navigating += (_, _) => { _navigationInProgress = true; _navigationGeneration++; };
        NavFrame.Navigated += (_, _) => QueueNavigationCompleted();
        NavFrame.NavigationStopped += (_, _) => QueueNavigationCompleted();
        NavFrame.NavigationFailed += (_, _) => QueueNavigationCompleted();

        // Reflects the package manifest, so a locally deployed build reads "Mixtri (dev)".
        Title = AppBranding.DisplayName;
        AppTitleBar.Title = AppBranding.DisplayName;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Minimum window size: 1024×768
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1024 * scale), (int)(768 * scale)));
        SetMinSize(1024, 768, scale);
        InitializePlacement();


    }

    private void QueueNavigationCompleted()
    {
        int generation = _navigationGeneration;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (generation != _navigationGeneration) return;
            _navigationInProgress = false;
            var page = NavFrame.Content?.GetType();
            if (page is not null)
                SelectNavigationItem(page == typeof(EditorPage) ? "editor"
                    : page == typeof(OpenProjectsPage) ? "projects"
                    : page == typeof(SettingsPage) ? "settings" : "record");
            if (!_inactiveReleasePending) return;
            _inactiveReleasePending = false;
            if (!_releasedToTray && _resourceVisible == false && !IsForegroundVisible)
                ReleaseInactiveContent();
        });
    }

    private void SetMinSize(int minWidth, int minHeight, double scale)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var presenter = AppWindow.Presenter as OverlappedPresenter;
        // Use subclassing to enforce min size
        _minWidth = (int)(minWidth * scale);
        _minHeight = (int)(minHeight * scale);
        _originalWndProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC,
            System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_wndProcDelegate = new WndProcDelegate(WndProc)));
    }

    private int _minWidth;
    private int _minHeight;
    private IntPtr _originalWndProc;
    private WndProcDelegate? _wndProcDelegate;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        const uint WM_GETMINMAXINFO = 0x0024;
        const uint WM_SHOWWINDOW = 0x0018;
        const uint WM_SIZE = 0x0005;
        const uint WM_WINDOWPOSCHANGED = 0x0047;
        const uint WM_QUERYENDSESSION = 0x0011;
        const uint WM_ENDSESSION = 0x0016;

        if (msg is WM_SHOWWINDOW or WM_SIZE or WM_WINDOWPOSCHANGED)
        {
            // SW_HIDE/SW_MINIMIZE do not reliably raise Window.VisibilityChanged.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_releasedToTray) _ = UpdateResourceVisibilityAsync(IsForegroundVisible);
            });
        }

        if (msg == WM_GETMINMAXINFO)
        {
            var info = System.Runtime.InteropServices.Marshal.PtrToStructure<MINMAXINFO>(lParam);
            info.ptMinTrackSize.X = _minWidth;
            info.ptMinTrackSize.Y = _minHeight;
            System.Runtime.InteropServices.Marshal.StructureToPtr(info, lParam, false);
            return IntPtr.Zero;
        }

        // Any OS-initiated session/quiesce signal: tell the OS we can close
        // and start a bounded shutdown so we never exceed the quiesce timeout.
        // This covers logoff, shutdown, MSIX update (ENDSESSION_CLOSEAPP), and
        // Task Manager's "End task" path.
        if (msg == WM_QUERYENDSESSION)
        {
            App.Current.BeginQuiesce();
            return new IntPtr(1);
        }

        if (msg == WM_ENDSESSION && wParam != IntPtr.Zero)
        {
            App.Current.BeginQuiesce();
            return IntPtr.Zero;
        }

        return CallWindowProc(_originalWndProc, hwnd, msg, wParam, lParam);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    private const int GWLP_WNDPROC = -4;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int nIndex, IntPtr dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        NavFrame.GoBack();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_restoringNavigation) return;
        // The built-in settings item is disabled in favour of a footer item that matches
        // the rail's icon-over-label layout, so everything arrives here by Tag.
        if (args.SelectedItem is NavigationViewItem item)
        {
            switch (item.Tag)
            {
                case "record":
                    ShowRecord();
                    break;
                case "editor":
                    ShowEditor();
                    break;
                case "projects":
                    NavFrame.Navigate(typeof(OpenProjectsPage));
                    break;
                case "settings":
                    NavFrame.Navigate(typeof(SettingsPage));
                    break;
                default:
                    System.Diagnostics.Debug.WriteLine($"[MainWindow] Unknown navigation item tag: {item.Tag}");
                    break;
            }
        }
    }

    private void CollapseToMiniButton_Click(object sender, RoutedEventArgs e)
        => Services.ShellCoordinator.Instance?.CollapseToMini();

    /// <summary>
    /// Navigates to the editor and moves the rail selection with it, so the shell
    /// doesn't end up showing the editor while "Record" is still highlighted.
    /// </summary>
    public void ShowEditor()
    {
        if (App.Current.EditorProcesses is { IsRecorder: true } processes)
        {
            _ = processes.OpenEditorAsync();
            return;
        }
        SelectNavigationItem("editor");
        if (NavFrame.Content is not EditorPage) NavFrame.Navigate(typeof(EditorPage));
    }

    public void ShowRecord(bool append = false)
    {
        SelectNavigationItem("record");
        if (append || NavFrame.Content is not RecordingPage)
            NavFrame.Navigate(typeof(RecordingPage), append ? "append" : null);
    }

    /// <summary>Surfaces a message on the shell-level InfoBar.</summary>
    public void ShowShellMessage(string message, InfoBarSeverity severity)
    {
        if (ShellInfoBar is null) return;
        ShellInfoBar.Severity = severity;
        ShellInfoBar.Title = string.Empty;
        ShellInfoBar.Message = message;
        ShellInfoBar.IsOpen = true;
    }

    /// <summary>Surfaces a recording failure on the shell-level InfoBar.</summary>
    public void ShowRecordingError(string message)
        => ShowShellMessage(message, InfoBarSeverity.Error);
}
