# Project memory contract

Canonical artifacts belong with the project, so another checkout/server can restore them. User discovery links are convenience pointers; they must resolve to canonical files and must not silently overwrite an existing agent/skill.

| Artifact | Purpose |
| --- | --- |
| `AGENTS.md` | Short startup routing and project invariants; preserve existing instructions |
| `.codex/agents/NAME.toml` | Specialist name, description and developer instructions; inherit model/permissions unless explicitly requested otherwise |
| `.agents/skills/SLUG/SKILL.md` | Precise discovery description, startup and maintenance workflow |
| `.agent/MEMORY.md` | Compact current handoff: intent, what works, last evidence, pending work, next action |
| `.agent/STATE.json` | Machine-readable checkpoint, validation references, uncertainty and pending items |
| `.agent/KNOWLEDGE.md` | Detailed architecture/contracts/decisions that influence future work |
| `.agent/CODEMAP.md` | Feature -> file/symbol -> relevant tests/guide mapping |
| `.agent/HISTORY.md` | Factual chronological checkpoints; distinguish requests from verified actions |
| `.agent/source-index.json` | Git-tracked safe file names, modes, exact byte lengths and SHA-256 at a checkpoint |
| `scripts/agent-memory.py` | Offline byte inventory, stale-file detection and compact briefing |

Do not put the entire repository into the startup prompt. The source index records identity and gives paths; the agent still reads relevant code. Avoid duplicated stale technical manuals: link maintained project guides and record only the non-obvious decisions needed to resume.

STATE.json should include `schema_version`, `project`, `checkpoint`, `validation`, `pending`, `unknown` and `last_session`. Give a source commit/time for historical observations. A memory-only commit after a tested implementation does not make that old CI evidence the latest CI. Append new factual checkpoints; keep older evidence associated with the version it actually tested.

Record user-visible intent and material decisions, plus commands that were run and their observed results. If early conversational details are missing, mark that gap. Do not claim server access, deployments, provider sends, paid account configuration or registrations based on plans or a user's reported screen state alone.

Never persist credential values, user message bodies, session cookies, private API responses, connection strings with passwords, certificates or raw configuration/log/history dumps. Source inventory is metadata only, covers safe Git-tracked files and excludes private paths and the memory itself. It must not follow filesystem symlinks outside the repository. `check` is read-only and should fail on changed/new/deleted tracked source; it must not revert files or silently refresh the baseline.

At task completion update affected memory, pending work and validation, then snapshot the intended source. Do not run live writes to validate memory. Changes to notes/instructions alone need document/schema/helper validation, not repeated application tests or production rollouts.

On another machine, clone/copy the project and install its profiles/skills into that machine's discovery locations. A missing symlink target is a setup problem, not evidence that the project or agent never existed. Explain whether the agent is installed locally versus only packaged in the repository.
