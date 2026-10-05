# Server deployment guide

[Documentation index](README.md) · [Configuration reference](../README.md#configuration-and-credentials)

Install Clarvio Mail on a Linux server using Docker Compose, with either bundled Caddy HTTPS or an existing host proxy. A different server/domain requires configuration changes, not C# changes.

Production packaging and deployment scripts are implemented. Tests use disposable containers and fake OAuth credentials. The real Clarvio server has not been changed; live provider consent, sends, and Clarvio Agent integration remain rollout tasks.

## 1. Prepare the server

Requirements: Linux, Docker Engine/Buildx, **Docker Compose 2.30+** (Compose 5 also works), **Python 3.12+**, Git, OpenSSL, curl, and access to one private repository. The .NET SDK is inside the build image; Node.js is not needed on the production host. Allow disk space for SDK/runtime images, database data, build cache, and backups.

Install Docker using its [official Debian guide](https://docs.docker.com/engine/install/debian/) or the guide for your distribution. On Debian, install helper tools if missing:

```bash
sudo apt update
sudo apt install git python3 openssl curl ca-certificates
docker version
docker compose version
git clone https://github.com/Abstract-IT-Group/clarvio-email.git
cd clarvio-email
```

Alternatively clone `https://github.com/Ravshan04/clarvio-mail.git`, then enter `clarvio-mail`. Use an operator account that can run Docker, and a persistent checkout owned by that account. Authenticate Git using your approved GitHub method; do not put tokens in clone URLs. Run remaining commands from the repository root.

## 2. Domain and OAuth

Replace **`mail.example.com`** everywhere with your real hostname. Point its DNS A record to the server; add AAAA only when IPv6 is reachable. Allow inbound 80/443 for bundled Caddy and outbound HTTPS to providers/certificate services. If another service owns 80/443, use external proxy mode.

Register these production **Web** callbacks:

```text
https://mail.example.com/auth/gmail/callback
https://mail.example.com/auth/outlook/callback
```

Microsoft needs the intended account audience and delegated `User.Read`, `Mail.ReadWrite`, `Mail.Send`, and `offline_access`. Google needs Gmail API, email/profile identity scopes, `openid`, and `gmail.modify`. See [provider registration](../README.md#local-setup). Use existing apps with added callbacks or separate production apps. For Google Testing, add intended test users; resolve publishing/verification for a broader audience. Docker does not grant mailbox consent.

## 3. Microsoft certificate

Skip this section when Microsoft is disabled. Production requires a certificate; the development client secret alone does not satisfy application validation.

Create private credentials outside the repository. These commands prompt for private-key and export passwords:

```bash
mkdir -p ~/.config/clarvio-mail
chmod 700 ~/.config/clarvio-mail
cd ~/.config/clarvio-mail
umask 077
openssl req -x509 -newkey rsa:3072 -sha256 -days 365 \
  -keyout microsoft-private.key -out microsoft-public.pem \
  -subj '/CN=Clarvio Mail OAuth'
openssl pkcs12 -export -out microsoft.pfx \
  -inkey microsoft-private.key -in microsoft-public.pem
```

In the Microsoft app registration, open **Certificates & secrets → Certificates → Upload certificate**, and upload **`microsoft-public.pem`**. Keep `.pfx` and the private key private. Record the Application (client) ID, absolute `.pfx` path, and export password, then return to the repository root.

Rotate before certificate expiry: register the new public certificate first, replace the `.pfx` and password together, check settings, then restart. Existing mailbox tokens remain associated with the application ID.

## 4. Generate configuration

```bash
python3 scripts/production.py init
```

Enter the hostname without scheme/path/port, initial invited login/contact email, proxy mode (`bundled` or `external`), Google Web client ID/secret, and Microsoft application ID/certificate path/password. A blank provider ID disables that provider; configure at least one. Secrets are prompted without displaying them.

The script generates new database credentials and an encryption key, with these files:

| File | Contents |
| --- | --- |
| `deploy/.env` | Domain, proxy mode, image tag, Compose project, network settings |
| `deploy/secrets/app.env` | Provider credentials, application settings, encryption keys |
| `deploy/secrets/postgres.env` | Database initialization credentials |
| `deploy/secrets/microsoft.pfx` | Private certificate, or empty mount file when disabled |

The secrets directory is `0700`; environment files are `0600`. The `.pfx` is `0644` inside that private directory, so the non-root API can read its individually mounted file. Generated secrets/configuration/backups are excluded from Git and the Docker build context.

Edit application settings as **raw `KEY=value` lines**, without shell quotes or multiline values. Compose's [raw environment format](https://docs.docker.com/reference/compose-file/services/#format) preserves quotes and `$` in values. Do not source the file in a shell or print resolved Compose configuration, which contains secret values.

```bash
python3 scripts/production.py check
```

This checks permissions, matching database settings, encryption-key format, proxy settings, PKCS#12/password when enabled, and Compose syntax. It does not authenticate provider accounts. Do not run `init` again over existing settings or restored data.

## 5. What changes on another server

| Change | Setting/action |
| --- | --- |
| Domain | `deploy/.env`: `MAIL_DOMAIN`; `app.env`: `Mail__PublicUrl` and `AllowedHosts`; update both OAuth callbacks |
| Permitted logins | `app.env`: `Mail__AllowedLoginEmails__0`, `__1`, etc. |
| Provider credentials | `app.env`: `Mail__Google__ClientId/ClientSecret`, `Mail__Microsoft__ClientId/Tenant`; replace Microsoft `.pfx` and password together |
| Existing host proxy | `deploy/.env`: `MAIL_PROXY_MODE=external` |
| Host API port | `deploy/.env`: `MAIL_BIND_PORT` in external mode; match proxy upstream |
| Docker subnet collision | Change `MAIL_PROXY_SUBNET`, `MAIL_PROXY_GATEWAY`, `MAIL_PROXY_IP`, and `MAIL_APP_IP` together before starting |
| Multiple instances | Different `COMPOSE_PROJECT_NAME`, subnet, external host port, and configuration directory |
| Swagger | `app.env`: `Mail__EnableSwagger=true`; default false |
| Webhook receivers | `app.env`: `Mail__WebhookHosts__0=receiver.example.com`, then indexed entries |
| Poll interval | `app.env`: `Mail__PollSeconds`; default 60, minimum 15 |

Compose supplies the exact `Mail__TrustedProxies__0` IP automatically: Caddy in bundled mode or the Docker gateway for a host proxy. Middleware accepts client IP/scheme only from that peer and does not accept forwarded Host. See [Microsoft's proxy guidance](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0).

A fresh installation generates new database/encryption credentials. To **move existing connected accounts**, restore database and keys together. Keep old key versions during key rotation. Changing a database password requires changing the PostgreSQL role and both environment files; `POSTGRES_PASSWORD` does not reset an existing database password.

For an alternate configuration directory, use `--directory /absolute/path/to/deployment` before each command. `init` copies Compose/Caddy templates there. On the same host, give each instance distinct networking/project settings before starting.

## 6. Bundled HTTPS deployment

With `MAIL_PROXY_MODE=bundled`, DNS pointing to the server, and ports 80/443 free:

```bash
python3 scripts/production.py up
python3 scripts/production.py status
curl --fail https://mail.example.com/health/ready
```

`up` checks configuration, builds a commit-tagged release image, starts PostgreSQL, stops the API for migrations, runs migrations once, and starts API/proxy. A failed migration leaves the API stopped. Containers restart with `unless-stopped`.

[Caddy obtains and renews HTTPS certificates](https://caddyserver.com/docs/automatic-https) and retains its TLS state in volumes. PostgreSQL and API have no public host ports. The API runs as non-root with a read-only root filesystem.

Open `https://mail.example.com`, sign in with the allowed identity, then connect other mailboxes through **Accounts**. A healthy container does not prove public TLS or provider authorization; verify through the real domain.

## 7. Existing host proxy

Set `MAIL_PROXY_MODE=external`, then run `up`. Bundled Caddy is skipped and the API binds only to `127.0.0.1:MAIL_BIND_PORT`, default 8740.

```bash
python3 scripts/production.py up
curl --fail http://127.0.0.1:8740/health/ready
```

For an existing host Caddy, add:

```caddyfile
mail.example.com {
    reverse_proxy 127.0.0.1:8740
}
```

For Nginx, use [nginx.conf.example](../deploy/nginx.conf.example), replacing domain, certificate paths, and upstream port. Obtain a valid certificate through the server's existing certificate process before enabling TLS, then run `nginx -t` and reload. The example overwrites `X-Forwarded-For` with the immediate client's IP and forwards the scheme.

This mode supports a proxy **on the Docker host**. A proxy in another container needs its own network attachment and exact trusted IP. When switching from bundled mode, stop the old bundled proxy first to release 80/443. Do not broaden trust to arbitrary networks.

## 8. Operation and monitoring

```bash
python3 scripts/production.py status
python3 scripts/production.py logs
python3 scripts/production.py stop
```

`stop` retains volumes. Start the same image with `up --no-build --image <existing-image-tag>`. Logs rotate to three 10 MiB files per service. Monitor `/health/live` (process) and `/health/ready` (database) through the public endpoint. Neither validates provider tokens or delivery. Restart policies restart exited processes; an unhealthy running process needs investigation.

Swagger is disabled in Production unless enabled explicitly. `/swagger` and `/openapi/v1.json` then retain their usual endpoint authentication. **Try it out** can change live mailbox state.

## 9. Backup and restore

```bash
python3 scripts/production.py backup
```

This writes a `0600` archive under `deploy/backups`: consistent PostgreSQL custom-format dump, revision/image manifest, public settings, provider credentials, Microsoft certificate, and **encryption keys**. Copy it to protected off-server storage and encrypt it using your backup system. Caddy-issued TLS data is not included and can be issued again.

Schedule backups as the operator account using an absolute script path, for example:

```cron
0 3 * * * /usr/bin/python3 /srv/clarvio-email/scripts/production.py backup
```

Configure retention/off-server replication in the backup system; the helper does not delete archives automatically.

Restore replaces database contents and leaves the API stopped:

```bash
python3 scripts/production.py restore /protected/path/backup.tar.gz --confirm-restore
```

Use a trusted archive and verify the target directory first. Existing credentials must match; otherwise use a fresh deployment with empty database volumes. The archive restores its original project/domain/network settings. The helper refuses to target an existing Compose project from an unconfigured/different deployment directory.

For a separate restore drill on the **same Docker host**, use a fresh directory and a new project name:

```bash
python3 scripts/production.py --directory /srv/clarvio-mail-restore \
  restore /protected/path/backup.tar.gz --project-name clarvio-mail-restore --confirm-restore
```

Before starting that restored app, edit the new directory's `.env` to use a different subnet and host port, and use external proxy mode or separate proxy listeners. Restore starts only its database, so these network changes can be made before `up`.

Read `manifest.json` in the archive for the matching image/revision. Check out that release and start its existing image:

```bash
python3 scripts/production.py up --no-build --image clarvio-mail:<restored-release-tag>
```

If the image is unavailable, build from the matching revision with `up --image clarvio-mail:<restored-release-tag>`. After moving servers/domains, edit restored domain settings and OAuth callbacks before starting. Recheck health and an existing mailbox read. Keep database backups and keys together.

## 10. Upgrade and rollback

Record the current commit/image, review migrations, and back up before upgrading:

```bash
python3 scripts/production.py backup
git pull --ff-only
python3 scripts/production.py up
```

Keep the old image/backup until public health, sign-in, and mailbox reads pass. Deployment includes brief migration downtime.

For a code-only rollback with a compatible schema, check out the previous revision and use `up --no-build --image <previous-image-tag>`. For incompatible migrations, restore the matching pre-upgrade backup and use its release. Database restore cannot undo provider emails/mutations; inspect unknown send outcomes before retrying.

## 11. CI and verification

[GitHub Actions](../.github/workflows/ci.yml) runs backend tests with isolated PostgreSQL, mocked browser tests, and production container tests on main pushes/PRs in either repository. It does not deploy a server or publish images.

```bash
python3 scripts/production-smoke.py
```

This builds and tests a disposable project using random loopback ports, a local test CA, and fake OAuth credentials. It checks non-root/read-only execution, HTTPS health, Swagger policy, OAuth redirect/cookie construction, backup/restore, and host-proxy mode, then removes its own containers/volumes. It does not sign in with providers or send email.

Real ACME issuance, provider consent, controlled live sends/replies/CC/BCC/attachments/drafts/moves/read flags, receiver webhook behavior, and Clarvio Agent integration must be checked during the actual rollout.

## Troubleshooting

| Problem | Action |
| --- | --- |
| Port 80/443 allocated | Use external mode or resolve the existing host listener. |
| Network overlap | Change subnet/gateway/proxy IP before creating the network. |
| Configuration check fails | Fix named fields or permissions; keep raw values without quotes. |
| Certificate rejected | Check PKCS#12/password and Microsoft public-certificate registration. |
| TLS not issued | Check DNS/AAAA, public 80/443, and Caddy logs. |
| Unhealthy app/migration failed | Inspect app/database logs; resolve the failure before retrying. |
| Callback mismatch | Match HTTPS domain/path in settings and provider registrations. |
| Login denied | Check allowlist and provider test-user/consent settings. |
| Swagger 404 | Expected until `Mail__EnableSwagger=true`. |
| Tokens cannot decrypt | Restore corresponding encryption-key versions with the database. |
