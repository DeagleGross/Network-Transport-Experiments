#!/usr/bin/env bash
set -euo pipefail
cd -- "$(dirname -- "${BASH_SOURCE[0]}")"
mkdir -p tls-assets
if [[ -e tls-assets/cert.pem || -e tls-assets/key.pem ]]; then
    [[ -f tls-assets/cert.pem && -f tls-assets/key.pem ]] || { echo "Incomplete certificate pair." >&2; exit 1; }
    exit 0
fi
umask 077
openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes \
    -keyout tls-assets/key.pem -out tls-assets/cert.pem -days 30 \
    -subj "/CN=localhost" -addext "subjectAltName=DNS:localhost,IP:127.0.0.1"
