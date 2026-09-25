#!/usr/bin/env python3
"""Run four isolated server workers and a genuine, rate-controlled wrk2 client."""

import argparse
import datetime
import json
import os
from pathlib import Path
import platform
import re
import resource
import shlex
import signal
import socket
import subprocess
import time

ROOT = Path(__file__).resolve().parent
EXPECTED_BODY = b"Hello world\n"


def cpu_sets():
    cores = {}
    for cpu in sorted(os.sched_getaffinity(0)):
        topology = Path(f"/sys/devices/system/cpu/cpu{cpu}/topology")
        key = (
            (topology / "physical_package_id").read_text().strip(),
            (topology / "core_id").read_text().strip(),
        )
        cores.setdefault(key, []).append(cpu)
    groups = list(cores.values())
    if len(groups) < 8:
        raise RuntimeError("Need eight available physical cores: four server + four client.")
    # Select one logical CPU from each core; never split SMT siblings across roles.
    return [group[0] for group in groups[:4]], [group[0] for group in groups[4:8]], groups


def check_response(port, payload=b"GET / HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"):
    with socket.create_connection(("127.0.0.1", port), timeout=3) as client:
        client.sendall(payload)
        chunks = []
        while chunk := client.recv(4096):
            chunks.append(chunk)
    headers, body = b"".join(chunks).split(b"\r\n\r\n", 1)
    if not headers.startswith(b"HTTP/1.1 200 OK\r\n") or body != EXPECTED_BODY:
        raise RuntimeError(f"Unexpected response: {headers!r} {body!r}")
    if b"Content-Length: 12" not in headers or b"Connection: close" not in headers:
        raise RuntimeError("Incorrect framing or connection-close policy.")


def stop_workers(workers):
    errors = []
    usage_before = resource.getrusage(resource.RUSAGE_CHILDREN)
    for process, _, _ in workers:
        if process.poll() is None:
            process.send_signal(signal.SIGTERM)
    for process, log, path in workers:
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
            errors.append(f"Worker {process.pid} did not shut down.")
        log.close()
        text = path.read_text()
        print(text, end="", flush=True)
        if process.returncode != 0:
            errors.append(f"Worker {process.pid} exited {process.returncode}; see {path}")
    if errors:
        raise RuntimeError("; ".join(errors))
    usage_after = resource.getrusage(resource.RUSAGE_CHILDREN)
    return usage_after.ru_utime + usage_after.ru_stime - usage_before.ru_utime - usage_before.ru_stime


def run_one(mode, rate, trial, port, server_cpus, client_cpus, args, directory):
    label = f"{trial}-{mode}-{rate}"
    workers = []
    try:
        for cpu in server_cpus:
            path = directory / f"{label}-cpu{cpu}.log"
            log = path.open("w")
            command = ["taskset", "-c", str(cpu), str(ROOT / "bin" / mode), str(port)]
            process = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT)
            workers.append((process, log, path))
        deadline = time.monotonic() + 10
        while not all("READY " in path.read_text() for _, _, path in workers):
            if any(p.poll() is not None for p, _, _ in workers) or time.monotonic() > deadline:
                raise RuntimeError("Server startup failed; see worker logs.")
            time.sleep(0.05)
        for process, _, _ in workers:
            print(f"Worker {process.pid}: CPUs {sorted(os.sched_getaffinity(process.pid))}")
        check_response(port)
        # Deliberately not an HTTP parser: any nonempty payload produces the response.
        check_response(port, b"x")

        command = [
            "taskset", "-c", ",".join(map(str, client_cpus)), "stdbuf", "-oL", str(args.wrk),
            "-t4", f"-c{args.connections}", f"-d{args.duration}s",
            f"-R{rate}", "--latency", "--timeout", "2s",
            "-H", "Connection: close", f"http://127.0.0.1:{port}/",
        ]
        print(f"\n=== {label} ===\n$ {shlex.join(command)}", flush=True)
        usage_before = resource.getrusage(resource.RUSAGE_CHILDREN)
        completed = subprocess.run(
            command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            text=True, timeout=args.duration + 45,
        )
        # Preserve the complete histogram on disk, keep console results readable.
        before, separator, _ = completed.stdout.partition("  Detailed Percentile spectrum:")
        print(before, end="", flush=True)
        if separator:
            for line in completed.stdout.splitlines():
                if re.match(r"\s+\d+ requests in|Requests/sec:|Transfer/sec:|.*Socket errors:|.*Non-2xx", line):
                    print(line, flush=True)
        (directory / f"{label}-wrk.txt").write_text(completed.stdout)
        usage_after = resource.getrusage(resource.RUSAGE_CHILDREN)
        if completed.returncode != 0:
            raise RuntimeError(f"wrk2 exited {completed.returncode}")
        match = re.search(r"Requests/sec:\s+([\d.]+)", completed.stdout)
        if not match:
            raise RuntimeError("wrk2 did not report Requests/sec.")
        socket_errors = re.search(
            r"Socket errors: connect (\d+), read (\d+), write (\d+), timeout (\d+)",
            completed.stdout,
        )
        errors = list(map(int, socket_errors.groups())) if socket_errors else [0, 0, 0, 0]
        non_success = re.search(r"Non-2xx or 3xx responses:\s+(\d+)", completed.stdout)
        result = {
            "mode": mode, "trial": trial, "offered_rps": rate,
            "achieved_rps": float(match[1]), "socket_errors": errors,
            "non_success": int(non_success[1]) if non_success else 0,
            "command": command,
            "client_cpu_seconds": usage_after.ru_utime + usage_after.ru_stime - usage_before.ru_utime - usage_before.ru_stime,
        }
    finally:
        server_cpu_seconds = stop_workers(workers)
    result["server_cpu_seconds"] = server_cpu_seconds
    return result


def main():
    def terminate(signum, frame):
        raise KeyboardInterrupt(f"Received signal {signum}; stopping owned processes.")

    signal.signal(signal.SIGTERM, terminate)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--wrk", type=Path, default=Path.home() / "code/wrk2/wrk")
    parser.add_argument("--rates", type=int, nargs="+", default=[30000, 60000, 120000])
    parser.add_argument("--duration", type=int, default=30)
    parser.add_argument("--trials", type=int, default=3)
    parser.add_argument("--connections", type=int, default=1024)
    parser.add_argument("--port", type=int, default=18080)
    args = parser.parse_args()
    args.wrk = args.wrk.expanduser().resolve()
    if min(args.rates + [args.duration, args.trials, args.connections]) <= 0 or args.connections < 4:
        parser.error("Positive rates/duration/trials and at least four connections are required.")
    if args.duration < 20:
        parser.error("Use at least 20 seconds so wrk2 has time to calibrate.")
    help_text = subprocess.run(
        [str(args.wrk), "--help"], capture_output=True, text=True, check=False
    )
    if "--rate" not in help_text.stdout + help_text.stderr:
        parser.error("--wrk must be wrk2 (supporting -R), not ordinary wrk.")
    server_cpus, client_cpus, topology = cpu_sets()
    directory = ROOT / "results" / datetime.datetime.now().strftime("%Y%m%d-%H%M%S")
    directory.mkdir(parents=True)
    metadata = {
        "kernel": platform.release(), "platform": platform.platform(),
        "server_cpus": server_cpus, "client_cpus": client_cpus,
        "available_core_groups": topology, "parameters": vars(args) | {"wrk": str(args.wrk)},
        "compiler": subprocess.check_output(["cc", "--version"], text=True).splitlines()[0],
    }
    print(json.dumps(metadata, indent=2), flush=True)
    results = []
    (directory / "environment.json").write_text(json.dumps(metadata, indent=2))
    for trial in range(1, args.trials + 1):
        modes = ["oneshot", "multishot"] if trial % 2 else ["multishot", "oneshot"]
        for rate in args.rates:
            for mode in modes:
                # Distinct ports reduce cross-trial TIME_WAIT tuple interference.
                port = args.port + len(results)
                if port > 65535:
                    parser.error("Port range exhausted.")
                results.append(run_one(mode, rate, trial, port, server_cpus, client_cpus, args, directory))
                (directory / "summary.json").write_text(json.dumps(results, indent=2))
    print("\ntrial mode       offered_rps achieved_rps socket_errors non_success")
    for item in results:
        print(f"{item['trial']:5} {item['mode']:10} {item['offered_rps']:11} "
              f"{item['achieved_rps']:12.2f} {str(item['socket_errors']):13} {item['non_success']}")
    print(f"\nRaw output and environment: {directory}")
    if any(any(item["socket_errors"]) or item["non_success"] for item in results):
        raise SystemExit("Some load points had errors; do not treat them as sustainable throughput.")


if __name__ == "__main__":
    main()
