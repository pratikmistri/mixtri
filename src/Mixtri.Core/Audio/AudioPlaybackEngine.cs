using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Mixtri.Core.Audio;

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
/// One inserted audio file placed at a chosen instant on the OUTPUT timeline, as
/// <see cref="AudioPlaybackEngine.LoadPlacements"/> plays it.
/// </summary>
/// <remarks>
/// This is the preview mirror of <see cref="Export.AudioPlacement"/>, and deliberately the
/// ONE thing this engine positions by output time rather than by a source file's own clock:
/// an inserted voice-over/music track (<see cref="Models.AudioTrack"/>) is anchored to the
/// finished timeline, so output time IS its native clock and none of the segment-mapping
/// problems documented on <see cref="AudioPlaybackEngine"/> apply to it.
/// </remarks>
/// <param name="FilePath">WAV to play.</param>
/// <param name="OutputStart">Where the track starts on the output timeline.</param>
/// <param name="TrimStart">How far into the file playback begins.</param>
/// <param name="Duration">How long the track sounds for.</param>
/// <param name="Volume">Constant gain, 0..1.</param>
public readonly record struct AudioTimelinePlacement(
    string FilePath,
    TimeSpan OutputStart,
    TimeSpan TrimStart,
    TimeSpan Duration,
    float Volume)
{
    /// <summary>Where this placement stops sounding on the output timeline.</summary>
    public TimeSpan OutputEnd => OutputStart + Duration;

    /// <summary>
    /// The position inside the file that <paramref name="outputPosition"/> maps to, or
    /// <c>null</c> when this track is silent there (before it starts, or after it ends).
    /// </summary>
    public TimeSpan? FilePositionFor(TimeSpan outputPosition)
    {
        if (outputPosition < OutputStart || outputPosition >= OutputEnd) return null;
        return TrimStart + (outputPosition - OutputStart);
    }
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
/// (<see cref="Mixtri.Core.Audio.EqualPowerCrossfade"/>) the (documented, export-side)
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

    /// <summary>
    /// Output-timeline placement of each entry in <see cref="_readers"/>, by index, or empty
    /// when the engine was loaded with plain paths. Non-empty switches
    /// <see cref="SeekCore"/> from "every reader to the same file position" to a per-reader
    /// mapping — see <see cref="LoadPlacements"/>.
    /// </summary>
    private readonly List<AudioTimelinePlacement> _placements = [];

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
    /// Initializes playback from the given WAV file paths, applying a constant per-file gain
    /// from <paramref name="volumeByPath"/> (missing entries play at full volume). Paths are
    /// matched case-insensitively.
    /// </summary>
    public void Load(
        IEnumerable<string> wavFilePaths,
        IReadOnlyDictionary<string, float>? volumeByPath)
        => Load(wavFilePaths, fadeWindowsByPath: null, volumeByPath);

    /// <summary>
    /// Initializes playback from the given WAV file paths, applying an equal-power gain
    /// ramp (see <see cref="AudioFadeWindow"/>) to any file with an entry in
    /// <paramref name="fadeWindowsByPath"/>. All files are mixed together for simultaneous
    /// playback. Paths are matched case-insensitively.
    /// </summary>
    public void Load(
        IEnumerable<string> wavFilePaths,
        IReadOnlyDictionary<string, IReadOnlyList<AudioFadeWindow>>? fadeWindowsByPath)
        => Load(wavFilePaths, fadeWindowsByPath, volumeByPath: null);

    private void Load(
        IEnumerable<string> wavFilePaths,
        IReadOnlyDictionary<string, IReadOnlyList<AudioFadeWindow>>? fadeWindowsByPath,
        IReadOnlyDictionary<string, float>? volumeByPath)
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

        // Same re-keying rationale as the fade windows above: a casing mismatch here would
        // silently play a track at full volume instead of the level the user set.
        Dictionary<string, float>? volumes = null;
        if (volumeByPath is { Count: > 0 })
        {
            volumes = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, volume) in volumeByPath)
                volumes[path] = volume;
        }

        try
        {
            // Open all audio files
            foreach (var path in validPaths)
            {
                var reader = new AudioFileReader(path);

                // AudioFileReader applies this to the samples it returns, so it survives
                // every Seek/ScrubTo without a separate provider in the chain.
                if (volumes is not null && volumes.TryGetValue(path, out float volume))
                    reader.Volume = Math.Clamp(volume, 0f, 1f);

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

    /// <summary>
    /// Initializes playback from inserted timeline-positioned tracks (voice-over, music),
    /// each at its own output-timeline offset and constant gain. Drive it with
    /// <see cref="SyncTo"/> rather than <see cref="Seek"/>, which takes an output-timeline
    /// position here rather than a file position.
    /// </summary>
    /// <remarks>
    /// Kept as a separate engine instance from the recorded-audio one on purpose: the two
    /// are seeked in DIFFERENT clocks. Recorded tracks are positioned by the caller in the
    /// primary recording's own file time (<c>EditorPage.AudioPositionForVideo</c>, which
    /// maps through segments); inserted tracks are positioned in output time directly. One
    /// engine cannot be seeked in both at once, and conflating them is exactly the drift
    /// this type's remarks describe.
    /// </remarks>
    public void LoadPlacements(IEnumerable<AudioTimelinePlacement> placements)
    {
        ArgumentNullException.ThrowIfNull(placements);

        Stop();
        DisposeReaders();
        _outputDevice?.Dispose();
        _outputDevice = null;
        _mixer = null;

        var valid = placements
            .Where(p => !string.IsNullOrWhiteSpace(p.FilePath)
                        && p.Duration > TimeSpan.Zero
                        && p.Volume > 0
                        && File.Exists(p.FilePath))
            .ToList();
        if (valid.Count == 0) return;

        try
        {
            foreach (var placement in valid)
            {
                _readers.Add(new AudioFileReader(placement.FilePath));
                _placements.Add(placement);
            }

            var first = _readers[0];
            _mixer = new MixingSampleProvider(
                WaveFormat.CreateIeeeFloatWaveFormat(first.WaveFormat.SampleRate, first.WaveFormat.Channels))
            {
                ReadFully = true
            };

            for (int i = 0; i < _readers.Count; i++)
            {
                var reader = _readers[i];

                // AudioFileReader has its own Volume, applied to the samples it returns —
                // no extra provider needed, and it survives every Seek/ScrubTo.
                reader.Volume = Math.Clamp(_placements[i].Volume, 0f, 1f);

                // CRITICAL: pad to silence BEFORE anything downstream sees the reader.
                // A placed track is parked at EOF wherever it is silent (before it starts,
                // after it ends), so it returns 0 samples — and MixingSampleProvider
                // permanently REMOVES any input that reads 0, while MediaFoundationResampler
                // latches end-of-input. Either would silently drop a track that simply had
                // not started yet, which is every track not placed at 00:00.
                ISampleProvider sampleProvider = new SilencePaddedSampleProvider(reader);

                if (reader.WaveFormat.SampleRate != _mixer.WaveFormat.SampleRate
                    || reader.WaveFormat.Channels != _mixer.WaveFormat.Channels)
                {
                    var resampled = new MediaFoundationResampler(
                        new SampleToWaveProvider(sampleProvider),
                        WaveFormat.CreateIeeeFloatWaveFormat(
                            _mixer.WaveFormat.SampleRate, _mixer.WaveFormat.Channels));
                    _resamplers.Add(resampled);
                    sampleProvider = resampled.ToSampleProvider();
                }

                _mixer.AddMixerInput(sampleProvider);
            }

            _outputDevice = new WaveOutEvent { DesiredLatency = 100 };
            _outputDevice.Init(_mixer);

            // Nothing has been positioned yet, so every reader sits at byte 0 — i.e. every
            // track would sound from the very start of the timeline until the first sync.
            // Park them all where the playhead actually is instead.
            SeekCore(TimeSpan.Zero);
        }
        catch
        {
            DisposeReaders();
            _outputDevice?.Dispose();
            _outputDevice = null;
            _mixer = null;
        }
    }

    /// <summary>
    /// Whether any loaded placement sounds at <paramref name="outputPosition"/>. Callers use
    /// this to pause rather than run silent readers through the mixer over a stretch of
    /// timeline no inserted track covers.
    /// </summary>
    public bool HasAudioAt(TimeSpan outputPosition)
    {
        lock (_transportLock)
        {
            foreach (var placement in _placements)
            {
                if (placement.FilePositionFor(outputPosition) is not null)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Re-aligns every placed track with an OUTPUT-timeline position, seeking only those
    /// that have drifted past <paramref name="tolerance"/>. Returns whether anything sounds
    /// there, so the caller can start or pause transport in one call.
    /// </summary>
    /// <remarks>
    /// Per-reader rather than the single whole-engine drift check the recorded-audio path
    /// uses (<c>EditorPage.SyncAudioToPlayhead</c>): placed tracks start at different
    /// instants, so "the first reader is in the right place" says nothing about the rest.
    /// A track that is silent at this position is parked at EOF rather than left where it
    /// was, so it contributes silence through the mixer's <c>ReadFully</c> padding instead
    /// of audibly running on past its own end.
    /// </remarks>
    public bool SyncTo(TimeSpan outputPosition, TimeSpan tolerance)
    {
        lock (_transportLock)
        {
            bool audible = false;

            for (int i = 0; i < _readers.Count && i < _placements.Count; i++)
            {
                var reader = _readers[i];
                var target = _placements[i].FilePositionFor(outputPosition);

                try
                {
                    if (target is not { } filePosition)
                    {
                        if (reader.Position < reader.Length)
                            reader.Position = reader.Length;
                        continue;
                    }

                    audible = true;
                    if ((reader.CurrentTime - filePosition).Duration() > tolerance)
                        SeekReader(reader, filePosition);
                }
                catch { }
            }

            return audible;
        }
    }

    /// <summary>
    /// Paths currently loaded, in load order. Lets a caller tell "only levels changed" from
    /// "the set of tracks changed" and avoid a rebuild for the former.
    /// </summary>
    public IReadOnlyList<string> LoadedPaths
    {
        get
        {
            lock (_transportLock)
            {
                return _readers.Select(r => r.FileName).ToList();
            }
        }
    }

    /// <summary>Number of loaded placements; see <see cref="TrySetPlacementVolumes"/>.</summary>
    public int PlacementCount
    {
        get { lock (_transportLock) { return _placements.Count; } }
    }

    /// <summary>
    /// Updates the gain of already-open readers, by path, without reopening anything.
    /// </summary>
    /// <remarks>
    /// Volume is a property of the reader, so changing it needs no reload at all. Rebuilding
    /// the engine for it — as the mix flyout first did — reopened every WAV and recreated the
    /// output device on EVERY slider tick, roughly thirty times a second while dragging.
    /// </remarks>
    public void SetVolumesByPath(IReadOnlyDictionary<string, float> volumeByPath)
    {
        ArgumentNullException.ThrowIfNull(volumeByPath);

        lock (_transportLock)
        {
            foreach (var reader in _readers)
            {
                if (volumeByPath.TryGetValue(reader.FileName, out float volume))
                    reader.Volume = Math.Clamp(volume, 0f, 1f);
            }
        }
    }

    /// <summary>
    /// Updates placed tracks' gains in load order, without reopening anything. Returns false
    /// when the counts disagree — i.e. the set of placements changed, not just their levels —
    /// so the caller must do a full <see cref="LoadPlacements"/> instead.
    /// </summary>
    public bool TrySetPlacementVolumes(IReadOnlyList<float> volumes)
    {
        ArgumentNullException.ThrowIfNull(volumes);

        lock (_transportLock)
        {
            if (_readers.Count == 0 || volumes.Count != _readers.Count) return false;

            for (int i = 0; i < _readers.Count; i++)
                _readers[i].Volume = Math.Clamp(volumes[i], 0f, 1f);

            return true;
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
    /// Where the loaded streams are currently reading from, or <c>null</c> when nothing is
    /// loaded. Reported from the first reader — <see cref="SeekCore"/> moves every reader to
    /// the same position, so they stay in lockstep.
    /// </summary>
    /// <remarks>
    /// Exposed so the editor can detect DRIFT between where the audio actually is and where
    /// the edited timeline says it should be. Preview audio is played as one continuous pass
    /// through each source file, but the output timeline can trim, delete, reorder, speed-shift
    /// and interleave text slides — so linear playback diverges from the timeline the moment
    /// any of those exist, and the caller has to notice and re-seek.
    /// </remarks>
    public TimeSpan? CurrentPosition
    {
        get
        {
            lock (_transportLock)
            {
                return _readers.Count > 0 ? _readers[0].CurrentTime : null;
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

        // Placement mode: `position` is an OUTPUT-timeline instant, and each reader maps it
        // through its own offset (or parks at EOF where that track is silent). Plain mode:
        // `position` is already a file position shared by every reader.
        bool placed = _placements.Count > 0;

        for (int i = 0; i < _readers.Count; i++)
        {
            var reader = _readers[i];
            try
            {
                if (!placed)
                {
                    SeekReader(reader, position);
                    continue;
                }

                if (i < _placements.Count && _placements[i].FilePositionFor(position) is { } filePosition)
                    SeekReader(reader, filePosition);
                else
                    reader.Position = reader.Length;
            }
            catch { }
        }
    }

    /// <summary>
    /// Moves one reader to a position in its OWN file, snapped down to a block boundary
    /// (a mid-sample position produces noise) and clamped to the file's length.
    /// </summary>
    private static void SeekReader(AudioFileReader reader, TimeSpan filePosition)
    {
        long targetBytes = (long)(filePosition.TotalSeconds
            * reader.WaveFormat.AverageBytesPerSecond);
        targetBytes -= targetBytes % reader.WaveFormat.BlockAlign;
        reader.Position = Math.Clamp(targetBytes, 0, reader.Length);
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

        // Indexed in lockstep with _readers, so it must never outlive them: a stale entry
        // would map the next Load's readers through the previous load's offsets.
        _placements.Clear();
    }

    /// <summary>
    /// Wraps a source provider so it NEVER reports end-of-stream: any shortfall in a read is
    /// zero-filled and the full requested count is returned.
    /// </summary>
    /// <remarks>
    /// Required by <see cref="LoadPlacements"/>, where a track is parked at EOF for every
    /// instant of the timeline it does not cover. Both consumers downstream treat a
    /// zero-sample read as permanent death — <see cref="MixingSampleProvider"/> REMOVES the
    /// input, and <see cref="MediaFoundationResampler"/> latches end-of-input — so without
    /// this, a track placed at anything other than 00:00 would be dropped during the very
    /// first buffer, before the playhead ever reached it, and never play at all.
    /// <para>
    /// Only <see cref="LoadPlacements"/> uses it. The plain <see cref="Load"/> path plays
    /// whole recordings from the start and deliberately keeps NAudio's default behaviour.
    /// </para>
    /// </remarks>
    private sealed class SilencePaddedSampleProvider(ISampleProvider source) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            if (read < count)
                Array.Clear(buffer, offset + read, count - read);
            return count;
        }
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
        /// Delegates to <see cref="EqualPowerCrossfade.GainAt"/> — the window-combination rules
        /// live there so they are directly unit-testable, rather than being sealed inside this
        /// private nested provider where nothing could reach them.
        /// </summary>
        private double GainAt(double timeSeconds) => EqualPowerCrossfade.GainAt(windows, timeSeconds);
    }
}
