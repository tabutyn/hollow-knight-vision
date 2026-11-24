using HollowKnightVision.Windows.Core;

internal static class GroundPatchConsensusParityTests
{
    private const int Width = 320, Height = 180;
    private static readonly int[] Rows = [30, 85, 130], Columns = [24, 72, 128, 184, 240];

    public static void TestGroundMajorityRejectsForegroundCompromise()
    {
        var source = Frame();
        var target = Frame(36, 6);
        var patches = Patches(source);
        InsertForeground(target, patches.Take(5));
        var fit = GroundPatchConsensus.Refine(patches, target, Width, Height, 38, 6, []);
        Check(fit is { Dx: 36, Dy: 6, Error: < .001 } && fit.Patches.Length >= 8,
            "The independent majority should recover the floor's motion.");

        var solver = new GroundCameraSolver(keyframeSubpixelEnabled: true, broadSearchEveryFrame: true);
        var before = Rows.Select(row => new GroundTextureLine(row, 16, 303)).ToArray();
        var after = Rows.Select(row => new GroundTextureLine(row + 6, 52, 303)).ToArray();
        _ = solver.Solve(source, Width, Height, before, 0, []);
        var result = solver.Solve(target, Width, Height, after, .1, []);
        Check(result is { IndependentTextureConsensus: true }
            && Math.Abs(result.ImageTranslation.X - 36) < .2 && Math.Abs(result.ImageTranslation.Y - 6) < .2,
            "The production solver should follow the floor and publish independently verified evidence.");
    }

    public static void TestTieDarknessMasksAndPeriodicTextureAreRejected()
    {
        var patches = Patches(Frame()).Take(14).ToArray();
        var tied = Frame(36, 6);
        InsertForeground(tied, patches.Take(7));
        Check(GroundPatchConsensus.Refine(patches, tied, Width, Height, 38, 6, []) is null,
            "An even foreground/floor split should not verify either displacement.");
        Check(GroundPatchConsensus.Refine(patches, new byte[Width * Height], Width, Height, 38, 6, []) is null,
            "Flat darkness cannot verify texture.");
        Check(GroundPatchConsensus.Refine(patches, Frame(36, 6), Width, Height, 38, 6,
            [new(0, 0, Width, Height)]) is null, "Masked pixels cannot verify texture.");
        var periodic = Enumerable.Range(0, Width * Height).Select(i => (byte)(i % Width % 4 < 2 ? 60 : 180)).ToArray();
        Check(GroundPatchConsensus.Refine(Patches(periodic), periodic, Width, Height, 0, 0, []) is null,
            "Periodic texture must not make a shifted match look unique.");
    }

    public static void TestMedianRevalidatesEveryPatchBeforePublishingQuality()
    {
        var source = Frame();
        var target = Frame(1);
        var x = Columns[0];
        var y = Rows[0];
        byte[] edge = [35, 201, 81, 171, 15, 230, 45, 153, 72, 210, 95, 126];
        for (var row = 0; row < 12; row++)
        {
            source.AsSpan((y + row) * Width + x, 16).Clear();
            source[(y + row) * Width + x] = edge[row];
        }
        for (var row = y - 4; row < y + 16; row++)
            target.AsSpan(row * Width + x - 4, 24).Clear();
        for (var row = 0; row < 12; row++)
            target[(y + row) * Width + x] = edge[row];

        var patches = Patches(source);
        var fit = GroundPatchConsensus.Refine(patches, target, Width, Height, 0, 0, []);
        Check(fit is { Dx: 1, Dy: 0, Error: < .001 } && fit.Patches.Length == 14
            && fit.Patches.All(patch => patch.X != x || patch.Y != y),
            "The stationary edge matches independently but must lose its vote at the shared displacement.");
        Check(GroundPatchConsensus.Refine(patches.Take(4).ToArray(), target, Width, Height, 0, 0, []) is null,
            "A group must still have enough support after revalidation.");
    }

    public static void TestRejectsInsufficientSpatialEvidenceAndInvalidBounds()
    {
        var source = Frame();
        var patches = Patches(source);
        GroundPatchConsensus.Result? Fit(IReadOnlyList<GroundCameraSolver.Patch> selected, int dx = 0) =>
            GroundPatchConsensus.Refine(selected, source, Width, Height, dx, 0, []);

        Check(Fit(patches.Take(3).ToArray()) is null, "Require at least four independent patches.");
        var repeated = patches.Take(3).Concat(Enumerable.Repeat(patches[0], 5)).ToArray();
        Check(Fit(repeated) is null, "Duplicate coordinates cannot vote twice.");
        Check(Fit(Enumerable.Repeat(patches[0], 33).ToArray()) is null, "The reference population is bounded.");
        Check(Fit(patches, int.MaxValue) is null && Fit(patches, int.MinValue) is null, "Displacement must fit the frame.");
        var close = new[] { 24, 28, 32, 36 }.Select(x => Patch(source, x, 30)).ToArray();
        Check(Fit(close) is null, "Nearby patches cannot verify the whole view.");
        Check(GroundPatchConsensus.Refine(patches, [], int.MaxValue, Height, 0, 0, []) is null,
            "Invalid frame dimensions must be rejected before indexing.");
        Check(Fit([new(-1, 30, patches[0].Pixels), new(Width, 30, patches[1].Pixels),
            new(128, Height, patches[2].Pixels), new(184, 30, [])]) is null, "Ignore malformed reference patches.");
        var wall = new[] { 30, 65, 100, 135 }.Select(y => Patch(source, 72, y)).ToArray();
        Check(Fit(wall) is null && GroundPatchConsensus.Refine(wall, source, Width, Height, 0, 0, [],
            allowVerticalSpread: true) is { Dx: 0, Dy: 0 }, "Texture-only recovery accepts measured vertical spread.");
    }

    private static GroundCameraSolver.Patch[] Patches(byte[] pixels) =>
        Rows.SelectMany(y => Columns.Select(x => Patch(pixels, x, y))).ToArray();

    private static GroundCameraSolver.Patch Patch(byte[] pixels, int x, int y) =>
        new(x, y, GroundCameraSolver.ReadPatch(pixels, Width, Height, x, y)!);

    private static void InsertForeground(byte[] target, IEnumerable<GroundCameraSolver.Patch> patches)
    {
        var foreground = Frame(40, 6);
        foreach (var patch in patches)
            for (var y = patch.Y + 2; y < patch.Y + 22; y++)
            {
                var start = y * Width + patch.X + 32;
                foreground.AsSpan(start, 24).CopyTo(target.AsSpan(start, 24));
            }
    }

    private static byte[] Frame(int dx = 0, int dy = 0)
    {
        var pixels = new byte[Width * Height];
        for (var index = 0; index < pixels.Length; index++)
        {
            var x = index % Width - dx;
            var y = index / Width - dy;
            var value = 110 + 18 * Math.Sin(.47 * x + .2 * y) + 22 * Math.Cos(.17 * x - .31 * y)
                + 20 * Math.Sin(.11 * x + .57 * y) + 16 * Math.Cos(.63 * x - .23 * y);
            pixels[index] = (byte)Math.Round(value);
        }
        return pixels;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
