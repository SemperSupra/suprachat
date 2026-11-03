#!/usr/bin/env bash
set -Eeuo pipefail
cd "$(dirname "$0")"
umask 077
workspace="$(mktemp -d "${RUNNER_TEMP:-/tmp}/oec-quick-tunnel.XXXXXXXX")"
server_pid=""
tunnel_pid=""
cleanup() {
  set +e
  if [[ -n "$tunnel_pid" ]]; then kill "$tunnel_pid" 2>/dev/null; wait "$tunnel_pid" 2>/dev/null; fi
  if [[ -n "$server_pid" ]]; then kill "$server_pid" 2>/dev/null; wait "$server_pid" 2>/dev/null; fi
  rm -rf "$workspace"
}
trap cleanup EXIT INT TERM
command -v python3 >/dev/null
command -v curl >/dev/null
command -v go >/dev/null
command -v sha256sum >/dev/null

# Fixed upstream artifact, separately verified against published release checksum.
cloudflared_sha="3bfc539f45d2620c71debb7a86be0efa737ed5ccbad9eb85c787116900e5c586"
cloudflared_url="https://github.com/cloudflare/cloudflared/releases/download/2026.10.1/cloudflared-linux-amd64"
curl -fsSL --retry 3 --connect-timeout 10 --max-time 150 "$cloudflared_url" -o "$workspace/cloudflared"
echo "$cloudflared_sha  $workspace/cloudflared" | sha256sum -c - >/dev/null
chmod 700 "$workspace/cloudflared"
go build -trimpath -o "$workspace/lab-mcp" ./cmd/lab-mcp

# No secrets, raw payloads, or temporary URL in public logs or public artifacts.
export OEC_LAB_TOKEN
OEC_LAB_TOKEN="$(python3 -c 'import secrets;print(secrets.token_hex(32))')"
"$workspace/lab-mcp" > "$workspace/server.log" 2>&1 &
server_pid=$!
ready=0
for _ in $(seq 1 30); do
  if curl --noproxy '*' -fsS --max-time 2 \
    -H "Authorization: Bearer $OEC_LAB_TOKEN" \
    "http://127.0.0.1:18761/healthz" >/dev/null 2>&1; then ready=1;break; fi
  sleep 1
done
if [[ "$ready" != 1 ]]; then echo "LOCAL_FIXTURE_START_FAILED" >&2;exit 1;fi

"$workspace/cloudflared" tunnel --url "http://127.0.0.1:18761" --no-autoupdate \
  --loglevel info > "$workspace/tunnel.log" 2>&1 &
tunnel_pid=$!
quick_url=""
for _ in $(seq 1 70); do
  if ! kill -0 "$tunnel_pid" 2>/dev/null; then
    echo "QUICK_TUNNEL_START_FAILED (private log withheld)" >&2;exit 1
  fi
  quick_url="$(grep -Eo 'https://[a-zA-Z0-9-]+[.]trycloudflare[.]com' "$workspace/tunnel.log" | head -n 1 || true)"
  if [[ -n "$quick_url" ]]; then break; fi
  sleep 1
done
if [[ -z "$quick_url" ]];then echo "QUICK_TUNNEL_URL_DISCOVERY_FAILED" >&2;exit 1;fi
mkdir -p evidence
python3 ./verify_remote.py --url "$quick_url" --receipt evidence/quick-tunnel-receipt.json
echo "EPHEMERAL_TUNNEL_TEARDOWN_SCHEDULED"
