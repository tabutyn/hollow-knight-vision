namespace HollowKnightVision.Windows.Core;

public sealed record LiveAtlasPresentation(
    BgraFrame LiveImage,
    StencilRect LiveBounds,
    AtlasPoint FocusPoint,
    LiveTiledAtlasSnapshot? Atlas,
    bool IntegratedFrame = false);

public sealed record LiveAtlasIntegrationCompletion(
    double Timestamp,
    long ObservationId,
    LiveTiledAtlasSnapshot Atlas);

public readonly record struct LiveAtlasCadenceDecision(
    bool Present,
    bool Integrate);

/// <summary>
/// Independently limits live presentation and the slower atlas-write path.
/// Capture timestamps own the schedule so processing jitter cannot manufacture
/// or suppress work. A one-microsecond tolerance keeps exact interval
/// boundaries deterministic in binary floating-point.
/// </summary>
public sealed class LiveAtlasCadence
{
    private const double BoundaryToleranceSeconds = 0.000_001;
    private readonly double minimumPresentationInterval;
    private readonly double minimumIntegrationInterval;
    private double? lastPresentationTimestamp;
    private double? lastIntegrationTimestamp;

    public LiveAtlasCadence(
        double presentationFramesPerSecond = 15,
        double atlasFramesPerSecond = 5)
    {
        if (!double.IsFinite(presentationFramesPerSecond)
            || presentationFramesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(presentationFramesPerSecond));
        }
        if (!double.IsFinite(atlasFramesPerSecond)
            || atlasFramesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(atlasFramesPerSecond));
        }

        minimumPresentationInterval = 1 / presentationFramesPerSecond;
        minimumIntegrationInterval = 1 / atlasFramesPerSecond;
    }

    public LiveAtlasCadenceDecision Decision(
        double timestamp,
        bool canIntegrate)
    {
        if (!double.IsFinite(timestamp))
            throw new ArgumentOutOfRangeException(nameof(timestamp));

        if (lastPresentationTimestamp is { } lastPresentation
            && timestamp < lastPresentation)
        {
            Reset();
        }
        if (lastPresentationTimestamp is { } previousPresentation
            && timestamp - previousPresentation + BoundaryToleranceSeconds
                < minimumPresentationInterval)
        {
            return new(false, false);
        }

        lastPresentationTimestamp = timestamp;
        if (!canIntegrate) return new(true, false);
        if (lastIntegrationTimestamp is { } previousIntegration
            && timestamp - previousIntegration + BoundaryToleranceSeconds
                < minimumIntegrationInterval)
        {
            return new(true, false);
        }

        lastIntegrationTimestamp = timestamp;
        return new(true, true);
    }

    public void Reset()
    {
        lastPresentationTimestamp = null;
        lastIntegrationTimestamp = null;
    }
}

public sealed record LiveAtlasAutoSavePresentation(
    long RegistrationCount,
    DateTimeOffset CreatedAt,
    TimeSpan Age);

/// <summary>
/// Derives Auto Save presentation from persisted atlas metadata. File write time
/// is intentionally not an input: publishing a newer manifest must not make the
/// atlas appear newly created.
/// </summary>
public sealed class LiveAtlasAutoSavePolicy(Func<DateTimeOffset>? now = null)
{
    private readonly Func<DateTimeOffset> now = now ?? (() => DateTimeOffset.UtcNow);

    public LiveAtlasAutoSavePresentation Summarize(
        long registrationCount,
        DateTimeOffset persistedAtlasCreatedAt)
    {
        if (registrationCount < 0)
            throw new ArgumentOutOfRangeException(nameof(registrationCount));

        return new(
            registrationCount,
            persistedAtlasCreatedAt,
            now() - persistedAtlasCreatedAt);
    }
}

/// <summary>
/// Owns the ordered frame-admission, atlas-composition, and presentation path.
/// Menu/fade frames are still presented, but only an established gameplay
/// session with current visible image signal may establish or refine the atlas.
/// </summary>
public sealed class LiveAtlasPresentationPipeline
{
    private const double CadenceBoundaryToleranceSeconds = 0.000_001;

    private sealed record IntegrationRequest(
        BgraFrame Frame,
        AtlasPoint CameraPosition,
        double SolveWidth,
        double Timestamp,
        bool HasGameplaySignal,
        long CaptureGeneration,
        ulong WorkGeneration);

    private readonly object stateGate = new();
    private readonly object transactionGate = new();
    private GameplayFrameAdmission admission = new();
    private readonly LiveTiledAtlas atlas;
    private readonly LiveAtlasCadence cadence;
    private readonly LatestFramePump<IntegrationRequest> integrationPump = new();
    private readonly bool runsIntegrationSynchronously;
    private readonly double minimumAtlasSubmissionInterval;
    private readonly TimeSpan integrationDelay;
    private readonly Action? integrationStarted;
    private readonly Action<LiveAtlasIntegrationCompletion>? integrationCompleted;
    private long nextObservationId;
    private long workGeneration;
    private double? lastLivePresentationTimestamp;
    private double? lastAtlasSubmissionTimestamp;
    private double? lastCommittedAtlasTimestamp;
    private long? lastCommittedCaptureGeneration;
    private AtlasPoint? presentationAnchor;
    private LiveTiledAtlasSnapshot? publishedAtlas;
    private bool resetInProgress;
    private Exception? integrationFailure;

    public LiveAtlasPresentationPipeline(
        LiveTiledAtlasPixelPolicy pixelPolicy =
            LiveTiledAtlasPixelPolicy.TemporalAgreement,
        double presentationFramesPerSecond = 60,
        double atlasFramesPerSecond = 7.5,
        bool runsIntegrationSynchronously = true,
        TimeSpan? integrationDelay = null,
        Action? integrationStarted = null,
        Action<LiveAtlasIntegrationCompletion>? integrationCompleted = null,
        Action? atlasInsertionEntered = null)
    {
        if (integrationDelay is { } delay && delay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(integrationDelay));

        atlas = new(
            pixelPolicy: pixelPolicy,
            insertionEntered: atlasInsertionEntered);
        cadence = new(presentationFramesPerSecond, atlasFramesPerSecond);
        this.runsIntegrationSynchronously = runsIntegrationSynchronously;
        minimumAtlasSubmissionInterval = 1 / atlasFramesPerSecond;
        this.integrationDelay = integrationDelay ?? TimeSpan.Zero;
        this.integrationStarted = integrationStarted;
        this.integrationCompleted = integrationCompleted;
    }

    public bool IsIntegrating => integrationPump.IsProcessing;

    public Exception? IntegrationFailure
    {
        get { lock (stateGate) return integrationFailure; }
    }

    public long IntegratedObservationCount
    {
        get
        {
            lock (transactionGate)
                return nextObservationId;
        }
    }

    public bool SubmitAtlasObservation(
        BgraFrame frame,
        AtlasPoint cameraPosition,
        double solveWidth,
        double timestamp,
        bool hasGameplaySignal,
        long captureGeneration = 1)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidatePlacement(cameraPosition, solveWidth);
        if (!double.IsFinite(timestamp))
            throw new ArgumentOutOfRangeException(nameof(timestamp));
        if (!hasGameplaySignal) return false;

        IntegrationRequest request;
        lock (stateGate)
        {
            if (resetInProgress) return false;

            presentationAnchor ??= cameraPosition;
            request = new(
                CopyFrame(frame),
                cameraPosition,
                solveWidth,
                timestamp,
                hasGameplaySignal,
                captureGeneration,
                CurrentWorkGeneration);
        }

        return RunIntegration(request);
    }

    /// <summary>
    /// Admits an exact-pose gameplay observation to the bounded atlas worker.
    /// Submission cadence is based on monotonic submission time, while capture
    /// timestamp ordering is enforced by the serialized worker after commit.
    /// </summary>
    public bool SubmitAtlasObservation(
        BgraFrame frame,
        AtlasPoint cameraPosition,
        double solveWidth,
        double timestamp,
        double? observationPoseTimestamp,
        bool hasGameplaySignal,
        double submittedAt,
        bool allowAtlasWrite = true,
        long captureGeneration = 1)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidatePlacement(cameraPosition, solveWidth);
        if (!double.IsFinite(timestamp))
            throw new ArgumentOutOfRangeException(nameof(timestamp));
        if (observationPoseTimestamp is { } poseTimestamp
            && !double.IsFinite(poseTimestamp))
        {
            throw new ArgumentOutOfRangeException(nameof(observationPoseTimestamp));
        }
        if (!double.IsFinite(submittedAt))
            throw new ArgumentOutOfRangeException(nameof(submittedAt));
        if (!hasGameplaySignal
            || !allowAtlasWrite
            || observationPoseTimestamp != timestamp)
        {
            return false;
        }

        IntegrationRequest request;
        lock (stateGate)
        {
            if (resetInProgress) return false;
            if (lastAtlasSubmissionTimestamp is { } previousSubmission
                && submittedAt - previousSubmission
                    + CadenceBoundaryToleranceSeconds
                    < minimumAtlasSubmissionInterval)
            {
                return false;
            }

            lastAtlasSubmissionTimestamp = submittedAt;
            presentationAnchor ??= cameraPosition;
            request = new(
                CopyFrame(frame),
                cameraPosition,
                solveWidth,
                timestamp,
                hasGameplaySignal,
                captureGeneration,
                CurrentWorkGeneration);
        }

        QueueIntegration(request);
        return true;
    }

    public LiveAtlasPresentation Process(
        BgraFrame frame,
        AtlasPoint cameraPosition,
        double solveWidth)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!double.IsFinite(cameraPosition.X)
            || !double.IsFinite(cameraPosition.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(cameraPosition));
        }
        if (!double.IsFinite(solveWidth) || solveWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(solveWidth));

        return Present(frame, cameraPosition, solveWidth, integratedFrame: false);
    }

    /// <summary>
    /// Applies capture-time presentation and atlas cadences without coupling
    /// presentation availability to atlas eligibility. A stale pose or an
    /// explicitly disabled write still produces the current live view.
    /// </summary>
    public LiveAtlasPresentation? Process(
        BgraFrame frame,
        AtlasPoint cameraPosition,
        double solveWidth,
        double timestamp,
        double? observationPoseTimestamp,
        bool hasGameplaySignal,
        bool allowAtlasWrite = true,
        long captureGeneration = 1)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ValidatePlacement(cameraPosition, solveWidth);
        if (!double.IsFinite(timestamp))
            throw new ArgumentOutOfRangeException(nameof(timestamp));
        if (observationPoseTimestamp is { } poseTimestamp
            && !double.IsFinite(poseTimestamp))
        {
            throw new ArgumentOutOfRangeException(nameof(observationPoseTimestamp));
        }

        IntegrationRequest? integration = null;
        var presentWithoutAtlas = false;
        lock (stateGate)
        {
            if (resetInProgress)
            {
                presentWithoutAtlas = true;
            }
            else if (lastLivePresentationTimestamp is { } lastLive
                     && timestamp < lastLive)
            {
                return null;
            }

            if (!presentWithoutAtlas)
            {
                var hasExactPose = observationPoseTimestamp == timestamp;
                var decision = cadence.Decision(
                    timestamp,
                    canIntegrate: hasGameplaySignal
                                  && allowAtlasWrite
                                  && hasExactPose);
                if (!decision.Present) return null;

                lastLivePresentationTimestamp = timestamp;
                if (decision.Integrate)
                {
                    presentationAnchor ??= cameraPosition;
                    integration = new(
                        CopyFrame(frame),
                        cameraPosition,
                        solveWidth,
                        timestamp,
                        hasGameplaySignal,
                        captureGeneration,
                        CurrentWorkGeneration);
                }
            }
        }

        if (presentWithoutAtlas)
            return PresentLiveOnly(frame, cameraPosition);

        var integrated = false;
        if (integration is not null)
        {
            if (runsIntegrationSynchronously)
            {
                integrated = RunIntegration(integration);
            }
            else
            {
                QueueIntegration(integration);
            }
        }

        return Present(frame, cameraPosition, solveWidth, integrated);

    }

    /// <summary>
    /// Invalidates queued and active atlas work before waiting for an in-flight
    /// transaction, then publishes an empty atlas generation.
    /// </summary>
    public void Reset()
    {
        ulong nextGeneration;
        lock (stateGate)
        {
            resetInProgress = true;
            nextGeneration = unchecked((ulong)++workGeneration);
            integrationPump.Invalidate(nextGeneration);
        }

        lock (transactionGate)
        {
            atlas.Reset();
            nextObservationId = 0;
            lock (stateGate)
            {
                admission = new();
                cadence.Reset();
                lastLivePresentationTimestamp = null;
                lastAtlasSubmissionTimestamp = null;
                lastCommittedAtlasTimestamp = null;
                lastCommittedCaptureGeneration = null;
                presentationAnchor = null;
                publishedAtlas = null;
                integrationFailure = null;
                resetInProgress = false;
            }
        }
    }

    private void QueueIntegration(IntegrationRequest request)
    {
        var ticket = integrationPump.Submit(request, request.WorkGeneration);
        if (ticket is null) return;
        if (runsIntegrationSynchronously)
            DrainIntegrationQueue(ticket);
        else
            _ = Task.Run(() => DrainIntegrationQueue(ticket));
    }

    private void DrainIntegrationQueue(
        LatestFramePump<IntegrationRequest>.Ticket first)
    {
        LatestFramePump<IntegrationRequest>.Ticket? ticket = first;
        while (ticket is not null)
        {
            try
            {
                _ = RunIntegration(ticket.Item);
            }
            catch (Exception exception)
            {
                lock (stateGate)
                {
                    if (ticket.Epoch == CurrentWorkGeneration)
                        integrationFailure = exception;
                }
            }

            ticket = integrationPump.Complete(ticket).Next;
        }
    }

    private bool RunIntegration(IntegrationRequest request)
    {
        if (!IsCurrent(request)) return false;
        integrationStarted?.Invoke();
        if (integrationDelay > TimeSpan.Zero)
            Thread.Sleep(integrationDelay);

        lock (transactionGate)
        {
            if (!IsCurrent(request)) return false;
            if (lastCommittedCaptureGeneration == request.CaptureGeneration
                && lastCommittedAtlasTimestamp is { } committedTimestamp
                && request.Timestamp <= committedTimestamp)
            {
                return false;
            }

            var decision = admission.Observe(
                FrameRegionRenderer.SignalProfile(request.Frame),
                request.HasGameplaySignal,
                request.CaptureGeneration);
            if (!decision.AllowsAtlasWrite)
            {
                atlas.EndTemporalWindow();
                return false;
            }

            var observationId = nextObservationId;
            if (!atlas.Insert(
                    observationId,
                    request.Frame,
                    request.SolveWidth,
                    request.CameraPosition,
                    timestamp: request.Timestamp,
                    captureGeneration: request.CaptureGeneration < 0
                        ? 0UL
                        : checked((ulong)request.CaptureGeneration),
                    shouldCancel: () => !IsCurrent(request)))
            {
                return false;
            }

            var snapshot = atlas.Snapshot;
            LiveAtlasIntegrationCompletion completion;
            lock (stateGate)
            {
                if (!IsCurrent(request)) return false;
                nextObservationId = checked(observationId + 1);
                lastCommittedAtlasTimestamp = request.Timestamp;
                lastCommittedCaptureGeneration = request.CaptureGeneration;
                publishedAtlas = snapshot;
                completion = new(
                    request.Timestamp,
                    observationId,
                    CloneSnapshot(snapshot));
            }
            integrationCompleted?.Invoke(completion);
            return true;
        }
    }

    private bool IsCurrent(IntegrationRequest request) =>
        request.WorkGeneration == CurrentWorkGeneration;

    private ulong CurrentWorkGeneration =>
        unchecked((ulong)Volatile.Read(ref workGeneration));

    private LiveAtlasPresentation Present(
        BgraFrame frame,
        AtlasPoint cameraPosition,
        double solveWidth,
        bool integratedFrame)
    {
        var liveImage = CopyFrame(frame);
        AtlasPoint anchor;
        LiveTiledAtlasSnapshot? snapshot;
        lock (stateGate)
        {
            anchor = presentationAnchor ?? cameraPosition;
            snapshot = publishedAtlas is null
                ? null
                : CloneSnapshot(publishedAtlas);
        }
        var origin = WorldPlacement.Offset(
            cameraPosition,
            anchor,
            frame.Width,
            solveWidth,
            gain: 1);
        var liveBounds = new StencilRect(
            origin.X,
            origin.Y,
            frame.Width,
            frame.Height);
        return new(
            liveImage,
            liveBounds,
            new(
                liveBounds.X + liveBounds.Width / 2,
                liveBounds.Y + liveBounds.Height / 2),
            snapshot is null || snapshot.Tiles.Count == 0 ? null : snapshot,
            integratedFrame);
    }

    private static LiveAtlasPresentation PresentLiveOnly(
        BgraFrame frame,
        AtlasPoint cameraPosition)
    {
        var liveImage = CopyFrame(frame);
        var bounds = new StencilRect(0, 0, frame.Width, frame.Height);
        return new(
            liveImage,
            bounds,
            new(bounds.Width / 2, bounds.Height / 2),
            null,
            false);
    }

    private static void ValidatePlacement(
        AtlasPoint cameraPosition,
        double solveWidth)
    {
        if (!double.IsFinite(cameraPosition.X)
            || !double.IsFinite(cameraPosition.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(cameraPosition));
        }
        if (!double.IsFinite(solveWidth) || solveWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(solveWidth));
    }

    private static BgraFrame CopyFrame(BgraFrame frame)
    {
        var stride = checked(frame.Width * BgraFrame.BytesPerPixel);
        var pixels = new byte[checked(stride * frame.Height)];
        for (var y = 0; y < frame.Height; y++)
            frame.Row(y).CopyTo(pixels.AsSpan(checked(y * stride), stride));
        return new(
            frame.Width,
            frame.Height,
            stride,
            pixels,
            frame.CaptureId,
            frame.ObservedAt);
    }

    private static LiveTiledAtlasSnapshot CloneSnapshot(
        LiveTiledAtlasSnapshot snapshot) => new(
        snapshot.Tiles.Select(tile => tile with
        {
            Image = CopyFrame(tile.Image),
            ContributionIds = tile.ContributionIds.ToArray()
        }).ToArray(),
        snapshot.Revision,
        snapshot.TotalBounds,
        snapshot.ContentBounds);
}
