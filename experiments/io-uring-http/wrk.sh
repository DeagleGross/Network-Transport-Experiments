#!/usr/bin/env bash
set -euo pipefail

wrk=${WRK2:-"$HOME/code/wrk2/wrk"}
rate=${1:-30000}
duration=${2:-30s}
connections=${3:-1024}
port=${4:-18080}
client_cpus=${CLIENT_CPUS:-8,10,12,14}
IFS=, read -r -a server <<< "${SERVER_CPUS:-0,2,4,6}"
IFS=, read -r -a client <<< "$client_cpus"
if [[ ! -x "$wrk" ]]; then
    echo "wrk2 not found: $wrk. Build it with setup.sh or set WRK2." >&2
    exit 1
fi
help=$("$wrk" --help 2>&1 || true)
if [[ $help != *--rate* ]]; then
    echo "This must be wrk2 (with -R support), not ordinary wrk." >&2
    exit 1
fi
core_key() {
    local base="/sys/devices/system/cpu/cpu$1/topology"
    cat "$base/physical_package_id" "$base/core_id"
}
for c in "${client[@]}"; do
    client_key=$(core_key "$c")
    for s in "${server[@]}"; do
        server_key=$(core_key "$s")
        if [[ $client_key == "$server_key" ]]; then
            echo "Client CPU $c shares a physical core with server CPU $s." >&2
            exit 1
        fi
    done
done

url="http://127.0.0.1:$port/"
body=$(curl --fail --silent --show-error --max-time 2 --retry 20 --retry-connrefused --retry-delay 1 "$url")
if [[ $body != "Hello world" ]]; then
    echo "Unexpected readiness response: $body" >&2
    exit 1
fi
echo "wrk2: CPUs=$client_cpus threads=${#client[@]} connections=$connections offered_rps=$rate duration=$duration"
exec taskset -c "$client_cpus" "$wrk" "-t${#client[@]}" "-c$connections" "-d$duration" "-R$rate" \
    --latency --timeout 2s -H "Connection: close" "$url"
