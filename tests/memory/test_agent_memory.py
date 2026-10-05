"""Offline observable checks for safe inventory and portable agent scaffolding."""

import hashlib
import json
import os
import subprocess
import sys
import tempfile
import tomllib
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
HELPER = ROOT / "scripts/agent-memory.py"
FACTORY = ROOT / ".agents/skills/project-memory/scripts/create_agent.py"


class MemoryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="clarvio-memory-test-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "project"
        self.root.mkdir()
        self.user = Path(self.temp.name) / "user"
        self.git("init", "-q")
        self.git("config", "user.name", "Memory test")
        self.git("config", "user.email", "memory-test@example.invalid")
        (self.root / "main.py").write_text("print('initial')\n")
        self.git("add", "main.py")
        self.git("commit", "-qm", "Initial fake source")

    def git(self, *args):
        return subprocess.run(["git", "-C", str(self.root), *args], check=True, capture_output=True)

    def run_script(self, script, *args, expected=0):
        result = subprocess.run([sys.executable, str(script), *args], cwd=self.root, capture_output=True, text=True)
        self.assertEqual(expected, result.returncode, result.stderr)
        return result

    def helper(self, command, expected=0):
        return json.loads(self.run_script(HELPER, command, "--repo", str(self.root), expected=expected).stdout)

    def scaffold(self, *extra, expected=0):
        return self.run_script(FACTORY, "--project", str(self.root), "--name", "fake_platform", "--description",
                               "Maintain the fake platform.", "--user-home", str(self.user), *extra, expected=expected)

    def test_exact_byte_snapshot_detects_changes_without_refreshing(self):
        self.helper("snapshot")
        index = self.root / ".agent/source-index.json"
        before = index.read_bytes()
        self.assertTrue(self.helper("check")["matches"])
        (self.root / "main.py").write_bytes(b"print('changed')\r\n")
        changed = self.helper("check", expected=1)
        self.assertEqual(["main.py"], changed["changed"])
        self.assertEqual(before, index.read_bytes())
        self.assertEqual(b"print('changed')\r\n", (self.root / "main.py").read_bytes())

    def test_new_removed_and_untracked_source_are_reported(self):
        self.helper("snapshot")
        (self.root / "added.py").write_text("# fake source\n")
        self.assertEqual(["added.py"], self.helper("check", expected=1)["untracked_source_not_in_snapshot"])
        self.git("add", "added.py")
        self.git("rm", "-q", "main.py")
        report = self.helper("check", expected=1)
        self.assertEqual(["added.py"], report["new_tracked"])
        self.assertEqual(["main.py"], report["removed_tracked"])

    def test_private_files_are_not_inventoried_and_symlink_target_is_not_read(self):
        sentinel = "fake-private-bytes-not-for-memory"
        for name in (".env", "secrets/secret.txt", ".local/settings.json", "private.pfx"):
            file = self.root / name
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_text(sentinel)
        outside = Path(self.temp.name) / "outside.txt"
        outside.write_text(sentinel)
        (self.root / "link.txt").symlink_to(outside)
        self.git("add", "-f", ".")
        self.helper("snapshot")
        document = json.loads((self.root / ".agent/source-index.json").read_text())
        self.assertEqual({"main.py", "link.txt"}, set(document["files"]))
        link = document["files"]["link.txt"]
        self.assertEqual("symlink", link["kind"])
        self.assertEqual(hashlib.sha256(os.fsencode(str(outside))).hexdigest(), link["sha256"])
        self.assertNotIn(sentinel, json.dumps(document))

    def test_scaffold_installs_profile_and_skill_preserves_instructions_and_refuses_overwrite(self):
        agents = self.root / "AGENTS.md"
        agents.write_text("# Existing project rule\nKeep the original rules.\n")
        report = json.loads(self.scaffold("--install").stdout)
        self.assertFalse(report["ready"])
        self.assertTrue(agents.read_text().startswith("# Existing project rule\n"))
        profile = tomllib.loads((self.user / ".codex/agents/fake_platform.toml").read_text())
        self.assertEqual("fake_platform", profile["name"])
        self.assertIn(".agent/MEMORY.md", profile["developer_instructions"])
        self.assertTrue((self.user / ".agents/skills/fake-platform/SKILL.md").is_file())
        before = agents.read_bytes()
        self.scaffold("--install", expected=2)
        self.assertEqual(before, agents.read_bytes())

    def test_discovery_collision_does_not_write_project_artifacts(self):
        existing = self.user / ".codex/agents/fake_platform.toml"
        existing.parent.mkdir(parents=True)
        existing.write_text("Do not replace me")
        self.scaffold("--install", expected=2)
        self.assertFalse((self.root / ".agent").exists())
        self.assertEqual("Do not replace me", existing.read_text())

    def test_install_existing_is_idempotent_and_does_not_change_project(self):
        self.scaffold()
        before = (self.root / "AGENTS.md").read_bytes()
        for _ in range(2):
            report = json.loads(self.run_script(FACTORY, "--project", str(self.root), "--name", "fake_platform",
                                                "--user-home", str(self.user), "--install-only").stdout)
            self.assertFalse(report["project_files_changed"])
        self.assertEqual(before, (self.root / "AGENTS.md").read_bytes())

    def test_external_instructions_symlink_is_rejected_before_writing(self):
        outside = Path(self.temp.name) / "outside-instructions.md"
        outside.write_text("Unrelated instructions")
        (self.root / "AGENTS.md").symlink_to(outside)
        self.scaffold(expected=2)
        self.assertFalse((self.root / ".agent").exists())
        self.assertEqual("Unrelated instructions", outside.read_text())

    def test_packaged_helper_matches_canonical_template_and_profiles_parse(self):
        self.assertEqual(HELPER.read_bytes(), FACTORY.with_name("agent_memory.py").read_bytes())
        for file in (ROOT / ".codex/agents").glob("*.toml"):
            profile = tomllib.loads(file.read_text())
            self.assertTrue(profile["name"] and profile["description"] and profile["developer_instructions"])


if __name__ == "__main__":
    unittest.main()
