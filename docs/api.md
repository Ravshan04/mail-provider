# HTTP API and Swagger

[Documentation index](README.md) · [Webhook integration](webhooks.md)

Local base URL: `http://localhost:8740`. All mailbox and workspace endpoints use `/api/v1`. JSON uses camelCase. Mailbox, key, subscription, and operation IDs are UUIDs. Message, folder, draft, and attachment IDs belong to the provider and must be URL-encoded when used in paths or query parameters.

## Swagger

Open http://localhost:8740/swagger. The OpenAPI document is at `/openapi/v1.json` and describes all 32 operations with tags, schemas, examples, headers, and security requirements.

1. Sign in at `/` and create an API key with the required scopes.
2. In Swagger, select **Authorize → ApiKey** and enter the full `cm_sk_` token without a `Bearer` prefix.
3. Execute `GET /api/v1/mailboxes` to obtain a mailbox ID.
4. Fill provider IDs and required headers before executing another operation.

Session-only operations require signing in in the same browser. Swagger sends the HttpOnly session cookie automatically and obtains the CSRF token for session mutations. API key authorization is not retained after a page reload.

**Try it out performs real operations**, including sends, deletions, moves, and account disconnection. Swagger assets are bundled locally. Development enables Swagger/OpenAPI by default; outside Development they return 404 unless `Mail__EnableSwagger=true`.

## Authentication

API integrations use `Authorization: Bearer <api-key>`. Keys belong to one workspace and can only access its mailboxes. A valid browser session can also access mailbox operations. Session mutations require `X-CSRF-Token` from `GET /api/v1/session`; API key mutations do not need that token.

| Key scope | Operations |
| --- | --- |
| `accounts:read` | List connected mailboxes |
| `mail:read` | List folders/messages, read messages/drafts, download attachments |
| `mail:write` | Read flags, move/trash messages, save/delete drafts |
| `mail:send` | Send messages/replies/drafts, inspect send operations |

Key management, webhook management, audit history, logout, session inspection, and mailbox disconnection require a browser session. A bearer token cannot replace that session. API keys expire after 1–365 days, default 90 days, with a maximum of 20 active keys per workspace. The full key is returned only when created.

Read-only curl example; supply a key privately in the environment:

```bash
curl --fail-with-body \
  -H "Authorization: Bearer ${CLARVIO_API_KEY}" \
  http://localhost:8740/api/v1/mailboxes
```

## Operations

`Public` requires no workspace identity. `Session` requires the signed-in browser. Scope names indicate endpoints supporting either a scoped key or a browser session.

| Method | Path | Authorization | Result |
| --- | --- | --- | --- |
| GET | `/health/live` | Public | 200 process liveness |
| GET | `/health/ready` | Public | 200 database ready, or 503 |
| GET | `/auth/{provider}` | Public; session for `mode=connect` | 302 OAuth redirect |
| GET | `/auth/{provider}/callback` | Browser-bound OAuth state | 302 workspace redirect |
| GET | `/api/v1/providers` | Public | Provider/configuration list |
| GET | `/api/v1/session` | Session | User and CSRF token |
| POST | `/api/v1/logout` | Session | 204 |
| GET | `/api/v1/mailboxes` | `accounts:read` | Connected mailbox list |
| DELETE | `/api/v1/mailboxes/{mailboxId}` | Session | 204 disconnect |
| GET | `/api/v1/mailboxes/{mailboxId}/folders` | `mail:read` | Folder/label list |
| GET | `/api/v1/mailboxes/{mailboxId}/messages` | `mail:read` | Message page |
| GET | `/api/v1/mailboxes/{mailboxId}/messages/{messageId}` | `mail:read` | Message |
| GET | `/api/v1/mailboxes/{mailboxId}/messages/{messageId}/attachments/{attachmentId}` | `mail:read` | Binary download |
| PATCH | `/api/v1/mailboxes/{mailboxId}/messages/{messageId}/read` | `mail:write` | 204 |
| POST | `/api/v1/mailboxes/{mailboxId}/messages/{messageId}/move` | `mail:write` | Moved message |
| DELETE | `/api/v1/mailboxes/{mailboxId}/messages/{messageId}` | `mail:write` | Message moved to Trash |
| GET | `/api/v1/mailboxes/{mailboxId}/drafts/{draftId}` | `mail:read` | Draft message |
| POST | `/api/v1/mailboxes/{mailboxId}/drafts` | `mail:write` | Saved draft |
| PUT | `/api/v1/mailboxes/{mailboxId}/drafts/{draftId}` | `mail:write` | Updated draft |
| DELETE | `/api/v1/mailboxes/{mailboxId}/drafts/{draftId}` | `mail:write` | 204 draft deletion |
| POST | `/api/v1/mailboxes/{mailboxId}/drafts/{draftId}/send` | `mail:send` | Send operation |
| POST | `/api/v1/mailboxes/{mailboxId}/messages` | `mail:send` | Send operation |
| POST | `/api/v1/mailboxes/{mailboxId}/messages/{messageId}/reply` | `mail:send` | Send operation |
| GET | `/api/v1/operations/{id}` | `mail:send` | Send operation |
| GET | `/api/v1/keys` | Session | Key metadata, without tokens |
| POST | `/api/v1/keys` | Session | Created key and one-time token |
| DELETE | `/api/v1/keys/{id}` | Session | 204 revoke |
| GET | `/api/v1/audit` | Session | Latest 100 activity records |
| GET | `/api/v1/webhooks` | Session | Latest 100 subscriptions |
| POST | `/api/v1/webhooks` | Session | Subscription and one-time signing secret |
| DELETE | `/api/v1/webhooks/{id}` | Session | 204 disable |
| GET | `/api/v1/webhook-deliveries` | Session | Latest 100 delivery records |

`provider` is `gmail` or `outlook`. OAuth callback parameters are supplied by the provider; do not call a callback as a replacement for starting an authorization flow.

## Reading messages and folders

List queries accept `folder` (default `inbox`), `query`, `limit` (default 25, range 1–50), and `cursor`. Search is interpreted by the provider and is limited to 300 characters. Use folder IDs returned by `/folders`, including custom folders/labels.

A page contains `messages` and `nextCursor`. Return the opaque cursor with the same mailbox, folder, search, and limit. Cursors expire after 30 minutes; restart pagination when expired. List messages can omit body details, so fetch a specific message to read its body and attachment metadata.

A message includes `id`, `threadId`, `subject`, `from`, `to`, `cc`, `receivedAt`, `preview`, `textBody`, `htmlBody`, `isRead`, and `attachments`, plus optional reply headers, `bcc`, and `draftId`. Attachment entries expose `id`, `name`, `contentType`, `size`, and optional `contentId`. File downloads use `application/octet-stream` with a download filename, and support at most 10 MiB per file.

Set read state with `{"isRead":true}`. Move with `{"destinationFolderId":"archive","sourceFolderId":"inbox"}`. Use a destination returned by `/folders`; `sourceFolderId` helps preserve unrelated Gmail labels. Restore a trashed message by moving it to Inbox or another supported destination. Regular message deletion moves to Trash.

## Sending and drafts

Example send/reply body, with placeholder addresses:

```json
{
  "to": [{"email": "recipient@example.com", "name": "Recipient"}],
  "cc": [{"email": "copy@example.com"}],
  "bcc": [{"email": "hidden@example.com"}],
  "subject": "Example message",
  "textBody": "Example body",
  "attachments": [
    {"name": "note.txt", "contentType": "text/plain", "contentBase64": "SGVsbG8="}
  ]
}
```

`cc`, `bcc`, `htmlBody`, and `attachments` are optional. Outgoing messages support at most 50 recipients across all recipient fields, 10 files, and 10 MiB of attachment bytes combined. Browser reply-all and forward actions construct requests using the existing routes; there is no dedicated forward endpoint.

Create/update a draft by wrapping the send body in `message`:

```json
{
  "message": {
    "to": [{"email": "recipient@example.com"}],
    "subject": "Example draft",
    "textBody": "Draft body"
  },
  "keepAttachmentIds": []
}
```

On updates, omitting `keepAttachmentIds` retains all existing attachments; an empty array removes them. Selected IDs retain those files, and `message.attachments` adds new files. A new reply draft may include `replyMessageId`.

List drafts using `/messages?folder=drafts`, then use the returned **`draftId`** for draft operations. Gmail's draft ID differs from its message ID; message IDs can change during draft edits. Draft deletion is permanent for Gmail. Microsoft draft updates involving attachments use multiple provider calls, so reopen a draft to inspect its state after an interrupted save.

## Send outcomes and idempotency

Every send, reply, and draft-send request requires an `Idempotency-Key` header containing 16–128 characters, for example `clarvio-example-send-0001`. The body of draft-send is empty.

Reuse the same key when retrying the same intent. Reusing a key with a different send intent returns 409. Keys apply across send operations in the same workspace. The operation is recorded before contacting the provider, and an existing operation is returned without automatically sending it again.

Send responses contain `id`, `status`, `providerMessageId`, `errorCode`, `createdAt`, and `updatedAt`. Inspect them through `/api/v1/operations/{id}`.

| Status | Meaning and next step |
| --- | --- |
| `sending` | Outcome is not yet recorded; inspect the operation again. |
| `accepted` | Provider accepted the request; delivery is not confirmed. Microsoft may omit the provider message ID. |
| `failed` | Recorded failure; inspect `errorCode` and resolve it before creating another send intent. |
| `unknown` | Provider acceptance could not be determined. Check Sent mail and the operation before deciding whether to create another intent. |

An ambiguous result must not trigger an automatic retry with a new key. Stale `sending` records are eventually marked `unknown` by the worker.

## Errors and rate limits

Application errors use this envelope:

```json
{
  "error": {"code": "example_code", "message": "An explanation."},
  "traceId": "request-trace-id"
}
```

| Status | Common cause |
| --- | --- |
| 400 | Invalid input, cursor, or idempotency key |
| 401 | Missing, expired, or invalid identity |
| 403 | Missing key scope, required session, or CSRF token |
| 404 | Resource unavailable to the authenticated workspace |
| 409 | Reconnection required, conflicting send intent, or resource limit |
| 413 | Request or attachment too large |
| 429 | Rate limit; observe `Retry-After` |
| 502 | Provider operation failed |
| 503 | Database readiness check failed |

Configured fixed-window policies allow 200 API requests/minute and 20 send or authentication requests/minute. Partitions use the authenticated workspace where available, otherwise the remote IP. Requests are not queued. Preserve `traceId` when reporting a failure; do not include tokens or message bodies in diagnostic reports.
