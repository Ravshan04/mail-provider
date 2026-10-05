---
name: clarvio-mail
description: Resume or maintain Clarvio Mail, clarvio-email and its Clarvio Agent MCP connector using saved project decisions, source maps, validation and deployment state. Use for this platform's code, integrations, operational questions and handoffs.
---

# Clarvio Mail specialist

Locate the Mail checkout. This skill normally lives in its `.agents/skills/clarvio-mail/`; follow symlinks to find the repository. Rocky's current path is `/home/rocky/Abstract-IT-Group/clarvio-mail`. Another machine may use either private mirror in another directory.

Read the repository's `AGENTS.md`, `.agent/MEMORY.md` and `.agent/STATE.json` first. These identify what exists, the latest verified checkpoint and unfinished work. They do not prove current server/process/CI state.

- For a code change, inspect `git status --short`, run `python3 scripts/agent-memory.py check` and read the relevant entries in `.agent/CODEMAP.md` plus the actual implementation. Current source takes precedence over the saved index.
- For design, auth, provider or integration decisions, read the applicable section of `.agent/KNOWLEDGE.md` and linked `docs/` guide. For why a decision was made or what the user previously requested, use `.agent/HISTORY.md`; do not reconstruct missing history as fact.
- For operations, follow `docs/local-development.md`, `docs/deployment.md` or `docs/mcp.md`. Mail's public UI is distinct from the private MCP service. Agent's internal API port is 8000; 8710 is the host mapping.
- Preserve request-local MCP keys and Agent's write approval annotations; ownership/scopes/CSRF; stable send idempotency; file resources; verified Mail-to-Agent webhook signatures and mailbox-to-connection bindings. Never automatically retry unknown sends or partial drafts.
- Continue within the current user's authorization. A saved planned rollout is not permission to deploy or send real emails. Never save credential values/configuration dumps/email bodies. Respect the latest requested push target without force-pushing user work. Project-agent/memory changes are explicitly personal `origin` only; do not sync them to `organization` unless the user later requests it.

After substantial work, update `.agent/MEMORY.md`, the affected code/decision map, `.agent/HISTORY.md` and `.agent/STATE.json` with observed results and unresolved work. Refresh the source index after intended new source is tracked. Record older tests/CI as historical until rerun or rechecked. Use `$project-memory` to create the same arrangement for another project.
