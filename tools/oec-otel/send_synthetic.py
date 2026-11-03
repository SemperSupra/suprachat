#!/usr/bin/env python3
"""No credentials, source texts, chat content or identifiable user data. stdlib only."""
import hashlib
import json
import pathlib
import sys
import time
import urllib.error
import urllib.request

ALLOW = {"oec.fixture", "oec.evidence.class", "oec.outcome", "oec.operation.class"}
FORBIDDEN = {"gen_ai.input.messages","gen_ai.output.messages","http.request.header.authorization","prompt","credential"}
TRACE = "ab" * 16
PARENT = "cd" * 8
CHILD = "ef" * 8

def sanitize(attributes):
    if not isinstance(attributes, dict) or len(attributes) > 8:
        raise ValueError("bad attribute map")
    if set(attributes) - ALLOW:
        raise ValueError("attribute outside metadata-only allowlist")
    out = []
    for k, v in sorted(attributes.items()):
        if not isinstance(v, str) or len(v) > 40 or not v.isascii():
            raise ValueError("non-metadata attribute")
        out.append({"key": k, "value": {"stringValue": v}})
    return out

def span(name, sid, parent, start, end, attributes, error=False):
    if not name.startswith("oec.fixture."):
        raise ValueError("unapproved span")
    record = {
        "traceId": TRACE,
        "spanId": sid,
        "name": name,
        "kind": 1,
        "startTimeUnixNano": str(start),
        "endTimeUnixNano": str(end),
        "attributes": sanitize(attributes),
        "status": {"code": 2 if error else 1},
    }
    if parent:
        record["parentSpanId"] = parent
    return record

def main():
    for forbidden in FORBIDDEN:
        try:
            sanitize({forbidden: "DO_NOT_EXPORT"})
        except ValueError:
            pass
        else:
            raise RuntimeError("privacy negative failed " + forbidden)
    now = 1793700000000000000
    shared = {"oec.fixture": "synthetic-agent","oec.evidence.class": "SYNTHETIC_TEST"}
    payload = {"resourceSpans": [{
        "resource": {"attributes": [{"key": "service.name", "value": {"stringValue": "oec-ephemeral-trace"}}]},
        "scopeSpans": [{"scope": {"name": "oec-synthetic-observer"},
          "spans": [
            span("oec.fixture.observe", PARENT, None, now, now + 30000000,
                 dict(shared, **{"oec.outcome": "PASS","oec.operation.class":"READ"})),
            span("oec.fixture.negative", CHILD, PARENT, now + 2000000, now + 7000000,
                 dict(shared, **{"oec.outcome": "FAIL_EXPECTED","oec.operation.class":"SYNTHETIC"}),True),
          ]}]
    }]}
    body = json.dumps(payload, separators=(",", ":")).encode("utf8")
    url = "http://127.0.0.1:4318/v1/traces"
    for attempt in range(35):
        try:
            req = urllib.request.Request(url, body, {"Content-Type": "application/json"}, method="POST")
            with urllib.request.urlopen(req, timeout=4) as resp:
                if resp.status != 200:
                    raise RuntimeError("OTLP HTTP not accepted")
                break
        except (urllib.error.URLError, TimeoutError):
            if attempt == 34:
                raise RuntimeError("collector never accepted synthetic traces")
            time.sleep(1)
    out = pathlib.Path("evidence")
    out.mkdir(exist_ok=True)
    receipt = {
        "schema": "oec-otel-synthetic-receipt/v1",
        "collector_submission": "HTTP_200",
        "trace_id": TRACE,
        "parent_child_span_count": 2,
        "expected_negative_span": True,
        "sensitive_attribute_negatives": len(FORBIDDEN),
        "source": "SYNTHETIC_OTLP_JSON_ONLY",
        "no_prompts_or_credentials": True,
        "body_sha256": hashlib.sha256(body).hexdigest(),
    }
    (out / "ingress.json").write_text(json.dumps(receipt, sort_keys=True, indent=2) + "\n")
    print("QUALIFIED_OTLP_HTTP_SYNTHETIC_INGEST spans=2 privacy_negatives=5")

if __name__ == "__main__":
    main()
