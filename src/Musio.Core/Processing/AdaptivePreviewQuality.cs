namespace Musio.Core.Processing;

public readonly record struct PreviewResolution(int MaxWidth, int MaxHeight);

/// <summary>
/// Selects a preview resolution tier from machine capability and sustained playback load.
/// </summary>
public sealed class AdaptivePreviewQuality
{
    private static readonly PreviewResolution[] Tiers =
    [
        new(960, 540),
        new(1280, 720),
        new(1600, 900),
        new(1920, 1080),
    ];

    private const int DowngradeSamples = 12;
    private const int UpgradeSamples = 120;
    private const int ChangeCooldownSamples = 90;
    private const double DowngradeUtilization = 1.05;
    private const double UpgradeUtilization = 0.55;
    private const double EmaWeight = 0.12;

    private int _tierIndex;
    private double _utilizationEma;
    private int _samplesAtThreshold;
    private int _cooldownSamples;
    private int _thresholdDirection;

    public AdaptivePreviewQuality(int sourceWidth, int sourceHeight, int processorCount)
    {
        _tierIndex = SelectInitialTier(sourceWidth, sourceHeight, processorCount);
    }

    public PreviewResolution Current => Tiers[_tierIndex];

    public PreviewResolution? ObservePlaybackFrame(TimeSpan elapsed, int targetFps)
    {
        if (elapsed <= TimeSpan.Zero || targetFps <= 0)
            return null;

        double frameBudgetMs = 1000.0 / targetFps;
        double utilization = elapsed.TotalMilliseconds / frameBudgetMs;
        _utilizationEma = _utilizationEma <= 0
            ? utilization
            : (_utilizationEma * (1 - EmaWeight)) + (utilization * EmaWeight);

        if (_cooldownSamples > 0)
        {
            _cooldownSamples--;
            return null;
        }

        if (_utilizationEma > DowngradeUtilization && _tierIndex > 0)
        {
            CountThreshold(-1);
            if (_samplesAtThreshold >= DowngradeSamples)
                return ProposeTier(_tierIndex - 1);
        }
        else if (_utilizationEma < UpgradeUtilization && _tierIndex < Tiers.Length - 1)
        {
            CountThreshold(1);
            if (_samplesAtThreshold >= UpgradeSamples)
                return ProposeTier(_tierIndex + 1);
        }
        else
        {
            _samplesAtThreshold = 0;
            _thresholdDirection = 0;
        }

        return null;
    }

    public void Commit(PreviewResolution resolution)
    {
        int tierIndex = Array.IndexOf(Tiers, resolution);
        if (tierIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(resolution));

        _tierIndex = tierIndex;
        _samplesAtThreshold = 0;
        _thresholdDirection = 0;
        _cooldownSamples = ChangeCooldownSamples;
        _utilizationEma = 0;
    }

    public void RejectChange()
    {
        _samplesAtThreshold = 0;
        _thresholdDirection = 0;
        _cooldownSamples = DowngradeSamples;
    }

    internal static PreviewResolution SelectInitial(
        int sourceWidth, int sourceHeight, int processorCount)
        => Tiers[SelectInitialTier(sourceWidth, sourceHeight, processorCount)];

    private static int SelectInitialTier(int sourceWidth, int sourceHeight, int processorCount)
    {
        int capabilityTier = processorCount switch
        {
            >= 8 => 3,
            >= 6 => 2,
            >= 4 => 1,
            _ => 0,
        };

        int sourceTier = 0;
        for (int i = 0; i < Tiers.Length; i++)
        {
            sourceTier = i;
            if (sourceWidth <= Tiers[i].MaxWidth && sourceHeight <= Tiers[i].MaxHeight)
                break;
        }

        return Math.Min(capabilityTier, sourceTier);
    }

    private void CountThreshold(int direction)
    {
        if (_thresholdDirection != direction)
        {
            _thresholdDirection = direction;
            _samplesAtThreshold = 0;
        }

        _samplesAtThreshold++;
    }

    private PreviewResolution ProposeTier(int tierIndex)
    {
        _samplesAtThreshold = 0;
        _thresholdDirection = 0;
        return Tiers[tierIndex];
    }
}
