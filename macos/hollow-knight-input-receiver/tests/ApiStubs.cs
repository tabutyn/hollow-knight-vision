// Test-only compile surface for the public calls used by the receiver.
// This file is not included by src/HollowKnightVisionInputReceiver.csproj.
using System.Collections.Generic;

namespace UnityEngine {
    public sealed class DefaultExecutionOrder : System.Attribute {
        public DefaultExecutionOrder(int order) { }
    }
    public class MonoBehaviour { }
    public class GameObject { public GameObject(string value) { } public T AddComponent<T>() where T : new() { return new T(); } }
    public class Object { public static void DontDestroyOnLoad(GameObject value) { } }
    public static class Application {
        public static bool runInBackground { get; set; }
        public static int targetFrameRate { get; set; }
    }
    public static class QualitySettings { public static int vSyncCount { get; set; } }
    public static class Time { public static float timeScale { get; set; } }
}
namespace Modding {
    public abstract class Mod {
        public abstract string GetVersion();
        public abstract void Initialize(Dictionary<string, Dictionary<string, UnityEngine.GameObject>> value);
        protected void Log(string value) { }
    }
}
namespace InControl {
    public enum InputControlType { DPadLeft, DPadRight, DPadUp, DPadDown, Action1, Action2, Action3, Button28, Button29 }
    public class BindingSource { }
    public class DeviceBindingSource : BindingSource { public DeviceBindingSource(InputControlType type) { } }
    public class PlayerAction {
        public bool AddBinding(BindingSource binding) { return true; }
        public bool HasBinding(BindingSource binding) { return true; }
        public void RemoveBinding(BindingSource binding) { }
    }
    public class InputDevice {
        public InputDevice(string value) { }
        protected void AddControl(InputControlType type, string value) { }
        protected void UpdateWithState(InputControlType type, bool value, ulong tick, float delta) { }
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
    public InControl.PlayerAction openInventory = new InControl.PlayerAction();
    public InControl.PlayerAction pause = new InControl.PlayerAction();
}
public class InputHandler {
    public static InputHandler Instance;
    public HeroActions inputActions;
}
