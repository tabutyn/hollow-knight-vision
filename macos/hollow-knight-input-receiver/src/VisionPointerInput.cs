using UnityEngine;
using UnityEngine.EventSystems;

namespace HollowKnightVisionInputReceiver
{
    /// Feeds the game's existing StandaloneInputModule with real button edges.
    /// A press/release delivered in one receiver tick is stretched across two
    /// Unity ticks so neither edge disappears before the UI module polls it.
    internal sealed class VisionPointerInput : BaseInput
    {
        private Vector2 position = new Vector2(-100f, -100f);
        private bool pressed;
        private bool downThisFrame;
        private bool upThisFrame;
        private bool releaseNextFrame;

        public override bool mousePresent { get { return true; } }
        public override Vector2 mousePosition { get { return position; } }
        public override Vector2 mouseScrollDelta { get { return Vector2.zero; } }

        public override bool GetMouseButtonDown(int button)
        {
            return button == 0 && downThisFrame;
        }

        public override bool GetMouseButtonUp(int button)
        {
            return button == 0 && upThisFrame;
        }

        public override bool GetMouseButton(int button)
        {
            return button == 0 && pressed;
        }

        internal void BeginFrame()
        {
            downThisFrame = false;
            upThisFrame = false;
            if (!releaseNextFrame) return;
            pressed = false;
            upThisFrame = true;
            releaseNextFrame = false;
        }

        internal void Apply(PointerRequest request)
        {
            if (request.Kind == "exited") {
                ResetState();
                return;
            }

            position = new Vector2(
                Mathf.Clamp01((float)request.NormalizedX) * Screen.width,
                Mathf.Clamp01((float)request.NormalizedY) * Screen.height
            );
            if (request.Kind == "leftDown") {
                pressed = true;
                downThisFrame = true;
            } else if (request.Kind == "leftUp") {
                if (downThisFrame) {
                    releaseNextFrame = true;
                } else {
                    pressed = false;
                    upThisFrame = true;
                }
            }
        }

        internal void ResetState()
        {
            position = new Vector2(-100f, -100f);
            pressed = false;
            downThisFrame = false;
            upThisFrame = false;
            releaseNextFrame = false;
        }
    }
}
