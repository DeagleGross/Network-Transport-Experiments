#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
make bin/oneshot

port=${1:-18080}
IFS=, read -r -a cpus <<< "${SERVER_CPUS:-0,2,4,6}"
if [[ ${#cpus[@]} != 4 ]]; then
    echo "SERVER_CPUS must specify four CPUs, for example 0,2,4,6." >&2
    exit 1
fi
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
    taskset -c "$cpu" ./bin/oneshot "$port" &
    pids+=("$!")
done
echo "One-shot workers: ${pids[*]}; CPUs: ${cpus[*]}. Ctrl+C stops them."
status=0
wait -n "${pids[@]}" || status=$?
echo "A server worker exited unexpectedly (status $status); stopping the others." >&2
exit 1
