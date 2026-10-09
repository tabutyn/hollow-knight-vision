using InControl;

namespace HollowKnightVisionInputReceiver
{
    // Public InControl extension point: no Harmony/MonoMod runtime hooks.
    internal sealed class VisionInputDevice : InputDevice
    {
        private VisionButtons buttons;
        private bool enabled;
        private readonly CommitAckGate commitAck = new CommitAckGate();
        internal System.Action<string, long, bool, VisionButtons> Committed;

        internal VisionInputDevice() : base("Hollow Knight Vision")
        {
            AddControl(InputControlType.DPadLeft, "Vision Left");
            AddControl(InputControlType.DPadRight, "Vision Right");
            AddControl(InputControlType.DPadUp, "Vision Up");
            AddControl(InputControlType.DPadDown, "Vision Down");
            AddControl(InputControlType.Action1, "Vision Z (Jump)");
            // Hollow Knight 1.5 default ControllerMapping: Action2 = cast,
            // Action3 = attack. Action1 remains jump.
            AddControl(InputControlType.Action2, "Vision A (Cast)");
            AddControl(InputControlType.Action3, "Vision X (Attack)");
            // Private controls are bound directly to HeroActions by the
            // receiver. Standard controller aliases overlap across profiles.
            AddControl(InputControlType.Button28, "Vision Inventory");
            AddControl(InputControlType.Button29, "Vision Pause Menu");
        }

        internal void SetState(string stateSession, long stateSequence, bool isEnabled, VisionButtons next)
        {
            enabled = isEnabled;
            buttons = isEnabled ? InputSnapshot.Normalize(next) : VisionButtons.None;
            commitAck.Set(stateSession, stateSequence, enabled, buttons);
        }

        public override void Update(ulong updateTick, float deltaTime)
        {
            // Opposite directions intentionally remain neutral to avoid oscillation.
            UpdateWithState(InputControlType.DPadLeft, Has(VisionButtons.Left) && !Has(VisionButtons.Right), updateTick, deltaTime);
            UpdateWithState(InputControlType.DPadRight, Has(VisionButtons.Right) && !Has(VisionButtons.Left), updateTick, deltaTime);
            UpdateWithState(InputControlType.DPadUp, Has(VisionButtons.Up) && !Has(VisionButtons.Down), updateTick, deltaTime);
            UpdateWithState(InputControlType.DPadDown, Has(VisionButtons.Down) && !Has(VisionButtons.Up), updateTick, deltaTime);
            // Preserve Hollow Knight's default A/Z/X keyboard meanings.
            UpdateWithState(InputControlType.Action1, Has(VisionButtons.ActionZ), updateTick, deltaTime);
            UpdateWithState(InputControlType.Action2, Has(VisionButtons.ActionA), updateTick, deltaTime);
            UpdateWithState(InputControlType.Action3, Has(VisionButtons.ActionX), updateTick, deltaTime);
            UpdateWithState(InputControlType.Button28, Has(VisionButtons.Inventory), updateTick, deltaTime);
            UpdateWithState(InputControlType.Button29, Has(VisionButtons.PauseMenu), updateTick, deltaTime);
            Commit(updateTick, deltaTime);
            var committed = Committed;
            string session;
            long sequence;
            bool committedEnabled;
            VisionButtons committedButtons;
            if (commitAck.TryTake(out session, out sequence, out committedEnabled, out committedButtons) && committed != null) committed(session, sequence, committedEnabled, committedButtons);
        }

        private bool Has(VisionButtons button) { return (buttons & button) != 0; }
    }
}
