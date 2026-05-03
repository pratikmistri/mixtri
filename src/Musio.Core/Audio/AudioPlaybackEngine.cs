using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Musio.Core.Audio;

/// <summary>
/// Plays back one or more WAV files in sync with the editor preview.
/// Mixes multiple sources (system audio + mic) into a single output.
/// </summary>
public sealed class AudioPlaybackEngine : IDisposable
{
    private WaveOutEvent? _outputDevice;
    private MixingSampleProvider? _mixer;
    private readonly List<AudioFileReader> _readers = [];
    private readonly object _transportLock = new();
    private Timer? _scrubTimer;
    private bool _disposed;

    /// <summary>
    /// Initializes playback from the given WAV file paths.
    /// All files are mixed together for simultaneous playback.
    /// </summary>
    public void Load(IEnumerable<string> wavFilePaths)
    {
        Stop();
        DisposeReaders();
        _outputDevice?.Dispose();
        _outputDevice = null;
        _mixer = null;

        var validPaths = wavFilePaths.Where(File.Exists).ToList();
        if (validPaths.Count == 0) return;

        try
        {
            // Open all audio files
            foreach (var path in validPaths)
            {
                var reader = new AudioFileReader(path);
                _readers.Add(reader);
            }

            // Create mixer matching the first file's format
            var first = _readers[0];
            _mixer = new MixingSampleProvider(
                WaveFormat.CreateIeeeFloatWaveFormat(first.WaveFormat.SampleRate, first.WaveFormat.Channels))
            {
                ReadFully = true
            };

            foreach (var reader in _readers)
            {
                // Resample if needed to match mixer format
                if (reader.WaveFormat.SampleRate != _mixer.WaveFormat.SampleRate
                    || reader.WaveFormat.Channels != _mixer.WaveFormat.Channels)
                {
                    var resampled = new MediaFoundationResampler(reader,
                        WaveFormat.CreateIeeeFloatWaveFormat(
                            _mixer.WaveFormat.SampleRate, _mixer.WaveFormat.Channels));
                    _mixer.AddMixerInput(resampled.ToSampleProvider());
                }
                else
                {
                    _mixer.AddMixerInput((ISampleProvider)reader);
                }
            }

            _outputDevice = new WaveOutEvent { DesiredLatency = 100 };
            _outputDevice.Init(_mixer);
        }
        catch
        {
            DisposeReaders();
            _outputDevice?.Dispose();
            _outputDevice = null;
            _mixer = null;
        }
    }

    public void Play()
    {
        lock (_transportLock)
        {
            if (_outputDevice?.PlaybackState != PlaybackState.Playing)
                _outputDevice?.Play();
        }
    }

    public void Pause()
    {
        lock (_transportLock)
        {
            // Use Stop to clear internal audio buffers so that after
            // seeking, Play() starts from the new position cleanly.
            try { _outputDevice?.Stop(); } catch { }
        }
    }

    public void Stop()
    {
        lock (_transportLock)
        {
            try { _outputDevice?.Stop(); } catch { }
        }
    }

    /// <summary>
    /// Seeks all audio streams to the given position.
    /// </summary>
    public void Seek(TimeSpan position)
    {
        lock (_transportLock)
        {
            // Stop output before repositioning to avoid racing the audio thread
            // and to ensure subsequent playback starts from the new position.
            try { _outputDevice?.Stop(); } catch { }

            foreach (var reader in _readers)
            {
                try
                {
                    long targetBytes = (long)(position.TotalSeconds
                        * reader.WaveFormat.AverageBytesPerSecond);
                    // Align to block boundary
                    targetBytes -= targetBytes % reader.WaveFormat.BlockAlign;
                    reader.Position = Math.Clamp(targetBytes, 0, reader.Length);
                }
                catch { /* best-effort seek */ }
            }
        }
    }

    /// <summary>
    /// Plays a short burst of audio at the given position for scrub feedback.
    /// Automatically stops after a brief silence gap (no new scrub events).
    /// </summary>
    public void ScrubTo(TimeSpan position)
    {
        if (_outputDevice is null) return;

        lock (_transportLock)
        {
            try { _outputDevice.Stop(); } catch { }
            Seek(position);
            try { _outputDevice.Play(); } catch { }
        }

        // Auto-stop after 80ms of no new scrub events
        _scrubTimer?.Dispose();
        _scrubTimer = new Timer(_ =>
        {
            lock (_transportLock)
            {
                try { _outputDevice?.Stop(); } catch { }
            }
        }, null, 80, Timeout.Infinite);
    }

    public bool IsLoaded => _outputDevice is not null;
    public bool IsPlaying => _outputDevice?.PlaybackState == PlaybackState.Playing;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scrubTimer?.Dispose();
        _scrubTimer = null;
        Stop();
        _outputDevice?.Dispose();
        _outputDevice = null;
        _mixer = null;
        DisposeReaders();
    }

    private void DisposeReaders()
    {
        foreach (var reader in _readers)
        {
            try { reader.Dispose(); } catch { }
        }
        _readers.Clear();
    }
}
