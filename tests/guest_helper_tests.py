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
