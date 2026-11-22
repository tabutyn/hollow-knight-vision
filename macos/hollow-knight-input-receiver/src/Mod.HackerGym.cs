using System;
using System.Collections.Generic;
using System.IO;
using InControl;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace HollowKnightVisionInputReceiver
{
    // Only the child launched by the trainer enters this mode. The normal game
    // keeps its own input device and Vision connection.
    internal sealed partial class ReceiverBehaviour
    {
        private const int GymWidth = 640, GymHeight = 360;
        private string gymDirectory;
        private string gymEpisode;
        private int gymSeed, gymStep, gymFramesLeft, gymArmedFrame, gymTicks, gymHealth, gymDamage, gymRenderHz;
        private float gymActivity, gymGroundedAt;
        private bool gymStarted, gymReady;
        private float gymNextSkip;
        private VisionButtons gymButtons;
        private bool gymVisual;
        private RenderTexture gymTarget;
        private Texture2D gymPixels;
        private readonly Dictionary<int, int> gymRockHits = new Dictionary<int, int>();
        private int gymDepositHits, gymDepositsMined;

        private bool InitializeGymWorker()
        {
            gymDirectory = Environment.GetEnvironmentVariable("HKV_GYM_DIRECTORY");
            if (String.IsNullOrEmpty(gymDirectory)) { gymDirectory = null; return false; }
            var config = JObject.Parse(File.ReadAllText(Path.Combine(gymDirectory, "episode.json")));
            gymEpisode = Guid.Parse(config.Value<string>("episode")).ToString();
            gymSeed = config.Value<int>("seed");
            gymVisual = config.Value<string>("domain") == "gameplay";
            gymRenderHz = config.Value<int?>("renderHz") ?? 60;
            if (gymRenderHz != 60 && gymRenderHz != 120)
                throw new InvalidDataException("Unsupported Gym frame rate.");
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
            InputManager.SuspendInBackground = false;
            device = new VisionInputDevice();
            InputManager.AttachDevice(device);
            gymActivity = Time.realtimeSinceStartup;
            return true;
        }

        private void TickGymWorker()
        {
            try {
                var gm = GameManager.instance;
                // Neither an idle client nor a failed training process may leave input held.
                if (Time.realtimeSinceStartup - gymActivity > (gymReady ? 30 : 120)) {
                    device.SetState(null, -1, false, VisionButtons.None);
                    Application.Quit(); return;
                }
                if (gm == null || gm.gameConfig == null) return;
                gm.profileID = -1;
                gm.gameConfig.disableSaveGame = true;
                if (!gymStarted) {
                    if (gm.gameState != GlobalEnums.GameState.MAIN_MENU || gm.ui == null) return;
                    UnityEngine.Random.InitState(gymSeed);
                    gm.StartNewGame(false, false);
                    gymStarted = true;
                    return;
                }
                if (gm.sceneName == "Opening_Sequence" && Time.unscaledTime >= gymNextSkip) {
                    gymNextSkip = Time.unscaledTime + 1f;
                    gm.SkipCutscene();
                    return;
                }
                if (!gymReady) {
                    var hero = HeroController.SilentInstance;
                    if (gm.sceneName != "Tutorial_01" || gm.gameState != GlobalEnums.GameState.PLAYING
                        || hero == null || hero.cState == null || !hero.cState.onGround || !hero.acceptingInput) {
                        gymGroundedAt = 0; return;
                    }
                    if (gymGroundedAt == 0) gymGroundedAt = Time.time;
                    if (Time.time - gymGroundedAt < 0.35f) return;
                    gymHealth = CurrentGymHealth();
                    if (gymHealth <= 0) return;
                    gymReady = true;
                    Time.captureDeltaTime = 1f / 60f;
                    Application.targetFrameRate = gymRenderHz;
                    Time.timeScale = 0;
                    gymActivity = Time.realtimeSinceStartup;
                    WriteGymObservation();
                    return;
                }
                if (gymFramesLeft > 0) return;
                var path = Path.Combine(gymDirectory, "request.json");
                if (!File.Exists(path)) return;
                var request = JObject.Parse(File.ReadAllText(path));
                File.Delete(path);
                if (request.Value<string>("episode") != gymEpisode || request.Value<int>("step") != gymStep + 1)
                    throw new InvalidDataException("Out-of-sequence gym action.");
                gymStep++;
                gymActivity = Time.realtimeSinceStartup;
                gymFramesLeft = request.Value<int>("ticks");
                var buttons = request.Value<int>("buttons");
                const VisionButtons allowed = VisionButtons.Left | VisionButtons.Right | VisionButtons.Up |
                    VisionButtons.Down | VisionButtons.ActionX | VisionButtons.ActionZ;
                if (gymFramesLeft < 1 || gymFramesLeft > 30 || (buttons & ~(int)allowed) != 0)
                    throw new InvalidDataException("Invalid gym input.");
                gymButtons = InputSnapshot.Normalize((VisionButtons)buttons);
                device.SetState(null, -1, true, gymButtons);
                gymArmedFrame = Time.frameCount;
                Time.timeScale = 1;
            } catch (Exception ex) { FailGymWorker(ex); }
        }

        private static int CurrentGymHealth()
        {
            var data = PlayerData.instance;
            return data == null ? 0 : Math.Max(0, data.health) + Math.Max(0, data.healthBlue);
        }

        private void CompleteGymTick()
        {
            if (!gymReady || gymFramesLeft <= 0 || Time.frameCount <= gymArmedFrame) return;
            try {
                int hp = CurrentGymHealth();
                gymDamage += Math.Max(0, gymHealth - hp);
                gymHealth = hp;
                gymTicks++;
                if (--gymFramesLeft > 0 && hp > 0 && GameManager.instance.sceneName == "Tutorial_01") return;
                gymFramesLeft = 0;
                Time.timeScale = 0;
                WriteGymObservation();
            } catch (Exception ex) { FailGymWorker(ex); }
        }

        private void WriteGymObservation()
        {
            var gm = GameManager.instance;
            var observation = JObject.Parse(CaptureGymTelemetry().ToJson(gymEpisode));
            observation["episode"] = gymEpisode;
            observation["step"] = gymStep;
            observation["ticks"] = gymTicks;
            observation["simulationHz"] = Mathf.RoundToInt(1f / Time.captureDeltaTime);
            observation["renderHz"] = Application.targetFrameRate;
            observation["health"] = gymHealth;
            observation["damage"] = gymDamage;
            RecordGymGeo(observation, gm.sceneName);
            observation["invincible"] = invincibilityEnabled;
            observation["noSave"] = gm.profileID < 0 && gm.gameConfig.disableSaveGame;
            observation["gameState"] = gm.gameState.ToString();
            if (gymVisual) WriteGymPixels(observation);
            var next = Path.Combine(gymDirectory, "reply.tmp");
            File.WriteAllText(next, observation.ToString(Formatting.None));
            File.Move(next, Path.Combine(gymDirectory, "reply.json"));
        }

        private void RecordGymGeo(JObject observation, string scene)
        {
            foreach (var rock in telemetryGeoRocks) {
                if (rock == null || rock.gameObject.scene.name != scene) continue;
                var remaining = RemainingGeoRockHits(rock);
                if (!remaining.HasValue || remaining.Value < 0) continue;
                int id = rock.GetInstanceID(), previous;
                if (gymRockHits.TryGetValue(id, out previous) && remaining.Value < previous) {
                    gymDepositHits += previous - remaining.Value;
                    if (remaining.Value == 0) gymDepositsMined++;
                }
                gymRockHits[id] = remaining.Value;
            }
            observation["geo"] = PlayerData.instance == null ? 0 : PlayerData.instance.geo;
            observation["depositHits"] = gymDepositHits;
            observation["depositsMined"] = gymDepositsMined;
        }

        private GroundTruthTelemetry CaptureGymTelemetry()
        {
            var host = GameCameras.instance;
            var camera = host == null || host.tk2dCam == null ? null : host.tk2dCam.GetComponent<Camera>();
            if (camera == null || camera.pixelHeight == 0) return CaptureGroundTruth();
            var rect = camera.rect;
            var projection = camera.projectionMatrix;
            float aspect = (float)camera.pixelWidth / camera.pixelHeight;
            float heightMultiplier = Mathf.Max(1f, ForceCameraAspect.CurrentMainCamHeightMult);
            try {
                // WorldToScreenPoint includes the desktop letterbox offset, while pixelHeight
                // excludes it. Set both to the bot's 16:9 image basis before projecting actors.
                camera.pixelRect = new Rect(0, 0, GymWidth, GymHeight);
                camera.projectionMatrix = Matrix4x4.Scale(new Vector3(
                    heightMultiplier * aspect * GymHeight / GymWidth, heightMultiplier, 1)) * projection;
                var sample = CaptureGroundTruth();
                sample.ScreenWidth = GymWidth;
                sample.ScreenHeight = GymHeight;
                return sample;
            } finally {
                camera.rect = rect;
                camera.projectionMatrix = projection;
            }
        }

        // Batch workers have no desktop window. Render the same visible camera stack into
        // one RGB frame while simulation is paused. Reward telemetry travels separately.
        private void WriteGymPixels(JObject observation)
        {
            const int width = GymWidth, height = GymHeight;
            if (gymTarget == null) {
                gymTarget = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                gymTarget.Create();
                gymPixels = new Texture2D(width, height, TextureFormat.RGB24, false);
            }
            var previous = RenderTexture.active;
            try {
                RenderTexture.active = gymTarget;
                GL.Clear(true, true, Color.black);
                var cameras = Camera.allCameras;
                Array.Sort(cameras, (a, b) => a.depth.CompareTo(b.depth));
                foreach (var camera in cameras) {
                    if (!camera.isActiveAndEnabled || camera.targetTexture != null || camera.targetDisplay != 0) continue;
                    RenderGymCamera(camera, width, height);
                }
                RenderTexture.active = gymTarget;
                gymPixels.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                File.WriteAllBytes(Path.Combine(gymDirectory, "frame.rgb"), gymPixels.GetRawTextureData());
                observation["pixelWidth"] = width;
                observation["pixelHeight"] = height;
            } finally { RenderTexture.active = previous; }
        }

        // Unity's batch display is generally 4:3 even when -screen-width and -screen-height
        // are supplied. Hollow Knight letterboxes it and widens its vertical field of view.
        // Use the game's 16:9 view for this render, including its top-left HUD anchor.
        private void RenderGymCamera(Camera camera, int width, int height)
        {
            var host = GameCameras.instance;
            bool hud = host != null && camera == host.hudCamera;
            var tk = host == null ? null : host.tk2dCam;
            bool world = tk != null && camera == tk.GetComponent<Camera>();
            var rect = camera.rect;
            var aspect = camera.aspect;
            var size = camera.orthographicSize;
            var worldProjection = camera.projectionMatrix;
            var tkRect = world ? tk.CameraSettings.rect : rect;
            var tkFov = world ? tk.CameraSettings.fieldOfView : 0;
            float heightMultiplier = Mathf.Max(1f, ForceCameraAspect.CurrentMainCamHeightMult);
            try {
                camera.targetTexture = gymTarget;
                if (hud || world) {
                    camera.rect = new Rect(0, 0, 1, 1);
                    camera.aspect = (float)width / height;
                    if (camera.orthographic) {
                        camera.orthographicSize = size / heightMultiplier;
                        if (hud) {
                            // The HUD is parented to its camera. Cancel the extra top-left
                            // anchor offset in clip space for the duration of this render.
                            var projection = camera.projectionMatrix;
                            projection.m13 -= heightMultiplier - 1f;
                            camera.projectionMatrix = projection;
                        }
                    }
                    if (world) {
                        tk.CameraSettings.rect = camera.rect;
                        tk.CameraSettings.fieldOfView = tkFov / heightMultiplier;
                    }
                }
                camera.Render();
            } finally {
                camera.targetTexture = null;
                if (hud || world) {
                    camera.rect = rect;
                    camera.aspect = aspect;
                    if (camera.orthographic) camera.orthographicSize = size;
                    if (world) {
                        tk.CameraSettings.rect = tkRect;
                        tk.CameraSettings.fieldOfView = tkFov;
                        camera.projectionMatrix = worldProjection;
                    }
                    if (hud) camera.ResetProjectionMatrix();
                }
            }
        }

        private void FailGymWorker(Exception ex)
        {
            device.SetState(null, -1, false, VisionButtons.None);
            File.WriteAllText(Path.Combine(gymDirectory, "error.txt"), ex.ToString());
            Application.Quit();
        }
    }
}
