using System.Security.Cryptography;
using HollowKnightVision.Windows.Core;
using static HollowKnightVision.Windows.Core.LiveActorConfirmation;

internal static class LiveActorConfirmationTests
{
    private static readonly Guid Run = new("f8883b2f-bfb8-4011-be37-1663a98ae98e");
    private static readonly ActorHeatmapRecipe Recipe = new("Heatmap.onnx", new string('a', 64), .995f, .05, 20d / 360);

    internal static void TestDepositRecipeRequiresMatchingRunAndConfidence()
    {
        var receipt = new ActorCalibrationReceipt(Run, Recipe, 0, 0, 0, false);
        Check(receipt.Matches(Deposit, Run), "Expected the current trained run's deposit heatmap.");
        Check(!receipt.Matches(Deposit, Guid.NewGuid()), "The previous run's heatmap must be ignored.");
        Check(!(receipt with { Localizer = Recipe with { StandaloneConfidence = .6f } }).Matches(Deposit, Run),
            "A weak deposit heatmap could match the HUD.");
        Check(!(receipt with { Localizer = Recipe with { Sha256 = "wrong" } }).Matches(Deposit, Run),
            "The receipt must include a complete digest.");
    }

    internal static void TestDepositRejectsSoulVessel()
    {
        static ObjectDetection Peak(double x, double y) => new(Deposit, new(x - .025, y - .05, .05, .1), .98f);
        Check(!VisibleDeposit(Peak(60d / 640, 40d / 360)), "A peak inside the soul vessel is not a deposit.");
        Check(VisibleDeposit(Peak(80d / 640, 40d / 360)), "The room beside the soul vessel is eligible.");
        Check(VisibleDeposit(Peak(60d / 640, 61d / 360)), "The room below the soul vessel is eligible.");
    }

    internal static void TestKnightAgreementAndHud()
    {
        var recipe = new ActorCalibrationReceipt(Run, Recipe with { StandaloneConfidence = .98f }, .25f, .975f, 0, true);
        static ObjectDetection Box(double x, double y, float score) => new(Knight, new(x, y, .034, .12), score);
        var strong = Box(.48, .56, .78f);
        var weak = Box(.48, .56, .35f);
        var corroborated = Box(.49, .56, .976f);
        var independent = Box(.49, .56, .997f);
        var hud = Box(.1, .04, .85f);
        Check(!NeedsKnightReference([strong], recipe), "A strong Knight in the room needs no additional inference.");
        Check(ConfirmKnight([strong], [independent], recipe).SequenceEqual([strong]), "Keep the strong baseline Knight.");
        Check(ConfirmKnight([], [corroborated], recipe).Count == 0, "A weak reference alone is insufficient.");
        Check(ConfirmKnight([weak], [corroborated], recipe).SequenceEqual([corroborated]), "Confirm a nearby weak result.");
        Check(ConfirmKnight([], [independent], recipe).SequenceEqual([independent]), "A sufficiently strong reference can recover the Knight.");
        Check(ConfirmKnight([hud], [independent], recipe).SequenceEqual([independent]), "A distant in-room match can replace a HUD error.");
        Check(ConfirmKnight([hud], [Box(.12, .03, .999f)], recipe).SequenceEqual([hud]), "A match inside the HUD cannot justify replacement.");
    }

    internal static void TestCrawlidHeatmapCoordinates()
    {
        var scores = new float[90 * 160];
        scores[30 * 160 + 80] = .998f;
        scores[30 * 160 + 81] = .994f;
        scores[75 * 160 + 35] = .992f;
        var boxes = Recipe.Peaks(scores, Crawlid, .99f);
        Check(boxes.Count == 2, "The lower adjacent peak should be suppressed.");
        var peak = ObjectDetectionGeometry.Rect(boxes[0].Box);
        Check(Math.Abs(peak.MidX - .5) < .000001 && Math.Abs(peak.MidY - 1d / 3) < .000001,
            "The peak should have top-left image coordinates.");
        var receipt = new ActorCalibrationReceipt(Run, Recipe, .1f, .99f, .25, false);
        Check(ConfirmCrawlids([], [], boxes, receipt).SequenceEqual([boxes[0]]),
            "Only the independently strong peak is eligible without corroboration.");
    }

    internal static void TestCrawlidSpatialAgreement()
    {
        var recipe = new ActorCalibrationReceipt(Run, null, .18f, .65f, .6, false);
        static ObjectDetection Box(double x, float confidence) => new(Crawlid, new(x, .6, .05, .08), confidence);
        var original = Box(.2, .86f);
        var knight = new ObjectDetection(Knight, new(.4, .51, .034, .12), .8f);
        var agreed = Box(.601, .93f);
        var result = ConfirmCrawlids([original, knight], [Box(.2, .24f), Box(.6, .23f)],
            [Box(.201, .8f), agreed, Box(.8, .97f), Box(.6, .53f)], recipe);
        Check(result.SequenceEqual([original, knight, agreed]), "Preserve primary detections and add only the agreed Crawlid.");
    }

    internal static void TestProductionDetectorUsesVerifiedLocalizers()
    {
        var root = Directory.CreateTempSubdirectory("hkv-actor-calibration-");
        try
        {
            var runDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "v8.0")).FullName;
            var calibrations = Directory.CreateDirectory(Path.Combine(root.FullName, "calibrations")).FullName;
            var detectorPath = Path.Combine(runDirectory, "Detector.onnx");
            CopyModel("actor-calibration-detector.onnx", detectorPath);
            var heatmapPath = Path.Combine(calibrations, Recipe.ModelFilename);
            CopyModel("actor-calibration-heatmap.onnx", heatmapPath);
            var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(heatmapPath)));
            File.WriteAllText(Path.Combine(runDirectory, "training.json"), $$"""
                {"id":"{{Run:D}}","modelFormat":"onnx","modelFilename":"Detector.onnx",
                 "classIdentifiers":["game.playable-knight","enemies.crawlid","world.geo-deposit","enemies.shade"],
                 "trainedClassIdentifiers":["game.playable-knight","enemies.crawlid","world.geo-deposit"]}
                """);
            var heatmap = Recipe with { Sha256 = digest, BoxHeight = .08 };
            var baselineRecipe = new ActorCalibrationReceipt(Run, heatmap, .18f, .65f, .6, true);
            WriteReceipt("knight", baselineRecipe);
            WriteReceipt("crawlid", baselineRecipe with { Localizer = heatmap with { StandaloneConfidence = 1 } });
            WriteReceipt("geo-deposit", baselineRecipe);
            var version = new LabelingModelSemanticVersion(8, 0);
            var artifact = new LabelingActiveModelArtifact("shared-object-model", version, detectorPath,
                new HashSet<string>([Knight, Crawlid, Deposit])) { SourceRunIdentifier = Run };
            var frame = new BgraFrame(640, 360, 640 * 4, new byte[640 * 360 * 4], 47, DateTimeOffset.UnixEpoch);

            using (var detector = new OnnxObjectDetectorSet([runDirectory], [artifact]))
            {
                var result = detector.Detect(frame);
                Check(result.Select(item => item.ClassIdentifier).SequenceEqual([Crawlid, Knight, Deposit, Crawlid]),
                    "The production detector should keep the baseline and add verified Knight, deposit, and corroborated enemy.");
                Check(result.All(item => item.SourceFrameIdentifier == 47 && item.ModelVersion == version),
                    "Every supplemental actor must keep the exact source frame and active version.");
                var raw = detector.Detect(frame, raw: true);
                Check(raw.Count == 2 && raw.All(item => item.ClassIdentifier == Crawlid),
                    "The raw debug path should expose only primary trained heads.");
            }
            using (var detector = new OnnxObjectDetectorSet([runDirectory]))
                Check(detector.Detect(frame).Count == 4, "A manually selected run uses the source identity in its manifest.");
            using (var detector = new OnnxObjectDetectorSet([runDirectory], [artifact], useActorCalibrations: false))
                Check(detector.Detect(frame).Single().ClassIdentifier == Crawlid, "Explicit Gym model selection should use baseline alone.");
            using (var detector = new OnnxObjectDetectorSet([runDirectory], [artifact with { SourceRunIdentifier = Guid.NewGuid() }]))
                Check(detector.Detect(frame).Single().ClassIdentifier == Crawlid, "A different active run must use baseline alone.");
            using (var detector = new OnnxObjectDetectorSet([runDirectory], [artifact with { TrainedClassIdentifiers = new HashSet<string>([Knight, Crawlid]) }]))
                Check(detector.Detect(frame).All(item => item.ClassIdentifier != Deposit), "Calibration cannot enable an untrained class.");
            using (var detector = new OnnxObjectDetectorSet([runDirectory], [artifact with { TrainedClassIdentifiers = new HashSet<string>() }]))
                Check(detector.Detect(frame).Count == 0, "A run without trained classes must publish no detections.");

            WriteReceipt("knight", baselineRecipe with { SourceRunIdentifier = Guid.NewGuid() });
            WriteReceipt("crawlid", baselineRecipe with { Localizer = heatmap with { ModelFilename = "../Heatmap.onnx" } });
            WriteReceipt("geo-deposit", baselineRecipe with { Localizer = Recipe });
            using (var detector = new OnnxObjectDetectorSet([runDirectory], [artifact]))
                Check(detector.Detect(frame).Single().ClassIdentifier == Crawlid,
                    "Invalid run, path, and hash metadata must leave the primary result usable.");

            File.WriteAllText(heatmapPath, "not an ONNX model");
            WriteReceipt("knight", baselineRecipe with { Localizer = heatmap with
                { Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(heatmapPath))) } });
            using (var detector = new OnnxObjectDetectorSet([runDirectory], [artifact]))
                Check(detector.Detect(frame).Single().ClassIdentifier == Crawlid,
                    "A corrupt optional model must leave the primary result usable.");

            void WriteReceipt(string name, ActorCalibrationReceipt receipt) =>
                ModelStoreJson.Write(Path.Combine(calibrations, $"{name}-{Run:D}.json"), receipt);
        }
        finally { root.Delete(recursive: true); }
    }

    private static void CopyModel(string name, string destination)
    {
        using var input = typeof(LiveActorConfirmationTests).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Missing ONNX fixture {name}.");
        using var output = File.Create(destination);
        input.CopyTo(output);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
