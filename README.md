# Clarvio Mail

An email workspace and HTTP API for connected Microsoft and Gmail accounts. Clarvio Mail reads and sends through Microsoft Graph and the Gmail API using delegated OAuth authorization.

**Status:** local development and production Docker/Compose packaging are implemented. Deployment to the real Clarvio server, live-provider write validation, and Clarvio Agent integration are pending. This service connects existing mailboxes; it does not host email domains or run an SMTP server.

The same source is maintained in two private repositories: [Abstract-IT-Group/clarvio-email](https://github.com/Abstract-IT-Group/clarvio-email) and [Ravshan04/clarvio-mail](https://github.com/Ravshan04/clarvio-mail).

## Documentation

- [Documentation index](docs/README.md)
- [Local development and troubleshooting](docs/local-development.md)
- [API reference and Swagger](docs/api.md)
- [Architecture and provider adapters](docs/architecture.md)
- [Webhook integration](docs/webhooks.md)
- [Complete server deployment guide](docs/deployment.md)

For a first run, follow **Local setup** below. For HTTP integrations, start with the API reference and the interactive Swagger UI at **http://localhost:8740/swagger**.

## Features

- Sign in with Microsoft or Google and connect additional mailboxes to the same workspace.
- Browse and search messages with pagination; download file attachments.
- Send, reply, reply all, and forward message text, with To, CC, BCC, and attachments.
- Create, reopen, edit, send, and delete drafts, including reply drafts and retained attachments.
- Mark messages read or unread; move them to Inbox, Archive, Trash, or custom folders/labels. Restore messages by moving them out of Trash.
- Create expiring API keys with separate read, write, and send scopes; revoke keys from the workspace.
- Receive signed webhook events through background polling, with retries and delivery history.
- Review activity without recording email bodies in the audit log.
- Explore all HTTP endpoints through Swagger UI, with API key authorization, session CSRF support, and request examples.

The browser workspace is served by the API. There is no separate frontend development server.

## Email protocols and provider support

SMTP (Simple Mail Transfer Protocol) submits outgoing email and is also used to transfer mail between servers. IMAP (Internet Message Access Protocol) reads and manages messages on a mailbox server, including folders, drafts, and read state. An email client using these protocols typically combines IMAP for mailbox operations with SMTP for sending.

OAuth authorizes access to an account. It can authorize HTTP API requests or IMAP/SMTP connections, depending on the provider's supported authentication methods. Switching to IMAP/SMTP does not automatically remove provider application registration or consent requirements.

| Provider | Current connection | Status |
| --- | --- | --- |
| Gmail | Gmail API over HTTPS, authorized with OAuth | Implemented |
| Outlook / Microsoft 365 | Microsoft Graph over HTTPS, authorized with OAuth | Implemented |
| Yahoo | Proposed IMAP/SMTP adapter with OAuth or an app password | Pending |
| Other IMAP/SMTP providers | Proposed adapter using provider-specific servers and credentials | Pending |

Clarvio Mail currently submits outgoing mail through the Gmail and Graph HTTP APIs. The provider handles subsequent delivery. `MimeKit` constructs MIME messages, headers, and attachments; the application currently has no SMTP or IMAP client implementation.

The HTTP API approach provides the mailbox operations required by the current Gmail and Microsoft integrations: reading, searching, drafting, replying, moving messages, and changing read state. An IMAP/SMTP adapter would extend the service to providers offering those protocols while keeping the existing HTTP API adapters.

### Planned Yahoo connection

For a manually connected Yahoo mailbox, Yahoo documents these [IMAP/SMTP settings](https://help.yahoo.com/kb/SLN4075.html):

| Setting | Value |
| --- | --- |
| IMAP server | `imap.mail.yahoo.com` |
| IMAP port / encryption | `993` / TLS |
| SMTP server | `smtp.mail.yahoo.com` |
| SMTP port / encryption | `465` / TLS |
| Username | Full Yahoo email address |
| Password authentication | Yahoo-generated app password |

When app-password creation is available for the account, generate one under Yahoo Account Security → External connections → Create app password, naming the application `Clarvio Mail`. See [Yahoo's app password guide](https://help.yahoo.com/kb/SLN15241.html?facet1=spreadsheets). This configuration cannot be entered into Clarvio Mail yet: the adapter, connection form, credential storage, and tests still need implementation.

For a **Sign in with Yahoo** connection, register a Yahoo developer application and obtain the required mail-scope access. Yahoo mail access has a review and approval process; identity sign-in permission alone does not authorize mailbox access. After approval, the planned adapter would use OAuth tokens to authenticate to Yahoo's IMAP/SMTP servers. See [Yahoo's OAuth IMAP/SMTP documentation](https://senders.yahooinc.com/developer/documentation/).

## Stack and layout

| Component | Implementation |
| --- | --- |
| API and background worker | ASP.NET Core / .NET 10 |
| Database | PostgreSQL 18, Entity Framework Core, Npgsql |
| Browser workspace | HTML, CSS, vanilla JavaScript |
| Email APIs | Microsoft Graph and Gmail API |
| MIME messages | MimeKit |
| Tests | xUnit with an isolated PostgreSQL database; Playwright with mocked email APIs |

```text
src/Clarvio.Mail/
  Api/          HTTP endpoints
  Auth/         OAuth, sessions, API key identity, CSRF
  Core/         Settings, credential encryption, errors
  Data/         Entities, database locks, migrations
  Providers/    Microsoft and Gmail adapters
  Services/     Mail operations, polling, webhook delivery
  wwwroot/      Browser workspace
scripts/        Local credential setup and service launcher
tests/          Backend and browser tests
docs/           API, architecture, local setup, webhooks, deployment
deploy/         Production Compose, HTTPS proxy, host-proxy example
.github/        Backend, browser, and production deployment CI
Dockerfile      Production application image
compose.local.yaml
```

## Local setup

The launcher supports Linux and uses `/proc` to identify its own API process. Development has been tested on Debian.

Requirements:

- .NET 10 SDK.
- Docker Engine with Docker Compose v2, accessible by your current user.
- Python 3.
- Microsoft and Google OAuth applications, configured below.
- Node.js 22 and npm for browser tests only.

Clone the private repository using a GitHub account with access:

```bash
git clone https://github.com/Abstract-IT-Group/clarvio-email.git
cd clarvio-email
```

Alternatively, clone the personal repository, which contains the same application:

```bash
git clone https://github.com/Ravshan04/clarvio-mail.git
cd clarvio-mail
```

Choose one checkout for local development. The launcher uses shared local ports and a fixed Compose project name.

### Microsoft application

1. Create an app under **Microsoft Entra ID → App registrations**. For organizational and personal mailboxes, select **Any Entra ID Tenant + Personal Microsoft accounts**. See [Microsoft's registration guide](https://learn.microsoft.com/en-us/entra/identity-platform/quickstart-register-app).
2. Add a **Web** redirect URI: `http://localhost:8740/auth/outlook/callback`.
3. Add Microsoft Graph **delegated** permissions: `User.Read`, `Mail.ReadWrite`, `Mail.Send`, and `offline_access`.
4. Create a client secret for local development. Use its **Value**, together with the **Application (client) ID**.
5. Run the interactive configuration script:

```bash
python3 scripts/configure-microsoft.py
```

The script also initializes the local database password, encryption key, and invited login email. It uses the `common` Microsoft tenant endpoint.

### Google application

1. Create a Google Cloud project and enable the Gmail API.
2. Configure Google Auth Platform branding and an External audience. While the app is in Testing, add the Gmail account under Test users.
3. Create an OAuth client of type **Web application** with this authorized redirect URI: `http://localhost:8740/auth/gmail/callback`.
4. Configure `openid`, `https://www.googleapis.com/auth/userinfo.email`, `https://www.googleapis.com/auth/userinfo.profile`, and `https://www.googleapis.com/auth/gmail.modify`. The authorization code uses the equivalent `email` and `profile` aliases. See [Gmail scopes](https://developers.google.com/workspace/gmail/api/auth/scopes) and [Google's web application OAuth guide](https://developers.google.com/identity/protocols/oauth2/web-server).
5. After the Microsoft script has initialized the local settings, run:

```bash
python3 scripts/configure-google.py
```

Alternatively, import the downloaded Web application client JSON from a private location:

```bash
python3 scripts/configure-google.py --credentials /absolute/path/to/google-client.json
```

Both scripts prompt for secrets without displaying them. Their `--check` option validates local presence and format; it does not authenticate against the provider.

### Start and sign in

```bash
python3 scripts/local.py start
```

This starts PostgreSQL, builds the solution, applies migrations, and launches the API in the background. Open **http://localhost:8740** and sign in. Use **Accounts** to connect additional mailboxes to that workspace; signing in with a separate provider identity can create a separate workspace.

| Local resource | Address or location |
| --- | --- |
| Browser workspace and API | `http://localhost:8740` |
| PostgreSQL | `127.0.0.1:5548`, database/user `clarvio_mail` |
| Credentials and encryption keys | `.local/settings.json` |
| Database environment file | `.local/database.env` |
| API log and process ID | `.local/api.log`, `.local/api.pid` |
| Database volume | Compose volume `mail-data` in project `clarvio-mail-local` |

Service commands:

```bash
python3 scripts/local.py status
python3 scripts/local.py restart
python3 scripts/local.py stop
```

Restart after changing credentials or backend code. Stop shuts down the API and database container while retaining database data.

## Configuration and credentials

The launcher loads `.local/settings.json` through `CLARVIO_MAIL_CONFIG` in Development. Environment variables override file values. This optional private configuration file is not loaded in Production.

| Setting | Purpose |
| --- | --- |
| `Mail:PublicUrl` | Public origin used to construct OAuth callbacks; HTTPS is required in Production |
| `Mail:Database` | PostgreSQL connection string |
| `Mail:ActiveKey` | Encryption key ID used for new encrypted values |
| `Mail:EncryptionKeys:<id>` | Base64-encoded 32-byte encryption key |
| `Mail:AllowSignup` / `Mail:AllowedLoginEmails` | Control admission of new login identities; setup disables open signup |
| `Mail:Google:ClientId` / `ClientSecret` | Google Web application credentials |
| `Mail:Microsoft:ClientId` / `ClientSecret` / `Tenant` | Microsoft application credentials and tenant endpoint |
| `Mail:Microsoft:CertificatePath` / `CertificatePassword` | PKCS#12 certificate credentials; this application's Production validation requires a certificate when Microsoft is enabled |
| `Mail:WebhookHosts` | Exact hostnames permitted as webhook destinations; empty by default |
| `Mail:PollSeconds` | Poll interval for subscribed mailboxes; default 60, minimum 15 seconds |
| `Mail:EnableSwagger` | Enable Swagger/OpenAPI outside Development; default false |
| `Mail:TrustedProxies` | Explicit proxy IP addresses allowed to forward client IP and scheme; supplied by production Compose |

For environment variables, replace `:` with `__`, for example `Mail__PublicUrl`, `Mail__EncryptionKeys__v1`, and `Mail__WebhookHosts__0`.

Provider tokens and webhook signing secrets are encrypted using AES-256-GCM. API keys and session tokens are stored as hashes. The `.local` directory, build outputs, browser dependencies, and private key/certificate files are excluded from Git.

Keep encryption keys with protected backups of the database. Retain old keys when rotating the active key so existing credentials remain decryptable. Store downloaded OAuth JSON files outside the repository.

## API

See the [complete API reference](docs/api.md) for all 32 operations, authentication, request examples, and error handling.

Endpoints use the `/api/v1` prefix. Create an API key from the browser workspace and send it using `Authorization: Bearer <api-key>`. Keys can access the owner's connected mailboxes. Key management, webhook management, and audit access require a browser session. Browser mutations also require `X-CSRF-Token`, obtained from `GET /api/v1/session`.

| Scope | Access |
| --- | --- |
| `accounts:read` | List connected mailboxes |
| `mail:read` | List folders/messages, read drafts, download attachments |
| `mail:write` | Change read state, move/trash messages, create/edit/delete drafts |
| `mail:send` | Send messages/replies/drafts and inspect send operations |

In the following table, `{mailbox}` is a mailbox UUID and message, draft, and attachment IDs are provider IDs that must be URL-encoded.

| Method | Path after `/api/v1` | Operation |
| --- | --- | --- |
| GET | `/mailboxes` | List connected mailboxes |
| GET | `/mailboxes/{mailbox}/folders` | List available folders/labels |
| GET | `/mailboxes/{mailbox}/messages` | List/search; query parameters: `folder`, `query`, `limit`, `cursor` |
| GET | `/mailboxes/{mailbox}/messages/{message}` | Read a message |
| GET | `/mailboxes/{mailbox}/messages/{message}/attachments/{attachment}` | Download a file |
| PATCH | `/mailboxes/{mailbox}/messages/{message}/read` | Body: `{"isRead":true}` |
| POST | `/mailboxes/{mailbox}/messages/{message}/move` | Body: `{"destinationFolderId":"archive","sourceFolderId":"inbox"}` |
| DELETE | `/mailboxes/{mailbox}/messages/{message}` | Move a regular message to Trash |
| POST | `/mailboxes/{mailbox}/messages` | Send a new message |
| POST | `/mailboxes/{mailbox}/messages/{message}/reply` | Send a reply |
| GET | `/mailboxes/{mailbox}/messages?folder=drafts` | List drafts, including their `draftId` |
| GET | `/mailboxes/{mailbox}/drafts/{draft}` | Read a draft |
| POST / PUT | `/mailboxes/{mailbox}/drafts` / `/mailboxes/{mailbox}/drafts/{draft}` | Create / update a draft |
| DELETE | `/mailboxes/{mailbox}/drafts/{draft}` | Delete a draft |
| POST | `/mailboxes/{mailbox}/drafts/{draft}/send` | Send a saved draft |
| GET | `/operations/{operation}` | Inspect a send outcome |

Send bodies contain `to`, `subject`, `textBody`, and optional `cc`, `bcc`, `htmlBody`, and `attachments`. Addresses use `{"email":"recipient@example.com","name":"Recipient"}`; attachments use `name`, `contentType`, and `contentBase64`.

Draft bodies wrap this content in `message`. `keepAttachmentIds` selects existing files to retain; omission retains all, and an empty array removes all. New reply drafts can include `replyMessageId`. Use the returned `draftId` for later draft operations; Gmail draft IDs differ from message IDs.

All send requests require an **`Idempotency-Key` of 16–128 characters**. Reuse the same key when retrying the same send intent. Outcomes are `sending`, `accepted`, `failed`, or `unknown`. `accepted` means provider acceptance, not delivery confirmation. Check the operation and Sent mail before issuing a new send when the outcome is unknown.

Health endpoints are `/health/live` and `/health/ready`. Readiness checks database connectivity.

### Swagger UI

Open **http://localhost:8740/swagger** for the interactive API reference. The OpenAPI document is served at `/openapi/v1.json`. All 32 operations are grouped by function, with request examples, response models, error responses, and authentication requirements.

To try mailbox APIs:

1. Sign in to the workspace and create an API key with the needed scopes.
2. Open Swagger, click **Authorize**, and paste the full `cm_sk_` key into **ApiKey** without the `Bearer` prefix.
3. Execute `GET /api/v1/mailboxes` to obtain your mailbox ID, then fill the parameters for another operation.
4. For send/reply/draft-send requests, provide an `Idempotency-Key` of 16–128 characters. Reuse it only for retries of the same send intent.

Session-only operations such as key and webhook management require signing in at `/` in the same browser. The HttpOnly session cookie is sent automatically; it cannot be entered manually in Swagger. The UI obtains the session CSRF token for mutations. Authorization is not persisted across page reloads.

**Try it out executes real requests.** Send, move, delete, and disconnect operations affect the connected mailbox or service state.

Swagger and OpenAPI are enabled in Development. Outside Development, set `Mail__EnableSwagger=true` to enable them explicitly; otherwise their routes return 404. All Swagger assets are bundled locally and use the existing Content Security Policy.

## Webhooks

See the [webhook integration guide](docs/webhooks.md) for subscription setup, signature verification, and delivery behavior.

Add permitted destination hostnames to `Mail:WebhookHosts`, restart, then create a subscription in **Webhooks**. Destinations must use HTTPS on the default port; redirects and connections to private network addresses are blocked.

The worker polls subscribed mailboxes and suppresses the initial synchronization. Event payloads contain `eventId`, `type`, `mailboxId`, `messageId`, and `createdAt`; they do not contain the email body.

Deliveries include `X-Clarvio-Mail-Event` and `X-Clarvio-Mail-Signature: t=<timestamp>,v1=<signature>`. Verify the lowercase hexadecimal HMAC-SHA256 signature over `<timestamp>.<raw-request-body>` using the signing secret, validate timestamp freshness, and deduplicate by `eventId`. Delivery is at least once, with up to 10 attempts before `dead_letter`.

## Tests

With local settings configured and PostgreSQL running:

```bash
dotnet restore --locked-mode
dotnet test Clarvio.Mail.slnx --no-restore
```

Database tests create and remove uniquely named `clarvio_mail_test_*` databases. They use the local database connection as the admin source; `CLARVIO_MAIL_TEST_ADMIN_DATABASE` can override it. The test database role needs permission to create and drop those databases. Email provider writes are mocked.

For browser tests, keep the local API running to serve the workspace:

```bash
cd tests/ui
npm ci
npx playwright install chromium
npm test
```

Browser tests intercept email API requests and do not send real messages. At the production packaging checkpoint, all 51 backend and 10 browser tests passed, including documentation generation, Production settings, API key authorization, Swagger session CSRF, and trusted proxy handling. Real Gmail and Microsoft folder/draft listing was checked separately; live send and mutation testing remains pending.

Production container verification builds the image and exercises an isolated HTTPS proxy, migrations, health, Swagger policy, OAuth redirect/cookie construction, backup/restore, and host-proxy mode using fake OAuth credentials:

```bash
python3 scripts/production-smoke.py
```

GitHub Actions runs the backend, browser, and production container checks on main pushes and pull requests in both repositories. It does not automatically deploy a server.

## Deploy to another server

Follow the [server deployment guide](docs/deployment.md) for host requirements, DNS/OAuth callbacks, Microsoft certificate creation, every configuration change, HTTPS, migrations, monitoring, backups/restores, and upgrades/rollbacks.

With the server, domain, and provider applications prepared:

```bash
python3 scripts/production.py init
python3 scripts/production.py check
python3 scripts/production.py up
```

The setup generates protected production credentials. Bundled Caddy supplies HTTPS; an existing host proxy can instead use external mode. The API runs as non-root, PostgreSQL has no public port, and migrations run before the API starts. Keep the database and encryption keys together when moving an existing installation.

## Current limits and deployment work

- Outgoing mail supports up to 50 recipients, 10 files, and 2 MiB of attachments combined. File downloads are limited to 5 MiB each.
- The UI displays HTML email as text. Editing an HTML draft's body saves it as plain text; forwarding includes message text and requires selecting attachments explicitly.
- Gmail uses labels, while Microsoft uses folders. Gmail draft deletion is permanent; regular message deletion through this API moves the message to Trash.
- The Gmail setup script currently depends on settings initialized by the Microsoft script.
- Inbox events use polling rather than provider push subscriptions. Validate provider change-event behavior before relying on it in production.

The [server deployment guide](docs/deployment.md) describes the implemented tooling and the checks needed for an actual rollout.

Before deployment:

- [ ] Run controlled live-provider tests for send, reply, CC/BCC, attachments, drafts, read state, moves, and webhook delivery.
- [x] Add a Dockerfile, production Compose, deploy/backup/restore commands, and CI checks.
- [x] Implement trusted forwarded headers and bundled/external HTTPS proxy configuration.
- [ ] Configure the actual production domain, proxy, and provider callback URLs.
- [ ] Supply production credentials, the Microsoft certificate, encryption keys, and invited login identities through protected configuration.
- [ ] Review Google OAuth publishing and restricted-scope verification requirements for the intended audience; see [Google's Gmail scope guidance](https://developers.google.com/workspace/gmail/api/auth/scopes).
- [ ] Configure database backups, monitoring, migrations, restart behavior, and rollback procedures.
- [ ] Integrate the API and signed webhooks with Clarvio Agent.

Provider expansion:

- [ ] Implement an IMAP/SMTP adapter, connection form, protected credentials, and provider compatibility tests.
- [ ] Add Yahoo support; obtain approved Yahoo mail access for the OAuth connection option.
