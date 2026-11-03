#!/usr/bin/env python3
"""Strict ZIP-byte and public-synthetic privacy audit. No external Python packages."""
import argparse
import hashlib
import io
import json
import pathlib
import re
import zipfile

VALID_HEX = re.compile(r"^[0-9a-f]{64}$")
SENSITIVE_VALUE = re.compile(
    r"(?i)(?:bearer\s+[a-z0-9._-]{8,}|github_pat_|ghp_[a-z0-9]+|sk-[a-z0-9]{12,}|https?://)"
)
SENSITIVE_KEYS = frozenset({
    "prompt", "messages", "response_text", "private_key", "api_key",
    "access_token", "authorization", "cookie", "cookies", "session_token",
    "raw_html", "raw_snapshot", "network_headers", "password",
})
MAX_ZIP_BYTES = 131072
MAX_JSON_BYTES = 16384

class AuditFailure(ValueError):
    pass

def reject_sensitive(obj):
    if isinstance(obj, dict):
        for k, value in obj.items():
            if k.lower() in SENSITIVE_KEYS:
                raise AuditFailure("forbidden JSON field")
            reject_sensitive(value)
    elif isinstance(obj, list):
        for item in obj:
            reject_sensitive(item)
    elif isinstance(obj, str) and SENSITIVE_VALUE.search(obj):
        raise AuditFailure("private token or endpoint marker")

def audit_one(entry, zip_data):
    if len(zip_data) > MAX_ZIP_BYTES or len(zip_data) < 100:
        raise AuditFailure("archive size outside bound")
    claimed = entry["archive_sha256"]
    if not VALID_HEX.fullmatch(claimed):
        raise AuditFailure("invalid expected archive digest")
    archive_digest = hashlib.sha256(zip_data).hexdigest()
    if archive_digest != claimed:
        raise AuditFailure("archive digest mismatch")
    with zipfile.ZipFile(io.BytesIO(zip_data)) as z:
        members = [n for n in z.namelist() if not n.endswith("/")]
        if len(members) != 1 or pathlib.PurePosixPath(members[0]).name != "receipt.json":
            raise AuditFailure("archive contains unexpected files")
        name = members[0]
        if name.startswith("/") or "\\" in name or ".." in pathlib.PurePosixPath(name).parts:
            raise AuditFailure("unsafe archive member")
        info = z.getinfo(name)
        if info.file_size > MAX_JSON_BYTES or info.compress_size == 0:
            raise AuditFailure("expanded JSON size outside bound")
        payload = z.read(name)
    try:
        record = json.loads(payload)
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise AuditFailure("invalid receipt JSON") from exc
    if not isinstance(record, dict):
        raise AuditFailure("receipt is not a JSON object")
    if record.get("schema") != entry["receipt_schema"]:
        raise AuditFailure("receipt schema drift")
    reject_sensitive(record)
    if entry["name"] == "inspector":
        if record.get("contains_credentials") is not False or record.get("exposes_chatgpt_internals") is not False:
            raise AuditFailure("Inspector claims excess authority")
    if entry["name"] == "chrome":
        if record.get("raw_requests_exported") is not False or record.get("session_cookies_exported") is not False or record.get("real_chatgpt_browser") is not False:
            raise AuditFailure("Chrome witness claims excess authority")
    if entry["name"] == "otel":
        if record.get("no_prompts_or_credentials") is not True or record.get("collector_debug_readback") != "PASS":
            raise AuditFailure("collector privacy or readback invalid")
    if entry["name"] == "cross-witness":
        if record.get("credentials_exported") is not False or record.get("authenticated_chatgpt_ui") is not False or record.get("hidden_chatgpt_model_access") is not False:
            raise AuditFailure("cross-witness claims excess authority")
        if record.get("source_class") != "CONTROLLED_SYNTHETIC_ONLY":
            raise AuditFailure("cross-witness evidence class unsupported")
    return {
      "name": entry["name"],
      "artifact_id": entry["artifact_id"],
      "run_id": entry["run_id"],
      "head_sha": entry["head_sha"],
      "archive_sha256": archive_digest,
      "receipt_sha256": hashlib.sha256(payload).hexdigest(),
      "receipt_bytes": len(payload),
      "schema": entry["receipt_schema"],
      "privacy": "PASS",
      "evidence_class": "PUBLIC_SYNTHETIC_ONLY",
    }

def main():
    p = argparse.ArgumentParser()
    p.add_argument("--manifest", required=True)
    p.add_argument("--zip-dir", required=True)
    p.add_argument("--out", required=True)
    args = p.parse_args()
    manifest = json.loads(pathlib.Path(args.manifest).read_text())
    if (manifest.get("schema") != "oec-evidence-artifact-manifest/v1" or
        manifest.get("repository") != "SemperSupra/suprachat" or
        manifest.get("evidence_class") != "PUBLIC_SYNTHETIC_ONLY"):
        raise AuditFailure("source manifest invalid")
    sources = manifest["sources"]
    if len(sources) != 4 or len({s["name"] for s in sources}) != 4:
        raise AuditFailure("unexpected source inventory")
    result = [audit_one(s, (pathlib.Path(args.zip_dir)/(s["name"]+".zip")).read_bytes())
              for s in sources]
    report = {
      "schema": "oec-evidence-zip-audit/v1",
      "status": "QUALIFIED_PUBLIC_SYNTHETIC_CUSTODY",
      "records": result,
      "zip_bytes_independently_hashed": True,
      "source_content_raw_exported": False,
      "sensitive_content_recorded": False,
      "limits": [
        "Original GHA artifacts expire under their configured retention",
        "SHA-indexed metadata does not preserve expired original ZIP bytes",
        "Does not independently attest private ChatGPT behavior",
      ],
    }
    target = pathlib.Path(args.out)
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(report, sort_keys=True, indent=2)+"\n")
    print("QUALIFIED_OEC_ARTIFACT_ZIP_CUSTODY count=4 privacy=true synthetic_only=true")

if __name__ == "__main__":
    main()
