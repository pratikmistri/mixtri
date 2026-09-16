using System.Diagnostics;
using System.Reflection;
using Mixtri.Core.Audio;
using Mixtri.Tests.TestSupport;
using NAudio.Wave;

namespace Mixtri.Tests;

[TestClass]
public class AudioScrubTimerTests
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    [TestMethod]
    public void RearmingReusesOneTimerAndOldCallbacksCannotEndTheNewQuietPeriod()
    {
        var clock = new ManualClock();
        int stopped = 0;
        using var timer = new ScrubStopTimer(new object(), () => stopped++, clock);
        timer.Restart();
        clock.Advance(60);
        timer.Restart();
        clock.Advance(20);
        clock.Timer!.FireQueuedCallback();
        Assert.AreEqual(0, stopped);
        Assert.AreEqual(TimeSpan.FromMilliseconds(60), clock.Timer.Due);
        clock.Advance(59);
        clock.Timer.FireQueuedCallback();
        Assert.AreEqual(0, stopped);
        Assert.AreEqual(TimeSpan.FromMilliseconds(1), clock.Timer.Due);
        clock.Advance(1);
        clock.Timer.FireQueuedCallback();
        clock.Timer.FireQueuedCallback();
        Assert.AreEqual(1, stopped);
        for (int i = 0; i < 1000; i++) timer.Restart();
        Assert.AreEqual(1, clock.CreatedTimers);
    }

    [TestMethod]
    public void CancelAndDisposeRejectAlreadyQueuedCallbacks()
    {
        var clock = new ManualClock();
        int stopped = 0;
        using var timer = new ScrubStopTimer(new object(), () => stopped++, clock);
        timer.Restart();
        timer.Cancel();
        clock.Advance(100);
        clock.Timer!.FireQueuedCallback();
        Assert.AreEqual(0, stopped);
        timer.Restart();
        timer.Dispose();
        clock.Advance(100);
        clock.Timer.FireQueuedCallback();
        Assert.AreEqual(0, stopped);
        Assert.IsTrue(clock.Timer.IsDisposed);
    }

    [TestMethod]
    public async Task CallbackRechecksCancellationAfterWaitingForTransport()
    {
        var clock = new ManualClock();
        var transport = new object();
        using var started = new ManualResetEventSlim();
        int stopped = 0;
        using var timer = new ScrubStopTimer(transport, () => stopped++, clock);
        timer.Restart();
        clock.Advance(100);
        Task callback;
        lock (transport)
        {
            callback = Task.Run(() =>
            {
                started.Set();
                clock.Timer!.FireQueuedCallback();
            });
            Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)));
            timer.Cancel();
        }
        await callback;
        Assert.AreEqual(0, stopped);
    }

    [TestMethod]
    public void WarmSystemTimerResetsDoNotAllocate()
    {
        using var timer = new ScrubStopTimer(new object(), static () => { }, TimeProvider.System);
        for (int i = 0; i < 100; i++) timer.Restart();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 3000; i++) timer.Restart();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        timer.Cancel();
        Assert.AreEqual(0L, allocated);
        Timer? previous = null;
        try
        {
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 3000; i++)
            {
                previous?.Dispose();
                previous = new Timer(static _ => { }, null, 80, Timeout.Infinite);
            }
            long previousBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.IsTrue(previousBytes > 100_000);
            Console.WriteLine($"3,000 scrub timer resets: {previousBytes:N0} previous bytes, {allocated:N0} reused-timer bytes.");
        }
        finally { previous?.Dispose(); }
    }

    [TestMethod]
    public async Task ScrubStopsAfterEightyMillisecondsAndCanStartAgain()
    {
        var clock = new ManualClock();
        var output = new FakeOutput();
        using var engine = new AudioPlaybackEngine(output, clock);
        engine.ScrubTo(TimeSpan.Zero);
        await WaitIdle(engine);
        Assert.AreEqual(1, output.Plays);
        int stops = output.Stops;
        clock.Advance(79);
        clock.Timer!.FireQueuedCallback();
        Assert.AreEqual(stops, output.Stops);
        clock.Advance(1);
        clock.Timer.FireQueuedCallback();
        Assert.AreEqual(stops + 1, output.Stops);
        engine.ScrubTo(TimeSpan.FromSeconds(1));
        await WaitIdle(engine);
        Assert.AreEqual(2, output.Plays);
        Assert.AreEqual(1, clock.CreatedTimers);
    }

    [TestMethod]
    [DataRow("play")]
    [DataRow("pause")]
    [DataRow("stop")]
    [DataRow("seek")]
    [DataRow("takeover")]
    public async Task NormalTransportCommandsInvalidateTheScrubTimeout(string command)
    {
        var clock = new ManualClock();
        var output = new FakeOutput();
        using var engine = new AudioPlaybackEngine(output, clock);
        engine.ScrubTo(TimeSpan.Zero);
        await WaitIdle(engine);
        switch (command)
        {
            case "play": engine.Play(); break;
            case "pause": engine.Pause(); break;
            case "stop": engine.Stop(); break;
            case "seek": engine.Seek(TimeSpan.FromSeconds(1)); break;
            case "takeover": engine.CancelPendingScrub(); break;
        }
        int stops = output.Stops;
        var state = output.PlaybackState;
        clock.Advance(100);
        clock.Timer!.FireQueuedCallback();
        Assert.AreEqual(stops, output.Stops);
        Assert.AreEqual(state, output.PlaybackState);
    }

    [TestMethod]
    public async Task PauseSupersedesADequeuedScrubWaitingForTransport()
    {
        var clock = new ManualClock();
        var output = new FakeOutput();
        using var engine = new AudioPlaybackEngine(output, clock);
        var transport = Field<object>(engine, "_transportLock");
        using var release = new ManualResetEventSlim();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = Task.Run(() =>
        {
            lock (transport)
            {
                ready.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test transport was not released.");
                engine.Pause();
            }
        });
        try
        {
            await ready.Task;
            engine.ScrubTo(TimeSpan.FromSeconds(1));
            await WaitUntil(() =>
            {
                lock (Field<object>(engine, "_scrubQueueLock"))
                    return Field<bool>(engine, "_scrubWorkerRunning")
                        && Field<TimeSpan?>(engine, "_queuedScrubPosition") is null;
            });
        }
        finally { release.Set(); }
        await controller;
        await WaitIdle(engine);
        Assert.AreEqual(0, output.Plays);
        Assert.AreEqual(0, clock.CreatedTimers);
    }

    [TestMethod]
    public async Task PlaybackTakeoverCannotBeRearmedByTheFinishingScrubWorker()
    {
        var clock = new ManualClock();
        var output = new FakeOutput();
        using var engine = new AudioPlaybackEngine(output, clock);
        output.OnPlay = engine.Play;
        engine.ScrubTo(TimeSpan.Zero);
        await WaitIdle(engine);
        Assert.AreEqual(1, output.Plays);
        Assert.AreEqual(PlaybackState.Playing, output.PlaybackState);
        Assert.AreEqual(0, clock.CreatedTimers, "The superseded worker re-armed an obsolete scrub timeout.");
    }

    [TestMethod]
    public async Task RapidScrubsCoalesceToTheLatestPositionWithoutBlockingTheCaller()
    {
        using var directory = new TempDirectoryFixture("mixtri_audio_scrub_");
        string path = Path.Combine(directory.Path, "audio.wav");
        using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(16000, 1)))
        {
            var values = new float[32000];
            writer.WriteSamples(values, 0, values.Length);
        }
        var clock = new ManualClock();
        var output = new FakeOutput();
        using var engine = new AudioPlaybackEngine(output, clock);
        var reader = new AudioFileReader(path);
        Field<List<AudioFileReader>>(engine, "_readers").Add(reader);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        output.OnPlay = () =>
        {
            if (output.Plays != 1) return;
            started.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test playback was not released.");
        };
        engine.ScrubTo(TimeSpan.Zero);
        await started.Task;
        var last = TimeSpan.Zero;
        try
        {
            for (int i = 0; i < 100; i++)
            {
                last = TimeSpan.FromMilliseconds(1000 + i * 5);
                engine.ScrubTo(last);
            }
        }
        finally { release.Set(); }
        await WaitIdle(engine);
        Assert.AreEqual(2, output.Plays);
        long expectedBytes = (long)(last.TotalSeconds * reader.WaveFormat.AverageBytesPerSecond);
        expectedBytes -= expectedBytes % reader.WaveFormat.BlockAlign;
        Assert.AreEqual(expectedBytes, reader.Position);
        Assert.AreEqual(1, clock.CreatedTimers);
    }

    [TestMethod]
    public async Task DisposalReleasesTheScrubQueueBeforeWaitingForTransport()
    {
        var clock = new ManualClock();
        var output = new FakeOutput();
        using var engine = new AudioPlaybackEngine(output, clock);
        engine.ScrubTo(TimeSpan.Zero);
        await WaitIdle(engine);
        var transport = Field<object>(engine, "_transportLock");
        var queue = Field<object>(engine, "_scrubQueueLock");
        Task? disposal = null;
        try
        {
            await Task.Factory.StartNew(() =>
            {
                // Dedicated threads keep the controlled lock waits from starving other tests.
                lock (transport)
                {
                    disposal = Task.Factory.StartNew(engine.Dispose, CancellationToken.None,
                        TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    Assert.IsTrue(SpinWait.SpinUntil(() => clock.Timer!.IsDisposed, TimeSpan.FromSeconds(5)),
                        "Timer disposal must not wait for the transport lock.");
                    bool entered = Monitor.TryEnter(queue, TimeSpan.FromSeconds(5));
                    try
                    {
                        Assert.IsTrue(entered, "Disposal held the queue lock while waiting for transport.");
                        Assert.IsFalse(disposal.IsCompleted, "Disposal must still wait for in-flight transport work.");
                    }
                    finally
                    {
                        if (entered) Monitor.Exit(queue);
                    }
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        finally
        {
            if (disposal is not null)
                await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.AreEqual(1, output.Disposals);
        Assert.IsFalse(engine.IsLoaded);
    }

    [TestMethod]
    public async Task DisposalPreventsLaterTimerOrScrubActivity()
    {
        var clock = new ManualClock();
        var output = new FakeOutput();
        using var engine = new AudioPlaybackEngine(output, clock);
        engine.ScrubTo(TimeSpan.Zero);
        await WaitIdle(engine);
        engine.Dispose();
        int stops = output.Stops, plays = output.Plays;
        clock.Advance(100);
        clock.Timer!.FireQueuedCallback();
        engine.ScrubTo(TimeSpan.FromSeconds(1));
        Assert.AreEqual(stops, output.Stops);
        Assert.AreEqual(plays, output.Plays);
        Assert.AreEqual(1, output.Disposals);
        Assert.IsTrue(clock.Timer.IsDisposed);
    }

    private static T Field<T>(object target, string name) =>
        (T)typeof(AudioPlaybackEngine).GetField(name, Fields)!.GetValue(target)!;

    private static Task WaitIdle(AudioPlaybackEngine engine) => WaitUntil(() =>
    {
        lock (Field<object>(engine, "_scrubQueueLock"))
            return !Field<bool>(engine, "_scrubWorkerRunning");
    });

    private static async Task WaitUntil(Func<bool> predicate)
    {
        var elapsed = Stopwatch.StartNew();
        while (!predicate())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) Assert.Fail("The scrub worker did not reach the expected state.");
            await Task.Delay(5);
        }
    }

    private sealed class FakeOutput : IWavePlayer
    {
        private int _plays, _stops, _disposals, _state;
        public int Plays => Volatile.Read(ref _plays);
        public int Stops => Volatile.Read(ref _stops);
        public int Disposals => Volatile.Read(ref _disposals);
        public Action? OnPlay { get; set; }
        public PlaybackState PlaybackState => (PlaybackState)Volatile.Read(ref _state);
        public float Volume { get; set; } = 1;
        public WaveFormat OutputWaveFormat { get; private set; } = WaveFormat.CreateIeeeFloatWaveFormat(16000, 1);
        public event EventHandler<StoppedEventArgs>? PlaybackStopped { add { } remove { } }
        public void Init(IWaveProvider waveProvider) => OutputWaveFormat = waveProvider.WaveFormat;
        public void Play()
        {
            Interlocked.Increment(ref _plays);
            Volatile.Write(ref _state, (int)PlaybackState.Playing);
            OnPlay?.Invoke();
        }
        public void Stop()
        {
            Interlocked.Increment(ref _stops);
            Volatile.Write(ref _state, (int)PlaybackState.Stopped);
        }
        public void Pause() => Volatile.Write(ref _state, (int)PlaybackState.Paused);
        public void Dispose() => Interlocked.Increment(ref _disposals);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        private ManualTimer? _timer;
        private int _created;
        public ManualTimer? Timer => Volatile.Read(ref _timer);
        public int CreatedTimers => Volatile.Read(ref _created);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Volatile.Read(ref _ticks);
        public void Advance(double milliseconds) => Interlocked.Add(ref _ticks, TimeSpan.FromMilliseconds(milliseconds).Ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            timer.Change(dueTime, period);
            Volatile.Write(ref _timer, timer);
            Interlocked.Increment(ref _created);
            return timer;
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        private long _dueTicks;
        private int _disposed;
        public TimeSpan Due => TimeSpan.FromTicks(Volatile.Read(ref _dueTicks));
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            Interlocked.Exchange(ref _dueTicks, dueTime.Ticks);
            return true;
        }
        public void FireQueuedCallback() => callback(state);
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
