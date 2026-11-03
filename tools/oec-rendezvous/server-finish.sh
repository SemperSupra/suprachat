#!/usr/bin/env bash
set -Eeuo pipefail
umask 077
: "${RUNNER_TEMP:?}" "${GITHUB_RUN_ID:?}" "${GITHUB_REPOSITORY:?}" "${GH_TOKEN:?}"
state="$RUNNER_TEMP/oec-server"
mkdir -p "$state/client-result"
for _ in $(seq 1 70);do
 if gh run download "$GITHUB_RUN_ID" --repo "$GITHUB_REPOSITORY" \
  --name "oec-client-verified" --dir "$state/client-result" >/dev/null 2>&1 \
  && test -s "$state/client-result/client-receipt.json";then break;fi
 sleep 2
done
if ! test -s "$state/client-result/client-receipt.json";then echo "RENDEZVOUS_CLIENT_VERIFICATION_MISSING";exit 1;fi
python3 - "$state/client-result/client-receipt.json" <<'PY'
import json,sys
with open(sys.argv[1]) as f:x=json.load(f)
expect=("mcp_initialization","tool_discovery","live_process_nonce_match","challenge_digest_roundtrip","unauthorized_401","origin_403","unknown_tool_refusal")
assert x.get("schema")=="oec-ephemeral-tunnel-receipt/v1"
assert x.get("evidence_class")=="DIRECT_PUBLIC_HTTPS_SYNTHETIC_FIXTURE"
assert x.get("runner_os")=="linux"
assert all(x.get(k)=="PASS" for k in expect)
assert x.get("secrets_or_endpoints_exported") is False
assert x.get("chatgpt_internal_visibility") is False
assert x.get("independent_runner") is True
PY
echo "QUALIFIED_TWO_INDEPENDENT_GHA_RUNNER_JOBS tunnel=cloudflare auth=rsa_oaep_sha256 shared_bearer_public=false"
