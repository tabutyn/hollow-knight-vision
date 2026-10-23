using HollowKnightVision.Windows.Core;

internal static class InputBridgeParityTests
{
    private static void Check(bool value, string message = "Input bridge parity assertion failed.")
    {
        if (!value) throw new Exception(message);
    }

    private static void Close(double expected, double actual, double tolerance = .000_001)
    {
        Check(Math.Abs(expected - actual) <= tolerance,
            $"Expected {expected}, got {actual}.");
    }

    public static void TestPlayerTestStateNormalizesBoundsAndRandomizesValidValues()
    {
        var normalized = new PlayerTestState(
            20, 0, 40, 300, 30, -5).Normalized();
        Check(normalized.MaxHealth == 9);
        Check(normalized.Health == 1);
        Check(normalized.LifebloodSeed == 9);
        Check(normalized.Mana == 198);
        Check(normalized.ExtraManaSlots == 3);
        Check(normalized.Geo == 0);
        Check(normalized.Invincible);

        var random = new Random(7);
        for (var index = 0; index < 100; index++)
        {
            var state = PlayerTestState.Randomized(false, random);
            Check(state == state.Normalized());
            Check(state.ExtraManaSlots is >= 0 and <= 3);
            Check(!state.Invincible);
        }
    }

    public static void TestPlayerOpsWireRoundTrip()
    {
        var sessionId = Guid.NewGuid();
        var state = new PlayerTestState(8, 3, 2, 112, 1, 987, false);
        var command = ReceiverPlayerOpsCommand.Apply(sessionId, state);
        var encoded = ReceiverWireCodec.EncodeLine(command);
        Check(encoded[^1] == '\n');
        Check(ReceiverWireCodec.DecodeLine<ReceiverPlayerOpsCommand>(encoded) == command);

        var acknowledgement = $$"""
            {"version":2,"type":"playerOpsAck","sessionID":"{{sessionId}}","commandID":"{{command.CommandId}}","accepted":true,"state":{"maxHealth":8,"health":3,"lifebloodSeed":2,"mana":112,"extraManaSlots":1,"geo":987,"invincible":false},"enemiesRestored":4}
            """;
        var decoded = ReceiverWireCodec.DecodeLine<ReceiverPlayerOpsAcknowledgement>(
            acknowledgement);
        Check(decoded.State == state);
        Check(decoded.EnemiesRestored == 4);
    }

    public static void TestWireCodecRoundTripsVersionedSnapshot()
    {
        var snapshot = ReceiverInputSnapshot.Create(
            Guid.NewGuid(),
            9,
            true,
            InputButtons.Up | InputButtons.ActionZ
                | InputButtons.Inventory | InputButtons.PauseMenu);
        Check((ushort)snapshot.HeldButtons == 424);
        var encoded = ReceiverWireCodec.EncodeLine(snapshot);
        Check(encoded[^1] == '\n');
        Check(ReceiverWireCodec.DecodeLine<ReceiverInputSnapshot>(encoded) == snapshot);
    }

    public static void TestCheckpointControlMessagesRoundTrip()
    {
        var sessionId = Guid.NewGuid();
        var checkpoint = Checkpoint();
        Check(checkpoint.HasFiniteCoordinates());

        var capture = ReceiverCheckpointCommand.Capture(sessionId);
        var encodedCapture = ReceiverWireCodec.EncodeLine(capture);
        Check(ReceiverWireCodec.MessageType(encodedCapture) == "captureCheckpoint");
        Check(ReceiverWireCodec.DecodeLine<ReceiverCheckpointCommand>(encodedCapture)
            .Checkpoint is null);

        var restore = ReceiverCheckpointCommand.Restore(sessionId, checkpoint);
        var decodedRestore = ReceiverWireCodec.DecodeLine<ReceiverCheckpointCommand>(
            ReceiverWireCodec.EncodeLine(restore));
        Check(decodedRestore with { Checkpoint = null } == restore with { Checkpoint = null });
        CheckCheckpoint(checkpoint, decodedRestore.Checkpoint);

        var acknowledgement =
            "{\"version\":2,\"type\":\"captureCheckpointAck\",\"sessionID\":\""
            + sessionId + "\",\"commandID\":\"" + capture.CommandId
            + "\",\"accepted\":true,\"checkpoint\":{\"sceneName\":\"Crossroads_01\"," 
            + "\"entryGateName\":null,\"heroX\":12.5,\"heroY\":7.25,\"heroZ\":0,"
            + "\"velocityX\":0,\"velocityY\":0,\"facingRight\":true,\"grounded\":true,"
            + "\"cameraX\":13,\"cameraY\":9,\"cameraZ\":-38.1,\"cameraTargetX\":13,"
            + "\"cameraTargetY\":9,\"cameraTargetZ\":0,\"enemies\":null,\"isFirstGame\":false,"
            + "\"enteredTutorialFirstTime\":false,\"visitedDirtmouth\":false,"
            + "\"visitedCrossroads\":false,\"openedTown\":false,\"openedCrossroads\":false,"
            + "\"scenesVisited\":[\"Tutorial_01\"]}}";
        var decoded = ReceiverWireCodec.DecodeLine<ReceiverCheckpointAcknowledgement>(
            acknowledgement);
        Check(decoded.Accepted);
        CheckCheckpoint(checkpoint, decoded.Checkpoint);
    }

    public static void TestGroundTruthTelemetryDecodesAndProvidesProjectionScale()
    {
        var sessionId = Guid.NewGuid();
        var data = $$"""
            {"version":2,"type":"groundTruth","sessionID":"{{sessionId}}","sequence":7,"unityFrame":99,"unityRealtime":12.5,"sceneName":"Tutorial_01","heroAvailable":true,"heroX":36.25,"heroY":11.5,"heroZ":0.004,"velocityX":-2,"velocityY":0,"facingRight":false,"grounded":true,"cameraAvailable":true,"cameraX":37.25,"cameraY":14.1,"cameraZ":-38.1,"cameraTargetX":36.25,"cameraTargetY":14.1,"cameraTargetZ":0.004,"orthographicSize":480,"pixelsPerWorldUnitX":38.4,"pixelsPerWorldUnitY":38.4,"heroScreenX":1000,"heroScreenY":420,"projectionPixelWidth":1920,"projectionPixelHeight":1080,"screenWidth":1920,"screenHeight":1080}
            """;
        var sample = ReceiverWireCodec.DecodeLine<ReceiverGroundTruthSample>(data);
        Check(sample.SessionId == sessionId);
        Check(sample.SceneName == "Tutorial_01");
        Close(37.25, sample.CameraX);
        Check(sample.HasFiniteCoordinates());
        Close(12.8, sample.PixelsPerWorldUnit(360) ?? 0);
    }

    public static void TestPlayerPosePlaybackEncodesRecordedGroundTruth()
    {
        var sessionId = Guid.NewGuid();
        var sample = Sample();
        var command = ReceiverPlayerPoseCommand.Create(sessionId, 7, sample);
        var encoded = ReceiverWireCodec.EncodeLine(command);
        Check(ReceiverWireCodec.MessageType(encoded) == "playerPose");
        var decoded = ReceiverWireCodec.DecodeLine<ReceiverPlayerPoseCommand>(encoded);
        Check(decoded == command);
        Check(decoded.SessionId == sessionId);
        Check(decoded.Sequence == 7);
        Close(sample.HeroX, decoded.HeroX);
        Close(sample.VelocityY, decoded.VelocityY);
        Check(decoded.SceneName == sample.SceneName);
    }

    private static RecordedGameCheckpoint Checkpoint() => new(
        "Crossroads_01", null,
        12.5, 7.25, 0,
        0, 0,
        true, true,
        13, 9, -38.1,
        13, 9, 0,
        null,
        false, false, false, false, false, false,
        ["Tutorial_01"]);

    private static void CheckCheckpoint(
        RecordedGameCheckpoint expected,
        RecordedGameCheckpoint? actual)
    {
        var value = actual ?? throw new Exception("Checkpoint did not decode.");
        Check(expected.SceneName == value.SceneName);
        Check(expected.EntryGateName == value.EntryGateName);
        Close(expected.HeroX, value.HeroX);
        Close(expected.HeroY, value.HeroY);
        Close(expected.HeroZ, value.HeroZ);
        Close(expected.VelocityX, value.VelocityX);
        Close(expected.VelocityY, value.VelocityY);
        Check(expected.FacingRight == value.FacingRight);
        Check(expected.Grounded == value.Grounded);
        Close(expected.CameraX, value.CameraX);
        Close(expected.CameraY, value.CameraY);
        Close(expected.CameraZ, value.CameraZ);
        Close(expected.CameraTargetX, value.CameraTargetX);
        Close(expected.CameraTargetY, value.CameraTargetY);
        Close(expected.CameraTargetZ, value.CameraTargetZ);
        Check((expected.Enemies ?? []).SequenceEqual(value.Enemies ?? []));
        Check(expected.IsFirstGame == value.IsFirstGame);
        Check(expected.EnteredTutorialFirstTime == value.EnteredTutorialFirstTime);
        Check(expected.VisitedDirtmouth == value.VisitedDirtmouth);
        Check(expected.VisitedCrossroads == value.VisitedCrossroads);
        Check(expected.OpenedTown == value.OpenedTown);
        Check(expected.OpenedCrossroads == value.OpenedCrossroads);
        Check((expected.ScenesVisited ?? []).SequenceEqual(value.ScenesVisited ?? []));
    }

    private static ReceiverGroundTruthSample Sample() => new(
        2, "groundTruth", Guid.NewGuid(), 19,
        120, 14.5, "Tutorial_01",
        true, 36.25, 11.5, .004,
        -2, .25, false, false,
        true, 37.25, 14.1, -38.1,
        36.25, 14.1, .004,
        14.0625,
        38.4, 38.4,
        1000, 420,
        1920, 1080,
        1920, 1080);
}
