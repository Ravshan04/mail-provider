# Local development

[Documentation index](README.md) · [Project README](../README.md)

## First run

Use Linux with the .NET 10 SDK, Docker Engine/Compose v2, and Python 3. Node.js 22 and npm are needed only for browser tests. Run commands from the repository root unless noted otherwise.

Register the Microsoft and Google OAuth applications as described in [Local setup](../README.md#local-setup). Configure Microsoft first: that script also generates the local database credentials, encryption key, and login allowlist. The Google configuration script currently requires those initialized settings.

```bash
python3 scripts/configure-microsoft.py
python3 scripts/configure-google.py
python3 scripts/local.py start
```

The launcher starts the isolated PostgreSQL container, builds the application, applies Entity Framework migrations, and launches the API in the background. Open http://localhost:8740 and sign in with the invited account.

Use **Accounts** in the signed-in workspace to connect another Gmail or Microsoft mailbox. Connecting an account does not add another sign-in identity to that workspace. Signing in separately with a different provider identity can create another workspace; a mailbox already owned elsewhere cannot be transferred through account connection.

## Addresses and private files

| Resource | Value |
| --- | --- |
| HTTP bind | `127.0.0.1:8740` |
| Workspace | `http://localhost:8740` |
| Swagger | `http://localhost:8740/swagger` |
| Database | `127.0.0.1:5548`, database/user `clarvio_mail` |
| Compose project / volume | `clarvio-mail-local` / `mail-data` |
| Private configuration | `.local/settings.json` |
| Generated database environment | `.local/database.env` |
| API log / process ID | `.local/api.log` / `.local/api.pid` |

The generated private directory and credential files are excluded from Git. Keep provider client JSON downloads outside the checkout. Do not paste configuration contents into issues or logs.

The fixed ports and Compose project are shared across checkouts. Run one checkout at a time. Stopping the service retains the database volume; deleting that volume removes stored mailboxes, credentials, keys, and service history.

## Daily commands

```bash
python3 scripts/local.py status
python3 scripts/local.py restart
python3 scripts/local.py stop
```

Restart after changing backend code or configuration. `start` leaves an already running instance in place. The launcher checks `/proc` before stopping a process recorded in its PID file.

Configuration checks validate local presence and format, without contacting Microsoft or Google:

```bash
python3 scripts/configure-microsoft.py --check
python3 scripts/configure-google.py --check
```

Use read-only HTTP checks to inspect runtime health and documentation:

```bash
curl --fail http://localhost:8740/health/live
curl --fail http://localhost:8740/health/ready
curl --fail http://localhost:8740/api/v1/providers
curl --fail http://localhost:8740/openapi/v1.json
```

Provider `configured: true` means application credentials are present; it does not prove that a mailbox token is valid. Readiness checks the database, not provider availability.

## Tests

Keep PostgreSQL running, then run:

```bash
dotnet restore --locked-mode
dotnet test Clarvio.Mail.slnx --no-restore
```

Database tests create and drop isolated `clarvio_mail_test_*` databases. Their admin connection defaults to the generated local configuration; `CLARVIO_MAIL_TEST_ADMIN_DATABASE` can override it. The database role must be able to create and drop test databases.

With the API running, run browser tests:

```bash
cd tests/ui
npm ci
npx playwright install chromium
npm test
```

Browser tests mock mailbox operations and do not send real email. The latest full validation passed 51 backend and 10 browser tests. Real-provider sends and mutations remain unverified.

## Troubleshooting

| Symptom | Check and action |
| --- | --- |
| Browser cannot connect | Run `local.py status`, then `local.py start`. This project serves the workspace at port **8740**. |
| Startup fails | Inspect `.local/api.log` privately and the launcher's preceding build/Compose output. Verify Docker access and that ports 8740/5548 are available. |
| Readiness returns 503 | Check `docker compose -f compose.local.yaml ps` and database availability. |
| Provider sign-in is not configured | Run the provider's `--check`; ensure credentials are present, then restart. |
| OAuth redirect mismatch | Match the app registration to `http://localhost:8740/auth/gmail/callback` or `/auth/outlook/callback`, including scheme, port, and path. |
| Login is denied | Check the generated invited login email and the provider test-user configuration where applicable. |
| API returns 401/403 | Verify key expiry, scope, ownership, or browser session. Session mutations need the CSRF token; key/webhook management requires a session. |
| Mailbox requires reconnection | Reconnect from **Accounts** using the original workspace login. |
| Swagger is 404 | It is enabled in Development; other environments require `Mail__EnableSwagger=true`. |
| Send outcome is unknown | Inspect the operation and provider Sent folder before issuing a new send intent. See [send outcomes](api.md#send-outcomes-and-idempotency). |

Production does not load `CLARVIO_MAIL_CONFIG`. See the [server deployment guide](deployment.md) for the production Docker/Compose setup.
