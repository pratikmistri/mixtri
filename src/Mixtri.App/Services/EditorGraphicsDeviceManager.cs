using System;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Dispatching;
using Mixtri.Core.Diagnostics;
using Mixtri.Core.Processing;

namespace Mixtri_App.Services;

/// <summary>
/// Owns the shared Win2D <see cref="CanvasDevice"/> attach/detach lifecycle plus the
/// serialized recovery that rebuilds the editor after the device raises
/// <see cref="CanvasDevice.DeviceLost"/> (GPU driver restart / TDR). The actual recovery work
/// — tearing down and rebuilding the preview pipeline, renderers, and timeline thumbnails —
/// stays owned by the editor page and is supplied via the <c>recoverAsync</c> delegate; this
/// class owns the device subscription and delegates bounded retries to
/// <see cref="GraphicsRecoveryCoordinator"/>, so it complements (rather than
/// duplicates) <see cref="GpuContext"/>, which centralizes shared-device acquisition for the
/// Core-side renderers.
/// </summary>
public sealed class EditorGraphicsDeviceManager
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Func<bool> _isUnloaded;

    private CanvasDevice? _graphicsDevice;
    private readonly GraphicsRecoveryCoordinator _recovery;

    /// <param name="dispatcherQueue">
    /// The owning page's <see cref="DispatcherQueue"/>, used to marshal recovery back onto the
    /// UI thread from the (potentially non-UI) <see cref="CanvasDevice.DeviceLost"/> callback.
    /// </param>
    /// <param name="recoverAsync">
    /// Performs the actual device recovery (tearing down and rebuilding renderers/preview
    /// state). Always invoked on the UI thread.
    /// </param>
    /// <param name="flushPendingRenderAsync">
    /// Repaints the preview once recovery has fully finished. This CANNOT be folded into
    /// <paramref name="recoverAsync"/>: that delegate runs inside the recovery scope, where
    /// <see cref="IsRecoveryInProgress"/> is still true and the preview deliberately parks
    /// render requests instead of drawing against a device that is mid-rebuild. Invoked on
    /// the UI thread after the flag has cleared.
    /// </param>
    /// <param name="isUnloaded">
    /// Returns true once the owning page has unloaded, so a queued or in-flight recovery does
    /// not keep retrying or touch torn-down page state.
    /// </param>
    /// <param name="onRecoveryFailed">Displays a terminal recovery error on the UI thread.</param>
    public EditorGraphicsDeviceManager(
        DispatcherQueue dispatcherQueue,
        Func<Task> recoverAsync,
        Func<Task> flushPendingRenderAsync,
        Func<bool> isUnloaded,
        Action<Exception> onRecoveryFailed)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        ArgumentNullException.ThrowIfNull(recoverAsync);
        ArgumentNullException.ThrowIfNull(flushPendingRenderAsync);
        _isUnloaded = isUnloaded ?? throw new ArgumentNullException(nameof(isUnloaded));
        ArgumentNullException.ThrowIfNull(onRecoveryFailed);
        _recovery = new GraphicsRecoveryCoordinator(
            work => _dispatcherQueue.TryEnqueue(async () => await work()),
            recoverAsync, flushPendingRenderAsync, _isUnloaded,
            ex =>
            {
                DiagLog.Write("Editor", $"graphics recovery failed: {ex}");
                if (_dispatcherQueue.HasThreadAccess)
                    onRecoveryFailed(ex);
                else if (!_dispatcherQueue.TryEnqueue(() => onRecoveryFailed(ex)))
                    DiagLog.Write("Editor", "graphics recovery error could not be displayed: dispatcher unavailable");
            });
    }

    /// <summary>True while a device-lost recovery is pending or rebuilding. Callers that render off the
    /// shared device (e.g. the preview) should defer/coalesce rather than render while this is
    /// true, since the device and dependent GPU resources are mid-teardown/rebuild.</summary>
    public bool IsRecoveryInProgress => _recovery.IsRecoveryInProgress;

    public void Attach()
    {
        try
        {
            var device = GpuContext.GetSharedDevice();
            if (ReferenceEquals(_graphicsDevice, device))
                return;

            Detach();
            _graphicsDevice = device;
            _graphicsDevice.DeviceLost += OnGraphicsDeviceLost;
        }
        catch (Exception ex)
        {
            DiagLog.Write("Editor", $"failed to attach graphics-device recovery: {ex.Message}");
        }
    }

    public void Detach()
    {
        if (_graphicsDevice is null)
            return;

        _graphicsDevice.DeviceLost -= OnGraphicsDeviceLost;
        _graphicsDevice = null;
    }

    private void OnGraphicsDeviceLost(CanvasDevice sender, object args)
        => RequestRecovery("shared CanvasDevice lost");

    /// <summary>Caught Win2D errors must still enter the device recovery path.</summary>
    public void RecoverFromRenderFailure(Exception exception)
    {
        if (_isUnloaded()) return;
        bool lost = GpuContext.IsDeviceLoss(exception);
        try
        {
            lost |= _graphicsDevice?.IsDeviceLost(exception.HResult) == true;
            if (lost && _graphicsDevice is not null)
            {
                // Notify Win2D as well as our resource owners, so GetSharedDevice returns
                // a replacement rather than handing recovery the same unusable device.
                _graphicsDevice.RaiseDeviceLost();
            }
        }
        catch (Exception ex)
        {
            DiagLog.Write("Editor", $"device-loss check failed: {ex}");
        }
        finally
        {
            if (lost)
                RequestRecovery($"render failed with device loss 0x{exception.HResult:X8}");
        }
    }

    /// <summary>
    /// Schedules a full editor graphics rebuild. Also callable for losses the shared-device
    /// subscription cannot see: a <c>CanvasControl</c> recovers from a device loss internally,
    /// so a GPU reset can invalidate every cached bitmap without
    /// <see cref="CanvasDevice.DeviceLost"/> ever firing here. Repeated requests coalesce into
    /// a single recovery pass.
    /// </summary>
    public void RequestRecovery(string reason)
    {
        if (_isUnloaded()) return;
        DiagLog.Write("Editor", $"{reason}; scheduling editor graphics recovery");
        _recovery.Request();
    }
}
