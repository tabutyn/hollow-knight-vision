using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.App;

public partial class MainWindow
{
    [Flags]
    internal enum GroundDiagnosticLayers
    {
        None = 0,
        Edges = 1,
        FilterResponse = 2,
        CandidateRuns = 4,
        CleanFloor = 8,
    }

    internal sealed record GroundDiagnosticImages(BgraFrame? Edges, BgraFrame? FilterResponse,
        BgraFrame? CandidateRuns, BgraFrame? CleanFloor);

    private int groundDiagnosticLayers;
    internal GroundDiagnosticLayers RequestedGroundDiagnostics =>
        (GroundDiagnosticLayers)Volatile.Read(ref groundDiagnosticLayers);

    private void GroundDiagnostics_Changed(object sender, RoutedEventArgs e)
    {
        var layers = GroundDiagnosticLayers.None;
        if (GroundEdgeCheck?.IsChecked == true) layers |= GroundDiagnosticLayers.Edges;
        if (GroundResponseCheck?.IsChecked == true) layers |= GroundDiagnosticLayers.FilterResponse;
        if (GroundCandidatesCheck?.IsChecked == true) layers |= GroundDiagnosticLayers.CandidateRuns;
        if (CleanFloorCheck?.IsChecked == true) layers |= GroundDiagnosticLayers.CleanFloor;
        Volatile.Write(ref groundDiagnosticLayers, (int)layers);
    }

    internal static GroundDiagnosticImages? PrepareGroundDiagnostics(GameplayTerrainAnalysis? terrain, GroundDiagnosticLayers layers)
    {
        if (terrain is null || layers == GroundDiagnosticLayers.None) return null;
        return new(
            layers.HasFlag(GroundDiagnosticLayers.Edges) ? GroundLineDetector.EdgeImage(terrain.Edges) : null,
            layers.HasFlag(GroundDiagnosticLayers.FilterResponse)
                ? GroundLineDetector.GroundDetectImage(terrain.SemanticTheory.Comparison) : null,
            layers.HasFlag(GroundDiagnosticLayers.CandidateRuns)
                ? GroundLineDetector.GroundStageImage(terrain.SemanticTheory) : null,
            layers.HasFlag(GroundDiagnosticLayers.CleanFloor)
                ? GroundLineDetector.CleanedImage(terrain.Floors, terrain.Edges.Width, terrain.Edges.Height) : null);
    }

    private void DrawGroundDiagnostics(GroundDiagnosticImages? images)
    {
        GroundEdgeImage.Source = DiagnosticBitmap(GroundEdgeCheck, images?.Edges);
        GroundResponseImage.Source = DiagnosticBitmap(GroundResponseCheck, images?.FilterResponse);
        GroundCandidatesImage.Source = DiagnosticBitmap(GroundCandidatesCheck, images?.CandidateRuns);
        CleanFloorImage.Source = DiagnosticBitmap(CleanFloorCheck, images?.CleanFloor);
    }

    private static BitmapSource? DiagnosticBitmap(CheckBox toggle, BgraFrame? frame) =>
        toggle.IsChecked == true && frame is not null ? FrameBitmap(frame) : null;
}
