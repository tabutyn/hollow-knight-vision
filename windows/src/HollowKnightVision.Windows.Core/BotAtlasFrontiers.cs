namespace HollowKnightVision.Windows.Core;

/// <summary>Samples the same normalized neighborhood around an observed Atlas viewport.</summary>
internal static class BotAtlasFrontiers
{
    public const double CellSize = 64;
    public static int Cell(double value) => (int)Math.Floor(value / CellSize);
    public static int FirstCenter(double value) => (int)Math.Ceiling(value / CellSize - .5);

    public static BotAtlasView View(AtlasPoint pose, WorldSize size, int revision, int? health,
        int covered, int earned, Func<int, int, bool> contains)
    {
        var middle = Cell(pose.Y + size.Height / 2);
        var missing = new List<double>(15);
        for (var y = -1; y <= 1; y++)
        for (var x = -2; x <= 2; x++)
        {
            var unknown = 0;
            for (var row = -1; row <= 1; row += 2)
            for (var column = -1; column <= 1; column += 2)
            {
                var wx = pose.X + size.Width * (.5 + x * .5 + column * .125);
                var wy = pose.Y + size.Height * (.5 + y * .5 + row * .125);
                if (!contains(Cell(wx), Cell(wy))) unknown++;
            }
            missing.Add(unknown / 4d);
        }
        return new(Proximity(-1), Proximity(1), [pose.X, pose.Y], size.Width, covered, earned, revision, health, missing);

        double Proximity(int direction)
        {
            var column = Cell(direction < 0 ? pose.X : pose.X + size.Width);
            for (var step = 1; step <= 64; step++)
            {
                var known = 0;
                for (var row = middle - 1; row <= middle + 1; row++)
                    if (contains(column + direction * step, row)) known++;
                if (known < 2) return 1 / (1 + (step - 1) / 5d);
            }
            return 0;
        }
    }
}
