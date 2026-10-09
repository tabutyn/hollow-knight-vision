using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace HollowKnightVisionInputReceiver
{
    // Owns the bounded transition path independently from TCP so its timing
    // behavior can be tested without listening on a port.
    internal sealed class InputStateQueue
    {
        internal const int MaxTransitions = 256;
        private readonly Queue<InputSnapshot> transitions = new Queue<InputSnapshot>();
        private string session;
        private long lastSequence = -1;
        private long lastReceiptTicks = -1;
        private bool hasState;
        private bool enabled;
        private VisionButtons buttons;

        internal bool Accept(InputSnapshot snapshot, long nowTicks)
        {
            if (session == null) session = snapshot.Session;
            if (!String.Equals(session, snapshot.Session, StringComparison.Ordinal) || snapshot.Sequence <= lastSequence) return false;

            lastSequence = snapshot.Sequence;
            lastReceiptTicks = nowTicks;
            var nextEnabled = snapshot.Enabled;
            var nextButtons = nextEnabled ? InputSnapshot.Normalize(snapshot.Buttons) : VisionButtons.None;
            if (hasState && enabled == nextEnabled && buttons == nextButtons) return true; // heartbeat: freshness only

            hasState = true;
            enabled = nextEnabled;
            buttons = nextButtons;
            if (!nextEnabled) {
                // Disable wins over every delayed press/release.
                transitions.Clear();
                transitions.Enqueue(Copy(snapshot, false, VisionButtons.None));
                return true;
            }

            if (transitions.Count >= MaxTransitions - 1) {
                // The newest complete state is safer than stale input. Keep a
                // neutral boundary before it, so an overflow cannot stick a key.
                transitions.Clear();
                if (nextButtons != VisionButtons.None) transitions.Enqueue(Copy(snapshot, true, VisionButtons.None));
            }
            transitions.Enqueue(Copy(snapshot, true, nextButtons));
            return true;
        }

        internal bool TryTake(out InputSnapshot snapshot)
        {
            if (transitions.Count == 0) { snapshot = null; return false; }
            snapshot = transitions.Dequeue();
            return true;
        }

        internal bool ExpireIfStale(long nowTicks, long timeoutTicks)
        {
            if (lastReceiptTicks < 0 || nowTicks - lastReceiptTicks <= timeoutTicks || !hasState || (!enabled && buttons == VisionButtons.None)) return false;
            ForceNeutral();
            return true;
        }

        internal void Disconnect()
        {
            // Retain a neutral transition for an active old session, but forget
            // its coalescing state. The next connection can then establish its
            // first (even disabled/neutral) snapshot and receive its own ACK.
            ForceNeutral();
            session = null;
            lastSequence = -1;
            lastReceiptTicks = -1;
            hasState = false;
            enabled = false;
            buttons = VisionButtons.None;
        }
        internal int PendingTransitions { get { return transitions.Count; } }

        private void ForceNeutral()
        {
            if (!hasState || (!enabled && buttons == VisionButtons.None)) return;
            enabled = false;
            buttons = VisionButtons.None;
            transitions.Clear();
            transitions.Enqueue(new InputSnapshot { Session = session, Sequence = lastSequence, Enabled = false, Buttons = VisionButtons.None });
        }

        private static InputSnapshot Copy(InputSnapshot source, bool isEnabled, VisionButtons nextButtons)
        {
            return new InputSnapshot { Session = source.Session, Sequence = source.Sequence, Enabled = isEnabled, Buttons = nextButtons };
        }
    }

    internal sealed class AckQueue
    {
        internal const int MaxPending = 128;
        private readonly Queue<string> lines = new Queue<string>();

        internal void Enqueue(string line)
        {
            // ACKs are observability only. Dropping the oldest acknowledgement
            // keeps gameplay independent of a peer that stops reading.
            if (lines.Count == MaxPending) lines.Dequeue();
            lines.Enqueue(line);
        }

        internal bool TryTake(out string line)
        {
            if (lines.Count == 0) { line = null; return false; }
            line = lines.Dequeue();
            return true;
        }

        internal void Clear() { lines.Clear(); }
        internal int Count { get { return lines.Count; } }
    }

    internal sealed class PauseRequestQueue
    {
        internal const int MaxPending = 64;
        private readonly Queue<PauseRequest> requests = new Queue<PauseRequest>();

        internal bool Accept(PauseRequest request)
        {
            if (request.Kind == PauseCommandKind.Resume) requests.Clear();
            if (requests.Count == MaxPending) requests.Dequeue();
            requests.Enqueue(request);
            return true;
        }

        internal bool TryTake(out PauseRequest request)
        {
            if (requests.Count == 0) { request = null; return false; }
            request = requests.Dequeue();
            return true;
        }

        internal void Disconnect(string session)
        {
            // Serve calls DisconnectLocked once more while accepting the next
            // client. Do not erase an old client's forced resume before Unity
            // main thread has consumed it.
            if (String.IsNullOrEmpty(session)) return;
            requests.Clear();
            requests.Enqueue(PauseRequest.Disconnect(session));
        }

        internal int Count { get { return requests.Count; } }
    }

    internal sealed class PointerRequestQueue
    {
        internal const int MaxPending = 512;
        private readonly Queue<PointerRequest> requests = new Queue<PointerRequest>();
        private string session;
        private long lastSequence = -1;

        internal bool Accept(PointerRequest request)
        {
            if (session == null) session = request.Session;
            if (!String.Equals(session, request.Session, StringComparison.Ordinal)
                || request.Sequence <= lastSequence) return false;
            lastSequence = request.Sequence;
            if (requests.Count == MaxPending) {
                // Movement is replaceable; button edges are not. Drop excess
                // motion first and otherwise make room for the newest edge.
                if (request.Kind == "moved" || request.Kind == "leftDragged") return true;
                requests.Dequeue();
            }
            requests.Enqueue(request);
            return true;
        }

        internal bool TryTake(out PointerRequest request)
        {
            if (requests.Count == 0) { request = null; return false; }
            request = requests.Dequeue();
            return true;
        }

        internal void Disconnect(string disconnectedSession)
        {
            if (String.IsNullOrEmpty(disconnectedSession)) return;
            requests.Clear();
            requests.Enqueue(PointerRequest.Disconnect(disconnectedSession));
            session = null;
            lastSequence = -1;
        }

        internal int Count { get { return requests.Count; } }
    }

    internal sealed class CheckpointRequestQueue
    {
        internal const int MaxPending = 16;
        private readonly Queue<CheckpointRequest> requests = new Queue<CheckpointRequest>();

        internal bool Accept(CheckpointRequest request)
        {
            if (requests.Count == MaxPending) requests.Dequeue();
            requests.Enqueue(request);
            return true;
        }

        internal bool TryTake(out CheckpointRequest request)
        {
            if (requests.Count == 0) { request = null; return false; }
            request = requests.Dequeue();
            return true;
        }

        internal void Disconnect() { requests.Clear(); }
        internal int Count { get { return requests.Count; } }
    }

    internal sealed class PlayerOpsRequestQueue
    {
        internal const int MaxPending = 16;
        private readonly Queue<PlayerOpsRequest> requests = new Queue<PlayerOpsRequest>();

        internal bool Accept(PlayerOpsRequest request)
        {
            if (requests.Count == MaxPending) requests.Dequeue();
            requests.Enqueue(request);
            return true;
        }

        internal bool TryTake(out PlayerOpsRequest request)
        {
            if (requests.Count == 0) { request = null; return false; }
            request = requests.Dequeue();
            return true;
        }

        internal void Disconnect() { requests.Clear(); }
        internal int Count { get { return requests.Count; } }
    }

    /// Player poses are sampled state, not an event stream. If Vision gets a
    /// frame ahead of Unity, only the newest pose is useful.
    internal sealed class PlayerPoseRequestQueue
    {
        private PlayerPoseRequest pending;
        private string session;
        private long lastSequence = -1;

        internal bool Accept(PlayerPoseRequest request)
        {
            if (session == null) session = request.Session;
            if (!String.Equals(session, request.Session, StringComparison.Ordinal)
                || request.Sequence <= lastSequence) return false;
            lastSequence = request.Sequence;
            pending = request;
            return true;
        }

        internal bool TryTake(out PlayerPoseRequest request)
        {
            request = pending;
            pending = null;
            return request != null;
        }

        internal void Disconnect()
        {
            pending = null;
            session = null;
            lastSequence = -1;
        }

        internal int Count { get { return pending == null ? 0 : 1; } }
    }

    internal sealed class LocalInputServer : IDisposable
    {
        private const int Port = 36752;
        private const int MaxLineLength = 4096;
        private static readonly long WatchdogTicks = Stopwatch.Frequency / 4;
        private readonly object gate = new object();
        private readonly InputStateQueue states = new InputStateQueue();
        private readonly PauseRequestQueue pauseRequests = new PauseRequestQueue();
        private readonly PointerRequestQueue pointerRequests = new PointerRequestQueue();
        private readonly CheckpointRequestQueue checkpointRequests = new CheckpointRequestQueue();
        private readonly PlayerOpsRequestQueue playerOpsRequests = new PlayerOpsRequestQueue();
        private readonly PlayerPoseRequestQueue playerPoseRequests = new PlayerPoseRequestQueue();
        private readonly AckQueue acknowledgements = new AckQueue();
        private readonly AutoResetEvent ackSignal = new AutoResetEvent(false);
        private TcpListener listener;
        private Thread listenerThread;
        private Thread writerThread;
        private NetworkStream clientStream;
        private string session;
        // Telemetry is state, not an event stream. Keep only the newest sample
        // so a slow reader cannot delay control acknowledgements or gameplay.
        private string pendingTelemetry;
        private int clientGeneration;
        private bool disposed;
        private volatile bool renderFrameMarker;
        internal bool RenderFrameMarker { get { return renderFrameMarker; } }

        internal void Start()
        {
            listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start(1);
            listenerThread = new Thread(Listen) { IsBackground = true, Name = "HK Vision input receiver" };
            writerThread = new Thread(WriteAcknowledgements) { IsBackground = true, Name = "HK Vision input acknowledgements" };
            listenerThread.Start();
            writerThread.Start();
        }

        internal bool TryTakeSnapshot(out InputSnapshot snapshot)
        {
            lock (gate) return states.TryTake(out snapshot);
        }

        internal bool ExpireIfStale()
        {
            lock (gate) return states.ExpireIfStale(Stopwatch.GetTimestamp(), WatchdogTicks);
        }

        internal bool TryTakePauseRequest(out PauseRequest request)
        {
            lock (gate) return pauseRequests.TryTake(out request);
        }

        internal bool TryTakePointerRequest(out PointerRequest request)
        {
            lock (gate) return pointerRequests.TryTake(out request);
        }

        internal bool TryTakeCheckpointRequest(out CheckpointRequest request)
        {
            lock (gate) return checkpointRequests.TryTake(out request);
        }

        internal bool TryTakePlayerPoseRequest(out PlayerPoseRequest request)
        {
            lock (gate) return playerPoseRequests.TryTake(out request);
        }

        internal bool TryTakePlayerOpsRequest(out PlayerOpsRequest request)
        {
            lock (gate) return playerOpsRequests.TryTake(out request);
        }

        // Called after VisionInputDevice.Commit, never from the socket thread.
        internal void QueueAck(string ackSession, long sequence, bool enabled, VisionButtons effectiveButtons)
        {
            lock (gate) {
                if (clientStream == null || !String.Equals(session, ackSession, StringComparison.Ordinal)) return;
                QueueLineLocked(InputSnapshot.Ack(ackSession, sequence, enabled, effectiveButtons));
            }
        }


        // Called only after ReceiverBehaviour applies a pause command on Unity's main thread.
        internal void QueuePauseAck(PauseRequest request, bool paused)
        {
            lock (gate) {
                if (request.IsDisconnect || clientStream == null
                    || !String.Equals(session, request.Session, StringComparison.Ordinal)) return;
                QueueLineLocked(PauseRequest.Ack(request, paused));
            }
        }

        // Called only after ReceiverBehaviour captures/restores on Unity's main thread.
        internal void QueueCheckpointAck(
            CheckpointRequest request,
            bool accepted,
            PlayerCheckpoint checkpoint,
            string failure)
        {
            lock (gate) {
                if (clientStream == null
                    || !String.Equals(session, request.Session, StringComparison.Ordinal)) return;
                QueueLineLocked(CheckpointRequest.Ack(request, accepted, checkpoint, failure));
            }
        }

        internal void QueuePlayerOpsAck(
            PlayerOpsRequest request,
            bool accepted,
            PlayerTestState state,
            int? enemiesRestored,
            string failure)
        {
            lock (gate) {
                if (clientStream == null
                    || !String.Equals(session, request.Session, StringComparison.Ordinal)) return;
                QueueLineLocked(PlayerOpsRequest.Ack(
                    request, accepted, state, enemiesRestored, failure));
            }
        }

        internal void QueueGroundTruth(GroundTruthTelemetry telemetry)
        {
            lock (gate) {
                if (clientStream == null || session == null || telemetry == null) return;
                pendingTelemetry = telemetry.ToJson(session) + "\n";
                ackSignal.Set();
            }
        }

        private void Listen()
        {
            while (!disposed) {
                TcpClient client = null;
                try {
                    client = listener.AcceptTcpClient();
                    // Unity 6's macOS Mono socket shim can reject TCP_NODELAY
                    // on an accepted loopback socket. Low-latency mode is an
                    // optimization; failure must not kill the receiver thread.
                    try { client.NoDelay = true; }
                    catch (ArgumentException) { }
                    Serve(client);
                }
                catch (SocketException) { if (disposed) return; }
                catch (ObjectDisposedException) { return; }
                finally { if (client != null) client.Close(); }
            }
        }

        private void Serve(TcpClient client)
        {
            var stream = client.GetStream();
            lock (gate) {
                DisconnectLocked();
                clientStream = stream;
                clientGeneration++;
            }
            try {
                using (var reader = new StreamReader(stream, new UTF8Encoding(false), false, MaxLineLength, true)) {
                    while (!disposed) {
                        var line = reader.ReadLine();
                        if (line == null || line.Length > MaxLineLength) break;
                        AcceptLine(line);
                    }
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            lock (gate) {
                if (ReferenceEquals(clientStream, stream)) DisconnectLocked();
            }
        }

        private void AcceptLine(string line)
        {
            string type;
            try { type = Newtonsoft.Json.Linq.JObject.Parse(line).Value<string>("type"); }
            catch { lock (gate) DisconnectLocked(); return; }

            if (type == "hello") {
                CapabilityRequest capability;
                if (!CapabilityRequest.TryParse(line, out capability)) { lock (gate) DisconnectLocked(); return; }
                lock (gate) {
                    if (!AcceptSessionLocked(capability.Session)) return;
                    renderFrameMarker = capability.RenderFrameMarker;
                    QueueLineLocked(CapabilityRequest.Ack(capability.Session));
                }
                return;
            }
            if (type == "pause" || type == "resume") {
                PauseRequest pause;
                if (!PauseRequest.TryParse(line, out pause)) { lock (gate) DisconnectLocked(); return; }
                lock (gate) {
                    if (!AcceptSessionLocked(pause.Session)) return;
                    pauseRequests.Accept(pause);
                }
                return;
            }
            if (type == "pointer") {
                PointerRequest pointer;
                if (!PointerRequest.TryParse(line, out pointer)) { lock (gate) DisconnectLocked(); return; }
                lock (gate) {
                    if (!AcceptSessionLocked(pointer.Session)) return;
                    pointerRequests.Accept(pointer);
                }
                return;
            }
            if (type == "captureCheckpoint" || type == "restoreCheckpoint") {
                CheckpointRequest checkpoint;
                if (!CheckpointRequest.TryParse(line, out checkpoint)) { lock (gate) DisconnectLocked(); return; }
                lock (gate) {
                    if (!AcceptSessionLocked(checkpoint.Session)) return;
                    checkpointRequests.Accept(checkpoint);
                }
                return;
            }
            if (type == "playerPose") {
                PlayerPoseRequest pose;
                if (!PlayerPoseRequest.TryParse(line, out pose)) { lock (gate) DisconnectLocked(); return; }
                lock (gate) {
                    if (!AcceptSessionLocked(pose.Session)) return;
                    playerPoseRequests.Accept(pose);
                }
                return;
            }
            if (type == "playerOps") {
                PlayerOpsRequest playerOps;
                if (!PlayerOpsRequest.TryParse(line, out playerOps)) { lock (gate) DisconnectLocked(); return; }
                lock (gate) {
                    if (!AcceptSessionLocked(playerOps.Session)) return;
                    playerOpsRequests.Accept(playerOps);
                }
                return;
            }

            InputSnapshot snapshot;
            if (!InputSnapshot.TryParse(line, out snapshot)) { lock (gate) DisconnectLocked(); return; }
            lock (gate) {
                if (!AcceptSessionLocked(snapshot.Session)) return;
                states.Accept(snapshot, Stopwatch.GetTimestamp());
            }
        }

        private bool AcceptSessionLocked(string requestedSession)
        {
            if (session == null) session = requestedSession;
            if (String.Equals(session, requestedSession, StringComparison.Ordinal)) return true;
            DisconnectLocked();
            return false;
        }

        private void QueueLineLocked(string line)
        {
            acknowledgements.Enqueue(line + "\n");
            ackSignal.Set();
        }

        private void WriteAcknowledgements()
        {
            while (!disposed) {
                ackSignal.WaitOne(100);
                while (!disposed) {
                    NetworkStream stream;
                    string line;
                    int generation;
                    lock (gate) {
                        if (!acknowledgements.TryTake(out line)) {
                            line = pendingTelemetry;
                            pendingTelemetry = null;
                        }
                        if (line == null || clientStream == null) break;
                        stream = clientStream;
                        generation = clientGeneration;
                    }
                    try {
                        var bytes = Encoding.UTF8.GetBytes(line);
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush();
                    }
                    catch {
                        lock (gate) { if (ReferenceEquals(clientStream, stream) && generation == clientGeneration) DisconnectLocked(); }
                        break;
                    }
                }
            }
        }

        private void DisconnectLocked()
        {
            states.Disconnect();
            pauseRequests.Disconnect(session);
            pointerRequests.Disconnect(session);
            checkpointRequests.Disconnect();
            playerOpsRequests.Disconnect();
            playerPoseRequests.Disconnect();
            session = null;
            renderFrameMarker = false;
            acknowledgements.Clear();
            pendingTelemetry = null;
            if (clientStream != null) { try { clientStream.Close(); } catch { } clientStream = null; }
            ackSignal.Set();
        }

        public void Dispose()
        {
            disposed = true;
            lock (gate) DisconnectLocked();
            if (listener != null) listener.Stop();
            ackSignal.Set();
        }
    }
}
