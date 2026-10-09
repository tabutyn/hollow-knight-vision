#!/usr/bin/env python3
"""Manual protocol client. It sends no macOS input and never activates the game."""
import argparse
import json
import socket
import statistics
import threading
import time
import uuid

PORT = 36752
FLAGS = {
    "left": 1, "right": 2, "down": 4, "up": 8,
    "a": 16, "z": 32, "x": 64, "inventory": 128, "pause_menu": 256,
}


def send_line(sock, value):
    sock.sendall((json.dumps(value, separators=(",", ":")) + "\n").encode("utf-8"))


def normalized_buttons(buttons):
    """Mirror the receiver's opposing-direction normalization."""
    if buttons & 3 == 3:
        buttons &= ~3
    if buttons & 12 == 12:
        buttons &= ~12
    return buttons


def percentile(values, percentage):
    if len(values) == 1:
        return values[0]
    index = (len(values) - 1) * percentage
    lower = int(index)
    upper = min(lower + 1, len(values) - 1)
    return values[lower] + (values[upper] - values[lower]) * (index - lower)


class AcknowledgementCollector:
    """Continuously drains TCP ACKs so samples include only actual transit time."""

    def __init__(self, sock, session):
        self.sock = sock
        self.session = session
        self.pending = {}
        self.valid = {}
        self.mismatches = []
        self.unexpected = []
        self.complete_lines = 0
        self.parse_errors = 0
        self.lock = threading.Lock()
        self.changed = threading.Condition(self.lock)
        self.stopped = threading.Event()
        self.thread = threading.Thread(target=self._read, name="vision-input-ack-reader", daemon=True)

    def start(self):
        self.thread.start()

    def register(self, sequence, enabled, buttons):
        with self.changed:
            self.pending[sequence] = (time.perf_counter(), enabled, normalized_buttons(buttons) if enabled else 0)

    def _read(self):
        pending = b""
        while not self.stopped.is_set():
            try:
                chunk = self.sock.recv(4096)
                if not chunk:
                    return
                pending += chunk
                while b"\n" in pending:
                    line, pending = pending.split(b"\n", 1)
                    if line:
                        self._record_line(line)
            except socket.timeout:
                continue
            except OSError:
                return

    def _record_line(self, line):
        received_at = time.perf_counter()
        try:
            acknowledgement = json.loads(line)
        except (TypeError, ValueError):
            with self.changed:
                self.parse_errors += 1
                self.changed.notify_all()
            return

        with self.changed:
            self.complete_lines += 1
            sequence = acknowledgement.get("sequence")
            if acknowledgement.get("type") != "ack" or acknowledgement.get("sessionID") != self.session:
                self.unexpected.append("foreign or non-ACK line")
            elif sequence not in self.pending:
                self.unexpected.append("unexpected or duplicate sequence {}".format(sequence))
            else:
                sent_at, expected_enabled, expected_buttons = self.pending[sequence]
                actual_enabled = acknowledgement.get("enabled")
                actual_buttons = acknowledgement.get("appliedButtons")
                if acknowledgement.get("version") != 1 or actual_enabled != expected_enabled or actual_buttons != expected_buttons:
                    self.mismatches.append(
                        "sequence {} expected enabled={} buttons={}, got enabled={} buttons={}".format(
                            sequence, expected_enabled, expected_buttons, actual_enabled, actual_buttons))
                else:
                    self.valid[sequence] = (received_at - sent_at) * 1000.0
            self.changed.notify_all()

    def wait_for_all(self, timeout):
        deadline = time.monotonic() + timeout
        with self.changed:
            while len(self.valid) < len(self.pending):
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                self.changed.wait(remaining)

    def stop(self):
        self.stopped.set()
        try:
            self.sock.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        self.thread.join(timeout=1)

    def report(self):
        with self.lock:
            expected = sorted(self.pending)
            valid = sorted(self.valid)
            missing = [sequence for sequence in expected if sequence not in self.valid]
            samples = sorted(self.valid.values())
            return (expected, valid, missing, samples, list(self.mismatches), list(self.unexpected),
                    self.complete_lines, self.parse_errors)


def run_client(args, host="127.0.0.1", port=PORT):
    held = [name for name in FLAGS if getattr(args, name)]
    session = str(uuid.uuid4())
    try:
        sock = socket.create_connection((host, port), timeout=2)
    except OSError as error:
        print("connection failed: {}".format(error))
        return 1

    with sock:
        sock.settimeout(0.05)
        collector = AcknowledgementCollector(sock, session)
        collector.start()
        sequence = 0

        def state(enabled, buttons):
            nonlocal sequence
            sequence += 1
            buttons_mask = sum(FLAGS[name] for name in buttons)
            collector.register(sequence, enabled, buttons_mask)
            send_line(sock, {"version": 1, "type": "state", "sessionID": session,
                             "sequence": sequence, "enabled": enabled, "heldButtons": buttons_mask})

        try:
            if args.cycle:
                for _ in range(args.cycle):
                    state(True, ["right"])
                    time.sleep(1 / 60)
                    state(True, [])
                    time.sleep(1 / 60)
            else:
                state(True, held)
                time.sleep(args.seconds)
                if args.drop:
                    print("sent held state then disconnected; receiver watchdog should release within 250 ms")
                    return 0
                state(True, [])

            # A 30 Hz game commits one transition per tick. The reader has
            # already been draining during send, so this only waits for tail ACKs.
            collector.wait_for_all(max(2, sequence / 30 + 2))
            expected, valid, missing, samples, mismatches, unexpected, complete, parse_errors = collector.report()
            print("acks: {}/{} complete lines; unique valid sequences: {}/{}".format(
                complete, len(expected), len(valid), len(expected)))
            if samples:
                print("latency_ms: p50={:.2f} p95={:.2f} max={:.2f} ({} samples)".format(
                    statistics.median(samples), percentile(samples, 0.95), max(samples), len(samples)))
            if missing:
                print("missing or invalid ACK sequences: " + ", ".join(map(str, missing)))
            for mismatch in mismatches:
                print("mismatched ACK: " + mismatch)
            if unexpected:
                print("unexpected ACK lines: {}".format(len(unexpected)))
            if parse_errors:
                print("unparseable ACK lines: {}".format(parse_errors))
            return 0 if not missing and not mismatches and not unexpected and not parse_errors else 1
        except OSError as error:
            print("send failed: {}".format(error))
            return 1
        finally:
            collector.stop()


def receive_json_line(reader, expected_type, session, command_id=None):
    while True:
        line = reader.readline()
        if not line:
            raise RuntimeError("receiver disconnected before {}".format(expected_type))
        value = json.loads(line)
        if value.get("sessionID") != session:
            raise RuntimeError("{} came from unexpected session".format(value.get("type")))
        if value.get("type") == expected_type:
            if command_id is not None and value.get("commandID") != command_id:
                raise RuntimeError("{} command ID mismatch".format(expected_type))
            return value
        # Input transitions are acknowledged by InControl's later Commit.
        # That valid delayed line may precede a main-thread pause ACK.
        if value.get("type") == "ack" and expected_type in ("pauseAck", "resumeAck"):
            continue
        raise RuntimeError("expected {}, got {}".format(expected_type, value.get("type")))


def run_pause_test(seconds, drop, pause_after=None, host="127.0.0.1", port=PORT):
    """Negotiate pause support, renew its two-second lease, then resume."""
    session = str(uuid.uuid4())
    try:
        sock = socket.create_connection((host, port), timeout=2)
    except OSError as error:
        print("connection failed: {}".format(error))
        return 1

    try:
        with sock:
            sock.settimeout(2)
            reader = sock.makefile("r", encoding="utf-8", newline="\n")
            send_line(sock, {"version": 2, "type": "hello", "sessionID": session})
            capabilities = receive_json_line(reader, "capabilitiesAck", session)
            if capabilities.get("version") != 2 or "pause-lease-v1" not in capabilities.get("capabilities", []):
                raise RuntimeError("receiver does not advertise pause-lease-v1")
            lease_ms = capabilities.get("pauseLeaseMilliseconds")
            if lease_ms != 2000:
                raise RuntimeError("unexpected pause lease: {}".format(lease_ms))
            print("capability confirmed: pause-lease-v1, {} ms".format(lease_ms))

            if pause_after is not None:
                for sequence, buttons in ((1, FLAGS[pause_after]), (2, 0)):
                    send_line(sock, {"version": 1, "type": "state", "sessionID": session,
                                     "sequence": sequence, "enabled": True, "heldButtons": buttons})
                    acknowledgement = receive_json_line(reader, "ack", session)
                    if (acknowledgement.get("version") != 1
                            or acknowledgement.get("sequence") != sequence
                            or acknowledgement.get("appliedButtons") != buttons):
                        raise RuntimeError("{} priming input was not applied".format(pause_after.upper()))
                    if buttons:
                        time.sleep(0.09)
                # Catch jump during descent and attack after its first visible frame.
                time.sleep(0.20 if pause_after == "z" else 0.03)
                print("primed {} before pause".format("jump" if pause_after == "z" else "attack"))

            deadline = time.monotonic() + seconds
            acknowledged = 0
            while True:
                command_id = str(uuid.uuid4())
                send_line(sock, {"version": 2, "type": "pause", "sessionID": session,
                                 "commandID": command_id, "leaseMilliseconds": lease_ms})
                acknowledgement = receive_json_line(reader, "pauseAck", session, command_id)
                if acknowledgement.get("version") != 2 or acknowledgement.get("paused") is not True:
                    raise RuntimeError("game did not confirm paused state")
                acknowledged += 1
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    break
                time.sleep(min(0.5, remaining))

            if drop:
                print("pause confirmed {} times; disconnecting without resume".format(acknowledged))
                print("game must resume from lease/disconnect recovery within two seconds")
                return 0

            command_id = str(uuid.uuid4())
            send_line(sock, {"version": 2, "type": "resume", "sessionID": session,
                             "commandID": command_id})
            acknowledgement = receive_json_line(reader, "resumeAck", session, command_id)
            if acknowledgement.get("version") != 2 or acknowledgement.get("paused") is not False:
                raise RuntimeError("game did not confirm resumed state")
            print("pause confirmed {} times; resume confirmed".format(acknowledged))
            return 0
    except (OSError, RuntimeError, ValueError) as error:
        print("pause test failed: {}".format(error))
        return 1


def run_self_check():
    """Exercise chunked, concurrent ACK reception without contacting the game."""
    listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    listener.bind(("127.0.0.1", 0))
    listener.listen(1)
    port = listener.getsockname()[1]
    server_error = []

    def fake_receiver():
        try:
            connection, _ = listener.accept()
            with connection:
                buffer = b""
                while True:
                    data = connection.recv(4096)
                    if not data:
                        return
                    buffer += data
                    while b"\n" in buffer:
                        line, buffer = buffer.split(b"\n", 1)
                        state = json.loads(line)
                        buttons = normalized_buttons(state["heldButtons"]) if state["enabled"] else 0
                        acknowledgement = json.dumps({
                            "version": 1, "type": "ack", "sessionID": state["sessionID"],
                            "sequence": state["sequence"], "enabled": state["enabled"], "appliedButtons": buttons,
                        }, separators=(",", ":")).encode("utf-8") + b"\n"
                        # Deliberately split writes to cover arbitrary TCP chunks.
                        midpoint = len(acknowledgement) // 2
                        connection.sendall(acknowledgement[:midpoint])
                        connection.sendall(acknowledgement[midpoint:])
        except Exception as error:  # pragma: no cover - reported by the check
            server_error.append(error)

    thread = threading.Thread(target=fake_receiver, name="vision-input-fake-receiver", daemon=True)
    thread.start()
    args = argparse.Namespace(left=False, right=False, up=False, down=False, a=False, z=False, x=False,
                              inventory=False, pause_menu=False,
                              seconds=0.0, cycle=8, drop=False)
    try:
        result = run_client(args, port=port)
    finally:
        listener.close()
        thread.join(timeout=1)
    if server_error:
        print("self-check server failed: {}".format(server_error[0]))
        return 1
    print("self-check: {}".format("passed" if result == 0 else "failed"))
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--left", action="store_true")
    parser.add_argument("--right", action="store_true")
    parser.add_argument("--up", action="store_true")
    parser.add_argument("--down", action="store_true")
    parser.add_argument("--a", action="store_true", help="send the A (cast) binding")
    parser.add_argument("--z", action="store_true", help="send the Z (jump) binding")
    parser.add_argument("--x", action="store_true", help="send the X (attack) binding")
    parser.add_argument("--inventory", action="store_true", help="open Hollow Knight's inventory")
    parser.add_argument("--pause-menu", action="store_true", help="open Hollow Knight's pause menu")
    parser.add_argument("--seconds", type=float, default=1.0)
    parser.add_argument("--cycle", type=int, default=0, help="send press/release pairs")
    parser.add_argument("--drop", action="store_true", help="disconnect held; watchdog must release within 250 ms")
    parser.add_argument("--pause-seconds", type=float, help="pause game with a renewed two-second lease")
    parser.add_argument("--pause-drop", action="store_true", help="disconnect while paused; game must auto-resume")
    parser.add_argument("--pause-after", choices=("z", "x"), help="prime jump or attack immediately before pause")
    parser.add_argument("--port", type=int, default=PORT, help="localhost receiver port (default: %(default)s)")
    parser.add_argument("--self-check", action="store_true", help="test ACK timing against a local fake receiver")
    args = parser.parse_args()
    if args.self_check:
        return run_self_check()
    if args.seconds < 0 or args.cycle < 0 or not 1 <= args.port <= 65535:
        parser.error("--seconds and --cycle must not be negative; --port must be 1-65535")
    if args.pause_seconds is not None:
        if args.pause_seconds < 0:
            parser.error("--pause-seconds must not be negative")
        if any(getattr(args, name) for name in FLAGS) or args.cycle or args.drop:
            parser.error("pause test cannot be combined with input test options")
        return run_pause_test(args.pause_seconds, args.pause_drop, args.pause_after, port=args.port)
    if args.pause_drop or args.pause_after:
        parser.error("--pause-drop and --pause-after require --pause-seconds")
    if not any(getattr(args, name) for name in FLAGS) and not args.cycle:
        parser.error("choose a button or --cycle")
    return run_client(args, port=args.port)


if __name__ == "__main__":
    raise SystemExit(main())
