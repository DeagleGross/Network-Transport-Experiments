#!/usr/bin/env python3
"""Small socket-level lifetime checks; also usable with sanitizer-built binaries."""

from concurrent.futures import ThreadPoolExecutor
import os
from pathlib import Path
import signal
import socket
import subprocess
import tempfile
import time

from run import check_response

ROOT = Path(__file__).resolve().parent
BIN = Path(os.environ.get("SERVER_BIN", str(ROOT / "bin")))


def check(mode, port):
    with tempfile.TemporaryFile(mode="w+") as log:
        process = subprocess.Popen([str(BIN / mode), str(port)], stdout=log, stderr=log)
        idle = []
        try:
            deadline = time.monotonic() + 10
            while True:
                log.seek(0)
                if "READY " in log.read():
                    break
                if process.poll() is not None or time.monotonic() > deadline:
                    raise RuntimeError("Server failed to start.")
                time.sleep(0.02)
            check_response(port)
            check_response(port, b"x")
            # Ensure all pool IDs are recycled across more than one pool's worth of requests.
            with ThreadPoolExecutor(max_workers=16) as executor:
                list(executor.map(lambda _: check_response(port), range(4200)))
            # EOF before data must free its connection without issuing a response.
            with socket.create_connection(("127.0.0.1", port), timeout=3) as client:
                client.shutdown(socket.SHUT_WR)
                assert client.recv(1) == b""
            # Termination must cancel outstanding receives, not free their state early.
            idle = [socket.create_connection(("127.0.0.1", port), timeout=3) for _ in range(32)]
            time.sleep(0.1)
            process.send_signal(signal.SIGTERM)
            process.wait(timeout=10)
            for client in idle:
                assert client.recv(1) == b""
            if process.returncode != 0:
                raise RuntimeError(f"{mode} exited {process.returncode}")
        finally:
            for client in idle:
                client.close()
            if process.poll() is None:
                process.kill()
                process.wait()
            log.seek(0)
            output = log.read()
            print(output, end="")
        if "errors=0" not in output or "ERROR: AddressSanitizer" in output or "runtime error:" in output:
            raise RuntimeError("Server errors or sanitizer diagnostics.")
        print(f"PASS {mode}: response, arbitrary data, close, pool reuse, EOF, idle shutdown")


if __name__ == "__main__":
    check("oneshot", 18980)
    check("multishot", 18981)
