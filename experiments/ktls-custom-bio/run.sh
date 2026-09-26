#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
make
binary=${DEMO_BINARY:-./bin/demo}
port=${PORT:-18970}
modes=(userspace ktls)
if [[ ${1:-} == --userspace-only ]]; then
    modes=(userspace)
elif [[ $# != 0 ]]; then
    echo "Usage: ./run.sh [--userspace-only]" >&2
    exit 1
fi
mkdir -p certs results
if [[ ! -e certs/cert.pem && ! -e certs/key.pem ]]; then
    (umask 077
     openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes \
         -keyout certs/key.pem -out certs/cert.pem -days 30 \
         -subj /CN=localhost -addext 'subjectAltName=DNS:localhost,IP:127.0.0.1')
fi
[[ -f certs/cert.pem && -f certs/key.pem ]] || { echo "Incomplete certificate pair." >&2; exit 1; }
openssl x509 -in certs/cert.pem -checkend 60 -noout
output="results/$(date +%Y%m%d-%H%M%S)"
mkdir -p "$output"
pid=''
cleanup() {
    if [[ -n $pid ]]; then
        if kill -0 "$pid" 2>/dev/null; then kill -TERM "$pid"; fi
        wait "$pid" || true
    fi
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
printf 'hello through the custom BIO\n' > "$output/input.txt"
for mode in "${modes[@]}"; do
    args=()
    if [[ $mode == userspace ]]; then args=(--userspace); fi
    if [[ -f /proc/net/tls_stat ]]; then cat /proc/net/tls_stat > "$output/$mode-kernel-before.txt"; fi
    "$binary" "$port" certs/cert.pem certs/key.pem "${args[@]}" > "$output/$mode-server.log" 2>&1 &
    pid=$!
    for attempt in $(seq 1 100); do
        if grep -q '^READY ' "$output/$mode-server.log"; then break; fi
        if ! kill -0 "$pid" 2>/dev/null; then cat "$output/$mode-server.log"; exit 1; fi
        sleep .02
    done
    grep -q '^READY ' "$output/$mode-server.log"
    # No readiness connection: the program accepts exactly once.
    # Delay input so the handshake completes before the application record.
    client_status=0
    { sleep .3; cat "$output/input.txt"; } | timeout --signal=TERM --kill-after=2s 15s \
        openssl s_client -connect "127.0.0.1:$port" -servername localhost \
        -tls1_2 -cipher ECDHE-ECDSA-AES128-GCM-SHA256 -CAfile certs/cert.pem \
        -verify_hostname localhost -verify_return_error -quiet -ign_eof \
        > "$output/$mode-echo.txt" 2> "$output/$mode-client.log" || client_status=$?
    server_status=0
    wait "$pid" || server_status=$?
    pid=''
    cat "$output/$mode-server.log"
    if [[ -f /proc/net/tls_stat ]]; then cat /proc/net/tls_stat > "$output/$mode-kernel-after.txt"; fi
    if [[ $client_status != 0 || $server_status != 0 ]]; then
        cat "$output/$mode-client.log" >&2
        echo "Failed: client=$client_status server=$server_status. Evidence: $output" >&2
        exit 1
    fi
    cmp "$output/input.txt" "$output/$mode-echo.txt"
    grep -q "^PASS mode=$mode" "$output/$mode-server.log"
    ((port+=1))
done
if [[ ${#modes[@]} == 2 ]]; then
    echo "Both echoes match; userspace control and required kTLS RX gates passed. Logs: $output"
else
    echo "Userspace control passed; kTLS was not tested. Logs: $output"
fi
