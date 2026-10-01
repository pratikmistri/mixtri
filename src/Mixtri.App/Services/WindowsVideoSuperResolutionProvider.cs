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
    public async Task<IVideoSuperResolutionSession?> TryCreateAsync(
        int inputWidth, int inputHeight, int outputWidth, int outputHeight,
        CancellationToken cancellationToken)
    {
        if (!VideoSuperResolutionSupport.SupportsDimensions(
                inputWidth, inputHeight, outputWidth, outputHeight))
            return null;

        VideoScaler? scaler = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var catalog = ExecutionProviderCatalog.GetDefault();
            await catalog.EnsureAndRegisterCertifiedAsync();
            cancellationToken.ThrowIfCancellationRequested();

            var readyState = VideoScaler.GetReadyState();
            if (readyState == AIFeatureReadyState.NotSupportedOnCurrentSystem)
            {
                DiagLog.Write("Export", "Windows AI VideoScaler is not supported on this device; using standard scaling.");
                return null;
            }

            if (readyState == AIFeatureReadyState.NotReady)
            {
                var readyOperation = await VideoScaler.EnsureReadyAsync();
                cancellationToken.ThrowIfCancellationRequested();
                if (readyOperation.Status != AIFeatureReadyResultState.Success)
                {
                    DiagLog.Write(
                        "Export",
                        $"Windows AI VideoScaler could not be initialized ({readyOperation.Status}); using standard scaling.");
                    return null;
                }
            }

            scaler = await VideoScaler.CreateAsync();
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
        private readonly byte[] _inputBgr;
        private readonly byte[] _outputBgr;
        private readonly byte[] _outputBgra;
        private bool _disabled;
        private bool _disposed;

        public WindowsVideoSuperResolutionSession(
            VideoScaler scaler, int inputWidth, int inputHeight, int outputWidth, int outputHeight)
        {
            _scaler = scaler;

            int inputPixelCount = checked(inputWidth * inputHeight);
            int outputPixelCount = checked(outputWidth * outputHeight);
            _inputBgr = new byte[checked(inputPixelCount * 3)];
            _outputBgr = new byte[checked(outputPixelCount * 3)];
            _outputBgra = new byte[checked(outputPixelCount * 4)];

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
                var bgra = source.GetPixelBytes();
                for (int src = 0, dst = 0; dst < _inputBgr.Length; src += 4, dst += 3)
                {
                    _inputBgr[dst] = bgra[src];
                    _inputBgr[dst + 1] = bgra[src + 1];
                    _inputBgr[dst + 2] = bgra[src + 2];
                }

                var result = _scaler.ScaleImageBuffer(
                    _inputImage, _outputImage, new VideoScalerOptions());
                if (result.Status != VideoScalerStatus.Success)
                    throw new InvalidOperationException($"VideoScaler returned {result.Status}.");

                _outputImage.CopyToByteArray(_outputBgr);
                for (int src = 0, dst = 0; src < _outputBgr.Length; src += 3, dst += 4)
                {
                    _outputBgra[dst] = _outputBgr[src];
                    _outputBgra[dst + 1] = _outputBgr[src + 1];
                    _outputBgra[dst + 2] = _outputBgr[src + 2];
                    _outputBgra[dst + 3] = byte.MaxValue;
                }
                destination.SetPixelBytes(_outputBgra);
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

        public void Dispose()
        {
            if (_disposed)
                return;

            _scaler.Dispose();
            _disposed = true;
        }
    }
}
