using Windows.Storage;

namespace Musio.Core.Settings;

public record CaptureRegion(int X, int Y, int Width, int Height, string MonitorId);

/// <summary>
/// Remembers the last capture region using ApplicationData composite values.
/// </summary>
public sealed class RegionMemory
{
    private const string CompositeKey = "LastCaptureRegion";

    private readonly ApplicationDataContainer? _settings;

    public RegionMemory()
    {
        try
        {
            _settings = ApplicationData.Current.LocalSettings;
        }
        catch
        {
            _settings = null;
        }
    }

    public bool HasSavedRegion
    {
        get => _settings is not null && _settings.Values.ContainsKey(CompositeKey);
    }

    public void SaveRegion(int x, int y, int w, int h, string monitorId)
    {
        if (_settings is null) return;

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
        if (_settings is null) return null;

        try
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
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RegionMemory] Failed to load region: {ex.Message}");
        }

        return null;
    }
}
