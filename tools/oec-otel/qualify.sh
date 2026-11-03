#!/usr/bin/env bash
set -Eeuo pipefail
cd "$(dirname "$0")"
umask 077
container="oec-otel-$GITHUB_RUN_ID"
logs="$(mktemp)"
cleanup(){
 set +e
 docker rm -f "$container" >/dev/null 2>&1
 rm -f "$logs"
}
trap cleanup EXIT INT TERM
docker run --rm -d --name "$container"   -p 127.0.0.1:4318:4318   -v "$PWD/collector.yaml:/etc/otelcol/config.yaml:ro"   otel/opentelemetry-collector:0.143.0 --config=/etc/otelcol/config.yaml >/dev/null
python3 send_synthetic.py
ok=false
for _ in $(seq 1 30);do
 docker logs "$container" > "$logs" 2>&1 || true
 if grep -q "oec.fixture.observe" "$logs" && grep -q "oec.fixture.negative" "$logs" &&    grep -q "abababababababababababababababab" "$logs";then
   ok=true;break
 fi
 sleep 1
done
if [[ "$ok" != true ]];then
  echo "OTEL_COLLECTOR_TRACE_CORRELATION_UNVERIFIED"
  exit 1
fi
if grep -Ei 'DO_NOT_EXPORT|gen_ai.input.messages|http.request.header.authorization' "$logs" >/dev/null;then
 echo "OTEL_COLLECTOR_PRIVACY_REJECT_FAIL";exit 1
fi
python3 - <<'PY'
import json,pathlib
p=pathlib.Path("evidence/ingress.json")
x=json.loads(p.read_text())
assert x["trace_id"]=="ab"*16
assert x["sensitive_attribute_negatives"]==5
assert x["parent_child_span_count"]==2
out={k:x[k] for k in ("schema","collector_submission","trace_id","parent_child_span_count","expected_negative_span","sensitive_attribute_negatives","no_prompts_or_credentials")}
out["collector_debug_readback"]="PASS"
out["evidence_class"]="DIRECT_COLLECTOR_SYNTHETIC_TRACES"
pathlib.Path("evidence/receipt.json").write_text(json.dumps(out,indent=2,sort_keys=True)+"\n")
PY
rm -f evidence/ingress.json
echo "QUALIFIED_OTEL_COLLECTOR_PIPELINE_TRUE receiver=otlp_http exporter=debug spans=2 traces=1 privacy=true"
