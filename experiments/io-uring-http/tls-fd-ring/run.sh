#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
make
../tls-cert.sh
port=${1:-18443}
IFS=, read -r -a cpus <<< "${SERVER_CPUS:-0,2,4,6}"
[[ ${#cpus[@]} == 4 ]] || { echo "Provide four SERVER_CPUS." >&2; exit 1; }
pids=()
cleanup() {
    if ((${#pids[@]})); then
        kill -TERM "${pids[@]}" 2>/dev/null || true
        wait "${pids[@]}" || true
    fi
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
for cpu in "${cpus[@]}"; do
    taskset -c "$cpu" ./bin/server "$port" ../tls-assets/cert.pem ../tls-assets/key.pem &
    pids+=("$!")
done
echo "TLS workers: ${pids[*]}; CPUs: ${cpus[*]}"
status=0
wait -n "${pids[@]}" || status=$?
echo "Worker exited ($status); stopping remaining workers." >&2
exit 1
