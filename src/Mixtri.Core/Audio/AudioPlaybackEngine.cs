using Mixtri.Core.Diagnostics;
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
    /// <summary><see cref="WaveOutEvent.DesiredLatency"/> of every output device this engine opens.</summary>
    private const int OutputLatencyMs = 100;

    /// <summary>
    /// Length of the gain ramp applied whenever transport starts, stops or jumps. Long enough
    /// to remove the click of a hard discontinuity, short enough to be inaudible as a fade.
    /// </summary>
    private const double TransportRampSeconds = 0.005;

    /// <summary>
    /// How long <see cref="Pause"/> lets the device keep running after starting its ramp-out.
    /// The faded samples are written into the NEXT device buffer, so they only reach the
    /// speaker after everything already queued (~<see cref="OutputLatencyMs"/>) has played;
    /// stopping sooner would discard them and cut the signal mid-wave — the click itself.
    /// </summary>
    private const int DeferredStopDelayMs = OutputLatencyMs + 30;

    /// <summary>Upper bound on how far a deferred seek is advanced to make up for its queueing delay.</summary>
    private static readonly TimeSpan MaxSeekLatencyCompensation = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// A device that fails again this soon after being rebuilt is not rebuilt a second time,
    /// so a persistently broken output cannot spin in a rebuild loop.
    /// </summary>
    private static readonly TimeSpan MinDeviceRecoveryInterval = TimeSpan.FromSeconds(2);

    private WaveOutEvent? _outputDevice;

    /// <summary>
    /// Head of the chain — what the output device actually plays. Owns the transport gain
    /// ramp and executes queued seeks on the audio thread; see <see cref="TransportFadeSampleProvider"/>.
    /// </summary>
    private TransportFadeSampleProvider? _transport;

    private readonly List<AudioFileReader> _readers = [];

    /// <summary>
    /// Resampler of each entry in <see cref="_readers"/>, by index; <c>null</c> where the file
    /// already plays at the mix rate. Rebuilt (<see cref="ResettableResampler.Reset"/>) whenever
    /// its reader jumps, so no audio from before a seek is ever played after it.
    /// </summary>
    private readonly List<ResettableResampler?> _resamplers = [];

    /// <summary>
    /// Output-timeline placement of each entry in <see cref="_readers"/>, by index, or empty
    /// when the engine was loaded with plain paths. Non-empty switches
    /// <see cref="MoveReaders"/> from "every reader to the same file position" to a per-reader
    /// mapping — see <see cref="LoadPlacements"/>.
    /// </summary>
    private readonly List<AudioTimelinePlacement> _placements = [];

    /// <summary>
    /// Guards the device, the chain and every list above. Load/teardown, transport and the
    /// audio thread's deferred seeks all take it. Nothing is ever called under it that calls
    /// back into this class synchronously: <see cref="WaveOutEvent"/> raises
    /// <see cref="WaveOutEvent.PlaybackStopped"/> from its own playback thread (or posts it),
    /// and <see cref="OnPlaybackStopped"/> only queues work.
    /// </summary>
    private readonly object _transportLock = new();
    private readonly object _scrubQueueLock = new();
    private Timer? _scrubTimer;
    private TimeSpan? _queuedScrubPosition;
    private bool _scrubWorkerRunning;
    private volatile bool _disposed;

    /// <summary>Bumped on every teardown, so a deferred seek queued against an old load is dropped.</summary>
    private int _loadGeneration;

    /// <summary>Whether the caller wants sound — survives a device rebuild, unlike the device's own state.</summary>
    private bool _wantPlaying;

    /// <summary>A <see cref="Pause"/> is ramping out and the device will stop when <see cref="_stopTimer"/> fires.</summary>
    private bool _stopPending;
    private int _stopToken;
    private Timer? _stopTimer;
    private long _lastDeviceRecoveryTimestamp;

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
        ArgumentNullException.ThrowIfNull(wavFilePaths);

        var validPaths = wavFilePaths.Where(File.Exists).ToList();

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
        var volumes = ToCaseInsensitive(volumeByPath);

        lock (_transportLock)
        {
            // Teardown and rebuild happen under ONE hold of the lock: the scrub worker and the
            // audio thread's deferred seeks index _readers under it, and must never observe a
            // half-disposed or half-built set.
            TearDownCore();
            if (_disposed || validPaths.Count == 0) return;

            MixingSampleProvider? mixer = null;
            foreach (var path in validPaths)
            {
                AudioFileReader? reader = null;
                try
                {
                    reader = new AudioFileReader(path);

                    // The mix runs at the first READABLE file's format; everything else is
                    // converted to it.
                    mixer ??= new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(
                        reader.WaveFormat.SampleRate, reader.WaveFormat.Channels))
                    {
                        ReadFully = true
                    };

                    // AudioFileReader applies this to the samples it returns, so it survives
                    // every Seek/ScrubTo without a separate provider in the chain.
                    if (volumes is not null && volumes.TryGetValue(path, out float volume))
                        reader.Volume = Math.Clamp(volume, 0f, 1f);

                    IReadOnlyList<AudioFadeWindow>? windows = null;
                    if (fadeWindows is not null && fadeWindows.TryGetValue(path, out var found) && found.Count > 0)
                        windows = found;

                    mixer.AddMixerInput(BuildInput(reader, windows, mixer.WaveFormat, out var resampler));
                    _readers.Add(reader);
                    _resamplers.Add(resampler);
                }
                catch (Exception ex)
                {
                    // One unreadable file must not silence every other track.
                    try { reader?.Dispose(); } catch { }
                    DiagLog.Write("Audio",
                        $"preview skipped '{Path.GetFileName(path)}': {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (mixer is null || _readers.Count == 0) return;
            AttachOutputCore(mixer);
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

        var valid = placements
            .Where(p => !string.IsNullOrWhiteSpace(p.FilePath)
                        && p.Duration > TimeSpan.Zero
                        && p.Volume > 0
                        && File.Exists(p.FilePath))
            .ToList();

        lock (_transportLock)
        {
            TearDownCore();
            if (_disposed || valid.Count == 0) return;

            MixingSampleProvider? mixer = null;
            foreach (var placement in valid)
            {
                AudioFileReader? reader = null;
                try
                {
                    reader = new AudioFileReader(placement.FilePath);
                    mixer ??= new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(
                        reader.WaveFormat.SampleRate, reader.WaveFormat.Channels))
                    {
                        ReadFully = true
                    };

                    // AudioFileReader has its own Volume, applied to the samples it returns —
                    // no extra provider needed, and it survives every Seek/ScrubTo.
                    reader.Volume = Math.Clamp(placement.Volume, 0f, 1f);

                    mixer.AddMixerInput(BuildInput(reader, windows: null, mixer.WaveFormat, out var resampler));

                    // All three lists are indexed in lockstep, so commit them together only
                    // once nothing else can fail for this file.
                    _readers.Add(reader);
                    _resamplers.Add(resampler);
                    _placements.Add(placement);
                }
                catch (Exception ex)
                {
                    try { reader?.Dispose(); } catch { }
                    DiagLog.Write("Audio",
                        $"preview skipped placed track '{Path.GetFileName(placement.FilePath)}': " +
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }

            if (mixer is null || _readers.Count == 0) return;
            if (!AttachOutputCore(mixer)) return;

            // Nothing has been positioned yet, so every reader sits at byte 0 — i.e. every
            // track would sound from the very start of the timeline until the first sync.
            // Park them all where the playhead actually is instead. (The device is stopped,
            // so this applies immediately.)
            SeekCore(TimeSpan.Zero, tolerance: null);
        }
    }

    /// <summary>
    /// Builds one mixer input around <paramref name="reader"/>, converted to
    /// <paramref name="mixFormat"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Silence padding, always.</b> Both <see cref="MixingSampleProvider"/> (which REMOVES
    /// an input whose read comes up short) and a resampler treat a short read as the end of
    /// the stream forever. A recording shorter than its neighbour, a placed track parked at
    /// EOF, or a resampler's warm-up shortfall would therefore drop that input for the rest
    /// of the session — seeking back would bring nothing back. Padding keeps every input
    /// alive for the life of the load.
    /// </para>
    /// <para>
    /// <b>Fade before resampling.</b> The equal-power ramp (if any) runs directly against the
    /// reader's own samples and clock, upstream of any rate conversion, so read-ahead inside
    /// the resampler cannot offset it from what is audible.
    /// </para>
    /// <para>
    /// <b>Managed, resettable resampling.</b> <c>MediaFoundationResampler</c> keeps its own
    /// read-ahead that nothing could flush, so after a seek it played a buffer of audio from
    /// BEFORE the jump. <see cref="WdlResamplingSampleProvider"/> is managed and cheap to
    /// construct, so <see cref="ResettableResampler"/> simply rebuilds it on every jump.
    /// </para>
    /// </remarks>
    private static ISampleProvider BuildInput(
        AudioFileReader reader,
        IReadOnlyList<AudioFadeWindow>? windows,
        WaveFormat mixFormat,
        out ResettableResampler? resampler)
    {
        // `reader` doubles as both the sample source AND the position reference when applying
        // the fade: its CurrentTime reflects real elapsed file time, staying correct across
        // every seek since those set `reader.Position` directly.
        ISampleProvider provider = windows is { Count: > 0 }
            ? new EqualPowerFadeSampleProvider(reader, reader, windows)
            : reader;

        provider = new SilencePaddedSampleProvider(provider);

        if (provider.WaveFormat.Channels != mixFormat.Channels)
            provider = new ChannelMatchingSampleProvider(provider, mixFormat.Channels);

        resampler = null;
        if (provider.WaveFormat.SampleRate != mixFormat.SampleRate)
        {
            resampler = new ResettableResampler(provider, mixFormat.SampleRate);
            provider = resampler;
        }

        // Conversion stages can come up short too (a resampler's filter warm-up after a
        // reset); pad the outermost stage as well so the mixer never sees a short read.
        return provider is SilencePaddedSampleProvider
            ? provider
            : new SilencePaddedSampleProvider(provider);
    }

    /// <summary>
    /// Opens an output device on <paramref name="mixer"/> behind a fresh transport ramp.
    /// Caller holds <see cref="_transportLock"/>. Tears everything down and returns
    /// <c>false</c> if no device could be opened.
    /// </summary>
    private bool AttachOutputCore(MixingSampleProvider mixer)
    {
        try
        {
            var transport = new TransportFadeSampleProvider(mixer, TransportRampSeconds);
            _outputDevice = CreateOutputDevice(transport);
            _transport = transport;
            return true;
        }
        catch (Exception ex)
        {
            DiagLog.Write("Audio", $"preview output device could not be opened: {ex.GetType().Name}: {ex.Message}");
            TearDownCore();
            return false;
        }
    }

    private WaveOutEvent CreateOutputDevice(ISampleProvider source)
    {
        var device = new WaveOutEvent { DesiredLatency = OutputLatencyMs };
        device.PlaybackStopped += OnPlaybackStopped;
        try
        {
            device.Init(source);
            return device;
        }
        catch
        {
            device.PlaybackStopped -= OnPlaybackStopped;
            try { device.Dispose(); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Raised by <see cref="WaveOutEvent"/> from its playback thread (or posted to the
    /// creating thread's context). An exception means the device died under us — typically
    /// the default output was unplugged or switched — so rebuild it on the new default.
    /// </summary>
    /// <remarks>
    /// Only queues the rebuild: this can run on the playback thread itself, and taking
    /// <see cref="_transportLock"/> here could deadlock against a caller stopping or disposing
    /// that very device under the lock.
    /// </remarks>
    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null || _disposed || sender is not WaveOutEvent failed) return;

        DiagLog.Write("Audio",
            $"preview output stopped with an error, rebuilding: {e.Exception.GetType().Name}: {e.Exception.Message}");
        ThreadPool.QueueUserWorkItem(_ => RecoverOutputDevice(failed));
    }

    private void RecoverOutputDevice(WaveOutEvent failed)
    {
        try
        {
            lock (_transportLock)
            {
                // A device replaced or torn down since the error was raised is not ours to rebuild.
                if (_disposed || !ReferenceEquals(failed, _outputDevice) || _transport is not { } transport)
                    return;

                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (_lastDeviceRecoveryTimestamp != 0
                    && System.Diagnostics.Stopwatch.GetElapsedTime(_lastDeviceRecoveryTimestamp, now) < MinDeviceRecoveryInterval)
                {
                    DiagLog.Write("Audio", "preview output failed again right after a rebuild; leaving it stopped");
                    return;
                }
                _lastDeviceRecoveryTimestamp = now;

                // A pause that was ramping out when the device died is complete now.
                bool resume = _wantPlaying && !_stopPending;
                CancelDeferredStopCore();

                DisposeDeviceCore();

                // Same mixer, same readers — they are still positioned where playback was, so
                // the new device simply carries on from there.
                var device = CreateOutputDevice(transport);
                _outputDevice = device;
                transport.ResetToSilence();
                transport.SetTarget(audible: true);
                if (resume)
                {
                    _wantPlaying = true;
                    device.Play();
                }
            }
        }
        catch (Exception ex)
        {
            DiagLog.Write("Audio", $"preview output rebuild failed: {ex.GetType().Name}: {ex.Message}");
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
    /// of audibly running on past its own end. While the device runs, the correction is
    /// applied on the audio thread under a short ramp (see <see cref="SeekCore"/>) rather
    /// than by jumping a reader the device is reading mid-buffer.
    /// </remarks>
    public bool SyncTo(TimeSpan outputPosition, TimeSpan tolerance)
    {
        lock (_transportLock)
        {
            bool audible = false;
            bool needsMove = false;

            for (int i = 0; i < _readers.Count && i < _placements.Count; i++)
            {
                var reader = _readers[i];
                var target = _placements[i].FilePositionFor(outputPosition);

                try
                {
                    if (target is not { } filePosition)
                    {
                        if (reader.Position < reader.Length)
                            needsMove = true;
                        continue;
                    }

                    audible = true;
                    if ((reader.CurrentTime - filePosition).Duration() > tolerance)
                        needsMove = true;
                }
                catch { }
            }

            if (needsMove)
                SeekCore(outputPosition, tolerance);

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
    /// Updates the gain of already-open readers, by path, without reopening anything. Paths
    /// are matched case-insensitively, like <see cref="Load(IEnumerable{string}, IReadOnlyDictionary{string, float})"/>.
    /// </summary>
    /// <remarks>
    /// Volume is a property of the reader, so changing it needs no reload at all. Rebuilding
    /// the engine for it — as the mix flyout first did — reopened every WAV and recreated the
    /// output device on EVERY slider tick, roughly thirty times a second while dragging.
    /// </remarks>
    public void SetVolumesByPath(IReadOnlyDictionary<string, float> volumeByPath)
    {
        ArgumentNullException.ThrowIfNull(volumeByPath);

        // Re-keyed for the same reason as Load: the caller's comparer is not ours to trust.
        var volumes = ToCaseInsensitive(volumeByPath);
        if (volumes is null) return;

        lock (_transportLock)
        {
            foreach (var reader in _readers)
            {
                if (volumes.TryGetValue(reader.FileName, out float volume))
                    reader.Volume = Math.Clamp(volume, 0f, 1f);
            }
        }
    }

    private static Dictionary<string, float>? ToCaseInsensitive(IReadOnlyDictionary<string, float>? source)
    {
        if (source is not { Count: > 0 }) return null;

        var result = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, volume) in source)
            result[path] = volume;
        return result;
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
        // Real playback supersedes a scrub burst: its pending auto-stop must not cut it off.
        lock (_scrubQueueLock)
        {
            _scrubTimer?.Dispose();
            _scrubTimer = null;
        }

        lock (_transportLock)
        {
            if (!_disposed) PlayCore();
        }
    }

    /// <summary>
    /// Ramps the output out and stops the device once the ramp has been heard (see
    /// <see cref="DeferredStopDelayMs"/>). <see cref="IsPlaying"/> reports <c>false</c> from
    /// the moment this is called, and a <see cref="Play"/> before the device actually stops
    /// simply ramps back in.
    /// </summary>
    public void Pause()
    {
        lock (_transportLock)
        {
            BeginFadeOutAndStopCore();
        }
    }

    /// <summary>Stops the device immediately (no ramp).</summary>
    public void Stop()
    {
        lock (_transportLock)
        {
            StopDeviceNowCore();
        }
    }

    /// <summary>
    /// Seeks all audio streams to the given position.
    /// </summary>
    /// <remarks>
    /// While the device is running the jump is made on the audio thread inside a short
    /// ramp-out/ramp-in, and playback continues; otherwise it is applied immediately and the
    /// next <see cref="Play"/> ramps in from it.
    /// </remarks>
    public void Seek(TimeSpan position)
    {
        lock (_transportLock)
        {
            SeekCore(position, tolerance: null);
        }
    }

    /// <summary>
    /// Where the loaded streams are currently reading from, or <c>null</c> when nothing is
    /// loaded. Reported from the first reader — <see cref="MoveReaders"/> moves every reader to
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
        bool loaded;
        lock (_transportLock)
        {
            loaded = _outputDevice is not null;
        }
        if (!loaded) return;

        lock (_scrubQueueLock)
        {
            if (_disposed) return;

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
        // Set once _scrubWorkerRunning has been cleared under the SAME lock hold that saw the
        // queue empty — clearing it later would let a ScrubTo in between see "running", skip
        // starting a worker, and strand its position.
        bool released = false;
        try
        {
            while (true)
            {
                TimeSpan position;
                lock (_scrubQueueLock)
                {
                    if (_disposed || _queuedScrubPosition is not { } queued)
                    {
                        _scrubWorkerRunning = false;
                        released = true;
                        return;
                    }

                    position = queued;
                    _queuedScrubPosition = null;
                }

                lock (_transportLock)
                {
                    if (!_disposed && _outputDevice is not null)
                    {
                        SeekCore(position, tolerance: null);
                        PlayCore();
                    }
                }

                lock (_scrubQueueLock)
                {
                    if (_queuedScrubPosition.HasValue)
                        continue;

                    _scrubWorkerRunning = false;
                    released = true;
                    if (!_disposed)
                        ResetScrubStopTimer();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            DiagLog.Write("Audio", $"scrub playback failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (!released)
            {
                lock (_scrubQueueLock)
                {
                    _scrubWorkerRunning = false;
                }
            }
        }
    }

    /// <summary>Caller holds <see cref="_transportLock"/>.</summary>
    private void PlayCore()
    {
        if (_outputDevice is not { } device || _transport is not { } transport) return;

        _wantPlaying = true;
        CancelDeferredStopCore();

        if (device.PlaybackState == PlaybackState.Playing)
        {
            // Still running (e.g. a Pause still ramping out): just ramp back in.
            transport.SetTarget(audible: true);
            return;
        }

        // Stopped, so nothing is reading: apply any seek still waiting for the audio thread
        // directly, then start from silence and ramp in.
        transport.TakePendingSeek()?.Invoke(false);
        transport.ResetToSilence();
        transport.SetTarget(audible: true);

        try { device.Play(); }
        catch (Exception ex)
        {
            DiagLog.Write("Audio", $"preview output failed to start: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Caller holds <see cref="_transportLock"/>.</summary>
    private void BeginFadeOutAndStopCore()
    {
        _wantPlaying = false;

        if (_outputDevice is not { PlaybackState: PlaybackState.Playing } || _transport is not { } transport)
        {
            StopDeviceNowCore();
            return;
        }

        if (_stopPending) return;

        transport.SetTarget(audible: false);
        _stopPending = true;
        int token = ++_stopToken;
        _stopTimer?.Dispose();
        _stopTimer = new Timer(_ =>
        {
            lock (_transportLock)
            {
                if (_disposed || !_stopPending || token != _stopToken) return;
                StopDeviceNowCore();
            }
        }, null, DeferredStopDelayMs, Timeout.Infinite);
    }

    /// <summary>Caller holds <see cref="_transportLock"/>.</summary>
    private void StopDeviceNowCore()
    {
        CancelDeferredStopCore();
        _wantPlaying = false;

        try { _outputDevice?.Stop(); } catch { }

        if (_transport is { } transport)
        {
            transport.ResetToSilence();
            transport.TakePendingSeek()?.Invoke(false);
        }
    }

    /// <summary>Caller holds <see cref="_transportLock"/>.</summary>
    private void CancelDeferredStopCore()
    {
        _stopPending = false;
        _stopToken++;
        _stopTimer?.Dispose();
        _stopTimer = null;
    }

    /// <summary>
    /// Moves the readers to <paramref name="position"/> (see <see cref="MoveReaders"/>).
    /// Caller holds <see cref="_transportLock"/>.
    /// </summary>
    /// <remarks>
    /// A running device is NOT stopped for this — stopping discards the queued buffers
    /// mid-waveform (a click) and restarting costs a full device latency. Instead the jump is
    /// queued on <see cref="TransportFadeSampleProvider"/>, which on the audio thread ramps the
    /// current signal out over a few milliseconds, moves the readers between two reads of the
    /// same buffer (so no read ever races the move), and ramps back in. The queued position is
    /// advanced by however long it waited, since the playhead kept moving meanwhile.
    /// </remarks>
    private void SeekCore(TimeSpan position, TimeSpan? tolerance)
    {
        if (_transport is { } transport && _outputDevice?.PlaybackState == PlaybackState.Playing)
        {
            int generation = _loadGeneration;
            long queuedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            transport.QueueSeek(fromAudioThread =>
            {
                lock (_transportLock)
                {
                    if (_disposed || generation != _loadGeneration) return;

                    var target = position;
                    if (fromAudioThread)
                    {
                        var late = System.Diagnostics.Stopwatch.GetElapsedTime(queuedAt);
                        target += late < MaxSeekLatencyCompensation ? late : MaxSeekLatencyCompensation;
                    }

                    MoveReaders(target, tolerance);
                }
            });
            return;
        }

        // Nothing is reading: apply now, superseding anything still queued.
        _transport?.TakePendingSeek();
        MoveReaders(position, tolerance);
    }

    /// <summary>
    /// Repositions every reader for <paramref name="position"/> and flushes the resampler of
    /// each one that moved. With a <paramref name="tolerance"/>, readers already within it are
    /// left alone. Caller holds <see cref="_transportLock"/>.
    /// </summary>
    private void MoveReaders(TimeSpan position, TimeSpan? tolerance)
    {
        // Placement mode: `position` is an OUTPUT-timeline instant, and each reader maps it
        // through its own offset (or parks at EOF where that track is silent). Plain mode:
        // `position` is already a file position shared by every reader.
        bool placed = _placements.Count > 0;

        for (int i = 0; i < _readers.Count; i++)
        {
            var reader = _readers[i];
            try
            {
                TimeSpan? target = !placed
                    ? position
                    : i < _placements.Count ? _placements[i].FilePositionFor(position) : null;

                if (target is not { } filePosition)
                {
                    if (reader.Position < reader.Length)
                    {
                        reader.Position = reader.Length;
                        ResetResampler(i);
                    }
                    continue;
                }

                if (tolerance is { } allowed && (reader.CurrentTime - filePosition).Duration() <= allowed)
                    continue;

                SeekReader(reader, filePosition);
                ResetResampler(i);
            }
            catch { }
        }
    }

    private void ResetResampler(int index)
    {
        if (index < _resamplers.Count)
            _resamplers[index]?.Reset();
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

    /// <summary>Caller holds <see cref="_scrubQueueLock"/>.</summary>
    private void ResetScrubStopTimer()
    {
        _scrubTimer?.Dispose();
        _scrubTimer = new Timer(_ =>
        {
            lock (_transportLock)
            {
                if (!_disposed) BeginFadeOutAndStopCore();
            }
        }, null, 80, Timeout.Infinite);
    }

    public bool IsLoaded
    {
        get { lock (_transportLock) { return _outputDevice is not null; } }
    }

    /// <summary>
    /// Whether the engine is (or is about to be) audible: <c>false</c> as soon as
    /// <see cref="Pause"/> is called, even while its short ramp-out is still playing.
    /// </summary>
    public bool IsPlaying
    {
        get
        {
            lock (_transportLock)
            {
                return !_stopPending && _outputDevice?.PlaybackState == PlaybackState.Playing;
            }
        }
    }

    public void Dispose()
    {
        lock (_scrubQueueLock)
        {
            if (_disposed) return;
            _disposed = true;
            _queuedScrubPosition = null;
            _scrubTimer?.Dispose();
            _scrubTimer = null;
        }

        lock (_transportLock)
        {
            TearDownCore();
        }
    }

    /// <summary>
    /// Stops and releases the device, the chain and every reader. Caller holds
    /// <see cref="_transportLock"/>.
    /// </summary>
    private void TearDownCore()
    {
        CancelDeferredStopCore();
        _wantPlaying = false;
        _loadGeneration++;
        DisposeDeviceCore();
        _transport = null;
        DisposeReaders();
    }

    /// <summary>Caller holds <see cref="_transportLock"/>.</summary>
    private void DisposeDeviceCore()
    {
        if (_outputDevice is not { } device) return;
        _outputDevice = null;

        // Unsubscribe first: stopping raises PlaybackStopped, and a disposed device's
        // errors must never trigger a rebuild.
        device.PlaybackStopped -= OnPlaybackStopped;
        try { device.Stop(); } catch { }
        try { device.Dispose(); } catch { }
    }

    private void DisposeReaders()
    {
        // The resamplers are managed and hold no native resources; they die with the chain.
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
    /// Head of the playback chain: applies a short linear gain ramp whenever transport
    /// starts, stops or jumps, and performs queued seeks on the audio thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every hard transport change used to stop the device outright, which cuts the waveform
    /// wherever it happens to be — an audible click on every pause, seek and scrub step. Here
    /// the gain glides toward a target (0 or 1) over <see cref="TransportRampSeconds"/>.
    /// </para>
    /// <para>
    /// A queued seek runs INSIDE <see cref="Read"/>: the first few milliseconds of the buffer
    /// are read from the old position and ramped to silence, the seek runs, and the rest of
    /// the buffer is read from the new position ramping back up. Because it runs on the very
    /// thread that reads the readers, the move can never land in the middle of a read.
    /// </para>
    /// <para>
    /// It also never returns short and never throws: a read failure in any input (a file
    /// that turns unreadable mid-session) becomes silence rather than killing the device's
    /// playback thread.
    /// </para>
    /// </remarks>
    private sealed class TransportFadeSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private readonly float _step;
        private readonly object _sync = new();
        private float _gain;
        private float _target = 1f;
        private Action<bool>? _pendingSeek;
        private bool _loggedReadFailure;

        public TransportFadeSampleProvider(ISampleProvider source, double rampSeconds)
        {
            _source = source;
            _channels = Math.Max(1, source.WaveFormat.Channels);
            int rampFrames = Math.Max(1, (int)(source.WaveFormat.SampleRate * rampSeconds));
            _step = 1f / rampFrames;
        }

        public WaveFormat WaveFormat => _source.WaveFormat;

        /// <summary>Sets the level the output glides toward: audible (1) or silent (0).</summary>
        public void SetTarget(bool audible)
        {
            lock (_sync) { _target = audible ? 1f : 0f; }
        }

        /// <summary>Starts the next read from silence. Only valid while the device is stopped.</summary>
        public void ResetToSilence()
        {
            lock (_sync) { _gain = 0f; }
        }

        /// <summary>
        /// Queues a seek for the next read, replacing any not yet taken. The argument passed
        /// to it is <c>true</c> when it runs on the audio thread.
        /// </summary>
        public void QueueSeek(Action<bool> seek)
        {
            lock (_sync) { _pendingSeek = seek; }
        }

        /// <summary>Removes and returns the queued seek, if any, for the caller to run itself.</summary>
        public Action<bool>? TakePendingSeek()
        {
            lock (_sync)
            {
                var pending = _pendingSeek;
                _pendingSeek = null;
                return pending;
            }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            Action<bool>? seek;
            float gain, target;
            lock (_sync)
            {
                seek = _pendingSeek;
                _pendingSeek = null;
                gain = _gain;
                target = _target;
            }

            int done = 0;
            if (seek is not null)
            {
                // Ramp what is sounding now down to silence, as far as this buffer allows.
                int framesAvailable = count / _channels;
                int outFrames = gain > 0f
                    ? Math.Min(framesAvailable, (int)Math.Ceiling(gain / _step))
                    : 0;
                if (outFrames > 0)
                {
                    done = ReadSource(buffer, offset, outFrames * _channels);
                    gain = ApplyRamp(buffer, offset, done, gain, 0f);
                }

                gain = 0f;
                try { seek(true); } catch { }
            }

            if (done < count)
            {
                int read = ReadSource(buffer, offset + done, count - done);
                gain = ApplyRamp(buffer, offset + done, read, gain, target);
                done += read;
            }

            lock (_sync) { _gain = gain; }
            return done;
        }

        /// <summary>Reads exactly <paramref name="count"/> samples, zero-filling any shortfall or failure.</summary>
        private int ReadSource(float[] buffer, int offset, int count)
        {
            int read;
            try
            {
                read = _source.Read(buffer, offset, count);
            }
            catch (Exception ex)
            {
                read = 0;
                if (!_loggedReadFailure)
                {
                    _loggedReadFailure = true;
                    DiagLog.Write("Audio", $"preview mix read failed, playing silence: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (read < 0) read = 0;
            if (read < count)
                Array.Clear(buffer, offset + read, count - read);
            return count;
        }

        private float ApplyRamp(float[] buffer, int offset, int samples, float gain, float target)
        {
            // Steady full level: leave the samples untouched.
            if (gain == 1f && target == 1f) return gain;

            int frames = samples / _channels;
            for (int frame = 0; frame < frames; frame++)
            {
                if (gain < target) gain = Math.Min(target, gain + _step);
                else if (gain > target) gain = Math.Max(target, gain - _step);

                int baseIndex = offset + frame * _channels;
                for (int ch = 0; ch < _channels; ch++)
                    buffer[baseIndex + ch] *= gain;
            }

            // A trailing partial frame (never expected) takes the last gain.
            for (int i = frames * _channels; i < samples; i++)
                buffer[offset + i] *= gain;

            return gain;
        }
    }

    /// <summary>
    /// Resamples to a fixed rate through <see cref="WdlResamplingSampleProvider"/>, which is
    /// rebuilt on <see cref="Reset"/> so a seek never plays the filter's buffered pre-seek
    /// input. Construction is cheap (managed, a few small arrays).
    /// </summary>
    private sealed class ResettableResampler : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _sampleRate;
        private volatile WdlResamplingSampleProvider _inner;

        public ResettableResampler(ISampleProvider source, int sampleRate)
        {
            _source = source;
            _sampleRate = sampleRate;
            _inner = new WdlResamplingSampleProvider(source, sampleRate);
        }

        public WaveFormat WaveFormat => _inner.WaveFormat;

        public int Read(float[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public void Reset() => _inner = new WdlResamplingSampleProvider(_source, _sampleRate);
    }

    /// <summary>
    /// Converts a float stream to a different channel count: mono is copied to every output
    /// channel, anything to mono is averaged, and otherwise channels are mapped one-to-one
    /// (extra outputs silent, extra inputs dropped).
    /// </summary>
    private sealed class ChannelMatchingSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _inChannels;
        private readonly int _outChannels;
        private float[] _scratch = [];

        public ChannelMatchingSampleProvider(ISampleProvider source, int channels)
        {
            _source = source;
            _inChannels = Math.Max(1, source.WaveFormat.Channels);
            _outChannels = Math.Max(1, channels);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, _outChannels);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int frames = count / _outChannels;
            int needed = frames * _inChannels;
            if (_scratch.Length < needed) _scratch = new float[needed];

            int framesRead = _source.Read(_scratch, 0, needed) / _inChannels;
            for (int frame = 0; frame < framesRead; frame++)
            {
                int src = frame * _inChannels;
                int dst = offset + frame * _outChannels;

                if (_inChannels == 1)
                {
                    float value = _scratch[src];
                    for (int ch = 0; ch < _outChannels; ch++) buffer[dst + ch] = value;
                }
                else if (_outChannels == 1)
                {
                    float sum = 0f;
                    for (int ch = 0; ch < _inChannels; ch++) sum += _scratch[src + ch];
                    buffer[dst] = sum / _inChannels;
                }
                else
                {
                    for (int ch = 0; ch < _outChannels; ch++)
                        buffer[dst + ch] = ch < _inChannels ? _scratch[src + ch] : 0f;
                }
            }

            return framesRead * _outChannels;
        }
    }

    /// <summary>
    /// Wraps a source provider so it NEVER reports end-of-stream: any shortfall in a read is
    /// zero-filled and the full requested count is returned.
    /// </summary>
    /// <remarks>
    /// Every mixer input is wrapped in this (see <see cref="BuildInput"/>). Both consumers
    /// downstream treat a short read as permanent death — <see cref="MixingSampleProvider"/>
    /// REMOVES the input, and a resampler stops pulling — so without it a placed track parked
    /// at EOF would be dropped before the playhead ever reached it, and a recording shorter
    /// than its neighbour would vanish for good the first time playback ran past its end.
    /// </remarks>
    private sealed class SilencePaddedSampleProvider(ISampleProvider source) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            if (read < 0) read = 0;
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
    /// <b>Must run BEFORE any resampling, not after.</b> <see cref="BuildInput"/> wires this
    /// provider directly against the raw <see cref="AudioFileReader"/> (so
    /// <paramref name="source"/> and <paramref name="position"/> are literally the same
    /// object) and only resamples the ALREADY-faded output afterward. Wrapping a
    /// resampler-derived provider instead would desync the ramp from
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
