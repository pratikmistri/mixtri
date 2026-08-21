using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Musio.Core.Media;
using Musio.Core.Models;
using Musio_App.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Musio_App.Pages;

/// <summary>
/// Dropping media files onto the editor. Video and audio files dragged from Explorer (or
/// anything else offering <see cref="StandardDataFormats.StorageItems"/>) go through exactly
/// the same import services as the Insert menu — this file only decides what each dropped file
/// is and where it lands.
/// </summary>
public sealed partial class EditorPage
{
    /// <summary>
    /// Guards against a second drop landing while an import is still running. Both import paths
    /// put up a <c>ContentDialog</c>, and only one may be shown at a time — a concurrent drop
    /// would throw out of the second <c>ShowAsync</c> rather than merely queue.
    /// </summary>
    private bool _dropImportInFlight;

    private void EditorPage_DragOver(object sender, DragEventArgs e)
    {
        if (_dropImportInFlight || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        // Deliberately NOT enumerating the items here. GetStorageItemsAsync needs a deferral
        // and runs for every pointer move of the drag; for a shell drag of a large selection,
        // or a virtual item from a cloud provider, that is a per-frame round trip. Accept on
        // the format and reject the individual files on drop, where it costs one call.
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Add to project";
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsContentVisible = true;
        e.Handled = true;

        DropHintOverlay.Visibility = Visibility.Visible;
    }

    private void EditorPage_DragLeave(object sender, DragEventArgs e)
        => DropHintOverlay.Visibility = Visibility.Collapsed;

    private async void EditorPage_Drop(object sender, DragEventArgs e)
    {
        DropHintOverlay.Visibility = Visibility.Collapsed;

        if (_dropImportInFlight || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        // Resolved BEFORE the deferral below: the drop position is only meaningful while the
        // event is being delivered, and the import that follows is seconds long.
        var insertAt = ResolveDropInsertTime(e);

        IReadOnlyList<IStorageItem> items;
        var deferral = e.GetDeferral();
        try
        {
            items = await e.DataView.GetStorageItemsAsync();
        }
        catch (Exception ex)
        {
            Musio.Core.Diagnostics.DiagLog.Write("Editor", $"drop: could not read dropped items: {ex.Message}");
            return;
        }
        finally
        {
            deferral.Complete();
        }

        await ImportDroppedFilesAsync(items.OfType<StorageFile>().ToList(), insertAt);
    }

    /// <summary>
    /// Where a dropped clip belongs: the time under the pointer when the drop lands on the
    /// timeline, and the playhead anywhere else.
    /// </summary>
    /// <remarks>
    /// A drop onto the timeline is a positional gesture — the user aimed at a moment — while a
    /// drop onto the preview expresses no position at all, so it falls back to the same anchor
    /// the Insert menu uses.
    /// </remarks>
    private TimeSpan ResolveDropInsertTime(DragEventArgs e)
    {
        var playhead = Timeline?.PlayheadPosition ?? ViewModel.Model.PlayheadPosition;
        if (Timeline is null) return playhead;

        var point = e.GetPosition(Timeline);
        bool overTimeline =
            point.X >= 0 && point.Y >= 0 &&
            point.X <= Timeline.ActualWidth && point.Y <= Timeline.ActualHeight;

        return overTimeline ? Timeline.TimeAtPoint(point) : playhead;
    }

    /// <summary>
    /// Imports every dropped file the editor understands, reporting the ones it does not
    /// understand once at the end.
    /// </summary>
    /// <remarks>
    /// Sequential rather than parallel: each import shows its own progress dialog, and both
    /// import services write into the session folder. Videos are laid out one after another by
    /// advancing the insert point past each clip, so dropping a handful of files produces a
    /// sequence rather than a stack of overlapping overlays at one instant.
    /// <para>
    /// Videos are taken before audio regardless of the order they were dropped in, because
    /// audio cannot start a project. Iterating the drop order instead meant that dropping a
    /// clip and its soundtrack together came out right or refused outright depending on which
    /// file the shell happened to list first.
    /// </para>
    /// </remarks>
    private async Task ImportDroppedFilesAsync(IReadOnlyList<StorageFile> files, TimeSpan insertAt)
    {
        if (files.Count == 0) return;

        var videos = files.Where(f => Matches(VideoImportService.SupportedExtensions, Path.GetExtension(f.Path))).ToList();
        var audio = files.Where(f => Matches(AudioImportService.SupportedExtensions, Path.GetExtension(f.Path))).ToList();
        var unsupported = files.Except(videos).Except(audio).Select(f => f.Name).ToList();

        _dropImportInFlight = true;
        try
        {
            foreach (var file in videos)
            {
                var inserted = await ImportVideoFileAsync(file.Path, insertAt);

                // Advance past what actually landed, not past the source file's length — the
                // two differ when a clip is inserted into an empty timeline, where it becomes
                // the primary and starts at zero regardless of the drop position.
                if (inserted is not null)
                    insertAt = inserted.Start + inserted.Duration;
            }

            if (audio.Count > 0)
            {
                // Audio has no frames, so it cannot start a project — the same rule the Insert
                // menu checks before it even opens its picker.
                if (ProjectService.Instance.CurrentProject is null)
                {
                    await ShowProjectDialogAsync(
                        "Could not insert audio",
                        "Record or import a video first — audio is inserted into an existing timeline.");
                }
                else
                {
                    foreach (var file in audio)
                    {
                        // Dropped audio arrives as a voice over, i.e. at full volume. The kind
                        // is unknowable from the file, and of the two the wrong guess is far
                        // cheaper this way round: narration inserted as a music bed plays at
                        // 0.35 and reads as broken, whereas music at full volume is obvious
                        // and one slider away from right.
                        await ImportAudioFileAsync(file.Path, AudioTrackKind.VoiceOver, insertAt);
                    }
                }
            }

            if (unsupported.Count > 0)
            {
                await ShowProjectDialogAsync(
                    unsupported.Count == 1 ? "Unsupported file" : "Unsupported files",
                    $"{string.Join(", ", unsupported)} could not be added.\n\n" +
                    $"Video: {string.Join(", ", VideoImportService.SupportedExtensions)}\n" +
                    $"Audio: {string.Join(", ", AudioImportService.SupportedExtensions)}");
            }
        }
        finally
        {
            _dropImportInFlight = false;
        }
    }

    private static bool Matches(IReadOnlyList<string> extensions, string? candidate) =>
        !string.IsNullOrEmpty(candidate)
        && extensions.Any(x => string.Equals(x, candidate, StringComparison.OrdinalIgnoreCase));
}
