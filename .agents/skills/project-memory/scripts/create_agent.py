#!/usr/bin/env python3
"""Create a portable project specialist scaffold and optional user discovery links.

No network, Git commits, overwrite, background agents or deployments.
"""

import argparse
import json
import os
import re
import shutil
from pathlib import Path


def quoted(text):
    return json.dumps(text, ensure_ascii=False)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--name", required=True, help="Agent name: lowercase letters, digits and underscores")
    parser.add_argument("--description")
    parser.add_argument("--install", action="store_true")
    parser.add_argument("--install-only", action="store_true", help="Install an existing profile/skill without changing project files")
    parser.add_argument("--user-home", type=Path, default=Path.home(), help="Discovery home; use an isolated path when testing")
    args = parser.parse_args()
    root = args.project.resolve()
    if not root.is_dir() or not re.fullmatch(r"[a-z][a-z0-9_]{1,62}", args.name):
        parser.error("Use an existing project directory and a 2–63 character agent name.")
    if not args.install_only and (not args.description or not 1 <= len(args.description.strip()) <= 800 or any(ord(c) < 32 for c in args.description)):
        parser.error("Use a nonempty one-line description of at most 800 characters.")
    slug = args.name.replace("_", "-")
    skill = root / ".agents/skills" / slug
    profile = root / ".codex/agents" / (args.name + ".toml")
    links = [(args.user_home / ".agents/skills" / slug, skill),
             (args.user_home / ".codex/agents" / (args.name + ".toml"), profile)] if args.install or args.install_only else []
    for link, target in links:
        if os.path.lexists(link) and (not link.is_symlink() or link.resolve() != target):
            parser.error("A user discovery name already exists. Choose another name; it will not be replaced.")
    if args.install_only:
        if not profile.is_file() or not (skill / "SKILL.md").is_file():
            parser.error("Existing profile and skill were not found. Create and fill them first.")
        for link, target in links:
            if not os.path.lexists(link):
                link.parent.mkdir(parents=True, exist_ok=True)
                link.symlink_to(target, target_is_directory=target.is_dir())
        print(json.dumps({"agent": args.name, "installed": True, "project_files_changed": False}))
        return
    docs = root / ".agent"
    helper = root / "scripts/agent-memory.py"
    destinations = [skill, profile, docs, helper]
    for destination in destinations:
        if destination.exists() or destination.is_symlink():
            parser.error("Existing agent/memory/helper found. Update it deliberately; the generator does not overwrite.")
        if not destination.parent.resolve().is_relative_to(root):
            parser.error("Agent artifacts must remain inside the project.")
    agents = root / "AGENTS.md"
    if agents.exists() and (not agents.is_file() or not agents.resolve().is_relative_to(root)):
        parser.error("Project AGENTS.md is not a regular in-project instructions file.")
    routing = f"""\n## {slug} project continuity\n\nRead `.agent/MEMORY.md` and `.agent/STATE.json` before resuming substantial work. The scoped skill is `.agents/skills/{slug}/SKILL.md`, and the specialist profile is `.codex/agents/{args.name}.toml`. Use `.agent/CODEMAP.md` and `.agent/KNOWLEDGE.md` for affected areas, then inspect actual code. Saved observations do not prove current runtime/CI state. After substantial work, update memory, history, validation and pending work; refresh the source index. Keep secrets out and stay within the current user's authorization.\n"""
    instructions = f"""Work as the {slug} project specialist within the current user's request. Locate the project from its instructions and scoped skill; this machine's original checkout is {root}. Read AGENTS.md, .agent/MEMORY.md and .agent/STATE.json first. Route affected code to .agent/CODEMAP.md; read deeper contracts in .agent/KNOWLEDGE.md and factual checkpoints in .agent/HISTORY.md when needed. Current source/runtime evidence overrides stale notes. For implementation run scripts/agent-memory.py check; changed bytes require inspection, never an automatic revert. Complete authorized work with proportional checks, then maintain memory and source inventory. Store credential locations rather than values. Memory does not authorize deployment, external messages, publishing or destructive actions. Inherit the parent model and execution permissions."""
    files = {
        profile: f"name = {quoted(args.name)}\ndescription = {quoted(args.description)}\ndeveloper_instructions = {quoted(instructions)}\n",
        skill / "SKILL.md": f"---\nname: {slug}\ndescription: {quoted(args.description + ' Resume using this project’s saved context and keep its knowledge current.')}\n---\n\n# {slug} specialist\n\nFollow this skill's canonical path to its project root; this machine's original path is `{root}`. Read `AGENTS.md`, `.agent/MEMORY.md` and `.agent/STATE.json` first. Use `.agent/CODEMAP.md` to locate the affected source and `.agent/KNOWLEDGE.md`/`.agent/HISTORY.md` for contracts and prior decisions. Verify current code/state before relying on old facts; for implementation run `python3 scripts/agent-memory.py check`. Update the affected knowledge, actual validation, history, pending items and byte snapshot after substantial work. Never persist secrets or use memory as authorization for deployment, publishing, external messages or destructive work.\n",
        skill / "agents/openai.yaml": f"interface:\n  display_name: {quoted(slug.replace('-', ' ').title() + ' Agent')}\n  short_description: {quoted('Resume project work from verified saved context')}\n  default_prompt: {quoted('Use $' + slug + ' to resume this project from saved context and complete my request.')}\n",
        docs / "MEMORY.md": f"# {slug}: current handoff\n\nPurpose: {args.description}\n\nEvidence has not yet been recorded. Fill this handoff from the current task's actual code, decisions and observed checks before reporting the agent ready.\n\nTrack what works, unresolved work, the last verified checkpoint and the next concrete action. Deep references: KNOWLEDGE.md, CODEMAP.md, HISTORY.md and STATE.json.\n",
        docs / "KNOWLEDGE.md": "# Project knowledge\n\nRecord verified architecture, decisions, operational invariants and relevant maintained guides here. No facts have been recorded yet.\n",
        docs / "CODEMAP.md": "# Implementation map\n\nMap requested capabilities to actual files/symbols and relevant checks. Read the source; no implementation map has been recorded yet.\n",
        docs / "HISTORY.md": "# Factual project checkpoints\n\nThe agent scaffold was created. Implementation, testing and deployment evidence have not yet been recorded. Append actual observations rather than invented history.\n",
        docs / "STATE.json": json.dumps({"schema_version": 1, "project": slug, "checkpoint": None, "validation": [],
                                        "pending": ["Fill verified task context and validate the handoff before reporting readiness."],
                                        "unknown": ["Implementation, runtime and deployment state have not been inspected."],
                                        "last_session": {"kind": "scaffold", "ready": False}}, indent=2) + "\n",
    }
    for path, content in files.items():
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content)
    helper.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(Path(__file__).with_name("agent_memory.py"), helper)
    with agents.open("a") as stream:
        stream.write(routing)
    for link, target in links:
        if not os.path.lexists(link):
            link.parent.mkdir(parents=True, exist_ok=True)
            link.symlink_to(target, target_is_directory=target.is_dir())
    print(json.dumps({"project": str(root), "agent": args.name, "skill": slug, "installed": bool(args.install),
                      "ready": False, "next": "Fill verified memory and validate; snapshot safe tracked source if this is a Git project."}))


if __name__ == "__main__":
    main()
