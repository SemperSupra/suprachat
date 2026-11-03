import hashlib
import io
import json
import unittest
import zipfile
from audit import audit_one, AuditFailure

def build_zip(name, payload):
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", compression=zipfile.ZIP_DEFLATED) as z:
        z.writestr(name, json.dumps(payload, sort_keys=True))
    return buffer.getvalue()

class EvidenceAuditTests(unittest.TestCase):
    def setUp(self):
        self.obj = {
            "schema": "oec-cross-witness-cycle/v1",
            "credentials_exported": False,
            "authenticated_chatgpt_ui": False,
            "hidden_chatgpt_model_access": False,
            "source_class": "CONTROLLED_SYNTHETIC_ONLY",
            "experiment_id": "e"+"a"*24,
        }
        self.zip = build_zip("receipt.json", self.obj)
        self.entry = {
            "name": "cross-witness",
            "run_id": 41708165067,
            "artifact_id": 13267945369,
            "head_sha": "f3e400aa768a0a87402b4ff92f0656fb1819ee79",
            "archive_sha256": hashlib.sha256(self.zip).hexdigest(),
            "receipt_schema": "oec-cross-witness-cycle/v1",
        }
    def test_valid_synthetic_receipt(self):
        record = audit_one(self.entry, self.zip)
        self.assertEqual(record["privacy"], "PASS")
        self.assertEqual(record["receipt_bytes"], len(json.dumps(self.obj, sort_keys=True)))
    def test_archive_digest_substitution(self):
        changed = build_zip("receipt.json", {**self.obj, "experiment_id": "forged"})
        with self.assertRaises(AuditFailure):
            audit_one(self.entry, changed)
    def test_forbidden_secret(self):
        obj = {**self.obj, "authorization": "Bearer pretend"}
        zip_data = build_zip("receipt.json", obj)
        entry = {**self.entry, "archive_sha256": hashlib.sha256(zip_data).hexdigest()}
        with self.assertRaises(AuditFailure):
            audit_one(entry, zip_data)
    def test_forbidden_auth_claim(self):
        obj = {**self.obj, "authenticated_chatgpt_ui": True}
        zip_data = build_zip("receipt.json", obj)
        entry = {**self.entry, "archive_sha256": hashlib.sha256(zip_data).hexdigest()}
        with self.assertRaises(AuditFailure):
            audit_one(entry, zip_data)
    def test_zip_path_traversal(self):
        zip_data = build_zip("../receipt.json", self.obj)
        entry = {**self.entry, "archive_sha256": hashlib.sha256(zip_data).hexdigest()}
        with self.assertRaises(AuditFailure):
            audit_one(entry, zip_data)
    def test_unknown_archive_file(self):
        zip_data = build_zip("private.json", self.obj)
        entry = {**self.entry, "archive_sha256": hashlib.sha256(zip_data).hexdigest()}
        with self.assertRaises(AuditFailure):
            audit_one(entry, zip_data)

if __name__ == "__main__":
    unittest.main()
