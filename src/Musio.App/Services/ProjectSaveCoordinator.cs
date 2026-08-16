using Microsoft.UI.Xaml;
using Musio.Core.Diagnostics;
using Musio.Core.Projects;

namespace Musio_App.Services;

/// <summary>
/// Saving a project from outside the editor page — specifically the "you have unsaved
/// changes" prompt raised when the window is dismissed.
/// </summary>
/// <remarks>
/// The editor has its own save entry point that also drives its toolbar button and
/// confirmation flyout. This one exists because the shutdown path has neither: it may run
/// while the editor is not even the current page, and it needs a plain "did the save
/// happen?" answer to decide whether to continue closing. The file-picker configuration is
/// shared with the editor so the two cannot drift on file type or default location.
/// </remarks>
public static class ProjectSaveCoordinator
{
    /// <summary>
    /// Saves the current project, prompting for a location when it has never been saved.
    /// </summary>
    /// <returns>
    /// True when the project is safely on disk (or there was nothing to save). False when the
    /// user backed out of the picker or the write failed — callers treat that as "do not
    /// close", because it is the last moment the edits can be rescued.
    /// </returns>
    public static async Task<bool> SaveAsync(XamlRoot root, Window? window)
    {
        var service = ProjectService.Instance;
        if (service.CurrentProject is null) return true;

        try
        {
            var targetPath = service.CurrentPackagePath
                ?? await PickSavePathAsync(service.CurrentProject.Name, window);

            if (targetPath is null) return false;

            await service.SavePackageAsync(targetPath);
            return true;
        }
        catch (Exception ex)
        {
            DiagLog.Write("Shell", $"Save before close failed: {ex}");
            try
            {
                await Helpers.DialogHelper.ShowErrorAsync(root, "Could not save project", ex.Message);
            }
            catch
            {
                // The window may already be tearing down; the failure is logged either way.
            }
            return false;
        }
    }

    /// <summary>Shows the standard <c>.musio</c> save picker, returning null when cancelled.</summary>
    public static async Task<string?> PickSavePathAsync(string projectName, Window? window)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.VideosLibrary,
            SuggestedFileName = SanitizeFileName(projectName),
        };
        picker.FileTypeChoices.Add("Musio project", [MusioPackage.FileExtension]);

        // A picker with no owning window never appears in a packaged app.
        window ??= App.Current.MainAppWindow;
        if (window is not null)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }

        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    /// <summary>
    /// Keeps the editor's long-standing behaviour: invalid characters become hyphens rather
    /// than vanishing, so a name like "Demo: v2" stays readable as "Demo- v2".
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Musio project";

        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '-');

        return name;
    }
}
