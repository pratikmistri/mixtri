using Mixtri.Core.Diagnostics;
using Mixtri.Core.Models;
using Mixtri_App.Services;

namespace Mixtri_App.Pages;

public sealed partial class EditorPage
{
    private bool _previewSuspended;
    private bool _previewNeedsRestore;
    private int _activePreviewInitializations;
    private readonly SemaphoreSlim _previewVisibilityGate = new(1, 1);
    private CancellationTokenSource _previewWorkCts = new();
    private Project? _defaultsAppliedProject;
    private CancellationTokenSource? _waveformWorkCts;
    private int _waveformEpoch;
    private int _activeWaveformJobs;
    private readonly Dictionary<string, int> _pendingInsertedWaveforms = new(StringComparer.OrdinalIgnoreCase);

    private CancellationToken WaveformWorkToken =>
        (_waveformWorkCts ??= CancellationTokenSource.CreateLinkedTokenSource(_previewWorkCts.Token)).Token;

    private bool IsWaveformWorkCurrent(int generation, CancellationToken ct) =>
        !ct.IsCancellationRequested && !_previewSuspended && !_pageUnloaded && generation == _previewInitGeneration;

    private void CancelWaveformWork()
    {
        _waveformEpoch++;
        var previous = _waveformWorkCts;
        _waveformWorkCts = null;
        _pendingInsertedWaveforms.Clear();
        previous?.Cancel();
        previous?.Dispose();
    }

    private async Task<T> RunWaveformWorkAsync<T>(
        Func<CancellationToken, T> build, CancellationToken ct, TimeSpan delay = default)
    {
        _activeWaveformJobs++;
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            return await Task.Run(() => build(ct), ct);
        }
        finally { _activeWaveformJobs--; }
    }

    /// <summary>Releases derived media state while hidden, without changing the project or undo history.</summary>
    public async Task SetPreviewVisibilityAsync(bool visible)
    {
        _previewSuspended = !visible;
        if (!visible)
        {
            _previewNeedsRestore = true;
            Preview.Pause();
            _pendingRenderPosition = null;
            _pendingRenderForce = false;
            _previewInitGeneration++;
            CancelWaveformWork();
            _insertedAudioGeneration++;
            _stretchedAudioGeneration++;
            CancelThumbnailGeneration();
            _previewWorkCts.Cancel();
            _graphicsDeviceManager.Detach();
        }

        await _previewVisibilityGate.WaitAsync();
        try
        {
            if (_pageUnloaded || visible == _previewSuspended) return;
            if (!visible)
            {
                // Drain code still using the old resources; generations reject late background results.
                while (_isRendering || _rebuildingPreviewRenderer || _activePreviewInitializations > 0
                    || _activeThumbnailGenerations > 0 || _activeWaveformJobs > 0
                    || _graphicsDeviceManager.IsRecoveryInProgress)
                {
                    await Task.Delay(25);
                    if (!_previewSuspended || _pageUnloaded) return;
                }

                // One already-presented frame avoids a blank flash while the reader warms
                // on restore. Closing the project still clears it through the ordinary reset.
                ResetPreviewToEmptyState(preservePresentedFrame: ProjectService.Instance.CurrentProject is not null);
                Timeline.ReleaseRenderResources();
                ReleaseWallpaperThumbnails();
                _insertedAudioWaveforms.Clear();
                var disposals = _resourceDisposals.ToArray();
                _resourceDisposals.Clear();
                await Task.WhenAll(disposals);
                if (!ExportVM.IsExporting && !ProjectService.Instance.IsSaveInFlight
                    && ShellCoordinator.Instance?.CurrentState != Mixtri.Core.Shell.AppShellState.Recording)
                    Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice().Trim();
                DiagLog.Write("Editor", "hidden: preview decoders, render targets, thumbnails and audio released");
            }
            else
            {
                if (!_previewNeedsRestore) return;
                while (_isRendering || _rebuildingPreviewRenderer || _activePreviewInitializations > 0
                    || _activeWaveformJobs > 0)
                {
                    await Task.Delay(25);
                    if (_previewSuspended || _pageUnloaded) return;
                }
                _previewWorkCts.Dispose();
                _previewWorkCts = new();
                if (ProjectService.Instance.CurrentProject is not null || !ViewModel.Model.IsEmpty)
                    _graphicsDeviceManager.Attach();
                await InitializePreviewCoreAsync();
                if (!_previewSuspended && !_pageUnloaded)
                {
                    await UpdatePreviewFrameAsync(ViewModel.Model.PlayheadPosition, force: true);
                    _previewNeedsRestore = false;
                }
                DiagLog.Write("Editor", "visible: preview resources restored");
            }
        }
        catch (Exception ex)
        {
            _previewNeedsRestore = true;
            DiagLog.Write("Editor", $"preview visibility transition failed: {ex}");
        }
        finally
        {
            _previewVisibilityGate.Release();
        }
    }
}
