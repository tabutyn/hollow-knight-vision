using System;
using System.Collections.Generic;
using System.Diagnostics;
using InControl;
using Modding;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace HollowKnightVisionInputReceiver
{
    // API loader entry point. It owns only the local receiver and virtual device.
    public sealed class HollowKnightVisionInputReceiverMod : Mod
    {
        public override string GetVersion() { return "0.9.1"; }

        public override void Initialize(Dictionary<string, Dictionary<string, GameObject>> preloadedObjects)
        {
            var host = new GameObject("HollowKnightVisionInputReceiver");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<ReceiverBehaviour>();
            Log("Hollow Knight Vision input receiver listening on 127.0.0.1:36752");
        }
    }

    // InControlManager has no explicit execution-order attribute in the pinned
    // 1.5.12620 assembly, so its normal MonoBehaviour Update order is zero.
    // Drain the socket queue first, allowing the virtual device to update and
    // commit during that same InControl tick instead of the following one.
    [DefaultExecutionOrder(-1000)]
    internal sealed class ReceiverBehaviour : MonoBehaviour
    {
        private LocalInputServer server;
        private VisionInputDevice device;
        private HeroActions boundActions;
        private DeviceBindingSource inventoryBinding;
        private DeviceBindingSource pauseMenuBinding;
        private VisionButtons applied = VisionButtons.None;
        private VisionPointerInput pointerInput;
        private InControl.HollowKnightInputModule pointerModule;
        private PointerRequest pendingPointerClick;
        private bool cursorVisibilityOwned;
        private bool cursorVisibilityBeforePointer;
        private string lastHoverTarget;
        private float lastHoverDiagnosticAt;
        private bool appliedEnabled;
        private bool priorRunInBackground;
        private bool priorSuspendInBackground;
        private int priorVSyncCount;
        private int priorTargetFrameRate;
        private bool pacingConfigured;
        private bool visionOwnsPause;
        private float timeScaleBeforeVisionPause;
        private long pauseLeaseDeadline;
        private string pauseSession;
        private long groundTruthSequence;
        private int lastTruthFrame = -1;
        private Texture2D frameMarkerTexture;
        private readonly Color32[] frameMarkerPixels = new Color32[80];
        private readonly HashSet<int> ignoredEnemyColliderIDs = new HashSet<int>();
        private int collisionHeroInstanceID;
        private float nextEnemyCollisionScanAt;
        private bool damageHooksInstalled;
        private bool invincibilityEnabled = true;
        private List<EnemyCheckpoint> lastCapturedEnemies = new List<EnemyCheckpoint>();
        private PlayerTestState pendingPlayerOpsReconciliation;
        private float playerOpsDisplayRefreshAt;
        private float playerOpsReconcileUntil;
        private bool playerOpsDisplayRefreshed;
        private int playerOpsBlueHealthOverride;
        private PlayerCheckpoint pendingEnemyResetCheckpoint;
        private float pendingEnemyResetRestoreAt;
        private CheckpointRequest pendingSceneRestoreRequest;
        private PlayerCheckpoint pendingSceneRestoreCheckpoint;
        private float pendingSceneRestoreDeadline;

        private void Awake()
        {
            priorRunInBackground = Application.runInBackground;
            priorSuspendInBackground = InputManager.SuspendInBackground;
            priorVSyncCount = QualitySettings.vSyncCount;
            priorTargetFrameRate = Application.targetFrameRate;
            Application.runInBackground = true;
            InputManager.SuspendInBackground = false;
            // A fully covered macOS game can otherwise be composited at a very
            // low cadence.  Its input device is updated from the game tick, so
            // ask Unity for an explicit 60 Hz tick while this removable mod is
            // alive.  The player's values are restored in StopReceiver().
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
            pacingConfigured = true;
            device = new VisionInputDevice();
            device.Committed = OnDeviceCommitted;
            InputManager.AttachDevice(device);
            pointerInput = gameObject.AddComponent<VisionPointerInput>();
            server = new LocalInputServer();
            server.Start();
            Camera.onPreRender += OnCameraPreRender;
            ModHooks.TakeHealthHook += PreventHealthLoss;
            ModHooks.TakeDamageHook += PreventDamage;
            ModHooks.BlueHealthHook += ProvidePlayerOpsBlueHealth;
            damageHooksInstalled = true;
        }

        private void Update()
        {
            if (pointerInput != null) pointerInput.BeginFrame();
            EnsureShortcutBindings();
            server.ExpireIfStale();
            InputSnapshot snapshot;
            if (server.TryTakeSnapshot(out snapshot)) {
                appliedEnabled = snapshot.Enabled;
                applied = snapshot.Enabled ? InputSnapshot.Normalize(snapshot.Buttons) : VisionButtons.None;
                device.SetState(snapshot.Session, snapshot.Sequence, appliedEnabled, applied);
            }
            PauseRequest pauseRequest;
            while (server.TryTakePauseRequest(out pauseRequest)) ApplyPauseRequest(pauseRequest);
            PointerRequest pointerRequest;
            while (server.TryTakePointerRequest(out pointerRequest)) ApplyPointerRequest(pointerRequest);
            CheckpointRequest checkpointRequest;
            while (server.TryTakeCheckpointRequest(out checkpointRequest)) ApplyCheckpointRequest(checkpointRequest);
            MaintainPendingSceneRestore();
            PlayerOpsRequest playerOpsRequest;
            while (server.TryTakePlayerOpsRequest(out playerOpsRequest)) ApplyPlayerOpsRequest(playerOpsRequest);
            MaintainPlayerOpsReconciliation();
            MaintainPendingEnemyReset();
            MaintainEnemyCollisionBypass();
            if (cursorVisibilityOwned) Cursor.visible = true;
            if (visionOwnsPause && Stopwatch.GetTimestamp() >= pauseLeaseDeadline) ReleaseVisionPause();
        }

        private void LateUpdate()
        {
            MaintainPlayerOpsReconciliation();
            PlayerPoseRequest poseRequest;
            if (server.TryTakePlayerPoseRequest(out poseRequest)) ApplyPlayerPose(poseRequest);
            var request = pendingPointerClick;
            pendingPointerClick = null;
            if (request != null) DispatchPointerClick(request);
        }

        // The receiver must run input before InControl, but its early
        // LateUpdate precedes CameraController. Sample at rendering instead.
        private void OnCameraPreRender(Camera renderingCamera)
        {
            var cameras = GameCameras.instance;
            if (cameras == null) return;
            var gameplayCamera = cameras.tk2dCam == null ? null
                : cameras.tk2dCam.GetComponent<Camera>();
            if (gameplayCamera == null && cameras.cameraController != null)
                gameplayCamera = cameras.cameraController.cam;
            if (renderingCamera != gameplayCamera || lastTruthFrame == Time.frameCount) return;
            lastTruthFrame = Time.frameCount;
            QueueGroundTruthTelemetry();
        }

        // Diagnostic only. The barcode occupies the excluded six-pixel top
        // border at 640x360. Vision records it but never uses it for tracking.
        private void OnGUI()
        {
            if (server == null || !server.RenderFrameMarker
                || Event.current.type != EventType.Repaint) return;
            if (frameMarkerTexture == null) {
                frameMarkerTexture = new Texture2D(40, 2, TextureFormat.RGBA32, false);
                frameMarkerTexture.filterMode = FilterMode.Point;
                frameMarkerTexture.wrapMode = TextureWrapMode.Clamp;
            }
            var frame = Time.frameCount & 0xFFFFFF;
            var checksum = 0x5A ^ (frame & 255) ^ ((frame >> 8) & 255) ^ ((frame >> 16) & 255);
            ulong word = 0xD3UL | ((ulong)frame << 8) | ((ulong)checksum << 32);
            for (var bit = 0; bit < 40; bit++) {
                var value = ((word >> bit) & 1) != 0 ? (byte)255 : (byte)0;
                // Texture coordinates start at bottom; GUI starts at top.
                frameMarkerPixels[bit] = new Color32((byte)(255 - value), (byte)(255 - value), (byte)(255 - value), 255);
                frameMarkerPixels[40 + bit] = new Color32(value, value, value, 255);
            }
            frameMarkerTexture.SetPixels32(frameMarkerPixels);
            frameMarkerTexture.Apply(false, false);
            var color = GUI.color;
            var depth = GUI.depth;
            GUI.color = Color.white;
            GUI.depth = -10000;
            GUI.DrawTexture(new Rect(0, 0, Screen.width * 120f / 640f,
                Screen.height * 6f / 360f), frameMarkerTexture, ScaleMode.StretchToFill, false);
            GUI.color = color;
            GUI.depth = depth;
        }

        private int PreventHealthLoss(int damage)
        {
            return invincibilityEnabled ? 0 : damage;
        }

        private int PreventDamage(ref int hazardType, int damage)
        {
            return invincibilityEnabled ? 0 : damage;
        }

        private int ProvidePlayerOpsBlueHealth() { return playerOpsBlueHealthOverride; }

        /// Keep the Knight's own terrain collider intact, but disable its
        /// collision pairs with enemy bodies and DamageHero triggers. Nail and
        /// spell hitboxes are separate objects, so attacks still reach enemies.
        private void MaintainEnemyCollisionBypass()
        {
            if (Time.unscaledTime < nextEnemyCollisionScanAt) return;
            nextEnemyCollisionScanAt = Time.unscaledTime + 0.25f;
            var hero = HeroController.SilentInstance;
            if (hero == null || !hero.gameObject.activeInHierarchy) return;
            var heroID = hero.GetInstanceID();
            if (heroID != collisionHeroInstanceID) {
                collisionHeroInstanceID = heroID;
                ignoredEnemyColliderIDs.Clear();
            }
            var heroColliders = hero.GetComponents<Collider2D>();
            if (heroColliders == null || heroColliders.Length == 0) return;

            var shouldIgnore = invincibilityEnabled;
            var enemies = UnityEngine.Object.FindObjectsByType<HealthManager>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (var index = 0; index < enemies.Length; index++) {
                SetEnemyCollisionsIgnored(hero.transform, heroColliders,
                    enemies[index].GetComponentsInChildren<Collider2D>(true),
                    shouldIgnore);
            }
            var damageSources = UnityEngine.Object.FindObjectsByType<DamageHero>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (var index = 0; index < damageSources.Length; index++) {
                SetEnemyCollisionsIgnored(hero.transform, heroColliders,
                    damageSources[index].GetComponentsInChildren<Collider2D>(true),
                    shouldIgnore);
                var ownCollider = damageSources[index].GetComponent<Collider2D>();
                if (ownCollider != null) SetEnemyCollisionsIgnored(
                    hero.transform, heroColliders, new[] { ownCollider },
                    shouldIgnore);
            }
            if (!shouldIgnore) ignoredEnemyColliderIDs.Clear();
        }

        private void SetEnemyCollisionsIgnored(
            Transform heroTransform,
            Collider2D[] heroColliders,
            Collider2D[] enemyColliders,
            bool ignored)
        {
            for (var enemyIndex = 0; enemyIndex < enemyColliders.Length; enemyIndex++) {
                var enemyCollider = enemyColliders[enemyIndex];
                if (enemyCollider == null || enemyCollider.transform.IsChildOf(heroTransform)) continue;
                var colliderID = enemyCollider.GetInstanceID();
                if (ignored) {
                    if (!ignoredEnemyColliderIDs.Add(colliderID)) continue;
                } else if (!ignoredEnemyColliderIDs.Remove(colliderID)) {
                    continue;
                }
                for (var heroIndex = 0; heroIndex < heroColliders.Length; heroIndex++) {
                    var heroCollider = heroColliders[heroIndex];
                    if (heroCollider != null) {
                        Physics2D.IgnoreCollision(heroCollider, enemyCollider, ignored);
                    }
                }
            }
        }

        private static void ApplyPlayerPose(PlayerPoseRequest request)
        {
            var hero = HeroController.SilentInstance;
            var gameManager = GameManager.instance;
            if (hero == null || !hero.gameObject.activeInHierarchy || gameManager == null
                || !String.Equals(gameManager.sceneName, request.SceneName,
                    StringComparison.Ordinal)) return;
            var body = hero.GetComponent<Rigidbody2D>();
            if (body == null) return;

            var position = new Vector3(
                (float)request.HeroX,
                (float)request.HeroY,
                (float)request.HeroZ);
            var velocity = new Vector2(
                (float)request.VelocityX,
                (float)request.VelocityY);
            hero.transform.position = position;
            body.position = new Vector2(position.x, position.y);
            // Replay poses arrive after the physics step so the camera can
            // observe the recorded route. Publish the transform immediately;
            // otherwise narrow TransitionPoint triggers can be skipped even
            // though the recorded pose visibly crosses them.
            Physics2D.SyncTransforms();
            body.linearVelocity = velocity;
            hero.current_velocity = velocity;
            if (hero.cState != null && hero.cState.facingRight != request.FacingRight) {
                if (request.FacingRight) hero.FaceRight(); else hero.FaceLeft();
            }
            if (request.Grounded && hero.cState != null && !hero.cState.onGround) {
                hero.SetBackOnGround();
            }
        }

        private void QueueGroundTruthTelemetry()
        {
            if (server == null) return;
            var gameManager = GameManager.instance;
            var hero = HeroController.SilentInstance;
            var body = hero == null ? null : hero.GetComponent<Rigidbody2D>();
            var cameras = GameCameras.instance;
            var controller = cameras == null ? null : cameras.cameraController;
            var target = cameras == null ? null : cameras.cameraTarget;
            // Camera.main and GameCameras.mainCamera both resolve to a
            // compositing/UI projection in this build (orthographic size
            // 480). tk2d owns the actual world projection used to render the
            // gameplay scene. CameraController.cam is the safe fallback while
            // tk2d is being rebuilt during a scene transition.
            var unityCamera = cameras == null || cameras.tk2dCam == null
                ? null : cameras.tk2dCam.GetComponent<Camera>();
            if (unityCamera == null && controller != null) unityCamera = controller.cam;
            var heroAvailable = hero != null && hero.gameObject.activeInHierarchy;
            var cameraAvailable = controller != null;
            var heroPosition = heroAvailable ? hero.transform.position : Vector3.zero;
            var velocity = body == null ? Vector2.zero : body.linearVelocity;
            var cameraPosition = cameraAvailable
                ? controller.transform.position : Vector3.zero;
            var targetPosition = target == null
                ? Vector3.zero : target.transform.position;
            // Project a point on the gameplay plane, not the camera's own
            // position. A point at the camera origin has zero clip-space
            // depth, so Unity collapses the one-world-unit probes and reports
            // a false zero scale for this customized tk2d projection.
            var projectionProbe = heroAvailable
                ? heroPosition : new Vector3(cameraPosition.x, cameraPosition.y, 0);
            var cameraScreenPosition = unityCamera == null
                ? Vector3.zero : unityCamera.WorldToScreenPoint(projectionProbe);
            var cameraScreenXUnit = unityCamera == null
                ? Vector3.zero : unityCamera.WorldToScreenPoint(projectionProbe + Vector3.right);
            var cameraScreenYUnit = unityCamera == null
                ? Vector3.zero : unityCamera.WorldToScreenPoint(projectionProbe + Vector3.up);
            var heroScreenPosition = unityCamera == null || !heroAvailable
                ? Vector3.zero : unityCamera.WorldToScreenPoint(heroPosition);
            server.QueueGroundTruth(new GroundTruthTelemetry {
                Sequence = groundTruthSequence++,
                UnityFrame = Time.frameCount,
                UnityRealtime = Time.realtimeSinceStartup,
                SceneName = gameManager == null ? "" : gameManager.sceneName,
                HeroAvailable = heroAvailable,
                HeroX = heroPosition.x,
                HeroY = heroPosition.y,
                HeroZ = heroPosition.z,
                VelocityX = velocity.x,
                VelocityY = velocity.y,
                FacingRight = heroAvailable && hero.cState != null && hero.cState.facingRight,
                Grounded = heroAvailable && hero.cState != null && hero.cState.onGround,
                CameraAvailable = cameraAvailable,
                CameraX = cameraPosition.x,
                CameraY = cameraPosition.y,
                CameraZ = cameraPosition.z,
                CameraTargetX = targetPosition.x,
                CameraTargetY = targetPosition.y,
                CameraTargetZ = targetPosition.z,
                OrthographicSize = unityCamera == null ? 0 : unityCamera.orthographicSize,
                PixelsPerWorldUnitX = unityCamera == null ? 0
                    : Math.Abs(cameraScreenXUnit.x - cameraScreenPosition.x),
                PixelsPerWorldUnitY = unityCamera == null ? 0
                    : Math.Abs(cameraScreenYUnit.y - cameraScreenPosition.y),
                HeroScreenX = heroScreenPosition.x,
                HeroScreenY = heroScreenPosition.y,
                ProjectionPixelWidth = unityCamera == null ? 0 : unityCamera.pixelWidth,
                ProjectionPixelHeight = unityCamera == null ? 0 : unityCamera.pixelHeight,
                ScreenWidth = Screen.width,
                ScreenHeight = Screen.height
            });
        }

        private void ApplyPointerRequest(PointerRequest request)
        {
            if (request.IsDisconnect) {
                pendingPointerClick = null;
                DetachPointerInput();
                return;
            }
            EnsurePointerInput();
            if (pointerInput != null) pointerInput.Apply(request);
            if (request.Kind == "exited") {
                ReleasePointerCursor();
                return;
            }
            if (pointerModule != null && !cursorVisibilityOwned) {
                cursorVisibilityBeforePointer = Cursor.visible;
                cursorVisibilityOwned = true;
            }
            if (cursorVisibilityOwned) Cursor.visible = true;
            if (request.Kind == "moved" || request.Kind == "leftDown") {
                DispatchPointerHover(request);
            }
            if (request.Kind == "leftUp") pendingPointerClick = request;
        }

        private void DispatchPointerHover(PointerRequest request)
        {
            var eventSystem = EventSystem.current;
            if (eventSystem == null) {
                LogPointer(request, "hover", 0, "no EventSystem");
                return;
            }
            var pointer = PointerEventFor(request, eventSystem);
            var raycasts = new List<RaycastResult>();
            eventSystem.RaycastAll(pointer, raycasts);
            for (var index = 0; index < raycasts.Count; index++) {
                var candidate = ExecuteEvents.GetEventHandler<ISelectHandler>(raycasts[index].gameObject)
                    ?? ExecuteEvents.GetEventHandler<ISubmitHandler>(raycasts[index].gameObject);
                if (candidate == null) continue;
                if (eventSystem.currentSelectedGameObject != candidate) {
                    eventSystem.SetSelectedGameObject(candidate);
                }
                if (request.Kind == "leftDown" || candidate.name != lastHoverTarget
                    || Time.realtimeSinceStartup - lastHoverDiagnosticAt >= 2f) {
                    LogPointer(request, "hover", raycasts.Count, candidate.name);
                    lastHoverTarget = candidate.name;
                    lastHoverDiagnosticAt = Time.realtimeSinceStartup;
                }
                return;
            }
            int selectableCount;
            string nearest;
            var rectTarget = SelectableAt(pointer.position, out selectableCount, out nearest);
            if (rectTarget != null) {
                if (eventSystem.currentSelectedGameObject != rectTarget) {
                    eventSystem.SetSelectedGameObject(rectTarget);
                }
                if (request.Kind == "leftDown" || rectTarget.name != lastHoverTarget
                    || Time.realtimeSinceStartup - lastHoverDiagnosticAt >= 2f) {
                    LogPointer(request, "hover/rect", selectableCount, rectTarget.name);
                    lastHoverTarget = rectTarget.name;
                    lastHoverDiagnosticAt = Time.realtimeSinceStartup;
                }
                return;
            }
            if (request.Kind == "leftDown" || Time.realtimeSinceStartup - lastHoverDiagnosticAt >= 2f) {
                LogPointer(request, "hover", raycasts.Count,
                    "no selectable target; eligible=" + selectableCount + "; nearest=" + nearest);
                lastHoverDiagnosticAt = Time.realtimeSinceStartup;
            }
        }

        private void DispatchPointerClick(PointerRequest request)
        {
            EnsurePointerInput();
            var eventSystem = EventSystem.current;
            if (eventSystem == null) {
                LogPointer(request, "click", 0, "no EventSystem");
                return;
            }

            var click = PointerEventFor(request, eventSystem);
            var raycasts = new List<RaycastResult>();
            eventSystem.RaycastAll(click, raycasts);
            for (var index = 0; index < raycasts.Count; index++) {
                var target = ExecuteEvents.GetEventHandler<IPointerClickHandler>(raycasts[index].gameObject);
                if (target != null) {
                    click.pointerCurrentRaycast = raycasts[index];
                    if (eventSystem.currentInputModule == pointerModule && Cursor.visible) {
                        LogPointer(request, "click/native", raycasts.Count, target.name);
                    } else {
                        eventSystem.SetSelectedGameObject(target);
                        ExecuteEvents.Execute(target, click, ExecuteEvents.pointerClickHandler);
                        LogPointer(request, "click/fallback", raycasts.Count, target.name);
                    }
                    return;
                }
                target = ExecuteEvents.GetEventHandler<ISubmitHandler>(raycasts[index].gameObject);
                if (target == null) continue;
                eventSystem.SetSelectedGameObject(target);
                ExecuteEvents.Execute(target, new BaseEventData(eventSystem), ExecuteEvents.submitHandler);
                LogPointer(request, "click/submit", raycasts.Count, target.name);
                return;
            }
            int selectableCount;
            string nearest;
            var rectTarget = SelectableAt(click.position, out selectableCount, out nearest);
            if (rectTarget != null) {
                eventSystem.SetSelectedGameObject(rectTarget);
                ExecuteEvents.Execute(rectTarget, new BaseEventData(eventSystem), ExecuteEvents.submitHandler);
                LogPointer(request, "click/rect", selectableCount, rectTarget.name);
                return;
            }
            LogPointer(request, "click", raycasts.Count,
                "no actionable target; eligible=" + selectableCount + "; nearest=" + nearest);
        }

        private static GameObject SelectableAt(
            Vector2 screenPoint, out int eligibleCount, out string nearest)
        {
            eligibleCount = 0;
            nearest = "none";
            GameObject best = null;
            float bestArea = float.PositiveInfinity;
            float nearestDistance = float.PositiveInfinity;
            foreach (var selectable in UnityEngine.Object.FindObjectsByType<Selectable>(
                FindObjectsSortMode.None)) {
                if (!selectable.gameObject.activeInHierarchy || !selectable.IsInteractable()) continue;
                var target = ExecuteEvents.GetEventHandler<ISubmitHandler>(selectable.gameObject);
                if (target == null) continue;
                var rect = selectable.transform as RectTransform;
                if (rect == null) continue;
                eligibleCount++;
                var canvas = selectable.GetComponentInParent<Canvas>();
                var rootCanvas = canvas == null ? null : canvas.rootCanvas;
                var camera = rootCanvas == null || rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay
                    ? null : rootCanvas.worldCamera;
                var corners = new Vector3[4];
                rect.GetWorldCorners(corners);
                var lower = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
                var upper = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
                var minX = Mathf.Min(lower.x, upper.x);
                var maxX = Mathf.Max(lower.x, upper.x);
                var minY = Mathf.Min(lower.y, upper.y);
                var maxY = Mathf.Max(lower.y, upper.y);
                var dx = Mathf.Max(minX - screenPoint.x, 0, screenPoint.x - maxX);
                var dy = Mathf.Max(minY - screenPoint.y, 0, screenPoint.y - maxY);
                var distance = dx * dx + dy * dy;
                if (distance < nearestDistance) {
                    nearestDistance = distance;
                    nearest = target.name + " [" + (minX / Screen.width).ToString("F2")
                        + "," + (minY / Screen.height).ToString("F2") + ".."
                        + (maxX / Screen.width).ToString("F2") + ","
                        + (maxY / Screen.height).ToString("F2") + "]";
                }
                if (!RectTransformUtility.RectangleContainsScreenPoint(rect, screenPoint, camera)) continue;
                var area = Mathf.Abs((upper.x - lower.x) * (upper.y - lower.y));
                if (area >= bestArea) continue;
                bestArea = area;
                best = target;
            }
            return best;
        }

        private void LogPointer(PointerRequest request, string phase, int raycastCount, string target)
        {
            var eventSystem = EventSystem.current;
            var module = eventSystem == null ? null : eventSystem.currentInputModule;
            Modding.Logger.Log("[HKV pointer] " + phase + " " + request.Kind
                + " x=" + request.NormalizedX.ToString("F3")
                + " y=" + request.NormalizedY.ToString("F3")
                + " screen=" + Screen.width + "x" + Screen.height
                + " rays=" + raycastCount + " target=" + target
                + " module=" + (module == null ? "none" : module.GetType().Name)
                + " cursor=" + Cursor.visible);
        }

        private static PointerEventData PointerEventFor(PointerRequest request, EventSystem eventSystem)
        {
            return new PointerEventData(eventSystem) {
                position = new Vector2(
                    Mathf.Clamp01((float)request.NormalizedX) * Screen.width,
                    Mathf.Clamp01((float)request.NormalizedY) * Screen.height
                ),
                button = PointerEventData.InputButton.Left,
                clickCount = request.ClickCount
            };
        }

        private void EnsurePointerInput()
        {
            var ui = UIManager.instance;
            var module = ui == null ? null : ui.inputModule;
            if (module == null) return;
            if (pointerModule != module) {
                DetachPointerInput();
                pointerModule = module;
            }
            if (pointerModule.inputOverride != pointerInput) pointerModule.inputOverride = pointerInput;
            pointerModule.allowMouseInput = true;
            pointerModule.focusOnMouseHover = true;
        }

        private void DetachPointerInput()
        {
            ReleasePointerCursor();
            if (pointerModule != null && pointerModule.inputOverride == pointerInput) {
                pointerModule.inputOverride = null;
            }
            pendingPointerClick = null;
            pointerModule = null;
            if (pointerInput != null) pointerInput.ResetState();
        }

        private void ReleasePointerCursor()
        {
            if (!cursorVisibilityOwned) return;
            Cursor.visible = cursorVisibilityBeforePointer;
            cursorVisibilityOwned = false;
            lastHoverTarget = null;
        }

        private void ApplyPauseRequest(PauseRequest request)
        {
            if (request.IsDisconnect || request.Kind == PauseCommandKind.Resume) {
                if (request.IsDisconnect || String.Equals(pauseSession, request.Session, StringComparison.Ordinal)) {
                    ReleaseVisionPause();
                }
                if (!request.IsDisconnect) server.QueuePauseAck(request, visionOwnsPause);
                return;
            }

            if (visionOwnsPause && !String.Equals(pauseSession, request.Session, StringComparison.Ordinal)) {
                ReleaseVisionPause();
            }
            if (!visionOwnsPause) {
                timeScaleBeforeVisionPause = Time.timeScale;
                pauseSession = request.Session;
                visionOwnsPause = true;
            }
            Time.timeScale = 0f;
            pauseLeaseDeadline = Stopwatch.GetTimestamp()
                + (long)(Stopwatch.Frequency * (request.LeaseMilliseconds / 1000.0));
            server.QueuePauseAck(request, visionOwnsPause && Time.timeScale == 0f);
        }

        private void ReleaseVisionPause()
        {
            if (!visionOwnsPause) return;
            // If another system changed timeScale after Vision paused, leave
            // that newer value untouched. Otherwise restore exactly what Vision replaced.
            if (Time.timeScale == 0f) Time.timeScale = timeScaleBeforeVisionPause;
            visionOwnsPause = false;
            pauseSession = null;
            pauseLeaseDeadline = 0;
        }

        private void ApplyCheckpointRequest(CheckpointRequest request)
        {
            if (request.Kind == CheckpointCommandKind.Restore
                && BeginSceneRestoreIfNeeded(request)) return;
            PlayerCheckpoint checkpoint;
            string failure;
            bool accepted;
            if (request.Kind == CheckpointCommandKind.Capture) {
                accepted = TryCaptureCheckpoint(out checkpoint, out failure);
                if (accepted && checkpoint != null) {
                    lastCapturedEnemies = checkpoint.Enemies ?? new List<EnemyCheckpoint>();
                }
            } else {
                checkpoint = request.Checkpoint;
                accepted = TryRestoreCheckpoint(checkpoint, out failure);
                if (accepted && checkpoint != null) {
                    lastCapturedEnemies = checkpoint.Enemies ?? new List<EnemyCheckpoint>();
                }
            }
            server.QueueCheckpointAck(request, accepted, checkpoint, failure);
        }

        private bool BeginSceneRestoreIfNeeded(CheckpointRequest request)
        {
            var checkpoint = request.Checkpoint;
            var gameManager = GameManager.instance;
            if (checkpoint == null || gameManager == null
                || String.IsNullOrWhiteSpace(gameManager.sceneName)) return false;

            if (pendingSceneRestoreRequest != null) {
                server.QueueCheckpointAck(
                    request,
                    false,
                    checkpoint,
                    "Another room restore is already in progress");
                return true;
            }

            if (String.Equals(gameManager.sceneName, checkpoint.SceneName,
                StringComparison.Ordinal)) return false;

            device.SetState(null, -1, false, VisionButtons.None);
            appliedEnabled = false;
            applied = VisionButtons.None;
            pendingSceneRestoreRequest = request;
            pendingSceneRestoreCheckpoint = checkpoint;
            pendingSceneRestoreDeadline = Time.unscaledTime + 12f;
            gameManager.ResetSemiPersistentItems();
            RestoreStoryState(checkpoint);
            var entryGate = ResolveCheckpointEntryGate(checkpoint);
            if (!String.IsNullOrWhiteSpace(entryGate)) {
                // Raw LoadScene leaves entryGateName unset. The next ordinary
                // room exit then searches for an empty/stale TransitionGate
                // and the Knight can run out of bounds. Use the game's normal
                // transition path, then restore story values which
                // ChangeToScene modifies while preparing the load.
                gameManager.ChangeToScene(checkpoint.SceneName, entryGate, 0f);
                RestoreStoryState(checkpoint);
            } else {
                // Legacy paths from arbitrary rooms may not identify a gate.
                // Preserve their old restore behavior rather than guessing a
                // gate that may immediately send the Knight elsewhere.
                gameManager.LoadScene(checkpoint.SceneName);
            }
            return true;
        }

        private static string ResolveCheckpointEntryGate(PlayerCheckpoint checkpoint)
        {
            if (checkpoint == null) return null;
            if (!String.IsNullOrWhiteSpace(checkpoint.EntryGateName))
                return checkpoint.EntryGateName;
            // Compatibility for the existing long stability route recorded
            // before entry gates became part of checkpoints.
            if (String.Equals(checkpoint.SceneName, "Tutorial_01",
                StringComparison.Ordinal)) return "top1";
            return null;
        }

        private void MaintainPendingSceneRestore()
        {
            var request = pendingSceneRestoreRequest;
            var checkpoint = pendingSceneRestoreCheckpoint;
            if (request == null || checkpoint == null) return;

            var gameManager = GameManager.instance;
            if (gameManager != null
                && String.Equals(gameManager.sceneName, checkpoint.SceneName,
                    StringComparison.Ordinal)) {
                string failure;
                if (TryRestoreCheckpoint(checkpoint, out failure)) {
                    lastCapturedEnemies = checkpoint.Enemies ?? new List<EnemyCheckpoint>();
                    ClearPendingSceneRestore();
                    server.QueueCheckpointAck(request, true, checkpoint, null);
                    return;
                }
            }

            if (Time.unscaledTime < pendingSceneRestoreDeadline) return;
            ClearPendingSceneRestore();
            server.QueueCheckpointAck(
                request,
                false,
                checkpoint,
                "Timed out loading path start room (" + checkpoint.SceneName + ")");
        }

        private void ClearPendingSceneRestore()
        {
            pendingSceneRestoreRequest = null;
            pendingSceneRestoreCheckpoint = null;
            pendingSceneRestoreDeadline = 0f;
        }

        private static bool TryCaptureCheckpoint(
            out PlayerCheckpoint checkpoint,
            out string failure)
        {
            checkpoint = null;
            failure = null;
            var hero = HeroController.SilentInstance;
            var gameManager = GameManager.instance;
            var cameras = GameCameras.instance;
            if (hero == null || !hero.gameObject.activeInHierarchy || gameManager == null
                || String.IsNullOrWhiteSpace(gameManager.sceneName)) {
                failure = "Knight is unavailable; enter gameplay first";
                return false;
            }
            if (hero.cState == null || !hero.cState.onGround) {
                failure = "Stand on the ground before recording a path";
                return false;
            }
            var body = hero.GetComponent<Rigidbody2D>();
            if (body == null) {
                failure = "Knight physics body is unavailable";
                return false;
            }
            if (Mathf.Abs(body.linearVelocity.x) > 0.1f
                || Mathf.Abs(body.linearVelocity.y) > 0.1f) {
                failure = "Stand still before recording a path";
                return false;
            }
            if (cameras == null || cameras.cameraController == null
                || cameras.cameraTarget == null) {
                failure = "Gameplay camera is unavailable";
                return false;
            }
            var heroPosition = hero.transform.position;
            var cameraPosition = cameras.cameraController.transform.position;
            var targetPosition = cameras.cameraTarget.transform.position;
            var playerData = PlayerData.instance;
            checkpoint = new PlayerCheckpoint {
                SceneName = gameManager.sceneName,
                EntryGateName = NearestEntryGateName(
                    gameManager.sceneName, heroPosition),
                HeroX = heroPosition.x,
                HeroY = heroPosition.y,
                HeroZ = heroPosition.z,
                VelocityX = body.linearVelocity.x,
                VelocityY = body.linearVelocity.y,
                FacingRight = hero.cState.facingRight,
                Grounded = hero.cState.onGround,
                CameraX = cameraPosition.x,
                CameraY = cameraPosition.y,
                CameraZ = cameraPosition.z,
                CameraTargetX = targetPosition.x,
                CameraTargetY = targetPosition.y,
                CameraTargetZ = targetPosition.z,
                Enemies = CaptureEnemies(),
                IsFirstGame = playerData == null ? (bool?)null : playerData.isFirstGame,
                EnteredTutorialFirstTime = playerData == null
                    ? (bool?)null : playerData.enteredTutorialFirstTime,
                VisitedDirtmouth = playerData == null
                    ? (bool?)null : playerData.visitedDirtmouth,
                VisitedCrossroads = playerData == null
                    ? (bool?)null : playerData.visitedCrossroads,
                OpenedTown = playerData == null ? (bool?)null : playerData.openedTown,
                OpenedCrossroads = playerData == null
                    ? (bool?)null : playerData.openedCrossroads,
                ScenesVisited = playerData == null || playerData.scenesVisited == null
                    ? null : new List<string>(playerData.scenesVisited)
            };
            return true;
        }

        private static string NearestEntryGateName(
            string sceneName,
            Vector3 heroPosition)
        {
            var gates = TransitionPoint.TransitionPoints;
            if (gates == null) return null;
            TransitionPoint closest = null;
            var closestDistance = Single.PositiveInfinity;
            foreach (var gate in gates) {
                if (gate == null || gate.gameObject == null
                    || !gate.gameObject.activeInHierarchy
                    || !String.Equals(gate.gameObject.scene.name, sceneName,
                        StringComparison.Ordinal)) continue;
                var distance = (gate.transform.position - heroPosition).sqrMagnitude;
                if (distance >= closestDistance) continue;
                closest = gate;
                closestDistance = distance;
            }
            return closest == null ? null : closest.gameObject.name;
        }

        private static bool TryRestoreCheckpoint(
            PlayerCheckpoint checkpoint,
            out string failure)
        {
            failure = null;
            if (checkpoint == null) {
                failure = "Saved path has no start position";
                return false;
            }
            var hero = HeroController.SilentInstance;
            var gameManager = GameManager.instance;
            if (hero == null || !hero.gameObject.activeInHierarchy || gameManager == null) {
                failure = "Knight is unavailable; enter gameplay first";
                return false;
            }
            if (!String.Equals(gameManager.sceneName, checkpoint.SceneName,
                StringComparison.Ordinal)) {
                failure = "Path starts in a different room (" + checkpoint.SceneName + ")";
                return false;
            }
            RestoreStoryState(checkpoint);
            RestoreEnemies(checkpoint.Enemies);
            var body = hero.GetComponent<Rigidbody2D>();
            if (body == null) {
                failure = "Knight physics body is unavailable";
                return false;
            }

            var position = new Vector3(
                (float)checkpoint.HeroX,
                (float)checkpoint.HeroY,
                (float)checkpoint.HeroZ);
            body.linearVelocity = Vector2.zero;
            body.angularVelocity = 0f;
            hero.current_velocity = Vector2.zero;
            hero.transform.position = position;
            body.position = new Vector2(position.x, position.y);
            Physics2D.SyncTransforms();
            if (checkpoint.Grounded) hero.SetBackOnGround();
            if (checkpoint.FacingRight) hero.FaceRight(); else hero.FaceLeft();
            body.linearVelocity = new Vector2(
                (float)checkpoint.VelocityX,
                (float)checkpoint.VelocityY);
            hero.current_velocity = body.linearVelocity;

            var cameras = GameCameras.instance;
            if (cameras != null && cameras.cameraTarget != null) {
                var targetPosition = new Vector3(
                    (float)checkpoint.CameraTargetX,
                    (float)checkpoint.CameraTargetY,
                    (float)checkpoint.CameraTargetZ);
                cameras.cameraTarget.transform.position = targetPosition;
                cameras.cameraTarget.destination = targetPosition;
            }
            if (cameras != null && cameras.cameraController != null) {
                var cameraPosition = new Vector3(
                    (float)checkpoint.CameraX,
                    (float)checkpoint.CameraY,
                    (float)checkpoint.CameraZ);
                cameras.cameraController.transform.position = cameraPosition;
                cameras.cameraController.lastFramePosition = cameraPosition;
                cameras.cameraController.destination = cameraPosition;
                cameras.cameraController.SnapTo(cameraPosition.x, cameraPosition.y);
            }
            return true;
        }

        private static void RestoreStoryState(PlayerCheckpoint checkpoint)
        {
            var data = PlayerData.instance;
            if (checkpoint == null || data == null) return;
            if (checkpoint.IsFirstGame.HasValue)
                data.isFirstGame = checkpoint.IsFirstGame.Value;
            if (checkpoint.EnteredTutorialFirstTime.HasValue)
                data.enteredTutorialFirstTime = checkpoint.EnteredTutorialFirstTime.Value;
            if (checkpoint.VisitedDirtmouth.HasValue)
                data.visitedDirtmouth = checkpoint.VisitedDirtmouth.Value;
            if (checkpoint.VisitedCrossroads.HasValue)
                data.visitedCrossroads = checkpoint.VisitedCrossroads.Value;
            if (checkpoint.OpenedTown.HasValue)
                data.openedTown = checkpoint.OpenedTown.Value;
            if (checkpoint.OpenedCrossroads.HasValue)
                data.openedCrossroads = checkpoint.OpenedCrossroads.Value;
            if (checkpoint.ScenesVisited == null) return;
            if (data.scenesVisited == null) data.scenesVisited = new List<string>();
            else data.scenesVisited.Clear();
            data.scenesVisited.AddRange(checkpoint.ScenesVisited);
        }

        private void ApplyPlayerOpsRequest(PlayerOpsRequest request)
        {
            var accepted = false;
            var failure = (string)null;
            var state = (PlayerTestState)null;
            int? enemiesRestored = null;
            if (request.Kind == PlayerOpsKind.Query) {
                accepted = TryReadPlayerTestState(out state, out failure);
            } else if (request.Kind == PlayerOpsKind.Apply) {
                accepted = TryApplyPlayerTestState(request.State, out state, out failure);
            } else {
                PlayerCheckpoint checkpoint;
                accepted = TryCaptureCheckpoint(out checkpoint, out failure);
                if (accepted && checkpoint != null) {
                    var gameManager = GameManager.instance;
                    gameManager.ResetSemiPersistentItems();
                    pendingEnemyResetCheckpoint = checkpoint;
                    pendingEnemyResetRestoreAt = Time.unscaledTime + 0.35f;
                    enemiesRestored = checkpoint.Enemies == null
                        ? 0 : checkpoint.Enemies.Count;
                    gameManager.LoadScene(checkpoint.SceneName);
                    accepted = true;
                }
            }
            server.QueuePlayerOpsAck(request, accepted, state, enemiesRestored, failure);
        }

        private void MaintainPendingEnemyReset()
        {
            var checkpoint = pendingEnemyResetCheckpoint;
            if (checkpoint == null || Time.unscaledTime < pendingEnemyResetRestoreAt) return;
            var gameManager = GameManager.instance;
            var hero = HeroController.SilentInstance;
            if (gameManager == null || hero == null || !hero.gameObject.activeInHierarchy
                || !String.Equals(gameManager.sceneName, checkpoint.SceneName,
                    StringComparison.Ordinal)) return;
            string failure;
            if (!TryRestoreCheckpoint(checkpoint, out failure)) return;
            pendingEnemyResetCheckpoint = null;
        }

        private bool TryReadPlayerTestState(
            out PlayerTestState state,
            out string failure)
        {
            state = null;
            failure = null;
            var hero = HeroController.SilentInstance;
            var data = PlayerData.instance;
            if (hero == null || !hero.gameObject.activeInHierarchy || data == null) {
                failure = "Knight is unavailable; enter gameplay first";
                return false;
            }

            state = new PlayerTestState {
                MaxHealth = Mathf.Clamp(data.maxHealth, 1, 9),
                Health = Mathf.Clamp(data.health, 1, Mathf.Clamp(data.maxHealth, 1, 9)),
                LifebloodSeed = Mathf.Clamp(data.healthBlue, 0, 9),
                Mana = Mathf.Clamp(data.MPCharge + data.MPReserve, 0, 198),
                ExtraManaSlots = Mathf.Clamp(data.MPReserveMax / 33, 0, 3),
                Geo = Mathf.Clamp(data.geo, 0, 9999999),
                Invincible = invincibilityEnabled
            };
            return true;
        }

        private bool TryApplyPlayerTestState(
            PlayerTestState requested,
            out PlayerTestState applied,
            out string failure)
        {
            applied = null;
            failure = null;
            if (requested == null) {
                failure = "Player state is missing";
                return false;
            }
            var hero = HeroController.SilentInstance;
            var data = PlayerData.instance;
            var cameras = GameCameras.instance;
            if (hero == null || !hero.gameObject.activeInHierarchy || data == null) {
                failure = "Knight is unavailable; enter gameplay first";
                return false;
            }

            var maxHealthDelta = requested.MaxHealth - data.maxHealthBase;
            if (maxHealthDelta != 0) data.AddToMaxHealth(maxHealthDelta);
            // AddToMaxHealth is the game's canonical mutation path. Explicitly
            // enforce the requested visible total as well so charms cannot
            // leave a stale derived maximum in this test-only operation.
            data.prevHealth = data.health;
            if (requested.Health > data.health) {
                hero.AddHealth(requested.Health - data.health);
            } else if (requested.Health < data.health) {
                hero.TakeHealth(data.health - requested.Health);
            }
            var reserveCapacityDelta = requested.ExtraManaSlots * 33 - data.MPReserveMax;
            if (reserveCapacityDelta != 0) data.AddToMaxMPReserve(reserveCapacityDelta);
            invincibilityEnabled = requested.Invincible;
            nextEnemyCollisionScanAt = -1;
            MaintainEnemyCollisionBypass();
            playerOpsBlueHealthOverride = requested.LifebloodSeed;
            WriteExactPlayerTestState(data, requested);

            // Keep the vanilla HUD hierarchy alive. Deactivating hudCanvas
            // restarts its PlayMaker FSMs outside their normal lifecycle: the
            // inventory can still send HUD OUT afterwards, but the matching
            // HUD IN no longer restores the top-left display reliably.
            // Canonical player mutations plus targeted refresh events below
            // update the counters without disturbing menu transitions.
            RefreshHealthDisplays(cameras);
            RefreshPlayerOpsDisplays(hero, data, cameras);
            // HUD FSM startup can clear soul one update after it is enabled.
            // Keep the requested values authoritative only during this short
            // redraw window, refresh once after startup, then release them so
            // normal gameplay can change health, soul, and Geo again.
            WriteExactPlayerTestState(data, requested);
            pendingPlayerOpsReconciliation = CopyPlayerTestState(requested);
            playerOpsDisplayRefreshAt = Time.unscaledTime + 0.5f;
            playerOpsReconcileUntil = Time.unscaledTime + 0.85f;
            playerOpsDisplayRefreshed = false;
            return TryReadPlayerTestState(out applied, out failure);
        }

        private void MaintainPlayerOpsReconciliation()
        {
            var requested = pendingPlayerOpsReconciliation;
            if (requested == null) return;
            var hero = HeroController.SilentInstance;
            var data = PlayerData.instance;
            if (hero == null || !hero.gameObject.activeInHierarchy || data == null) {
                pendingPlayerOpsReconciliation = null;
                return;
            }
            WriteExactPlayerTestState(data, requested);
            if (!playerOpsDisplayRefreshed
                && Time.unscaledTime >= playerOpsDisplayRefreshAt) {
                RefreshHealthDisplays(GameCameras.instance);
                RefreshPlayerOpsDisplays(hero, data, GameCameras.instance);
                RefreshBlueHealthDisplay(GameCameras.instance);
                WriteExactPlayerTestState(data, requested);
                playerOpsDisplayRefreshed = true;
            }
            if (Time.unscaledTime >= playerOpsReconcileUntil) {
                pendingPlayerOpsReconciliation = null;
            }
        }

        private static void WriteExactPlayerTestState(
            PlayerData data,
            PlayerTestState requested)
        {
            data.maxHealthBase = requested.MaxHealth;
            data.maxHealth = requested.MaxHealth;
            data.health = requested.Health;
            data.healthBlue = requested.LifebloodSeed;
            data.joniHealthBlue = 0;
            data.maxMP = 99;
            data.MPReserveMax = requested.ExtraManaSlots * 33;
            data.MPCharge = Math.Min(99, requested.Mana);
            data.MPReserve = Math.Max(0, requested.Mana - 99);
            data.geo = requested.Geo;
        }

        private static PlayerTestState CopyPlayerTestState(PlayerTestState state)
        {
            return new PlayerTestState {
                MaxHealth = state.MaxHealth,
                Health = state.Health,
                LifebloodSeed = state.LifebloodSeed,
                Mana = state.Mana,
                ExtraManaSlots = state.ExtraManaSlots,
                Geo = state.Geo,
                Invincible = state.Invincible
            };
        }

        private static void RefreshPlayerOpsDisplays(
            HeroController hero,
            PlayerData data,
            GameCameras cameras)
        {
            PlayMakerFSM.BroadcastEvent("HUD IN");
            EventRegister.SendEvent("UPDATE BLUE HEALTH", null);
            EventRegister.SendEvent("UPDATE VESSELS", null);
            PlayMakerFSM.BroadcastEvent("UPDATE VESSELS");
            if (cameras != null && cameras.soulOrbFSM != null) {
                cameras.soulOrbFSM.SendEvent("MP SET");
            }
            var gameManager = GameManager.instance;
            if (gameManager != null && gameManager.soulVessel_fsm != null) {
                gameManager.soulVessel_fsm.SendEvent(data.MPReserve > 0
                    ? "MP RESERVE UP" : "MP RESERVE DOWN");
            }
            RefreshReserveVesselDisplays(cameras, data.MPReserve, data.MPReserveMax);
            if (hero.geoCounter != null) hero.geoCounter.NewSceneRefresh();
        }

        private static void RefreshReserveVesselDisplays(
            GameCameras cameras,
            int reserveMana,
            int reserveCapacity)
        {
            if (cameras == null || cameras.hudCanvas == null) return;
            var fsms = cameras.hudCanvas.GetComponentsInChildren<PlayMakerFSM>(true);
            for (var index = 0; index < fsms.Length; index++) {
                var fsm = fsms[index];
                if (!String.Equals(fsm.FsmName, "vessel_orb",
                    StringComparison.Ordinal)) continue;
                var reserveVariable = fsm.FsmVariables.GetFsmInt("Reserve MP");
                var capacityVariable = fsm.FsmVariables.GetFsmInt("Reserve MP Max");
                var emptyThreshold = fsm.FsmVariables.GetFsmInt("Empty Amount");
                if (reserveVariable != null) reserveVariable.Value = reserveMana;
                if (capacityVariable != null) capacityVariable.Value = reserveCapacity;
                if (emptyThreshold == null || emptyThreshold.Value >= reserveCapacity) {
                    fsm.SetState("Not Obtained");
                } else {
                    // Up Check compares the freshly synchronized reserve value
                    // with this vessel's quarter/half/full thresholds and
                    // selects the matching fill animation and final sprite.
                    fsm.SetState("Up Check");
                }
            }
        }

        private static void RefreshBlueHealthDisplay(GameCameras cameras)
        {
            // Target the dedicated controller without restarting the parent
            // HUD canvas; the parent owns inventory/pause visibility state.
            if (cameras == null || cameras.hudCanvas == null) return;
            var fsms = cameras.hudCanvas.GetComponentsInChildren<PlayMakerFSM>(true);
            for (var index = 0; index < fsms.Length; index++) {
                var fsm = fsms[index];
                if (!String.Equals(fsm.FsmName, "Blue Health Control",
                    StringComparison.Ordinal)) continue;
                fsm.SendEvent("UPDATE BLUE HEALTH");
                return;
            }
        }

        private static void RefreshHealthDisplays(GameCameras cameras)
        {
            if (cameras == null || cameras.hudCanvas == null) return;
            var fsms = cameras.hudCanvas.GetComponentsInChildren<PlayMakerFSM>(true);
            for (var index = 0; index < fsms.Length; index++) {
                var fsm = fsms[index];
                if (!String.Equals(fsm.FsmName, "health_display",
                    StringComparison.Ordinal)) continue;
                var states = fsm.FsmStates;
                for (var stateIndex = 0; stateIndex < states.Length; stateIndex++) {
                    if (!String.Equals(states[stateIndex].Name, "Init",
                        StringComparison.Ordinal)) continue;
                    fsm.SetState("Init");
                    break;
                }
            }
        }

        private static List<EnemyCheckpoint> CaptureEnemies()
        {
            var result = new List<EnemyCheckpoint>();
            var enemies = UnityEngine.Object.FindObjectsByType<HealthManager>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (var index = 0; index < enemies.Length; index++) {
                var enemy = enemies[index];
                if (enemy == null || enemy.hp < 1) continue;
                var position = enemy.transform.position;
                result.Add(new EnemyCheckpoint {
                    Name = enemy.gameObject.name,
                    X = position.x,
                    Y = position.y,
                    Z = position.z,
                    HP = enemy.hp,
                    Active = enemy.gameObject.activeSelf
                });
            }
            return result;
        }

        private static int RestoreEnemies(List<EnemyCheckpoint> snapshots)
        {
            if (snapshots == null || snapshots.Count == 0) return 0;
            var gameManager = GameManager.instance;
            if (gameManager != null) gameManager.ResetSemiPersistentItems();
            var available = new List<HealthManager>(
                UnityEngine.Object.FindObjectsByType<HealthManager>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None));
            var restored = 0;
            foreach (var snapshot in snapshots) {
                HealthManager best = null;
                var bestDistance = float.MaxValue;
                for (var index = 0; index < available.Count; index++) {
                    var candidate = available[index];
                    if (candidate == null || candidate.gameObject.name != snapshot.Name) continue;
                    var delta = candidate.transform.position - new Vector3(
                        (float)snapshot.X, (float)snapshot.Y, (float)snapshot.Z);
                    var distance = delta.sqrMagnitude;
                    if (distance >= bestDistance) continue;
                    best = candidate;
                    bestDistance = distance;
                }
                if (best == null) continue;
                available.Remove(best);
                best.transform.position = new Vector3(
                    (float)snapshot.X, (float)snapshot.Y, (float)snapshot.Z);
                best.hp = snapshot.HP;
                best.SetIsDead(false);
                best.gameObject.SetActive(snapshot.Active);
                var fsms = best.GetComponents<PlayMakerFSM>();
                for (var fsmIndex = 0; fsmIndex < fsms.Length; fsmIndex++) {
                    fsms[fsmIndex].SendEvent("RESET");
                }
                restored++;
            }
            Physics2D.SyncTransforms();
            return restored;
        }

        private void OnDeviceCommitted(string session, long sequence, bool enabled, VisionButtons effectiveButtons)
        {
            if (server != null) server.QueueAck(session, sequence, enabled, effectiveButtons);
        }

        private void OnApplicationQuit() { StopReceiver(); }
        private void OnDestroy() { StopReceiver(); }

        private void EnsureShortcutBindings()
        {
            var handler = InputHandler.Instance;
            var actions = handler == null ? null : handler.inputActions;
            if (actions == null) return;
            if (ReferenceEquals(boundActions, actions)
                && inventoryBinding != null && pauseMenuBinding != null
                && actions.openInventory.HasBinding(inventoryBinding)
                && actions.pause.HasBinding(pauseMenuBinding)) return;

            RemoveShortcutBindings();
            inventoryBinding = new DeviceBindingSource(InputControlType.Button28);
            pauseMenuBinding = new DeviceBindingSource(InputControlType.Button29);
            actions.openInventory.AddBinding(inventoryBinding);
            actions.pause.AddBinding(pauseMenuBinding);
            boundActions = actions;
        }

        private void RemoveShortcutBindings()
        {
            if (boundActions != null) {
                if (inventoryBinding != null) boundActions.openInventory.RemoveBinding(inventoryBinding);
                if (pauseMenuBinding != null) boundActions.pause.RemoveBinding(pauseMenuBinding);
            }
            boundActions = null;
            inventoryBinding = null;
            pauseMenuBinding = null;
        }

        private void StopReceiver()
        {
            Camera.onPreRender -= OnCameraPreRender;
            if (frameMarkerTexture != null) { Destroy(frameMarkerTexture); frameMarkerTexture = null; }
            if (invincibilityEnabled) {
                invincibilityEnabled = false;
                nextEnemyCollisionScanAt = -1;
                MaintainEnemyCollisionBypass();
            }
            if (damageHooksInstalled) {
                ModHooks.TakeHealthHook -= PreventHealthLoss;
                ModHooks.TakeDamageHook -= PreventDamage;
                ModHooks.BlueHealthHook -= ProvidePlayerOpsBlueHealth;
                damageHooksInstalled = false;
            }
            ReleaseVisionPause();
            DetachPointerInput();
            RemoveShortcutBindings();
            if (server != null) { server.Dispose(); server = null; }
            if (device != null) { device.Committed = null; InputManager.DetachDevice(device); device = null; }
            if (pointerInput != null) { Destroy(pointerInput); pointerInput = null; }
            Application.runInBackground = priorRunInBackground;
            InputManager.SuspendInBackground = priorSuspendInBackground;
            if (pacingConfigured) {
                QualitySettings.vSyncCount = priorVSyncCount;
                Application.targetFrameRate = priorTargetFrameRate;
                pacingConfigured = false;
            }
        }
    }
}
