#!/usr/bin/env python3
"""Synthetic-only GitHub-run-scoped Cloudflare rendezvous envelope. stdlib."""
import argparse
import hashlib
import json
import os
import pathlib
import re
import sys
import time

URL = re.compile(r"https://[a-z0-9-]{1,80}[.]trycloudflare[.]com\Z")
HEX24 = re.compile(r"[0-9a-f]{24}\Z")
HEX40 = re.compile(r"[0-9a-f]{40}\Z")
HEX64 = re.compile(r"[0-9a-f]{64}\Z")
TOOLS = ["lab.digest", "lab.observe"]
KEYS = {
    "schema", "endpoint", "cipher", "server_nonce", "workflow_run_id", "workflow_attempt", "producer_job",
    "repository", "source_sha", "issued_unix", "expires_unix",
    "client_key_sha256", "encrypted_token_sha256", "allowed_tools",
}
SCHEMA = "oec-rendezvous-public/v2"
TTL_SECONDS = 600

class Refused(ValueError):
    pass

def digest(value):
    return hashlib.sha256(value).hexdigest()

def no_duplicate_keys(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise Refused("duplicate manifest JSON key")
        result[key] = value
    return result

def build(url, nonce, public_key, ciphertext, run_id, repo, sha, now, attempt="1"):
    if not URL.fullmatch(url) or not HEX24.fullmatch(nonce):
        raise Refused("unapproved endpoint/nonce")
    if not str(run_id).isdigit() or not HEX40.fullmatch(sha) or repo != "SemperSupra/suprachat":
        raise Refused("unapproved workflow identity")
    if len(public_key) < 300 or len(ciphertext) < 256:
        raise Refused("invalid key or encrypted token length")
    if not isinstance(now, int):
        raise Refused("invalid clock")
    return {
        "schema": SCHEMA,
        "endpoint": url,
        "cipher": "encrypted-token.bin",
        "server_nonce": nonce,
        "workflow_run_id": str(run_id),
        "workflow_attempt": str(attempt),
        "producer_job": "server",
        "repository": repo,
        "source_sha": sha,
        "issued_unix": now,
        "expires_unix": now + TTL_SECONDS,
        "client_key_sha256": digest(public_key),
        "encrypted_token_sha256": digest(ciphertext),
        "allowed_tools": TOOLS,
    }

def verify(doc, public_key, ciphertext, run_id, repo, sha, now, attempt="1"):
    if not isinstance(doc, dict) or set(doc) != KEYS:
        raise Refused("unrecognized manifest structure")
    if doc["schema"] != SCHEMA or doc["cipher"] != "encrypted-token.bin":
        raise Refused("unapproved schema or cipher")
    if not isinstance(doc["endpoint"], str) or not URL.fullmatch(doc["endpoint"]):
        raise Refused("endpoint substitution")
    if not isinstance(doc["server_nonce"], str) or not HEX24.fullmatch(doc["server_nonce"]):
        raise Refused("invalid server nonce")
    if (doc["workflow_run_id"] != str(run_id) or
        doc["workflow_attempt"] != str(attempt) or doc["producer_job"] != "server" or
        doc["repository"] != repo or
        doc["source_sha"] != sha or repo != "SemperSupra/suprachat" or
        not isinstance(sha, str) or not HEX40.fullmatch(sha)):
        raise Refused("cross-run or source identity substitution")
    if doc["allowed_tools"] != TOOLS:
        raise Refused("tool scope changed")
    issued, expires = doc["issued_unix"], doc["expires_unix"]
    if type(issued) is not int or type(expires) is not int:
        raise Refused("noninteger time bounds")
    if issued > now + 30 or expires < now or expires - issued != TTL_SECONDS:
        raise Refused("expired, replayed or future manifest")
    if len(public_key) < 300 or len(ciphertext) < 256:
        raise Refused("invalid key or ciphertext size")
    if doc["client_key_sha256"] != digest(public_key):
        raise Refused("public key fingerprint substitution")
    if doc["encrypted_token_sha256"] != digest(ciphertext):
        raise Refused("encrypted payload substitution")
    return doc["endpoint"]

def main():
    p = argparse.ArgumentParser()
    p.add_argument("command", choices=["create", "verify"])
    p.add_argument("--manifest", required=True)
    p.add_argument("--client-pem", required=True)
    p.add_argument("--encrypted-token", required=True)
    p.add_argument("--endpoint")
    p.add_argument("--server-nonce")
    a = p.parse_args()
    try:
        run_id = os.environ["GITHUB_RUN_ID"]
        repo = os.environ["GITHUB_REPOSITORY"]
        sha = os.environ["GITHUB_SHA"]
        attempt = os.environ["GITHUB_RUN_ATTEMPT"]
        public_key = pathlib.Path(a.client_pem).read_bytes()
        ciphertext = pathlib.Path(a.encrypted_token).read_bytes()
        now = int(time.time())
        if a.command == "create":
            if not a.endpoint or not a.server_nonce:
                raise Refused("create needs endpoint and nonce")
            result = build(a.endpoint, a.server_nonce, public_key,
                           ciphertext, run_id, repo, sha, now, attempt)
            pathlib.Path(a.manifest).write_text(json.dumps(result, sort_keys=True)+"\n")
        else:
            raw = pathlib.Path(a.manifest).read_bytes()
            if len(raw) > 4096:
                raise Refused("oversized manifest")
            doc = json.loads(raw, object_pairs_hook=no_duplicate_keys)
            print(verify(doc, public_key, ciphertext, run_id, repo, sha, now, attempt))
    except (Refused, KeyError, OSError, UnicodeError, json.JSONDecodeError):
        print("RENDEZVOUS_V2_FAIL_CLOSED", file=sys.stderr)
        sys.exit(1)

if __name__ == "__main__":
    main()
