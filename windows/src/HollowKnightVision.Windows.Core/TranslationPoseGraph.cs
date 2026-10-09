namespace HollowKnightVision.Windows.Core;

public readonly record struct TranslationPoseNode(
    int Id,
    AtlasPoint Raw,
    AtlasPoint Initial);

public enum TranslationPoseConstraintKind
{
    Motion,
    LoopClosure
}

public readonly record struct TranslationPoseConstraint(
    int Id,
    int From,
    int To,
    AtlasPoint MeasuredDelta,
    double Weight,
    TranslationPoseConstraintKind Kind);

public sealed record TranslationPoseSolution(
    int Revision,
    int BaseRevision,
    IReadOnlyDictionary<int, AtlasPoint> Positions);

public enum TranslationPoseGraphError
{
    DuplicateNodeId,
    DuplicateConstraintId,
    NonFiniteNodePosition,
    NonFiniteConstraintDelta,
    InvalidWeight,
    MissingAnchor,
    DanglingConstraint,
    InvalidConstraint,
    DisconnectedGraph,
    InvalidBaseRevision,
    NumericalFailure
}

public sealed class TranslationPoseGraphException(
    TranslationPoseGraphError error,
    string message,
    int? relatedId = null) : Exception(message)
{
    public TranslationPoseGraphError Error { get; } = error;
    public int? RelatedId { get; } = relatedId;
}

/// <summary>
/// Deterministic weighted least-squares optimizer matching the macOS
/// translation-only graph. X/Y solve independently and the anchor is exact.
/// </summary>
public sealed class TranslationPoseGraph
{
    public TranslationPoseGraph(
        IReadOnlyList<TranslationPoseNode> nodes,
        IReadOnlyList<TranslationPoseConstraint> constraints,
        int anchorId,
        int baseRevision = 0)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(constraints);
        Nodes = nodes.ToArray();
        Constraints = constraints.ToArray();
        AnchorId = anchorId;
        BaseRevision = baseRevision;
        Validate();
    }

    public IReadOnlyList<TranslationPoseNode> Nodes { get; }
    public IReadOnlyList<TranslationPoseConstraint> Constraints { get; }
    public int AnchorId { get; }
    public int BaseRevision { get; }

    public TranslationPoseSolution Solve()
    {
        var orderedNodes = Nodes.OrderBy(node => node.Id).ToArray();
        var orderedConstraints = Constraints.OrderBy(constraint => constraint.Id).ToArray();
        var anchor = orderedNodes.Single(node => node.Id == AnchorId);
        var unknownIds = orderedNodes.Select(node => node.Id)
            .Where(id => id != AnchorId).ToArray();
        var unknownIndex = unknownIds.Select((id, index) => (id, index))
            .ToDictionary(item => item.id, item => item.index);
        var maximumWeight = orderedConstraints.Select(value => value.Weight)
            .DefaultIfEmpty(1).Max();
        var x = SolveCoordinate(
            unknownIndex,
            orderedConstraints,
            anchor.Initial.X,
            point => point.X,
            maximumWeight);
        var y = SolveCoordinate(
            unknownIndex,
            orderedConstraints,
            anchor.Initial.Y,
            point => point.Y,
            maximumWeight);
        var positions = new Dictionary<int, AtlasPoint>(orderedNodes.Length)
        {
            [AnchorId] = anchor.Initial
        };
        foreach (var id in unknownIds)
        {
            var index = unknownIndex[id];
            if (!double.IsFinite(x[index]) || !double.IsFinite(y[index]))
            {
                throw Error(TranslationPoseGraphError.NumericalFailure,
                    "Pose solution is non-finite.");
            }
            positions[id] = new AtlasPoint(x[index], y[index]);
        }
        return new TranslationPoseSolution(
            checked(BaseRevision + 1),
            BaseRevision,
            positions);
    }

    private void Validate()
    {
        if (BaseRevision < 0 || BaseRevision == int.MaxValue)
        {
            throw Error(TranslationPoseGraphError.InvalidBaseRevision,
                "Base revision is invalid.");
        }
        var nodeIds = new HashSet<int>();
        foreach (var node in Nodes)
        {
            if (!nodeIds.Add(node.Id))
            {
                throw Error(TranslationPoseGraphError.DuplicateNodeId,
                    $"Duplicate node {node.Id}.", node.Id);
            }
            if (!Finite(node.Raw) || !Finite(node.Initial))
            {
                throw Error(TranslationPoseGraphError.NonFiniteNodePosition,
                    $"Node {node.Id} has a non-finite pose.", node.Id);
            }
        }
        if (!nodeIds.Contains(AnchorId))
        {
            throw Error(TranslationPoseGraphError.MissingAnchor,
                $"Anchor {AnchorId} is missing.", AnchorId);
        }

        var constraintIds = new HashSet<int>();
        foreach (var constraint in Constraints)
        {
            if (!constraintIds.Add(constraint.Id))
            {
                throw Error(TranslationPoseGraphError.DuplicateConstraintId,
                    $"Duplicate constraint {constraint.Id}.", constraint.Id);
            }
            if (!Finite(constraint.MeasuredDelta))
            {
                throw Error(TranslationPoseGraphError.NonFiniteConstraintDelta,
                    $"Constraint {constraint.Id} has a non-finite delta.", constraint.Id);
            }
            if (!double.IsFinite(constraint.Weight) || constraint.Weight <= 0)
            {
                throw Error(TranslationPoseGraphError.InvalidWeight,
                    $"Constraint {constraint.Id} has invalid weight.", constraint.Id);
            }
            if (constraint.From == constraint.To)
            {
                throw Error(TranslationPoseGraphError.InvalidConstraint,
                    $"Constraint {constraint.Id} is self-referential.", constraint.Id);
            }
            if (!nodeIds.Contains(constraint.From) || !nodeIds.Contains(constraint.To))
            {
                throw Error(TranslationPoseGraphError.DanglingConstraint,
                    $"Constraint {constraint.Id} references a missing node.", constraint.Id);
            }
        }
        if (!Connected(nodeIds))
        {
            throw Error(TranslationPoseGraphError.DisconnectedGraph,
                "Pose graph is disconnected.");
        }
    }

    private bool Connected(IReadOnlySet<int> nodeIds)
    {
        var neighbors = nodeIds.ToDictionary(id => id, _ => new List<int>());
        foreach (var constraint in Constraints)
        {
            neighbors[constraint.From].Add(constraint.To);
            neighbors[constraint.To].Add(constraint.From);
        }
        var visited = new HashSet<int> { AnchorId };
        var pending = new Stack<int>();
        pending.Push(AnchorId);
        while (pending.TryPop(out var node))
        {
            foreach (var neighbor in neighbors[node])
            {
                if (visited.Add(neighbor)) pending.Push(neighbor);
            }
        }
        return visited.SetEquals(nodeIds);
    }

    private static double[] SolveCoordinate(
        IReadOnlyDictionary<int, int> unknownIndex,
        IReadOnlyList<TranslationPoseConstraint> constraints,
        double anchorPosition,
        Func<AtlasPoint, double> coordinate,
        double maximumWeight)
    {
        var count = unknownIndex.Count;
        if (count == 0) return [];
        var normal = new double[count, count];
        var rhs = new double[count];
        foreach (var constraint in constraints)
        {
            var weight = constraint.Weight / maximumWeight;
            var measured = coordinate(constraint.MeasuredDelta);
            var hasFrom = unknownIndex.TryGetValue(constraint.From, out var fromIndex);
            var hasTo = unknownIndex.TryGetValue(constraint.To, out var toIndex);
            if (hasFrom)
            {
                normal[fromIndex, fromIndex] += weight;
                rhs[fromIndex] -= weight * measured;
            }
            if (hasTo)
            {
                normal[toIndex, toIndex] += weight;
                rhs[toIndex] += weight * measured;
            }
            if (hasFrom && hasTo)
            {
                normal[fromIndex, toIndex] -= weight;
                normal[toIndex, fromIndex] -= weight;
            }
            if (!hasFrom && hasTo) rhs[toIndex] += weight * anchorPosition;
            if (!hasTo && hasFrom) rhs[fromIndex] += weight * anchorPosition;
        }
        return SolveDenseSystem(normal, rhs);
    }

    private static double[] SolveDenseSystem(double[,] matrix, double[] vector)
    {
        var count = vector.Length;
        var scale = 0.0;
        for (var row = 0; row < count; row++)
        {
            for (var column = 0; column < count; column++)
            {
                scale = Math.Max(scale, Math.Abs(matrix[row, column]));
            }
        }
        var tolerance = Math.Max(1e-14, scale * 1e-12);
        for (var column = 0; column < count; column++)
        {
            var pivot = column;
            for (var row = column + 1; row < count; row++)
            {
                if (Math.Abs(matrix[row, column]) > Math.Abs(matrix[pivot, column]))
                {
                    pivot = row;
                }
            }
            if (!double.IsFinite(matrix[pivot, column])
                || Math.Abs(matrix[pivot, column]) <= tolerance)
            {
                throw Error(TranslationPoseGraphError.NumericalFailure,
                    "Pose graph normal matrix is singular.");
            }
            if (pivot != column)
            {
                for (var index = 0; index < count; index++)
                {
                    (matrix[pivot, index], matrix[column, index]) =
                        (matrix[column, index], matrix[pivot, index]);
                }
                (vector[pivot], vector[column]) = (vector[column], vector[pivot]);
            }
            var pivotValue = matrix[column, column];
            for (var row = column + 1; row < count; row++)
            {
                var factor = matrix[row, column] / pivotValue;
                if (!double.IsFinite(factor))
                {
                    throw Error(TranslationPoseGraphError.NumericalFailure,
                        "Pose graph elimination failed.");
                }
                matrix[row, column] = 0;
                for (var index = column + 1; index < count; index++)
                {
                    matrix[row, index] -= factor * matrix[column, index];
                }
                vector[row] -= factor * vector[column];
            }
        }

        var result = new double[count];
        for (var row = count - 1; row >= 0; row--)
        {
            var value = vector[row];
            for (var column = row + 1; column < count; column++)
            {
                value -= matrix[row, column] * result[column];
            }
            result[row] = value / matrix[row, row];
            if (!double.IsFinite(result[row]))
            {
                throw Error(TranslationPoseGraphError.NumericalFailure,
                    "Pose graph back-substitution failed.");
            }
        }
        return result;
    }

    private static bool Finite(AtlasPoint point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y);

    private static TranslationPoseGraphException Error(
        TranslationPoseGraphError error,
        string message,
        int? relatedId = null) => new(error, message, relatedId);
}

/// <summary>Optimizes every connected room component and advances one snapshot revision.</summary>
public static class LiveWorldPoseOptimizer
{
    public static LiveWorldSnapshot Optimize(LiveWorldSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Validate();
        if (snapshot.Observations.Count == 0) return snapshot;

        var positions = snapshot.Observations.ToDictionary(
            observation => observation.Id,
            observation => observation.OptimizedPose);
        var observationById = snapshot.Observations.ToDictionary(value => value.Id);
        var keyframeById = snapshot.Keyframes.ToDictionary(value => value.Id);
        var constraints = new List<TranslationPoseConstraint>();
        foreach (var edge in snapshot.RelativeMotionEdges.OrderBy(value => value.Id))
        {
            constraints.Add(new TranslationPoseConstraint(
                constraints.Count,
                edge.FromObservationId,
                edge.ToObservationId,
                new AtlasPoint(edge.DeltaX, edge.DeltaY),
                edge.Support,
                TranslationPoseConstraintKind.Motion));
        }
        foreach (var edge in snapshot.LoopClosureEdges.OrderBy(value => value.Id))
        {
            var from = keyframeById[edge.FromKeyframeId].ObservationId;
            var to = keyframeById[edge.ToKeyframeId].ObservationId;
            constraints.Add(new TranslationPoseConstraint(
                constraints.Count,
                from,
                to,
                new AtlasPoint(edge.DeltaX, edge.DeltaY),
                Math.Max(1, edge.Support * 8.0),
                TranslationPoseConstraintKind.LoopClosure));
        }
        if (constraints.Count == 0) return snapshot;

        foreach (var room in snapshot.Observations.GroupBy(value => value.RoomId))
        {
            var roomIds = room.Select(value => value.Id).ToHashSet();
            var roomConstraints = constraints.Where(value =>
                roomIds.Contains(value.From) && roomIds.Contains(value.To)).ToArray();
            var neighbors = roomIds.ToDictionary(id => id, _ => new List<int>());
            foreach (var constraint in roomConstraints)
            {
                neighbors[constraint.From].Add(constraint.To);
                neighbors[constraint.To].Add(constraint.From);
            }
            var unseen = roomIds.ToHashSet();
            while (unseen.Count > 0)
            {
                var anchor = unseen.Min();
                var component = new HashSet<int> { anchor };
                var pending = new Stack<int>();
                pending.Push(anchor);
                unseen.Remove(anchor);
                while (pending.TryPop(out var current))
                {
                    foreach (var adjacent in neighbors[current])
                    {
                        if (component.Add(adjacent))
                        {
                            unseen.Remove(adjacent);
                            pending.Push(adjacent);
                        }
                    }
                }
                if (component.Count == 1) continue;
                var componentConstraints = roomConstraints.Where(value =>
                    component.Contains(value.From) && component.Contains(value.To)).ToArray();
                var nodes = component.OrderBy(id => id).Select(id =>
                {
                    var observation = observationById[id];
                    return new TranslationPoseNode(
                        id,
                        observation.RawPose,
                        observation.OptimizedPose);
                }).ToArray();
                var solution = new TranslationPoseGraph(
                    nodes,
                    componentConstraints,
                    anchor,
                    snapshot.MapRevision).Solve();
                foreach (var pair in solution.Positions) positions[pair.Key] = pair.Value;
            }
        }
        return snapshot.ReplaceOptimizedPoses(positions, snapshot.MapRevision);
    }
}
