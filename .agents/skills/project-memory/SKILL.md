---
name: project-memory
description: Create or update a persistent project specialist agent, scoped skill and verified handoff memory after substantial project work, or when asked to save task context for a later session. Reuse an existing project agent for incremental changes.
---

# Persistent project agents

The user wants each substantial project to have an agent that can resume from a short request. Create reusable knowledge and a specialist configuration; this does not train a model or create a continuously running process.

First locate the actual project and read its existing instructions/memory. Reuse an existing specialist for incremental work. A quick factual answer or minor edit does not justify a new agent. When no project directory exists, keep task knowledge in an explicitly chosen local directory; do not invent a repository or publish one just to store memory.

For a new project, run [scripts/create_agent.py](scripts/create_agent.py):

```bash
python3 /path/to/project-memory/scripts/create_agent.py \
  --project /absolute/project --name project_name \
  --description 'The concrete platform and tasks this specialist owns.' --install
```

The generator creates a TOML specialist, a discoverable skill, compact/deep memory scaffolds, source-inventory helper and project routing instructions. `--install` links only this project's new agent/skill into user discovery locations. It refuses to overwrite existing artifacts and has no network/publish/deploy effects. Inspect and fill the generated knowledge; scaffolding by itself is not a completed handoff. Use `--user-home` with an isolated directory for tests, never repurpose environment HOME.

Follow [references/memory-contract.md](references/memory-contract.md) when filling memory:

- Save intent, decisions, implementation locations, invariants, working commands, observed validation, unresolved work and the next action.
- Separate verified facts, historical evidence, plans, user requests and unknowns. Preserve the scope of user instructions; do not turn one project's exception into a rule for every project.
- Keep secrets and private content out. Record protected locations and setup procedures only. Never copy a complete session/configuration dump.
- Keep startup brief. Route deeper questions to a code map and focused references. The actual source remains authoritative; SHA-256/file-size inventory proves byte identity, not semantic understanding or current runtime health.
- After substantive work, refresh the affected knowledge and source index. Do not erase earlier evidence, stale-check warnings or pending live validation just to make the handoff look complete.

Validate the skill/profile and run the inventory helper against a temporary changed-file example when creating a new helper. Confirm discovery using Codex's local prompt/config introspection when available; do not claim model recall based only on file existence. Do not automatically start agents, deploy, publish or send external messages as part of memory maintenance.
