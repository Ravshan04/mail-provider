# Feature and implementation map

Use this map to locate relevant code, then read the current file. The SHA-256 index records exact tracked bytes independently; this map describes behavior at the saved implementation checkpoint. No line numbers are frozen because later edits move them.

| Question/change | Files and symbols | Checks/reference |
| --- | --- | --- |
| Boot/config/health/rate limits | `src/Clarvio.Mail/Program.cs`, `Core/Settings.cs` (`MailSettings.Validate`) | Security/Proxy/Documentation tests; deployment guide |
| Key/session auth, scopes and CSRF | `Auth/RequestIdentity.cs` (`RequestIdentity.Require`, `IdentityMiddleware.Invoke`) | `tests/Clarvio.Mail.Tests/SecurityTests.cs`, `DatabaseTests.cs`; API auth |
| OAuth, PKCE/state, sessions, connect vs login | `Auth/OAuthService.cs`, Google/Microsoft provider authorization/exchange/identity methods | Security/Database tests; README provider setup |
| Token secrecy/key rotation | `Core/TokenCipher.cs`, `Core/Settings.cs` (`Secrets`) | Security tests; architecture/deployment backup |
| Ownership and provider orchestration | `Services/MailService.cs` (`Owned`, `AccessToken`, send/draft methods) | Database tests; architecture |
| Models and provider surface | `Providers/Contracts.cs` (`IEmailProvider`, `MailMessage`, `SendMailRequest`, `SaveDraftRequest`, `ProviderRegistry`) | API and provider tests |
| Route/auth/scope changes | `Api/Endpoints.cs` | Documentation/Database tests; `docs/api.md` |
| OpenAPI/Swagger models/assets | `Api/ApiDocumentation.cs`, `DocumentationResponses.cs`, `Api/Swagger/index.html`, `clarvio-init.js`; embedded resources in csproj | Documentation tests + `tests/ui/swagger.spec.js`; 32 operations baseline |
| Gmail search/get/send/change polling | `Providers/GoogleProvider.cs` (`List`, `Get`, `Attachment`, `Send`, `Changes`) | Security/MailboxProvider tests; Gmail guide linked in README |
| Gmail labels/read/move/drafts | `Providers/GoogleMailbox.cs` (`Folders`, `SetRead`, `Move`, `GetDraft`, `SaveDraft`, `SendDraft`, `DeleteDraft`) | MailboxProvider tests; draft IDs/source labels |
| Microsoft search/get/send/change polling | `Providers/MicrosoftProvider.cs`, `ValidateCursor` | Security/MailboxProvider tests; Graph limits |
| Microsoft folders/drafts/attachments | `Providers/MicrosoftMailbox.cs`, `NeedsAttachmentWorkflow`, `AddAttachment`, `DraftBody`, `SaveDraft` | Large-file and retained-attachment tests |
| Outlook chunk PUT/security | `Providers/ProviderHttp.cs` (`UploadOutlookAttachment`, `Json`, `BoundedBytes`) and provider HTTP registration in Program | Microsoft ten-MiB/reply, invalid-host, unknown-range/failure tests; `docs/mcp.md` |
| Send/attachment validation/MIME | `Providers/MessageValidation.cs` (`Validate`, `SafeFilename`, `Mime`), `DraftAttachments` in GoogleMailbox | Security/MailboxProvider tests; 10 MiB files, 16 MiB envelope |
| Send idempotency/unknown handling | `Services/MailService.cs`, `Data/Entities.cs` (`SendOperation`), `Data/DbLocks.cs`, `Services/MailWorker.cs` stale operation cleanup | Database tests; `docs/api.md` outcomes |
| DB tables/indexes/migrations | `Data/Entities.cs`, `MailDb.cs`, `DbLocks.cs`, `Data/Migrations/` | Database tests; production migrate/restore smoke |
| Polling and durable deliveries | `Services/MailWorker.cs` | Database/Security tests; `docs/webhooks.md` |
| Webhook destination/signature security | `Services/WebhookTargets.cs` (`Validate`, `IsPublic`, `Connect`, `Sign`) | Security tests; public HTTPS allowlist |
| Browser mailbox/compose/key/trigger UI | `wwwroot/app.js`, `index.html`, `styles.css`, `mark.svg` | `tests/ui/mailbox.spec.js`, swagger.spec.js; no Node server in production |
| Local config and lifecycle | `scripts/configure-microsoft.py`, `configure-google.py`, `local.py`, `compose.local.yaml` | Local development guide; protected .local files |
| Production release/migrate/backup/restore | `scripts/production.py`, `Dockerfile`, `deploy/compose.yaml`, external proxy compose, Caddy/nginx examples, `.dockerignore` | `scripts/production-smoke.py`, proxy tests; `docs/deployment.md` |
| MCP tool catalog/manifest/health/skill | `connector/server.py` (`create_server`, `create_app`, `hints`), `connector/skills/clarvio-mail-operations/SKILL.md` | `connector/tests/test_tools.py`, `test_http.py`; `docs/mcp.md` |
| MCP inputs/file and recipient validation | `connector/models.py` (`Address`, `Attachment`, `Message.api_body`) | Nested base64/10MiB/invalid-message tests |
| MCP request isolation/error/redaction/output caps | `connector/client.py` (`MailClient.request`, `authorization`, `part`, `result`, `MailText`) | Every-tool, concurrent keys, timeout/no-retry, redaction/HTML/cap tests |
| MCP file downloads into Agent user files | `server.py` (`download_attachment`) + `BlobResourceContents` | HTTP/blob Docker smoke + filename/size tests |
| Mail -> Agent signed triggers | `connector/bridge.py` (`EventBridge.from_env`, `receive`), `deploy/compose.mcp-webhooks.yaml` | `connector/tests/test_bridge.py`; optional trigger guide |
| MCP Docker/private networks/dependency locks | `connector/Dockerfile`, `.dockerignore`, `pyproject.toml`, `uv.lock`, `requirements.lock`, `deploy/compose.mcp.yaml` | `scripts/mcp-smoke.py`, CI mcp job |
| Read-only actual-account MCP diagnosis | `connector/doctor.py` (`check`) | Private key prompt; no writes; exact account counts only |
| CI steps and pinned tools | `.github/workflows/ci.yml` | Jobs mcp/tests/production; GitHub latest results require recheck |
| Persistent development specialist | `AGENTS.md`, `.codex/agents/clarvio_mail.toml`, `.agents/skills/clarvio-mail/` | Codex local discovery + TOML/YAML validation |
| Future project agent creation | `.agents/skills/project-memory/SKILL.md`, `scripts/create_agent.py`, `references/memory-contract.md` inside that skill | Isolated scaffold/collision/helper checks; no network |
| Byte index/briefing | `scripts/agent-memory.py`, canonical copy in project-memory/scripts/agent_memory.py, `.agent/source-index.json` | Offline change/new/remove/symlink/private exclusion checks |

## Sibling Agent integration reference

These are read-only references unless a new task explicitly changes Agent:

- `../clarvio-agent/docs/connector-contract.md`, `examples/reference_connector/server.py`: stateless transport, files, hints, manifest, event shape.
- `../clarvio-agent/app/connectors/manifest.py`: manifest validation and credential-header mapping.
- `../clarvio-agent/app/connectors/client.py`: Streamable HTTP init/catalog/check_tool, skill fetch, bad-key classification and SSE cap.
- `../clarvio-agent/app/services/connectors.py`: registry/connections, encrypted vault, MCP server selection and injected connection ID.
- `../clarvio-agent/app/agent/approvals.py`: approval rules and write/read gating; inspect associated hooks when required.
- `../clarvio-agent/app/api/routers/connectors.py`: connector registration, checks and signed `/webhooks/connectors/{key}` ingestion.
- `../clarvio-agent/app/core/security.py`: Agent webhook HMAC verification.
- `../clarvio-agent/compose.yaml`: actual service aliases/networks and host8710 -> container8000 mapping.
- `../clarvio-agent-front`: admin connector registration and Connections UI, generated credential form; do not confuse it with Mail's static UI.
