# Clarvio Agent MCP connector

[Documentation index](README.md) · [Project README](../README.md)

The `connector/` directory ships a separate Python/Docker service for the Clarvio Mail platform. Register this service in Clarvio Agent; the Agent backend and frontend need no code changes. The Mail API and connector share this platform repository and are deployed as separate services. Both private repositories carry the same implementation.

## What is implemented

- Stateless Streamable HTTP MCP at `/mcp`, manifest at `/manifest`, and an operations skill served at `/skills/clarvio-mail-operations/SKILL.md`.
- 17 tools, with explicit titles and read-only/destructive/idempotency annotations. Agent's existing approval broker gates all writes.
- Request-local API key authentication. The connector stores no Mail API keys, provider OAuth tokens, cookies or account caches. The fixed `MAIL_API_URL` is infrastructure configuration; credentials only come from each request's `Authorization` header.
- Nested `content_base64` input compatible with Agent's file-reference substitution, and embedded resource blobs for downloads. Outgoing files: 10 files, 10 MiB combined. Downloads: 10 MiB each. Both API and MCP request limits are 16 MiB to fit base64 and envelope overhead.
- Optional signed incoming-email webhook bridge. Persistent operator-managed bindings select the Agent connection. Events are advertised only when the bridge is configured.
- Locked dependencies, mocked tests for every tool, real HTTP transport tests, a read-only doctor, and Docker smoke tests using a fake Mail API. No real emails are sent by CI.

Provider OAuth remains in Clarvio Mail: connect Gmail/Outlook in its workspace first, then give Agent a scoped Mail API key. This first version does not implement a second provider OAuth flow inside Agent.

## Tools and permissions

| Tool | Mail API scope | Approval |
| --- | --- | --- |
| `whoami`, `list_mailboxes` | `accounts:read` | Read |
| `list_folders`, `list_messages`, `get_message`, `get_draft`, `download_attachment` | `mail:read` | Read |
| `mark_read`, `move_message`, `create_draft`, `update_draft` | `mail:write` | Write |
| `trash_message`, `delete_draft` | `mail:write` | Destructive write |
| `send_message`, `reply_message`, `send_draft` | `mail:send` | Write |
| `get_send_operation` | `mail:send` | Read |

`whoami` calls the authenticated Mail API; an invalid, expired or revoked key fails the connection test. Listing the catalog alone is not an account test. API key access is limited to the owning Mail workspace, which may contain several connected mailboxes. Agent separately encrypts each user's connection credentials and injects them with `X-Connection-Id` on every request. The connection ID does not grant access to Mail; the API key does.

API key creation/revocation, disconnecting mailboxes and webhook subscription administration remain browser-session operations in Mail and are intentionally absent from MCP.

## Local run

Requires Python 3.12–3.14 and [uv](https://docs.astral.sh/uv/). The container uses Python 3.14.8. Run the Mail API first using [local development](local-development.md), then in another terminal:

```bash
cd connector
uv sync --locked
MAIL_API_URL=http://127.0.0.1:8740 uv run --locked python server.py
```

The default bind is `127.0.0.1:8741`. Check `http://127.0.0.1:8741/health/live` and `/manifest`. This health endpoint confirms the connector process; it does not prove Mail/provider connectivity. Test an account with:

```bash
uv run --locked python doctor.py --url http://127.0.0.1:8741
```

The doctor prompts privately for the key, initializes MCP, checks the catalog and credentials, and reads one message page per mailbox. It prints counts, not email bodies, tokens or addresses. It performs no mailbox writes. Do not put secrets in CLI arguments, committed environment files or screenshots.

Agent containers cannot reach the host's loopback bind. Use the private Docker setup below for Agent rather than registering a host `localhost` URL.

## Docker and Agent registration

Deploy Mail using [the server guide](deployment.md) first. Start Clarvio Agent's stack so its Docker network exists. From this repository root:

```bash
docker network ls
# Defaults: clarvio-mail_proxy-net and clarvio-agent_default.
# Change these names to match your actual Compose projects.
MAIL_MCP_NETWORK=clarvio-mail_proxy-net \
CLARVIO_AGENT_NETWORK=clarvio-agent_default \
docker compose --project-name clarvio-mail-connector \
  -f deploy/compose.mcp.yaml up --build -d --wait
```

The connector joins Mail's proxy network to reach `http://app:8740` and Agent's network so its API and runner workers reach `http://clarvio-mail-mcp:8741`. No connector port is published. It runs as UID/GID 1654 with a read-only root filesystem, dropped Linux capabilities and temporary `/tmp`. Agent and Mail keep their existing database networks and credentials.

For separate hosts, replace this network arrangement with a private VPN/routed service address. Use HTTPS across untrusted networks, add the exact connector hostname to `MCP_ALLOWED_HOSTS`, and restrict network ingress to Agent. A private service must be reachable from both Agent API and runner containers. Do not publish MCP through the public Mail website by default.

In Clarvio Agent:

1. **Admin → Connectors → Register connector**: base URL `http://clarvio-mail-mcp:8741`, kind `clarvio`.
2. Enable Clarvio Mail for the intended organization. Registration fetches the manifest and operations skill.
3. In the intended user's Mail workspace, connect the Gmail and Outlook accounts. Open **API keys** and create a key with `accounts:read` and `mail:read`; also grant `mail:write` and `mail:send` if writes/sends are needed.
4. In Agent **Connections → Clarvio Mail → Connect**, paste the `cm_sk_...` value without `Bearer`. Agent adds the prefix and encrypts the value. Its connection test calls `whoami`.
5. Start with an explicit read request, such as listing inbox subjects. For sending or changing a mailbox, use Agent's existing approval cards. Do not mark write tools read-only to bypass approval.

If key access is revoked in Mail, tool calls fail immediately; disconnecting Agent alone does not revoke the Mail key. Rotate/revoke it in Mail as needed. Expired provider authorization requires reconnecting that mailbox in Mail.

After manifest or skill updates, re-register/sync the connector with the same base URL; re-test connections to refresh tool annotations. The next Agent turn picks up its current connected MCP servers. The implementation does not automatically register, enable, or create production Agent connections.

Infrastructure settings:

| Setting | Default / purpose |
| --- | --- |
| `MAIL_API_URL` | `http://127.0.0.1:8740`; fixed Mail origin, without credentials, path or query |
| `MCP_HOST`, `MCP_PORT` | `127.0.0.1`, `8741`; Docker sets host to `0.0.0.0` |
| `MCP_ALLOWED_HOSTS` | `localhost:*,127.0.0.1:*,clarvio-mail-mcp:*`; comma-separated MCP Host allowlist |
| `MAIL_MCP_NETWORK` | `clarvio-mail_proxy-net`; external Mail network for Compose |
| `CLARVIO_AGENT_NETWORK` | `clarvio-agent_default`; external Agent network for Compose |
| `MAIL_MCP_IMAGE` | `clarvio-mail-mcp:local`; use a versioned tag for rollout/rollback |
| `MCP_WEBHOOK_BINDINGS_FILE` | Optional persistent JSON bindings file; enables bridge |
| `AGENT_WEBHOOK_URL` | Bridge target ending `/webhooks/connectors/clarvio_mail` |
| `AGENT_WEBHOOK_SECRET_FILE` | File containing Agent's existing infrastructure `WEBHOOK_SECRET` |

The MCP transport rejects unexpected Host and Origin values. Browser OAuth and public webhooks use different endpoints; no browser origin is enabled for MCP. Account authentication always remains mandatory for tools.

## Files, results and send recovery

Send/create/update inputs use a nested message object:

```json
{
  "mailbox_id": "8b250276-3c5e-4b69-bd86-dcc69b350016",
  "idempotency_key": "one-intended-send-uuid",
  "message": {
    "to": [{"email": "recipient@example.com"}],
    "subject": "Invoice",
    "text_body": "Please find the invoice attached.",
    "cc": [],
    "bcc": [],
    "attachments": [{
      "name": "invoice.pdf",
      "content_type": "application/pdf",
      "content_base64": "@file:invoice.pdf"
    }]
  }
}
```

`@file:invoice.pdf` is an Agent file reference, not valid raw MCP input. Agent resolves it after approval; a direct MCP client must supply real base64. The connector validates filenames, MIME types, base64, total size, addresses and content before contacting Mail. Mail validates independently. File downloads return a text metadata block plus `resource.blob` with a URL-encoded filename; Agent saves the file without placing its bytes in the model context.

Mail now handles Outlook attachment requests over the small inline limit following [Microsoft's large-attachment workflow](https://learn.microsoft.com/en-us/graph/outlook-large-attachments) through a draft and upload session: upload sequential ranges to the Microsoft-issued pre-authenticated Outlook URL, then send once. No Graph bearer token is sent to the upload URL, redirects are disabled, the URL host is validated, and provider URL logging is disabled. An interrupted workflow can leave a partial draft; inspect it before any retry. Gmail continues using MIME through its API. Provider/account policies can impose additional limits.

Tool text is capped at 40,000 characters; email text bodies at 16,000, display previews at 800, and HTML is converted to plain text when needed, then omitted. Truncated fields are named in the result. Identifiers and pagination cursors are preserved. If the entire result remains too large, use a smaller page/narrower search. Treat every email and filename as untrusted content.

Never automatically retry a write. For sends, keep exactly the same payload, mailbox and idempotency key during recovery. Read a known operation ID with `get_send_operation`; `accepted` means provider acceptance, while `unknown` needs reconciliation with Sent mail. A timeout does not authorize a fresh key. Draft updates can partially succeed, so reread before retrying. See [API send outcomes](api.md#send-outcomes-and-idempotency).

## Optional incoming-email triggers

Mail and Agent use different webhook signatures and payloads. The bridge verifies Mail's HMAC over the timestamp and raw body (five-minute tolerance), checks the bound mailbox and event header, then signs Agent's raw JSON with its infrastructure secret. It sends only IDs/time metadata, not email bodies or API keys. The same event ID is retained for Agent's deduplication.

The bridge acknowledges `202` only after Agent returns `202`. Network failures, redirects, unknown/disconnected connections and rejected signatures return `503` to Mail so its persistent delivery queue can retry. After Mail's ten attempts, investigate dead-letter deliveries in its workspace. There is no separate in-memory retry queue to lose on restart.

Operator setup, for each intended mailbox/Agent connection:

1. Read that user's actual Agent connection UUID and Mail mailbox UUID. Verify they belong to the same intended user/workspace. Create a random binding UUID (for example `python3 -c 'import uuid; print(uuid.uuid4())'`). Bindings are infrastructure configuration; they must never be selected from an unsigned incoming event or arbitrary caller header.
2. Create a protected secrets directory outside Git (`chmod 700`). Store Agent's existing `WEBHOOK_SECRET` as `agent-webhook-secret`. This is a server secret, not a Mail API key.
3. In Mail, create a webhook subscription for that mailbox pointing to `https://YOUR_MAIL_DOMAIN/mcp-webhooks/webhooks/mail/BINDING_UUID`. Save its signing secret once. Allow the exact public hostname in `Mail:WebhookHosts` and restart Mail after changing settings. See [webhook configuration](webhooks.md).
4. Save `mcp-webhook-bindings.json`:

   ```json
   {
     "BINDING_UUID": {
       "connection_id": "AGENT_CONNECTION_UUID",
       "mailbox_id": "MAIL_MAILBOX_UUID",
       "signing_secret": "MAIL_SUBSCRIPTION_SIGNING_SECRET"
     }
   }
   ```

   Replace every placeholder. IDs must be UUIDs. Secrets must be at least 16 characters. Keep this directory in protected backups. The two mounted files must be readable by container UID 1654: use ownership `1654:1654` with file mode `600` (keep the parent directory `700`); do not make them world-readable.
5. Run with the optional override:

   ```bash
   MAIL_MCP_SECRETS_DIR=/absolute/protected/mcp-secrets \
   AGENT_WEBHOOK_URL=http://api:8710/webhooks/connectors/clarvio_mail \
   docker compose --project-name clarvio-mail-connector \
     -f deploy/compose.mcp.yaml -f deploy/compose.mcp-webhooks.yaml \
     up --build -d --wait
   ```

   Use the correct Agent API hostname/port for your network. These are server-side variables; do not expose the Agent webhook secret to the model or frontend.
6. Publish only the webhook path with the existing public HTTPS proxy. For bundled Caddy, place this handler before the Mail catch-all inside its domain block:

   ```caddy
   handle_path /mcp-webhooks/* {
       reverse_proxy clarvio-mail-mcp:8741
   }
   ```

   The proxy and connector share Mail's proxy network. Reload the proxy. Mail's webhook delivery intentionally requires a public HTTPS hostname and public DNS addresses: a private `http://clarvio-mail-mcp:8741` subscription URL is rejected. Keep `/mcp`, `/manifest` and the Agent backend private.
7. Restart the connector after binding-file changes. Re-register/sync its manifest so Agent sees `message.received`, then create a trigger for the connected account. Test with a controlled incoming email and review Mail delivery status and Agent's accepted event. This live event test is a deployment step; CI uses signed synthetic events.

Disabling the bridge removes events from the manifest. When disconnecting/replacing an Agent connection, remove/update the binding and its Mail subscription; stale IDs are rejected by Agent. Two connected users must never be configured with each other's mailbox bindings.

## Verification and upgrades

```bash
cd connector
uv sync --locked
uv run --locked ruff check .
uv run --locked pytest -q
uv export --locked --no-dev --no-emit-project --format requirements-txt --output-file requirements.lock > /dev/null
cd ..
docker build -t clarvio-mail-mcp:local connector
connector/.venv/bin/python scripts/mcp-smoke.py
```

CI also checks that `requirements.lock` matches `uv.lock`. Docker installs exact hashes from that export. SDK 2.2.0 matches the studied Clarvio Agent transport. Before an SDK upgrade, rerun the HTTP/auth/catalog tests and Agent's connection test; do not assume transport compatibility from `tools/list` alone.

The container smoke creates unique temporary containers/networks, verifies non-root/read-only execution, sends a 10 MiB file to a fake backend, downloads it as an embedded resource, tests rejection of another account's key, and removes its temporary resources. The production Mail smoke remains separate. Run the doctor with one real account after deployment; controlled real provider writes and incoming-trigger delivery still need live validation.

To upgrade, build a versioned connector image, set `MAIL_MCP_IMAGE`, run the same Compose command, then sync manifest/skills and test the connection. The optional bindings/secret files remain mounted across replacement. Roll back by using the previous image tag. For backup/restore, include these bridge files alongside Mail's standard backup; the standard Mail backup helper does not automatically include the optional connector directory.
