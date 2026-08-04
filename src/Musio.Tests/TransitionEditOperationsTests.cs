namespace Musio.Tests;

using Musio.Core.Models;
using Musio.Core.Processing;
using Musio.Core.Projects;
using Musio.Core.Timeline;

/// <summary>
/// Tests for <see cref="UpdateTransitionOperation"/> and
/// <see cref="ApplyTransitionToAllBoundariesOperation"/>: setting/clearing a single boundary's
/// <see cref="TimelineSegment.InTransition"/>, undo/redo round trips (including the
/// <c>null</c> &lt;-&gt; set-value transitions, which are semantically distinct — see
/// <see cref="TimelineSegment.InTransition"/>'s remarks), and the "apply to all boundaries"
/// action being a single undo entry that restores every boundary it touched independently.
/// </summary>
[TestClass]
public sealed class TransitionEditOperationsTests
{
    private static TimeSpan S(double sec) => TimeSpan.FromSeconds(sec);

    private static VideoSegment Video(double durSec, TransitionConfig? inTransition = null) => new()
    {
        VideoFilePath = "C:\\clip.mp4",
        SourceStart = TimeSpan.Zero,
        SourceDuration = TimeSpan.FromSeconds(durSec),
        Duration = TimeSpan.FromSeconds(durSec),
        InTransition = inTransition,
    };

    private static TimelineModel ModelWith(params TimelineSegment[] segments)
    {
        var model = new TimelineModel();
        model.Segments.AddRange(segments);
        model.RecalculateSegmentPositions();
        return model;
    }

    private static TransitionConfig Config(
        TransitionType type = TransitionType.CrossFade,
        double durationSec = 0.5,
        TransitionEasing easing = TransitionEasing.EaseInOut) => new()
    {
        Type = type,
        Duration = TimeSpan.FromSeconds(durationSec),
        Easing = easing,
    };

    // ── UpdateTransitionOperation ────────────────────────────────────────

    [TestMethod]
    public void Update_SetsConfig_OnPreviouslyUnconfiguredBoundary()
    {
        var model = ModelWith(Video(4), Video(4));
        var incomingId = model.Segments[1].Id;
        var config = Config(TransitionType.SlideLeft, 0.6);

        var op = new UpdateTransitionOperation(incomingId, config);
        op.Execute(model);

        Assert.AreSame(config, model.Segments[1].InTransition);
    }

    [TestMethod]
    public void Update_Undo_RestoresNull_WhenBoundaryWasPreviouslyUnconfigured()
    {
        var model = ModelWith(Video(4), Video(4));
        var incomingId = model.Segments[1].Id;
        Assert.IsNull(model.Segments[1].InTransition);

        var op = new UpdateTransitionOperation(incomingId, Config(TransitionType.Fade));
        op.Execute(model);
        Assert.IsNotNull(model.Segments[1].InTransition);

        op.Undo(model);
        Assert.IsNull(model.Segments[1].InTransition);
    }

    [TestMethod]
    public void Update_Undo_RestoresPreviousConfig_WhenBoundaryWasAlreadyConfigured()
    {
        var previous = Config(TransitionType.WipeUp, 0.3, TransitionEasing.EaseIn);
        var model = ModelWith(Video(4), Video(4, previous));
        var incomingId = model.Segments[1].Id;

        var op = new UpdateTransitionOperation(incomingId, Config(TransitionType.Glitch, 1.2, TransitionEasing.EaseOut));
        op.Execute(model);
        Assert.AreEqual(TransitionType.Glitch, model.Segments[1].InTransition!.Type);

        op.Undo(model);
        Assert.AreSame(previous, model.Segments[1].InTransition);
    }

    [TestMethod]
    public void Update_SetToNull_ClearsAConfiguredBoundary_UndoRestoresIt()
    {
        var previous = Config(TransitionType.PushDown, 0.8);
        var model = ModelWith(Video(4), Video(4, previous));
        var incomingId = model.Segments[1].Id;

        // "Remove Transition" is expressed as setting the config back to null.
        var op = new UpdateTransitionOperation(incomingId, null, "Remove Transition");
        op.Execute(model);
        Assert.IsNull(model.Segments[1].InTransition);

        op.Undo(model);
        Assert.AreSame(previous, model.Segments[1].InTransition);
    }

    [TestMethod]
    public void Update_ExplicitNone_IsDistinctFromNull()
    {
        // An explicit Type=None must round-trip as a real (non-null) config distinct from
        // "unconfigured" — TransitionResolver treats these two states differently.
        var model = ModelWith(Video(4), Video(4));
        var incomingId = model.Segments[1].Id;

        var op = new UpdateTransitionOperation(incomingId, Config(TransitionType.None));
        op.Execute(model);

        var applied = model.Segments[1].InTransition;
        Assert.IsNotNull(applied);
        Assert.AreEqual(TransitionType.None, applied!.Type);

        op.Undo(model);
        Assert.IsNull(model.Segments[1].InTransition);
    }

    [TestMethod]
    public void Update_Redo_Reapplies()
    {
        var model = ModelWith(Video(4), Video(4));
        var incomingId = model.Segments[1].Id;
        var config = Config(TransitionType.ZoomBlur, 0.9);

        var op = new UpdateTransitionOperation(incomingId, config);
        op.Execute(model);
        op.Undo(model);
        Assert.IsNull(model.Segments[1].InTransition);

        op.Execute(model);
        Assert.AreSame(config, model.Segments[1].InTransition);
    }

    [TestMethod]
    public void Update_NonExistentId_DoesNotThrow_ModelUnchanged()
    {
        var model = ModelWith(Video(4), Video(4));

        var op = new UpdateTransitionOperation("does-not-exist", Config());
        op.Execute(model);
        op.Undo(model);

        Assert.IsNull(model.Segments[0].InTransition);
        Assert.IsNull(model.Segments[1].InTransition);
    }

    [TestMethod]
    public void Update_NullSegmentId_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(() => new UpdateTransitionOperation(null!, Config()));
    }

    [TestMethod]
    public void Update_FirstSegment_NeverReceivesATransition_WhenOperationTargetsItDirectly()
    {
        // The first segment has no leading boundary. UpdateTransitionOperation guards this
        // itself (index <= 0 -> not-found), matching ApplyTransitionToAllBoundariesOperation
        // and TransitionResolver's own "index <= 0 -> no transition" rule. Without this guard,
        // a config written to segment 0 lies dormant on InTransition and can activate later if
        // that segment is ever reordered away from index 0 — a latent data bug.
        var model = ModelWith(Video(4), Video(4));
        var firstId = model.Segments[0].Id;

        var op = new UpdateTransitionOperation(firstId, Config());
        op.Execute(model);

        Assert.IsNull(model.Segments[0].InTransition, "Segment 0 has no boundary and must never receive a transition.");

        // Undo must also be a no-op — there is nothing to restore.
        op.Undo(model);
        Assert.IsNull(model.Segments[0].InTransition);
    }

    // ── ApplyTransitionToAllBoundariesOperation ──────────────────────────

    [TestMethod]
    public void ApplyToAll_SetsConfig_OnEveryBoundary_ButNeverTheFirstSegment()
    {
        var model = ModelWith(Video(4), Video(4), Video(4), Video(4));
        var config = Config(TransitionType.Wipe, 0.4);

        var op = new ApplyTransitionToAllBoundariesOperation(config);
        op.Execute(model);

        Assert.IsNull(model.Segments[0].InTransition);
        Assert.AreSame(config, model.Segments[1].InTransition);
        Assert.AreSame(config, model.Segments[2].InTransition);
        Assert.AreSame(config, model.Segments[3].InTransition);
    }

    [TestMethod]
    public void ApplyToAll_IsSingleUndoEntry_RestoringEveryBoundaryIndependently()
    {
        // Mixed starting state: boundary 1 was unconfigured (null), boundary 2 had one config,
        // boundary 3 had a different config. A single Undo must restore each to its own prior
        // value, not collapse them all to one shared "previous".
        var configAtTwo = Config(TransitionType.SlideRight, 0.3);
        var configAtThree = Config(TransitionType.DipToWhite, 1.0, TransitionEasing.EaseOut);
        var model = ModelWith(
            Video(4),
            Video(4, null),
            Video(4, configAtTwo),
            Video(4, configAtThree));

        var applied = Config(TransitionType.PushLeft, 0.5);
        var op = new ApplyTransitionToAllBoundariesOperation(applied);
        op.Execute(model);

        Assert.AreSame(applied, model.Segments[1].InTransition);
        Assert.AreSame(applied, model.Segments[2].InTransition);
        Assert.AreSame(applied, model.Segments[3].InTransition);

        op.Undo(model);

        Assert.IsNull(model.Segments[0].InTransition);
        Assert.IsNull(model.Segments[1].InTransition);
        Assert.AreSame(configAtTwo, model.Segments[2].InTransition);
        Assert.AreSame(configAtThree, model.Segments[3].InTransition);
    }

    [TestMethod]
    public void ApplyToAll_Redo_ReappliesToEveryBoundary()
    {
        var model = ModelWith(Video(4), Video(4), Video(4));
        var applied = Config(TransitionType.WhipPanLeft, 0.7);

        var op = new ApplyTransitionToAllBoundariesOperation(applied);
        op.Execute(model);
        op.Undo(model);
        Assert.IsNull(model.Segments[1].InTransition);
        Assert.IsNull(model.Segments[2].InTransition);

        op.Execute(model);
        Assert.AreSame(applied, model.Segments[1].InTransition);
        Assert.AreSame(applied, model.Segments[2].InTransition);
    }

    [TestMethod]
    public void ApplyToAll_WithNullConfig_ClearsEveryBoundary()
    {
        var model = ModelWith(
            Video(4),
            Video(4, Config(TransitionType.Fade)),
            Video(4, Config(TransitionType.Glitch)));

        var op = new ApplyTransitionToAllBoundariesOperation(null);
        op.Execute(model);

        Assert.IsNull(model.Segments[1].InTransition);
        Assert.IsNull(model.Segments[2].InTransition);

        op.Undo(model);
        Assert.IsNotNull(model.Segments[1].InTransition);
        Assert.IsNotNull(model.Segments[2].InTransition);
    }

    [TestMethod]
    public void ApplyToAll_SingleSegmentTimeline_IsANoOp()
    {
        var model = ModelWith(Video(4));

        var op = new ApplyTransitionToAllBoundariesOperation(Config());
        op.Execute(model);

        Assert.IsNull(model.Segments[0].InTransition);

        op.Undo(model);
        Assert.IsNull(model.Segments[0].InTransition);
    }

    [TestMethod]
    public void ApplyToAll_EmptyTimeline_DoesNotThrow()
    {
        var model = new TimelineModel();

        var op = new ApplyTransitionToAllBoundariesOperation(Config());
        op.Execute(model);
        op.Undo(model);

        Assert.AreEqual(0, model.Segments.Count);
    }
}

/// <summary>
/// Round-trips a timeline containing a mix of <c>null</c> (unconfigured), explicit
/// <see cref="TransitionType.None"/> (hard cut), and fully-configured
/// <see cref="TimelineSegment.InTransition"/> boundaries through
/// <see cref="MusioPackageService.SaveAsync"/> / <see cref="MusioPackageService.OpenAsync"/>.
/// </summary>
/// <remarks>
/// This is the highest-consequence coverage for this feature: the <c>null</c> vs explicit
/// <c>None</c> distinction only matters if it survives serialization — <c>null</c> means
/// "fall back to legacy behaviour" and explicit <c>None</c> means "hard cut, suppress even
/// the legacy fallback". If a future change to <see cref="TransitionConfig"/> or the package
/// format ever collapses these two states into one on save/open, this test must fail.
/// </remarks>
[TestClass]
public sealed class TransitionPackageRoundTripTests
{
    private string _root = string.Empty;
    private string _sourceFolder = string.Empty;
    private string _workingRoot = string.Empty;

    [TestInitialize]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "musio_transition_pkg_" + Guid.NewGuid().ToString("N"));
        _sourceFolder = Path.Combine(_root, "session");
        _workingRoot = Path.Combine(_root, "working");
        Directory.CreateDirectory(_sourceFolder);
        Directory.CreateDirectory(_workingRoot);
    }

    [TestCleanup]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteFile(string name, int sizeBytes, byte fill = 0xAB)
    {
        var path = Path.Combine(_sourceFolder, name);
        File.WriteAllBytes(path, Enumerable.Repeat(fill, sizeBytes).ToArray());
        return path;
    }

    [TestMethod]
    public async Task SaveThenOpen_PreservesNullExplicitNoneAndConfigured_AsDistinctPerBoundaryStates()
    {
        var video = WriteFile("video.mp4", 4096);

        var project = new Project
        {
            Name = "Transition round trip",
            VideoFilePath = video,
            Duration = TimeSpan.FromSeconds(16),
            Width = 1920,
            Height = 1080,
            Fps = 30,
        };

        var timeline = new TimelineModel
        {
            Duration = project.Duration,
            TrimEnd = project.Duration,
            Fps = 30,
            PrimaryVideoFilePath = video,
        };

        // Segment 0: no boundary, InTransition must stay null regardless.
        timeline.Segments.Add(new VideoSegment
        {
            VideoFilePath = video,
            SourceDuration = TimeSpan.FromSeconds(4),
            Duration = TimeSpan.FromSeconds(4),
            SourceWidth = 1920,
            SourceHeight = 1080,
            Fps = 30,
        });

        // Segment 1: unconfigured boundary — falls back to legacy behaviour.
        timeline.Segments.Add(new VideoSegment
        {
            VideoFilePath = video,
            SourceDuration = TimeSpan.FromSeconds(4),
            Duration = TimeSpan.FromSeconds(4),
            SourceWidth = 1920,
            SourceHeight = 1080,
            Fps = 30,
            InTransition = null,
        });

        // Segment 2: explicit hard cut — suppresses even the legacy fallback.
        timeline.Segments.Add(new VideoSegment
        {
            VideoFilePath = video,
            SourceDuration = TimeSpan.FromSeconds(4),
            Duration = TimeSpan.FromSeconds(4),
            SourceWidth = 1920,
            SourceHeight = 1080,
            Fps = 30,
            InTransition = new TransitionConfig
            {
                Type = TransitionType.None,
                Duration = TimeSpan.FromSeconds(0.5),
                Easing = TransitionEasing.Linear,
            },
        });

        // Segment 3: fully configured, non-default values throughout.
        timeline.Segments.Add(new VideoSegment
        {
            VideoFilePath = video,
            SourceDuration = TimeSpan.FromSeconds(4),
            Duration = TimeSpan.FromSeconds(4),
            SourceWidth = 1920,
            SourceHeight = 1080,
            Fps = 30,
            InTransition = new TransitionConfig
            {
                Type = TransitionType.WhipPanLeft,
                Duration = TimeSpan.FromSeconds(0.85),
                Easing = TransitionEasing.EaseInOut,
            },
        });

        var packagePath = Path.Combine(_root, "transitions.musio");
        await MusioPackageService.SaveAsync(packagePath, project, new CompositionConfig(), timeline);
        var opened = await MusioPackageService.OpenAsync(packagePath, _workingRoot);

        Assert.AreEqual(4, opened.Timeline.Segments.Count);

        Assert.IsNull(opened.Timeline.Segments[0].InTransition,
            "segment 0 has no boundary and must never carry a transition");

        Assert.IsNull(opened.Timeline.Segments[1].InTransition,
            "an unconfigured boundary must reload as null, not a default-constructed config");

        var reloadedNone = opened.Timeline.Segments[2].InTransition;
        Assert.IsNotNull(reloadedNone, "an explicit Type=None config must reload as a real config, not null");
        Assert.AreEqual(TransitionType.None, reloadedNone!.Type);
        Assert.AreEqual(TimeSpan.FromSeconds(0.5), reloadedNone.Duration);
        Assert.AreEqual(TransitionEasing.Linear, reloadedNone.Easing);

        var reloadedConfigured = opened.Timeline.Segments[3].InTransition;
        Assert.IsNotNull(reloadedConfigured);
        Assert.AreEqual(TransitionType.WhipPanLeft, reloadedConfigured!.Type);
        Assert.AreEqual(TimeSpan.FromSeconds(0.85), reloadedConfigured.Duration);
        Assert.AreEqual(TransitionEasing.EaseInOut, reloadedConfigured.Easing);
    }
}
