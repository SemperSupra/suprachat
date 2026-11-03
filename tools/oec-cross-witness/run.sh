#!/usr/bin/env bash
set -Eeuo pipefail
cd "$(dirname "$0")"
umask 077
tmp="$(mktemp -d "${RUNNER_TEMP:-/tmp}/oec-cross-witness.XXXXXXXX")"
server_pid=""
container="oec-synthetic-$GITHUB_RUN_ID"
cleanup(){
  set +e
  [[ -z "$server_pid" ]] || { kill "$server_pid" 2>/dev/null;wait "$server_pid" 2>/dev/null; }
  docker rm -f "$container" >/dev/null 2>&1
  rm -rf "$tmp"
}
trap cleanup EXIT INT TERM
export OEC_EXPERIMENT_ID OEC_OBSERVER_TOKEN
OEC_EXPERIMENT_ID="$(python3 -c 'import secrets;print("e"+secrets.token_hex(12))')"
OEC_OBSERVER_TOKEN="$(python3 -c 'import secrets;print(secrets.token_hex(32))')"
go -C ../oec-observer-v02 build -trimpath -o "$tmp/oec-observer" ./cmd/observer
"$tmp/oec-observer" > "$tmp/observer.log" 2>&1 &
server_pid=$!
ready=0
for _ in $(seq 1 25); do
 if curl --noproxy '*' -fsS --max-time 2 -H "Authorization: Bearer $OEC_OBSERVER_TOKEN" \
   -H 'Content-Type: application/json' \
   -d '{"jsonrpc":"2.0","id":1,"method":"ping"}' \
   "http://127.0.0.1:18763/mcp" >/dev/null 2>&1;then ready=1;break;fi
 sleep 1
done
if [[ "$ready" != 1 ]];then echo "SYNTHETIC_OBSERVER_NOT_READY" >&2;exit 1;fi
./node_modules/.bin/mcp-inspector --cli \
  --server-url http://127.0.0.1:18763/mcp \
  --transport http --protocol-era legacy \
  --header "Authorization: Bearer $OEC_OBSERVER_TOKEN" \
  --method tools/list --format json > "$tmp/inspector-tool-list.json"
if ! grep -q 'mirror.experiment' "$tmp/inspector-tool-list.json";then
 echo "MCP_INSPECTOR_TOOL_DISCOVERY_FAILED" >&2;exit 1
fi
docker run --rm -d --name "$container" -p 127.0.0.1:4318:4318 \
  -v "$PWD/collector.yaml:/etc/otelcol/config.yaml:ro" \
  otel/opentelemetry-collector:0.143.0 --config=/etc/otelcol/config.yaml >/dev/null
node ./probe.mjs
ok=false
for _ in $(seq 1 30);do
 docker logs "$container" > "$tmp/otel.log" 2>&1 || true
 if grep -q "$OEC_EXPERIMENT_ID" "$tmp/otel.log" \
   && grep -q 'oec.synthetic.mcp_action' "$tmp/otel.log" \
   && grep -q 'oec.synthetic.independent_browser_witness' "$tmp/otel.log";then
    ok=true;break
 fi
 sleep 1
done
[[ "$ok" == true ]] || { echo "OTEL_CORRELATED_SPAN_READBACK_FAILED" >&2;exit 1; }
python3 - <<'PY'
import hashlib,json,os,pathlib
x=json.loads(pathlib.Path('evidence/receipt.json').read_text())
assert x['schema']=='oec-cross-witness-cycle/v1'
assert x['experiment_id']==os.environ['OEC_EXPERIMENT_ID']
assert x['trace_id']==hashlib.sha256(
  ('oec-synthetic-trace:'+x['experiment_id']).encode()).hexdigest()[:32]
for key in ('observer_tool_effect','playwright_mcp_control',
 'chrome_devtools_independent_browser_state','chrome_devtools_network',
 'chrome_devtools_console','idempotent_replay','wrong_bearer_refusal'):
 assert x[key]=='PASS',key
assert x['observer_before']==0 and x['observer_after']==1
assert x['otlp_collector_ingest']=='HTTP_200'
assert not x['credentials_exported'] and not x['authenticated_chatgpt_ui']
assert not x['hidden_chatgpt_model_access']
assert not x['first_party_claim_is_independent_proof']
assert len(x['unknowns'])==4
PY
echo "QUALIFIED_CORRELATED_SYNTHETIC_CYCLE inspector=true playwright=true chrome=true otel=true secrets=false"
