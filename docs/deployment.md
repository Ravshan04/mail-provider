# Production deployment preparation

[Documentation index](README.md) · [Configuration reference](../README.md#configuration-and-credentials)

**Status:** the application runs locally. It has not been deployed to the Clarvio server. This repository does not yet contain a Dockerfile, production Compose file, or CI deployment pipeline. The local launcher is a Development helper rather than a production service manager.

The target is the existing Clarvio server, with Gmail and Microsoft support. The following describes the required configuration and remaining release work; it is not a completed deployment procedure.

## Configuration contract

Production does not load the private `CLARVIO_MAIL_CONFIG` file. Supply protected ASP.NET Core configuration, such as environment variables or a platform secret store. Environment variable names replace `:` with `__`.

| Variable | Required production value |
| --- | --- |
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `Mail__PublicUrl` | Actual HTTPS origin without a path or query |
| `Mail__Database` | Production PostgreSQL connection string |
| `Mail__ActiveKey` | Active encryption key ID, for example `v1` |
| `Mail__EncryptionKeys__v1` | Protected base64-encoded 32-byte key |
| `Mail__AllowSignup` | Set according to the intended audience; false for invited users |
| `Mail__AllowedLoginEmails__0` | First invited login email when restricting signup |
| `Mail__Google__ClientId` / `Mail__Google__ClientSecret` | Google Web application credentials |
| `Mail__Microsoft__ClientId` / `Mail__Microsoft__Tenant` | Microsoft application ID and tenant selection |
| `Mail__Microsoft__CertificatePath` / `Mail__Microsoft__CertificatePassword` | Protected PKCS#12 file and its password |
| `Mail__WebhookHosts__0` | First allowed webhook receiver hostname, if using webhooks |
| `Mail__PollSeconds` | At least 15; default 60 |
| `Mail__EnableSwagger` | Optional true to expose Swagger/OpenAPI; otherwise disabled |

This application's production validation requires certificate credentials when Microsoft is enabled. A development client secret alone does not satisfy that validation. Register the matching certificate with the Microsoft application and provide the private PKCS#12 certificate to the deployed service.

Retain existing encryption-key versions when changing the active key. A database restored without the corresponding keys cannot recover encrypted provider credentials or signing secrets. Do not reuse the generated local database password for production.

## Domain, proxy, and OAuth

Choose the final HTTPS origin before configuring callbacks. For example, if the chosen origin is `https://mail.example.com`, register:

```text
https://mail.example.com/auth/gmail/callback
https://mail.example.com/auth/outlook/callback
```

`mail.example.com` is a placeholder, not an existing deployment. Set `Mail__PublicUrl` to the actual origin. Add production Web redirect URIs in Google and Microsoft; keep localhost callbacks only when local development is still needed.

Prepare an HTTPS reverse proxy, a private application bind, and forwarded-header handling with explicitly trusted proxies. Forwarded-header middleware is not implemented in the current application. Verify redirects, secure cookies, client IP/rate-limit behavior, and the workspace/Swagger Content Security Policy through the actual proxy.

Provider application publishing and consent requirements must be resolved for the intended users before production rollout. The README links to provider scope and registration documentation.

## Build and migration primitives

The application has a migration command, but deployment orchestration is still pending. These commands build a release and apply migrations once **after** production configuration and the database are supplied:

```bash
dotnet restore --locked-mode
dotnet publish src/Clarvio.Mail/Clarvio.Mail.csproj \
  --configuration Release --no-restore --output artifacts/mail
dotnet artifacts/mail/Clarvio.Mail.dll --migrate
```

Do not run the migration command against an unverified connection string. Run it as a controlled release step with a backup and a migration plan. Start the service from the published directory so the content root resolves browser assets correctly:

```bash
cd artifacts/mail
dotnet Clarvio.Mail.dll
```

A production container or service manager must provide restart behavior, logs, the listening address, mounted credentials, and the environment. Those definitions still need to be added. Normal API startup does not automatically apply migrations; the local launcher invokes the migration command separately.

## Work remaining before release

- Run controlled Gmail/Microsoft send, reply, CC/BCC, attachment, draft, read-state, move/restore, and webhook tests using dedicated test mailboxes.
- Add production packaging and CI validation, then build a reproducible release artifact.
- Configure the Clarvio server, database, HTTPS domain, trusted proxy handling, credentials, and provider callbacks.
- Configure database/key backups, restore verification, health monitoring, service restarts, and a migration-aware rollback procedure.
- Validate signed webhook processing and duplicate-event handling in the intended receiver.
- Connect Clarvio Agent using scoped keys and validated webhooks.

Local checks passed 46 backend and 10 browser tests at the Swagger checkpoint. Provider writes in those tests are mocked. Live folder/draft reads passed separately; this does not validate production sending or deployment.

## Runtime checks

After a release, inspect `/health/live`, `/health/ready`, and the workspace through the public proxy. Readiness reports database access, not provider token validity. Validate provider sign-in, one authorized mailbox read, and application error handling with the intended production configuration.

Swagger is disabled outside Development unless `Mail__EnableSwagger=true`. Enabling it exposes documentation; authenticated endpoints still require their usual identity, scopes, and CSRF protection. **Try it out** can change live mailbox state.
