namespace HollowKnightVision.Windows.Core;

/// <summary>The pixel-only ground extraction shared by capture and Gym.</summary>
public sealed record GameplayTerrainAnalysis(GroundEdgeAnalysis Edges, GroundTheoryAnalysis Theory,
    IReadOnlyList<DetectedFloorLine> Floors, IReadOnlyList<DetectedFloorLine> CameraReferences)
{
    public static GameplayTerrainAnalysis Analyze(BgraFrame frame, GameplaySceneMasks masks,
        GroundTheoryTuning? tuning = null, GroundTheoryTuning? semanticTuning = null)
    {
        var edges = GroundLineDetector.Analyze(frame, masks.GroundExclusions);
        var comparison = GroundLineDetector.Compare(edges);
        var captions = new GroundCaptionEvidence(comparison);
        var theory = GroundLineDetector.TrackingTheory(comparison, tuning ?? GroundTheoryTuning.Default, captions);
        var floors = GroundLineDetector.RejectKnownForegroundLines(
            GroundLineDetector.SemanticFloorLines(comparison, semanticTuning ?? GroundTheoryTuning.SemanticDefault, captions),
            frame.Height, masks.GroundExclusions, masks.GroundKnight);
        var references = GroundLineDetector.CameraReferenceFloors(
            GroundLineDetector.CleanFloorLines(theory), floors, frame.Height, masks.GroundKnight, masks.GroundExclusions);
        return new(edges, theory, floors, references);
    }
}
