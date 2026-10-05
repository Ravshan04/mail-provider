#!/usr/bin/env python3
"""Offline project continuity: exact safe tracked bytes, stale checks and a brief.

No network, credential/configuration inspection, git mutations or model calls.
"""

import argparse
import hashlib
import json
import os
import subprocess
from datetime import datetime, timezone
from pathlib import Path

PRIVATE_PARTS = {".git", ".local", ".venv", "node_modules", "bin", "obj", "secrets", "backups", "__pycache__", "TestResults"}
METADATA_PARTS = {".agent", ".agents", ".codex"}
PRIVATE_SUFFIXES = {".pfx", ".pem", ".key", ".p12", ".p8"}


def git(root, *args):
    return subprocess.run(["git", "-C", str(root), *args], check=True, capture_output=True).stdout


def excluded(path):
    parts = Path(path).parts
    if set(parts) & (PRIVATE_PARTS | METADATA_PARTS) or path == "AGENTS.md":
        return True
    name = Path(path).name
    return Path(path).suffix.lower() in PRIVATE_SUFFIXES or (name.startswith(".env") and not name.endswith(".example"))


def inventory(root):
    records = {}
    skipped = []
    for entry in git(root, "ls-files", "--stage", "-z").split(b"\0"):
        if not entry:
            continue
        metadata, name = entry.split(b"\t", 1)
        mode, _, stage = metadata.decode().split()
        path = os.fsdecode(name)
        if stage != "0":
            raise ValueError("Resolve the index's merge conflicts before taking a byte snapshot.")
        if excluded(path):
            continue
        file = root / path
        if mode == "160000":
            skipped.append({"path": path, "reason": "submodule; inspect its own repository"})
            continue
        # Resolve parents only: a tracked leaf symlink is hashed as its link text,
        # never as the contents of its target.
        if not file.parent.resolve().is_relative_to(root):
            skipped.append({"path": path, "reason": "parent resolves outside repository"})
            continue
        try:
            if file.is_symlink():
                data = os.fsencode(os.readlink(file))
                kind = "symlink"
            else:
                if not file.resolve().is_relative_to(root):
                    skipped.append({"path": path, "reason": "resolves outside repository"})
                    continue
                data = file.read_bytes()
                kind = "file"
        except FileNotFoundError:
            records[path] = {"missing": True}
            continue
        records[path] = {"sha256": hashlib.sha256(data).hexdigest(), "bytes": len(data), "kind": kind,
                         "git_mode": mode, "executable": kind == "file" and bool(file.stat().st_mode & 0o111)}
    return records, skipped


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["snapshot", "check", "brief"])
    parser.add_argument("--repo", type=Path, default=Path.cwd())
    parser.add_argument("--index", default=".agent/source-index.json")
    args = parser.parse_args()
    try:
        root = Path(os.fsdecode(git(args.repo, "rev-parse", "--show-toplevel")).strip()).resolve()
        if args.command == "brief":
            print((root / ".agent/MEMORY.md").read_text())
            return 0
        output = (root / args.index).resolve()
        if not output.is_relative_to(root / ".agent"):
            raise ValueError("The inventory output must stay in this repository's .agent directory.")
        records, skipped = inventory(root)
        head = git(root, "rev-parse", "HEAD").decode().strip()
        untracked = [os.fsdecode(p) for p in git(root, "ls-files", "--others", "--exclude-standard", "-z").split(b"\0")
                     if p and not excluded(os.fsdecode(p))]
        if args.command == "snapshot":
            if any(record.get("missing") for record in records.values()):
                raise ValueError("Tracked source is missing. Resolve intended deletions in the index before snapshotting.")
            output.parent.mkdir(parents=True, exist_ok=True)
            document = {"schema_version": 1, "project": root.name, "generated_at": datetime.now(timezone.utc).isoformat(),
                        "git_head_at_snapshot": head, "content_source": "working_tree_safe_git_tracked_files",
                        "excluded": "agent metadata, private/generated directories, credential files and non-example .env files",
                        "file_count": len(records), "total_bytes": sum(r["bytes"] for r in records.values()),
                        "untracked_source_not_in_snapshot": untracked, "skipped": skipped, "files": records}
            temporary = output.with_suffix(".json.tmp")
            temporary.write_text(json.dumps(document, ensure_ascii=True, indent=2) + "\n")
            temporary.replace(output)
            print(json.dumps({"snapshot": str(output.relative_to(root)), "files": len(records), "untracked_source": len(untracked), "skipped": len(skipped)}))
            return 0 if not untracked and not skipped else 1
        baseline = json.loads(output.read_text())
        if baseline.get("schema_version") != 1:
            raise ValueError("Unsupported inventory schema.")
        old = baseline["files"]
        changed = sorted(p for p in records.keys() & old.keys() if records[p] != old[p])
        new = sorted(records.keys() - old.keys())
        removed = sorted(old.keys() - records.keys())
        report = {"matches": not (changed or new or removed or untracked or skipped), "changed": changed, "new_tracked": new,
                  "removed_tracked": removed, "untracked_source_not_in_snapshot": untracked, "skipped": skipped,
                  "snapshot_head": baseline["git_head_at_snapshot"], "current_head": head,
                  "head_changed": baseline["git_head_at_snapshot"] != head, "files": len(records)}
        print(json.dumps(report, ensure_ascii=True, indent=2))
        return 0 if report["matches"] else 1
    except (OSError, ValueError, KeyError, subprocess.CalledProcessError):
        parser.exit(2, "Cannot read project memory/index. Check the repository, inventory schema and tracked files. No source was changed.\n")


if __name__ == "__main__":
    raise SystemExit(main())
