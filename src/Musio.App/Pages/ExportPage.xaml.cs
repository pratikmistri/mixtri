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
        var mainWindow = App.Current.MainAppWindow;
        if (mainWindow is not null)
        {
            ViewModel.WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(mainWindow);
        }
    }
}
