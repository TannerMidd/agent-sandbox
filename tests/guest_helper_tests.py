import hashlib
import json
import os
import pathlib
import subprocess
import sys
import tempfile
import unittest
import zipfile

HELPER = pathlib.Path(__file__).parents[1] / "guest" / "guest_helper.py"


class GuestHelperTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = pathlib.Path(self.temp.name) / "work"
        self.root.mkdir()
        self.requests = pathlib.Path(self.temp.name) / "requests"
        self.requests.mkdir()
        self.env = dict(os.environ, AGENT_SANDBOX_TEST_WORK=str(self.root), AGENT_SANDBOX_TEST_REQUESTS=str(self.requests), PYTHONUTF8="1")

    def tearDown(self):
        self.temp.cleanup()

    def call(self, **request):
        payload = {"v": 1, "id": "00000000-0000-0000-0000-000000000001", "op": "list", "rootId": "work", "relativePath": [], **request}
        result = subprocess.run([sys.executable, str(HELPER)], input=json.dumps(payload), text=True, capture_output=True, env=self.env, check=False)
        return json.loads(result.stdout)

    def test_spaces_unicode_and_newlines_round_trip(self):
        response = self.call(op="writeText", relativePath=["space Ω.txt"], content="one\r\ntwo\n")
        self.assertTrue(response["ok"])
        read = self.call(op="readText", relativePath=["space Ω.txt"])
        self.assertEqual("one\r\ntwo\n", read["content"])

    def test_request_file_transport_is_bounded_and_removed(self):
        request_file = self.requests / "request.json"
        request_file.write_text(json.dumps({"v": 1, "id": "00000000-0000-0000-0000-000000000001", "op": "list", "rootId": "work", "relativePath": []}), encoding="utf-8")
        result = subprocess.run([sys.executable, str(HELPER), "--request-file", str(request_file)], text=True, capture_output=True, env=self.env, check=False)
        self.assertTrue(json.loads(result.stdout)["ok"])
        self.assertFalse(request_file.exists())

    def test_dotdot_and_separators_are_rejected(self):
        for value in ("..", "a/b", "a\\b", ""):
            with self.subTest(value=value):
                self.assertEqual("INVALID_PATH", self.call(relativePath=[value])["error"]["code"])

    def test_symlink_parent_is_rejected(self):
        target = self.root / "real"; target.mkdir()
        try: (self.root / "link").symlink_to(target, target_is_directory=True)
        except OSError: self.skipTest("Symlinks are not available")
        self.assertEqual("SYMLINK_PARENT", self.call(relativePath=["link", "file"])["error"]["code"])

    def test_listing_cursor_detects_change(self):
        for number in range(3): (self.root / f"{number}.txt").touch()
        first = self.call(pageSize=1)
        (self.root / "changed.txt").touch()
        second = self.call(pageSize=1, cursor=first["nextCursor"])
        self.assertEqual("LISTING_CHANGED", second["error"]["code"])

    def test_trash_and_restore(self):
        (self.root / "recover.txt").write_text("safe", encoding="utf-8")
        trashed = self.call(op="trash", relativePath=["recover.txt"])
        trash_id = json.loads(trashed["content"])["id"]
        restored = self.call(op="restore", relativePath=[trash_id])
        self.assertTrue(restored["ok"])
        self.assertEqual("safe", (self.root / "recover.txt").read_text(encoding="utf-8"))

    def test_large_directory_is_bounded_and_paginated(self):
        for number in range(230): (self.root / f"item-{number:03}.txt").touch()
        first = self.call(pageSize=200)
        self.assertEqual(200, len(first["entries"]))
        self.assertNotIn(".agent-sandbox", {item["name"] for item in first["entries"]})
        self.assertIsNotNone(first["nextCursor"])
        second = self.call(pageSize=200, cursor=first["nextCursor"])
        self.assertEqual(30, len(second["entries"]))

    def test_leading_dash_is_a_component_not_an_option(self):
        created = self.call(op="writeText", relativePath=["--danger"], content="data")
        self.assertTrue(created["ok"])
        self.assertEqual("data", self.call(op="readText", relativePath=["--danger"])["content"])

    def test_reconcile_does_not_remove_active_staging(self):
        active = self.root / ".agent-sandbox" / "staging" / ("c" * 32)
        active.mkdir(parents=True)
        (active / "data").write_text("active", encoding="utf-8")
        response = self.call(content="reconcile")
        self.assertTrue(response["ok"])
        self.assertTrue(active.exists())

    def test_overwrite_move_commits_through_replacement_journal(self):
        (self.root / "source.txt").write_text("new", encoding="utf-8")
        (self.root / "destination.txt").write_text("old", encoding="utf-8")
        response = self.call(op="move", relativePath=["source.txt"], destinationPath=["destination.txt"], conflict="overwrite")
        self.assertTrue(response["ok"])
        self.assertEqual("new", (self.root / "destination.txt").read_text(encoding="utf-8"))
        replacements = self.root / ".agent-sandbox" / "staging" / "replacements"
        self.assertEqual([], list(replacements.iterdir()))

    def test_interrupted_overwrite_transaction_finishes_on_next_request(self):
        control = self.root / ".agent-sandbox"
        transaction = control / "staging" / "replacements" / ("d" * 32)
        transaction.mkdir(parents=True)
        (transaction / "new").write_text("new", encoding="utf-8")
        (transaction / "backup").write_text("old", encoding="utf-8")
        (transaction / "transaction.json").write_text(json.dumps({
            "source": ["stage.txt"], "destination": ["destination.txt"], "phase": "backedUp", "copy": False
        }), encoding="utf-8")
        response = self.call()
        self.assertTrue(response["ok"])
        self.assertEqual("new", (self.root / "destination.txt").read_text(encoding="utf-8"))
        self.assertFalse(transaction.exists())

    def test_interrupted_copy_never_promotes_partial_content(self):
        destination = self.root / "destination.txt"
        destination.write_text("original", encoding="utf-8")
        transaction = self.root / ".agent-sandbox" / "staging" / "replacements" / ("e" * 32)
        transaction.mkdir(parents=True)
        (transaction / "new").write_text("partial", encoding="utf-8")
        (transaction / "transaction.json").write_text(json.dumps({
            "source": ["source.txt"], "destination": [destination.name], "phase": "preparing", "copy": True
        }), encoding="utf-8")
        response = self.call()
        self.assertTrue(response["ok"])
        self.assertEqual("original", destination.read_text(encoding="utf-8"))
        self.assertFalse(transaction.exists())

    def test_download_staging_is_immutable_and_digest_verified(self):
        source = self.root / "project.txt"
        source.write_text("original Ω", encoding="utf-8")
        inspected = self.call(op="download", relativePath=[source.name])["entries"][0]
        job = "a" * 32
        staged = self.call(
            op="stageDownload",
            relativePath=[source.name],
            destinationPath=[".agent-sandbox", "staging", "downloads", job, source.name],
            expected={"kind": inspected["kind"], "size": inspected["size"], "mtimeNs": inspected["mtimeNs"], "mode": inspected["mode"]},
        )
        self.assertTrue(staged["ok"])
        expected = hashlib.sha256(b"agent-sandbox-file-v1\0" + "original Ω".encode()).hexdigest()
        self.assertEqual(expected, staged["content"])
        source.write_text("changed", encoding="utf-8")
        self.assertEqual("original Ω", (self.root / ".agent-sandbox" / "staging" / "downloads" / job / source.name).read_text(encoding="utf-8"))

    def test_download_staging_rejects_changed_source(self):
        source = self.root / "changed.bin"
        source.write_bytes(b"first")
        inspected = self.call(op="download", relativePath=[source.name])["entries"][0]
        source.write_bytes(b"other!")
        staged = self.call(
            op="stageDownload",
            relativePath=[source.name],
            destinationPath=[".agent-sandbox", "staging", "downloads", "b" * 32, source.name],
            expected={"kind": inspected["kind"], "size": inspected["size"], "mtimeNs": inspected["mtimeNs"], "mode": inspected["mode"]},
        )
        self.assertEqual("SOURCE_CHANGED", staged["error"]["code"])

    def test_changed_text_source_is_rejected(self):
        path = self.root / "conflict.txt"
        path.write_text("first", encoding="utf-8")
        read = self.call(op="readText", relativePath=["conflict.txt"])
        entry = read["entries"][0]
        path.write_text("changed outside", encoding="utf-8")
        written = self.call(op="writeText", relativePath=["conflict.txt"], content="editor", expected={
            "kind": entry["kind"], "size": entry["size"], "mtimeNs": entry["mtimeNs"], "mode": entry["mode"]})
        self.assertEqual("SOURCE_CHANGED", written["error"]["code"])

    def test_archive_traversal_is_rejected(self):
        archive = self.root / "unsafe.zip"
        with zipfile.ZipFile(archive, "w") as item: item.writestr("../escape.txt", "no")
        response = self.call(op="extract", relativePath=["unsafe.zip"], destinationPath=["out"])
        self.assertEqual("ARCHIVE_TRAVERSAL", response["error"]["code"])
        self.assertFalse((self.root.parent / "escape.txt").exists())

    def test_windows_reserved_name_blocks_directory_download(self):
        folder = self.root / "project"; folder.mkdir(); (folder / "CON.txt").touch()
        response = self.call(op="download", relativePath=["project"])
        self.assertEqual("WINDOWS_NAME", response["error"]["code"])

    @unittest.skipUnless(os.name != "nt", "Case-distinct names require a case-sensitive test filesystem")
    def test_windows_case_collision_blocks_directory_download(self):
        folder = self.root / "project"; folder.mkdir(); (folder / "Readme").touch(); (folder / "README").touch()
        response = self.call(op="download", relativePath=["project"])
        self.assertEqual("WINDOWS_CASE_COLLISION", response["error"]["code"])

    @unittest.skipUnless(hasattr(os, "mkfifo"), "FIFO creation is not available")
    def test_special_file_is_rejected(self):
        fifo = self.root / "pipe"
        try: os.mkfifo(fifo)
        except OSError: self.skipTest("FIFO creation is not permitted")
        response = self.call(op="download", relativePath=["pipe"])
        self.assertEqual("UNSUPPORTED_TYPE", response["error"]["code"])


if __name__ == "__main__":
    unittest.main()
