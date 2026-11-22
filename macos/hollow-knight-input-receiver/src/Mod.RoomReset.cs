using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Scenes = UnityEngine.SceneManagement.SceneManager;

namespace HollowKnightVisionInputReceiver
{
    internal sealed partial class ReceiverBehaviour
    {
        private PlayerCheckpoint roomEntrance;
        private readonly RoomEntrances roomEntrances = new RoomEntrances();
        private PlayerData entrancePlayerData;
        private int entranceProfile = Int32.MinValue;
        private int entranceSceneHandle = -1;
        private PlayerOpsRequest roomResetRequest;
        private PlayerCheckpoint roomResetPose;
        private int roomResetOldHandle;
        private float roomResetDeadline;
        private bool roomResetLoaded;

        private void RememberRoomEntrance()
        {
            if (roomResetRequest != null) return;
            var scene = Scenes.GetActiveScene();
            var gm = GameManager.instance;
            if (gm != null && gm.gameState == GlobalEnums.GameState.MAIN_MENU) {
                roomEntrances.Clear();
                roomEntrance = null;
                entranceSceneHandle = -1;
                entrancePlayerData = null;
                entranceProfile = Int32.MinValue;
                return;
            }
            var hero = HeroController.SilentInstance;
            if (gm == null || scene.name != gm.sceneName || gm.IsInSceneTransition ||
                gm.gameState != GlobalEnums.GameState.PLAYING || PlayerData.instance == null || hero == null ||
                !hero.acceptingInput || hero.cState == null || !hero.cState.onGround) return;
            // Never carry positions between loaded save games.
            if (entrancePlayerData != PlayerData.instance || entranceProfile != gm.profileID) {
                roomEntrances.Clear();
                roomEntrance = null;
                entranceSceneHandle = -1;
                entrancePlayerData = PlayerData.instance;
                entranceProfile = gm.profileID;
            }
            if (scene.handle == entranceSceneHandle) return;
            PlayerCheckpoint previous;
            if (roomEntrances.TryGet(scene.name, out previous)) {
                roomEntrance = previous;
                entranceSceneHandle = scene.handle;
                return;
            }
            PlayerCheckpoint arrival;
            string failure;
            if (!TryCaptureCheckpoint(out arrival, out failure, true, false)) return;
            arrival.EntryGateName = null;
            var gates = TransitionPoint.TransitionPoints;
            if (gates != null && !String.IsNullOrEmpty(gm.entryGateName)) {
                foreach (var gate in gates) {
                    if (gate != null && gate.gameObject.scene.handle == scene.handle && gate.gameObject.name == gm.entryGateName) {
                        arrival.EntryGateName = gm.entryGateName;
                        break;
                    }
                }
            }
            roomEntrance = arrival;
            roomEntrances.Remember(arrival);
            entranceSceneHandle = scene.handle;
        }

        private bool TryBeginRoomReset(PlayerOpsRequest request, out string failure)
        {
            var gm = GameManager.instance;
            var scene = Scenes.GetActiveScene();
            if (roomResetRequest != null || pendingEnemyResetCheckpoint != null || pendingSceneRestoreRequest != null
                || gm == null || scene.name != gm.sceneName || gm.IsInSceneTransition || gm.isPaused
                || gm.gameState != GlobalEnums.GameState.PLAYING || SceneData.instance == null) {
                failure = "Enter an active gameplay room before resetting.";
                return false;
            }
            PlayerCheckpoint pose;
            if (!TryCaptureCheckpoint(out pose, out failure, true, false)) return false;
            if (scene.handle != entranceSceneHandle || roomEntrance == null || !pose.UseRoomEntrance(roomEntrance)) {
                failure = "Wait for the Knight to land at the room entrance.";
                return false;
            }
            roomResetRequest = request;
            roomResetPose = pose;
            roomResetOldHandle = scene.handle;
            roomResetDeadline = Time.realtimeSinceStartup + 12f;
            roomResetLoaded = false;
            Scenes.sceneLoaded += OnRoomResetLoaded;
            try {
                // LoadScene invokes the game's unload callbacks before scheduling the new scene.
                // Remove saved state before the replacement GeoRocks read it on activation.
                if (String.IsNullOrEmpty(pose.EntryGateName)) gm.LoadScene(scene.name);
                else gm.ChangeToScene(scene.name, pose.EntryGateName, 0f);
                ClearRoomProgress(scene);
                return true;
            } catch (Exception error) {
                FinishRoomReset(false, error.Message);
                return true;
            }
        }

        private static bool RoomObstacle(Component item)
        {
            if (item.GetComponentInParent<HealthManager>(true) != null || item.GetComponentInChildren<HealthManager>(true) != null
                || item.GetComponentInParent<Breakable>(true) != null) return true;
            for (var node = item.transform; node != null; node = node.parent) {
                if (RoomResetProgress.BarrierName(node.name)) return true;
                foreach (var fsm in node.GetComponents<PlayMakerFSM>()) if (RoomResetProgress.BarrierName(fsm.FsmName)) return true;
            }
            return false;
        }

        private static void ClearRoomProgress(Scene scene)
        {
            var data = SceneData.instance;
            var boolIDs = new HashSet<string>(StringComparer.Ordinal);
            var intIDs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var root in scene.GetRootGameObjects()) {
                foreach (var item in root.GetComponentsInChildren<PersistentBoolItem>(true))
                    if (RoomObstacle(item)) boolIDs.Add(String.IsNullOrEmpty(item.GetId()) ? item.name : item.GetId());
                foreach (var item in root.GetComponentsInChildren<PersistentIntItem>(true))
                    if (RoomObstacle(item)) intIDs.Add(String.IsNullOrEmpty(item.GetId()) ? item.name : item.GetId());
            }
            RoomResetProgress.Clear(data, scene.name, boolIDs, intIDs);
        }

        private void OnRoomResetLoaded(Scene scene, LoadSceneMode mode)
        {
            if (roomResetPose == null || scene.name != roomResetPose.SceneName || scene.handle == roomResetOldHandle) return;
            // Fresh prefabs reveal persistent IDs even for enemies and barriers destroyed in the old scene.
            // This callback runs before their Start methods consume the persistent records.
            try { ClearRoomProgress(scene); roomResetLoaded = true; }
            catch (Exception error) { FinishRoomReset(false, error.Message); }
        }

        private void MaintainRoomReset()
        {
            if (roomResetRequest == null) return;
            if (Time.realtimeSinceStartup >= roomResetDeadline) {
                FinishRoomReset(false, "Timed out waiting for the reset room.");
                return;
            }
            var gm = GameManager.instance;
            if (!roomResetLoaded || gm == null || gm.IsInSceneTransition || gm.gameState != GlobalEnums.GameState.PLAYING) return;
            string failure;
            if (TryRestoreCheckpoint(roomResetPose, out failure)) FinishRoomReset(true, null);
        }

        private void FinishRoomReset(bool accepted, string failure)
        {
            Scenes.sceneLoaded -= OnRoomResetLoaded;
            if (accepted) {
                entranceSceneHandle = Scenes.GetActiveScene().handle;
                roomResetRequest.ResetScene = roomResetPose.SceneName;
            }
            server.QueuePlayerOpsAck(roomResetRequest, accepted, null, null, failure);
            roomResetRequest = null;
            roomResetPose = null;
        }
    }
}
