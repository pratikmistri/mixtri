using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Musio_App.Services;
using Musio.Core.Services;

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

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();

        _trayService = new SystemTrayService();
        _trayService.Initialize(_window);
        _trayService.Show();

        _trayService.ShowWindowRequested += OnShowWindowRequested;
        _trayService.ExitRequested += OnExitRequested;

        // Global hotkeys
        _hotkeyService = new GlobalHotkeyService();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        _hotkeyService.Initialize(hwnd);

        // Ctrl+Shift+R → Start/Stop Recording (VK_R = 0x52)
        _hotkeyService.RegisterHotkey(
            GlobalHotkeyService.StartStopRecording,
            ModifierKeys.Ctrl | ModifierKeys.Shift, 0x52);

        // Ctrl+Shift+P → Pause/Resume Recording (VK_P = 0x50)
        _hotkeyService.RegisterHotkey(
            GlobalHotkeyService.PauseResumeRecording,
            ModifierKeys.Ctrl | ModifierKeys.Shift, 0x50);

        // Ctrl+Shift+S → Take Screenshot (VK_S = 0x53)
        _hotkeyService.RegisterHotkey(
            GlobalHotkeyService.TakeScreenshot,
            ModifierKeys.Ctrl | ModifierKeys.Shift, 0x53);

        _hotkeyService.HotkeyPressed += OnHotkeyPressed;

        // Minimize to tray instead of closing
        _window.AppWindow.Closing += OnWindowClosing;
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
    }

    private void OnWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_isExiting || _window is null) return;

        // Minimize to tray instead of exiting
        args.Cancel = true;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        ShowWindow(hwnd, SW_HIDE);
    }

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
