using Windows.Storage;
using Windows.Storage.Pickers;

namespace Mixtri_App.Helpers;

/// <summary>
/// Builds and shows a <see cref="FileOpenPicker"/> against the app's main window, replacing
/// the repeated "construct picker → set start location/view mode/filters → initialize with
/// the main window's hwnd → pick" blocks scattered across the wallpaper, import-video, and
/// slide-image call sites (an unpackaged WinUI 3 picker throws without a window handle).
/// Each call site's own try/catch around the result is left in place — they differ (log and
/// continue, silent no-op, or none at all) — so this method lets exceptions from
/// <see cref="FileOpenPicker.PickSingleFileAsync"/> propagate rather than swallowing them.
/// </summary>
internal static class PickerHelper
{
    public static async Task<StorageFile?> PickSingleFileAsync(
        PickerLocationId suggestedStartLocation,
        IEnumerable<string> fileTypeFilter,
        PickerViewMode? viewMode = null)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = suggestedStartLocation };
        if (viewMode is { } mode) picker.ViewMode = mode;
        foreach (var ext in fileTypeFilter) picker.FileTypeFilter.Add(ext);

        var window = App.Current.MainAppWindow;
        if (window is not null)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        return await picker.PickSingleFileAsync();
    }
}
