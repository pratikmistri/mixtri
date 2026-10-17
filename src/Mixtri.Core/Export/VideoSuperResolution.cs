using Microsoft.Graphics.Canvas;

namespace Mixtri.Core.Export;

public interface IVideoSuperResolutionProvider
{
    Task<IVideoSuperResolutionSession?> TryCreateAsync(
        int inputWidth, int inputHeight, int outputWidth, int outputHeight,
        CancellationToken cancellationToken);
}

public interface IVideoSuperResolutionSession : IDisposable
{
    bool TryScale(CanvasRenderTarget source, CanvasRenderTarget destination);
}

public static class VideoSuperResolutionSupport
{
    public static bool SupportsDimensions(
        int inputWidth, int inputHeight, int outputWidth, int outputHeight)
    {
        return inputWidth > 0 && inputHeight > 0 &&
            outputWidth > 0 && outputHeight > 0 &&
            Math.Min(inputWidth, inputHeight) >= 240 &&
            Math.Min(inputWidth, inputHeight) <= 1440 &&
            outputWidth >= inputWidth && outputHeight >= inputHeight &&
            (outputWidth > inputWidth || outputHeight > inputHeight) &&
            Math.Max(outputWidth, outputHeight) <= 3840;
    }
}
