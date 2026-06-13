using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Musio_App.Pages;

namespace Musio_App.Controls;

/// <summary>
/// Visual content of the full app shell: TitleBar + NavigationView + content
/// Frame. Hosted today by <see cref="MainWindow"/>; in Phase B the same
/// control will also be hosted inside the unified <c>AppShellWindow</c>.
/// </summary>
public sealed partial class FullShellControl : UserControl
{
    /// <summary>Exposes the navigation frame so the host window / App can reach the current page.</summary>
    public Frame ContentFrame => NavFrame;

    /// <summary>Title bar element so the host window can call <c>SetTitleBar</c>.</summary>
    public TitleBar TitleBarElement => AppTitleBar;

    /// <summary>
    /// Raised when the placeholder Collapse-to-Mini button is clicked. Phase A:
    /// wired to the (hidden) button so the slot exists; no host consumes it yet.
    /// </summary>
    public event EventHandler? CollapseRequested;

    public FullShellControl()
    {
        InitializeComponent();
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        NavFrame.GoBack();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavFrame.Navigate(typeof(SettingsPage));
        }
        else if (args.SelectedItem is NavigationViewItem item)
        {
            switch (item.Tag)
            {
                case "record":
                    NavFrame.Navigate(typeof(RecordingPage));
                    break;
                case "editor":
                    NavFrame.Navigate(typeof(EditorPage));
                    break;
                default:
                    System.Diagnostics.Debug.WriteLine($"[FullShellControl] Unknown navigation item tag: {item.Tag}");
                    break;
            }
        }
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e)
    {
        CollapseRequested?.Invoke(this, EventArgs.Empty);
    }
}
