using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Musio_App.Pages;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
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
