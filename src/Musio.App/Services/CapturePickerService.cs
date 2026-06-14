using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Musio_App.Controls;
using Musio_App.ViewModels;
using Musio.Core.Capture;

namespace Musio_App.Services;

/// <summary>
/// Owns the "launch picker, await user selection, push result into the shared
/// recording VM" flow that used to live inline in
/// <c>RecordingPage.LaunchRegionPickerAsync</c> / <c>LaunchWindowPickerAsync</c>.
/// </summary>
/// <remarks>
/// Centralising this lets both the page (Phase A) and the future
/// <c>AppShellWindow</c> (Phase B+) launch pickers via the same code path,
/// and lets the Mini Setup toolbar plug in its dim-while-picking behaviour
/// through <see cref="IDimmable"/> without each call site duplicating the
/// orchestration.
/// </remarks>
public sealed class CapturePickerService
{
    /// <summary>Shared singleton mirroring <see cref="RecordingViewModel.Shared"/>.</summary>
    public static CapturePickerService Shared { get; } = new();

    private CapturePickerService() { }

    private bool _isPickerOpen;

    /// <summary>
    /// True while a picker overlay is on screen. Used to suppress re-entrant
    /// launches (e.g. capture-mode toggle while a picker is already up).
    /// </summary>
    public bool IsPickerOpen => _isPickerOpen;

    /// <summary>Raised right before a picker overlay is shown.</summary>
    public event EventHandler? PickerOpening;

    /// <summary>Raised after a picker overlay has been dismissed (confirmed or cancelled).</summary>
    public event EventHandler? PickerClosed;

    private RecordingViewModel ViewModel => RecordingViewModel.Shared;

    /// <summary>
    /// Show the region picker. On confirm, updates
    /// <see cref="RecordingViewModel.SelectedRegion"/> / <see cref="RecordingViewModel.HasSelectedRegion"/>
    /// on <see cref="RecordingViewModel.Shared"/> and returns
    /// <see cref="PickerResult.Selected"/>. Returns
    /// <see cref="PickerResult.Cancelled"/> if the user explicitly dismissed
    /// the picker, or <see cref="PickerResult.AlreadyOpen"/> if the re-entrancy
    /// guard rejected the call (no picker was shown — callers should treat
    /// this as a silent no-op).
    /// </summary>
    /// <param name="owner">
    /// Window that should own the picker overlay (used today only by
    /// future-phase positioning logic; the picker itself self-hosts).
    /// </param>
    /// <param name="dimTarget">
    /// Optional dim target (e.g. the Mini Setup toolbar). Phase A wires the
    /// dim/undim around the picker await; concrete implementations land in
    /// Phase C.
    /// </param>
    public async Task<PickerResult> PickRegionAsync(Window? owner, IDimmable? dimTarget = null)
    {
        if (_isPickerOpen) return PickerResult.AlreadyOpen;
        _isPickerOpen = true;

        var dim = dimTarget ?? NoOpDimmable.Instance;
        try
        {
            PickerOpening?.Invoke(this, EventArgs.Empty);
            await dim.DimAsync();

            var overlay = new RegionSelectorOverlay();
            var region = await overlay.ShowAsync(ViewModel.SelectedRegion);

            if (region is not null)
            {
                ViewModel.SelectedRegion = region;
                ViewModel.HasSelectedRegion = true;
                return PickerResult.Selected;
            }

            return PickerResult.Cancelled;
        }
        finally
        {
            try { await dim.UndimAsync(); } catch { /* never let undim crash the caller */ }
            _isPickerOpen = false;
            PickerClosed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Show the window picker. On confirm, updates
    /// <see cref="RecordingViewModel.SelectedWindow"/> on
    /// <see cref="RecordingViewModel.Shared"/> and returns
    /// <see cref="PickerResult.Selected"/>. Returns
    /// <see cref="PickerResult.Cancelled"/> if the user explicitly dismissed
    /// the picker, or <see cref="PickerResult.AlreadyOpen"/> if the
    /// re-entrancy guard rejected the call.
    /// </summary>
    public async Task<PickerResult> PickWindowAsync(Window? owner, IDimmable? dimTarget = null)
    {
        if (_isPickerOpen) return PickerResult.AlreadyOpen;
        _isPickerOpen = true;

        var dim = dimTarget ?? NoOpDimmable.Instance;
        try
        {
            PickerOpening?.Invoke(this, EventArgs.Empty);
            await dim.DimAsync();

            var overlay = new WindowSelectorOverlay();
            var window = await overlay.ShowAsync();

            if (window is not null)
            {
                ViewModel.SelectedWindow = window;
                try { ShellSettings.Instance.LastWindowSelection = BuildWindowSelectionTuple(window); }
                catch (Exception ex) { Debug.WriteLine($"[CapturePickerService] Persist window failed: {ex.Message}"); }
                return PickerResult.Selected;
            }

            return PickerResult.Cancelled;
        }
        finally
        {
            try { await dim.UndimAsync(); } catch { /* never let undim crash the caller */ }
            _isPickerOpen = false;
            PickerClosed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static (string ProcessName, string WindowTitle, string ClassName) BuildWindowSelectionTuple(WindowInfo window)
    {
        string className = TryGetClassName(window.Handle) ?? string.Empty;
        // WindowInfo.ProcessName is already without the .exe suffix.
        return (window.ProcessName ?? string.Empty,
                window.Title ?? string.Empty,
                className);
    }

    private static string? TryGetClassName(IntPtr hwnd)
    {
        try
        {
            var sb = new StringBuilder(256);
            int copied = GetClassName(hwnd, sb, sb.Capacity);
            return copied > 0 ? sb.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
}

/// <summary>
/// Outcome of a <see cref="CapturePickerService"/> picker call.
/// </summary>
public enum PickerResult
{
    /// <summary>User confirmed a selection; the shared VM was updated.</summary>
    Selected,

    /// <summary>The picker was shown and the user explicitly cancelled (Esc / close / cancel button).</summary>
    Cancelled,

    /// <summary>
    /// The re-entrancy guard rejected the call because another picker was
    /// already on screen. No picker was shown; callers should treat this as
    /// a silent no-op (in particular, do NOT surface a "kept previous
    /// selection" InfoBar message — nothing changed).
    /// </summary>
    AlreadyOpen,
}
