using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Musio.Core.Audio;

/// <summary>
/// One window, in the loaded file's own position (seconds from the start of that WAV),
/// across which <see cref="AudioPlaybackEngine"/> should ramp gain using
/// <see cref="EqualPowerCrossfade"/> instead of playing at a flat 1.0.
/// </summary>
/// <param name="Start">Where the ramp begins, measured from the start of the file.</param>
/// <param name="Duration">How long the ramp runs.</param>
/// <param name="IsFadeIn">
/// <c>true</c> to ramp from 0 up to 1 (<see cref="EqualPowerCrossfade.InGain"/>); <c>false</c>
/// to ramp from 1 down to 0 (<see cref="EqualPowerCrossfade.OutGain"/>).
/// </param>
public readonly record struct AudioFadeWindow(TimeSpan Start, TimeSpan Duration, bool IsFadeIn)
{
    /// <summary>The instant this window's ramp finishes.</summary>
    public TimeSpan End => Start + Duration;
}

/// <summary>
/// Plays back one or more WAV files in sync with the editor preview.
/// Mixes multiple sources (system audio + mic) into a single output.
/// </summary>
/// <remarks>
/// <para><b>Not segment/placement-aware.</b> Unlike the exporter (<see
/// cref="Export.ExportAudioPlan"/>/<see cref="Export.VideoEncoder"/>), this engine plays
/// each loaded file as ONE continuous stream, seeked directly from the video playhead's
/// OUTPUT time through a constant per-file offset (see
/// <c>EditorPage.AudioPositionForVideo</c>) — it has no <c>TimelineModel</c> and does not
/// re-cut audio at segment boundaries the way exported audio does. This predates the T7
/// transition-crossfade feature.</para>
/// <para><b>What T7 added here.</b> <see cref="Load"/> accepts an optional set of
/// <see cref="AudioFadeWindow"/>s per file and, when given any, wraps that file's samples
/// in a gain ramp using the same equal-power curve
/// (<see cref="Musio.Core.Audio.EqualPowerCrossfade"/>) the (documented, export-side)
/// transition fade metadata on <c>AudioPlacement</c> describes — so preview and export
/// agree on what curve a crossfade SHOULD use, and preview COULD actually apply it (export
/// cannot — see <see cref="Export.ExportAudioPlan"/>'s and
/// <see cref="Export.VideoEncoder.ApplyPlacement"/>'s remarks for exactly why).</para>
/// <para><b>T9 investigated wiring this and concluded it is NOT feasible without faking
/// the mapping — every existing call site (<c>EditorPage.xaml.cs</c>) still calls the
/// parameterless <see cref="Load(IEnumerable{string})"/> overload deliberately, not because
/// nobody got to it.</b> <see cref="AudioFadeWindow.Start"/> is measured from the loaded
/// file's OWN start (native <see cref="NAudio.Wave.AudioFileReader.CurrentTime"/>), because
/// that is the only clock <see cref="EqualPowerFadeSampleProvider"/> can compare against —
/// there is no per-segment placement here to give it anything else. Producing a correct
/// window therefore means converting a <see cref="Timeline.TransitionResolver"/> boundary's
/// OUTPUT-timeline instant into that same native-file instant. Three separate facts make
/// that conversion impossible to do honestly with today's engine:
/// <list type="number">
/// <item>The files this engine ever loads are the PROJECT-level continuous system/mic
/// recordings (<c>project.AudioFilePaths</c>, filtered only by mute state in
/// <c>EditorPage.GetUnmutedAudioPaths</c>) mapped via a single constant offset
/// (<c>EditorPage.AudioPositionForVideo</c>), not <c>EditorPage.MapToSourceTime</c> (which
/// DOES account for cuts, via the timeline's frame mapper). A transition boundary exists
/// only where the timeline has a cut/trim/reorder/insertion — exactly the condition under
/// which "output time minus a constant" stops corresponding to the right instant in the
/// raw file. Placing a ramp there via the constant offset would land it at an arbitrary
/// point in unrelated, still-continuously-playing source audio, not at the actual moment
/// of the edit.</item>
/// <item>A boundary between a <c>VideoSegment</c> and a <c>TextSlideSegment</c> — the most
/// common case, since it is the legacy 500&#160;ms fallback <see
/// cref="Timeline.TransitionResolver"/> applies to every unconfigured slide edge — has an
/// incoming or outgoing side with no recorded audio at all. There is no file position for
/// half of that crossfade to reference, in principle, regardless of offset arithmetic.</item>
/// <item>Appended recordings' own audio files (see <c>VideoSegment.AudioFilePaths</c>,
/// used in <c>EditorPage.LoadAppendedTrackVisualsAsync</c> only to build timeline waveform
/// visuals) are never passed to this engine at all — only the primary recording's
/// project-level tracks are. So most cross-recording transition boundaries in a multi-clip
/// project have no loaded file to attach a window to in the first place, independent of
/// point 1 above.</item>
/// </list>
/// Fixing this for real means rebuilding this engine's preview call sites around the same
/// per-segment/per-placement model <see cref="Export.ExportAudioPlan.BuildFromSegments"/>
/// already uses for export (delay + trim + take-duration keyed to each segment, not one
/// offset per whole project) — a materially larger architectural change than adding a
/// windows parameter, and explicitly out of scope for "wire the existing overload up".
/// Shipping a crossfade computed through the constant-offset mapping instead would drift
/// the instant the user trims, reorders, deletes, or inserts a slide — i.e. on every
/// project that actually has a transition to show — which is strictly worse than today's
/// honest hard cut. <see cref="AudioFadeWindow"/>/<see cref="EqualPowerFadeSampleProvider"/>
/// remain correct, tested, reusable machinery, ready for a future task that makes this
/// engine placement-aware.</para>
/// </remarks>
public sealed class AudioPlaybackEngine : IDisposable
{
    private WaveOutEvent? _outputDevice;
    private MixingSampleProvider? _mixer;
    private readonly List<AudioFileReader> _readers = [];
    private readonly List<MediaFoundationResampler> _resamplers = [];
    private readonly object _transportLock = new();
    private readonly object _scrubQueueLock = new();
    private Timer? _scrubTimer;
    private TimeSpan? _queuedScrubPosition;
    private bool _scrubWorkerRunning;
    private bool _disposed;

    /// <summary>
    /// Initializes playback from the given WAV file paths.
    /// All files are mixed together for simultaneous playback.
    /// </summary>
    public void Load(IEnumerable<string> wavFilePaths) => Load(wavFilePaths, fadeWindowsByPath: null);

    /// <summary>
    /// Initializes playback from the given WAV file paths, applying an equal-power gain
    /// ramp (see <see cref="AudioFadeWindow"/>) to any file with an entry in
    /// <paramref name="fadeWindowsByPath"/>. All files are mixed together for simultaneous
    /// playback. Paths are matched case-insensitively.
    /// </summary>
    public void Load(
        IEnumerable<string> wavFilePaths,
        IReadOnlyDictionary<string, IReadOnlyList<AudioFadeWindow>>? fadeWindowsByPath)
    {
        Stop();
        DisposeReaders();
        _outputDevice?.Dispose();
        _outputDevice = null;
        _mixer = null;

        var validPaths = wavFilePaths.Where(File.Exists).ToList();
        if (validPaths.Count == 0) return;

        // The caller's dictionary carries whatever comparer it was built with — typically the
        // default ordinal one — so looking paths up directly would quietly miss on any casing
        // difference and disable the fade rather than fail loudly. Re-key into an explicitly
        // case-insensitive map so the documented matching behaviour is actually this method's,
        // not the caller's to get right.
        Dictionary<string, IReadOnlyList<AudioFadeWindow>>? fadeWindows = null;
        if (fadeWindowsByPath is { Count: > 0 })
        {
            fadeWindows = new Dictionary<string, IReadOnlyList<AudioFadeWindow>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var (path, windows) in fadeWindowsByPath)
                fadeWindows[path] = windows;
        }

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

            for (int i = 0; i < _readers.Count; i++)
            {
                var reader = _readers[i];
                IReadOnlyList<AudioFadeWindow>? windows = null;
                bool hasFade = fadeWindows is not null
                    && fadeWindows.TryGetValue(validPaths[i], out windows)
                    && windows.Count > 0;

                // `reader` doubles as both the sample source AND the position reference
                // when applying the fade: AudioFileReader implements ISampleProvider
                // directly (against its own native format), and its CurrentTime reflects
                // real elapsed file time, staying correct across Seek()/ScrubTo() since
                // those set `reader.Position` directly.
                ISampleProvider sampleProvider = hasFade
                    ? new EqualPowerFadeSampleProvider(reader, reader, windows!)
                    : reader;

                // Resample if needed to match mixer format. The fade (if any) is applied
                // BEFORE this step, directly against the reader's own native samples/rate
                // — not after, downstream of MediaFoundationResampler — because the
                // resampler buffers/reads ahead internally, which would otherwise offset
                // the ramp from what is actually audible at any given instant. Once faded,
                // the (now float, IEEE) samples are converted back to a byte-based
                // IWaveProvider via SampleToWaveProvider so MediaFoundationResampler (which
                // wants an IWaveProvider) can still resample the ALREADY-faded signal.
                if (reader.WaveFormat.SampleRate != _mixer.WaveFormat.SampleRate
                    || reader.WaveFormat.Channels != _mixer.WaveFormat.Channels)
                {
                    IWaveProvider resamplerSource = hasFade
                        ? new SampleToWaveProvider(sampleProvider)
                        : reader;
                    var resampled = new MediaFoundationResampler(resamplerSource,
                        WaveFormat.CreateIeeeFloatWaveFormat(
                            _mixer.WaveFormat.SampleRate, _mixer.WaveFormat.Channels));
                    _resamplers.Add(resampled);
                    sampleProvider = resampled.ToSampleProvider();
                }

                _mixer.AddMixerInput(sampleProvider);
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
            SeekCore(position);
        }
    }

    /// <summary>
    /// Plays a short burst of audio at the given position for scrub feedback.
    /// Automatically stops after a brief silence gap (no new scrub events).
    /// </summary>
    public void ScrubTo(TimeSpan position)
    {
        if (_outputDevice is null) return;

        lock (_scrubQueueLock)
        {
            _scrubTimer?.Dispose();
            _scrubTimer = null;
            _queuedScrubPosition = position;
            if (_scrubWorkerRunning)
                return;

            _scrubWorkerRunning = true;
        }

        ThreadPool.QueueUserWorkItem(_ => ProcessScrubQueue());
    }

    private void ProcessScrubQueue()
    {
        while (true)
        {
            TimeSpan position;
            lock (_scrubQueueLock)
            {
                if (_disposed || _queuedScrubPosition is not { } queued)
                {
                    _scrubWorkerRunning = false;
                    return;
                }

                position = queued;
                _queuedScrubPosition = null;
            }

            lock (_transportLock)
            {
                if (_disposed)
                    continue;

                SeekCore(position);
                try { _outputDevice?.Play(); } catch { }
            }

            lock (_scrubQueueLock)
            {
                if (_queuedScrubPosition.HasValue)
                    continue;

                _scrubWorkerRunning = false;
                if (_disposed)
                    return;

                ResetScrubStopTimer();
                return;
            }
        }
    }

    private void SeekCore(TimeSpan position)
    {
        try { _outputDevice?.Stop(); } catch { }

        foreach (var reader in _readers)
        {
            try
            {
                long targetBytes = (long)(position.TotalSeconds
                    * reader.WaveFormat.AverageBytesPerSecond);
                targetBytes -= targetBytes % reader.WaveFormat.BlockAlign;
                reader.Position = Math.Clamp(targetBytes, 0, reader.Length);
            }
            catch { }
        }
    }

    private void ResetScrubStopTimer()
    {
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
        lock (_scrubQueueLock)
        {
            _queuedScrubPosition = null;
            _scrubTimer?.Dispose();
            _scrubTimer = null;
        }
        Stop();
        _outputDevice?.Dispose();
        _outputDevice = null;
        _mixer = null;
        DisposeReaders();
    }

    private void DisposeReaders()
    {
        foreach (var resampler in _resamplers)
        {
            try { resampler.Dispose(); } catch { }
        }
        _resamplers.Clear();

        foreach (var reader in _readers)
        {
            try { reader.Dispose(); } catch { }
        }
        _readers.Clear();
    }

    /// <summary>
    /// Wraps a source <see cref="ISampleProvider"/> and multiplies its samples by an
    /// equal-power gain (see <see cref="EqualPowerCrossfade"/>) whenever the CURRENT
    /// playback position, per <paramref name="position"/>, falls inside one of
    /// <paramref name="windows"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="position"/> is deliberately a separate <see cref="AudioFileReader"/>
    /// reference rather than something this class tracks internally by counting samples:
    /// counting would desync the instant <see cref="Seek"/>/<see cref="ScrubTo"/> moves
    /// <c>reader.Position</c> directly, whereas re-reading <see cref="AudioFileReader.CurrentTime"/>
    /// every buffer is always correct, including immediately after a seek.
    /// </para>
    /// <para>
    /// <b>Must run BEFORE any resampling, not after.</b> <see cref="Load"/> wires this
    /// provider directly against the raw <see cref="AudioFileReader"/> (so
    /// <paramref name="source"/> and <paramref name="position"/> are literally the same
    /// object) and only resamples the ALREADY-faded output afterward. Wrapping a
    /// <c>MediaFoundationResampler</c>-derived provider instead would desync the ramp from
    /// what is actually audible: the resampler reads ahead and buffers internally, so
    /// <c>position.CurrentTime</c> would reflect a point further into the file than what the
    /// CURRENT output buffer actually contains.
    /// </para>
    /// <para>
    /// <b>Position is captured BEFORE reading, not after.</b> <see cref="Read"/> captures
    /// <c>position.CurrentTime</c> before calling <c>source.Read</c>: reading advances the
    /// reader by the whole buffer, so capturing afterward would timestamp the buffer's
    /// FIRST sample with the position of its LAST — a fixed, buffer-sized offset (worse at
    /// this engine's ~100ms <c>WaveOutEvent.DesiredLatency</c>) that would show up as the
    /// ramp starting audibly early or late. Because the fade is applied pre-resampling
    /// (see above), a window spanning multiple buffers ramps smoothly across the boundary
    /// with no extra handling needed — each frame's own time is computed independently
    /// from the correctly-captured buffer-start position.
    /// </para>
    /// </remarks>
    private sealed class EqualPowerFadeSampleProvider(
        ISampleProvider source, AudioFileReader position, IReadOnlyList<AudioFadeWindow> windows)
        : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            // Capture the position of the FIRST sample this call will produce before
            // `source.Read` advances the reader past the whole buffer (see remarks).
            double startSeconds = position.CurrentTime.TotalSeconds;
            int samplesRead = source.Read(buffer, offset, count);
            if (samplesRead <= 0 || windows.Count == 0) return samplesRead;

            int channels = Math.Max(WaveFormat.Channels, 1);
            double perFrameSeconds = 1.0 / WaveFormat.SampleRate;

            int frames = samplesRead / channels;
            for (int frame = 0; frame < frames; frame++)
            {
                // Per-frame time within this buffer, so a fade ramps smoothly across it
                // instead of stepping once per NAudio callback (~100ms at this engine's
                // WaveOutEvent.DesiredLatency).
                double gain = GainAt(startSeconds + frame * perFrameSeconds);
                if (gain == 1.0) continue;

                int baseIndex = offset + frame * channels;
                for (int ch = 0; ch < channels; ch++)
                    buffer[baseIndex + ch] = (float)(buffer[baseIndex + ch] * gain);
            }

            return samplesRead;
        }

        /// <summary>
        /// Indexed <c>for</c> loop deliberately, not <c>foreach</c>: <paramref name="windows"/>
        /// is typed as the interface <see cref="IReadOnlyList{T}"/>, and <c>foreach</c> over
        /// an interface-typed sequence allocates a boxed enumerator every call — this method
        /// runs once per audio FRAME (tens of thousands of times per second at typical
        /// sample rates), on the real-time audio thread, so that allocation is not
        /// acceptable here.
        /// </summary>
        private double GainAt(double timeSeconds)
        {
            double gain = 1.0;
            for (int i = 0; i < windows.Count; i++)
            {
                var window = windows[i];
                double start = window.Start.TotalSeconds;
                double end = window.End.TotalSeconds;

                if (timeSeconds < start)
                {
                    // Before this window starts, it does not affect gain at all — this
                    // is unrelated (earlier) content in the same continuously-played file,
                    // not something this specific window governs.
                    continue;
                }

                if (timeSeconds >= end)
                {
                    // At/after this window's end: a fade-IN has completed, so it no longer
                    // affects gain (matches EqualPowerCrossfade.InGain(1)==1, i.e. leaving
                    // `gain` untouched is already correct). A fade-OUT, however, has
                    // finished dropping to silence by this instant and MUST STAY silent —
                    // the placement it represents has conceptually stopped playing here
                    // (mirroring how VideoEncoder truncates TakeDuration at exactly this
                    // same duration on export) — rather than snapping back to full volume,
                    // which would be an audible "pop" immediately after every crossfade.
                    if (!window.IsFadeIn) gain = 0.0;
                    continue;
                }

                double t = (timeSeconds - start) / window.Duration.TotalSeconds;
                gain *= window.IsFadeIn ? EqualPowerCrossfade.InGain(t) : EqualPowerCrossfade.OutGain(t);
            }

            return gain;
        }
    }
}
