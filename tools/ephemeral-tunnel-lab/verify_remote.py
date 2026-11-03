#!/usr/bin/env python3
"""HTTPS remote MCP JSON fixture client. Never prints the URL, token, or payload."""
import argparse
import hashlib
import json
import os
import platform
import secrets
import sys
import time
import urllib.error
import urllib.request

def send(base, token, method, ident, params=None, origin=None):
    message = {"jsonrpc": "2.0", "id": ident, "method": method}
    if params is not None:
        message["params"] = params
    data = json.dumps(message, separators=(",", ":")).encode()
    headers = {
        "Authorization": "Bearer " + token,
        "Content-Type": "application/json",
        "Accept": "application/json",
        "MCP-Protocol-Version": "2025-06-18",
        "User-Agent": "OEC-Disposable-Tunnel-Qualification/0.1",
    }
    if origin:
        headers["Origin"] = origin
    req = urllib.request.Request(base + "/mcp", data=data, headers=headers, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=7) as res:
            return res.status, json.loads(res.read(8192))
    except urllib.error.HTTPError as err:
        try:
            payload = json.loads(err.read(8192))
        except ValueError:
            payload = {}
        return err.code, payload

def require(ok, message):
    if not ok:
        raise RuntimeError(message)

def negatives(base, token):
    status, _ = send(base, "incorrect-token", "tools/list", 71)
    require(status == 401, "remote invalid bearer did not fail closed")
    status, _ = send(base, token, "tools/list", 72, origin="https://untrusted.example.invalid")
    require(status == 403, "remote browser-origin did not fail closed")
    status, obj = send(base, token, "tools/call", 73, {"name": "shell.exec", "arguments": {}})
    require(status == 200 and obj.get("error", {}).get("code") == -32601,
            "unregistered remote tool was not refused")

def run(url, local, token):
    require(url.startswith("https://") and url.endswith(".trycloudflare.com"),
            "refuse non-Quick-Tunnel URL")
    ready, _ = send(local, token, "initialize", 1)
    require(ready == 200, "local fixture failed initialization")
    for i in range(24):
        try:
            status, init = send(url, token, "initialize", 2)
            if status == 200 and init.get("result", {}).get("protocolVersion") == "2025-06-18":
                break
        except (OSError, TimeoutError, ValueError):
            pass
        time.sleep(2)
    else:
        raise RuntimeError("Quick Tunnel public HTTPS request never initialized")
    status, listing = send(url, token, "tools/list", 3)
    names = [x.get("name") for x in listing.get("result", {}).get("tools", [])]
    require(status == 200 and set(names) == {"lab.observe", "lab.digest"},
            "unexpected remote tool catalog")
    status, local_obs = send(local, token, "tools/call", 4, {"name": "lab.observe", "arguments": {}})
    status2, remote_obs = send(url, token, "tools/call", 5, {"name": "lab.observe", "arguments": {}})
    loc = local_obs.get("result", {}).get("structuredContent", {})
    rem = remote_obs.get("result", {}).get("structuredContent", {})
    require(status == 200 and status2 == 200 and loc and loc == rem,
            "HTTPS remote did not reach this same live runner instance")
    require(rem.get("evidence_class") == "DIRECT_RUNNER"
            and rem.get("transport") == "HTTP_JSON_NONSTREAMING",
            "evidence scope overclaimed")
    challenge = secrets.token_hex(16)
    status3, response = send(url, token, "tools/call", 6,
        {"name": "lab.digest", "arguments": {"challenge": challenge}})
    result = response.get("result", {}).get("structuredContent", {})
    expected = hashlib.sha256(challenge.encode()).hexdigest()
    require(status3 == 200 and result.get("sha256") == expected
            and result.get("nonce") == rem.get("nonce")
            and result.get("evidence_class") == "SYNTHETIC_REMOTE_ROUNDTRIP",
            "remote challenge digest mismatch")
    negatives(url, token)
    return {
      "schema": "oec-ephemeral-tunnel-receipt/v1",
      "evidence_class": "DIRECT_PUBLIC_HTTPS_SYNTHETIC_FIXTURE",
      "runner_os": platform.system().lower(),
      "transport": "quick_tunnel_https_json_no_sse",
      "mcp_initialization": "PASS",
      "tool_discovery": "PASS",
      "live_process_nonce_match": "PASS",
      "challenge_digest_roundtrip": "PASS",
      "unauthorized_401": "PASS",
      "origin_403": "PASS",
      "unknown_tool_refusal": "PASS",
      "secrets_or_endpoints_exported": False,
      "chatgpt_internal_visibility": False,
    }

def main():
    p = argparse.ArgumentParser()
    p.add_argument("--url", required=True)
    p.add_argument("--local", default="http://127.0.0.1:18761")
    p.add_argument("--receipt", required=True)
    args = p.parse_args()
    token = os.environ.get("OEC_LAB_TOKEN", "")
    require(len(token) >= 48, "missing runner-local token")
    result = run(args.url, args.local, token)
    with open(args.receipt, "w", encoding="utf-8") as out:
        json.dump(result, out, indent=2, sort_keys=True)
        out.write("\n")
    print("QUALIFIED_EPHEMERAL_QUICK_TUNNEL_HTTPS_MCP_ONLY auth_negatives=3 secrets=false sse=false")

if __name__ == "__main__":
    try:
        main()
    except Exception:
        print("EPHEMERAL_TUNNEL_QUALIFICATION_FAILED: sanitized; inspect bounded runner-local traces", file=sys.stderr)
        sys.exit(1)
