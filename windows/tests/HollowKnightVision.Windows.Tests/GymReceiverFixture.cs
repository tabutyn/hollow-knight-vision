using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using HollowKnightVision.Windows.Core;

internal static class GymReceiverFixture
{
    private const string Scene = "Tutorial_01";
    private const int Width = 640, Height = 360;
    private const double HeroX = 34.5, HeroY = 11.41, PixelsPerUnit = 20;
    private const double HeroPixelX = 180.8, HeroPixelY = 144;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static void Export(string root)
    {
        Directory.CreateDirectory(root);
        new GroundTruthLineStore(Path.Combine(root, "labels.json")).Save(new()
        {
            Lines = [new(Scene, 18, 62, 10.26, GroundTruthLineKind.Positive, false)]
        });
    }

    internal static void Run(string[] args, string fixture, string directory)
    {
        try
        {
            using var configuration = Read(Path.Combine(directory, "episode.json"));
            var episode = configuration.RootElement.GetProperty("episode").GetGuid();
            var domain = configuration.RootElement.GetProperty("domain").GetString();
            var visual = domain == "gameplay";
            var renderRate = configuration.RootElement.GetProperty("renderHz").GetInt32();
            if (configuration.RootElement.GetProperty("policyDomain").GetString() != domain
                || args.Contains("-nographics") == visual
                || OperatingSystem.IsWindows() && !string.Equals(Path.GetFullPath(Environment.CurrentDirectory),
                    Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Fixture received mismatched Gym domain or launch options.");
            if (visual) File.Copy(Path.Combine(fixture, "frame.rgb"), Path.Combine(directory, "frame.rgb"));

            var unsafeSpawn = Environment.GetEnvironmentVariable("HKV_GYM_TEST_INVINCIBLE") == "1";
            Publish(0);
            var requestPath = Path.Combine(directory, "request.json");
            for (var step = 1; step <= 3; step++)
            {
                WaitForRequest(requestPath);
                using var request = Read(requestPath);
                var body = request.RootElement;
                if (body.GetProperty("episode").GetGuid() != episode || body.GetProperty("step").GetInt32() != step
                    || body.GetProperty("ticks").GetInt32() != 6 || body.GetProperty("buttons").GetInt32() != (step < 3 ? 66 : 2))
                    throw new InvalidDataException("Fixture received an unexpected Gym input or tick count.");
                File.Delete(requestPath);
                Publish(step);
            }
            WaitForRequest(requestPath);

            void Publish(int step)
            {
                var cameraX = HeroX + (Width / 2d - HeroPixelX) / PixelsPerUnit;
                var cameraY = HeroY + (Height / 2d - HeroPixelY) / PixelsPerUnit;
                var sample = new ReceiverGroundTruthSample(ReceiverProtocol.ControlVersion, "groundTruth", episode,
                    (ulong)step, step * 6, step / 10d, Scene, true, HeroX, HeroY, 0, 0, 0, true, true, true,
                    cameraX, cameraY, 0, cameraX, cameraY, 0, Height / (2 * PixelsPerUnit), PixelsPerUnit, PixelsPerUnit,
                    HeroPixelX, HeroPixelY, Width, Height, Width, Height)
                {
                    AtlasGameplayActive = true,
                    Actors = new(1, new(1, "Knight", HeroPixelX, HeroPixelY, 28.8, 50.4),
                        [new(2, "Crawlid", 408, 131, 38.4, 39.6)], [new(3, "Geo", 296, 128, 16, 24)], []),
                    BlueHealthOutcomes = new(episode, step == 3 ? 1 : 0)
                };
                var reply = JsonSerializer.SerializeToNode(sample, Json)!.AsObject();
                reply["episode"] = episode.ToString("D");
                reply["step"] = step;
                reply["ticks"] = step * 6;
                reply["health"] = step == 3 ? 6 : 5;
                reply["damage"] = 0;
                reply["noSave"] = true;
                reply["invincible"] = unsafeSpawn;
                reply["gameState"] = "PLAYING";
                reply["renderHz"] = renderRate;
                reply["simulationHz"] = 60;
                reply["pixelWidth"] = Width;
                reply["pixelHeight"] = Height;
                var pending = Path.Combine(directory, "reply.tmp");
                File.WriteAllText(pending, reply.ToJsonString());
                File.Move(pending, Path.Combine(directory, "reply.json"));
            }
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(directory, "error.txt"), error.Message);
            throw;
        }
    }

    private static JsonDocument Read(string path) => JsonDocument.Parse(File.ReadAllBytes(path));

    private static void WaitForRequest(string path)
    {
        var started = Stopwatch.GetTimestamp();
        while (!File.Exists(path))
        {
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(40))
                throw new TimeoutException("Fixture did not receive its next Gym request.");
            Thread.Sleep(10);
        }
    }
}
