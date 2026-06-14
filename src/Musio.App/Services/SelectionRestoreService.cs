using System;
using System.Diagnostics;
using System.Linq;
using Musio.Core.Capture;
using Musio.Core.Settings;
using Musio_App.ViewModels;

namespace Musio_App.Services;

/// <summary>
/// Applies persisted Mini-Mode selections (capture mode, region, window,
/// audio/cam toggles) onto the shared <see cref="RecordingViewModel"/> at
/// shell-launch time. Returns a <see cref="SelectionRestoreOutcome"/> the
/// caller uses to drive any follow-up UX (auto-launch a picker, show an
/// InfoBar about a discarded region, etc.).
/// </summary>
/// <remarks>
/// Spec §3.1, §5.4: the toolbar must come up pre-configured to the user's
/// last-used selection. On first launch (no persisted capture mode at all),
/// we default to <c>CustomRegion</c> and signal the host to auto-launch the
/// region picker so the first action is "pick what to record".
/// </remarks>
public static class SelectionRestoreService
{
    public static SelectionRestoreOutcome RestoreOnLaunch(RecordingViewModel viewModel)
    {
        if (viewModel is null) throw new ArgumentNullException(nameof(viewModel));

        var settings = ShellSettings.Instance;

        CaptureRegion? savedRegion = null;
        try { savedRegion = new RegionSelector().LoadLastRegion(); }
        catch (Exception ex) { Debug.WriteLine($"[SelectionRestoreService] LoadLastRegion failed: {ex.Message}"); }

        var persisted = new PersistedSelectionState(
            settings.LastCaptureMode,
            savedRegion,
            settings.LastWindowSelection,
            settings.LastMicEnabled,
            settings.LastSystemAudioEnabled,
            settings.LastWebcamEnabled);

        return RestoreOnLaunch(viewModel, persisted, WindowMatcher.FindWindow, DoesRegionFit);
    }

    internal static SelectionRestoreOutcome RestoreOnLaunch(
        RecordingViewModel viewModel,
        PersistedSelectionState settings,
        Func<string, string, IntPtr?> findWindow,
        Func<CaptureRegion, bool> doesRegionFit)
    {
        if (viewModel is null) throw new ArgumentNullException(nameof(viewModel));
        if (findWindow is null) throw new ArgumentNullException(nameof(findWindow));
        if (doesRegionFit is null) throw new ArgumentNullException(nameof(doesRegionFit));

        try { viewModel.IsMicEnabled = settings.LastMicEnabled; } catch { }
        try { viewModel.IsSystemAudioEnabled = settings.LastSystemAudioEnabled; } catch { }
        try { viewModel.IsWebcamEnabled = settings.LastWebcamEnabled; } catch { }

        var savedMode = settings.LastCaptureMode;
        if (savedMode is null)
        {
            // First launch ever (spec §3.1 / §7 Resolution 7): pick Region
            // and tell the caller to auto-launch the picker.
            viewModel.CaptureMode = CaptureMode.CustomRegion;
            return new SelectionRestoreOutcome(
                CaptureMode.CustomRegion,
                AutoLaunchPicker: true,
                RegionDiscardedReason: null);
        }

        // Apply the persisted mode immediately so the UI lands in the right
        // segmented-control segment even before the per-mode restore work.
        viewModel.CaptureMode = savedMode.Value;

        switch (savedMode.Value)
        {
            case CaptureMode.FullScreen:
                // Nothing to restore.
                return new SelectionRestoreOutcome(CaptureMode.FullScreen, AutoLaunchPicker: false, RegionDiscardedReason: null);

            case CaptureMode.CustomRegion:
                return RestoreRegion(viewModel, settings.LastRegion, doesRegionFit);

            case CaptureMode.Window:
                return RestoreWindow(viewModel, settings.LastWindowSelection, findWindow);

            default:
                return new SelectionRestoreOutcome(savedMode.Value, AutoLaunchPicker: false, RegionDiscardedReason: null);
        }
    }

    private static SelectionRestoreOutcome RestoreRegion(
        RecordingViewModel viewModel,
        CaptureRegion? saved,
        Func<CaptureRegion, bool> doesRegionFit)
    {
        if (saved is null)
            return new SelectionRestoreOutcome(CaptureMode.CustomRegion, AutoLaunchPicker: false, RegionDiscardedReason: null);

        // Validate the region fits the current display layout. If the user's
        // resolution changed (or the owning monitor is gone) we discard it
        // and fall back to FullScreen with a toast (spec §6).
        if (!doesRegionFit(saved))
        {
            try { viewModel.SelectedRegion = null; } catch { }
            try { viewModel.HasSelectedRegion = false; } catch { }
            viewModel.CaptureMode = CaptureMode.FullScreen;
            return new SelectionRestoreOutcome(
                CaptureMode.FullScreen,
                AutoLaunchPicker: false,
                RegionDiscardedReason: "Previous region no longer fits this display.");
        }

        viewModel.SelectedRegion = saved;
        viewModel.HasSelectedRegion = true;
        return new SelectionRestoreOutcome(CaptureMode.CustomRegion, AutoLaunchPicker: false, RegionDiscardedReason: null);
    }

    private static SelectionRestoreOutcome RestoreWindow(
        RecordingViewModel viewModel,
        (string ProcessName, string WindowTitle, string ClassName)? saved,
        Func<string, string, IntPtr?> findWindow)
    {
        if (saved is null)
        {
            // No prior selection — land in Window mode and ask the host to
            // auto-launch the picker (spec §5.4).
            return new SelectionRestoreOutcome(CaptureMode.Window, AutoLaunchPicker: true, RegionDiscardedReason: null);
        }

        var hwnd = findWindow(saved.Value.ProcessName, saved.Value.WindowTitle);
        if (hwnd is null)
        {
            // Last window is gone — surface the picker (spec §6 / §5.4).
            return new SelectionRestoreOutcome(CaptureMode.Window, AutoLaunchPicker: true, RegionDiscardedReason: null);
        }

        try
        {
            // Reconstruct a minimal WindowInfo. The picker normally fills X/Y/W/H
            // for hover-highlight, but for a remembered selection none of that
            // matters until the user starts recording (BuildCaptureTarget only
            // checks the handle is still alive).
            var info = new WindowInfo(
                hwnd.Value,
                saved.Value.WindowTitle,
                saved.Value.ProcessName,
                0, 0, 0, 0,
                ExecutablePath: null);
            viewModel.SelectedWindow = info;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SelectionRestoreService] Apply window failed: {ex.Message}");
            return new SelectionRestoreOutcome(CaptureMode.Window, AutoLaunchPicker: true, RegionDiscardedReason: null);
        }

        return new SelectionRestoreOutcome(CaptureMode.Window, AutoLaunchPicker: false, RegionDiscardedReason: null);
    }

    private static bool DoesRegionFit(CaptureRegion region)
    {
        try
        {
            var monitors = new RegionSelector().GetMonitors();
            // Match either the exact device name or the "(Primary)"-suffixed form.
            var monitor = monitors.FirstOrDefault(m =>
                m.Id == region.MonitorId
                || m.Name == region.MonitorId
                || m.Name.StartsWith(region.MonitorId + " ", StringComparison.Ordinal));
            if (monitor is null) return false;

            // Region X/Y are monitor-local logical (DIP). Validate against the
            // monitor's own width/height (also in physical px, but the rough
            // "is it contained within the monitor" test is a safe lower bound:
            // a region 4× too wide because the user halved their resolution
            // will fail this trivially).
            if (region.X < 0 || region.Y < 0) return false;
            if (region.Width <= 0 || region.Height <= 0) return false;
            if (region.X + region.Width > monitor.Width) return false;
            if (region.Y + region.Height > monitor.Height) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Result of a launch-time selection restore. The host uses these signals
/// to decide whether to auto-launch a picker and whether to surface a
/// toast/InfoBar about a discarded selection.
/// </summary>
/// <param name="AppliedMode">The capture mode the VM ended up in.</param>
/// <param name="AutoLaunchPicker">
/// True when the host should immediately launch a picker (region picker on
/// first launch / no prior selection, window picker when the remembered
/// window can't be re-resolved).
/// </param>
/// <param name="RegionDiscardedReason">
/// Non-null when a remembered region was discarded (e.g. doesn't fit the
/// current display) and the user should see a transient explanation.
/// </param>
public sealed record SelectionRestoreOutcome(
    CaptureMode AppliedMode,
    bool AutoLaunchPicker,
    string? RegionDiscardedReason);

internal readonly record struct PersistedSelectionState(
    CaptureMode? LastCaptureMode,
    CaptureRegion? LastRegion,
    (string ProcessName, string WindowTitle, string ClassName)? LastWindowSelection,
    bool LastMicEnabled,
    bool LastSystemAudioEnabled,
    bool LastWebcamEnabled);
