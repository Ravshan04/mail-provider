# Factual project checkpoints

Dates use Asia/Samarkand. Git commits and recorded observed checks are evidence. Earlier user requests whose assistant actions are not available are marked as requests, not completed steps. Secret values and private account content are intentionally absent.

## Earlier requests and scope

The user asked for Debian VS Code setup/architecture detection and supplied an amd64 .deb file URL; asked for a professional developer/DevOps environment and storage checks; worked with the Clarvio Agent local frontend/backend/database; reported an invalid Python interpreter, localhost8711 reachability errors, a service-not-reachable UI and later a Next.js ready log. These messages do not establish current runtime health or every environment modification.

The user asked whether the ETA skill was merged on main/dev, to inspect the PR/check and how to remove a check. Their later instruction was **leave it alone**. Mail work must not alter that item.

The user then requested an independent email provider/application inspired by relay.clarvio.tech, which they reported unavailable. They selected Gmail and Microsoft, eventual use of the existing Clarvio server, and completing preparation before deployment. They chose a professional implementation from scratch. Azure sign-in/tenant/app/scopes/client credentials and Google project/Gmail API/consent/scopes setup were discussed. User-reported portal completion is not a substitute for future live validation; no credential values are preserved.

## 2026-10-05: independent Mail platform

- `763124f` 15:41:59 — initial Clarvio Mail Gmail/Microsoft platform implementation.
- `17c2fb6` 15:47:41 — local setup, HTTP API, tests and deployment documentation.
- `c7067ff` 16:18:33 — clarified SMTP/IMAP/OAuth and planned Yahoo support. Current providers use HTTP APIs; Yahoo/generic IMAP-SMTP was not implemented.
- `107fff8` 16:41:50 — bundled authenticated Swagger/OpenAPI. 32 routes documented; session CSRF and key-based Try it out; no auth persistence/CDN.
- `4eb3935` 16:54:50 — API/local/webhooks/deployment preparation guides.

User requested the personal private repo outside the organization, then an identical copy in `Abstract-IT-Group/clarvio-email`. At that implementation stage, they requested the same source in both mirrors; the later personal-only agent/memory instruction below overrides mirroring for that batch. They requested full README/developer/server instructions, including what another server operator must change.

## 2026-10-05: production tooling

- `62189e9` 17:30:57 — Docker/production Compose, bundled/external proxy modes, trusted forwarded headers, protected initialization, release/migrate, backup/restore, fresh-project/collision guards and isolated HTTPS production smoke. CI for backend/browser/production; no automatic deployment.
- Observed at that checkpoint: 51 backend and 10 browser tests passed; production container smoke passed; private mirror CI runs 37310076117 and 37310083047 succeeded.
- Real provider folder/draft reads were checked; controlled provider sends/mutations and actual server rollout remained pending. Local protected provider configuration exists, but its values are not memory.

## 2026-10-05: Clarvio Agent study and MCP implementation

User requested studying Clarvio Agent before creating Mail MCP; later explicitly approved implementing the plan.

- Studied connector contract/reference, manifest validation, encrypted per-user credential vault, live Streamable HTTP connection test, skill sync, approval broker/file hooks, signed event contract, UI registration and Docker networks. No code changes were made to Agent/frontend. User-owned `.vscode/` remained untouched.
- `ef74d39` 18:31:05 — separate stateless Mail MCP service, 17 annotated tools, header-scoped keys, operations skill, Docker/private-network Compose, nested file inputs/embedded downloads, signed optional event adapter, live doctor and docs/CI. Raised API/UI outgoing limits from 2 to 10 MiB and downloads from 5 to 10 MiB to meet Agent file contract; added safe Graph draft/upload-session handling.
- `edd4587` 18:32:15 — corrected webhook target to Agent's container port8000 (host mapping8710).
- Observed: 61 backend + 10 browser + 59 MCP tests passed. Both isolated Docker smokes passed; MCP smoke moved 10 MiB to/from a fake API, never a real provider. Exact Agent manifest/skills/connection client passed; invalid key rejected. Live doctor read one connected mailbox using a temporary accounts:read/mail:read key; that key was removed. No live mail writes were made.
- Both remotes matched `edd4587cb0d859293842e458676584c1cc5156e4`; personal run37317574456 and organization run37317579297 completed successfully (mcp/tests/production jobs).
- Actual production deploy, Agent registration/user connection, live event and controlled provider writes remained pending. The user then asked whether both repos were pushed; the answer was yes, with the verified equal commit and green CI.

## Current task: durable project agents

User requested an agent that understands the work so far and the same practice for future substantial tasks. Implemented a project-scoped `clarvio_mail` specialist profile plus `$clarvio-mail` skill; compact handoff, detailed knowledge/code map/factual history/structured evidence; offline byte index and stale checker; reusable `$project-memory` scaffold generator; and local standing instructions for future continuity. The persisted model context is file-based knowledge, not new model training or a running autonomous process.

Scaffold/discovery/helper checks and the final memory checkpoint are recorded in STATE.json after actual validation. Earlier application CI results remain associated with edd4587; later memory-only commits require separate evidence rather than relabeling those old runs.

## 2026-10-06: agent setup completed locally

- Installed both profile/skill discovery links and local global continuity instructions; existing caller model/permissions stay inherited.
- Eight isolated offline helper/factory tests passed. Both skill frontmatter/UI metadata and TOML profiles passed validation. Local Codex prompt introspection confirmed global/project instruction and both skill discovery; no named-agent model execution was claimed.
- The source index covers 96 safe tracked files with zero uncovered untracked/skipped source; byte comparison passed. Workflow actionlint and diff whitespace checks passed.
- Added an offline memory CI job, but this memory-only change has not been pushed or run on GitHub. No production deployment, model/API call or real mail write occurred. Application evidence remains historical at edd4587.

## 2026-10-06: personal-only publication requested

The user explicitly requested pushing the final project-agent/memory changes only to personal Ravshan04/clarvio-mail, not Abstract-IT-Group/clarvio-email. Read-only preflight confirmed both remote main heads at edd4587cb0d859293842e458676584c1cc5156e4 and the personal repo is private. Updated routing/profile/skill to preserve this intentional divergence. Publication results must be verified after pushing; no organization mutation is authorized.

- Publication observed: git push origin HEAD:refs/heads/main succeeded for 83cad432c8e4c748348f126a331cbd529be66eb5. Read-only remote checks confirmed personal main at that commit and organization main unchanged at edd4587cb0d859293842e458676584c1cc5156e4. The personal memory CI job in run37336062670 passed; full CI was in progress at this observation. A follow-up metadata checkpoint records these actual results; it does not claim its own latest CI has completed.
