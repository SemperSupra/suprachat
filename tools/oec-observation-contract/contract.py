"""Fail-closed contract for SupraChat surface observations. Python 3 stdlib.

This validator is NOT an MCP server, does not open a listener, and does not
access an authenticated browser. Its purpose is to prevent unsupported claims
or unsafe payload fields before future MCP transport integration.
"""
import json
from typing import Any

SCHEMA = "suprachat-observable-surface/v1"
ROOT_KEYS = frozenset({"schema", "evidence_class", "authentication_claim",
                       "observer", "capability", "measurements", "privacy"})
METRIC_KEYS = frozenset({"root_present", "root_id_valid",
                         "tab_chatgpt_present", "tab_sessions_present",
                         "webview_document_count", "uia_descendant_count",
                         "sample_count", "provider_error_count"})
PRIVACY_KEYS = frozenset({"user_content", "cookies", "tokens", "dom_text",
                          "screenshots", "raw_trace", "window_titles"})
CAPABILITY_KEYS = frozenset({"observe_environment", "inspect_ui",
                             "compare_state", "remote_control"})
EVIDENCE = frozenset({"PUBLIC_SYNTHETIC_NATIVE", "USER_ATTESTED_LOCAL_UIA"})
AUTH = frozenset({"NOT_ASSESSED", "USER_ATTESTED_ONLY"})
OBSERVERS = frozenset({"PUBLIC_WINDOWS_TEST", "LOCAL_WINDOWS_READONLY"})
MAX_BYTES = 4096

class Refused(ValueError):
    pass

def no_duplicate_keys(pairs):
    doc = {}
    for name, value in pairs:
        if name in doc:
            raise Refused("DUPLICATE_JSON_KEY")
        doc[name] = value
    return doc

def exact_keys(value: Any, expected, field):
    if type(value) is not dict or set(value) != expected:
        raise Refused("UNEXPECTED_FIELDS_" + field.upper())

def exact_bool(value, field):
    if type(value) is not bool:
        raise Refused("BAD_BOOLEAN_" + field.upper())

def count(value, field, maximum):
    if type(value) is not int or value < 0 or value > maximum:
        raise Refused("OUT_OF_RANGE_" + field.upper())

def validate(raw):
    if isinstance(raw, str):
        raw = raw.encode("utf-8")
    if type(raw) is not bytes or len(raw) > MAX_BYTES:
        raise Refused("OVERSIZED_OR_NONBYTE_INPUT")
    try:
        data = json.loads(raw, object_pairs_hook=no_duplicate_keys)
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise Refused("INVALID_JSON") from exc
    exact_keys(data, ROOT_KEYS, "top")
    if data["schema"] != SCHEMA:
        raise Refused("BAD_SCHEMA")
    if data["evidence_class"] not in EVIDENCE:
        raise Refused("BAD_EVIDENCE_CLASS")
    if data["authentication_claim"] not in AUTH:
        raise Refused("BAD_AUTH_CLAIM")
    if data["observer"] not in OBSERVERS:
        raise Refused("BAD_OBSERVER")
    if data["evidence_class"] == "PUBLIC_SYNTHETIC_NATIVE":
        if data["observer"] != "PUBLIC_WINDOWS_TEST" or data["authentication_claim"] != "NOT_ASSESSED":
            raise Refused("SYNTHETIC_CANNOT_ATTEST_LOGIN")
    else:
        if data["observer"] != "LOCAL_WINDOWS_READONLY" or data["authentication_claim"] != "USER_ATTESTED_ONLY":
            raise Refused("LOCAL_REQUIRES_EXPLICIT_ATTESTATION")
    m = data["measurements"]
    exact_keys(m, METRIC_KEYS, "measurements")
    for key in ("root_present", "root_id_valid", "tab_chatgpt_present", "tab_sessions_present"):
        exact_bool(m[key], key)
    for key in ("webview_document_count", "uia_descendant_count", "provider_error_count", "sample_count"):
        count(m[key], key, 3000 if key in ("webview_document_count", "uia_descendant_count") else 20)
    if m["sample_count"] < 1 or m["provider_error_count"] > m["sample_count"]:
        raise Refused("INVALID_SAMPLE_COUNTS")
    if not m["root_present"] and (m["root_id_valid"] or m["tab_chatgpt_present"] or m["tab_sessions_present"]):
        raise Refused("ROOT_ABSENCE_CONTRADICTION")
    if (m["tab_chatgpt_present"] or m["tab_sessions_present"]) and not m["root_id_valid"]:
        raise Refused("UNTRUSTED_ROOT")
    if m["webview_document_count"] > m["uia_descendant_count"]:
        raise Refused("DOCUMENTS_EXCEED_DESCENDANTS")
    p = data["privacy"]
    exact_keys(p, PRIVACY_KEYS, "privacy")
    for k, v in p.items():
        exact_bool(v, k)
        if v:
            raise Refused("PROHIBITED_DATA_" + k.upper())
    c = data["capability"]
    exact_keys(c, CAPABILITY_KEYS, "capability")
    for k, v in c.items():
        exact_bool(v, k)
    if not c["observe_environment"] or not c["inspect_ui"] or c["remote_control"]:
        raise Refused("UNSAFE_TOOL_SCOPE")
    return data

def canonical_receipt(raw):
    data = validate(raw)
    return json.dumps(data, sort_keys=True, separators=(",", ":")) + "\n"
