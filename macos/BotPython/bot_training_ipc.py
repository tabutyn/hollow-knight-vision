"""Private worker process I/O shared by the desktop trainers."""
from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
import errno
import os
import time


@contextmanager
def exclusive_file_lock(path, *, wait=True, deadline=None):
    """An OS-owned lock that expires with the owning process."""
    with path.open("a+b") as stream:
        if os.name == "nt":
            import msvcrt

            def try_lock():
                stream.seek(0)
                try:
                    msvcrt.locking(stream.fileno(), msvcrt.LK_NBLCK, 1)
                except OSError as error:
                    if error.errno not in (errno.EACCES, errno.EAGAIN, errno.EDEADLK):
                        raise
                    raise BlockingIOError(errno.EAGAIN, "Training lock is busy", path) from error
        else:
            import fcntl

            def try_lock():
                fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)

        if os.name != "nt" and wait and deadline is None:
            fcntl.flock(stream, fcntl.LOCK_EX)
        else:
            while True:
                if deadline is not None and time.monotonic() >= deadline:
                    raise TimeoutError(f"Timed out waiting for {path.name}")
                try:
                    try_lock()
                    break
                except BlockingIOError:
                    if not wait:
                        raise
                    remaining = .05 if deadline is None else min(.05, deadline - time.monotonic())
                    if remaining > 0:
                        time.sleep(remaining)
        yield


class EncoderReplies:
    """Read one complete reply per request on native subprocess pipes."""

    def __init__(self):
        self.executor = None
        self.pending = None

    def request(self, process, message, timeout):
        if self.pending is not None:
            raise RuntimeError("The timed-out encoder must be restarted before sending another frame")
        process.stdin.write(message)
        process.stdin.flush()
        if self.executor is None:
            self.executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix="hkv-encoder-reply")
        self.pending = self.executor.submit(process.stdout.readline)
        line = self.pending.result(timeout=timeout)
        self.pending = None
        if line and not line.endswith(b"\n"):
            raise EOFError("The encoder closed its reply pipe before completing the line")
        return line

    def close(self):
        # The caller shuts down the subprocess before closing its pipe reader.
        if self.executor is not None:
            self.executor.shutdown(wait=True, cancel_futures=True)
        self.executor = self.pending = None
