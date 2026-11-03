#!/usr/bin/env bash
set -Eeuo pipefail
cd "$(dirname "$0")/../.."
umask 077
tmp="$(mktemp -d)"
server_pid=""
cleanup(){
  set +e
  [[ -z "$server_pid" ]] || { kill "$server_pid" 2>/dev/null; wait "$server_pid" 2>/dev/null; }
  rm -rf "$tmp"
}
trap cleanup EXIT INT TERM
export OEC_OBSERVER_TOKEN
OEC_OBSERVER_TOKEN="$(python3 -c 'import secrets;print(secrets.token_hex(32))')"
( cd tools/oec-observer-v02 && go build -trimpath -o "$tmp/observer" ./cmd/observer )
"$tmp/observer" > "$tmp/observer.log" 2>&1 &
server_pid=$!
ready=false
for _ in $(seq 1 35);do
  if curl --noproxy '*' -fsS --max-time 2     -H "Authorization: Bearer $OEC_OBSERVER_TOKEN"     -H "Content-Type: application/json"     -d '{"jsonrpc":"2.0","id":1,"method":"ping"}'     "http://127.0.0.1:18763/mcp" > /dev/null;then ready=true;break;fi
  sleep 1
done
[[ "$ready" == "true" ]] || { echo "INSPECTOR_FIXTURE_START_FAIL";exit 1; }
inspector="./tools/oec-inspector/node_modules/.bin/mcp-inspector"
url="http://127.0.0.1:18763/mcp"
# Inspector is the actual CLI issuing MCP initialize/list/call over Streamable HTTP.
"$inspector" --cli --server-url "$url" --transport http --protocol-era legacy   --header "Authorization: Bearer $OEC_OBSERVER_TOKEN" --method initialize --format json > "$tmp/initialize.json"
"$inspector" --cli --server-url "$url" --transport http --protocol-era legacy   --header "Authorization: Bearer $OEC_OBSERVER_TOKEN" --method tools/list --format json > "$tmp/tools.json"
"$inspector" --cli --server-url "$url" --transport http --protocol-era legacy   --header "Authorization: Bearer $OEC_OBSERVER_TOKEN" --method tools/call   --tool-name mirror.capabilities --tool-args-json '{}' --format json > "$tmp/capabilities.json"
"$inspector" --cli --server-url "$url" --transport http --protocol-era legacy   --header "Authorization: Bearer $OEC_OBSERVER_TOKEN" --method tools/call   --tool-name mirror.ui_diff --tool-args-json '{"before":20,"after":21}' --format json > "$tmp/invalid.json" 2>&1 || true
python3 - "$tmp" <<'PY'
import json,pathlib,sys
p=pathlib.Path(sys.argv[1])
def safe_text(n):
  return (p/n).read_text()[:20000]
init=safe_text('initialize.json')
catalog=safe_text('tools.json')
caps=safe_text('capabilities.json')
neg=safe_text('invalid.json')
assert 'oec-mirror-observer-v02' in init, 'INSPECTOR_INITIALIZE_CONTRACT_FAIL'
names=['mirror.capabilities','mirror.environment','mirror.ui_snapshot','mirror.ui_diff','mirror.experiment','mirror.verify','mirror.evidence']
assert all(name in catalog for name in names),'INSPECTOR_TOOL_DISCOVERY_FAIL'
assert 'SYNTHETIC_FIXTURE_ONLY' in caps,'INSPECTOR_TOOL_CALL_FAIL'
assert 'unknown or reversed version' in neg or '-32602' in neg,'INSPECTOR_NEGATIVE_TEST_FAIL'
receipt={'schema':'oec-inspector-public-receipt/v1','source':'@modelcontextprotocol/inspector@2.13.0',
         'target':'oec-observer-v02','transport':'loopback_http','methods_qualified':['initialize','tools/list','tools/call'],
         'catalog_count_expected':len(names),'negative_invalid_bounds':'PASS',
         'contains_credentials':False,'exposes_chatgpt_internals':False,
         'evidence_class':'DIRECT_INSPECTOR_SYNTHETIC_MCP'}
dst=pathlib.Path('tools/oec-inspector/evidence');dst.mkdir(exist_ok=True)
(dst/'receipt.json').write_text(json.dumps(receipt,sort_keys=True,indent=2)+'\n')
PY
echo "QUALIFIED_REAL_MCP_INSPECTOR_CLI schema_discovery=true bad_bounds_rejected=true secrets=false"
