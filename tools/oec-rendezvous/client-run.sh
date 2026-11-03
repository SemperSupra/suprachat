#!/usr/bin/env bash
set -Eeuo pipefail
cd "$(dirname "$0")/../.."
umask 077
: "${RUNNER_TEMP:?}" "${GITHUB_RUN_ID:?}" "${GITHUB_REPOSITORY:?}" "${GH_TOKEN:?}"
state="$RUNNER_TEMP/oec-client"
mkdir -p "$state/peer"
for _ in $(seq 1 75);do
 if gh run download "$GITHUB_RUN_ID" --repo "$GITHUB_REPOSITORY" \
   --name "oec-server-rendezvous" --dir "$state/peer" >/dev/null 2>&1 \
   && test -s "$state/peer/endpoint.json" \
   && test -s "$state/peer/encrypted-token.bin";then break;fi
 sleep 2
done
if ! test -s "$state/peer/endpoint.json";then echo "RENDEZVOUS_SERVER_HANDOFF_MISSING";exit 1;fi
# Reject malformed URL and unexpected cipher filename without printing either.
remote_url="$(python3 - "$state/peer/endpoint.json" <<'PY'
import json,sys,re
with open(sys.argv[1]) as f:x=json.load(f)
url=x.get("endpoint","")
assert x.get("schema")=="oec-rendezvous-public/v1"
assert x.get("cipher")=="encrypted-token.bin"
assert re.fullmatch(r"https://[a-zA-Z0-9-]+[.]trycloudflare[.]com",url)
print(url)
PY
)"
openssl pkeyutl -decrypt -inkey "$state/private/client-key.pem" \
  -pkeyopt rsa_padding_mode:oaep -pkeyopt rsa_oaep_md:sha256 \
  -in "$state/peer/encrypted-token.bin" -out "$state/private/remote-token"
export OEC_LAB_TOKEN
OEC_LAB_TOKEN="$(cat "$state/private/remote-token")"
mkdir -p "$state/receipt"
python3 tools/ephemeral-tunnel-lab/verify_remote.py --url "$remote_url" \
 --receipt "$state/receipt/client-receipt.json"
python3 - "$state/receipt/client-receipt.json" <<'PY'
import json,sys
with open(sys.argv[1]) as f:x=json.load(f)
assert x["evidence_class"]=="DIRECT_PUBLIC_HTTPS_SYNTHETIC_FIXTURE"
assert x["live_process_nonce_match"]=="PASS"
assert x["challenge_digest_roundtrip"]=="PASS"
assert x["unauthorized_401"]=="PASS"
PY
echo "QUALIFIED_CROSS_RUNNER_CLIENT_REMOTE_MCP_ONLY rsa_oaep=true credentials_exported=false"
