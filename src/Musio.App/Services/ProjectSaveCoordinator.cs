using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Musio.Core.Diagnostics;
using Musio.Core.Projects;
using Musio_App.Helpers;

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
    /// <summary>What the user chose when asked to save before an action that loses edits.</summary>
    public enum UnsavedChangesDecision
    {
        /// <summary>Keep working: the caller must abandon whatever it was about to do.</summary>
        Cancel,

        /// <summary>Go ahead and lose the edits.</summary>
        Discard,

        /// <summary>Go ahead; the project is safely on disk.</summary>
        Saved,
    }

    /// <summary>
    /// True while an unsaved-changes prompt is on screen anywhere in the app. The window's
    /// X stays clickable behind a <see cref="ContentDialog"/>, and dismissing the window out
    /// from under the question would hide the dialog while it still owns the decision — the
    /// exact stranded state that made the tray's Exit appear to do nothing.
    /// </summary>
    public static bool IsPromptActive { get; private set; }

    /// <summary>
    /// Asks whether to save before something that would discard the current edits — closing
    /// the window, exiting from the tray, or closing the project.
    /// </summary>
    /// <param name="question">
    /// The action being confirmed, e.g. "before closing?" — the only part that differs
    /// between callers.
    /// </param>
    /// <remarks>
    /// Reports <see cref="UnsavedChangesDecision.Saved"/> only once the save has actually
    /// succeeded: a cancelled picker or a failed write comes back as
    /// <see cref="UnsavedChangesDecision.Cancel"/>, never as "go ahead", because this is the
    /// last moment the edits can be rescued. Cancel and Esc (which reports the same result)
    /// both mean "keep working".
    /// </remarks>
    public static async Task<UnsavedChangesDecision> PromptUnsavedChangesAsync(
        XamlRoot root, Window? window, string question)
    {
        var dialog = new ContentDialog
        {
            Title = "You have unsaved changes",
            Content = $"Do you want to save this project {question}",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don't save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };

        ContentDialogResult choice;
        IsPromptActive = true;
        try
        {
            choice = await dialog.ShowAsync();
        }
        finally
        {
            // Cleared before the save runs: the picker that follows is a window of its own,
            // and holding the flag across it would leave the app unclosable for as long as
            // the user browsed for a folder.
            IsPromptActive = false;
        }

        if (choice == ContentDialogResult.None) return UnsavedChangesDecision.Cancel;
        if (choice == ContentDialogResult.Secondary) return UnsavedChangesDecision.Discard;

        return await SaveAsync(root, window)
            ? UnsavedChangesDecision.Saved
            : UnsavedChangesDecision.Cancel;
    }

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
                await DialogHelper.ShowErrorAsync(root, "Could not save project", ex.Message);
            }
            catch
            {
                // The window may already be tearing down; the failure is logged either way.
            }
            return false;
        }
    }

    /// <summary>
    /// Guards an action that REPLACES or discards the whole current project — opening
    /// another one, closing this one, ending the session. Returns false when the user backs
    /// out, or when the prompt itself could not be shown.
    /// </summary>
    /// <remarks>
    /// Gated on <see cref="ProjectService.HasUnrecoverableWork"/>, not on the dirty flag: a
    /// freshly captured recording is deliberately clean but has never been written anywhere,
    /// so replacing it destroys the whole take with nothing to reopen it from — no Recents
    /// entry, no autosave. Every failure to ASK is treated as a refusal: a dialog failure
    /// (WinUI refuses a second <see cref="ContentDialog"/>) and a missing
    /// <see cref="XamlRoot"/> both abandon the action, because proceeding would discard the
    /// work precisely because the guard protecting it could not run.
    /// </remarks>
    public static async Task<bool> ConfirmDiscardCurrentProjectAsync(
        XamlRoot? root, Window? window, string question)
    {
        // Nothing to lose — the action is free to proceed without asking anything.
        if (!ProjectService.Instance.HasUnrecoverableWork) return true;

        // There IS something to lose and no way to ask about it. Fail closed.
        if (root is null)
        {
            DiagLog.Write("Shell", "Discard-project prompt has no XamlRoot; action abandoned.");
            return false;
        }

        try
        {
            var decision = await PromptUnsavedChangesAsync(root, window, question);
            return decision != UnsavedChangesDecision.Cancel;
        }
        catch (Exception ex)
        {
            DiagLog.Write("Shell", $"Discard-project prompt failed; action abandoned: {ex.Message}");
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
