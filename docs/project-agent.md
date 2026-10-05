# Persistent Clarvio Mail development agent

[Documentation index](README.md) · [Current handoff](../.agent/MEMORY.md)

This repository packages a Codex specialist named `clarvio_mail` and an invocable `$clarvio-mail` skill, with maintained project knowledge. It is the development assistant for this codebase; the Mail operations skill served to the end-user Clarvio Agent over MCP is a separate artifact.

The specialist restores decisions, implementation maps, verified checkpoints and unfinished work from files. It does not train a new model, keep every source byte in the model's context, or start an autonomous background process. Source hashes identify exact tracked bytes; the agent still reads the relevant implementation and verifies changed/runtime state. This design keeps startup fast while preserving detailed knowledge.

## Use tomorrow

In a new Codex session in this checkout:

```text
$clarvio-mail kechagi ishimizdan davom et, hozir serverga deploy tayyorgarligini tekshir.
```

Or explicitly request the named specialist:

```text
clarvio_mail agentini chaqir va MCP ulanish muammosini tekshir.
```

Root `AGENTS.md` routes ordinary project work to the same handoff. On Rocky's machine, user-level discovery links also make the skill and specialist available outside this checkout. Codex normally detects skill changes automatically; restart the session/app if a new entry does not appear. See [official skill discovery](https://learn.chatgpt.com/docs/build-skills) and [custom agent files](https://learn.chatgpt.com/docs/agent-configuration/subagents).

The setup inherits the caller's model and execution permissions and supplies project knowledge. A saved planned deployment does not authorize one. The latest user instruction and current observed source/runtime state take precedence over history.

## What the specialist remembers

| File | Contents |
| --- | --- |
| `AGENTS.md` | Short routing and essential operational rules |
| `.agent/MEMORY.md` | Compact handoff, tested baseline, pending work and next action |
| `.agent/STATE.json` | Structured evidence, run links, service configuration and unknowns |
| `.agent/KNOWLEDGE.md` | Auth/provider/MCP/event/deployment decisions and contracts |
| `.agent/CODEMAP.md` | Feature-to-file/symbol/check map, including sibling Agent references |
| `.agent/HISTORY.md` | Factual checkpoints; early user requests distinguished from verified actions |
| `.agent/source-index.json` | Safe tracked-file byte counts, modes and SHA-256 snapshot |
| `.codex/agents/clarvio_mail.toml` | Named specialist profile |
| `.agents/skills/clarvio-mail/` | Discoverable project workflow and UI metadata |
| `.agents/skills/project-memory/` | Reusable generator/workflow for another project |

Credentials, configuration dumps, certificates, private mailbox content and full chat/log transcripts are not stored. Protected locations and procedures are enough to resume. The index does not follow tracked symlinks into external files, and deliberately excludes private/generated files and agent metadata.

## Check memory against source

From the repository root:

```bash
python3 scripts/agent-memory.py brief
python3 scripts/agent-memory.py check
```

`brief` prints the compact handoff. `check` is read-only: it reports changed/new/deleted tracked files, untracked source and whether HEAD changed. Changed source causes a nonzero exit; it never refreshes the index or reverts user changes. A metadata-only commit may change HEAD while source bytes still match. The index records a working-tree snapshot and the HEAD at creation, not a promise that every hash is from a clean commit.

After completing a task, update affected knowledge and actual validation, track intended new source files as part of the authorized change, and deliberately refresh:

```bash
python3 scripts/agent-memory.py snapshot
python3 -m unittest discover -s tests/memory -v
```

Snapshotting only safe Git-tracked source avoids harvesting untracked secrets. Untracked source is reported as uncovered so it cannot be mistaken for a complete inventory. For a non-Git task, maintain the narrative handoff without inventing a Git repository/byte baseline.

## Install on another machine

Canonical profiles/skills/knowledge are portable files in the project. On a machine with Codex, clone/copy the project, then install discovery links without rewriting its content:

```bash
python3 .agents/skills/project-memory/scripts/create_agent.py \
  --project "$PWD" --name clarvio_mail --install-only
python3 .agents/skills/project-memory/scripts/create_agent.py \
  --project "$PWD" --name project_memory --install-only
```

This links the project skills under `~/.agents/skills/` and profiles under `~/.codex/agents/`. Existing different targets are refused rather than overwritten. Match name collisions deliberately. Keep the checkout in place; moving/deleting it breaks absolute convenience links until reinstalled. Instructions prefer the current checkout/resolved skill path over the original machine's fallback directory.

Rocky's standing continuity preferences are in the local `~/.codex/AGENTS.md`. This user file is not silently copied to another person's home or published with credentials; another user can choose their own preferences. In this repository the portable root instructions already require handoff maintenance.

## Create an agent for another substantial project

Ask:

```text
$project-memory shu yangi loyiha uchun ham agent yarat va qilgan ishimizni xotirasiga yoz.
```

The generic workflow locates the real project, preserves existing instructions and creates a narrow specialist only for a separate substantial project. For an incremental feature it updates the existing specialist instead of generating dozens of overlapping agents.

Its offline scaffold generator is available directly:

```bash
python3 /path/to/clarvio-mail/.agents/skills/project-memory/scripts/create_agent.py \
  --project /absolute/other-project \
  --name other_project \
  --description 'Concrete platform and responsibilities of this project.' \
  --install
```

The output explicitly starts `ready: false`. Fill verified intent, architecture, decisions, code map, completed behavior, observed checks, unknowns and the next step before reporting readiness. It refuses existing artifacts; updating them requires inspection. The workflow's memory contract lists expected files and source/evidence rules. It makes no model/API/network calls, Git commits, pushes, deployments or real email sends.

## Validation scope

The memory helper/factory has eight isolated offline behavioral checks: exact byte changes; new/remove/untracked files; private exclusions and symlink target safety; scaffold/profile installation; preservation/no overwrite; user discovery collisions; idempotent install-only; and external instruction-file rejection. TOML/YAML metadata is validated and local Codex prompt introspection confirms instruction/skill discovery. Those checks do not prove that a model has semantically inspected every line or that production Mail is running.

The earlier application baseline (130 tests, both Docker smokes, read-only live doctor and both CI runs at edd4587) remains historical evidence. Memory-only work must not relabel old CI as current or run live mailbox writes to test knowledge files.
