using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using Mixtri.Core.Capture;
using Windows.Storage.Streams;

namespace Mixtri_App.ViewModels;

/// <summary>
/// Wraps a <see cref="WindowInfo"/> with a lazily-loaded app icon for display in the UI.
/// </summary>
public partial class WindowItem : ObservableObject
{
    public WindowInfo Info { get; }
    public string Title => Info.Title;
    public string ProcessName => Info.ProcessName;

    [ObservableProperty]
    private BitmapImage? _icon;

    /// <summary>Visibility for the fallback generic icon — collapsed when app icon is loaded.</summary>
    public Microsoft.UI.Xaml.Visibility FallbackIconVisibility =>
        Icon is not null ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    partial void OnIconChanged(BitmapImage? value)
    {
        OnPropertyChanged(nameof(FallbackIconVisibility));
    }

    public WindowItem(WindowInfo info)
    {
        Info = info;
    }

    /// <summary>
    /// Extracts the app icon from the process executable on a background thread,
    /// then creates the BitmapImage on the calling (UI) thread.
    /// </summary>
    public async Task LoadIconAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(Info.ExecutablePath))
            return;

        try
        {
            var pngBytes = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(Info.ExecutablePath);
                if (icon is null) return null;

                using var bmp = icon.ToBitmap();
                using var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return ms.ToArray();
            }, ct);

            if (pngBytes is null || ct.IsCancellationRequested)
                return;

            var bitmapImage = new BitmapImage();
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(pngBytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            await bitmapImage.SetSourceAsync(stream);

            Icon = bitmapImage;
        }
        catch (OperationCanceledException) { }
        catch { /* Icon extraction is best-effort */ }
    }
}
