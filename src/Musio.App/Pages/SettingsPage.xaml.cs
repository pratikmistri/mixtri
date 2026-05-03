using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Musio.Core.Settings;

namespace Musio_App.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
    }

    private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
    {
        SystemAudioToggle.IsOn = AppSettings.Instance.IsSystemAudioEnabled;
        MicToggle.IsOn = AppSettings.Instance.IsMicEnabled;
    }

    private void SystemAudioToggle_Toggled(object sender, RoutedEventArgs e)
    {
        AppSettings.Instance.IsSystemAudioEnabled = SystemAudioToggle.IsOn;
    }

    private void MicToggle_Toggled(object sender, RoutedEventArgs e)
    {
        AppSettings.Instance.IsMicEnabled = MicToggle.IsOn;
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
