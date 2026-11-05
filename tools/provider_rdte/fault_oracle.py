#!/usr/bin/env python3
"""Independent synthetic protocol/security oracle for SupraChat provider RDTE.

No provider SDK, credentials, imports from SupraChat or network traffic.
This is a *model* for adversarial proof obligations, NOT a production adapter,
not a session authority and not a direct test of product integration.
"""
from __future__ import annotations

import hashlib
import json
import re
import unittest
from dataclasses import dataclass


@dataclass(frozen=True)
class Submission:
    operation_id: str
    session_id: str
    provider_id: str
    account_alias: str
    payload_digest: str
    epoch: int


class SubmissionOracle:
    """Non-durable test double: actual idempotency belongs to SupraChat core."""
    def __init__(self):
        self.previous: dict[str, Submission] = {}
        self.uncertain: set[str] = set()

    def submit(self, submission: Submission) -> str:
        if not submission.operation_id or not submission.session_id or submission.epoch < 1:
            return "REFUSED"
        earlier = self.previous.get(submission.operation_id)
        if earlier:
            return "IDENTICAL_REPLAY" if earlier == submission else "REPLAY_CONFLICT"
        self.previous[submission.operation_id] = submission
        return "ADMITTED"

    def ambiguous_disconnect(self, operation_id: str) -> str:
        if operation_id not in self.previous:
            return "UNKNOWN_OPERATION"
        self.uncertain.add(operation_id)
        return "UNKNOWN_NO_AUTO_RETRY"

    def retry(self, operation_id: str) -> str:
        return "REFUSED_UNCERTAIN" if operation_id in self.uncertain else "MANUAL_RECONCILE"


class StreamOracle:
    """Bounded source-neutral JSON SSE terminal-event classifier."""
    allowed = frozenset({"delta", "completed", "failed", "incomplete"})
    terminal = frozenset({"completed", "failed", "incomplete"})

    def __init__(self, operation_id: str, *, max_frame=2048, max_events=128):
        self.operation_id = operation_id
        self.max_frame = max_frame
        self.max_events = max_events
        self.pending = b""
        self.count = 0
        self.seq = -1
        self.terminal_kind = None
        self.invalid = False

    def feed(self, fragment: bytes):
        if self.invalid:
            return
        self.pending += fragment
        if len(self.pending) > self.max_frame and b"\n\n" not in self.pending:
            self.invalid = True
            self.pending = b""
            return
        while b"\n\n" in self.pending:
            frame, self.pending = self.pending.split(b"\n\n", 1)
            if len(frame) > self.max_frame or not frame.startswith(b"data: "):
                self.invalid = True
                break
            try:
                event = json.loads(frame[6:].decode("utf-8"))
                if not isinstance(event, dict):
                    raise ValueError("not an object")
                seq = event["seq"]
                kind = event["kind"]
                if type(seq) is not int or seq <= self.seq:
                    raise ValueError("nonmonotonic sequence")
                if (
                    event["operation_id"] != self.operation_id
                    or kind not in self.allowed
                    or self.terminal_kind is not None
                    or self.count >= self.max_events
                ):
                    raise ValueError("bad event")
                self.seq = seq
                self.count += 1
                if kind in self.terminal:
                    self.terminal_kind = kind
            except (UnicodeDecodeError, json.JSONDecodeError, ValueError, KeyError, TypeError):
                self.invalid = True
                break
        if self.invalid:
            self.pending = b""

    def outcome(self, connection_closed: bool) -> str:
        if self.invalid:
            return "UNKNOWN"
        if not connection_closed or self.pending:
            return "UNKNOWN"
        return {
            "completed": "COMPLETED",
            "failed": "FAILED",
            "incomplete": "INCOMPLETE",
        }.get(self.terminal_kind, "UNKNOWN")


def frame(op: str, seq: int, kind: str) -> bytes:
    return b"data: " + json.dumps(
        {"operation_id": op, "seq": seq, "kind": kind}, separators=(",", ":")
    ).encode("utf-8") + b"\n\n"


def route_decision(current: tuple[str, str], destination: tuple[str, str],
                   approved: bool, classification: str) -> str:
    if classification not in {"public", "restricted", "sovereign"}:
        return "REFUSED_UNKNOWN_CLASS"
    if current == destination:
        return "SAME_BINDING"
    if not approved:
        return "REFUSED_TRANSFER_CONSENT"
    if classification == "sovereign":
        return "REFUSED_SOVEREIGN_BOUNDARY"
    return "EXPLICIT_TRANSFER"


_TELEM_FIELDS = frozenset({"operation_alias", "provider_alias", "outcome",
                           "duration_ms", "usage_source", "input_tokens", "output_tokens"})
_ALIAS = re.compile(r"^[a-z0-9_-]{1,32}$")
_OUTCOMES = frozenset({"COMPLETED", "FAILED", "INCOMPLETE", "UNKNOWN", "REFUSED"})
_USAGE = frozenset({"PROVIDER_REPORTED", "LOCALLY_MEASURED", "ESTIMATED", "UNKNOWN"})


def telemetry_projection(raw: dict) -> dict:
    """Positive allowlist: never forward content, HTTP bodies, cookies or headers."""
    out = {k: v for k, v in raw.items() if k in _TELEM_FIELDS}
    for key in ("operation_alias", "provider_alias"):
        if key in out and (not isinstance(out[key], str) or not _ALIAS.fullmatch(out[key])):
            out.pop(key)
    if out.get("outcome") not in _OUTCOMES:
        out.pop("outcome", None)
    if out.get("usage_source") not in _USAGE:
        out.pop("usage_source", None)
    for key in ("duration_ms", "input_tokens", "output_tokens"):
        value = out.get(key)
        if type(value) is not int or value < 0 or value > 10**9:
            out.pop(key, None)
    if out.get("usage_source") == "UNKNOWN":
        out.pop("input_tokens", None)
        out.pop("output_tokens", None)
    return out


class ContractOracleTests(unittest.TestCase):
    def setUp(self):
        self.a = Submission("op-a", "s1", "mock-a", "acct-1",
                            hashlib.sha256(b"request-a").hexdigest(), 1)
        self.b = Submission("op-b", "s2", "mock-b", "acct-2",
                            hashlib.sha256(b"request-b").hexdigest(), 1)

    def test_two_distinct_sessions(self):
        oracle = SubmissionOracle()
        self.assertEqual(oracle.submit(self.a), "ADMITTED")
        self.assertEqual(oracle.submit(self.b), "ADMITTED")

    def test_exact_replay_recognized(self):
        oracle = SubmissionOracle()
        oracle.submit(self.a)
        self.assertEqual(oracle.submit(self.a), "IDENTICAL_REPLAY")

    def test_changed_payload_collision(self):
        oracle = SubmissionOracle()
        oracle.submit(self.a)
        changed = Submission(**{**vars(self.a), "payload_digest": "changed"})
        self.assertEqual(oracle.submit(changed), "REPLAY_CONFLICT")

    def test_changed_account_collision(self):
        oracle = SubmissionOracle()
        oracle.submit(self.a)
        changed = Submission(**{**vars(self.a), "account_alias": "acct-2"})
        self.assertEqual(oracle.submit(changed), "REPLAY_CONFLICT")

    def test_changed_epoch_collision(self):
        oracle = SubmissionOracle()
        oracle.submit(self.a)
        changed = Submission(**{**vars(self.a), "epoch": 2})
        self.assertEqual(oracle.submit(changed), "REPLAY_CONFLICT")

    def test_ambiguous_retry_refused(self):
        oracle = SubmissionOracle()
        oracle.submit(self.a)
        self.assertEqual(oracle.ambiguous_disconnect("op-a"), "UNKNOWN_NO_AUTO_RETRY")
        self.assertEqual(oracle.retry("op-a"), "REFUSED_UNCERTAIN")

    def test_invalid_epoch_refused(self):
        oracle = SubmissionOracle()
        self.assertEqual(oracle.submit(Submission(**{**vars(self.a), "epoch": 0})), "REFUSED")

    def test_complete_stream(self):
        stream = StreamOracle("op-a")
        stream.feed(frame("op-a", 0, "delta") + frame("op-a", 1, "completed"))
        self.assertEqual(stream.outcome(True), "COMPLETED")

    def test_fragmented_stream(self):
        data = frame("op-a", 0, "delta") + frame("op-a", 1, "completed")
        stream = StreamOracle("op-a")
        for part in [data[:5], data[5:17], data[17:40], data[40:]]:
            stream.feed(part)
        self.assertEqual(stream.outcome(True), "COMPLETED")

    def test_missing_terminal_not_completed(self):
        stream = StreamOracle("op-a")
        stream.feed(frame("op-a", 0, "delta"))
        self.assertEqual(stream.outcome(True), "UNKNOWN")

    def test_open_transport_not_completed(self):
        stream = StreamOracle("op-a")
        stream.feed(frame("op-a", 0, "completed"))
        self.assertEqual(stream.outcome(False), "UNKNOWN")

    def test_cross_operation_refused(self):
        stream = StreamOracle("op-a")
        stream.feed(frame("op-b", 0, "completed"))
        self.assertEqual(stream.outcome(True), "UNKNOWN")

    def test_duplicate_seq_refused(self):
        stream = StreamOracle("op-a")
        stream.feed(frame("op-a", 0, "delta") + frame("op-a", 0, "completed"))
        self.assertEqual(stream.outcome(True), "UNKNOWN")

    def test_after_terminal_refused(self):
        stream = StreamOracle("op-a")
        stream.feed(frame("op-a", 0, "completed") + frame("op-a", 1, "delta"))
        self.assertEqual(stream.outcome(True), "UNKNOWN")

    def test_truncated_frame_not_completed(self):
        stream = StreamOracle("op-a")
        stream.feed(frame("op-a", 0, "completed")[:-1])
        self.assertEqual(stream.outcome(True), "UNKNOWN")

    def test_frame_limit(self):
        stream = StreamOracle("op-a", max_frame=32)
        stream.feed(b"x" * 33)
        self.assertEqual(stream.outcome(True), "UNKNOWN")

    def test_event_limit(self):
        stream = StreamOracle("op-a", max_events=1)
        stream.feed(frame("op-a", 0, "delta") + frame("op-a", 1, "completed"))
        self.assertEqual(stream.outcome(True), "UNKNOWN")

    def test_explicit_cross_vendor_consent(self):
        self.assertEqual(route_decision(("mock-a", "acct-1"),
                                        ("mock-b", "acct-2"), False, "public"),
                         "REFUSED_TRANSFER_CONSENT")

    def test_sovereign_no_cloud_even_with_consent(self):
        self.assertEqual(route_decision(("local", "acct-1"),
                                        ("cloud", "acct-2"), True, "sovereign"),
                         "REFUSED_SOVEREIGN_BOUNDARY")

    def test_unknown_classification_refused(self):
        self.assertEqual(route_decision(("local", "a"),
                                        ("cloud", "b"), True, "unclassified"),
                         "REFUSED_UNKNOWN_CLASS")

    def test_allowlisted_telemetry_secret_canary(self):
        canary = "NEVER_LOG_API_KEY_v1"
        raw = {"provider_alias": "mock-a", "operation_alias": "op-a",
               "outcome": "COMPLETED", "duration_ms": 12,
               "prompt": canary, "authorization": canary,
               "cookie": canary, "completion": canary,
               "input_tokens": 10, "usage_source": "PROVIDER_REPORTED"}
        result = telemetry_projection(raw)
        self.assertNotIn(canary, json.dumps(result))
        self.assertEqual(set(result), {"provider_alias", "operation_alias", "outcome",
                                      "duration_ms", "input_tokens", "usage_source"})

    def test_unknown_usage_not_zero(self):
        result = telemetry_projection({"usage_source": "UNKNOWN",
                                       "input_tokens": 0, "output_tokens": 0})
        self.assertNotIn("input_tokens", result)
        self.assertNotIn("output_tokens", result)

    def test_sensitive_alias_rejected(self):
        result = telemetry_projection({"operation_alias": "SECRET_TOKEN 1",
                                       "provider_alias": "../../other"})
        self.assertEqual(result, {})

    def test_negative_measurement_refused(self):
        self.assertEqual(telemetry_projection({"duration_ms": -1}), {})


if __name__ == "__main__":
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(ContractOracleTests)
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    print(json.dumps({
        "schema": "suprachat-provider-fault-oracle/v1",
        "classification": "SYNTHETIC_MODEL_ONLY",
        "cases": result.testsRun,
        "failures": len(result.failures),
        "errors": len(result.errors),
        "live_network": False,
        "provider_tokens": False,
        "source_authority_changed": False,
    }, sort_keys=True))
    raise SystemExit(0 if result.wasSuccessful() else 1)
