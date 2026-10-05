# Clarvio Mail: resume here

User: Rocky / Ravshanbek; prefers concise Uzbek, autonomous completion and no repeated confirmations. Goal: a professional independent email workspace/API connecting Gmail and Microsoft, integrated with Clarvio Agent through MCP. The user now explicitly wants maintained specialist agents for substantial projects, so tomorrow a short request restores the relevant context.

## What exists

- Mail platform: ASP.NET Core/.NET 10, static browser UI, PostgreSQL 18, delegated Gmail API/Microsoft Graph OAuth. It connects existing mailboxes; SMTP/IMAP/Yahoo and mail-domain hosting are not implemented.
- Separate Python stateless Streamable HTTP MCP connector: 17 tools, scoped request-local API keys, Agent approval annotations, an operations skill, embedded file downloads and optional signed inbox-event bridge. Source and developer/deploy guide are in `connector/` and `docs/mcp.md`.
- Docker/Compose, HTTPS proxy modes, migration/release/backup/restore scripts, OpenAPI/Swagger (32 Mail API operations), and CI. CD is manual deployment tooling; there is no automatic server deployment.
- Persistent specialist: `.codex/agents/clarvio_mail.toml`, `.agents/skills/clarvio-mail/SKILL.md`, this `.agent/` knowledge, and safe tracked-file SHA-256/byte inventory. `$project-memory` creates the same arrangement for new projects. Agents inherit the caller's model/permissions; these files do not train a model or start a background service.

## Agent setup checkpoint

2026-10-06: both specialist profiles and skills are installed in local Codex discovery. Eight offline memory/factory tests, two skill/profile metadata checks, local instruction/skill discovery, workflow lint and a 96-file byte comparison passed. At agent setup, these new files were local and their CI job had not run. The user subsequently requested publication only to personal origin. Agent implementation 83cad43 was pushed there and remote-verified; organization remained at edd4587. Its memory CI job passed; full CI was still running at that observation. Check newer remote/CI state rather than inferring it from this checkpoint. Named-agent model execution and current Mail runtime were not tested by this task.

## Current push target

Project-agent/memory changes: **personal `origin` only** (`Ravshan04/clarvio-mail`). The user explicitly excluded `organization`; preserve that intentional divergence until a later request changes it. The application/MCP baseline below was mirrored historically.

## Last verified implementation checkpoint

Source commit `edd4587cb0d859293842e458676584c1cc5156e4`, 2026-10-05 Asia/Samarkand. Both private `main` branches matched:

- `origin`: https://github.com/Ravshan04/clarvio-mail
- `organization`: https://github.com/Abstract-IT-Group/clarvio-email

Evidence: **61 backend + 10 browser + 59 MCP tests = 130**, production Docker smoke and 10 MiB MCP Docker smoke passed. Actual Clarvio Agent manifest/skill/connection client and one live mailbox were checked read-only. The temporary read-only key was removed. CI passed on both repositories at that commit; `.agent/STATE.json` records run links. These are historical results, not proof of the latest CI or current process health.

## Do not rediscover or break

- Writes must remain behind Agent approval; API keys arrive per request, never in connector environment/global state. `X-Connection-Id` alone does not authenticate Mail.
- Browser-only management includes keys, mailbox disconnect and webhook subscriptions. Preserve workspace ownership, scopes, key revocation/expiration, PKCE/state, CSRF and purpose-bound encryption.
- Outgoing files: 10 files, 10 MiB combined; downloads: 10 MiB; API/MCP request bodies: 16 MiB. Outlook large files use draft/upload-session/send, not one oversized sendMail JSON request.
- Sends keep one stable idempotency key. `accepted` is acceptance, not delivery; never auto-resend `unknown` with a new key. Draft/upload writes can partially succeed.
- Agent host API port is **8710**, container port **8000**. Frontend port is 8711. Mail local API is 8740, local DB 5548, MCP default 8741. Inspect actual runtime before diagnosing a connection failure.
- Protected files: `.local/settings.json`, production secret files/certificate, and optional MCP binding/Agent signing-secret mounts. Never copy their contents into notes, Git or tool output.
- Do not alter the unrelated ETA PR/checks or the sibling Agent's untracked `.vscode/`. The old relay.clarvio.tech availability is the user's historical report, not verified current infrastructure.

## Pending and next action

The actual Clarvio server deployment, production connector registration/enablement, per-user Mail API key connection, live incoming trigger and controlled provider writes are **not completed**. Production server address/access/domain/credentials are not saved as facts here. Use existing protected configuration and authorized read-only discovery; ask only for genuinely missing information.

Next, when requested: deploy Mail following `docs/deployment.md`; launch `deploy/compose.mcp.yaml` on the real Mail/Agent networks; register `http://clarvio-mail-mcp:8741` as kind `clarvio`; connect the user's scoped Mail key. Configure the optional public HTTPS webhook route, matching mailbox/connection bindings and Agent's infrastructure signing secret only if triggers are requested. Then perform read-only doctor checks and separately authorized live writes.

## Load only what is needed

- `.agent/STATE.json`: evidence, pending work and uncertain state.
- `.agent/CODEMAP.md`: feature -> files/symbols/checks.
- `.agent/KNOWLEDGE.md`: non-obvious contracts, decisions and operations.
- `.agent/HISTORY.md`: factual checkpoints and the user's earlier requests.
- `python3 scripts/agent-memory.py check`: changed/new/deleted tracked bytes and untracked source; inspect changes instead of reverting them. `brief` prints this handoff; `snapshot` deliberately refreshes the baseline.

Maintain these files after substantial work. Relevant source wins over stale memory; hashes prove byte identity, not complete semantic understanding of every file or live health.
