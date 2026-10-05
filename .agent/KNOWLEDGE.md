# Clarvio Mail detailed knowledge

Checkpoint: implementation through `edd4587` on 2026-10-05. This reference supplements maintained guides; source and current evidence are authoritative. Contents: intent/history boundaries; platform/auth/storage; mailbox contracts; MCP/Agent; webhooks; local/production operations; evidence/remaining work.

## Intent and decisions

The user chose Gmail **and** Microsoft for the first version and eventual deployment on the existing Clarvio server. They requested a professional implementation from scratch, a separate private personal GitHub repository, a second identical organization mirror, full API documentation/Swagger, developer/server instructions, and finally an MCP integration after studying Clarvio Agent. For the later project-agent/memory batch, the user explicitly selected personal origin only; organization must remain at its prior implementation checkpoint unless later authorized.

The earlier user questions covered Debian/VS Code, CPU architecture, local Clarvio Agent frontend/backend/database, a Python interpreter warning, an unavailable frontend/backend, an ETA skill/PR/check, Azure/Google app setup, scopes and SMTP/IMAP/Yahoo. These are historical requests. The available conversation does not prove every early environment/PR action happened. The user explicitly said to leave the ETA item alone. Do not reopen it when doing Mail work.

Do not mistake the former relay.clarvio.tech service for this implementation. The user's report that its server was down is historical and was not used as a current server-access record. No production server address, password, OAuth client secret or mailbox contents belong in memory.

## Platform and packages

The Mail application is .NET 10, ASP.NET Core static UI/API/OAuth/worker in one service. PostgreSQL 18 stores service state. Build settings enable nullable types, implicit usings, warnings-as-errors and locked NuGet restore. `Clarvio.Mail.csproj` pins EF/OpenAPI 10.0.12, Npgsql EF 10.0.3, MimeKit 4.18.1 and Swagger UI 10.2.3. Docker uses SDK 10.0.401 and ASP.NET runtime 10.0.12. UI tests use Playwright 1.63.0; Node 22 is needed for those tests, not the production UI.

The production UI is `wwwroot` HTML/JavaScript/CSS, not the sibling Next.js Clarvio Agent frontend. Providers retain actual mailbox contents; Postgres is not a full mailbox archive. MimeKit builds MIME; that does not mean SMTP or IMAP is used. Yahoo/generic IMAP-SMTP support remains planned.

## Identity, authentication and isolation

OAuth authorization codes use PKCE, one-use state and browser binding. Provider identities use provider/subject identity rather than auto-merging equal email strings. Connecting another mailbox attaches it to the signed-in workspace; separately signing in through another identity can create a different workspace.

`RequestIdentity` distinguishes a browser session from an API key. The `cm_session` cookie is HttpOnly; session mutations under `/api` require `X-CSRF-Token`. API key values begin `cm_sk_`, are stored as SHA-256 hashes, expire and can be revoked. Scopes: `accounts:read`, `mail:read`, `mail:write`, `mail:send`. Ownership queries bind mailbox/key/operation/subscription rows to the caller's UserId. The presence of a key does not authorize session-only management.

Browser-only routes manage keys, mailbox disconnect, webhook subscriptions, delivery status and activity. MCP must not expose those routes with a key or fall back to a cookie. Provider access/refresh tokens and Mail webhook signing secrets use versioned AES-256-GCM with purpose binding. Store encryption keys with DB backups; retain old key versions during rotation.

Tables: users, identities, mailboxes, OAuth attempts, sessions, API keys, send operations, audit events, webhook subscriptions, incoming events and deliveries. Database coordination locks serialize provider token refresh/send/poll/delivery paths where required. Send intents and incoming/delivery events have uniqueness constraints. Rate limits partition by identity/IP; defaults API 200/minute, sends 20, auth 20.

## HTTP and mailbox contracts

The maintained `docs/api.md` and bundled Swagger describe 32 API operations, request/response models and auth. Swagger assets use the application CSP, no CDN dependency and no persisted bearer authorization. Swagger/OpenAPI are enabled in Development, disabled by default in Production unless explicitly configured.

Public paths include health, providers and OAuth entry/callback. Key-capable routes include mailboxes; folders; list/get messages; attachment download; read/move/trash; get/create/update/delete drafts; send/new/reply/draft; and get send operation. Admin/session routes remain browser-only. Typical lists use folder `inbox`, query up to 300 characters and limits 1–50 (API default 25, MCP default 10). Opaque cursors bind the caller/mailbox/filter/limit and expire after 30 minutes.

Messages carry IDs, thread IDs, subject, From/To/CC/BCC, body text/HTML, read status, preview, attachment metadata and optional threading/draft fields. Use folders' actual IDs/canMove flags. Standard folders include Inbox/Sent/Drafts/Trash/Archive/All; custom IDs have `custom:` prefixes.

Gmail labels are not exclusive folders: specify a source custom label to remove it, preserve unrelated labels, and use untrash when restoring. Gmail's stable draft ID differs from its changing message ID. Graph moves may return a different ID. Regular deletion moves to Trash; Gmail draft deletion is permanent.

Draft saves wrap the send body in `message`. Omit `keepAttachmentIds` to retain all existing files; `[]` removes all; selected IDs retain those files; new attachments are added. `replyMessageId` is for a new reply draft. Multipart provider changes can partially succeed, so reread before retrying. Browser reply-all explicitly selects recipients; forwarding uses a composed message and manually selected attachments, not a separate forward API endpoint.

Send validation: at most 50 total recipients, 500-character subject without controls, text/HTML bodies up to 100,000 characters, at most 10 attachments and 10 MiB combined decoded files. Downloads support 10 MiB per file. Both API and MCP accept 16 MiB request envelopes; bounded provider JSON responses support 16 MiB. Filenames cannot contain slashes/control characters; base64 and MIME types are checked.

Large Microsoft attachment requests (combined decoded content over 2 MiB) build a draft first. Files below 3,000,000 bytes use the normal attachment POST; larger files create a Graph upload session, then sequential 2 MiB PUT ranges to the pre-authenticated `https://outlook.office.com/api/...` URL. No Graph bearer token/cookies go to that URL; reject unexpected hosts/ports/user-info/fragments, redirects and unexpected ranges. Final upload must return 201; intermediate confirmation is 200 with the expected next range. Provider URL logging is removed so upload query tokens are not logged. Only then send the draft once. Interruption can leave a partial draft; do not automatically recreate/upload/send it. See Microsoft's source linked in `docs/mcp.md` for future changes.

## Send outcomes

All API/MCP sends require `Idempotency-Key` (16–128 characters); MCP validates a printable ASCII key. Mail stores the intent before contacting a provider. Uniqueness is workspace-level, fingerprinting includes mailbox/request/reply or mailbox/draft. Same key/different payload returns conflict. Same key/same send retrieves its operation rather than submitting twice.

Operations are `sending`, `accepted`, `failed` or `unknown`. Accepted is provider acceptance, not proof of final delivery. Graph may return no message ID. An uncertain timeout/provider result must not cause an automatic resend/new key; reconcile a known operation and Sent mail. The worker marks stale sending operations unknown. Send outcome persistence continues independently of browser disconnect where possible. Neither provider API submission nor draft multipart writes are a transaction with Postgres.

## Clarvio Agent contract and studied source

Sibling repositories: `/home/rocky/Abstract-IT-Group/clarvio-agent` (Python/FastAPI/SQLAlchemy/Postgres/Redis/Claude Agent SDK) and `clarvio-agent-front` (Next.js). Agent Python requires >=3.14; its existing `.venv` uses uv-managed Python. Agent's source is outside Mail; implementation made no changes there. Its untracked `.vscode/` is user-owned.

Read `clarvio-agent/docs/connector-contract.md`, `examples/reference_connector/server.py`, `app/connectors/manifest.py`, `app/connectors/client.py`, `app/services/connectors.py`, `app/agent/approvals.py` and the connector router when changing integration. Registry kind is `clarvio`; custom agents/skills in this repository are Codex development artifacts, distinct from the end-user Mail operations skill served over MCP.

Agent registers the base URL, fetches `/manifest` and skill bodies, enables a connector for an organization, generates the user's credential form, encrypts submitted credentials and injects them per request. Connection testing must call manifest `check_tool: whoami`; tools/list by itself proves nothing about key validity. `X-Connection-Id` is request context, not Mail auth. API and runner workers both need private connector reachability. Agent's container API listens on 8000, published host port 8710; frontend host port 8711. Docker default network observed was `clarvio-agent_default`.

Agent's approval broker uses readOnly/destructive hints; reads can run freely unless forced, writes require user/allowed approval policy, and unattended writes cannot bypass approval. PreToolUse checks enforce gating. Do not change tool annotations to evade it. Agent replaces nested `@file:relative/path` string references after approval and saves embedded resource blobs to user files before exposing metadata to the model. Accept at least 10 MiB decoded files; no base64 in JSON text outputs. Agent transport accepts tool catalogs/SSE events up to 16 MiB.

## Mail MCP service

`connector/` ships Python >=3.12/<3.15 with MCP 2.2.0 (matching the studied Agent), httpx 0.28.1, pydantic 2.13.5, Starlette 1.6.0 and uvicorn 0.53.0, locked by uv and hashed Docker requirements. Docker uses Python 3.14.8, UID/GID 1654, dropped capabilities and read-only root. It is a separate stateless shared service, not a subprocess per account or a change to Agent's backend.

Manifest key `clarvio_mail`, name Clarvio Mail, version 1.0.0, MCP path `/mcp`, user-scoped headers auth; field `api_key` -> `Authorization: Bearer ...`; check tool `whoami`; one skill `clarvio-mail-operations`. OAuth stays in Mail; first-version Agent connection is a scoped Mail key, not a second Google/Microsoft OAuth flow. No account credentials/global cache/cookies are stored in the connector. Infrastructure `MAIL_API_URL` is a fixed origin; never a per-user arbitrary URL.

17 tools: `whoami`, `list_mailboxes`, `list_folders`, `list_messages`, `get_message`, `download_attachment`, `mark_read`, `move_message`, `trash_message`, `get_draft`, `create_draft`, `update_draft`, `delete_draft`, `send_message`, `reply_message`, `send_draft`, `get_send_operation`. Check/read/list tools are read-only; delete/trash are destructive; every other mutation has readOnly false. Send tools are idempotent with the supplied key. `get_send_operation` is read-only but requires `mail:send` scope.

`Message` inputs use snake_case including `text_body`, `html_body`, nested `content_base64`/`content_type`; the adapter maps them to Mail's camelCase JSON. Resource IDs are URL-encoded as path segments. File downloads return text metadata plus a blob resource with a safe URL-encoded filename. Tool text cap 40,000 characters; body cap 16,000; previews/names/subjects capped for display with truncated-fields markers. HTML-only messages are converted to text and HTML omitted. Preserve IDs/cursors; return a size error rather than silently lose page elements. Raw upstream errors and credential values are redacted. HTTP writes are never retried by this service.

MCP validates Host/Origin with allowed hosts defaulting to localhost, 127.0.0.1 and clarvio-mail-mcp (wildcard ports). Health/live proves process availability only; `doctor.py` tests initialize/catalog/key and one message page per connected mailbox. It privately prompts for a key and prints counts, not message bodies/addresses. No mailbox writes.

## Webhooks and trigger bridge

Mail worker polls only active subscribed mailboxes, default 60 seconds (minimum 15), suppresses initial sync, deduplicates provider IDs and persists events/delivery retries. Mail event payload: `eventId`, `type: message.received`, `mailboxId`, `messageId`, `createdAt`; no email body. Mail headers: `X-Clarvio-Mail-Event` and `X-Clarvio-Mail-Signature: t=<unix>,v1=<hex HMAC-SHA256(timestamp.rawJSON, subscriptionSecret)>`. The worker regenerates timestamp/signature per delivery attempt. Up to 10 attempts then dead-letter.

Mail destinations require HTTPS/default 443, an exact `Mail:WebhookHosts` allowlist hostname, public DNS/network addresses, no user-info/fragments and no redirects/private routing. Subscription management requires browser session/CSRF; keys cannot create subscriptions. Provider push is not implemented; polling semantics need controlled live validation.

Optional `EventBridge` receives `/webhooks/mail/{binding_id}`; persistent protected JSON binds a random binding UUID to the intended mailbox UUID, Agent connection UUID and subscription signing secret. It verifies raw HMAC/5-minute timestamp tolerance, event/header identity and bound mailbox before forwarding. Bindings must belong to the same intended user; never infer them from unsigned payloads/arbitrary caller connection IDs.

Agent expects POST `/webhooks/connectors/clarvio_mail`, `X-Signature: sha256=<HMAC(rawJSON, Agent WEBHOOK_SECRET)>`, body `{connection_id,event_type,event_id,payload}`. The bridge keeps the event ID, includes mailbox/message/time metadata and acknowledges 202 only after Agent returns 202. Failure/redirect/timeout gives Mail 503 so its durable queue retries; Agent handles duplicates. The bridge stores no API/OAuth keys and has no volatile retry queue. Events appear in its manifest only when bridge configured; resync manifest afterward.

To expose events use the existing HTTPS proxy's `/mcp-webhooks/*` handler, stripping that prefix and forwarding to the private connector. Keep `/mcp` private. Set a public HTTPS Mail subscription URL and exact allowlist. Mount the protected binding and Agent signing-secret files, readable by UID1654, via `compose.mcp-webhooks.yaml`; restart after file changes. Agent default private target `http://api:8000/webhooks/connectors/clarvio_mail`. Update/remove stale bindings/subscriptions on disconnect. Standard Mail backups do not automatically include these optional mounted files.

## Local and production operations

Local: `.local/settings.json` is private/ignored, created by Microsoft/Google setup scripts. Current Google script depends on initialized Microsoft settings. Local launcher starts isolated Postgres port5548, builds/migrates and serves API on 127.0.0.1:8740. `start` leaves an already running instance unchanged; use `restart` for backend/config changes. It verifies PID ownership through /proc before killing. Database volume survives stop. Do not print settings/database env/logs wholesale.

Local MCP: from connector `uv sync --locked`; `MAIL_API_URL=http://127.0.0.1:8740 uv run --locked python server.py`; default loopback8741. Containers cannot reach host loopback, so Agent integration uses private Docker networks instead of registering localhost. Mail production stack and Agent network must exist before `compose.mcp.yaml`; defaults Mail `clarvio-mail_proxy-net`, Agent `clarvio-agent_default`, upstream `http://app:8740`, service URL `http://clarvio-mail-mcp:8741`, no published connector port. Change exact networks/allowed-host/upstream values for another server.

Production helper `scripts/production.py`: init/check/up/status/logs/stop/backup/restore. It generates protected env/keys/certificate inputs, validates, builds versioned Mail image, waits for DB, stops API, runs migration once and starts app/proxy. Not zero-downtime, not automatic CD. Production Microsoft OAuth requires a certificate; Google uses client credentials. Bundled Caddy handles HTTPS; external host proxy mode is available. Forwarded headers trust explicit proxy IPs with limit1, not arbitrary forwarded client values. DB has no public port.

Backup retains DB dump, app/postgres env, Microsoft PFX and deployment manifest/config together; archives are protected. Restore requires explicit `--confirm-restore`, validates archive members/paths, refuses project/container/volume collisions and stops API; it starts DB only so operator can adjust domain/network/provider settings before up. Existing DB password constraints apply. Backups cannot undo already-sent external emails. The optional MCP bindings/Agent secret need a separate protected backup policy. See maintained deployment guide rather than reproducing commands from memory.

## Verification and remaining work

At edd4587: 61 .NET tests, 10 browser tests, 59 Python MCP tests; both Docker smokes; actual Agent check client/manifest/skill, wrong-key rejection and a one-mailbox live read-only doctor. A temporary DB-backed read-only key was removed afterward; no live email send/mutation was performed. Both private CI workflows passed (URLs in STATE.json). Mocked tests cover CC/BCC, drafts, scopes, ownership, idempotency/unknowns, proxy/auth docs, every MCP tool, concurrent account isolation, blob files, redaction, HTML-only text, event signatures/rejections, upload chunk boundaries and unknown uploads.

Unknown/pending: actual production server access/current availability, domain and credential preparation, real server rollout, Agent production registration/user connection, live incoming-event behavior, and controlled live Gmail/Outlook writes. Do not claim those complete. Google publishing/restricted scopes and Yahoo approval are audience/provider-specific deployment decisions, not completed work.
