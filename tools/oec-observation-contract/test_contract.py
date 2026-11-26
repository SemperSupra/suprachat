import copy
import json
import unittest
from contract import Refused, canonical_receipt, validate

FIXTURE = {
 "schema": "suprachat-observable-surface/v1",
 "evidence_class": "PUBLIC_SYNTHETIC_NATIVE",
 "authentication_claim": "NOT_ASSESSED",
 "observer": "PUBLIC_WINDOWS_TEST",
 "capability": {"observe_environment": True, "inspect_ui": True, "compare_state": True, "remote_control": False},
 "measurements": {"root_present": True, "root_id_valid": True,
                  "tab_chatgpt_present": True, "tab_sessions_present": True,
                  "webview_document_count": 1, "uia_descendant_count": 128,
                  "sample_count": 6, "provider_error_count": 0},
 "privacy": {"user_content": False, "cookies": False, "tokens": False,
             "dom_text": False, "screenshots": False, "raw_trace": False,
             "window_titles": False}
}

def wire(value):
    return json.dumps(value, sort_keys=True).encode()

class ContractTests(unittest.TestCase):
    def assert_denied(self, obj):
        with self.assertRaises(Refused):
            validate(wire(obj))

    def test_fixture_qualifies(self):
        self.assertEqual(validate(wire(FIXTURE))["measurements"]["uia_descendant_count"], 128)

    def test_deterministic_canonical_receipt(self):
        a = canonical_receipt(wire(FIXTURE))
        b = canonical_receipt(json.dumps(FIXTURE).encode())
        self.assertEqual(a, b)
        self.assertNotIn("cookie", a.lower().replace('"cookies":false', ""))

    def test_unknown_attribute_fails(self):
        x=copy.deepcopy(FIXTURE);x["user_email"]="x@example.invalid";self.assert_denied(x)

    def test_nested_credentials_fail(self):
        x=copy.deepcopy(FIXTURE);x["measurements"]["access_token"]="synthetic";self.assert_denied(x)

    def test_unapproved_content_fields_fail(self):
        for key in ("dom_text", "screenshots", "raw_trace", "window_titles", "cookies"):
            x=copy.deepcopy(FIXTURE);x["privacy"][key]=True
            with self.subTest(key=key):self.assert_denied(x)

    def test_synthetic_cannot_claim_authenticated(self):
        x=copy.deepcopy(FIXTURE);x["authentication_claim"]="USER_ATTESTED_ONLY";self.assert_denied(x)

    def test_attested_local_is_classified_not_verified(self):
        x=copy.deepcopy(FIXTURE)
        x["evidence_class"]="USER_ATTESTED_LOCAL_UIA"
        x["authentication_claim"]="USER_ATTESTED_ONLY"
        x["observer"]="LOCAL_WINDOWS_READONLY"
        self.assertEqual(validate(wire(x))["authentication_claim"],"USER_ATTESTED_ONLY")

    def test_local_requires_operator_attestation(self):
        x=copy.deepcopy(FIXTURE);x["evidence_class"]="USER_ATTESTED_LOCAL_UIA"
        x["observer"]="LOCAL_WINDOWS_READONLY"
        self.assert_denied(x)

    def test_no_remote_control(self):
        x=copy.deepcopy(FIXTURE);x["capability"]["remote_control"]=True;self.assert_denied(x)

    def test_no_mutable_tool_scope(self):
        x=copy.deepcopy(FIXTURE);x["capability"]["inspect_ui"]=False;self.assert_denied(x)

    def test_no_implicit_zero_root(self):
        x=copy.deepcopy(FIXTURE);x["measurements"]["root_present"]=False;self.assert_denied(x)

    def test_no_wrong_metrics(self):
        for v in (-1,3001,True,1.2,"1"):
            x=copy.deepcopy(FIXTURE);x["measurements"]["uia_descendant_count"]=v
            with self.subTest(v=v):self.assert_denied(x)

    def test_no_excess_provider_errors(self):
        x=copy.deepcopy(FIXTURE);x["measurements"]["provider_error_count"]=7;self.assert_denied(x)

    def test_no_duplicate_json_keys(self):
        raw=wire(FIXTURE).decode().replace('"schema":', '"schema":"suprachat-observable-surface/v1","schema":', 1)
        with self.assertRaises(Refused):validate(raw)

    def test_reject_oversized_input(self):
        with self.assertRaises(Refused):validate(b" " * 4097)

    def test_reject_trailing_json(self):
        with self.assertRaises(Refused):validate(wire(FIXTURE)+b" {}")

if __name__ == "__main__":
    unittest.main()
