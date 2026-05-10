using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Musio.Core.Capture;

/// <summary>
/// High-performance screen capture engine using Windows.Graphics.Capture.
/// </summary>
public sealed class ScreenCaptureEngine : IDisposable
{
    private readonly IDirect3DDevice _device;
    private readonly GraphicsCaptureItem _captureItem;
    private readonly int _fps;
    private readonly TimeSpan _frameInterval;

    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private readonly Stopwatch _stopwatch = new();

    private SizeInt32 _lastSize;
    private long _framesCaptured;
    private long _droppedFrames;
    private long _throttledFrames;
    private volatile bool _isPaused;
    private bool _disposed;

    // Frame-slot gating: accept at most one frame per time slot to enforce CFR pacing
    private long _lastEmittedSlot = -1;

    public bool IsRecording { get; private set; }
    public int Fps => _fps;
    public long FramesCaptured => Interlocked.Read(ref _framesCaptured);
    public long DroppedFrames => Interlocked.Read(ref _droppedFrames);
    public long ThrottledFrames => Interlocked.Read(ref _throttledFrames);
    public IDirect3DDevice Device => _device;

    public event EventHandler<CapturedFrameEventArgs>? FrameCaptured;
    public event EventHandler? CaptureStarted;
    public event EventHandler? CaptureStopped;
    public event EventHandler<string>? Error;

    private ScreenCaptureEngine(IDirect3DDevice device, GraphicsCaptureItem item, int fps)
    {
        _device = device;
        _captureItem = item;
        _fps = fps;
        _frameInterval = TimeSpan.FromSeconds(1.0 / fps);
    }

    /// <summary>
    /// Creates a capture engine targeting a specific monitor.
    /// </summary>
    public static ScreenCaptureEngine CreateForMonitor(IntPtr hMonitor, int fps = 30)
    {
        var device = Direct3DDeviceHelper.CreateDevice();
        try
        {
            var item = CreateCaptureItemForMonitor(hMonitor);
            return new ScreenCaptureEngine(device, item, fps);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a capture engine targeting a specific window.
    /// </summary>
    public static ScreenCaptureEngine CreateForWindow(IntPtr hwnd, int fps = 30)
    {
        var device = Direct3DDeviceHelper.CreateDevice();
        try
        {
            var item = CreateCaptureItemForWindow(hwnd);
            return new ScreenCaptureEngine(device, item, fps);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    public void StartCapture()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRecording)
            return;

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            _captureItem.Size);

        _framePool.FrameArrived += OnFrameArrived;

        _session = _framePool.CreateCaptureSession(_captureItem);

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            _session.IsCursorCaptureEnabled = false;

        Interlocked.Exchange(ref _framesCaptured, 0);
        Interlocked.Exchange(ref _droppedFrames, 0);
        Interlocked.Exchange(ref _throttledFrames, 0);
        _lastEmittedSlot = -1;
        _isPaused = false;

        _stopwatch.Restart();
        _session.StartCapture();
        IsRecording = true;

        CaptureStarted?.Invoke(this, EventArgs.Empty);
    }

    public void StopCapture()
    {
        if (!IsRecording)
            return;

        _stopwatch.Stop();
        IsRecording = false;

        _session?.Dispose();
        _session = null;

        if (_framePool is not null)
        {
            _framePool.FrameArrived -= OnFrameArrived;
            _framePool.Dispose();
            _framePool = null;
        }

        CaptureStopped?.Invoke(this, EventArgs.Empty);
    }

    public void PauseCapture()
    {
        if (IsRecording)
            _isPaused = true;
    }

    public void ResumeCapture()
    {
        if (IsRecording)
            _isPaused = false;
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null)
        {
            Interlocked.Increment(ref _droppedFrames);
            return;
        }

        if (_isPaused)
            return;

        try
        {
            var size = frame.ContentSize;

            // Recreate frame pool if capture size changed
            if (size.Width != _lastSize.Width || size.Height != _lastSize.Height)
            {
                _lastSize = size;
                _framePool?.Recreate(
                    _device,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    2,
                    size);
            }

            var timestamp = _stopwatch.Elapsed;

            // Frame-slot gating: compute which slot this frame belongs to
            // and only emit one frame per slot for consistent CFR pacing.
            long slot = (long)(timestamp.TotalSeconds / _frameInterval.TotalSeconds);
            long previousSlot = Interlocked.Exchange(ref _lastEmittedSlot, slot);
            if (slot <= previousSlot)
            {
                // Already emitted a frame for this slot — skip
                Interlocked.Exchange(ref _lastEmittedSlot, previousSlot);
                Interlocked.Increment(ref _throttledFrames);
                return;
            }

            var surface = frame.Surface;

            // Compute how many slots were missed since the last emitted frame.
            // Skip gap-fill for the very first frame (startup latency is
            // handled by MouseToVideoOffsetSeconds, not by frame duplication).
            int skippedSlots = previousSlot >= 0 ? (int)(slot - previousSlot - 1) : 0;

            Interlocked.Increment(ref _framesCaptured);

            FrameCaptured?.Invoke(this, new CapturedFrameEventArgs(
                surface,
                timestamp,
                size.Width,
                size.Height,
                skippedSlots));
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, ex.Message);
        }
    }

    #region GraphicsCaptureItem Interop

    private static GraphicsCaptureItem CreateCaptureItemForMonitor(IntPtr hMonitor)
    {
        var interop = GetInteropFactory();
        var itemIid = GraphicsCaptureItemGuid;
        interop.CreateForMonitor(hMonitor, ref itemIid, out var rawItem);
        var item = MarshalInterface<GraphicsCaptureItem>.FromAbi(rawItem);
        Marshal.Release(rawItem);
        return item;
    }

    private static GraphicsCaptureItem CreateCaptureItemForWindow(IntPtr hwnd)
    {
        var interop = GetInteropFactory();
        var itemIid = GraphicsCaptureItemGuid;
        interop.CreateForWindow(hwnd, ref itemIid, out var rawItem);
        var item = MarshalInterface<GraphicsCaptureItem>.FromAbi(rawItem);
        Marshal.Release(rawItem);
        return item;
    }

    private static readonly Guid GraphicsCaptureItemGuid =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    private static IGraphicsCaptureItemInterop GetInteropFactory()
    {
        var interopGuid = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
        var className = "Windows.Graphics.Capture.GraphicsCaptureItem";

        var hstring = MarshalString.CreateMarshaler(className);
        var hr = RoGetActivationFactory(
            MarshalString.GetAbi(hstring),
            ref interopGuid,
            out var factoryPtr);
        MarshalString.DisposeMarshaler(hstring);
        Marshal.ThrowExceptionForHR(hr);

        var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
        Marshal.Release(factoryPtr);
        return interop;
    }

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId,
        ref Guid iid,
        out IntPtr factory);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        void CreateForWindow(
            IntPtr window,
            ref Guid iid,
            out IntPtr result);

        void CreateForMonitor(
            IntPtr monitor,
            ref Guid iid,
            out IntPtr result);
    }

    #endregion

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        StopCapture();
        _device.Dispose();
    }
}
