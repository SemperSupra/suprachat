import copy
import json
import unittest
from handoff import build, verify, Refused, no_duplicate_keys

class RendezvousV2SecurityTests(unittest.TestCase):
    def setUp(self):
        self.key=b"P"*400
        self.cipher=b"C"*384
        self.run="123456"
        self.repo="SemperSupra/suprachat"
        self.sha="b"*40
        self.now=1700000000
        self.msg=build("https://synthetic-unique.trycloudflare.com",
            "a"*24,self.key,self.cipher,self.run,self.repo,self.sha,self.now)

    def check(self, msg=None, key=None, cipher=None, run=None, sha=None, now=None):
        return verify(msg if msg is not None else self.msg,
            key if key is not None else self.key,
            cipher if cipher is not None else self.cipher,
            run if run is not None else self.run,
            self.repo,
            sha if sha is not None else self.sha,
            now if now is not None else self.now+1)

    def test_valid_run_scoped_roundtrip(self):
        self.assertEqual(self.check(),self.msg["endpoint"])

    def test_cross_run_replay_refused(self):
        with self.assertRaises(Refused):self.check(run="123457")

    def test_expired_replay_refused(self):
        with self.assertRaises(Refused):self.check(now=self.now+601)

    def test_future_issued_refused(self):
        with self.assertRaises(Refused):self.check(now=self.now-60)

    def test_source_sha_substitution_refused(self):
        with self.assertRaises(Refused):self.check(sha="c"*40)

    def test_public_key_substitution_refused(self):
        with self.assertRaises(Refused):self.check(key=b"K"*400)

    def test_ciphertext_substitution_refused(self):
        with self.assertRaises(Refused):self.check(cipher=b"F"*384)

    def test_tool_scope_expansion_refused(self):
        bad=copy.deepcopy(self.msg)
        bad["allowed_tools"].append("shell.exec")
        with self.assertRaises(Refused):self.check(msg=bad)

    def test_public_endpoint_substitution_refused(self):
        bad={**self.msg,"endpoint":"https://attacker.example.invalid"}
        with self.assertRaises(Refused):self.check(msg=bad)

    def test_unknown_privileged_field_refused(self):
        bad={**self.msg,"plaintext_bearer":"leak"}
        with self.assertRaises(Refused):self.check(msg=bad)

    def test_noninteger_expiration_refused(self):
        bad={**self.msg,"expires_unix":str(self.msg["expires_unix"])}
        with self.assertRaises(Refused):self.check(msg=bad)

    def test_extended_expiration_refused(self):
        bad={**self.msg,"expires_unix":self.now+3600}
        with self.assertRaises(Refused):self.check(msg=bad)

    def test_duplicate_json_key_refused(self):
        with self.assertRaises(Refused):
            json.loads('{"endpoint":"one","endpoint":"two"}',
                object_pairs_hook=no_duplicate_keys)

    def test_non_cloudflare_domain_build_refused(self):
        with self.assertRaises(Refused):
            build("https://127.0.0.1", "a"*24,
                self.key,self.cipher,self.run,self.repo,self.sha,self.now)

    def test_wrong_repository_build_refused(self):
        with self.assertRaises(Refused):
            build(self.msg["endpoint"],"a"*24,
                self.key,self.cipher,self.run,"unknown/repo",self.sha,self.now)

if __name__=="__main__":
    unittest.main()
