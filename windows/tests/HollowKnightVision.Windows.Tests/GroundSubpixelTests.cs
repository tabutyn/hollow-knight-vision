using HollowKnightVision.Windows.Core;

internal static class GroundSubpixelTests
{
    private const int Width = 320, Height = 180;
    private static readonly int[] Rows = [30, 85, 130], Columns = [24, 72, 128, 184, 240];
    private static void Check(bool value) { if (!value) throw new Exception("Subpixel registration parity assertion failed."); }
    private static void Near(double expected, double actual, double tolerance) => Check(Math.Abs(expected - actual) <= tolerance);
    private static byte[] Frame(int dx = 0, int dy = 0, double offsetX = 0, double offsetY = 0) => Enumerable.Range(0, Width * Height).Select(index =>
    {
        var x = index % Width - dx - offsetX; var y = index / Width - dy - offsetY;
        return (byte)Math.Clamp(Math.Round(110 + 18 * Math.Sin(.47 * x + .2 * y) + 22 * Math.Cos(.17 * x - .31 * y)
            + 20 * Math.Sin(.11 * x + .57 * y) + 16 * Math.Cos(.63 * x - .23 * y), MidpointRounding.AwayFromZero), 0, 255);
    }).ToArray();
    private static GroundSubpixelRegistration.Patch[] Patches(byte[] pixels) => Rows.SelectMany(y => Columns.Select(x =>
    {
        var values = new byte[192]; for (var row = 0; row < 12; row++) Array.Copy(pixels, (y + row) * Width + x, values, row * 16, 16);
        return new GroundSubpixelRegistration.Patch(x, y, values);
    })).ToArray();
    private static GroundTextureLine[] Lines(int dy = 0) => Rows.Select(row => new GroundTextureLine(row + dy, 16, 303)).ToArray();
    public static void TestFractionalTranslation()
    {
        var result = GroundSubpixelRegistration.Refine(Patches(Frame()), Frame(4, -2, .4, -.3), Width, Height, 4, -2)
            ?? throw new Exception("Fractional translation was not recovered.");
        Near(.4, result.Offset.X, .07); Near(-.3, result.Offset.Y, .07); Check(result.Iterations <= 6);
    }
    public static void TestIntegratedFractionalSolve()
    {
        var solver = new GroundCameraSolver(subpixelEnabled: true);
        Check(solver.Solve(Frame(), Width, Height, Lines(), 0, []) is not null);
        var result = solver.Solve(Frame(-3, 5, .35, -.25), Width, Height, Lines(5), 1.0 / 60, []) ?? throw new Exception("Fractional solver rejected the match.");
        Near(-2.65, result.ImageTranslation.X, .12); Near(4.75, result.ImageTranslation.Y, .12);
        Near(-2.65, result.MatchedImageDisplacement!.Value.X, .12); Near(4.75, result.MatchedImageDisplacement!.Value.Y, .12);
    }
    public static void TestIntegerAndFlat()
    {
        var solver = new GroundCameraSolver(subpixelEnabled: true); Check(solver.Solve(Frame(), Width, Height, Lines(), 0, []) is not null);
        var moved = solver.Solve(Frame(-4, 5), Width, Height, Lines(5), 1.0 / 60, []) ?? throw new Exception("Integer match rejected.");
        Check(moved.ImageTranslation == new CameraVector(-4, 5));
        Check(GroundSubpixelRegistration.Refine(Patches(Frame()), Enumerable.Repeat((byte)80, Width * Height).ToArray(), Width, Height, 0, 0) is null);
    }
}
