"""Validate and minimize owner-local first-party metadata aggregates.

Does not collect or inspect raw WebView/CDP/network events. Reads only the
already-saved, consent-reviewed native aggregate and emits immutable
metadata-only results. Never promote signed-in state to independently verified.
"""
import hashlib
import json
import re
from contract import Refused, no_duplicate_keys

SCHEMA = "suprachat-first-party-metadata-hil-summary/v3"
MAX_SOURCE_BYTES = 32768
TOP = frozenset({
 "schema", "evidence", "scope", "completeness", "assembly_sha256",
 "observed_network_events", "native_loss", "capture_gap",
 "native_loss_semantics", "diagnostics", "capture_active",
 "observer_attached", "attaching", "first_lease_end_reason",
 "cleanup_complete", "enable_completed", "disable_completed",
 "stopped_interval_disclosed", "total_evicted_events", "retained_events",
 "lifecycle", "signed_in_state", "provider_operation_verified",
 "conversation_binding_verified", "human_identity_verified",
 "prompts_responses_headers_cookies_exported", "automatic_upload",
 "lifecycle_readback"
})
LIFECYCLE = frozenset({"schema", "build_version", "completeness",
                       "is_authoritative_for_consent", "events"})
ENTRY = frozenset({"ElapsedMs","Action","State","EndReason",
                   "Active","ObserverAttached","CleanupComplete",
                   "EnableCompleted","DisableCompleted"})
ACTIONS = frozenset({"Opened","Prepared","Confirmed","Refused",
                     "StopRequested","SaveRequested","SummarySaved",
                     "SaveFailed","CloseRequested","Closed","Observed"})

def _keys(doc, fields, label):
    if type(doc) is not dict or set(doc) != fields:
        raise Refused("UNEXPECTED_" + label + "_FIELDS")

def _boolean(doc, name, expected):
    if type(doc[name]) is not bool or doc[name] is not expected:
        raise Refused("UNSAFE_" + name.upper())

def _number(doc, name):
    x = doc[name]
    if type(x) is not int or not (0 <= x <= 1000000):
        raise Refused("BAD_" + name.upper())
    return x

def project(raw):
    if isinstance(raw, str):
        raw=raw.encode("utf-8")
    if type(raw) is not bytes or len(raw)>MAX_SOURCE_BYTES:
        raise Refused("INVALID_SOURCE_SIZE")
    try:
        d=json.loads(raw, object_pairs_hook=no_duplicate_keys)
    except (UnicodeError, json.JSONDecodeError) as e:
        raise Refused("INVALID_SOURCE_JSON") from e
    _keys(d,TOP,"TOP")
    if d["schema"] != SCHEMA or d["scope"] != "SINGLE_OPERATOR_CHATGPT_EXACT_ORIGIN":
        raise Refused("WRONG_FIRST_PARTY_SCOPE")
    if d["evidence"] not in ("FIRST_PARTY_LIVE_REDACTED","NO_FIRST_PARTY_OBSERVATIONS"):
        raise Refused("UNAPPROVED_EVIDENCE")
    if d["completeness"] != "UNKNOWN" or d["signed_in_state"] != "OPERATOR_CHECK_REQUIRED":
        raise Refused("FALSE_VERIFICATION_CLAIM")
    if d["native_loss_semantics"] != "DIAGNOSTIC_INCIDENTS_NOT_LOST_EVENT_COUNT":
        raise Refused("UNSUPPORTED_LOSS_SEMANTICS")
    if d["assembly_sha256"]!="UNAVAILABLE" and not re.fullmatch(r"[A-Fa-f0-9]{64}",d["assembly_sha256"]):
        raise Refused("BAD_ASSEMBLY_DIGEST")
    for k in ("capture_active","observer_attached","attaching",
              "provider_operation_verified","conversation_binding_verified",
              "human_identity_verified","prompts_responses_headers_cookies_exported",
              "automatic_upload"):
        _boolean(d,k,False)
    for k in ("cleanup_complete","stopped_interval_disclosed"):
        _boolean(d,k,True)
    for k in ("enable_completed","disable_completed","capture_gap"):
        if type(d[k]) is not bool: raise Refused("INVALID_BOOLEAN_"+k.upper())
    observed=_number(d,"observed_network_events")
    loss=_number(d,"native_loss")
    _number(d,"total_evicted_events")
    _number(d,"retained_events")
    if (observed>0) != (d["evidence"]=="FIRST_PARTY_LIVE_REDACTED"):
        raise Refused("EVIDENCE_COUNT_MISMATCH")
    if type(d["first_lease_end_reason"]) is not str or not 1 <= len(d["first_lease_end_reason"]) <= 60:
        raise Refused("BAD_LEASE_END_REASON")
    if type(d["lifecycle"]) is not dict or len(d["lifecycle"])>25:
        raise Refused("UNBOUNDED_EVENT_KIND_SUMMARY")
    for k,v in d["lifecycle"].items():
        if type(k) is not str or not re.fullmatch(r"[A-Za-z]{1,40}",k) or type(v) is not int or not 0<=v<=1000000:
            raise Refused("UNSAFE_EVENT_KIND_SUMMARY")
    # Diagnostics are a locally-authored bounded object; never send it to MCP.
    if type(d["diagnostics"]) is not dict or len(d["diagnostics"])>32:
        raise Refused("UNSAFE_DIAGNOSTICS_SHAPE")
    hist=d["lifecycle_readback"]
    _keys(hist,LIFECYCLE,"LIFECYCLE")
    if hist["schema"]!="suprachat-native-lifecycle-readback/v1" or hist["completeness"]!="UNKNOWN":
        raise Refused("BAD_LIFECYCLE_VERSION")
    _boolean(hist,"is_authoritative_for_consent",False)
    events=hist["events"]
    if type(events) is not list or not 1<=len(events)<=24:
        raise Refused("UNBOUNDED_LIFECYCLE")
    for event in events:
        _keys(event,ENTRY,"LIFECYCLE_EVENT")
        if event["Action"] not in ACTIONS:
            raise Refused("UNKNOWN_LIFECYCLE_ACTION")
        if type(event["ElapsedMs"]) is not int or not 0<=event["ElapsedMs"]<=3600000:
            raise Refused("BAD_LIFECYCLE_ELAPSED")
        for k in ("Active","ObserverAttached","CleanupComplete","EnableCompleted","DisableCompleted"):
            if type(event[k]) is not bool: raise Refused("BAD_LIFECYCLE_BOOLEAN")
        for k in ("State","EndReason"):
            if type(event[k]) is not str or not re.fullmatch(r"[A-Za-z_]{1,60}",event[k]):
                raise Refused("BAD_LIFECYCLE_CLASS")
    last=events[-1]
    if (last["Active"]!=d["capture_active"] or last["ObserverAttached"]!=d["observer_attached"] or
        last["CleanupComplete"]!=d["cleanup_complete"] or
        last["EndReason"]!=d["first_lease_end_reason"]):
        raise Refused("LIFECYCLE_STATUS_DISAGREEMENT")
    # Only output fixed approved fields. Nothing from diagnostics, lifecycle,
    # event text, source location, GUIDs, web URLs or credentials is propagated.
    return {
      "schema":"oec-consented-first-party-projection/v1",
      "source_schema":SCHEMA,
      "evidence_class":d["evidence"],
      "source_aggregate_sha256":hashlib.sha256(raw).hexdigest(),
      "observed_network_events":observed,
      "native_loss":loss,
      "capture_gap":d["capture_gap"],
      "cleanup_complete":d["cleanup_complete"],
      "capture_stopped":True,
      "authenticated_state":"OPERATOR_CHECK_REQUIRED",
      "user_content_exported":False,
      "verified_provider_action":False,
      "source_completeness":"UNKNOWN"
    }

def canonical_projection(raw):
    return json.dumps(project(raw),sort_keys=True,separators=(",",":"))+"\n"
