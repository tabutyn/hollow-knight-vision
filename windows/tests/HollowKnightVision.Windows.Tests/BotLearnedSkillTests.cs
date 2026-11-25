using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class BotLearnedSkillTests
{
    private const int Schema = BotPolicyContract.LandingVersion;
    private const int Frames = BotRecordingContract.HistoryLength;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed record Fixture(string Domain, float[][] Observations, InputButtons[] Buttons);

    internal static void TestAbilityModelsAndBoundInference()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-ability-models-");
        var root = temporary.FullName;
        var width = BotPolicyContract.Names(Schema)!.Count;
        var newest = (Frames - 1) * width;
        var raw = new float[Frames * width];
        var folder = Directory.CreateDirectory(Path.Combine(root, "skill-actors", "hacker")).FullName;
        try
        {
            Require(BotLearnedSkill.Load(root, BotCaptureDomain.Hacker, BotAbilitySkill.Fight) is null,
                "A missing optional model changes ordinary maneuvers.");
            Write("fight", 2, ["none", "dash"], [newest],
                [new Layer(1, 2, "linear", [0, 1], [0, -.2f])]);
            var fight = BotLearnedSkill.Load(root, BotCaptureDomain.Hacker, BotAbilitySkill.Fight)!;
            Require(fight.Predict(raw) == InputButtons.None, "A released ability leaves the motor alone.");
            raw[newest] = .5f;
            Require(fight.Predict(raw) == InputButtons.Dash && fight.ObservationSchema == Schema,
                "The compact ability model must use the saved newest-frame column.");
            raw[newest] = float.NaN;
            Reject(() => fight.Predict(raw));

            Write("recover", 1, ["none", "a"], null,
                [new Layer(raw.Length, 2, "linear", new float[raw.Length * 2], [0, 1])]);
            raw[newest] = 0;
            Require(BotLearnedSkill.Load(root, BotCaptureDomain.Hacker, BotAbilitySkill.Recover)!.Predict(raw) == InputButtons.ActionA,
                "An earlier full-width recovery model must still decode its Focus label.");
            Write("fight", 2, ["none", "superDash"], [newest],
                [new Layer(1, 2, "linear", [0, 1], [0, 1])]);
            Reject(() => BotLearnedSkill.Load(root, BotCaptureDomain.Hacker, BotAbilitySkill.Fight));
            Write("fight", 2, ["none", "dash"], [newest, newest],
                [new Layer(2, 2, "linear", [0, 0, 0, 1], [0, 1])]);
            Reject(() => BotLearnedSkill.Load(root, BotCaptureDomain.Hacker, BotAbilitySkill.Fight));
            Write("fight", 2, ["none", "dash"], [newest],
                [new Layer(1, 2, "linear", [0, 1], [0, 1])], domain: "gameplay");
            Reject(() => BotLearnedSkill.Load(root, BotCaptureDomain.Hacker, BotAbilitySkill.Fight));

            var scope = new ThinkingScope(1, BotCaptureDomain.Hacker, "King's Pass", Guid.NewGuid());
            var scene = new ThinkingScene(2, true, null, [new(8, -.05, .02)], [], []);
            var sight = new ThinkingSight(scope, 10, true, null, null, [], scene);
            var candidate = new ThinkingPolicyOffer(sight).Candidates.Single();
            var drive = new ThinkingAbilityDrive(fight, null, candidate);
            var features = new double[BotRecordingContract.FeatureNames.Count];
            features[0] = .5;
            features[4] = features[6] = 1;
            var observed = new BotCaptureObservation(10, 10, 10.01, scope.Generation, 2, 1, features, [], "human-authored")
                { DetectionTimestamp = 10 };
            var motion = new ThinkingMotion(InputButtons.Left | InputButtons.ActionX, 10.20, "Fight");
            Require(drive.Apply(motion, sight, observed, InputButtons.None, 10.02, true) == (motion.Buttons | InputButtons.Dash)
                && drive.Deadline(motion.Deadline, 10.02) < motion.Deadline, "Current authored features must add Dash to the bound attack.");
            Require(drive.Apply(motion, sight, observed, InputButtons.None, 10.04, false) == motion.Buttons
                && drive.Apply(motion, sight, observed with { CaptureTimestamp = 9 }, InputButtons.None, 10.05, true) == motion.Buttons
                && drive.Apply(motion, sight, observed with { TerrainSource = "detected" }, InputButtons.None, 10.05, true) == motion.Buttons
                && drive.Apply(motion, sight, observed, InputButtons.None, 10.50, true) == motion.Buttons,
                "Other frames, the wrong terrain domain, expired input, and unsupported receivers must never retain Dash.");
        }
        finally { temporary.Delete(recursive: true); }

        void Write(string skill, int version, string[] labels, int[]? columns, Layer[] layers, string domain = "hacker") =>
            File.WriteAllText(Path.Combine(folder, skill + ".json"), JsonSerializer.Serialize(new
            {
                version, domain, skill, observationSchema = Schema, historyLength = Frames, labels, inputColumns = columns, layers
            }, Json));
    }

    internal static void VerifyTrainedActor(string root, string path)
    {
        var fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(path), Json)!;
        var domain = fixture.Domain == "hacker" ? BotCaptureDomain.Hacker : BotCaptureDomain.Gameplay;
        var model = BotLearnedSkill.Load(root, domain, BotAbilitySkill.Traverse)!;
        Require(fixture.Observations.Length > 0 && fixture.Observations.Select(model.Predict).SequenceEqual(fixture.Buttons),
            "Native ability inference disagreed with the trainer on Windows-captured features.");
        Console.WriteLine($"PASS native {fixture.Domain} traversal abilities on {fixture.Buttons.Length} trained fixture rows");
    }

    private sealed record Layer(int InputSize, int OutputSize, string Activation, float[] Weights, float[] Biases);

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("An invalid ability model or input was accepted.");
    }

    private static void Require(bool valid, string reason)
    {
        if (!valid) throw new InvalidOperationException(reason);
    }
}
