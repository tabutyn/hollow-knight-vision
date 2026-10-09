using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HollowKnightVisionInputReceiver
{
    internal static class ReceiverControlProtocol
    {
        internal const int Version = 2;
        internal const int PauseLeaseMilliseconds = 2000;
    }

    [Flags]
    internal enum VisionButtons
    {
        None = 0,
        Left = 1 << 0,
        Right = 1 << 1,
        Down = 1 << 2,
        Up = 1 << 3,
        // These values name Vision's physical keys, not a game action.  The
        // receiver maps them to Hollow Knight's normal keyboard semantics.
        ActionA = 1 << 4,
        ActionZ = 1 << 5,
        ActionX = 1 << 6,
        Inventory = 1 << 7,
        PauseMenu = 1 << 8,
        All = Left | Right | Down | Up | ActionA | ActionZ | ActionX | Inventory | PauseMenu
    }

    // Separates a newly assigned state from the recurring InControl updates
    // that commit it. A state can therefore acknowledge exactly once, after
    // its first completed Commit.
    internal sealed class CommitAckGate
    {
        private string session;
        private long sequence = -1;
        private bool enabled;
        private VisionButtons buttons;
        private bool pending;

        internal void Set(string nextSession, long nextSequence, bool nextEnabled, VisionButtons nextButtons)
        {
            session = nextSession;
            sequence = nextSequence;
            enabled = nextEnabled;
            buttons = nextEnabled ? InputSnapshot.Normalize(nextButtons) : VisionButtons.None;
            pending = !String.IsNullOrEmpty(session) && sequence >= 0;
        }

        internal bool TryTake(out string committedSession, out long committedSequence, out bool committedEnabled, out VisionButtons committedButtons)
        {
            if (!pending) {
                committedSession = null;
                committedSequence = -1;
                committedEnabled = false;
                committedButtons = VisionButtons.None;
                return false;
            }
            pending = false;
            committedSession = session;
            committedSequence = sequence;
            committedEnabled = enabled;
            committedButtons = buttons;
            return true;
        }
    }

    internal sealed class InputSnapshot
    {
        internal const int ProtocolVersion = 1;
        internal string Session;
        internal long Sequence;
        internal bool Enabled;
        internal VisionButtons Buttons;

        internal static bool TryParse(string json, out InputSnapshot snapshot)
        {
            snapshot = null;
            try
            {
                var objectValue = JObject.Parse(json);
                if (objectValue == null || ReadInt(objectValue, "version") != ProtocolVersion || ReadString(objectValue, "type") != "state") return false;
                var session = ReadString(objectValue, "sessionID");
                if (String.IsNullOrWhiteSpace(session)) return false;
                var buttons = (VisionButtons) ReadInt(objectValue, "heldButtons") & VisionButtons.All;
                snapshot = new InputSnapshot {
                    Session = session,
                    Sequence = ReadLong(objectValue, "sequence"),
                    Enabled = ReadBool(objectValue, "enabled"),
                    Buttons = buttons
                };
                return snapshot.Sequence >= 0;
            }
            catch { return false; }
        }

        internal static string Ack(string session, long sequence, bool enabled, VisionButtons buttons)
        {
            var effectiveButtons = enabled ? Normalize(buttons) : VisionButtons.None;
            return new JObject {
                ["version"] = ProtocolVersion,
                ["type"] = "ack",
                ["sessionID"] = session,
                ["sequence"] = sequence,
                ["appliedButtons"] = (int)effectiveButtons,
                ["enabled"] = enabled
            }.ToString(Formatting.None);
        }

        internal static VisionButtons Normalize(VisionButtons buttons)
        {
            var result = buttons & VisionButtons.All;
            if ((result & (VisionButtons.Left | VisionButtons.Right)) == (VisionButtons.Left | VisionButtons.Right)) result &= ~(VisionButtons.Left | VisionButtons.Right);
            if ((result & (VisionButtons.Up | VisionButtons.Down)) == (VisionButtons.Up | VisionButtons.Down)) result &= ~(VisionButtons.Up | VisionButtons.Down);
            return result;
        }

        private static int ReadInt(JObject value, string name) { return value.Value<int>(name); }
        private static long ReadLong(JObject value, string name) { return value.Value<long>(name); }
        private static bool ReadBool(JObject value, string name) { return value.Value<bool>(name); }
        private static string ReadString(JObject value, string name) { return value.Value<string>(name); }

    }

    internal sealed class CapabilityRequest
    {
        internal string Session;
        internal bool RenderFrameMarker;

        internal static bool TryParse(string json, out CapabilityRequest request)
        {
            request = null;
            try
            {
                var value = JObject.Parse(json);
                if (value == null || value.Value<int>("version") != ReceiverControlProtocol.Version
                    || value.Value<string>("type") != "hello") return false;
                var session = value.Value<string>("sessionID");
                if (String.IsNullOrWhiteSpace(session)) return false;
                request = new CapabilityRequest { Session = session,
                    RenderFrameMarker = value.Value<bool?>("renderFrameMarker") ?? false };
                return true;
            }
            catch { return false; }
        }

        internal static string Ack(string session)
        {
            return new JObject {
                ["version"] = ReceiverControlProtocol.Version,
                ["type"] = "capabilitiesAck",
                ["sessionID"] = session,
                ["capabilities"] = new JArray("input-state-v1", "pause-lease-v1", "menu-shortcuts-v1", "pointer-events-v1", "player-checkpoint-v1", "player-pose-playback-v1", "ground-truth-telemetry-v1", "player-ops-v1", "render-frame-marker-v1"),
                ["pauseLeaseMilliseconds"] = ReceiverControlProtocol.PauseLeaseMilliseconds
            }.ToString(Formatting.None);
        }
    }

    internal enum PauseCommandKind
    {
        Pause,
        Resume
    }

    internal sealed class PauseRequest
    {
        internal string Session;
        internal string CommandID;
        internal PauseCommandKind Kind;
        internal int LeaseMilliseconds;
        internal bool IsDisconnect;

        internal static bool TryParse(string json, out PauseRequest request)
        {
            request = null;
            try
            {
                var value = JObject.Parse(json);
                if (value == null || value.Value<int>("version") != ReceiverControlProtocol.Version) return false;
                var type = value.Value<string>("type");
                PauseCommandKind kind;
                if (type == "pause") kind = PauseCommandKind.Pause;
                else if (type == "resume") kind = PauseCommandKind.Resume;
                else return false;
                var session = value.Value<string>("sessionID");
                var commandID = value.Value<string>("commandID");
                if (String.IsNullOrWhiteSpace(session) || String.IsNullOrWhiteSpace(commandID)) return false;
                var requestedLease = kind == PauseCommandKind.Pause
                    ? value.Value<int?>("leaseMilliseconds") ?? 0
                    : 0;
                if (kind == PauseCommandKind.Pause && requestedLease <= 0) return false;
                request = new PauseRequest {
                    Session = session,
                    CommandID = commandID,
                    Kind = kind,
                    LeaseMilliseconds = Math.Min(requestedLease, ReceiverControlProtocol.PauseLeaseMilliseconds)
                };
                return true;
            }
            catch { return false; }
        }

        internal static PauseRequest Disconnect(string session)
        {
            return new PauseRequest {
                Session = session,
                CommandID = null,
                Kind = PauseCommandKind.Resume,
                LeaseMilliseconds = 0,
                IsDisconnect = true
            };
        }

        internal static string Ack(PauseRequest request, bool paused)
        {
            return new JObject {
                ["version"] = ReceiverControlProtocol.Version,
                ["type"] = request.Kind == PauseCommandKind.Pause ? "pauseAck" : "resumeAck",
                ["sessionID"] = request.Session,
                ["commandID"] = request.CommandID,
                ["paused"] = paused
            }.ToString(Formatting.None);
        }
    }

    internal sealed class PointerRequest
    {
        internal string Session;
        internal long Sequence;
        internal string Kind;
        internal double NormalizedX;
        internal double NormalizedY;
        internal int ClickCount;
        internal bool IsDisconnect;

        internal static bool TryParse(string json, out PointerRequest request)
        {
            request = null;
            try
            {
                var value = JObject.Parse(json);
                if (value == null || value.Value<int>("version") != ReceiverControlProtocol.Version
                    || value.Value<string>("type") != "pointer") return false;
                var session = value.Value<string>("sessionID");
                var kind = value.Value<string>("kind");
                var sequence = value.Value<long>("sequence");
                var x = value.Value<double>("normalizedX");
                var y = value.Value<double>("normalizedY");
                if (String.IsNullOrWhiteSpace(session) || sequence < 0
                    || (kind != "moved" && kind != "leftDown" && kind != "leftDragged"
                        && kind != "leftUp" && kind != "exited")) return false;
                if (kind != "exited" && (Double.IsNaN(x) || Double.IsInfinity(x)
                    || Double.IsNaN(y) || Double.IsInfinity(y)
                    || x < 0 || x > 1 || y < 0 || y > 1)) return false;
                request = new PointerRequest {
                    Session = session,
                    Sequence = sequence,
                    Kind = kind,
                    NormalizedX = x,
                    NormalizedY = y,
                    ClickCount = Math.Max(1, value.Value<int?>("clickCount") ?? 1)
                };
                return true;
            }
            catch { return false; }
        }

        internal static PointerRequest Disconnect(string session)
        {
            return new PointerRequest {
                Session = session,
                Sequence = -1,
                Kind = "exited",
                ClickCount = 1,
                IsDisconnect = true
            };
        }
    }

    internal sealed class PlayerCheckpoint
    {
        internal string SceneName;
        internal string EntryGateName;
        internal double HeroX;
        internal double HeroY;
        internal double HeroZ;
        internal double VelocityX;
        internal double VelocityY;
        internal bool FacingRight;
        internal bool Grounded;
        internal double CameraX;
        internal double CameraY;
        internal double CameraZ;
        internal double CameraTargetX;
        internal double CameraTargetY;
        internal double CameraTargetZ;
        internal List<EnemyCheckpoint> Enemies = new List<EnemyCheckpoint>();
        internal bool? IsFirstGame;
        internal bool? EnteredTutorialFirstTime;
        internal bool? VisitedDirtmouth;
        internal bool? VisitedCrossroads;
        internal bool? OpenedTown;
        internal bool? OpenedCrossroads;
        internal List<string> ScenesVisited;

        internal static bool TryParse(JObject value, out PlayerCheckpoint checkpoint)
        {
            checkpoint = null;
            try
            {
                if (value == null) return false;
                var sceneName = value.Value<string>("sceneName");
                var parsed = new PlayerCheckpoint {
                    SceneName = sceneName,
                    EntryGateName = value.Value<string>("entryGateName"),
                    HeroX = value.Value<double>("heroX"),
                    HeroY = value.Value<double>("heroY"),
                    HeroZ = value.Value<double>("heroZ"),
                    VelocityX = value.Value<double>("velocityX"),
                    VelocityY = value.Value<double>("velocityY"),
                    FacingRight = value.Value<bool>("facingRight"),
                    Grounded = value.Value<bool>("grounded"),
                    CameraX = value.Value<double>("cameraX"),
                    CameraY = value.Value<double>("cameraY"),
                    CameraZ = value.Value<double>("cameraZ"),
                    CameraTargetX = value.Value<double>("cameraTargetX"),
                    CameraTargetY = value.Value<double>("cameraTargetY"),
                    CameraTargetZ = value.Value<double>("cameraTargetZ"),
                    IsFirstGame = value.Value<bool?>("isFirstGame"),
                    EnteredTutorialFirstTime = value.Value<bool?>("enteredTutorialFirstTime"),
                    VisitedDirtmouth = value.Value<bool?>("visitedDirtmouth"),
                    VisitedCrossroads = value.Value<bool?>("visitedCrossroads"),
                    OpenedTown = value.Value<bool?>("openedTown"),
                    OpenedCrossroads = value.Value<bool?>("openedCrossroads")
                };
                var enemies = value["enemies"] as JArray;
                if (enemies != null) {
                    foreach (var enemyValue in enemies) {
                        EnemyCheckpoint enemy;
                        if (!EnemyCheckpoint.TryParse(enemyValue as JObject, out enemy)) return false;
                        parsed.Enemies.Add(enemy);
                    }
                }
                var scenesVisited = value["scenesVisited"] as JArray;
                if (scenesVisited != null) {
                    if (scenesVisited.Count > 256) return false;
                    parsed.ScenesVisited = new List<string>();
                    foreach (var sceneValue in scenesVisited) {
                        var visitedScene = sceneValue.Value<string>();
                        if (String.IsNullOrWhiteSpace(visitedScene)
                            || visitedScene.Length > 128) return false;
                        parsed.ScenesVisited.Add(visitedScene);
                    }
                }
                if (String.IsNullOrWhiteSpace(sceneName)
                    || (parsed.EntryGateName != null
                        && (String.IsNullOrWhiteSpace(parsed.EntryGateName)
                            || parsed.EntryGateName.Length > 128))
                    || !FiniteCoordinate(parsed.HeroX) || !FiniteCoordinate(parsed.HeroY)
                    || !FiniteCoordinate(parsed.HeroZ) || !FiniteCoordinate(parsed.VelocityX)
                    || !FiniteCoordinate(parsed.VelocityY) || !FiniteCoordinate(parsed.CameraX)
                    || !FiniteCoordinate(parsed.CameraY) || !FiniteCoordinate(parsed.CameraZ)
                    || !FiniteCoordinate(parsed.CameraTargetX) || !FiniteCoordinate(parsed.CameraTargetY)
                    || !FiniteCoordinate(parsed.CameraTargetZ)) return false;
                checkpoint = parsed;
                return true;
            }
            catch { return false; }
        }

        internal JObject ToJson()
        {
            var enemies = new JArray();
            foreach (var enemy in Enemies) enemies.Add(enemy.ToJson());
            var value = new JObject {
                ["sceneName"] = SceneName,
                ["heroX"] = HeroX,
                ["heroY"] = HeroY,
                ["heroZ"] = HeroZ,
                ["velocityX"] = VelocityX,
                ["velocityY"] = VelocityY,
                ["facingRight"] = FacingRight,
                ["grounded"] = Grounded,
                ["cameraX"] = CameraX,
                ["cameraY"] = CameraY,
                ["cameraZ"] = CameraZ,
                ["cameraTargetX"] = CameraTargetX,
                ["cameraTargetY"] = CameraTargetY,
                ["cameraTargetZ"] = CameraTargetZ,
                ["enemies"] = enemies
            };
            if (!String.IsNullOrWhiteSpace(EntryGateName))
                value["entryGateName"] = EntryGateName;
            if (IsFirstGame.HasValue) value["isFirstGame"] = IsFirstGame.Value;
            if (EnteredTutorialFirstTime.HasValue)
                value["enteredTutorialFirstTime"] = EnteredTutorialFirstTime.Value;
            if (VisitedDirtmouth.HasValue) value["visitedDirtmouth"] = VisitedDirtmouth.Value;
            if (VisitedCrossroads.HasValue) value["visitedCrossroads"] = VisitedCrossroads.Value;
            if (OpenedTown.HasValue) value["openedTown"] = OpenedTown.Value;
            if (OpenedCrossroads.HasValue) value["openedCrossroads"] = OpenedCrossroads.Value;
            if (ScenesVisited != null) {
                var scenesVisited = new JArray();
                foreach (var visitedScene in ScenesVisited) scenesVisited.Add(visitedScene);
                value["scenesVisited"] = scenesVisited;
            }
            return value;
        }

        private static bool FiniteCoordinate(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value) && Math.Abs(value) <= 1000000;
        }
    }

    internal sealed class EnemyCheckpoint
    {
        internal string Name;
        internal double X;
        internal double Y;
        internal double Z;
        internal int HP;
        internal bool Active;

        internal static bool TryParse(JObject value, out EnemyCheckpoint enemy)
        {
            enemy = null;
            try {
                if (value == null) return false;
                var parsed = new EnemyCheckpoint {
                    Name = value.Value<string>("name"),
                    X = value.Value<double>("x"),
                    Y = value.Value<double>("y"),
                    Z = value.Value<double>("z"),
                    HP = value.Value<int>("hp"),
                    Active = value.Value<bool>("active")
                };
                if (String.IsNullOrWhiteSpace(parsed.Name) || parsed.HP < 1
                    || !Finite(parsed.X) || !Finite(parsed.Y) || !Finite(parsed.Z)) return false;
                enemy = parsed;
                return true;
            }
            catch { return false; }
        }

        internal JObject ToJson()
        {
            return new JObject {
                ["name"] = Name,
                ["x"] = X,
                ["y"] = Y,
                ["z"] = Z,
                ["hp"] = HP,
                ["active"] = Active
            };
        }

        private static bool Finite(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value) && Math.Abs(value) <= 1000000;
        }
    }

    internal sealed class PlayerTestState
    {
        internal int MaxHealth;
        internal int Health;
        internal int LifebloodSeed;
        internal int Mana;
        internal int ExtraManaSlots;
        internal int Geo;
        internal bool Invincible;

        internal static bool TryParse(JObject value, out PlayerTestState state)
        {
            state = null;
            try {
                if (value == null) return false;
                var parsed = new PlayerTestState {
                    MaxHealth = value.Value<int>("maxHealth"),
                    Health = value.Value<int>("health"),
                    LifebloodSeed = value.Value<int>("lifebloodSeed"),
                    Mana = value.Value<int>("mana"),
                    ExtraManaSlots = value.Value<int>("extraManaSlots"),
                    Geo = value.Value<int>("geo"),
                    Invincible = value.Value<bool?>("invincible") ?? true
                };
                if (parsed.MaxHealth < 1 || parsed.MaxHealth > 9
                    || parsed.Health < 1 || parsed.Health > parsed.MaxHealth
                    || parsed.LifebloodSeed < 0 || parsed.LifebloodSeed > 9
                    || parsed.ExtraManaSlots < 0 || parsed.ExtraManaSlots > 3
                    || parsed.Mana < 0
                    || parsed.Mana > 99 + parsed.ExtraManaSlots * 33
                    || parsed.Geo < 0 || parsed.Geo > 9999999) return false;
                state = parsed;
                return true;
            }
            catch { return false; }
        }

        internal JObject ToJson()
        {
            return new JObject {
                ["maxHealth"] = MaxHealth,
                ["health"] = Health,
                ["lifebloodSeed"] = LifebloodSeed,
                ["mana"] = Mana,
                ["extraManaSlots"] = ExtraManaSlots,
                ["geo"] = Geo,
                ["invincible"] = Invincible
            };
        }
    }

    internal enum PlayerOpsKind { Query, Apply, RestoreEnemies }

    internal sealed class PlayerOpsRequest
    {
        internal string Session;
        internal string CommandID;
        internal PlayerOpsKind Kind;
        internal PlayerTestState State;

        internal static bool TryParse(string json, out PlayerOpsRequest request)
        {
            request = null;
            try {
                var value = JObject.Parse(json);
                if (value == null || value.Value<int>("version") != ReceiverControlProtocol.Version
                    || value.Value<string>("type") != "playerOps") return false;
                var session = value.Value<string>("sessionID");
                var commandID = value.Value<string>("commandID");
                var operation = value.Value<string>("operation");
                PlayerOpsKind kind;
                if (operation == "query") kind = PlayerOpsKind.Query;
                else if (operation == "apply") kind = PlayerOpsKind.Apply;
                else if (operation == "restoreEnemies") kind = PlayerOpsKind.RestoreEnemies;
                else return false;
                PlayerTestState state = null;
                if (kind == PlayerOpsKind.Apply
                    && !PlayerTestState.TryParse(value["state"] as JObject, out state)) return false;
                if (String.IsNullOrWhiteSpace(session) || String.IsNullOrWhiteSpace(commandID)) return false;
                request = new PlayerOpsRequest {
                    Session = session, CommandID = commandID, Kind = kind, State = state
                };
                return true;
            }
            catch { return false; }
        }

        internal static string Ack(
            PlayerOpsRequest request,
            bool accepted,
            PlayerTestState state,
            int? enemiesRestored,
            string failure)
        {
            var value = new JObject {
                ["version"] = ReceiverControlProtocol.Version,
                ["type"] = "playerOpsAck",
                ["sessionID"] = request.Session,
                ["commandID"] = request.CommandID,
                ["accepted"] = accepted
            };
            if (state != null) value["state"] = state.ToJson();
            if (enemiesRestored.HasValue) value["enemiesRestored"] = enemiesRestored.Value;
            if (!String.IsNullOrWhiteSpace(failure)) value["failure"] = failure;
            return value.ToString(Formatting.None);
        }
    }

    /// Development-only truth sampled immediately before the gameplay camera renders.
    /// Vision records this beside its independent visual solve; production
    /// tracking never needs the modified game to provide it.
    internal sealed class GroundTruthTelemetry
    {
        internal long Sequence;
        internal long UnityFrame;
        internal double UnityRealtime;
        internal string SceneName;
        internal bool HeroAvailable;
        internal double HeroX;
        internal double HeroY;
        internal double HeroZ;
        internal double VelocityX;
        internal double VelocityY;
        internal bool FacingRight;
        internal bool Grounded;
        internal bool CameraAvailable;
        internal double CameraX;
        internal double CameraY;
        internal double CameraZ;
        internal double CameraTargetX;
        internal double CameraTargetY;
        internal double CameraTargetZ;
        internal double OrthographicSize;
        internal double PixelsPerWorldUnitX;
        internal double PixelsPerWorldUnitY;
        internal double HeroScreenX;
        internal double HeroScreenY;
        internal int ProjectionPixelWidth;
        internal int ProjectionPixelHeight;
        internal int ScreenWidth;
        internal int ScreenHeight;

        internal string ToJson(string session)
        {
            return new JObject {
                ["version"] = ReceiverControlProtocol.Version,
                ["type"] = "groundTruth",
                ["sessionID"] = session,
                ["sequence"] = Sequence,
                ["unityFrame"] = UnityFrame,
                ["unityRealtime"] = UnityRealtime,
                ["sceneName"] = SceneName ?? "",
                ["heroAvailable"] = HeroAvailable,
                ["heroX"] = HeroX,
                ["heroY"] = HeroY,
                ["heroZ"] = HeroZ,
                ["velocityX"] = VelocityX,
                ["velocityY"] = VelocityY,
                ["facingRight"] = FacingRight,
                ["grounded"] = Grounded,
                ["cameraAvailable"] = CameraAvailable,
                ["cameraX"] = CameraX,
                ["cameraY"] = CameraY,
                ["cameraZ"] = CameraZ,
                ["cameraTargetX"] = CameraTargetX,
                ["cameraTargetY"] = CameraTargetY,
                ["cameraTargetZ"] = CameraTargetZ,
                ["orthographicSize"] = OrthographicSize,
                ["pixelsPerWorldUnitX"] = PixelsPerWorldUnitX,
                ["pixelsPerWorldUnitY"] = PixelsPerWorldUnitY,
                ["heroScreenX"] = HeroScreenX,
                ["heroScreenY"] = HeroScreenY,
                ["projectionPixelWidth"] = ProjectionPixelWidth,
                ["projectionPixelHeight"] = ProjectionPixelHeight,
                ["screenWidth"] = ScreenWidth,
                ["screenHeight"] = ScreenHeight
            }.ToString(Formatting.None);
        }
    }

    /// A lightweight playback correction. Unlike checkpoint restoration this
    /// does not reset the camera: the normal game camera follows the recorded
    /// Knight trajectory and therefore exercises the same tracking path as the
    /// original run.
    internal sealed class PlayerPoseRequest
    {
        internal string Session;
        internal long Sequence;
        internal string SceneName;
        internal double HeroX;
        internal double HeroY;
        internal double HeroZ;
        internal double VelocityX;
        internal double VelocityY;
        internal bool FacingRight;
        internal bool Grounded;

        internal static bool TryParse(string json, out PlayerPoseRequest request)
        {
            request = null;
            try
            {
                var value = JObject.Parse(json);
                if (value == null || value.Value<int>("version") != ReceiverControlProtocol.Version
                    || value.Value<string>("type") != "playerPose") return false;
                var session = value.Value<string>("sessionID");
                var sceneName = value.Value<string>("sceneName");
                var parsed = new PlayerPoseRequest {
                    Session = session,
                    Sequence = value.Value<long>("sequence"),
                    SceneName = sceneName,
                    HeroX = value.Value<double>("heroX"),
                    HeroY = value.Value<double>("heroY"),
                    HeroZ = value.Value<double>("heroZ"),
                    VelocityX = value.Value<double>("velocityX"),
                    VelocityY = value.Value<double>("velocityY"),
                    FacingRight = value.Value<bool>("facingRight"),
                    Grounded = value.Value<bool>("grounded")
                };
                if (String.IsNullOrWhiteSpace(session) || String.IsNullOrWhiteSpace(sceneName)
                    || parsed.Sequence < 0
                    || !FiniteCoordinate(parsed.HeroX) || !FiniteCoordinate(parsed.HeroY)
                    || !FiniteCoordinate(parsed.HeroZ) || !FiniteCoordinate(parsed.VelocityX)
                    || !FiniteCoordinate(parsed.VelocityY)) return false;
                request = parsed;
                return true;
            }
            catch { return false; }
        }

        private static bool FiniteCoordinate(double value)
        {
            return !Double.IsNaN(value) && !Double.IsInfinity(value) && Math.Abs(value) <= 1000000;
        }
    }

    internal enum CheckpointCommandKind
    {
        Capture,
        Restore
    }

    internal sealed class CheckpointRequest
    {
        internal string Session;
        internal string CommandID;
        internal CheckpointCommandKind Kind;
        internal PlayerCheckpoint Checkpoint;

        internal static bool TryParse(string json, out CheckpointRequest request)
        {
            request = null;
            try
            {
                var value = JObject.Parse(json);
                if (value == null || value.Value<int>("version") != ReceiverControlProtocol.Version) return false;
                var type = value.Value<string>("type");
                CheckpointCommandKind kind;
                if (type == "captureCheckpoint") kind = CheckpointCommandKind.Capture;
                else if (type == "restoreCheckpoint") kind = CheckpointCommandKind.Restore;
                else return false;
                var session = value.Value<string>("sessionID");
                var commandID = value.Value<string>("commandID");
                if (String.IsNullOrWhiteSpace(session) || String.IsNullOrWhiteSpace(commandID)) return false;
                PlayerCheckpoint checkpoint = null;
                if (kind == CheckpointCommandKind.Restore
                    && !PlayerCheckpoint.TryParse(value["checkpoint"] as JObject, out checkpoint)) return false;
                request = new CheckpointRequest {
                    Session = session,
                    CommandID = commandID,
                    Kind = kind,
                    Checkpoint = checkpoint
                };
                return true;
            }
            catch { return false; }
        }

        internal static string Ack(
            CheckpointRequest request,
            bool accepted,
            PlayerCheckpoint checkpoint,
            string failure)
        {
            var value = new JObject {
                ["version"] = ReceiverControlProtocol.Version,
                ["type"] = request.Kind == CheckpointCommandKind.Capture
                    ? "captureCheckpointAck" : "restoreCheckpointAck",
                ["sessionID"] = request.Session,
                ["commandID"] = request.CommandID,
                ["accepted"] = accepted
            };
            if (checkpoint != null) value["checkpoint"] = checkpoint.ToJson();
            if (!String.IsNullOrWhiteSpace(failure)) value["failure"] = failure;
            return value.ToString(Formatting.None);
        }
    }

}
