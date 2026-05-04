using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Musio.Core.Capture;
using Musio.Core.Settings;

namespace Musio_App.Pages;

public sealed partial class SettingsPage : Page
{
    private List<WebcamDeviceInfo> _webcamDevices = [];
    private bool _suppressWebcamEvents;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
    }

    private async void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        SystemAudioToggle.IsOn = AppSettings.Instance.IsSystemAudioEnabled;
        MicToggle.IsOn = AppSettings.Instance.IsMicEnabled;

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
                WebcamDeviceCombo.SelectedIndex = 0;
        }
        catch { /* no webcam devices available */ }
        finally
        {
            _suppressWebcamEvents = false;
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
