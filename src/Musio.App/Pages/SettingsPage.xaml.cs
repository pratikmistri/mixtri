using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Musio.Core.Capture;
using Musio.Core.Settings;

namespace Musio_App.Pages;

public sealed partial class SettingsPage : Page
{
    private List<WebcamDeviceInfo> _webcamDevices = [];
    private bool _suppressWebcamEvents;
    private bool _suppressExportDefaultEvents;
    private bool _suppressStartupModeEvents;
    private bool _suppressCaptureQualityEvents;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
    }

    private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        SystemAudioToggle.IsOn = AppSettings.Instance.IsSystemAudioEnabled;
        MicToggle.IsOn = AppSettings.Instance.IsMicEnabled;

        _suppressStartupModeEvents = true;
        try
        {
            SelectComboBoxByTag(StartupModeCombo, ShellSettings.Instance.StartupMode.ToString());
        }
        finally
        {
            _suppressStartupModeEvents = false;
        }

        _suppressExportDefaultEvents = true;
        try
        {
            SelectComboBoxByTag(ExportResolutionCombo,
                AppSettings.Instance.DefaultExportResolution.ToString());
            SelectComboBoxByTag(ExportQualityCombo,
                AppSettings.Instance.DefaultExportQuality.ToString());
        }
        finally
        {
            _suppressExportDefaultEvents = false;
        }

        _suppressCaptureQualityEvents = true;
        try
        {
            SelectComboBoxByTag(CaptureQualityCombo,
                AppSettings.Instance.CaptureQuality.ToString());
        }
        finally
        {
            _suppressCaptureQualityEvents = false;
        }

        _suppressWebcamEvents = true;
        WebcamToggle.IsOn = AppSettings.Instance.IsWebcamEnabled;

        try
        {
            _webcamDevices = await WebcamCaptureEngine.GetDevicesAsync();
            WebcamDeviceCombo.ItemsSource = _webcamDevices;

            var savedId = AppSettings.Instance.WebcamDeviceId;
            var match = _webcamDevices.FindIndex(d => d.Id == savedId);
            if (match >= 0)
                WebcamDeviceCombo.SelectedIndex = match;
            else if (_webcamDevices.Count > 0)
            {
                WebcamDeviceCombo.SelectedIndex = 0;
                AppSettings.Instance.WebcamDeviceId = _webcamDevices[0].Id;
            }
        }
        catch { /* no webcam devices available */ }
        finally
        {
            _suppressWebcamEvents = false;
        }
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

    private void SystemAudioToggle_Toggled(object sender, RoutedEventArgs e)
    {
        AppSettings.Instance.IsSystemAudioEnabled = SystemAudioToggle.IsOn;
    }

    private void MicToggle_Toggled(object sender, RoutedEventArgs e)
    {
        AppSettings.Instance.IsMicEnabled = MicToggle.IsOn;
    }

    private void WebcamToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressWebcamEvents) return;
        AppSettings.Instance.IsWebcamEnabled = WebcamToggle.IsOn;
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
            && Enum.TryParse<Musio.Core.Shell.StartupMode>(item.Tag?.ToString(), out var mode))
        {
            ShellSettings.Instance.StartupMode = mode;
        }
    }

    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeSelector.SelectedItem is ComboBoxItem item)
        {
            var content = item.Content?.ToString();
            if (this.XamlRoot?.Content is FrameworkElement root)
            {
                root.RequestedTheme = content switch
                {
                    "Light" => ElementTheme.Light,
                    "Dark" => ElementTheme.Dark,
                    _ => ElementTheme.Default,
                };
            }
        }
    }
}
