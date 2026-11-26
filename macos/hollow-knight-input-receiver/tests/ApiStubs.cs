// Test-only compile surface for the public calls used by the receiver.
// This file is not included by src/HollowKnightVisionInputReceiver.csproj.
using System.Collections.Generic;

namespace UnityEngine {
    public sealed class DefaultExecutionOrder : System.Attribute {
        public DefaultExecutionOrder(int order) { }
    }
    public class MonoBehaviour { }
    public class GameObject {
        public GameObject(string value) { }
        public T AddComponent<T>() where T : new() { return new T(); }
        public readonly Transform transform = new Transform();
        public SceneManagement.Scene scene;
        public int layer;
        public bool activeInHierarchy = true;
        public HealthManager ParentHealth;
        public T GetComponentInParent<T>() where T : class { return ParentHealth as T; }
    }
    public class Transform {
        public Transform parent;
        public bool IsChildOf(Transform root) {
            for (var node = this; node != null; node = node.parent)
                if (node == root) return true;
            return false;
        }
    }
    public static class LayerMask {
        public const int EnemyLayer = 8;
        public static int NameToLayer(string name) { return name == "Enemies" ? EnemyLayer : -1; }
    }
    public class Object { public static void DontDestroyOnLoad(GameObject value) { } }
    public static class Application {
        public static bool runInBackground { get; set; }
        public static int targetFrameRate { get; set; }
    }
    public static class QualitySettings { public static int vSyncCount { get; set; } }
    public static class Time { public static float timeScale { get; set; } }
}
namespace UnityEngine.SceneManagement {
    public struct Scene { public int handle; }
}
namespace HutongGames.PlayMaker {
    public class Fsm { public FsmState ActiveState; }
    public class FsmState { public object ActiveAction; }
    public class FsmGameObject { public UnityEngine.GameObject Value; }
}
namespace HutongGames.PlayMaker.Actions {
    public class TakeDamage { public FsmGameObject Target; }
}
public enum AttackTypes { Nail, Spell }
public struct HitInstance {
    public AttackTypes AttackType;
    public int DamageDealt;
    public UnityEngine.GameObject Source;
}
public class HeroController {
    public static HeroController instance;
    public readonly UnityEngine.Transform transform = new UnityEngine.Transform();
}
public class DamageHero { }
public class HealthManager {
    public int hp;
    public UnityEngine.GameObject gameObject;
    public DamageHero ChildDamage;
    public event System.Action OnDeath;
    public int DeathListeners => OnDeath?.GetInvocationList().Length ?? 0;
    public T GetComponentInChildren<T>(bool includeInactive) where T : class { return ChildDamage as T; }
    public void Die() { hp = 0; OnDeath?.Invoke(); }
}
namespace Modding {
    public abstract class Mod {
        public abstract string GetVersion();
        public abstract void Initialize(Dictionary<string, Dictionary<string, UnityEngine.GameObject>> value);
        protected void Log(string value) { }
    }
}
namespace InControl {
    public enum InputControlType { DPadLeft, DPadRight, DPadUp, DPadDown, Action1, Action2, Action3, Button22, Button23, Button24, Button25, Button28, Button29 }
    public class BindingSource { }
    public class DeviceBindingSource : BindingSource { public DeviceBindingSource(InputControlType type) { } }
    public class PlayerAction {
        public bool AddBinding(BindingSource binding) { return true; }
        public bool HasBinding(BindingSource binding) { return true; }
        public void RemoveBinding(BindingSource binding) { }
    }
    public class InputDevice {
        public readonly HashSet<InputControlType> RegisteredControls = new HashSet<InputControlType>();
        public readonly HashSet<InputControlType> HeldControls = new HashSet<InputControlType>();
        public InputDevice(string value) { }
        protected void AddControl(InputControlType type, string value) { RegisteredControls.Add(type); }
        protected void UpdateWithState(InputControlType type, bool value, ulong tick, float delta) {
            if (value) HeldControls.Add(type);
            else HeldControls.Remove(type);
        }
        protected void Commit(ulong tick, float delta) { }
        public virtual void Update(ulong tick, float delta) { }
    }
    public static class InputManager {
        public static bool SuspendInBackground { get; set; }
        public static void AttachDevice(InputDevice device) { }
        public static void DetachDevice(InputDevice device) { }
    }
}
public class HeroActions {
    public InControl.PlayerAction quickCast = new InControl.PlayerAction();
    public InControl.PlayerAction dash = new InControl.PlayerAction();
    public InControl.PlayerAction superDash = new InControl.PlayerAction();
    public InControl.PlayerAction dreamNail = new InControl.PlayerAction();
    public InControl.PlayerAction openInventory = new InControl.PlayerAction();
    public InControl.PlayerAction pause = new InControl.PlayerAction();
}
public class InputHandler {
    public static InputHandler Instance;
    public HeroActions inputActions;
}

public sealed class SceneData {
    public readonly List<GeoRockState> geoRocks = new List<GeoRockState>();
    public readonly List<PersistentItemState> persistentBoolItems = new List<PersistentItemState>();
    public readonly List<PersistentItemState> persistentIntItems = new List<PersistentItemState>();
}
public sealed class GeoRockState {
    public string sceneName;
}
public sealed class PersistentItemState {
    public string sceneName;
    public string id;
    public bool semiPersistent;
}
