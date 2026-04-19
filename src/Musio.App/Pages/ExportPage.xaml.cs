using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Musio_App.ViewModels;

namespace Musio_App.Pages;

public sealed partial class ExportPage : Page
{
    public ExportViewModel ViewModel { get; }

    public ExportPage()
    {
        ViewModel = new ExportViewModel();
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Pass window handle to ViewModel for file picker initialization
        var window = (Application.Current as App)?.GetType()
            .GetField("_window", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(Application.Current) as Window;

        if (window is not null)
        {
            ViewModel.WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        }
    }
}
