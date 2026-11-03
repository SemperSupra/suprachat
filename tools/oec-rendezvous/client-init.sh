#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
: "${RUNNER_TEMP:?}"
state="$RUNNER_TEMP/oec-client"
mkdir -p "$state/private" "$state/public"
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 \
 -out "$state/private/client-key.pem" >/dev/null 2>&1
openssl pkey -in "$state/private/client-key.pem" -pubout \
 -out "$state/public/client.pem" >/dev/null 2>&1
chmod 600 "$state/private/client-key.pem"
echo "RENDEZVOUS_CLIENT_EPHEMERAL_RSA_PUBLIC_ONLY"
