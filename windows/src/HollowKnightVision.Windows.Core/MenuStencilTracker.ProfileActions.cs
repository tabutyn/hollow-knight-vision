using static HollowKnightVision.Windows.Core.MenuStencilCatalog;

namespace HollowKnightVision.Windows.Core;

public sealed partial class MenuStencilTracker
{
    private IReadOnlyList<MenuStencilMatch> WithRepeatedProfileActions(Scene scene, ImageStencilPixels pixels,
        BgraFrame image, string? language, IReadOnlyList<MenuStencilMatch> matches)
    {
        const string identifier = "select-profile.clear-save";
        if (scene.ContextIdentifier != "select-profile"
            || scene.Anchors.FirstOrDefault(anchor => anchor.ClassIdentifier == identifier) is not { } reference) return matches;
        var result = matches.Where(match => match.ClassIdentifier != identifier).ToList();
        foreach (var option in scene.Options.Where(option => option.ClassIdentifier.StartsWith(identifier + "-", StringComparison.Ordinal)))
        {
            // Row positions are fractional annotation coordinates; kernels use
            // outward-rounded pixel crops. Translate the crop without shrinking it.
            var expected = reference.Bounds.Offset(option.Bounds.MidX - reference.Bounds.MidX + trackedOffset.X,
                option.Bounds.MidY - reference.Bounds.MidY + trackedOffset.Y);
            var score = BestScore(VariantsFor(reference.Variants, language), pixels, expected, 4, 6, reference.Bounds);
            // Empty slots have no Clear Save action. Require pixels at each row,
            // rather than displaying four copies of the annotated reference.
            if (score is null || score.Confidence < AnchorThreshold) continue;
            result.Add(new(identifier, option.Name, OutputRect(score.Rect, image, scene), score.Confidence, score.Reference));
        }
        return result;
    }
}
