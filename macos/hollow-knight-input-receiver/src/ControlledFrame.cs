using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HollowKnightVisionInputReceiver
{
    // A command advances one simulation tick. It never supplies camera truth.
    internal sealed class ControlledFrameRequest
    {
        internal string Session, CommandID;
        internal long Sequence;
        internal bool End;
        internal double Delta;
        internal VisionButtons Buttons;
        internal PlayerPoseRequest Pose;
        internal static bool TryParse(string json, out ControlledFrameRequest request)
        {
            request = null;
            try {
                var v = JObject.Parse(json);
                var kind = v.Value<string>("kind");
                var session = v.Value<string>("sessionID");
                var command = v.Value<string>("commandID");
                var sequence = v.Value<long?>("sequence");
                Guid id;
                if (v.Value<int>("version") != 2 || v.Value<string>("type") != "controlledFrame"
                    || String.IsNullOrWhiteSpace(session) || !Guid.TryParse(command, out id)
                    || !sequence.HasValue || sequence < 0 || (kind != "advance" && kind != "end")) return false;
                var parsed = new ControlledFrameRequest { Session = session, CommandID = command,
                    Sequence = sequence.Value, End = kind == "end" };
                if (!parsed.End) {
                    var delta = v.Value<double?>("deltaSeconds");
                    var buttons = v.Value<int?>("heldButtons");
                    if (!delta.HasValue || Double.IsNaN(delta.Value) || delta <= 0 || delta > 1.0 / 30 + 1e-8
                        || !buttons.HasValue || buttons < 0 || (buttons & ~511) != 0) return false;
                    var pose = v["pose"] as JObject;
                    if (pose == null || !PlayerPoseRequest.TryParse(pose.ToString(Formatting.None), out parsed.Pose)
                        || parsed.Pose.Session != session || parsed.Pose.Sequence != sequence.Value) return false;
                    parsed.Delta = delta.Value;
                    parsed.Buttons = InputSnapshot.Normalize((VisionButtons)buttons.Value);
                }
                request = parsed; return true;
            } catch { return false; }
        }
        internal string Ack(bool accepted, int frame, double delta, string failure)
        {
            return new JObject { ["version"] = 2, ["type"] = "controlledFrameAck", ["sessionID"] = Session,
                ["commandID"] = CommandID, ["accepted"] = accepted, ["unityFrame"] = frame,
                ["deltaSeconds"] = delta, ["failure"] = failure }.ToString(Formatting.None);
        }
    }

    // At most one command waits for Unity. Never silently drop or repeat a step.
    internal sealed class ControlledFrameQueue
    {
        private ControlledFrameRequest pending;
        private long sequence = -1;
        internal bool Accept(ControlledFrameRequest request)
        {
            if (request.Sequence <= sequence || (pending != null && !request.End)) return false;
            sequence = request.Sequence; pending = request; return true;
        }
        internal bool TryTake(out ControlledFrameRequest request)
        {
            request = pending; pending = null; return request != null;
        }
        internal void Clear() { pending = null; sequence = -1; }
    }
}
