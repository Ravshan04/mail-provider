# Clarvio Mail documentation

Clarvio Mail connects Gmail and Microsoft mailboxes through OAuth and provider HTTP APIs. The browser workspace, API, and background worker run in one ASP.NET Core application backed by PostgreSQL.

| Guide | Contents |
| --- | --- |
| [Project README](../README.md) | Features, provider registration, configuration, tests, and current limits |
| [Local development](local-development.md) | Start/stop commands, local files, verification, and troubleshooting |
| [API reference](api.md) | Swagger, authentication, all 32 operations, payloads, and send outcomes |
| [Architecture](architecture.md) | Components, ownership, credentials, provider adapters, and persistence |
| [Webhooks](webhooks.md) | Subscription setup, signed requests, retries, and polling limitations |
| [Server deployment](deployment.md) | Fresh server setup, domain/provider changes, HTTPS, release commands, backup/restore, upgrades/rollback |

Local addresses:

- Workspace: http://localhost:8740
- Swagger UI: http://localhost:8740/swagger
- OpenAPI JSON: http://localhost:8740/openapi/v1.json
- Readiness: http://localhost:8740/health/ready

Both private repositories contain this documentation and the same source:

- [Abstract-IT-Group/clarvio-email](https://github.com/Abstract-IT-Group/clarvio-email)
- [Ravshan04/clarvio-mail](https://github.com/Ravshan04/clarvio-mail)

**Validation status:** 51 backend and 10 browser tests passed at the production packaging checkpoint. Container deployment is exercised by `scripts/production-smoke.py`. Browser/provider mutation tests use mocks. Live Gmail and Microsoft folder/draft reads were checked; live sends, other mailbox mutations, and the actual server rollout still need validation.

Yahoo and generic IMAP/SMTP connections are planned. The current service supports Gmail and Microsoft only.
