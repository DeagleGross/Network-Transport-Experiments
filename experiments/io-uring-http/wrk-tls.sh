#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
mode=${1:-keepalive}
rate=${2:-30000}
duration=${3:-30s}
connections=${4:-1024}
port=${5:-18443}
wrk=${WRK2:-"$HOME/code/wrk2/wrk"}
client_cpus=${CLIENT_CPUS:-8,10,12,14}
IFS=, read -r -a server <<< "${SERVER_CPUS:-0,2,4,6}"
IFS=, read -r -a client <<< "$client_cpus"
case "$mode" in
    close) headers=(-H "Connection: close");;
    keepalive) headers=();;
    *) echo "Mode must be close or keepalive." >&2; exit 1;;
esac
[[ -x "$wrk" ]] || { echo "wrk2 missing: $wrk" >&2; exit 1; }
help=$("$wrk" --help 2>&1 || true)
[[ $help == *--rate* ]] || { echo "Use wrk2 with -R support." >&2; exit 1; }
core_key() {
    cat "/sys/devices/system/cpu/cpu$1/topology/physical_package_id" "/sys/devices/system/cpu/cpu$1/topology/core_id"
}
for c in "${client[@]}"; do
    ck=$(core_key "$c")
    for s in "${server[@]}"; do
        sk=$(core_key "$s")
        [[ $ck != "$sk" ]] || { echo "Client/server share physical core." >&2; exit 1; }
    done
done
url="https://127.0.0.1:$port/"
body=$(curl --cacert tls-assets/cert.pem --tlsv1.3 --tls-max 1.3 --fail --silent --show-error \
    --max-time 3 --retry 20 --retry-connrefused --retry-delay 1 "$url")
[[ $body == "Hello world" ]] || { echo "Unexpected response: $body" >&2; exit 1; }
echo "wrk2 TLS mode=$mode CPUs=$client_cpus threads=${#client[@]} connections=$connections offered_rps=$rate duration=$duration"
exec taskset -c "$client_cpus" "$wrk" "-t${#client[@]}" "-c$connections" "-d$duration" "-R$rate" \
    --latency --timeout 2s "${headers[@]}" "$url"
