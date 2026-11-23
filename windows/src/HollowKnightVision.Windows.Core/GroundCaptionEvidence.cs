namespace HollowKnightVision.Windows.Core;

/// <summary>Finds groups of thin, bright strokes ending on a common baseline.</summary>
internal sealed class GroundCaptionEvidence(GroundComparisonAnalysis analysis)
{
    private readonly Dictionary<int, IReadOnlyList<PixelSpan>> rangesByBaseline = [];

    internal List<PixelSpan> RemoveText(List<PixelSpan> runs, int row)
    {
        if (analysis.Width < 48 || analysis.SourceLuma is null) return runs;
        var firstBaseline = Math.Max(2, row - 2);
        var lastBaseline = Math.Min(analysis.Height - 4, row + 12);
        for (var baseline = firstBaseline; baseline <= lastBaseline && runs.Count > 0; baseline++)
        {
            if (!rangesByBaseline.TryGetValue(baseline, out var ranges))
            {
                ranges = CaptionRanges(baseline, analysis.SourceLuma);
                rangesByBaseline.Add(baseline, ranges);
            }

            foreach (var mask in ranges)
            {
                var remaining = new List<PixelSpan>();
                foreach (var run in runs)
                {
                    if (run.EndInclusive < mask.Start || mask.EndInclusive < run.Start)
                        remaining.Add(run);
                    else
                    {
                        if (run.Start < mask.Start) remaining.Add(new(run.Start, mask.Start - 1));
                        if (mask.EndInclusive < run.EndInclusive) remaining.Add(new(mask.EndInclusive + 1, run.EndInclusive));
                    }
                }
                runs = remaining;
            }
        }
        return runs;
    }

    private List<PixelSpan> CaptionRanges(int baseline, byte[] luma)
    {
        var width = analysis.Width;
        var valid = analysis.ValidSourcePixels;
        var masks = new List<PixelSpan>();
        var start = 0;
        var previous = -32;
        var strokes = 0;

        bool BrightStroke(int index)
        {
            var center = luma[index];
            if (center < 64) return false;
            var sides = Math.Max(Math.Min(luma[index - 2], luma[index - 3]),
                Math.Min(luma[index + 2], luma[index + 3]));
            return center - sides > 24;
        }

        void FinishGroup()
        {
            if (strokes >= 10 && previous - start >= 32)
            {
                const int pad = GroundLineDetector.ComparisonKernelSpan / 2;
                masks.Add(new(Math.Max(0, start - pad), Math.Min(width - 1, previous + pad)));
            }
        }

        for (var x = 3; x < width - 3; x++)
        {
            var i = baseline * width + x;
            if (valid?[i] == 0 || !BrightStroke(i) || !BrightStroke(i - width) || !BrightStroke(i - 2 * width)
                || BrightStroke(i + 2 * width) || BrightStroke(i + 3 * width)) continue;
            if (x - previous > 16)
            {
                FinishGroup();
                start = x;
                strokes = 1;
            }
            else if (x - previous > 1)
                strokes++;
            previous = x;
        }
        FinishGroup();
        return masks;
    }
}
