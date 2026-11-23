using System.Text.Json;
using HollowKnightVision.Windows.Core;

internal static class BotBlockadeTests
{
    internal static void TestCameraCompensationAndCrossingInBothDirections()
    {
        foreach (var side in new[] { -1, 1 })
        {
            var visit = new BotBlockadeVisit();
            for (var i = 0; i < 9; i++)
                Require(Observe(i, side * i * .025, .5, side) is null, "Following the moving Knight cannot prove a stall.");
            for (var i = 9; i < 15; i++)
                Require(Observe(i, side * .2, .5, 0) is null, "Neutral cannot establish a blockade.");
            BotBlockadeEvidence? evidence = null;
            for (var i = 15; i < 23; i++)
            {
                var drift = side * (i - 15) * .003;
                evidence = Observe(i, side * .2 + drift, .5 - drift, side);
                if (i < 17) Require(evidence is null, "The camera eased before contact was confirmed.");
            }
            Require(evidence?.Mark.Direction == side, "A world-stationary push must expose the correct side.");
            Require(Observe(23, side * .24, .5, side) is null, "Passage must retire the barrier.");
            Require(Observe(24, side * .2, .5, 0) is null, "Returning to a crossed barrier must leave it retired.");

            BotBlockadeEvidence? Observe(int tick, double camera, double knight, int drive)
            {
                var row = Row(tick, knight, drive, camera);
                return visit.Observe(row, BotBlockadePose.Visual(row));
            }
        }
    }

    internal static void TestRequiresFreshGroundContactsAndResetsCoordinates()
    {
        var visit = new BotBlockadeVisit();
        var pose = new BotBlockadePose("test", 0, 0, 1);
        for (var i = 0; i < 9; i++)
            Require(visit.Observe(Row(i) with { DetectionFrameId = 10 }, pose) is null,
                "Repeating one detector frame with new timestamps cannot establish a blockade.");
        for (var i = 9; i < 15; i++)
        {
            var row = Row(i) with { Grounded = i % 2 == 0 };
            Require(visit.Observe(row, pose) is null, "Airborne samples interrupt a ground push.");
        }
        Require(visit.Observe(Row(15), null) is null, "A missing pose cannot prove a stationary push.");
        Require(visit.Observe(Row(16) with { KnightSource = "visual-predicted" }, pose) is null,
            "A predicted position cannot prove contact.");
        Require(visit.Observe(Row(17), pose) is null && visit.Observe(Row(18), pose) is null
            && visit.Observe(Row(19), pose) is not null, "Fresh frames can establish the contact after a gap.");
        Require(visit.Observe(Row(20) with { RoomId = 2 }, pose) is null
            && visit.Observe(Row(21, drive: 0), pose) is null, "The old room must not retain a contact on reentry.");
        Require(visit.Observe(Row(22), pose) is null && visit.Observe(Row(23), pose) is null
            && visit.Observe(Row(24), pose) is not null, "A fresh visit may remeasure the obstruction.");
        Require(visit.Observe(Row(25, drive: 0), pose with { Space = "new-camera-chart" }) is null,
            "Changing visual coordinates must retire the old chart.");
    }

    internal static void TestLegacyReplayUsesOnlyPriorInputAndLeavesRecordedRowsIntact()
    {
        var temporary = Directory.CreateTempSubdirectory("hkv-blockade-replay-");
        try
        {
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var id = Guid.NewGuid();
            File.WriteAllText(Path.Combine(temporary.FullName, "manifest.json"), JsonSerializer.Serialize(new { id }, json));
            var frames = Enumerable.Range(0, 10).Select(i => Row(i, drive: null) with
            {
                BlockadeVersion = i == 6 ? 1 : null,
                VisualContext = i == 7 ? null : Row(i).VisualContext,
                Features = i >= 8 ? Row(i, .54).Features : Row(i).Features
            });
            var events = Path.Combine(temporary.FullName, "events.jsonl");
            File.WriteAllLines(events, frames.Select(row => JsonSerializer.Serialize(row, json))
                .Append(JsonSerializer.Serialize(new BotCaptureInput(.25, "right", true), json)));
            var before = File.ReadAllBytes(events);
            var recovered = BotBlockadeReplay.Recover(temporary.FullName);
            Require(recovered.Id == id && recovered.Rows.Select(row => row.CaptureTimestamp).SequenceEqual([.5])
                && recovered.Rows[0].Blockade.Mark.Direction == 1,
                "The replay may only recover contact after prior input, with a measured pose, on rows without a saved decision.");
            Require(before.SequenceEqual(File.ReadAllBytes(events)), "Replaying blockade evidence cannot modify the clip.");
        }
        finally { temporary.Delete(recursive: true); }
    }

    private static BotCaptureObservation Row(int tick, double knightX = .5, int? drive = 1, double camera = 0)
    {
        var time = tick / 10d;
        return new(time, time, time + .001, 1, 1, 0, [knightX, .2, .03, .05, 1, 0, 1], [], "detected")
        {
            DetectionTimestamp = time,
            DetectionFrameId = tick,
            DriveDirection = drive,
            KnightSource = "visual",
            VisualContext = new(640, 360d / 640, camera, 0)
        };
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
