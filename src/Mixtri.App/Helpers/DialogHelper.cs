using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Mixtri_App.Helpers;

public static class DialogHelper
{
    /// <summary>
    /// The theme the app is showing, taken from the root element of the window that owns
    /// <paramref name="root"/>. A ContentDialog does not inherit the window's
    /// <c>RequestedTheme</c>, so without this it renders in the system theme even when the
    /// user picked the opposite one in Settings.
    /// </summary>
    public static ElementTheme ThemeOf(XamlRoot? root)
        => root?.Content is FrameworkElement element ? element.RequestedTheme : ElementTheme.Default;

    /// <summary>Applies the app theme to a dialog whose <c>XamlRoot</c> is already set.</summary>
    public static ContentDialog WithAppTheme(this ContentDialog dialog)
    {
        dialog.RequestedTheme = ThemeOf(dialog.XamlRoot);
        return dialog;
    }

    public static async Task<bool> ShowConfirmAsync(XamlRoot root, string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = "Yes",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = root,
            RequestedTheme = ThemeOf(root)
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    public static async Task ShowErrorAsync(XamlRoot root, string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = root,
            RequestedTheme = ThemeOf(root)
        };

        await dialog.ShowAsync();
    }

    public static async Task ShowInfoAsync(XamlRoot root, string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = root,
            RequestedTheme = ThemeOf(root)
        };

        await dialog.ShowAsync();
    }

    /// <summary>
    /// Builds (but does not show) a modal progress dialog with a Cancel button wired to
    /// <paramref name="cts"/>. Returns the dialog and its <see cref="ProgressBar"/> so the
    /// caller can drive it from an <see cref="IProgress{T}"/> and control Show/Hide timing
    /// itself — unlike <see cref="ShowConfirmAsync"/>/<see cref="ShowErrorAsync"/>/
    /// <see cref="ShowInfoAsync"/>, a progress dialog needs to stay open across an
    /// awaited long-running operation rather than being shown and awaited in one call.
    /// </summary>
    public static (ContentDialog Dialog, ProgressBar Bar) BuildProgressDialog(
        XamlRoot root, string title, string message, CancellationTokenSource cts)
    {
        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Width = 260,
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(bar);

        var dialog = new ContentDialog
        {
            Title = title,
            Content = panel,
            CloseButtonText = "Cancel",
            XamlRoot = root,
            RequestedTheme = ThemeOf(root),
        };
        // Closing (via the Cancel button or Esc) requests cancellation; the caller's awaited
        // operation observes the token and is expected to throw OperationCanceledException.
        dialog.CloseButtonClick += (_, _) => cts.Cancel();
        return (dialog, bar);
    }
}
