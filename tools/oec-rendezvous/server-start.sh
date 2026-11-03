#!/usr/bin/env bash
set -Eeuo pipefail
cd "$(dirname "$0")/../.."
umask 077
: "${RUNNER_TEMP:?}" "${GITHUB_RUN_ID:?}" "${GITHUB_REPOSITORY:?}" "${GH_TOKEN:?}"
state="$RUNNER_TEMP/oec-server"
mkdir -p "$state/private" "$state/public"
for _ in $(seq 1 75);do
 if gh run download "$GITHUB_RUN_ID" --repo "$GITHUB_REPOSITORY" --name "oec-client-public" --dir "$state/peer" >/dev/null 2>&1 && test -s "$state/peer/client.pem";then break;fi
 sleep 2
done
if ! test -s "$state/peer/client.pem";then echo "RENDEZVOUS_CLIENT_PUBLIC_KEY_MISSING";exit 1;fi
openssl pkey -pubin -in "$state/peer/client.pem" -noout >/dev/null 2>&1
curl -fsSL --retry 3 --connect-timeout 10 --max-time 150 \
 "https://github.com/cloudflare/cloudflared/releases/download/2026.10.1/cloudflared-linux-amd64" -o "$state/private/cloudflared"
echo "3bfc539f45d2620c71debb7a86be0efa737ed5ccbad9eb85c787116900e5c586  $state/private/cloudflared" | sha256sum -c - >/dev/null
chmod 700 "$state/private/cloudflared"
( cd tools/ephemeral-tunnel-lab && go build -trimpath -o "$state/private/lab-mcp" ./cmd/lab-mcp )
export OEC_LAB_TOKEN
OEC_LAB_TOKEN="$(python3 -c 'import secrets;print(secrets.token_hex(32))')"
printf %s "$OEC_LAB_TOKEN" > "$state/private/token"
nohup "$state/private/lab-mcp" > "$state/private/server.log" 2>&1 < /dev/null &
echo "$!" > "$state/private/server.pid"
ready=0
for _ in $(seq 1 25); do
 if curl --noproxy '*' -fsS --max-time 2 -H "Authorization: Bearer $OEC_LAB_TOKEN" \
 "http://127.0.0.1:18761/healthz" >/dev/null 2>&1;then ready=1;break;fi
 sleep 1
done
if [[ "$ready" != 1 ]];then echo "RENDEZVOUS_LOCAL_SERVER_NOT_READY";exit 1;fi
nohup "$state/private/cloudflared" tunnel --url http://127.0.0.1:18761 --no-autoupdate \
 --loglevel info > "$state/private/tunnel.log" 2>&1 < /dev/null &
echo "$!" > "$state/private/tunnel.pid"
host=""
for _ in $(seq 1 60);do
 host="$(grep -Eo 'https://[a-zA-Z0-9-]+[.]trycloudflare[.]com' "$state/private/tunnel.log" | head -n 1 || true)"
 if [[ -n "$host" ]];then break;fi
 sleep 1
done
if [[ -z "$host" ]];then echo "RENDEZVOUS_TUNNEL_NOT_READY";exit 1;fi
openssl pkeyutl -encrypt -pubin -inkey "$state/peer/client.pem" -in "$state/private/token" \
 -pkeyopt rsa_padding_mode:oaep -pkeyopt rsa_oaep_md:sha256 \
 -out "$state/public/encrypted-token.bin"
server_nonce="$(python3 - "$state/private/token" <<'PY'
import json,sys,urllib.request
token=open(sys.argv[1]).read()
q=json.dumps({"jsonrpc":"2.0","id":12,"method":"tools/call",
 "params":{"name":"lab.observe","arguments":{}}}).encode()
request=urllib.request.Request(
 "http://127.0.0.1:18761/mcp",data=q,
 headers={"Authorization":"Bearer "+token,"Content-Type":"application/json"},
 method="POST")
with urllib.request.urlopen(request,timeout=4) as res:
 observer=json.load(res)["result"]["structuredContent"]
assert observer["evidence_class"]=="DIRECT_RUNNER"
assert len(observer["nonce"])==24
print(observer["nonce"])
PY
)"
python3 tools/oec-rendezvous/handoff.py create \
 --manifest "$state/public/endpoint.json" \
 --endpoint "$host" --server-nonce "$server_nonce" \
 --client-pem "$state/peer/client.pem" \
 --encrypted-token "$state/public/encrypted-token.bin"
echo "RENDEZVOUS_SERVER_PUBLISHED_ENCRYPTED_HANDOFF_ONLY"
