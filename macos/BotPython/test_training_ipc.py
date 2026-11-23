"""Exercise native subprocess pipes and file locking on both trainer hosts."""
from pathlib import Path
import subprocess
import sys
from tempfile import TemporaryDirectory
import unittest

from bot_policy_continuation import publication_lock
from bot_training_ipc import EncoderReplies


PROBE = """
from pathlib import Path
import sys
import time
from bot_training_ipc import exclusive_file_lock

path, mode = sys.argv[1:]
try:
    with exclusive_file_lock(Path(path), wait=mode != "try",
                             deadline=time.monotonic() + .15 if mode == "deadline" else None):
        print("acquired")
except BlockingIOError:
    print("busy")
except TimeoutError:
    print("expired")
"""


class TrainingLockTests(unittest.TestCase):
    def test_publication_serializes_other_processes_and_releases_on_exception(self):
        with TemporaryDirectory() as temporary:
            models = Path(temporary) / "models"
            lock = models / ".publication.lock"
            with self.assertRaisesRegex(ValueError, "test failure inside publication"):
                with publication_lock(models):
                    self.assertEqual(self.probe(lock, "try"), "busy")
                    self.assertEqual(self.probe(lock, "deadline"), "expired")
                    raise ValueError("test failure inside publication")
            self.assertEqual(self.probe(lock, "try"), "acquired")

    @staticmethod
    def probe(path, mode):
        result = subprocess.run([sys.executable, "-B", "-c", PROBE, str(path), mode],
                                cwd=Path(__file__).resolve().parent,
                                capture_output=True, text=True, timeout=5, check=True)
        return result.stdout.strip()


class EncoderRepliesTests(unittest.TestCase):
    def test_partial_reply_at_eof_is_rejected(self):
        replies = EncoderReplies()
        script = "import sys; sys.stdin.buffer.readline(); sys.stdout.write('{}')"
        with subprocess.Popen([sys.executable, "-B", "-c", script], stdin=subprocess.PIPE,
                              stdout=subprocess.PIPE) as process:
            try:
                with self.assertRaises(EOFError):
                    replies.request(process, b"request\n", timeout=5)
            finally:
                process.wait(timeout=5)
                replies.close()

    def test_subprocess_reply_requires_a_complete_line_and_times_out_on_partial_reply(self):
        replies = EncoderReplies()
        stall = ("import sys, time; sys.stdin.buffer.readline(); "
                 "sys.stdout.buffer.write(b'partial'); sys.stdout.buffer.flush(); time.sleep(5)")
        with subprocess.Popen([sys.executable, "-B", "-c", stall], stdin=subprocess.PIPE,
                              stdout=subprocess.PIPE) as process:
            try:
                with self.assertRaises(TimeoutError):
                    replies.request(process, b"request\n", timeout=.25)
                with self.assertRaisesRegex(RuntimeError, "must be restarted"):
                    replies.request(process, b"next request\n", timeout=.25)
            finally:
                process.terminate()
                process.wait(timeout=5)
                replies.close()

        echo = "import sys; sys.stdout.buffer.write(sys.stdin.buffer.readline()); sys.stdout.buffer.flush()"
        with subprocess.Popen([sys.executable, "-B", "-c", echo], stdin=subprocess.PIPE,
                              stdout=subprocess.PIPE) as process:
            try:
                self.assertEqual(replies.request(process, b"new request\n", timeout=5), b"new request\n")
            finally:
                process.wait(timeout=5)
                replies.close()


if __name__ == "__main__":
    unittest.main()
