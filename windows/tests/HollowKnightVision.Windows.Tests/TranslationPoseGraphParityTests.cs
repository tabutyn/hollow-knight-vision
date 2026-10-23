using HollowKnightVision.Windows.Core;

internal static class TranslationPoseGraphParityTests
{
    private static void Check(bool value, string message = "Translation pose graph parity assertion failed.")
    { if (!value) throw new Exception(message); }

    public static void TestStrongLoopClosureSmoothlyDistributesOutwardReturnDriftInBothCoordinates()
    {
        var nodes = new[]
        {
            Node(0, new(100, 200), new(0, 0)),
            Node(1, new(110, 205), new(10, 5)),
            Node(2, new(120, 210), new(20, 10)),
            Node(3, new(130, 215), new(30, 15))
        };
        var graph = new TranslationPoseGraph(
            nodes,
            [
                Edge(0, 0, 1, new(10, 5), 1, TranslationPoseConstraintKind.Motion),
                Edge(1, 1, 2, new(10, 5), 1, TranslationPoseConstraintKind.Motion),
                Edge(2, 2, 3, new(10, 5), 1, TranslationPoseConstraintKind.Motion),
                Edge(3, 3, 0, new(-24, -12), 80, TranslationPoseConstraintKind.LoopClosure)
            ],
            anchorId: 0,
            baseRevision: 41);
        var solution = graph.Solve();

        CheckPoint(solution.Positions, 0, new(0, 0));
        CheckPoint(solution.Positions, 1, new(8, 4), 0.03);
        CheckPoint(solution.Positions, 2, new(16, 8), 0.03);
        CheckPoint(solution.Positions, 3, new(24, 12), 0.03);
        Check(solution.BaseRevision == 41);
        Check(solution.Revision == 42);
        Check(nodes[2].Raw == new AtlasPoint(120, 210));
    }

    public static void TestAnchorRemainsExactAndConstraintOrderDoesNotChangeSolution()
    {
        var nodes = new[]
        {
            Node(7, new(0, 0), new(3.25, -7.5)),
            Node(3, new(0, 0), new(0, 0)),
            Node(11, new(0, 0), new(0, 0))
        };
        var edges = new[]
        {
            Edge(20, 7, 3, new(4, 1), 2, TranslationPoseConstraintKind.Motion),
            Edge(10, 3, 11, new(-2, 8), 3, TranslationPoseConstraintKind.Motion),
            Edge(30, 11, 7, new(-2, -9), 2, TranslationPoseConstraintKind.LoopClosure)
        };
        var first = new TranslationPoseGraph(nodes, edges, 7).Solve();
        var second = new TranslationPoseGraph(nodes.Reverse().ToArray(), edges.Reverse().ToArray(), 7).Solve();

        CheckPoint(first.Positions, 7, new(3.25, -7.5));
        Check(first.Positions.Keys.Order().SequenceEqual(second.Positions.Keys.Order()));
        foreach (var id in first.Positions.Keys)
        {
            Check(second.Positions.TryGetValue(id, out var point), "Expected a position.");
            CheckPoint(first.Positions[id], point, 0.0000001);
        }
    }

    public static void TestConsistentMotionChainNeedsNoLoopClosure()
    {
        var graph = new TranslationPoseGraph(
            [
                Node(0, new(50, 90), new(5, -3)),
                Node(1, new(54, 92), new(0, 0)),
                Node(2, new(59, 88), new(0, 0))
            ],
            [
                Edge(1, 0, 1, new(4, 2), 1, TranslationPoseConstraintKind.Motion),
                Edge(2, 1, 2, new(5, -4), 1, TranslationPoseConstraintKind.Motion)
            ],
            anchorId: 0);
        var solution = graph.Solve();
        CheckPoint(solution.Positions, 0, new(5, -3));
        CheckPoint(solution.Positions, 1, new(9, -1));
        CheckPoint(solution.Positions, 2, new(14, -5));
    }

    public static void TestInvalidDanglingDisconnectedAndNonFiniteGraphsAreRejected()
    {
        var validNodes = new[]
        {
            Node(0, new(0, 0), new(0, 0)),
            Node(1, new(0, 0), new(0, 0))
        };
        var error = Throws(() => _ = new TranslationPoseGraph(
            [validNodes[0], validNodes[0]], [], 0));
        CheckError(error, TranslationPoseGraphError.DuplicateNodeId, 0);

        error = Throws(() => _ = new TranslationPoseGraph(
            validNodes,
            [
                Edge(1, 0, 1, new(0, 0), 1, TranslationPoseConstraintKind.Motion),
                Edge(1, 1, 0, new(0, 0), 1, TranslationPoseConstraintKind.LoopClosure)
            ],
            0));
        CheckError(error, TranslationPoseGraphError.DuplicateConstraintId, 1);

        error = Throws(() => _ = new TranslationPoseGraph(
            validNodes,
            [Edge(2, 0, 9, new(0, 0), 1, TranslationPoseConstraintKind.Motion)],
            0));
        CheckError(error, TranslationPoseGraphError.DanglingConstraint, 2);

        error = Throws(() => _ = new TranslationPoseGraph(
            validNodes,
            [Edge(3, 0, 1, new(double.PositiveInfinity, 0), 1, TranslationPoseConstraintKind.Motion)],
            0));
        CheckError(error, TranslationPoseGraphError.NonFiniteConstraintDelta, 3);

        error = Throws(() => _ = new TranslationPoseGraph(
            [
                Node(0, new(0, 0), new(0, 0)),
                Node(1, new(double.NaN, 0), new(0, 0))
            ],
            [Edge(31, 0, 1, new(0, 0), 1, TranslationPoseConstraintKind.Motion)],
            0));
        CheckError(error, TranslationPoseGraphError.NonFiniteNodePosition, 1);

        error = Throws(() => _ = new TranslationPoseGraph(
            validNodes,
            [Edge(4, 0, 1, new(0, 0), 0, TranslationPoseConstraintKind.Motion)],
            0));
        CheckError(error, TranslationPoseGraphError.InvalidWeight, 4);

        error = Throws(() => _ = new TranslationPoseGraph(validNodes, [], 0));
        CheckError(error, TranslationPoseGraphError.DisconnectedGraph, null);

        error = Throws(() => _ = new TranslationPoseGraph(
            validNodes,
            [Edge(5, 0, 1, new(0, 0), 1, TranslationPoseConstraintKind.Motion)],
            8));
        CheckError(error, TranslationPoseGraphError.MissingAnchor, 8);
    }

    private static TranslationPoseNode Node(int id, AtlasPoint raw, AtlasPoint initial) =>
        new(id, raw, initial);

    private static TranslationPoseConstraint Edge(
        int id,
        int from,
        int to,
        AtlasPoint delta,
        double weight,
        TranslationPoseConstraintKind kind) =>
        new(id, from, to, delta, weight, kind);

    private static TranslationPoseGraphException Throws(Action action)
    {
        try
        {
            action();
        }
        catch (TranslationPoseGraphException error)
        {
            return error;
        }
        throw new Exception("Expected TranslationPoseGraphException.");
    }

    private static void CheckError(
        TranslationPoseGraphException actual,
        TranslationPoseGraphError expected,
        int? relatedId)
    {
        Check(actual.Error == expected, $"Expected {expected}; got {actual.Error}.");
        Check(actual.RelatedId == relatedId, $"Expected related ID {relatedId}; got {actual.RelatedId}.");
    }

    private static void CheckPoint(
        IReadOnlyDictionary<int, AtlasPoint> positions,
        int id,
        AtlasPoint expected,
        double tolerance = 0.000001)
    {
        Check(positions.TryGetValue(id, out var actual), "Expected a position.");
        CheckPoint(actual, expected, tolerance);
    }

    private static void CheckPoint(
        AtlasPoint actual,
        AtlasPoint expected,
        double tolerance = 0.000001)
    {
        Check(Math.Abs(actual.X - expected.X) <= tolerance,
            $"Expected X {expected.X}; got {actual.X}.");
        Check(Math.Abs(actual.Y - expected.Y) <= tolerance,
            $"Expected Y {expected.Y}; got {actual.Y}.");
    }
}
