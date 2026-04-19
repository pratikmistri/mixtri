using Windows.Storage;

namespace Musio.Core.Settings;

public record CaptureRegion(int X, int Y, int Width, int Height, string MonitorId);

/// <summary>
/// Remembers the last capture region using ApplicationData composite values.
/// </summary>
public sealed class RegionMemory
{
    private const string CompositeKey = "LastCaptureRegion";

    private readonly ApplicationDataContainer _settings;

    public RegionMemory()
    {
        _settings = ApplicationData.Current.LocalSettings;
    }

    public bool HasSavedRegion
    {
        get => _settings.Values.ContainsKey(CompositeKey);
    }

    public void SaveRegion(int x, int y, int w, int h, string monitorId)
    {
        var composite = new ApplicationDataCompositeValue
        {
            ["X"] = x,
            ["Y"] = y,
            ["Width"] = w,
            ["Height"] = h,
            ["MonitorId"] = monitorId
        };

        _settings.Values[CompositeKey] = composite;
    }

    public CaptureRegion? LoadRegion()
    {
        if (_settings.Values.TryGetValue(CompositeKey, out var value)
            && value is ApplicationDataCompositeValue composite)
        {
            return new CaptureRegion(
                (int)composite["X"],
                (int)composite["Y"],
                (int)composite["Width"],
                (int)composite["Height"],
                (string)composite["MonitorId"]
            );
        }

        return null;
    }
}
