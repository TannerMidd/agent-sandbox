#!/usr/bin/env python3
"""Versioned, daemonless file protocol for Agent Sandbox. Python stdlib only."""

from __future__ import annotations

import base64
import hashlib
import json
import os
import pathlib
import shutil
import stat
import sys
import tarfile
import time
import uuid
import zipfile

VERSION = 1
WORK_ROOT = pathlib.Path(os.environ.get("AGENT_SANDBOX_TEST_WORK", "/home/ubuntu/work"))
ROOTS = {"work": WORK_ROOT, "system": pathlib.Path("/")}
READ_ONLY = {"list", "stat", "search", "download", "readText"}
OPS = READ_ONLY | {"upload", "mkdir", "createFile", "rename", "copy", "move", "trash", "restore", "purge", "writeText", "chmod", "archive", "extract"}
MAX_TEXT = 5 * 1024 * 1024
MAX_ENTRIES = 10_000
MAX_EXPANDED = 2 * 1024 * 1024 * 1024
WINDOWS_RESERVED = {"CON", "PRN", "AUX", "NUL", *(f"COM{i}" for i in range(1, 10)), *(f"LPT{i}" for i in range(1, 10))}
CONTROL = WORK_ROOT / ".agent-sandbox"
TRASH = CONTROL / "trash"
STAGING = CONTROL / "staging"
REQUESTS = pathlib.Path(os.environ.get("AGENT_SANDBOX_TEST_REQUESTS", "/home/ubuntu/.local/lib/agent-sandbox/requests"))
MAX_REQUEST = 8 * 1024 * 1024


class ProtocolError(Exception):
    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code


def components(value) -> list[str]:
    if not isinstance(value, list):
        raise ProtocolError("INVALID_PATH", "Path must be a component array.")
    for part in value:
        if not isinstance(part, str) or not part or part in (".", "..") or "\0" in part or "/" in part or "\\" in part:
            raise ProtocolError("INVALID_PATH", "Path contains an invalid component.")
    return value


def resolve(root_id: str, parts: list[str], allow_missing_leaf: bool = False) -> pathlib.Path:
    if root_id not in ROOTS:
        raise ProtocolError("UNKNOWN_ROOT", "Unknown root identifier.")
    root = ROOTS[root_id].resolve(strict=True)
    current = root
    for index, part in enumerate(components(parts)):
        candidate = current / part
        is_leaf = index == len(parts) - 1
        try:
            info = candidate.lstat()
        except FileNotFoundError:
            if allow_missing_leaf and is_leaf:
                return candidate
            raise ProtocolError("NOT_FOUND", "The requested path does not exist.")
        if stat.S_ISLNK(info.st_mode):
            if not is_leaf:
                raise ProtocolError("SYMLINK_PARENT", "Symlink traversal is not allowed.")
            return candidate
        if not is_leaf and not stat.S_ISDIR(info.st_mode):
            raise ProtocolError("NOT_DIRECTORY", "A parent component is not a directory.")
        current = candidate
    return current


def kind(info: os.stat_result) -> str:
    mode = info.st_mode
    if stat.S_ISREG(mode): return "file"
    if stat.S_ISDIR(mode): return "directory"
    if stat.S_ISLNK(mode): return "symlink"
    return "special"


def entry(path: pathlib.Path) -> dict:
    info = path.lstat()
    target = os.readlink(path) if stat.S_ISLNK(info.st_mode) else None
    return {"name": path.name, "kind": kind(info), "size": info.st_size, "mtimeNs": info.st_mtime_ns,
            "mode": stat.S_IMODE(info.st_mode), "uid": info.st_uid, "gid": info.st_gid, "linkTarget": target}


def ensure_regular(path: pathlib.Path, directory_ok: bool = False):
    info = path.lstat()
    accepted = stat.S_ISREG(info.st_mode) or (directory_ok and stat.S_ISDIR(info.st_mode))
    if not accepted:
        raise ProtocolError("UNSUPPORTED_TYPE", "Only regular files and directories are supported.")


def expectation(path: pathlib.Path, expected):
    if expected is None: return
    info = path.lstat()
    actual = (kind(info), info.st_size, info.st_mtime_ns, stat.S_IMODE(info.st_mode))
    wanted = (expected.get("kind"), expected.get("size"), expected.get("mtimeNs"), expected.get("mode"))
    if actual != wanted:
        raise ProtocolError("SOURCE_CHANGED", "The source changed since it was inspected.")


def revision(paths: list[pathlib.Path]) -> str:
    digest = hashlib.sha256()
    for path in paths:
        info = path.lstat()
        digest.update(path.name.encode("utf-8", "surrogateescape"))
        digest.update(f"\0{info.st_mode}\0{info.st_size}\0{info.st_mtime_ns}\n".encode())
    return digest.hexdigest()


def cursor_encode(rev: str, offset: int) -> str:
    raw = json.dumps({"revision": rev, "offset": offset}, separators=(",", ":")).encode()
    return base64.urlsafe_b64encode(raw).decode().rstrip("=")


def cursor_decode(value: str | None) -> tuple[str | None, int]:
    if not value: return None, 0
    try:
        raw = base64.urlsafe_b64decode(value + "=" * (-len(value) % 4))
        item = json.loads(raw)
        return str(item["revision"]), int(item["offset"])
    except Exception as error:
        raise ProtocolError("INVALID_CURSOR", "The listing cursor is invalid.") from error


def conflict_target(path: pathlib.Path, policy: str) -> pathlib.Path:
    if not path.exists() and not path.is_symlink(): return path
    if policy == "fail": raise ProtocolError("CONFLICT", "The destination already exists.")
    if policy == "overwrite":
        ensure_regular(path, directory_ok=True)
        if path.is_dir(): shutil.rmtree(path)
        else: path.unlink()
        return path
    if policy == "rename":
        stem, suffix = path.stem, path.suffix
        for number in range(1, 10_000):
            candidate = path.with_name(f"{stem} ({number}){suffix}")
            if not candidate.exists() and not candidate.is_symlink(): return candidate
    raise ProtocolError("INVALID_CONFLICT", "Unknown conflict policy or no available name.")


def atomic_text(path: pathlib.Path, content: str, expected):
    encoded = content.encode("utf-8")
    if len(encoded) > MAX_TEXT: raise ProtocolError("TEXT_TOO_LARGE", "Text editing is limited to 5 MiB.")
    if path.exists():
        ensure_regular(path)
        expectation(path, expected)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f".{path.name}.{uuid.uuid4().hex}.tmp")
    try:
        with temporary.open("xb") as stream:
            stream.write(encoded)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def safe_copy(source: pathlib.Path, destination: pathlib.Path):
    ensure_regular(source, directory_ok=True)
    if source.is_dir():
        for child in source.rglob("*"):
            if child.is_symlink() or not (child.is_file() or child.is_dir()):
                raise ProtocolError("UNSUPPORTED_TYPE", "Directories containing links or special files cannot be copied.")
        shutil.copytree(source, destination)
    else:
        shutil.copy2(source, destination)


def validate_windows_tree(path: pathlib.Path) -> None:
    """Reject names that cannot round-trip onto a case-insensitive Windows volume."""
    directories = [path] if path.is_dir() else []
    if path.is_dir(): directories.extend(child for child in path.rglob("*") if child.is_dir())
    for directory in directories:
        folded: set[str] = set()
        for child in directory.iterdir():
            name = child.name
            stem = name.split(".", 1)[0].upper()
            if (not name or name[-1] in " ." or any(char in '<>:"/\\|?*' or ord(char) < 32 for char in name) or stem in WINDOWS_RESERVED):
                raise ProtocolError("WINDOWS_NAME", f"'{name}' cannot be represented safely on Windows.")
            key = name.casefold()
            if key in folded: raise ProtocolError("WINDOWS_CASE_COLLISION", "The directory contains names that differ only by case.")
            folded.add(key)


def handle(request: dict) -> dict:
    if request.get("v") != VERSION: raise ProtocolError("VERSION", "Unsupported protocol version.")
    operation = request.get("op")
    root_id = request.get("rootId", "work")
    if operation not in OPS: raise ProtocolError("OPERATION", "Unsupported operation.")
    if root_id == "system" and operation not in READ_ONLY: raise ProtocolError("READ_ONLY", "System browsing is read-only.")
    parts = components(request.get("relativePath", []))
    page_size = request.get("pageSize", 200)
    if not isinstance(page_size, int) or not 1 <= page_size <= 200: raise ProtocolError("PAGE_SIZE", "Page size must be 1 to 200.")
    path = WORK_ROOT if operation in {"restore", "purge"} else resolve(
        root_id, parts, allow_missing_leaf=operation in {"mkdir", "createFile", "writeText"})
    response = {"v": VERSION, "id": request.get("id"), "ok": True, "rootId": root_id,
                "relativePath": parts, "entries": [], "revision": None, "nextCursor": None,
                "unstable": False, "content": None, "warnings": [], "error": None}

    if operation in {"list", "search"}:
        ensure_regular(path, directory_ok=True)
        if not path.is_dir(): raise ProtocolError("NOT_DIRECTORY", "The requested path is not a directory.")
        query = str(request.get("content") or "").casefold()
        is_workspace_root = root_id == "work" and not parts
        items = sorted((p for p in path.iterdir() if not (is_workspace_root and p.name == CONTROL.name) and (operation == "list" or query in p.name.casefold())), key=lambda p: (not p.is_dir(), p.name.casefold(), p.name))
        rev = revision(items)
        expected_rev, offset = cursor_decode(request.get("cursor"))
        if expected_rev is not None and expected_rev != rev: raise ProtocolError("LISTING_CHANGED", "The directory changed; refresh the listing.")
        page = items[offset:offset + page_size]
        response["entries"] = [entry(item) for item in page]
        response["revision"] = rev
        if offset + len(page) < len(items): response["nextCursor"] = cursor_encode(rev, offset + len(page))
    elif operation in {"stat", "download"}:
        ensure_regular(path, directory_ok=True)
        if operation == "download" and path.is_dir(): validate_windows_tree(path)
        response["entries"] = [entry(path)]
    elif operation == "readText":
        ensure_regular(path)
        if path.stat().st_size > MAX_TEXT: raise ProtocolError("TEXT_TOO_LARGE", "Text editing is limited to 5 MiB.")
        try: response["content"] = path.read_bytes().decode("utf-8")
        except UnicodeDecodeError as error: raise ProtocolError("NOT_UTF8", "The file is not valid UTF-8 text.") from error
        response["entries"] = [entry(path)]
    elif operation in {"mkdir", "createFile"}:
        destination = conflict_target(path, request.get("conflict", "fail"))
        if operation == "mkdir": destination.mkdir()
        else: destination.touch(exist_ok=False)
        response["entries"] = [entry(destination)]
    elif operation == "writeText":
        atomic_text(path, request.get("content") or "", request.get("expected"))
        response["entries"] = [entry(path)]
    elif operation == "chmod":
        ensure_regular(path, directory_ok=True); expectation(path, request.get("expected"))
        mode = request.get("mode")
        if not isinstance(mode, int) or mode < 0 or mode > 0o777: raise ProtocolError("MODE", "Mode must be between 000 and 777.")
        if path.lstat().st_uid != os.getuid(): raise ProtocolError("OWNERSHIP", "Permissions can only be changed on user-owned items.")
        path.chmod(mode); response["entries"] = [entry(path)]
    elif operation in {"rename", "copy", "move"}:
        ensure_regular(path, directory_ok=True); expectation(path, request.get("expected"))
        destination_parts = components(request.get("destinationPath"))
        destination = resolve(root_id, destination_parts, allow_missing_leaf=True)
        destination = conflict_target(destination, request.get("conflict", "fail"))
        if operation == "copy": safe_copy(path, destination)
        else: os.replace(path, destination)
        response["relativePath"] = destination_parts; response["entries"] = [entry(destination)]
    elif operation == "trash":
        ensure_regular(path, directory_ok=True); expectation(path, request.get("expected"))
        TRASH.mkdir(parents=True, exist_ok=True)
        trash_id = uuid.uuid4().hex
        destination = TRASH / trash_id
        os.replace(path, destination)
        metadata = {"id": trash_id, "original": parts, "name": path.name, "deletedAt": int(time.time())}
        (TRASH / f"{trash_id}.json").write_text(json.dumps(metadata), encoding="utf-8")
        response["content"] = json.dumps(metadata, separators=(",", ":"))
    elif operation == "restore":
        trash_id = parts[-1] if parts else ""
        if not trash_id.isalnum(): raise ProtocolError("TRASH_ID", "Invalid trash identifier.")
        metadata_path = TRASH / f"{trash_id}.json"
        if not metadata_path.exists(): raise ProtocolError("NOT_FOUND", "Trash item not found.")
        metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
        destination = resolve("work", components(metadata["original"]), allow_missing_leaf=True)
        destination = conflict_target(destination, request.get("conflict", "fail"))
        os.replace(TRASH / trash_id, destination); metadata_path.unlink()
        response["relativePath"] = metadata["original"]; response["entries"] = [entry(destination)]
    elif operation == "purge":
        trash_id = parts[-1] if parts else ""
        target = TRASH / trash_id
        if target.is_dir(): shutil.rmtree(target)
        elif target.exists(): target.unlink()
        (TRASH / f"{trash_id}.json").unlink(missing_ok=True)
    elif operation == "archive":
        ensure_regular(path, directory_ok=True)
        destination_parts = components(request.get("destinationPath"))
        destination = conflict_target(resolve(root_id, destination_parts, allow_missing_leaf=True), request.get("conflict", "fail"))
        if destination.suffix.lower() == ".zip":
            with zipfile.ZipFile(destination, "x", zipfile.ZIP_DEFLATED) as archive:
                if path.is_dir():
                    for child in path.rglob("*"):
                        if child.is_symlink(): raise ProtocolError("ARCHIVE_SYMLINK", "Links cannot be archived.")
                        archive.write(child, child.relative_to(path.parent))
                else: archive.write(path, path.name)
        else:
            with tarfile.open(destination, "x:gz") as archive: archive.add(path, arcname=path.name, recursive=True, filter=lambda item: None if item.issym() or item.islnk() else item)
        response["entries"] = [entry(destination)]
    elif operation == "extract":
        ensure_regular(path)
        destination = resolve(root_id, components(request.get("destinationPath")), allow_missing_leaf=True)
        destination.mkdir(exist_ok=True)
        members = []
        if zipfile.is_zipfile(path):
            with zipfile.ZipFile(path) as archive:
                for item in archive.infolist(): members.append((item.filename, item.file_size, item.is_dir(), None, item))
                validate_archive(members)
                for name, _, is_dir, _, item in members:
                    target = archive_target(destination, name)
                    if is_dir: target.mkdir(parents=True, exist_ok=True)
                    else:
                        target.parent.mkdir(parents=True, exist_ok=True)
                        with archive.open(item) as source, target.open("xb") as output: shutil.copyfileobj(source, output)
        else:
            with tarfile.open(path, "r:*") as archive:
                for item in archive.getmembers(): members.append((item.name, item.size, item.isdir(), item, item))
                validate_archive(members)
                for name, _, is_dir, tar_item, _ in members:
                    target = archive_target(destination, name)
                    if is_dir: target.mkdir(parents=True, exist_ok=True)
                    else:
                        source = archive.extractfile(tar_item)
                        if source is None: raise ProtocolError("ARCHIVE_TYPE", "Unsupported archive entry.")
                        target.parent.mkdir(parents=True, exist_ok=True)
                        with source, target.open("xb") as output: shutil.copyfileobj(source, output)
    return response


def archive_target(destination: pathlib.Path, name: str) -> pathlib.Path:
    path = pathlib.PurePosixPath(name)
    if path.is_absolute() or any(part in ("", ".", "..") for part in path.parts): raise ProtocolError("ARCHIVE_TRAVERSAL", "Archive entry escapes the destination.")
    return destination.joinpath(*path.parts)


def validate_archive(members):
    if len(members) > MAX_ENTRIES: raise ProtocolError("ARCHIVE_ENTRIES", "Archive has too many entries.")
    if sum(item[1] for item in members) > MAX_EXPANDED: raise ProtocolError("ARCHIVE_SIZE", "Archive expands beyond the safety limit.")
    for name, _, _, tar_item, _ in members:
        archive_target(pathlib.Path("/tmp/safe"), name)
        if tar_item is not None and not (tar_item.isfile() or tar_item.isdir()): raise ProtocolError("ARCHIVE_TYPE", "Links and special archive entries are not allowed.")


def cleanup_staging(force: bool = False):
    if not STAGING.exists(): return
    cutoff = time.time() - 24 * 60 * 60
    for item in STAGING.iterdir():
        try:
            if force or item.lstat().st_mtime < cutoff:
                if item.is_dir(): shutil.rmtree(item)
                else: item.unlink()
        except OSError: pass


def read_request():
    if len(sys.argv) == 1:
        return json.load(sys.stdin)
    if len(sys.argv) != 3 or sys.argv[1] != "--request-file":
        raise ProtocolError("INVALID_TRANSPORT", "The guest request transport is invalid.")
    request_file = pathlib.Path(sys.argv[2])
    request_root = REQUESTS.resolve(strict=True)
    if request_file.parent.resolve(strict=True) != request_root or request_file.suffix != ".json":
        raise ProtocolError("INVALID_TRANSPORT", "The guest request file is outside the request directory.")
    info = request_file.lstat()
    if not stat.S_ISREG(info.st_mode) or info.st_size > MAX_REQUEST:
        raise ProtocolError("INVALID_TRANSPORT", "The guest request file is invalid or too large.")
    try:
        with request_file.open("r", encoding="utf-8") as stream:
            return json.load(stream)
    finally:
        request_file.unlink(missing_ok=True)


def main() -> int:
    request = {}
    try:
        CONTROL.mkdir(parents=True, exist_ok=True)
        request = read_request()
        cleanup_staging(force=request.get("op") == "list" and request.get("content") == "reconcile")
        response = handle(request)
    except ProtocolError as error:
        response = {"v": VERSION, "id": request.get("id"), "ok": False, "rootId": request.get("rootId", "work"),
                    "relativePath": request.get("relativePath", []), "entries": [], "revision": None, "nextCursor": None,
                    "unstable": False, "content": None, "warnings": [], "error": {"code": error.code, "message": str(error)}}
    except Exception:
        response = {"v": VERSION, "id": request.get("id"), "ok": False, "rootId": request.get("rootId", "work"),
                    "relativePath": request.get("relativePath", []), "entries": [], "revision": None, "nextCursor": None,
                    "unstable": False, "content": None, "warnings": [], "error": {"code": "INTERNAL", "message": "The guest helper could not complete the operation."}}
    json.dump(response, sys.stdout, ensure_ascii=False, separators=(",", ":")); sys.stdout.write("\n")
    return 0 if response["ok"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
