using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class RoomReturnReplayAuditTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private sealed record GridFrame(double CaptureTimestamp, int Width, int Height, byte[]? Luma);
    private sealed record Source(Guid Id, GridFrame[] LowResolutionFrames, RecordedVisualFrameArchive? VisualFrameArchive);
    private sealed record ImageView(double Seconds, LowResolutionMotionGrid Grid, string Role);
    private sealed record ImageMatch(int Room, double Similarity, double ReferenceSeconds, string Role);
    private sealed record ImageDecision(double ArrivalSeconds, double DepartureSeconds, int SourceRoom,
        int ChosenRoom, bool ReusedRoom, double? Margin, ImageMatch[] Ranked, double DecisionSeconds);
    private sealed record FeatureView(GlobalFeatureReference Reference, double Seconds);
    private sealed record FeatureMatch(int Room, double ReferenceSeconds, double Confidence, int Inliers,
        double Residual, double Ambiguity, AtlasPoint Translation);
    private sealed record FeatureQuery(double Seconds, FeatureMatch[] Ranked);
    private sealed record FeatureDecision(double ArrivalSeconds, double DecisionSeconds, int SourceRoom, FeatureQuery[] Queries);

    internal static void TestRecordedRoomReturnImagesWhenRequested()
    {
        var input = Path.GetFullPath(Setting("HKV_ROOM_VIEW_AUDIT_INPUT"));
        var source = ReadSource(input);
        var cuts = ReadCuts(Setting("HKV_ROOM_VIEW_AUDIT_CUTS"));
        var origin = source.LowResolutionFrames[0].CaptureTimestamp;
        var frames = source.LowResolutionFrames.Select(frame =>
        {
            if (frame.Width < 24 || frame.Height < 18 || frame.Luma is null)
                throw new InvalidDataException("The room image audit needs recorded luma grids of at least 24 × 18.");
            return new ImageView(frame.CaptureTimestamp - origin, new(frame.Width, frame.Height, frame.Luma), "stable visit view");
        }).ToArray();
        var stable = Environment.GetEnvironmentVariable("HKV_ROOM_VIEW_AUDIT_STABLE_VIEWS") == "1";
        var decisions = ReplayImages(frames, cuts, stable);
        WriteReport("HKV_ROOM_VIEW_AUDIT_OUTPUT", decisions);
        Console.WriteLine($"Room image audit: {decisions.Length} cuts, {decisions.Count(decision => decision.ReusedRoom)} possible revisits.");
    }

    internal static void TestRoomImageAuditConfirmsTwoSeparatedViews()
    {
        var first = MakeGrid(1);
        var second = MakeGrid(2);
        var unrelated = MakeGrid(3);
        var cuts = new LoadingGapReplayAuditTests.MeasuredCut[] { new(1, 2, 1), new(4, 5, -1) };
        bool Revisits(LowResolutionMotionGrid? last)
        {
            ImageView[] views = [new(0, first, ""), new(1, first, ""), new(2, second, ""),
                new(2.5, second, ""), new(3, second, ""), new(4, second, ""), new(5, first, ""),
                new(5.5, first, "")];
            if (last is not null) views = [.. views, new(6, last, "")];
            var decisions = ReplayImages(views, cuts, stable: true);
            if (decisions[0].ReusedRoom || decisions[0].ChosenRoom != 1
                || decisions[1].SourceRoom != 1 || decisions[1].DecisionSeconds != views[^1].Seconds)
                throw new InvalidOperationException("The replay must retain the completed visit and wait for both query images.");
            return decisions[1] is { ReusedRoom: true, ChosenRoom: 0 };
        }
        if (!Revisits(first) || Revisits(unrelated) || Revisits(null))
            throw new InvalidOperationException("Both separated query images must recognize the same earlier room.");

        static LowResolutionMotionGrid MakeGrid(int seed)
        {
            var luma = new byte[64 * 36];
            new Random(seed).NextBytes(luma);
            return new(64, 36, luma);
        }
    }

    internal static void TestRecordedRoomReturnFeaturesWhenRequested()
    {
        var input = Path.GetFullPath(Setting("HKV_ROOM_FEATURE_AUDIT_INPUT"));
        var source = ReadSource(input);
        var cuts = ReadCuts(Setting("HKV_ROOM_FEATURE_AUDIT_CUTS"));
        if (source.VisualFrameArchive is not { } archive)
            throw new InvalidDataException("The room feature audit requires archived images.");
        var reader = new VisualReplayArchiveReader(Path.GetDirectoryName(input)!, archive, source.Id, requireComplete: false);
        var frames = reader.Manifest.Frames.Where(frame => frame.FileName is not null).ToArray();
        if (frames.Length == 0) throw new InvalidDataException("The archive has no readable image entries.");
        var origin = source.LowResolutionFrames[0].CaptureTimestamp;
        var masks = reader.Manifest.GroundInputs.ToLookup(input => (input.CaptureGeneration, input.CaptureTimestamp));
        var matcher = new GlobalFeatureMatcher();
        var bank = new List<FeatureView[]>();
        var results = new List<FeatureDecision>();
        var enteredAt = 0d;
        foreach (var cut in cuts)
        {
            var room = bank.Count;
            var visit = frames.Where(frame => frame.CaptureTimestamp - origin >= enteredAt + .5
                && frame.CaptureTimestamp - origin <= cut.DepartureSeconds).ToArray();
            var count = Math.Min(32, visit.Length);
            var views = new List<FeatureView>();
            for (var index = 0; index < count; index++)
            {
                var frame = visit[index * (visit.Length - 1) / Math.Max(1, count - 1)];
                var image = reader.ReadImage(frame);
                if (matcher.MakeReference(image, default, frame.SolveWidth, excluding: Masks(frame, image)) is { } reference)
                    views.Add(new(reference, frame.CaptureTimestamp - origin));
            }
            var queries = new[] { Query(cut.ArrivalSeconds + .5), Query(cut.ArrivalSeconds + 1) };
            results.Add(new(cut.ArrivalSeconds, queries.Max(query => query.Seconds), room, queries));
            bank.Add(views.ToArray());
            enteredAt = cut.ArrivalSeconds;
        }
        WriteReport("HKV_ROOM_FEATURE_AUDIT_OUTPUT", results);
        Console.WriteLine($"Room feature audit: {results.Count} cuts, "
            + $"{results.Sum(decision => decision.Queries.Count(query => query.Ranked.Length > 0))} queries with earlier-room matches.");

        FeatureQuery Query(double requested)
        {
            var frame = frames.MinBy(frame => Math.Abs(frame.CaptureTimestamp - origin - requested))!;
            var image = reader.ReadImage(frame);
            var exclusions = Masks(frame, image);
            var ranked = new List<FeatureMatch>();
            for (var room = 0; room < bank.Count; room++)
            {
                FeatureMatch? best = null;
                foreach (var view in bank[room])
                {
                    var match = matcher.Match(image, [view.Reference], currentSolveWidth: frame.SolveWidth, excluding: exclusions);
                    if (match is not null && (best is null || match.Confidence > best.Confidence))
                        best = new(room, view.Seconds, match.Confidence, match.InlierReferenceFeatureIds.Count,
                            match.Residual, match.AmbiguityRatio, match.MeasuredTranslation);
                }
                if (best is not null) ranked.Add(best);
            }
            return new(frame.CaptureTimestamp - origin, ranked.OrderByDescending(match => match.Confidence)
                .ThenBy(match => match.Room).Take(5).ToArray());
        }

        StencilRect[] Masks(VisualReplayFrame frame, BgraFrame image)
        {
            var ownedInput = masks[(frame.CaptureGeneration, frame.CaptureTimestamp)].LastOrDefault();
            var regions = VisualReplayGroundInput.RestoreMasks(ownedInput, image);
            return [new(0, 0, image.Width, image.Height * .24), .. regions.GroundRegions.OmittedRects];
        }
    }

    private static ImageDecision[] ReplayImages(ImageView[] frames, LoadingGapReplayAuditTests.MeasuredCut[] cuts, bool stable)
    {
        var bank = new Dictionary<int, List<ImageView>> { [0] = [frames[0] with { Role = "start" }] };
        var current = 0;
        var next = 1;
        var enteredAt = 0d;
        var decisions = new List<ImageDecision>();
        foreach (var cut in cuts)
        {
            var visit = bank[current];
            if (stable)
            {
                var last = double.NegativeInfinity;
                foreach (var frame in frames)
                {
                    if (frame.Seconds < enteredAt || frame.Seconds > cut.DepartureSeconds || frame.Seconds - last < .28
                        || !VisualSceneDiscontinuityTracker.HasRecoveredImage(frame.Grid))
                        continue;
                    visit.Add(frame);
                    last = frame.Seconds;
                    if (visit.Count > 32) visit.RemoveAt(1);
                }
            }
            visit.Add(Frame(cut.DepartureSeconds) with { Role = "departure" });
            var arrival = Frame(cut.ArrivalSeconds);
            var queries = stable ? new[] { Frame(cut.ArrivalSeconds + .5, exact: false),
                Frame(cut.ArrivalSeconds + 1, exact: false) } : [arrival];
            var ranked = new List<ImageMatch>();
            var independentQueries = queries.Length == 1 || queries[0].Seconds < queries[1].Seconds;
            foreach (var (room, views) in bank)
            {
                if (!independentQueries || room == current) continue;
                var scores = queries.Select(query => views.Select(view =>
                    (View: view, Score: VisualSceneDiscontinuityTracker.HasRecoveredImage(view.Grid)
                        ? VisualSceneDiscontinuityTracker.Similarity(view.Grid, query.Grid) : null))
                    .Where(candidate => candidate.Score.HasValue).DefaultIfEmpty().MaxBy(candidate => candidate.Score)).ToArray();
                if (scores.Any(score => score.Score is null)) continue;
                var (view, score) = scores.MinBy(candidate => candidate.Score);
                ranked.Add(new(room, score!.Value, view.Seconds, view.Role));
            }
            var best = ranked.OrderByDescending(match => match.Similarity).ThenBy(match => match.Room).Take(5).ToArray();
            double? margin = best.Length >= 2 ? best[0].Similarity - best[1].Similarity : null;
            var reuse = best.FirstOrDefault() is { Similarity: >= .90 } && (margin ?? 1) >= .10;
            var chosen = reuse ? best[0].Room : next++;
            decisions.Add(new(cut.ArrivalSeconds, cut.DepartureSeconds, current, chosen, reuse, margin, best,
                queries.Max(query => query.Seconds)));
            current = chosen;
            enteredAt = cut.ArrivalSeconds;
            if (!bank.TryGetValue(chosen, out var target)) bank.Add(chosen, target = []);
            target.Add(arrival with { Role = "arrival" });
        }
        return decisions.ToArray();

        ImageView Frame(double seconds, bool exact = true)
        {
            var frame = frames.MinBy(frame => Math.Abs(frame.Seconds - seconds))!;
            if (exact && Math.Abs(frame.Seconds - seconds) >= .000001)
                throw new InvalidDataException($"The supplied cut has no exact luma frame at {seconds} seconds.");
            return frame;
        }
    }

    private static Source ReadSource(string path)
    {
        using var stream = File.OpenRead(path);
        var source = JsonSerializer.Deserialize<Source>(stream, Json);
        return source?.LowResolutionFrames is { Length: > 0 } ? source
            : throw new InvalidDataException("The archive must contain the original luma frame clock.");
    }

    private static LoadingGapReplayAuditTests.MeasuredCut[] ReadCuts(string path)
    {
        var cuts = JsonSerializer.Deserialize<LoadingGapReplayAuditTests.MeasuredCut[]>(File.ReadAllBytes(path), Json);
        if (cuts is not { Length: > 0 }) throw new InvalidDataException("Supply at least one native room cut.");
        var previous = 0d;
        foreach (var cut in cuts)
        {
            if (!double.IsFinite(cut.DepartureSeconds) || !double.IsFinite(cut.ArrivalSeconds)
                || cut.DepartureSeconds < previous || cut.ArrivalSeconds <= cut.DepartureSeconds)
                throw new InvalidDataException("Cuts must have chronological finite departure and arrival times.");
            previous = cut.ArrivalSeconds;
        }
        return cuts;
    }

    private static string Setting(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Set {name} to run the recorded room-return audit.");

    private static void WriteReport<T>(string name, T report) =>
        AtomicFile.Write(Setting(name), JsonSerializer.SerializeToUtf8Bytes(report, Json), overwrite: false);
}
