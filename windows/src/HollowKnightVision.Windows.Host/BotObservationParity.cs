using System.Text.Json;
using HollowKnightVision.Windows.Core;

namespace HollowKnightVision.Windows.Host;

internal static class BotObservationParity
{
    private const int MaximumFileBytes = 64 * 1024 * 1024;
    private const double Tolerance = .000002;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Fixture(int Version, bool AllowMaskedKnight, Row[] Rows);
    private sealed record Row(BotCaptureObservation Observation, BotPolicyAction Previous, float[]? Expected);

    internal static void Verify(string path, TextWriter output)
    {
        using var file = File.OpenRead(path);
        if (file.Length > MaximumFileBytes) throw new InvalidDataException("The observation fixture is too large.");
        var fixtures = JsonSerializer.Deserialize<Fixture[]>(file, Json);
        if (fixtures is not { Length: > 0 }) throw new InvalidDataException("Empty observation parity fixture.");
        var vectors = 0;
        foreach (var fixture in fixtures)
        {
            var history = new BotObservationHistory(fixture.Version);
            var names = BotPolicyContract.Names(fixture.Version)!;
            if (fixture.Rows is not { Length: > 0 }) throw new InvalidDataException("Empty observation sequence.");
            for (var rowIndex = 0; rowIndex < fixture.Rows.Length; rowIndex++)
            {
                var row = fixture.Rows[rowIndex];
                var actual = history.Append(row.Observation, row.Previous, row.Observation.AvailableAt, fixture.AllowMaskedKnight);
                if (actual is null && row.Expected is null) continue;
                if (actual is null || row.Expected is null || actual.Length != row.Expected.Length)
                    throw new InvalidDataException($"Observation availability or width disagreed at schema {fixture.Version}, row {rowIndex + 1}.");
                for (var i = 0; i < actual.Length; i++)
                    if (!float.IsFinite(actual[i]) || !float.IsFinite(row.Expected[i]) || Math.Abs(actual[i] - row.Expected[i]) > Tolerance)
                        throw new InvalidDataException($"Schema {fixture.Version}, row {rowIndex + 1}, history frame {i / names.Count + 1}, {names[i % names.Count]}: native {actual[i]}, trainer {row.Expected[i]}.");
                vectors++;
            }
        }
        output.WriteLine($"Verified {vectors} bot observation vectors against the trainer.");
    }
}
