using System.Collections.ObjectModel;

namespace Musio.Core.Processing;

/// <summary>
/// One requested camera shot before the path builder turns neighboring shots into a
/// single ordered camera move.
/// </summary>
/// <param name="RampStart">Time, in source seconds, where the camera begins zooming in.</param>
/// <param name="HoldStart">Time, in source seconds, where the camera reaches <paramref name="Zoom"/>.</param>
/// <param name="HoldEnd">Time, in source seconds, where the settled hold would normally end.</param>
/// <param name="ReleaseEnd">Time, in source seconds, where the camera would normally return to 1×.</param>
/// <param name="Zoom">Target zoom level. Values at or below 1× are no-ops and are dropped.</param>
/// <param name="CenterX">Target focal point in source pixels.</param>
/// <param name="CenterY">Target focal point in source pixels.</param>
/// <param name="Seed">
/// Stable drift heading seed. Callers derive this from the original ramp start so
/// preview and export keep the same drift direction even after the path clips holds.
/// </param>
public readonly record struct ZoomShot(
    double RampStart,
    double HoldStart,
    double HoldEnd,
    double ReleaseEnd,
    float Zoom,
    float CenterX,
    float CenterY,
    int Seed,
    bool IsManual = false);

/// <summary>
/// The camera state resolved from a <see cref="ZoomCameraPath"/> at one instant.
/// </summary>
/// <param name="Zoom">Resolved zoom level before downstream drift is applied.</param>
/// <param name="CenterX">Resolved focal point X in source pixels.</param>
/// <param name="CenterY">Resolved focal point Y in source pixels.</param>
/// <param name="SegmentProgress">
/// Normalized progress through the active shot. During a handoff this is blended
/// from the two shots' original progress values so drift keeps moving through the
/// chained camera move instead of restarting at the join.
/// </param>
/// <param name="HeadingX">X component of the drift heading vector.</param>
/// <param name="HeadingY">Y component of the drift heading vector.</param>
/// <param name="DriftScale">
/// Multiplier for downstream drift amplitude. Handoffs suppress drift in the middle
/// because a deliberate camera move already supplies motion; ordinary holds and ramps
/// leave it at 1.
/// </param>
/// <param name="CursorFollowWeight">
/// How strongly the compositor should re-centre this sample on the live cursor rather
/// than on <see cref="CenterX"/>/<see cref="CenterY"/>: <c>1</c> for an auto (click-driven)
/// shot, <c>0</c> for a manual one.
/// <para>
/// It is a weight rather than a bool precisely so a manual shot can hand off to an auto
/// shot. Those two kinds of shot resolve their focal point from different sources — the
/// live cursor versus the keyframe's stored centre — so flipping between them at a piece
/// boundary would snap the camera. Across a handoff this eases between the two, which
/// keeps the focal point continuous even though its *source* changes.
/// </para>
/// </param>
public readonly record struct ZoomCameraSample(
    float Zoom,
    float CenterX,
    float CenterY,
    float SegmentProgress,
    float HeadingX,
    float HeadingY,
    float DriftScale,
    float CursorFollowWeight = 0f);

/// <summary>
/// A precomputed, immutable camera path for zoom shots that overlap or nearly touch.
/// <para>
/// This type builds one strictly ordered piecewise path: linked shots hand off
/// directly from one target zoom and center to the next, so there is never more
/// than one active camera piece to resolve.
/// </para>
/// <para>
/// Build performs all ordering, containment repair, and piece creation up front.
/// <see cref="TryEvaluate"/> only reads immutable arrays and is therefore a pure
/// function of time, which is required by preview scrubbing, export, and motion-blur
/// shutter sampling that can query frames out of order.
/// </para>
/// </summary>
public sealed class ZoomCameraPath
{
    /// <summary>
    /// Maximum source-time gap that still turns two shots into one camera handoff.
    /// <para>
    /// A shot's <c>RampStart</c>/<c>ReleaseEnd</c> are the instants the camera leaves and
    /// returns to 1×, so this value is literally <b>how long the frame is allowed to sit
    /// fully zoomed out before the next zoom begins</b>. Anything shorter reads as a pump —
    /// the camera snaps out to full frame and immediately punches back in — which is the
    /// artifact the chained path exists to remove, so those pairs are linked and hand off
    /// directly instead.
    /// </para>
    /// <para>
    /// This was originally 0.35s, which was too tight to be useful: with the default ease
    /// durations, two segments dragged out near each other typically leave a 0.4–0.7s gap,
    /// so they stayed unlinked and the feature never engaged on exactly the "close together"
    /// case it was built for. 0.75s links that range while still preserving a deliberate
    /// return to full frame whenever the user leaves a full second or more between zooms.
    /// </para>
    /// </summary>
    public const double LinkGapSeconds = 0.75;

    /// <summary>Shortest handoff window allowed when overlapping holds would otherwise collapse to a snap.</summary>
    public const double MinTransitionSeconds = 0.25;

    private const double ZoomNoOpEpsilon = 1e-4;
    private const double SmallDuration = 1e-9;
    private const double HeadingNormalizeEpsilon = 1e-4;
    private const double ArcOnsetRatio = 0.35;
    private const double ArcFullRatio = 1.2;
    private const double ArcStrength = 0.6;

    /// <summary>
    /// Zoom-level difference between two linked shots at which the arc floor reaches the
    /// lower of the two zooms, guaranteeing the handoff never undershoots its destination.
    /// Below this the floor eases back toward 0, restoring the full arc for shots that sit
    /// at (or near) the same zoom, where a dip is the only way to widen the frame for a
    /// long lateral move.
    /// </summary>
    private const double ArcFloorFullDelta = 0.25;

    private static readonly ReadOnlyCollection<ZoomShot> EmptyShots = Array.AsReadOnly(Array.Empty<ZoomShot>());

    private readonly bool[] _linkedAfter;
    private readonly Piece[] _pieces;
    private readonly ShotData[] _shotData;
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;

    /// <summary>
    /// An empty path that never evaluates to an active zoom. Reusing a singleton keeps
    /// callers from having to special-case null while preserving the pure lookup model.
    /// </summary>
    public static ZoomCameraPath Empty { get; } = new(
        EmptyShots,
        Array.Empty<bool>(),
        Array.Empty<ShotData>(),
        Array.Empty<Piece>(),
        0,
        0);

    private ZoomCameraPath(
        ReadOnlyCollection<ZoomShot> shots,
        bool[] linkedAfter,
        ShotData[] shotData,
        Piece[] pieces,
        int sourceWidth,
        int sourceHeight)
    {
        Shots = shots;
        _linkedAfter = linkedAfter;
        _shotData = shotData;
        _pieces = pieces;
        _sourceWidth = Math.Max(0, sourceWidth);
        _sourceHeight = Math.Max(0, sourceHeight);
        IsEmpty = pieces.Length == 0;
    }

    /// <summary>True when this path has no active camera pieces.</summary>
    public bool IsEmpty { get; }

    /// <summary>
    /// The normalized shots after sorting and hold repair. These are the times the
    /// renderer should reason about; the original hold endpoints are kept privately
    /// only so transition drift can preserve the segment-relative motion design.
    /// </summary>
    public IReadOnlyList<ZoomShot> Shots { get; }

    /// <summary>
    /// Builds an immutable camera path from unordered shot requests.
    /// <para>
    /// The repair step runs each handoff across the INCOMING shot's own ramp — its leading
    /// edge on the timeline, over its authored <c>PreDuration</c> — so an overlapped segment
    /// animates in at the same place and speed it would have unoverlapped. The outgoing shot
    /// holds until that edge. See <see cref="RepairLinkedHolds"/> for the degenerate cases.
    /// </para>
    /// </summary>
    /// <param name="shots">Shot requests in source time. Invalid or no-op shots are ignored.</param>
    /// <param name="sourceWidth">Source width in pixels, used only to size long-move arc easing.</param>
    /// <param name="sourceHeight">Source height in pixels, used only to size long-move arc easing.</param>
    public static ZoomCameraPath Build(IEnumerable<ZoomShot> shots, int sourceWidth, int sourceHeight)
    {
        if (shots is null)
            return Empty;

        var candidates = new List<BuildShot>();
        int originalIndex = 0;
        foreach (var shot in shots)
        {
            if (!IsValidShot(shot))
            {
                originalIndex++;
                continue;
            }

            double rampStart = shot.RampStart;
            double holdStart = Math.Max(shot.HoldStart, rampStart);
            double holdEnd = Math.Max(shot.HoldEnd, holdStart);
            double releaseEnd = Math.Max(shot.ReleaseEnd, holdEnd);

            candidates.Add(new BuildShot
            {
                RampStart = rampStart,
                HoldStart = holdStart,
                HoldEnd = holdEnd,
                ReleaseEnd = releaseEnd,
                Zoom = shot.Zoom,
                CenterX = shot.CenterX,
                CenterY = shot.CenterY,
                Seed = shot.Seed,
                IsManual = shot.IsManual,
                OriginalRampStart = shot.RampStart,
                OriginalHoldStart = holdStart,
                OriginalHoldEnd = holdEnd,
                OriginalReleaseEnd = shot.ReleaseEnd,
                OriginalIndex = originalIndex,
            });

            originalIndex++;
        }

        if (candidates.Count == 0)
            return Empty;

        candidates.Sort(static (a, b) =>
        {
            int byHoldStart = a.HoldStart.CompareTo(b.HoldStart);
            if (byHoldStart != 0) return byHoldStart;

            int byRampStart = a.RampStart.CompareTo(b.RampStart);
            if (byRampStart != 0) return byRampStart;

            return a.OriginalIndex.CompareTo(b.OriginalIndex);
        });

        var linkedAfter = new bool[Math.Max(0, candidates.Count - 1)];
        for (int i = 0; i < linkedAfter.Length; i++)
        {
            linkedAfter[i] = candidates[i + 1].OriginalRampStart <=
                candidates[i].OriginalReleaseEnd + LinkGapSeconds;
        }

        RepairLinkedHolds(candidates, linkedAfter);
        EnforceMonotonicHolds(candidates);

        var shotData = new ShotData[candidates.Count];
        var normalizedShots = new ZoomShot[candidates.Count];
        for (int i = 0; i < candidates.Count; i++)
        {
            var shot = candidates[i].ToZoomShot();
            normalizedShots[i] = shot;
            shotData[i] = new ShotData(
                shot,
                candidates[i].OriginalHoldStart,
                candidates[i].OriginalHoldEnd);
        }

        var pieces = BuildPieces(shotData, linkedAfter);
        if (pieces.Length == 0)
            return Empty;

        return new ZoomCameraPath(
            Array.AsReadOnly(normalizedShots),
            linkedAfter,
            shotData,
            pieces,
            sourceWidth,
            sourceHeight);
    }

    /// <summary>
    /// Returns whether shot <paramref name="shotIndex"/> hands off directly to the
    /// next shot. The value is computed before hold repair so timeline indicators use
    /// the same source-time predicate as rendering and cannot disagree visually.
    /// </summary>
    public bool IsLinkedAfter(int shotIndex)
        => shotIndex >= 0 && shotIndex < _linkedAfter.Length && _linkedAfter[shotIndex];

    /// <summary>
    /// Evaluates the path at one source-time instant.
    /// <para>
    /// A binary search over prebuilt, ordered pieces keeps the function stateless and
    /// deterministic. Outside those pieces the caller should render the normal 1×
    /// frame with no active segment rather than trying to infer state from the last
    /// call.
    /// </para>
    /// </summary>
    /// <param name="timeSeconds">Source time in seconds.</param>
    /// <param name="sample">Resolved camera sample when a piece is active.</param>
    /// <returns>True when the path owns the camera at this instant.</returns>
    public bool TryEvaluate(double timeSeconds, out ZoomCameraSample sample)
    {
        sample = default;
        if (!double.IsFinite(timeSeconds) || _pieces.Length == 0)
            return false;

        int lo = 0;
        int hi = _pieces.Length - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) / 2);
            Piece piece = _pieces[mid];

            if (timeSeconds < piece.Start)
            {
                hi = mid - 1;
            }
            else if (timeSeconds > piece.End)
            {
                lo = mid + 1;
            }
            else
            {
                sample = EvaluatePiece(piece, timeSeconds);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Shared linkage predicate for UI and rendering. Linkage is intentionally based
    /// on source time: a timeline cut between two source-adjacent zooms should not
    /// force the camera to visit 1× when the source motion says they are one chain.
    /// </summary>
    public static bool AreLinked(
        Musio.Core.Timeline.ZoomKeyframe earlier,
        Musio.Core.Timeline.ZoomKeyframe later)
    {
        if (earlier is null || later is null)
            return false;

        return later.Start <= earlier.End + TimeSpan.FromSeconds(LinkGapSeconds);
    }

    private static bool IsValidShot(ZoomShot shot)
        => double.IsFinite(shot.RampStart)
            && double.IsFinite(shot.HoldStart)
            && double.IsFinite(shot.HoldEnd)
            && double.IsFinite(shot.ReleaseEnd)
            && float.IsFinite(shot.Zoom)
            && float.IsFinite(shot.CenterX)
            && float.IsFinite(shot.CenterY)
            && shot.Zoom > 1f + ZoomNoOpEpsilon;

    /// <summary>
    /// Separates the holds of consecutive shots so exactly one piece is ever active, and in
    /// doing so decides when each handoff runs.
    /// <para>
    /// <b>The handoff runs across the INCOMING shot's own ramp</b> — from
    /// <see cref="ZoomShot.RampStart"/> to <see cref="ZoomShot.HoldStart"/> of shot B — not from
    /// wherever shot A happened to stop holding. That window is exactly the segment's leading
    /// edge on the timeline and exactly its authored <c>PreDuration</c>, so an overlapped
    /// segment animates in at the same place and over the same span it would have if nothing
    /// overlapped it. The outgoing shot simply holds until that edge arrives.
    /// </para>
    /// <para>
    /// Deriving the window from A's hold end instead (the original approach) put the move in the
    /// wrong place and gave it the wrong duration: for two segments overlapping by ~0.5s it
    /// started the animation early, when A stopped holding, and compressed it into the leftover
    /// ~440ms instead of B's authored 1s — so it read as both mistimed and faster than a normal
    /// zoom-in.
    /// </para>
    /// <para>
    /// The clamp to <c>[a.HoldStart, a.HoldEnd]</c> covers the two degenerate directions: a B
    /// whose ramp opens before A has even settled (start as soon as A settles) and a linked pair
    /// with a real gap between them (start when A stops holding, so the camera never releases
    /// toward 1× in between — the gap is absorbed into a longer move).
    /// </para>
    /// </summary>
    private static void RepairLinkedHolds(List<BuildShot> shots, bool[] linkedAfter)
    {
        for (int i = 0; i < linkedAfter.Length; i++)
        {
            if (!linkedAfter[i] && shots[i + 1].HoldStart > shots[i].HoldEnd)
                continue;

            BuildShot a = shots[i];
            BuildShot b = shots[i + 1];

            // Run the move across B's own ramp: its timeline leading edge, its PreDuration.
            double windowStart = Math.Clamp(b.RampStart, a.HoldStart, a.HoldEnd);
            double windowEnd = Math.Max(b.HoldStart, windowStart);

            // Only when that authored window is too short to read as a move do we widen it,
            // symmetrically, bounded by how much hold either neighbour can spare.
            if (windowEnd - windowStart < MinTransitionSeconds)
            {
                double mid = (windowStart + windowEnd) / 2.0;
                windowStart = Math.Max(mid - (MinTransitionSeconds / 2.0), a.HoldStart);
                windowEnd = Math.Min(Math.Max(mid + (MinTransitionSeconds / 2.0), windowStart), b.HoldEnd);
            }

            if (windowStart > windowEnd)
            {
                double midpoint = (windowStart + windowEnd) / 2.0;
                windowStart = midpoint;
                windowEnd = midpoint;
            }

            a.HoldEnd = windowStart;
            b.HoldStart = windowEnd;
            b.HoldEnd = Math.Max(b.HoldEnd, b.HoldStart);
            a.ReleaseEnd = Math.Max(a.ReleaseEnd, a.HoldEnd);
            b.ReleaseEnd = Math.Max(b.ReleaseEnd, b.HoldEnd);

            shots[i] = a;
            shots[i + 1] = b;
        }
    }

    private static void EnforceMonotonicHolds(List<BuildShot> shots)
    {
        for (int i = 0; i < shots.Count; i++)
        {
            BuildShot current = shots[i];
            current.HoldStart = Math.Max(current.HoldStart, current.RampStart);
            current.HoldEnd = Math.Max(current.HoldEnd, current.HoldStart);
            current.ReleaseEnd = Math.Max(current.ReleaseEnd, current.HoldEnd);
            shots[i] = current;

            if (i >= shots.Count - 1)
                continue;

            BuildShot next = shots[i + 1];
            if (current.HoldEnd > next.HoldStart)
            {
                next.HoldStart = current.HoldEnd;
                next.HoldEnd = Math.Max(next.HoldEnd, next.HoldStart);
                next.ReleaseEnd = Math.Max(next.ReleaseEnd, next.HoldEnd);
                shots[i + 1] = next;
            }
        }
    }

    private static Piece[] BuildPieces(ShotData[] shots, bool[] linkedAfter)
    {
        var pieces = new List<Piece>(Math.Max(1, shots.Length * 3));
        for (int i = 0; i < shots.Length; i++)
        {
            ZoomShot shot = shots[i].Shot;

            if (i == 0 || !linkedAfter[i - 1])
                AddPiece(pieces, shot.RampStart, shot.HoldStart, PieceKind.RampIn, i);

            AddPiece(pieces, shot.HoldStart, shot.HoldEnd, PieceKind.Hold, i);

            if (i < linkedAfter.Length && linkedAfter[i])
            {
                AddPiece(pieces, shot.HoldEnd, shots[i + 1].Shot.HoldStart, PieceKind.Transition, i);
            }
            else
            {
                AddPiece(pieces, shot.HoldEnd, shot.ReleaseEnd, PieceKind.Release, i);
            }
        }

        pieces.Sort(static (a, b) =>
        {
            int byStart = a.Start.CompareTo(b.Start);
            if (byStart != 0) return byStart;
            return a.End.CompareTo(b.End);
        });

        return [.. pieces];
    }

    private static void AddPiece(List<Piece> pieces, double start, double end, PieceKind kind, int shotIndex)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || start > end)
            return;

        pieces.Add(new Piece(start, end, kind, shotIndex));
    }

    private ZoomCameraSample EvaluatePiece(Piece piece, double timeSeconds)
    {
        return piece.Kind switch
        {
            PieceKind.RampIn => EvaluateSingleShot(_shotData[piece.ShotIndex], timeSeconds, 1f, _shotData[piece.ShotIndex].Shot.Zoom),
            PieceKind.Hold => EvaluateSingleShot(_shotData[piece.ShotIndex], timeSeconds, _shotData[piece.ShotIndex].Shot.Zoom, _shotData[piece.ShotIndex].Shot.Zoom),
            PieceKind.Release => EvaluateSingleShot(_shotData[piece.ShotIndex], timeSeconds, _shotData[piece.ShotIndex].Shot.Zoom, 1f),
            PieceKind.Transition => EvaluateTransition(piece.ShotIndex, timeSeconds),
            _ => default,
        };
    }

    private static ZoomCameraSample EvaluateSingleShot(ShotData data, double timeSeconds, float fromZoom, float toZoom)
    {
        ZoomShot shot = data.Shot;
        double pieceStart = fromZoom < toZoom ? shot.RampStart : shot.HoldEnd;
        double pieceEnd = fromZoom < toZoom
            ? shot.HoldStart
            : fromZoom > toZoom
                ? shot.ReleaseEnd
                : shot.HoldEnd;

        float u = Normalize(timeSeconds, pieceStart, pieceEnd);
        float zoom = fromZoom == toZoom ? toZoom : Ease(fromZoom, toZoom, u);
        float progress = Normalize(timeSeconds, shot.RampStart, shot.ReleaseEnd);
        var (headingX, headingY) = CameraDrift.HeadingFromSeed(shot.Seed);

        return new ZoomCameraSample(
            zoom,
            shot.CenterX,
            shot.CenterY,
            progress,
            headingX,
            headingY,
            1f,
            shot.IsManual ? 0f : 1f);
    }

    private ZoomCameraSample EvaluateTransition(int shotIndex, double timeSeconds)
    {
        ShotData fromData = _shotData[shotIndex];
        ShotData toData = _shotData[shotIndex + 1];
        ZoomShot from = fromData.Shot;
        ZoomShot to = toData.Shot;

        double duration = to.HoldStart - from.HoldEnd;
        float u = duration > SmallDuration
            ? Clamp01((timeSeconds - from.HoldEnd) / duration)
            : 1f;
        float e = CubicBezierEasing.EaseInOutCinematic(u);

        float baseZoom = Lerp(from.Zoom, to.Zoom, e);
        float centerX = Lerp(from.CenterX, to.CenterX, e);
        float centerY = Lerp(from.CenterY, to.CenterY, e);

        double travel = Math.Sqrt(
            Math.Pow(to.CenterX - from.CenterX, 2.0) +
            Math.Pow(to.CenterY - from.CenterY, 2.0));
        double refZoom = Math.Min(from.Zoom, to.Zoom);
        double viewportDiag = Math.Sqrt(
            Math.Pow(_sourceWidth / refZoom, 2.0) +
            Math.Pow(_sourceHeight / refZoom, 2.0));
        double ratio = travel / Math.Max(1e-6, viewportDiag);
        double arcAmount = ArcStrength * Clamp01((ratio - ArcOnsetRatio) / (ArcFullRatio - ArcOnsetRatio));

        // sin² is zero and has zero derivative at both ends. A plain sine or
        // parabolic bump would lower the zoom with non-zero endpoint velocity,
        // reintroducing the very kick the chained path removes.
        double s = Math.Sin(Math.PI * e);
        double bump = s * s;

        // Apply the arc to the zoom ABOVE a floor rather than to the whole zoom, so a
        // handoff between two DIFFERENT zoom levels can never undershoot its destination.
        // Dividing the whole interpolated zoom (the original form) sent a 2x -> 1.5x move
        // down to ~1.39x mid-transition and then back up to 1.5x — a bounce, because the
        // move already widens the frame and the arc widened it again on top. The floor
        // rises to the lower endpoint as the two zooms diverge, which makes such a move
        // monotonic, and falls to 0 when they are equal, where the arc is the only thing
        // that can widen the frame and is still wanted at full strength.
        // Note this is a smooth blend, never a clamp: a max() against the floor would flatten
        // the curve at the crossing point and reintroduce a derivative corner.
        double zoomDelta = Math.Abs(from.Zoom - to.Zoom);
        double arcFloor = Math.Min(from.Zoom, to.Zoom) * Clamp01(zoomDelta / ArcFloorFullDelta);
        float zoom = (float)(arcFloor + ((baseZoom - arcFloor) / (1.0 + (arcAmount * bump))));
        zoom = Math.Clamp(zoom, 1f, Math.Max(from.Zoom, to.Zoom));

        float progressA = Normalize(fromData.OriginalHoldEnd, from.RampStart, from.ReleaseEnd);
        float progressB = Normalize(toData.OriginalHoldStart, to.RampStart, to.ReleaseEnd);
        float progress = Lerp(progressA, progressB, e);

        var (fromHeadingX, fromHeadingY) = CameraDrift.HeadingFromSeed(from.Seed);
        var (toHeadingX, toHeadingY) = CameraDrift.HeadingFromSeed(to.Seed);
        float headingX = Lerp(fromHeadingX, toHeadingX, e);
        float headingY = Lerp(fromHeadingY, toHeadingY, e);
        float headingLength = MathF.Sqrt((headingX * headingX) + (headingY * headingY));
        if (headingLength > HeadingNormalizeEpsilon && float.IsFinite(headingLength))
        {
            headingX /= headingLength;
            headingY /= headingLength;
        }

        float driftScale = (float)(1.0 - (0.85 * bump));

        return new ZoomCameraSample(
            zoom,
            centerX,
            centerY,
            progress,
            headingX,
            headingY,
            driftScale,
            Lerp(from.IsManual ? 0f : 1f, to.IsManual ? 0f : 1f, e));
    }

    private static float Ease(float from, float to, float t)
    {
        float clamped = Math.Clamp(t, 0f, 1f);
        if (clamped <= 0f) return from;
        if (clamped >= 1f) return to;

        float eased = CubicBezierEasing.EaseInOutCinematic(clamped);
        return from + ((to - from) * eased);
    }

    private static float Normalize(double value, double start, double end)
    {
        double duration = end - start;
        if (!double.IsFinite(value) || !double.IsFinite(duration) || duration <= SmallDuration)
            return 0f;

        return Clamp01((value - start) / duration);
    }

    private static float Clamp01(double value)
    {
        if (!double.IsFinite(value))
            return 0f;

        return (float)Math.Clamp(value, 0.0, 1.0);
    }

    private static float Lerp(float from, float to, float amount)
        => from + ((to - from) * amount);

    private readonly record struct ShotData(
        ZoomShot Shot,
        double OriginalHoldStart,
        double OriginalHoldEnd);

    private readonly record struct Piece(
        double Start,
        double End,
        PieceKind Kind,
        int ShotIndex);

    private enum PieceKind
    {
        RampIn,
        Hold,
        Release,
        Transition,
    }

    private struct BuildShot
    {
        public double RampStart;
        public double HoldStart;
        public double HoldEnd;
        public double ReleaseEnd;
        public float Zoom;
        public float CenterX;
        public float CenterY;
        public int Seed;
        public bool IsManual;
        public double OriginalRampStart;
        public double OriginalHoldStart;
        public double OriginalHoldEnd;
        public double OriginalReleaseEnd;
        public int OriginalIndex;

        public readonly ZoomShot ToZoomShot()
            => new(RampStart, HoldStart, HoldEnd, ReleaseEnd, Zoom, CenterX, CenterY, Seed, IsManual);

    }
}
