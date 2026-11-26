import copy
import json
import unittest
from contract import Refused
from native_summary import project, canonical_projection

def sample():
    return {
      "schema":"suprachat-first-party-metadata-hil-summary/v3",
      "evidence":"FIRST_PARTY_LIVE_REDACTED",
      "scope":"SINGLE_OPERATOR_CHATGPT_EXACT_ORIGIN",
      "completeness":"UNKNOWN",
      "assembly_sha256":"A"*64,
      "observed_network_events":12,
      "native_loss":0,
      "capture_gap":False,
      "native_loss_semantics":"DIAGNOSTIC_INCIDENTS_NOT_LOST_EVENT_COUNT",
      "diagnostics":{},
      "capture_active":False,
      "observer_attached":False,
      "attaching":False,
      "first_lease_end_reason":"SummarySave",
      "cleanup_complete":True,
      "enable_completed":True,
      "disable_completed":True,
      "stopped_interval_disclosed":True,
      "total_evicted_events":0,
      "retained_events":12,
      "lifecycle":{"NetworkObserved":12},
      "signed_in_state":"OPERATOR_CHECK_REQUIRED",
      "provider_operation_verified":False,
      "conversation_binding_verified":False,
      "human_identity_verified":False,
      "prompts_responses_headers_cookies_exported":False,
      "automatic_upload":False,
      "lifecycle_readback":{
        "schema":"suprachat-native-lifecycle-readback/v1",
        "build_version":"1.0.0.0",
        "completeness":"UNKNOWN",
        "is_authoritative_for_consent":False,
        "events":[
          {"ElapsedMs":150,"Action":"Opened","State":"UNKNOWN","EndReason":"None",
           "Active":False,"ObserverAttached":False,"CleanupComplete":False,
           "EnableCompleted":False,"DisableCompleted":False},
          {"ElapsedMs":3000,"Action":"Observed","State":"STOPPED","EndReason":"SummarySave",
           "Active":False,"ObserverAttached":False,"CleanupComplete":True,
           "EnableCompleted":True,"DisableCompleted":True}
        ]
      }
    }

def wire(x):
    return json.dumps(x,sort_keys=True).encode()

class NativeSummaryTests(unittest.TestCase):
    def deny(self,x):
        with self.assertRaises(Refused):project(wire(x))

    def test_minimal_consented_projection(self):
        r=project(wire(sample()))
        self.assertEqual(r["observed_network_events"],12)
        self.assertEqual(r["authenticated_state"],"OPERATOR_CHECK_REQUIRED")
        self.assertFalse(r["user_content_exported"])
        self.assertEqual(len(r["source_aggregate_sha256"]),64)

    def test_canonical_repeatability(self):
        x=sample()
        self.assertEqual(canonical_projection(wire(x)),canonical_projection(wire(x)))

    def test_empty_native_capture_classification(self):
        x=sample();x["observed_network_events"]=0;x["evidence"]="NO_FIRST_PARTY_OBSERVATIONS"
        self.assertEqual(project(wire(x))["observed_network_events"],0)

    def test_synthetic_scope_refused(self):
        x=sample();x["scope"]="SYNTHETIC_LOOPBACK_NATIVE";self.deny(x)

    def test_pretend_login_verified_refused(self):
        x=sample();x["signed_in_state"]="VERIFIED";self.deny(x)

    def test_raw_credentials_claim_refused(self):
        x=sample();x["prompts_responses_headers_cookies_exported"]=True;self.deny(x)

    def test_automatic_upload_claim_refused(self):
        x=sample();x["automatic_upload"]=True;self.deny(x)

    def test_still_active_refused(self):
        x=sample();x["capture_active"]=True;self.deny(x)

    def test_no_cleanup_refused(self):
        x=sample();x["cleanup_complete"]=False;self.deny(x)

    def test_extra_url_field_refused(self):
        x=sample();x["url"]="https://chatgpt.com/c/secret";self.deny(x)

    def test_nested_event_details_refused(self):
        x=sample();x["lifecycle_readback"]["events"][0]["RawUrl"]="secret";self.deny(x)

    def test_mismatched_network_events_refused(self):
        x=sample();x["observed_network_events"]=0;self.deny(x)

    def test_lifecycle_disagreement_refused(self):
        x=sample();x["lifecycle_readback"]["events"][-1]["CleanupComplete"]=False;self.deny(x)

    def test_unbounded_history_refused(self):
        x=sample();x["lifecycle_readback"]["events"]*=13;self.deny(x)

    def test_duplicate_key_refused(self):
        raw=wire(sample()).decode().replace('"scope":','"scope":"wrong","scope":',1)
        with self.assertRaises(Refused):project(raw)

    def test_wrong_schema_refused(self):
        x=sample();x["schema"]="suprachat-synthetic-native-metadata-summary/v2";self.deny(x)

    def test_no_diagnostics_leakage(self):
        x=sample();x["diagnostics"]={"OpaqueSafeCount":0}
        out=canonical_projection(wire(x))
        self.assertNotIn("OpaqueSafeCount",out)
        self.assertNotIn("lifecycle_readback",out)

    def test_overlarge_summary_refused(self):
        with self.assertRaises(Refused):project(b" "*33000)

if __name__=="__main__":
    unittest.main()
