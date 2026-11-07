#!/usr/bin/env python3
"""Synthetic, credential-free ChatGPT MCP conversation-correlation oracle.

This is a contract test, NOT an MCP server, OAuth implementation, ChatGPT
automation client, session database, or permission to access product transcripts.
Run: python3 -m unittest discover -s tools -p 'test_plugin_session_binding.py' -v
"""
from __future__ import annotations

import unittest
from dataclasses import dataclass, field


SCHEMA = "suprachat-plugin-binding/v1"


@dataclass(frozen=True)
class Call:
    operation: str
    principal: str | None = None
    authenticated: bool = False
    trusted_mcp_transport: bool = False
    meta: dict = field(default_factory=dict)
    workstream: str | None = None
    user_confirmed: bool = False


class SyntheticBindingOracle:
    """Pure model of a proposed boundary; no real authentication occurs here."""

    def __init__(self):
        self.acl = {"alice": {"ws-red", "ws-blue"}, "bob": {"ws-green"}}
        self.bindings: dict[tuple[str, str], str] = {}

    @staticmethod
    def outcome(status: str, workstream: str | None = None) -> dict:
        result = {"schema": SCHEMA, "status": status}
        if workstream is not None:
            result["workstream"] = workstream
        return result

    def apply(self, call: Call) -> dict:
        # In production the following two predicates must be established by
        # the MCP transport/OAuth middleware, never by a tool argument or _meta.
        if not call.trusted_mcp_transport:
            return self.outcome("REFUSED_UNTRUSTED_TRANSPORT")
        if not call.authenticated or not call.principal:
            return self.outcome("REFUSED_UNAUTHENTICATED")

        # ChatGPT remains authoritative for its own turns and history.
        if call.operation in {"chatgpt.turn.send", "chatgpt.turn.read",
                              "chatgpt.history.sync", "chatgpt.turn.delete"}:
            return self.outcome("BLOCKED_SUPPORTED_INTERFACE")
        if call.operation not in {"binding.confirm", "binding.inspect"}:
            return self.outcome("REFUSED_UNKNOWN_OPERATION")

        # _meta is a *correlation hint* from the authenticated MCP invocation.
        # It is not a credential, verified account ID, or ChatGPT turn ID.
        session = call.meta.get("openai/session")
        if not isinstance(session, str) or not session.strip() or len(session) > 512:
            return self.outcome("UNBOUND_NO_CORRELATION")
        key = (call.principal, session)

        if call.operation == "binding.inspect":
            mapped = self.bindings.get(key)
            if mapped is None:
                return self.outcome("UNBOUND")
            if mapped not in self.acl.get(call.principal, set()):
                return self.outcome("REFUSED_SCOPE")
            if call.workstream and call.workstream != mapped:
                return self.outcome("REFUSED_CONTEXT_MISMATCH")
            return self.outcome("BOUND", mapped)

        if not call.user_confirmed:
            return self.outcome("NEEDS_USER_CONFIRMATION")
        if not call.workstream or call.workstream not in self.acl.get(call.principal, set()):
            return self.outcome("REFUSED_SCOPE")
        prior = self.bindings.get(key)
        if prior:
            if prior != call.workstream:
                return self.outcome("REFUSED_REBIND")
            return self.outcome("ALREADY_BOUND", prior)
        self.bindings[key] = call.workstream
        return self.outcome("BOUND", call.workstream)


class PluginSessionBindingContract(unittest.TestCase):
    def setUp(self):
        self.oracle = SyntheticBindingOracle()

    def req(self, op="binding.inspect", principal="alice", session="opaque-A", **kw):
        return Call(operation=op, principal=principal, authenticated=True,
                    trusted_mcp_transport=True,
                    meta={"openai/session": session, "openai/subject": "untrusted-hint"},
                    **kw)

    def test_explicit_confirmation_then_consistent_inspection(self):
        bind = self.req("binding.confirm", workstream="ws-red", user_confirmed=True)
        self.assertEqual("BOUND", self.oracle.apply(bind)["status"])
        self.assertEqual({"schema": SCHEMA, "status": "BOUND", "workstream": "ws-red"},
                         self.oracle.apply(self.req()))

    def test_two_conversations_do_not_overwrite_each_other(self):
        self.oracle.apply(self.req("binding.confirm", workstream="ws-red", user_confirmed=True))
        self.oracle.apply(self.req("binding.confirm", session="opaque-B",
                                   workstream="ws-blue", user_confirmed=True))
        self.assertEqual("ws-red", self.oracle.apply(self.req())["workstream"])
        self.assertEqual("ws-blue", self.oracle.apply(self.req(session="opaque-B"))["workstream"])

    def test_another_authenticated_principal_cannot_read_first_binding(self):
        self.oracle.apply(self.req("binding.confirm", workstream="ws-red", user_confirmed=True))
        self.assertEqual("UNBOUND", self.oracle.apply(self.req(principal="bob"))["status"])
        self.assertEqual("BOUND", self.oracle.apply(self.req(
            "binding.confirm", principal="bob", workstream="ws-green",
            user_confirmed=True))["status"])
        self.assertEqual("ws-red", self.oracle.apply(self.req())["workstream"])

    def test_repeated_binding_is_noop_and_rebind_fails_closed(self):
        bind = self.req("binding.confirm", workstream="ws-red", user_confirmed=True)
        self.oracle.apply(bind)
        self.assertEqual("ALREADY_BOUND", self.oracle.apply(bind)["status"])
        self.assertEqual("REFUSED_REBIND", self.oracle.apply(self.req(
            "binding.confirm", workstream="ws-blue", user_confirmed=True))["status"])
        self.assertEqual("ws-red", self.oracle.apply(self.req())["workstream"])

    def test_user_approval_is_not_inferred_from_chatgpt_metadata(self):
        self.assertEqual("NEEDS_USER_CONFIRMATION", self.oracle.apply(self.req(
            "binding.confirm", workstream="ws-red", user_confirmed=False))["status"])
        self.assertEqual("UNBOUND", self.oracle.apply(self.req())["status"])

    def test_scope_denial_and_context_mismatch(self):
        self.assertEqual("REFUSED_SCOPE", self.oracle.apply(self.req(
            "binding.confirm", workstream="ws-green", user_confirmed=True))["status"])
        self.oracle.apply(self.req("binding.confirm", workstream="ws-red", user_confirmed=True))
        self.assertEqual("REFUSED_CONTEXT_MISMATCH", self.oracle.apply(
            self.req(workstream="ws-blue"))["status"])

    def test_no_identity_or_untrusted_transport(self):
        self.assertEqual("REFUSED_UNAUTHENTICATED", self.oracle.apply(Call(
            "binding.confirm", principal="alice", authenticated=False,
            trusted_mcp_transport=True, meta={"openai/session": "opaque-A"},
            workstream="ws-red", user_confirmed=True))["status"])
        self.assertEqual("REFUSED_UNTRUSTED_TRANSPORT", self.oracle.apply(Call(
            "binding.confirm", principal="alice", authenticated=True,
            trusted_mcp_transport=False, meta={"openai/session": "opaque-A"},
            workstream="ws-red", user_confirmed=True))["status"])

    def test_missing_malformed_or_oversized_correlation_fails_closed(self):
        for invalid in [None, "", " ", 42, "x" * 513]:
            with self.subTest(session=repr(invalid)[:24]):
                self.assertEqual("UNBOUND_NO_CORRELATION", self.oracle.apply(
                    self.req(session=invalid))["status"])

    def test_chatgpt_traffic_or_history_actions_never_inferred_from_plugin(self):
        for op in ["chatgpt.turn.send", "chatgpt.turn.read",
                   "chatgpt.history.sync", "chatgpt.turn.delete"]:
            with self.subTest(operation=op):
                self.assertEqual("BLOCKED_SUPPORTED_INTERFACE",
                                 self.oracle.apply(self.req(op))["status"])

    def test_metadata_subject_cannot_change_verified_principal(self):
        self.oracle.apply(self.req("binding.confirm", workstream="ws-red", user_confirmed=True))
        forged = Call("binding.inspect", principal="bob", authenticated=True,
                      trusted_mcp_transport=True,
                      meta={"openai/session": "opaque-A", "openai/subject": "alice"})
        self.assertEqual("UNBOUND", self.oracle.apply(forged)["status"])

    def test_no_raw_metadata_or_secret_sentinal_in_receipts(self):
        call = Call("binding.confirm", "alice", True, True, {
            "openai/session": "PRIVATE_SESSION_SENTINEL",
            "openai/subject": "PRIVATE_SUBJECT_SENTINEL",
            "authorization": "BEARER_SECRET_SENTINEL",
            "prompt": "PRIVATE_PROMPT_SENTINEL"}, "ws-red", True)
        receipt = str(self.oracle.apply(call))
        for secret in call.meta.values():
            self.assertNotIn(str(secret), receipt)

    def test_unknown_operation_denied(self):
        self.assertEqual("REFUSED_UNKNOWN_OPERATION",
                         self.oracle.apply(self.req("session.destroy"))["status"])


if __name__ == "__main__":
    unittest.main()
