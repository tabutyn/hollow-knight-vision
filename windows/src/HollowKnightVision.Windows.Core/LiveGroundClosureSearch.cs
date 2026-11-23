namespace HollowKnightVision.Windows.Core;

/// <summary>Proposes a pose from scenery, then checks it against saved floor texture.</summary>
internal sealed class LiveGroundClosureSearch
{
    private const int MaximumReferences = 32;
    private const int MaximumCachedReferences = 64;
    private const int RecentReferences = 8;

    internal sealed record Source(int KeyframeId, LiveWorldObservation Observation);
    internal sealed record Match(int KeyframeId, AtlasPoint CameraPose, GlobalFeatureReference Reference,
        IReadOnlyList<int> Inliers, double Ambiguity, double ResidualPixels);

    private sealed record Appearance(GlobalFeatureReference Features, CalibratedSceneMotionTracker.Grid? Grid);
    private sealed record Candidate(Source Source, GlobalFeatureReference Features, CalibratedSceneMotionTracker.Grid? Grid);
    private sealed record PoseProposal(Candidate Reference, AtlasPoint Pose, double Confidence, double Residual,
        IReadOnlyList<int> Inliers, double Ambiguity = 0);

    private readonly GlobalFeatureMatcher matcher = new();
    private readonly Dictionary<int, Appearance?> appearances = [];
    private readonly Queue<int> cachedOrder = [];

    internal bool LastSearchWasAmbiguous { get; private set; }

    internal Match? Find(
        IReadOnlyList<Source> sources, BgraFrame current, IReadOnlyList<StencilRect> exclusions,
        IReadOnlyList<LiveGroundLineEvidence> currentGround, double solveWidth,
        Func<int, BgraFrame> loadImage, Func<int, IReadOnlyList<WorldRect>> loadExclusions,
        bool searchAcrossRooms = false)
    {
        LastSearchWasAmbiguous = false;
        var candidates = new List<Candidate>();
        foreach (var source in SelectSources(sources))
        {
            var observation = source.Observation;
            if (!appearances.TryGetValue(source.KeyframeId, out var appearance))
            {
                var image = loadImage(observation.SourceObservationId);
                var omissions = loadExclusions(observation.SourceObservationId)
                    .Select(rect => new StencilRect(rect.X, rect.Y, rect.Width, rect.Height)).ToArray();
                var feature = matcher.MakeReference(image, observation.OptimizedPose, solveWidth,
                    id: new Guid(source.KeyframeId, 0, 0, new byte[8]),
                    excluding: BottomOrigin(omissions, image.Height));
                appearance = feature is null ? null : new Appearance(feature, Grid(image, omissions));
                appearances.Add(source.KeyframeId, appearance);
                cachedOrder.Enqueue(source.KeyframeId);
                if (cachedOrder.Count > MaximumCachedReferences) appearances.Remove(cachedOrder.Dequeue());
            }
            if (appearance is not null)
                candidates.Add(new(source, appearance.Features with { CameraPose = observation.OptimizedPose }, appearance.Grid));
        }
        if (candidates.Count == 0) return null;

        var features = candidates.Select(candidate => candidate.Features).ToArray();
        var masks = BottomOrigin(exclusions, current.Height);
        var scenery = searchAcrossRooms
            ? matcher.MatchAcrossRooms(current, features, solveWidth, masks,
                candidates.ToDictionary(candidate => candidate.Features.Id, candidate => candidate.Source.Observation.RoomId))
            : matcher.Match(current, features, currentSolveWidth: solveWidth, excluding: masks);
        if (matcher.LastMatchWasAmbiguous)
        {
            LastSearchWasAmbiguous = true;
            return null;
        }

        var proposal = scenery is not null
            ? new PoseProposal(candidates.First(candidate => candidate.Features.Id == scenery.ReferenceId),
                scenery.ProposedCameraPose, scenery.Confidence, scenery.Residual, scenery.InlierReferenceFeatureIds,
                scenery.AmbiguityRatio)
            : ProposeFromPatches(candidates, Grid(current, exclusions), solveWidth, searchAcrossRooms);
        if (proposal is null) return null;

        var reference = proposal.Reference.Source;
        var saved = reference.Observation;
        var verification = GroundPlaneClosureVerifier.Verify(loadImage(saved.SourceObservationId),
            saved.GroundGeometry!.Lines, saved.OptimizedPose, current, currentGround, proposal.Pose, solveWidth);
        return verification is null ? null : new Match(reference.KeyframeId, verification.CameraPose,
            proposal.Reference.Features, proposal.Inliers, proposal.Ambiguity,
            proposal.Residual * solveWidth / proposal.Reference.Features.SampleWidth);
    }

    private static IEnumerable<Source> SelectSources(IReadOnlyList<Source> sources)
    {
        if (sources.Count <= MaximumReferences) return sources;
        var olderCount = sources.Count - RecentReferences;
        return Enumerable.Range(0, MaximumReferences - RecentReferences)
            .Select(index => sources[index * olderCount / (MaximumReferences - RecentReferences)])
            .Concat(sources.Skip(olderCount));
    }

    private PoseProposal? ProposeFromPatches(
        IReadOnlyList<Candidate> candidates, CalibratedSceneMotionTracker.Grid? current, double solveWidth,
        bool searchAcrossRooms)
    {
        if (current is null) return null;
        var proposals = new List<PoseProposal>();
        foreach (var candidate in candidates)
        {
            if (candidate.Grid is not { } prior || prior.Width != current.Width || prior.Height != current.Height
                || !matcher.PermitsPatchProposal(candidate.Features)) continue;

            var features = candidate.Features.Features.Take(80).ToArray();
            var fits = new List<(int FeatureId, AtlasPoint Point, CalibratedSceneMotionTracker.PatchFit Fit)>();
            foreach (var feature in features)
            {
                var point = feature.SamplePoint with { Y = prior.Height - 1 - feature.SamplePoint.Y };
                if (CalibratedSceneMotionTracker.FitPatch(prior, current, point) is { } fit)
                    fits.Add((feature.Id, point, fit));
            }
            if (fits.Count < 6) continue;
            var dx = Median(fits.Select(value => value.Fit.Motion.X));
            var dy = Median(fits.Select(value => value.Fit.Motion.Y));
            var agreeing = fits.Where(value => Distance(value.Fit.Motion.X - dx, value.Fit.Motion.Y - dy) <= .65).ToArray();
            if (agreeing.Length < 6 || agreeing.Length < fits.Count * .65
                || agreeing.Max(value => value.Point.X) - agreeing.Min(value => value.Point.X) < 20
                || agreeing.Max(value => value.Point.Y) - agreeing.Min(value => value.Point.Y) < 18) continue;

            dx = Median(agreeing.Select(value => value.Fit.Motion.X));
            dy = Median(agreeing.Select(value => value.Fit.Motion.Y));
            var residual = agreeing.Average(value => Distance(value.Fit.Motion.X - dx, value.Fit.Motion.Y - dy));
            var confidence = agreeing.Length / (double)features.Length * agreeing.Average(value => value.Fit.Correlation)
                * (1 - Math.Min(.9, residual / .65));
            var origin = candidate.Features.CameraPose;
            var scale = solveWidth / current.Width;
            proposals.Add(new(candidate, new AtlasPoint(origin.X - dx * scale, origin.Y + dy * scale),
                confidence, residual, agreeing.Select(value => value.FeatureId).ToArray()));
        }
        var ranked = proposals.OrderByDescending(value => value.Confidence).ThenBy(value => value.Residual)
            .ThenBy(value => value.Reference.Source.KeyframeId).ToArray();
        if (ranked.FirstOrDefault() is not { } best) return null;
        var separate = Math.Max(solveWidth * .025, 4);
        var rival = ranked.Skip(1).FirstOrDefault(value =>
            (searchAcrossRooms && value.Reference.Source.Observation.RoomId != best.Reference.Source.Observation.RoomId)
            || Distance(value.Pose.X - best.Pose.X, value.Pose.Y - best.Pose.Y) > separate);
        var ambiguity = rival is null ? 0 : rival.Confidence / best.Confidence;
        LastSearchWasAmbiguous = ambiguity >= .80;
        return LastSearchWasAmbiguous ? null : best with { Ambiguity = ambiguity };
    }

    private static CalibratedSceneMotionTracker.Grid? Grid(BgraFrame frame, IReadOnlyList<StencilRect> masks) =>
        CalibratedSceneMotionTracker.FromLuma(GroundPixels.Luma(frame)!, frame.Width, frame.Height,
            SceneRegions.PixelRectangles(frame.Width, frame.Height, masks));

    private static StencilRect[] BottomOrigin(IReadOnlyList<StencilRect> masks, int height) =>
        masks.Select(mask => mask with { Y = height - mask.Bottom }).ToArray();

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2;
    }

    private static double Distance(double x, double y) => Math.Sqrt(x * x + y * y);
}
