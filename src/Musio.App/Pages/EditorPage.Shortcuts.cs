using System.ComponentModel;
using System.Globalization;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Musio.Core.Audio;
using Musio.Core.Capture;
using Musio.Core.Export;
using Musio.Core.Media;
using Musio.Core.Models;
using Musio.Core.Processing;
using Musio.Core.Projects;
using Musio.Core.Settings;
using Musio.Core.Timeline;
using Musio_App.Controls;
using Musio_App.Helpers;
using Musio_App.Services;
using Musio_App.ViewModels;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace Musio_App.Pages;

public sealed partial class EditorPage
{

    private void UndoAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsFocusOnInteractiveControl()) return;

        if (ViewModel.CanUndo)
        {
            ViewModel.UndoCommand.Execute(null);
            args.Handled = true;
        }
    }

    private void RedoAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsFocusOnInteractiveControl()) return;

        if (ViewModel.CanRedo)
        {
            ViewModel.RedoCommand.Execute(null);
            args.Handled = true;
        }
    }

    private void SplitAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsFocusOnInteractiveControl()) return;

        // A selected inserted audio block claims the split, so the same key cuts whichever
        // track the user is actually working on rather than always the primary video.
        if (Timeline.SelectedInsertedAudioTrackId is { } audioTrackId)
        {
            SplitInsertedAudioTrack(audioTrackId);
            args.Handled = true;
            return;
        }

        ViewModel.SplitAtPlayheadCommand.Execute(null);
        args.Handled = true;
    }

    private void DeleteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsFocusOnInteractiveControl()) return;

        // If an inserted voice-over/music block is selected, remove it.
        if (Timeline.SelectedInsertedAudioTrackId is { } audioTrackId)
        {
            DeleteInsertedAudioTrack(audioTrackId);
            args.Handled = true;
            return;
        }

        // If a camera segment is selected, remove it.
        if (Timeline.SelectedCameraSegmentId is { } cameraSegId)
        {
            DeleteCameraSegment(cameraSegId);
            args.Handled = true;
            return;
        }

        // If a text overlay is selected, remove it.
        if (Timeline.SelectedTextOverlayId is { } overlayId)
        {
            DeleteTextOverlay(overlayId);
            args.Handled = true;
            return;
        }

        // If a zoom segment is selected, remove it instead of deleting a clip segment
        if (Timeline.SelectedZoomKeyframeId is { } selectedId)
        {
            var operation = new RemoveZoomKeyframeOperation(selectedId);
            ViewModel.UndoRedoManager.Execute(operation);
            Timeline.ClearZoomSelection();
            UpdateZoomPanelVisibility();
            args.Handled = true;
            return;
        }

        // If a primary-track segment is selected, ripple-delete it.
        if (_selectedPrimarySegmentId is { } segId &&
            ViewModel.Model.Segments.Any(s => s.Id == segId))
        {
            DeletePrimarySegment(segId);
            args.Handled = true;
            return;
        }

        ViewModel.DeleteSelectedCommand.Execute(null);
        args.Handled = true;
    }

    private void CutAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsFocusOnInteractiveControl()) return;

        ViewModel.CutSelectionCommand.Execute(null);
        args.Handled = true;
    }

    private async void SaveAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await SaveProjectAsync(forcePrompt: false);
    }

    private async void SaveAsAccelerator_Invoked(
        KeyboardAccelerator sender,
        KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await SaveProjectAsync(forcePrompt: true);
    }

    private void PlayPauseAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsFocusOnInteractiveControl())
        {
            return;
        }

        if (Preview.IsPlaying)
        {
            Preview.Pause();
        }
        else
        {
            Preview.Play();
        }
        args.Handled = true;
    }

    private void GoToStartAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsFocusOnInteractiveControl()) return;

        GoToStart();
        args.Handled = true;
    }

    private void GoToEndAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsFocusOnInteractiveControl()) return;

        GoToEnd();
        args.Handled = true;
    }

    private void GoToStart() => SeekPreviewAndTimeline(TimeSpan.Zero);

    private void GoToEnd()
    {
        var model = ViewModel.Model;
        var duration = model.DisplayDuration;
        if (duration <= TimeSpan.Zero)
        {
            SeekPreviewAndTimeline(TimeSpan.Zero);
            return;
        }

        var fps = model.Fps > 0 ? model.Fps : 30;
        var target = duration - TimeSpan.FromSeconds(1.0 / fps);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        SeekPreviewAndTimeline(target);
    }

    private void SeekPreviewAndTimeline(TimeSpan target)
    {
        Preview.Pause();
        Timeline.PlayheadPosition = target;
        Preview.PlayheadPosition = target;
        ViewModel.Model.PlayheadPosition = target;
        _ = UpdatePreviewFrameAsync(target, force: true);
    }

    private bool IsFocusOnInteractiveControl()
    {
        DependencyObject? node = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (node is not null)
        {
            switch (node)
            {
                case TextBox:
                case PasswordBox:
                case RichEditBox:
                case AutoSuggestBox:
                case ComboBox:
                case NumberBox:
                case Microsoft.UI.Xaml.Controls.Primitives.ButtonBase:
                case ToggleSwitch:
                case Slider:
                case ColorPicker:
                case FlyoutPresenter:
                case MenuFlyoutPresenter:
                    return true;
            }
            node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    // --- Export flyout ---

    private async void ExportFlyout_Opened(object? sender, object e)
    {
        if (ExportVM.IsExporting)
        {
            ShowExportingState();
            return;
        }

        if (ExportVM.ExportSucceeded)
        {
            ShowExportedState();
            return;
        }

        if (ExportVM.ExportFailed)
        {
            ShowErrorState();
            return;
        }

        // Pause preview playback before export. The export pipeline composites
        // frames on the shared Win2D device; running it concurrently with the
        // preview's per-frame composition can corrupt output frames and crash
        // the encoder when both pull from the same source files at once.
        Preview.Pause();
        try { _audioPlayer?.Pause(); } catch { /* best-effort */ }
        try { _insertedAudioPlayer?.Pause(); } catch { /* best-effort */ }

        // Start new export
        ExportVM.PrepareForExport();
        if (!ExportVM.ExportCommand.CanExecute(null))
        {
            ExportFlyout.Hide();
            EditorInfoBar.Message = "No recording available to export.";
            EditorInfoBar.Severity = InfoBarSeverity.Warning;
            EditorInfoBar.IsOpen = true;
            return;
        }

        ShowExportingState();
        await ExportVM.ExportCommand.ExecuteAsync(null);
    }

    private void ExportFlyout_Closed(object? sender, object e)
    {
        // Reset terminal state (Exported / Failed) so the next open starts a fresh
        // export. Without this, dismissing the flyout via click-outside leaves
        // ExportSucceeded=true; reopening the flyout would then short-circuit and
        // show the prior result instead of running a new export — which makes
        // subsequent style/edit changes appear to never apply to the output file.
        if (ExportVM.IsExporting) return;
        if (!ExportVM.ExportSucceeded && !ExportVM.ExportFailed) return;
        ExportVM.PrepareForExport();
        ShowExportingState();
    }

    private void ExportVM_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ExportViewModel.IsExporting) && !ExportVM.IsExporting)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ExportVM.ExportSucceeded)
                    ShowExportedState();
                else if (ExportVM.ExportFailed)
                    ShowErrorState();
                else
                    ExportFlyout.Hide(); // Cancelled
            });
        }
    }

    private void ShowExportingState()
    {
        ExportingPanel.Visibility = Visibility.Visible;
        ExportedPanel.Visibility = Visibility.Collapsed;
        ExportErrorPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowExportedState()
    {
        ExportingPanel.Visibility = Visibility.Collapsed;
        ExportedPanel.Visibility = Visibility.Visible;
        ExportErrorPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowErrorState()
    {
        ExportingPanel.Visibility = Visibility.Collapsed;
        ExportedPanel.Visibility = Visibility.Collapsed;
        ExportErrorPanel.Visibility = Visibility.Visible;
    }

    private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
    {
        ExportVM.OpenOutputFolderCommand.Execute(null);
    }

    private void CancelExport_Click(object sender, RoutedEventArgs e)
    {
        ExportVM.CancelExportCommand.Execute(null);
    }

    private void CloseFlyout_Click(object sender, RoutedEventArgs e)
    {
        ExportFlyout.Hide();
        // Reset state so next open starts a fresh export
        ExportVM.PrepareForExport();
        ShowExportingState();
    }

    private void AddTextSlide_Click(object sender, RoutedEventArgs e)
    {
        var slide = new TextSlideSegment
        {
            Text = "Title",
            Duration = TimeSpan.FromSeconds(3),
        };

        var playhead = ViewModel.Model.PlayheadPosition;

        var operation = new InsertSegmentOnOverlayTrackOperation(slide, playhead);
        ViewModel.UndoRedoManager.Execute(operation);

        // Select the new slide and show properties
        _selectedTextSlideId = slide.Id;
        Timeline.SelectSegment(slide.Id);
        ShowTextSlidePanel(slide);

        InvalidatePreview();
    }

    /// <summary>
    /// Creates a new text overlay at the playhead's current source time (mapping through
    /// the same output→source logic the live preview uses) and selects it so its pane opens
    /// immediately. A no-op when the playhead can't be mapped to any source (no primary
    /// video loaded yet).
    /// </summary>
    private void AddTextOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (GetPlayheadSourceTimeForOverlay() is not { } mapped) return;

        var operation = new AddTextOverlayOperation(
            mapped.SourceTime, TextOverlayDefaultDuration, sourceVideoFilePath: mapped.VideoFilePath);
        ViewModel.UndoRedoManager.Execute(operation);

        Timeline.SelectedTextOverlayId = operation.CreatedId;
        _selectedTextOverlayId = operation.CreatedId;
        SyncTextOverlayUI(operation.CreatedId);
        Timeline.InvalidateAllCanvases();

        // The overlay starts at the playhead, and every animation is fully transparent on
        // its very first frame - so the thing the user just inserted would render as
        // nothing at all until they scrubbed into it, which reads as "insert is broken".
        // Park the playhead past the entrance so the new overlay is visible, selected and
        // directly editable on the preview the moment it appears.
        SeekPastOverlayEntrance(TextOverlayDefaultDuration);

        RefreshOverlayPreview();
    }

    /// <summary>Duration a newly inserted text overlay is created with.</summary>
    private static readonly TimeSpan TextOverlayDefaultDuration = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Moves the playhead from a new overlay's start to its middle, which is past the
    /// entrance animation and before the exit, so the overlay renders at full opacity.
    /// <paramref name="sourceDuration"/> is the overlay's source-time length; the offset is
    /// applied in output time, because a sped-up segment covers that source range in
    /// proportionally less output time.
    /// </summary>
    private void SeekPastOverlayEntrance(TimeSpan sourceDuration)
    {
        var model = ViewModel.Model;
        double speed = 1.0;
        VideoSegment? owning = null;

        if (model.Segments.Count > 0 &&
            model.GetSegmentAtTime(Timeline.PlayheadPosition).Segment is VideoSegment seg)
        {
            owning = seg;
            if (seg.SpeedFactor > 0) speed = seg.SpeedFactor;
        }

        var offset = TimeSpan.FromTicks((long)(sourceDuration.Ticks / 2 / speed));
        var target = Timeline.PlayheadPosition + offset;

        // Stay inside the clip the overlay belongs to. The overlay is only active over its
        // own recording, so a midpoint that spills past the clip boundary would land on the
        // next segment (another recording, or a text slide) where it renders nothing at all
        // — the very problem this seek exists to avoid. Back off just inside the end so the
        // playhead is still within the clip rather than exactly on its exclusive edge.
        if (owning is not null && target >= owning.End)
            target = owning.End - TimeSpan.FromMilliseconds(1);

        // Never run past the end of the timeline; the overlay is still partly visible
        // wherever we land, and seeking beyond the content would blank the preview.
        var end = model.TotalSegmentsDuration;
        if (end > TimeSpan.Zero && target > end)
            target = end;
        if (target < Timeline.PlayheadPosition) target = Timeline.PlayheadPosition;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;

        Preview.Pause();
        Timeline.PlayheadPosition = target;
        Preview.PlayheadPosition = target;
        model.PlayheadPosition = target;
        _ = UpdatePreviewFrameAsync(target, force: true);
    }

    /// <summary>
    /// Maps the playhead's output-time position to the source time (and owning recording)
    /// a newly-created text overlay should be authored against, mirroring the output→source
    /// mapping the live preview renders with. Returns null when there is nothing to author
    /// the overlay against (no primary video, or the playhead sits over a non-video segment
    /// such as a text slide).
    /// </summary>
    private (string? VideoFilePath, TimeSpan SourceTime)? GetPlayheadSourceTimeForOverlay()
    {
        var model = ViewModel.Model;
        var position = Timeline.PlayheadPosition;

        if (model.Segments.Count > 0)
        {
            var (segment, localOffset) = model.GetSegmentAtTime(position);
            if (segment is not VideoSegment videoSeg) return null;

            var sourceInSeg = videoSeg.SourceStart +
                TimeSpan.FromTicks((long)(localOffset.Ticks * videoSeg.SpeedFactor));

            // null SourceVideoFilePath means "the primary recording" (mirrors
            // ZoomKeyframe/TextOverlaySegment's convention), so map the primary video's own
            // segments to null rather than storing its path explicitly.
            bool isPrimary = string.Equals(videoSeg.VideoFilePath, PrimaryVideoPath, StringComparison.OrdinalIgnoreCase);
            return (isPrimary ? null : videoSeg.VideoFilePath, sourceInSeg);
        }

        // Legacy (pre-segment) projects: the whole timeline is the primary recording.
        if (string.IsNullOrEmpty(PrimaryVideoPath)) return null;
        return (null, MapToSourceTime(position));
    }

    private void RecordMore_Click(object sender, RoutedEventArgs e)
    {
        // Navigate to RecordingPage in append mode
        Preview?.Pause();
        _audioPlayer?.Stop();
        _insertedAudioPlayer?.Stop();
        Frame.Navigate(typeof(RecordingPage), "append");
    }

    /// <summary>
    /// Lets the user pick an external video file and inserts it into the project. The file is
    /// normalised by <see cref="VideoImportService"/> (transcoded to a constant-frame-rate
    /// H.264 clip, its audio extracted to WAV) so the rest of the editor can treat it exactly
    /// like an appended recording — the only difference being that it carries no cursor, click
    /// or keystroke data.
    /// </summary>
    private async void ImportVideo_Click(object sender, RoutedEventArgs e)
    {
        Windows.Storage.StorageFile? file = null;
        try
        {
            file = await PickerHelper.PickSingleFileAsync(
                Windows.Storage.Pickers.PickerLocationId.VideosLibrary,
                VideoImportService.SupportedExtensions,
                Windows.Storage.Pickers.PickerViewMode.Thumbnail);
        }
        catch (Exception ex)
        {
            Musio.Core.Diagnostics.DiagLog.Write("Editor", $"Import picker failed: {ex.Message}");
        }
        if (file is null) return;

        // Captured before the transcode: the playhead can move while a multi-second import
        // runs, and the imported clip belongs where the user was when they asked for it.
        var insertAt = Timeline?.PlayheadPosition ?? ViewModel.Model.PlayheadPosition;

        // Import transcodes the whole file, so it can run for many seconds; surface progress
        // and a cancel path rather than freezing the UI on a silent await.
        using var cts = new CancellationTokenSource();
        var (dialog, bar) = DialogHelper.BuildProgressDialog(
            XamlRoot, "Importing video", "Transcoding and importing the video…", cts);
        var progress = new Progress<double>(p =>
        {
            // Progress arrives on a background thread; marshal the bound update onto the UI.
            DispatcherQueue.TryEnqueue(() => bar.Value = Math.Clamp(p, 0, 1) * 100);
        });

        var showTask = dialog.ShowAsync();
        string? errorMessage = null;
        try
        {
            var result = await VideoImportService.ImportAsync(file.Path, null, progress, cts.Token);

            // The import staying on this page means the editor must be told to reload; the
            // service fires ProjectChanged, which the EditorViewModel turns into a
            // ModelReloaded the page already handles (timeline swap + preview re-init).
            var inserted = ProjectService.Instance.ImportVideo(result, insertAt);
            if (inserted is not null)
            {
                _selectedPrimarySegmentId = inserted.Id;
                _selectedTextSlideId = null;
                Timeline?.SelectSegment(inserted.Id);
                HideTextSlidePanel();
                InvalidatePreview();
            }
        }
        catch (OperationCanceledException)
        {
            // User cancelled — nothing to report.
        }
        catch (VideoImportException ex)
        {
            errorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            Musio.Core.Diagnostics.DiagLog.Write("Editor", $"Video import failed: {ex}");
            errorMessage = ex.Message;
        }
        finally
        {
            // Fully close the progress dialog before anything else opens one: only a single
            // ContentDialog may be shown at a time, so the error dialog below must wait for it.
            dialog.Hide();
            try { await showTask; } catch { /* dialog dismissed */ }
        }

        if (errorMessage is not null)
            await ShowProjectDialogAsync("Could not import video", errorMessage);
    }

    /// <summary>Inserts an external audio file as narration at the playhead.</summary>
    private void ImportVoiceOver_Click(object sender, RoutedEventArgs e)
        => _ = ImportAudioAsync(AudioTrackKind.VoiceOver);

    /// <summary>Inserts an external audio file as a background music bed at the playhead.</summary>
    private void ImportMusic_Click(object sender, RoutedEventArgs e)
        => _ = ImportAudioAsync(AudioTrackKind.Music);

    /// <summary>
    /// Lets the user pick an external audio file and inserts it at the playhead as an
    /// <see cref="AudioTrack"/>. The file is normalised by <see cref="AudioImportService"/>
    /// into a PCM WAV, so preview (which seeks by byte offset) and export (which trims by
    /// time) both land on the same sample.
    /// </summary>
    /// <remarks>
    /// Anchored to the playhead, and to the OUTPUT timeline, deliberately: a voice-over
    /// belongs to the moment in the finished video the user is looking at, and must stay
    /// there when the footage beneath it is later trimmed or reordered — which is exactly
    /// what separates an inserted track from the recording's own audio.
    /// </remarks>
    private async Task ImportAudioAsync(AudioTrackKind kind)
    {
        bool isMusic = kind == AudioTrackKind.Music;
        string label = isMusic ? "music" : "voice over";

        // An audio track has no frames, so unlike a video import it cannot start a project.
        // Checked BEFORE the picker so the user is not made to choose a file first.
        if (ProjectService.Instance.CurrentProject is null)
        {
            await ShowProjectDialogAsync(
                $"Could not insert {label}",
                "Open or record a project first — audio is inserted into an existing timeline.");
            return;
        }

        Windows.Storage.StorageFile? file = null;
        try
        {
            file = await PickerHelper.PickSingleFileAsync(
                Windows.Storage.Pickers.PickerLocationId.MusicLibrary,
                AudioImportService.SupportedExtensions,
                Windows.Storage.Pickers.PickerViewMode.List);
        }
        catch (Exception ex)
        {
            Musio.Core.Diagnostics.DiagLog.Write("Editor", $"Audio import picker failed: {ex.Message}");
        }
        if (file is null) return;

        // Captured before the transcode: the playhead can move while a multi-second import
        // runs, and the track belongs where the user was when they asked for it.
        var startTime = Timeline?.PlayheadPosition ?? ViewModel.Model.PlayheadPosition;

        using var cts = new CancellationTokenSource();
        var (dialog, bar) = DialogHelper.BuildProgressDialog(
            XamlRoot, $"Importing {label}", "Converting and importing the audio…", cts);
        var progress = new Progress<double>(p =>
        {
            DispatcherQueue.TryEnqueue(() => bar.Value = Math.Clamp(p, 0, 1) * 100);
        });

        var showTask = dialog.ShowAsync();
        string? errorMessage = null;
        try
        {
            var result = await AudioImportService.ImportAsync(file.Path, null, progress, cts.Token);

            // Executed through the undo manager, like every other timeline edit: an inserted
            // track lives on TimelineModel.AudioTracks precisely so Ctrl+Z can take it back.
            var name = Path.GetFileNameWithoutExtension(result.SourceFileName);

            // Clamped to what is left of the timeline. A music bed is routinely far longer
            // than the footage, and a block running past the end of the ruler puts its own
            // trim handle somewhere the user cannot reach or scroll to. The rest of the file
            // is not lost — the lane draws it dimmed behind the block, so it can be dragged
            // back in from the right edge whenever there is room for it.
            var timelineEnd = ViewModel.Model.DisplayDuration;
            var start = startTime < TimeSpan.Zero ? TimeSpan.Zero : startTime;

            // A playhead parked AT the end of the timeline (pressing End, or a clip that ends
            // the project) leaves no room at all, and a block starting there maps to the
            // canvas's right edge — so it draws as nothing, and the ruler cannot scroll past
            // DisplayDuration to reach it. The insert would appear to do nothing at all. Back
            // the start off just far enough for the clip to land somewhere it can be seen and
            // grabbed; the audio is still anchored as late as it can be.
            if (timelineEnd > TimeSpan.Zero && start >= timelineEnd)
            {
                var wanted = result.Duration < timelineEnd ? result.Duration : timelineEnd;
                start = timelineEnd - wanted;
            }

            TimeSpan? duration = null;
            if (timelineEnd > start)
            {
                var room = timelineEnd - start;
                if (room < result.Duration)
                {
                    // Never clamp below the minimum a block can be trimmed to, or inserting
                    // at the very end of the timeline would produce something too small to
                    // grab and drag back out.
                    duration = room >= AudioTrackEditing.MinDuration
                        ? room
                        : AudioTrackEditing.MinDuration;
                }
            }

            var operation = new AddAudioTrackOperation(new AudioTrack
            {
                FilePath = result.AudioFilePath,
                Name = string.IsNullOrWhiteSpace(name) ? (isMusic ? "Music" : "Voice over") : name,
                Kind = kind,
                StartTime = start,
                TrimStart = TimeSpan.Zero,
                SourceDuration = result.Duration,
                Duration = duration,
                Volume = AudioTrack.DefaultVolumeFor(kind),
            });

            ViewModel.UndoRedoManager.Execute(operation);

            // The import can run for many seconds, during which the page may have been
            // unloaded — the model edit above still stands (it is the shared timeline), but
            // there may no longer be a control to select in or a preview to rebuild.
            if (Timeline is not null)
            {
                Timeline.SelectedInsertedAudioTrackId = operation.CreatedId;
                RefreshInsertedAudio();
            }
        }
        catch (OperationCanceledException)
        {
            // User cancelled — nothing to report.
        }
        catch (AudioImportException ex)
        {
            errorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            Musio.Core.Diagnostics.DiagLog.Write("Editor", $"Audio import failed: {ex}");
            errorMessage = ex.Message;
        }
        finally
        {
            // Only one ContentDialog may be shown at a time, so the error dialog below has
            // to wait for this one to finish closing.
            dialog.Hide();
            try { await showTask; } catch { /* dialog dismissed */ }
        }

        if (errorMessage is not null)
            await ShowProjectDialogAsync($"Could not insert {label}", errorMessage);
    }
}
