using Microsoft.Graphics.Canvas;

namespace Mixtri.Core.Processing;

/// <summary>
/// Single entry point for acquiring the shared Win2D <see cref="CanvasDevice"/>. Classes
/// that need the process-wide shared device should go through here rather than calling
/// <see cref="CanvasDevice.GetSharedDevice"/> directly, so device acquisition is grep-able
/// from one place.
/// </summary>
public static class GpuContext
{
    public static CanvasDevice GetSharedDevice() => CanvasDevice.GetSharedDevice();
}

/// <summary>
/// Thrown when a GPU operation could not proceed because its <see cref="CanvasDevice"/>
/// raised <see cref="CanvasDevice.DeviceLost"/>. Callers should treat this as recoverable —
/// let it propagate out of the current frame/export and let the caller restart with a
/// freshly (re)initialized renderer, rather than attempting to recreate the device mid-frame.
/// </summary>
public sealed class RecoverableDeviceLostException : Exception
{
    public RecoverableDeviceLostException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Tracks whether a <see cref="CanvasDevice"/> has raised <see cref="CanvasDevice.DeviceLost"/>,
/// via a flag safe to read from any thread, so callers can consolidate the
/// subscribe/volatile-flag/unsubscribe dance that used to be hand-rolled per class.
/// Subscribes in the constructor; <see cref="Dispose"/> unsubscribes (idempotent).
/// </summary>
public sealed class DeviceLostGuard : IDisposable
{
    private readonly CanvasDevice _device;
    private readonly string _message;
    private readonly Action? _onLost;
    private volatile bool _lost;
    private bool _disposed;

    public DeviceLostGuard(CanvasDevice device, string message, Action? onLost = null)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _message = message;
        _onLost = onLost;
        _device.DeviceLost += OnDeviceLost;
    }

    /// <summary>True once the guarded device has raised <see cref="CanvasDevice.DeviceLost"/>.</summary>
    public bool IsLost => _lost;

    private void OnDeviceLost(CanvasDevice sender, object args)
    {
        _lost = true;
        Mixtri.Core.Diagnostics.DiagLog.Write("Gpu",
            $"CanvasDevice.DeviceLost raised; every guard on this device now fails until its " +
            $"owner is rebuilt. Context: {_message}");
        _onLost?.Invoke();
    }

    /// <summary>Throws <see cref="RecoverableDeviceLostException"/> if the device has been lost.</summary>
    public void ThrowIfLost()
    {
        if (_lost)
            throw new RecoverableDeviceLostException(_message);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _device.DeviceLost -= OnDeviceLost;
            _disposed = true;
        }
    }
}
