using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Imaging;
using Microsoft.Windows.AI;
using Microsoft.Windows.AI.MachineLearning;
using Microsoft.Windows.AI.Video;
using Mixtri.Core.Diagnostics;
using Mixtri.Core.Export;

namespace Mixtri_App.Services;

internal sealed class WindowsVideoSuperResolutionProvider : IVideoSuperResolutionProvider
{
    /// <summary>
    /// Upper bound on preparing the upscaler (execution-provider registration and model
    /// download/initialization). Past it the export continues with standard scaling rather
    /// than sitting at 0% indefinitely.
    /// </summary>
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(60);

    public async Task<IVideoSuperResolutionSession?> TryCreateAsync(
        int inputWidth, int inputHeight, int outputWidth, int outputHeight,
        CancellationToken cancellationToken)
    {
        if (!VideoSuperResolutionSupport.SupportsDimensions(
                inputWidth, inputHeight, outputWidth, outputHeight))
            return null;

        using var setupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        setupTimeout.CancelAfter(SetupTimeout);
        // WaitAsync guarantees the await returns at the deadline even if a WinRT operation
        // ignores Cancel(); AsTask alone waits for the operation to acknowledge it.
        var setupToken = setupTimeout.Token;

        VideoScaler? scaler = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var catalog = ExecutionProviderCatalog.GetDefault();
            await catalog.EnsureAndRegisterCertifiedAsync().AsTask(setupToken).WaitAsync(setupToken);
            cancellationToken.ThrowIfCancellationRequested();

            var readyState = VideoScaler.GetReadyState();
            if (readyState == AIFeatureReadyState.NotSupportedOnCurrentSystem)
            {
                DiagLog.Write("Export", "Windows AI VideoScaler is not supported on this device; using standard scaling.");
                return null;
            }

            if (readyState == AIFeatureReadyState.NotReady)
            {
                var readyOperation = await VideoScaler.EnsureReadyAsync().AsTask(setupToken).WaitAsync(setupToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (readyOperation.Status != AIFeatureReadyResultState.Success)
                {
                    DiagLog.Write(
                        "Export",
                        $"Windows AI VideoScaler could not be initialized ({readyOperation.Status}); using standard scaling.");
                    return null;
                }
            }

            scaler = await VideoScaler.CreateAsync().AsTask(setupToken).WaitAsync(setupToken);
            cancellationToken.ThrowIfCancellationRequested();
            var session = new WindowsVideoSuperResolutionSession(
                scaler, inputWidth, inputHeight, outputWidth, outputHeight);
            scaler = null;

            DiagLog.Write(
                "Export",
                $"Windows AI VideoScaler enabled for {inputWidth}x{inputHeight} -> {outputWidth}x{outputHeight}.");
            return session;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (setupTimeout.IsCancellationRequested)
        {
            DiagLog.Write(
                "Export",
                $"Windows AI VideoScaler setup did not finish within {SetupTimeout.TotalSeconds:0}s; using standard scaling.");
            return null;
        }
        catch (Exception ex)
        {
            DiagLog.Write(
                "Export",
                $"Windows AI VideoScaler initialization failed; using standard scaling. {ex}");
            return null;
        }
        finally
        {
            scaler?.Dispose();
        }
    }

    private sealed class WindowsVideoSuperResolutionSession : IVideoSuperResolutionSession
    {
        private readonly VideoScaler _scaler;
        private readonly ImageBuffer _inputImage;
        private readonly ImageBuffer _outputImage;
        private readonly byte[] _inputBgra;
        private readonly byte[] _inputBgr;
        private readonly byte[] _outputBgr;
        private readonly byte[] _outputBgra;
        private readonly Windows.Storage.Streams.IBuffer _inputBgraBuffer;
        private readonly Windows.Storage.Streams.IBuffer _outputBgraBuffer;
        private bool _disabled;
        private bool _disposed;

        public WindowsVideoSuperResolutionSession(
            VideoScaler scaler, int inputWidth, int inputHeight, int outputWidth, int outputHeight)
        {
            _scaler = scaler;

            int inputPixelCount = checked(inputWidth * inputHeight);
            int outputPixelCount = checked(outputWidth * outputHeight);
            // All staging buffers are allocated once per export and reused for every frame.
            _inputBgra = new byte[checked(inputPixelCount * 4)];
            _inputBgr = new byte[checked(inputPixelCount * 3)];
            _outputBgr = new byte[checked(outputPixelCount * 3)];
            _outputBgra = new byte[checked(outputPixelCount * 4)];
            _inputBgraBuffer = _inputBgra.AsBuffer();
            _outputBgraBuffer = _outputBgra.AsBuffer();

            _inputImage = ImageBuffer.CreateForBuffer(
                _inputBgr.AsBuffer(), ImageBufferPixelFormat.Bgr8,
                inputWidth, inputHeight, checked(inputWidth * 3));
            _outputImage = ImageBuffer.CreateForBuffer(
                _outputBgr.AsBuffer(), ImageBufferPixelFormat.Bgr8,
                outputWidth, outputHeight, checked(outputWidth * 3));
        }

        public bool TryScale(CanvasRenderTarget source, CanvasRenderTarget destination)
        {
            if (_disabled)
                return false;
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                source.GetPixelBytes(_inputBgraBuffer);
                BgraToBgr(_inputBgra, _inputBgr);

                var result = _scaler.ScaleImageBuffer(
                    _inputImage, _outputImage, new VideoScalerOptions());
                if (result.Status != VideoScalerStatus.Success)
                    throw new InvalidOperationException($"VideoScaler returned {result.Status}.");

                _outputImage.CopyToByteArray(_outputBgr);
                BgrToOpaqueBgra(_outputBgr, _outputBgra);
                destination.SetPixelBytes(_outputBgraBuffer);
                return true;
            }
            catch (Exception ex)
            {
                DiagLog.Write("Export", $"Windows AI VideoScaler failed; switching to standard scaling. {ex}");
                _disabled = true;
                Dispose();
                return false;
            }
        }

        private static void BgraToBgr(ReadOnlySpan<byte> bgra, Span<byte> bgr)
        {
            int pixels = bgr.Length / 3;
            for (int i = 0, src = 0, dst = 0; i < pixels; i++, src += 4, dst += 3)
            {
                bgr[dst] = bgra[src];
                bgr[dst + 1] = bgra[src + 1];
                bgr[dst + 2] = bgra[src + 2];
            }
        }

        private static void BgrToOpaqueBgra(ReadOnlySpan<byte> bgr, Span<byte> bgra)
        {
            // One 32-bit store per pixel (little-endian B, G, R, A=255).
            var output = MemoryMarshal.Cast<byte, uint>(bgra);
            for (int i = 0, src = 0; i < output.Length; i++, src += 3)
                output[i] = bgr[src] | ((uint)bgr[src + 1] << 8) | ((uint)bgr[src + 2] << 16) | 0xFF000000u;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _scaler.Dispose();
            _disposed = true;
        }
    }
}
