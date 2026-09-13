using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Mixtri.Core.Diagnostics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Mixtri.Core.Capture;

/// <summary>Reuses one immutable held JPEG's decoded pixels, never an encoder-owned buffer.</summary>
internal sealed class CaptureFrameDecoder(int width, int height, bool cacheLinkedFrames) : IDisposable
{
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private readonly uint _bufferCapacity = GetBufferCapacity(width, height);
    private SoftwareBitmap? _cachedBitmap;
    private FileIdentity? _cachedIdentity;
    private int _identityAvailable = 1;
    private long _decodeCount;
    private long _maximumCachedBytes;
    private bool _disposed;

    internal long DecodeCount => Interlocked.Read(ref _decodeCount);
    internal long MaximumCachedBytes => Interlocked.Read(ref _maximumCachedBytes);

    internal async Task<IBuffer> ReadAsync(string path, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        FileIdentity? identity = null;
        if (cacheLinkedFrames && Volatile.Read(ref _identityAvailable) != 0)
        {
            if (GetFileInformationByHandle(file.SafeFileHandle, out var info))
            {
                if (info.NumberOfLinks > 1)
                    identity = new(info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow,
                        info.FileSizeHigh, info.FileSizeLow, info.LastWriteTimeHigh, info.LastWriteTimeLow);
            }
            else if (Interlocked.Exchange(ref _identityAvailable, 0) != 0)
            {
                DiagLog.Write("Capture",
                    $"Frame file identity unavailable (Win32 error {Marshal.GetLastWin32Error()}); decoding frames independently.");
            }
        }

        if (identity is null)
        {
            if (cacheLinkedFrames)
            {
                await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
                try { ClearCache(); }
                finally { _cacheGate.Release(); }
            }
            using var bitmap = await DecodeAsync(file, ct).ConfigureAwait(false);
            return CopyForEncoder(bitmap);
        }

        await _cacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cachedIdentity != identity)
            {
                ClearCache();
                _cachedBitmap = await DecodeAsync(file, ct).ConfigureAwait(false);
                _cachedIdentity = identity;
                _maximumCachedBytes = Math.Max(_maximumCachedBytes, checked((long)width * height * 4));
            }
            return CopyForEncoder(_cachedBitmap!);
        }
        finally { _cacheGate.Release(); }
    }

    private async Task<SoftwareBitmap> DecodeAsync(FileStream file, CancellationToken ct)
    {
        Interlocked.Increment(ref _decodeCount);
        using var stream = file.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);
        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform
            {
                ScaledWidth = (uint)width,
                ScaledHeight = (uint)height,
                InterpolationMode = BitmapInterpolationMode.Fant,
                // Uncompressed RGB buffers with positive stride are bottom-up in Media Foundation.
                Flip = BitmapFlip.Vertical,
            },
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(ct).ConfigureAwait(false);
    }

    private IBuffer CopyForEncoder(SoftwareBitmap bitmap)
    {
        var buffer = new Windows.Storage.Streams.Buffer(_bufferCapacity);
        bitmap.CopyToBuffer(buffer);
        return buffer;
    }

    private static uint GetBufferCapacity(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        return checked((uint)((long)width * height * 4));
    }

    private void ClearCache()
    {
        _cachedBitmap?.Dispose();
        _cachedBitmap = null;
        _cachedIdentity = null;
    }

    public void Dispose()
    {
        _cacheGate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            ClearCache();
        }
        finally { _cacheGate.Release(); }
    }

    private readonly record struct FileIdentity(
        uint Volume, uint IndexHigh, uint IndexLow, uint SizeHigh, uint SizeLow, uint WriteHigh, uint WriteLow);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public uint CreationTimeLow, CreationTimeHigh;
        public uint LastAccessTimeLow, LastAccessTimeHigh;
        public uint LastWriteTimeLow, LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
}
