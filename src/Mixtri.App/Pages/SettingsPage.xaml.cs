using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Mixtri.Core.Capture;
using Mixtri.Core.Settings;
using Mixtri_App.Helpers;
using Mixtri_App.ViewModels;

namespace Mixtri_App.Pages;

public sealed partial class SettingsPage : Page
{
    private List<WebcamDeviceInfo> _webcamDevices = [];
    private bool _suppressWebcamEvents;
    private bool _suppressExportDefaultEvents;
    private bool _suppressStartupModeEvents;
    private bool _suppressCaptureQualityEvents;
    private bool _suppressAudioEvents;
    private bool _suppressThemeEvents;

    public SettingsPage()
    {
        InitializeComponent();
        // Reflects the package manifest, so a locally deployed build reads "Mixtri (dev)".
        AboutAppNameText.Text = AppBranding.DisplayName;
        Loaded += SettingsPage_Loaded;
    }

    private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        // Read from the shared recording view model, which is what a recording actually
        // uses; it persists its own changes to AppSettings.
        var recording = RecordingViewModel.Shared;
        using (SuppressScope.Enter(ref _suppressAudioEvents))
        {
            SystemAudioToggle.IsOn = recording.IsSystemAudioEnabled;
            MicToggle.IsOn = recording.IsMicEnabled;
        }

        using (SuppressScope.Enter(ref _suppressThemeEvents))
        {
            SelectComboBoxByTag(ThemeSelector, NormalizeThemeName(AppSettings.Instance.Theme));
        }

        using (SuppressScope.Enter(ref _suppressStartupModeEvents))
        {
            SelectComboBoxByTag(StartupModeCombo, ShellSettings.Instance.StartupMode.ToString());
        }

        using (SuppressScope.Enter(ref _suppressExportDefaultEvents))
        {
            SelectComboBoxByTag(ExportResolutionCombo,
                AppSettings.Instance.DefaultExportResolution.ToString());
            SelectComboBoxByTag(ExportQualityCombo,
                AppSettings.Instance.DefaultExportQuality.ToString());
        }

        using (SuppressScope.Enter(ref _suppressCaptureQualityEvents))
        {
            SelectComboBoxByTag(CaptureQualityCombo,
                AppSettings.Instance.CaptureQuality.ToString());
        }

        using (SuppressScope.Enter(ref _suppressWebcamEvents))
        {
            WebcamToggle.IsOn = recording.IsWebcamEnabled;
        }
        UpdateMiniHotkeyStatus();

        // Enumeration is awaited with events live: a toggle the user flips meanwhile must
        // not be swallowed. Only the synchronous assignments below are suppressed.
        List<WebcamDeviceInfo> devices;
        try
        {
            devices = await WebcamCaptureEngine.GetDevicesAsync();
        }
        catch { return; /* no webcam devices available */ }

        using (SuppressScope.Enter(ref _suppressWebcamEvents))
        {
            _webcamDevices = devices;
            WebcamDeviceCombo.ItemsSource = _webcamDevices;

            var savedId = AppSettings.Instance.WebcamDeviceId;
            var match = _webcamDevices.FindIndex(d => d.Id == savedId);
            if (match >= 0)
            {
                WebcamDeviceCombo.SelectedIndex = match;
            }
            else if (!string.IsNullOrWhiteSpace(savedId))
            {
                // The chosen camera is unplugged right now. Leave the saved choice alone —
                // overwriting it with whatever camera happens to be first would silently
                // lose the user's pick the moment they plug it back in.
                WebcamDeviceCombo.SelectedIndex = -1;
                WebcamDeviceCombo.PlaceholderText = "Selected camera is not connected";
            }
            // No saved choice: leave the "Default camera" placeholder; recording uses the
            // system default until the user picks one.
        }
    }

    private void UpdateMiniHotkeyStatus()
    {
        if (App.Current.IsMiniHotkeyRegistered)
        {
            MiniHotkeyInfoBar.Severity = InfoBarSeverity.Informational;
            MiniHotkeyInfoBar.Message =
                "Win+Shift+X reveals Mini mode from any app; it never starts or stops recording.";
            return;
        }

        MiniHotkeyInfoBar.Severity = InfoBarSeverity.Warning;
        MiniHotkeyInfoBar.Message =
            "Win+Shift+X is not active in this app window. It may be reserved by Windows or another app.";
    }

    private static void SelectComboBoxByTag(ComboBox combo, string tag)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item
                && string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private void ExportResolutionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressExportDefaultEvents) return;
        if (ExportResolutionCombo.SelectedItem is ComboBoxItem item
            && Enum.TryParse<VideoResolution>(item.Tag?.ToString(), out var resolution))
        {
            AppSettings.Instance.DefaultExportResolution = resolution;
        }
    }

    private void ExportQualityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressExportDefaultEvents) return;
        if (ExportQualityCombo.SelectedItem is ComboBoxItem item
            && Enum.TryParse<VideoQuality>(item.Tag?.ToString(), out var quality))
        {
            AppSettings.Instance.DefaultExportQuality = quality;
        }
    }

    private void CaptureQualityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCaptureQualityEvents) return;
        if (CaptureQualityCombo.SelectedItem is ComboBoxItem item
            && Enum.TryParse<CaptureQuality>(item.Tag?.ToString(), out var quality))
        {
            AppSettings.Instance.CaptureQuality = quality;
        }
    }

    // These write through the shared recording view model rather than AppSettings directly:
    // the view model caches the values for the session, so writing only the setting left the
    // next recording (and the toolbar) using the old value until restart.
    private void SystemAudioToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressAudioEvents || SystemAudioToggle is null) return;
        RecordingViewModel.Shared.IsSystemAudioEnabled = SystemAudioToggle.IsOn;
    }

    private void MicToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressAudioEvents || MicToggle is null) return;
        RecordingViewModel.Shared.IsMicEnabled = MicToggle.IsOn;
    }

    private void WebcamToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressWebcamEvents || WebcamToggle is null) return;
        RecordingViewModel.Shared.IsWebcamEnabled = WebcamToggle.IsOn;
    }

    private void WebcamDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressWebcamEvents) return;
        if (WebcamDeviceCombo.SelectedItem is WebcamDeviceInfo device)
            AppSettings.Instance.WebcamDeviceId = device.Id;
    }

    private void StartupModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressStartupModeEvents) return;
        if (StartupModeCombo.SelectedItem is ComboBoxItem item
            && Enum.TryParse<Mixtri.Core.Shell.StartupMode>(item.Tag?.ToString(), out var mode))
        {
            ShellSettings.Instance.StartupMode = mode;
        }
    }

    /// <summary>Persists the theme and applies it to every app window (full and Mini).</summary>
    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressThemeEvents || ThemeSelector is null) return;
        if (ThemeSelector.SelectedItem is ComboBoxItem item)
        {
            var theme = NormalizeThemeName(item.Tag?.ToString());
            AppSettings.Instance.Theme = theme;
            App.Current.ApplyTheme(theme);
        }
    }

    private static string NormalizeThemeName(string? theme) => theme switch
    {
        "Light" => "Light",
        "Dark" => "Dark",
        _ => "System",
    };
}
