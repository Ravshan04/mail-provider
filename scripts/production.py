#!/usr/bin/env python3
"""Configure and operate the Clarvio Mail production Compose deployment."""
import argparse
import base64
from datetime import datetime, timezone
import getpass
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import subprocess
import sys
import tarfile
import tempfile

ROOT = Path(__file__).resolve().parents[1]
DOMAIN = re.compile(r"(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$")


def read_env(path):
    result = {}
    for line in path.read_text().splitlines():
        if not line or line.startswith("#"):
            continue
        key, separator, value = line.partition("=")
        if not separator or not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", key) or key in result:
            raise ValueError(f"Invalid environment file: {path.name}")
        result[key] = value
    return result


def write_private(path, content, mode=0o600):
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, mode)
    with os.fdopen(descriptor, "wb") as stream:
        stream.write(content if isinstance(content, bytes) else content.encode())
    os.chmod(path, mode)


def encode_env(values):
    if any("\n" in str(value) or "\r" in str(value) or "\0" in str(value) for value in values.values()):
        raise ValueError("Environment values must be single-line strings.")
    return "".join(f"{key}={value}\n" for key, value in values.items())


def revision():
    return subprocess.check_output(["git", "rev-parse", "--short=12", "HEAD"], cwd=ROOT, text=True).strip()


class Deployment:
    def __init__(self, directory):
        self.directory = directory.resolve()
        self.secret_dir = self.directory / "secrets"

    def compose(self, *arguments, **kwargs):
        public = read_env(self.directory / ".env")
        command = ["docker", "compose", "--project-directory", str(self.directory),
                   "--env-file", str(self.directory / ".env"), "-f", str(self.directory / "compose.yaml")]
        if public.get("MAIL_PROXY_MODE") == "external":
            command += ["-f", str(self.directory / "compose.external-proxy.yaml")]
        env = os.environ.copy()
        # Do not let unrelated shell variables override this deployment's public settings.
        for key in public:
            env.pop(key, None)
        env["MAIL_BUILD_CONTEXT"] = str(ROOT)
        return subprocess.run(command + list(arguments), cwd=ROOT, env=env, check=True, **kwargs)

    def initialize(self):
        if (self.directory / ".env").exists() or self.secret_dir.exists():
            raise ValueError("Configuration already exists. Edit protected settings or restore a backup; do not regenerate keys.")
        domain = input("Public domain (example: mail.example.com): ").strip().lower()
        if not DOMAIN.fullmatch(domain):
            raise ValueError("Enter a DNS hostname without a scheme, path, or port.")
        email = input("Invited login / certificate contact email: ").strip()
        if not re.fullmatch(r"[A-Za-z0-9._+%\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", email):
            raise ValueError("Enter a valid email address.")
        mode = input("Proxy: bundled Caddy or existing host proxy [bundled/external]: ").strip() or "bundled"
        if mode not in ("bundled", "external"):
            raise ValueError("Proxy mode must be bundled or external.")
        google_id = input("Google Web client ID (blank disables Gmail): ").strip()
        google_secret = getpass.getpass("Google client secret: ") if google_id else ""
        microsoft_id = input("Microsoft application ID (blank disables Outlook): ").strip()
        certificate = b""
        certificate_password = ""
        if microsoft_id:
            if not re.fullmatch(r"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}", microsoft_id):
                raise ValueError("Microsoft application ID must be a UUID.")
            source = Path(input("Private Microsoft PKCS#12 (.pfx) file path: ").strip()).expanduser()
            certificate = source.read_bytes()
            certificate_password = getpass.getpass("Certificate password: ")
        if not google_id and not microsoft_id:
            raise ValueError("Configure at least one provider.")
        if google_id and not google_secret:
            raise ValueError("Google client secret is required.")
        password = secrets.token_urlsafe(32)
        app = {
            "ASPNETCORE_ENVIRONMENT": "Production", "ASPNETCORE_HTTP_PORTS": "8740",
            "AllowedHosts": f"{domain};localhost;127.0.0.1",
            "Mail__PublicUrl": "https://" + domain,
            "Mail__Database": f"Host=db;Port=5432;Database=clarvio_mail;Username=clarvio_mail;Password={password}",
            "Mail__ActiveKey": "v1", "Mail__EncryptionKeys__v1": base64.b64encode(secrets.token_bytes(32)).decode(),
            "Mail__AllowSignup": "false", "Mail__AllowedLoginEmails__0": email,
            "Mail__Google__ClientId": google_id, "Mail__Google__ClientSecret": google_secret,
            "Mail__Microsoft__ClientId": microsoft_id, "Mail__Microsoft__Tenant": "common",
            "Mail__Microsoft__CertificatePath": "/run/secrets/microsoft.pfx" if microsoft_id else "",
            "Mail__Microsoft__CertificatePassword": certificate_password,
            "Mail__EnableSwagger": "false", "Mail__PollSeconds": "60",
        }
        public = read_env(ROOT / "deploy/.env.example")
        public.update(MAIL_DOMAIN=domain, ACME_EMAIL=email, MAIL_PROXY_MODE=mode, MAIL_IMAGE="clarvio-mail:" + revision())
        # Validate values before writing any secrets.
        app_text = encode_env(app)
        public_text = encode_env(public)
        self.directory.mkdir(parents=True, exist_ok=True)
        self.secret_dir.mkdir(mode=0o700)
        for filename in ("compose.yaml", "compose.external-proxy.yaml", "Caddyfile"):
            source = ROOT / "deploy" / filename
            target = self.directory / filename
            if source.resolve() != target.resolve():
                shutil.copyfile(source, target)
        write_private(self.directory / ".env", public_text)
        write_private(self.secret_dir / "app.env", app_text)
        write_private(self.secret_dir / "postgres.env", encode_env({
            "POSTGRES_DB": "clarvio_mail", "POSTGRES_USER": "clarvio_mail", "POSTGRES_PASSWORD": password,
        }))
        # Parent directory is 0700; only this file is mounted into the non-root container.
        write_private(self.secret_dir / "microsoft.pfx", certificate, 0o644)
        self.check()
        print("Production configuration saved privately. Register HTTPS callbacks, then run: production.py up")

    def check(self):
        public = read_env(self.directory / ".env")
        app = read_env(self.secret_dir / "app.env")
        postgres = read_env(self.secret_dir / "postgres.env")
        if self.secret_dir.stat().st_mode & 0o077:
            raise ValueError("The secrets directory must have permissions 0700.")
        for path in (self.directory / ".env", self.secret_dir / "app.env", self.secret_dir / "postgres.env"):
            if path.stat().st_mode & 0o077:
                raise ValueError(f"{path.name} must have permissions 0600.")
        domain = public.get("MAIL_DOMAIN", "")
        if not DOMAIN.fullmatch(domain) or app.get("Mail__PublicUrl") != "https://" + domain:
            raise ValueError("MAIL_DOMAIN and Mail__PublicUrl must match the HTTPS origin.")
        if public.get("MAIL_PROXY_MODE") not in ("bundled", "external"):
            raise ValueError("MAIL_PROXY_MODE must be bundled or external.")
        network = ipaddress.ip_network(public["MAIL_PROXY_SUBNET"])
        for key in ("MAIL_PROXY_IP", "MAIL_PROXY_GATEWAY", "MAIL_APP_IP"):
            address = ipaddress.ip_address(public[key])
            if address not in network or address in (network.network_address, network.broadcast_address):
                raise ValueError("Proxy addresses must be usable addresses inside the configured subnet.")
        if len({public[key] for key in ("MAIL_PROXY_IP", "MAIL_PROXY_GATEWAY", "MAIL_APP_IP")}) != 3:
            raise ValueError("The proxy, app and gateway need different addresses.")
        if not re.fullmatch(r"[a-z0-9][a-z0-9_-]*", public["COMPOSE_PROJECT_NAME"]):
            raise ValueError("Invalid Compose project name.")
        if not 1 <= int(public["MAIL_BIND_PORT"]) <= 65535:
            raise ValueError("Invalid API bind port.")
        if app.get("ASPNETCORE_ENVIRONMENT") != "Production" or int(app.get("Mail__PollSeconds", "60")) < 15:
            raise ValueError("Production environment and a poll interval of at least 15 seconds are required.")
        active = app["Mail__ActiveKey"]
        if "." in active or len(base64.b64decode(app["Mail__EncryptionKeys__" + active], validate=True)) != 32:
            raise ValueError("The active encryption key must be 32 bytes encoded as base64.")
        password = postgres["POSTGRES_PASSWORD"]
        if not re.fullmatch(r"[A-Za-z0-9_-]{20,}", password):
            raise ValueError("Database password must be a generated URL-safe value.")
        expected = f"Host=db;Port=5432;Database=clarvio_mail;Username=clarvio_mail;Password={password}"
        if app.get("Mail__Database") != expected or postgres.get("POSTGRES_USER") != "clarvio_mail" or postgres.get("POSTGRES_DB") != "clarvio_mail":
            raise ValueError("Application and database configuration do not match.")
        if app.get("Mail__Google__ClientId") and not app.get("Mail__Google__ClientSecret"):
            raise ValueError("Google client secret is missing.")
        if app.get("Mail__Microsoft__ClientId"):
            if app.get("Mail__Microsoft__CertificatePath") != "/run/secrets/microsoft.pfx":
                raise ValueError("Microsoft certificate path must match the mounted certificate.")
            env = os.environ.copy()
            env["CLARVIO_CERTIFICATE_PASSWORD"] = app.get("Mail__Microsoft__CertificatePassword", "")
            result = subprocess.run(["openssl", "pkcs12", "-in", str(self.secret_dir / "microsoft.pfx"),
                                     "-passin", "env:CLARVIO_CERTIFICATE_PASSWORD", "-noout"],
                                    env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            if result.returncode:
                raise ValueError("Microsoft PKCS#12 certificate or password is invalid.")
        if not (self.secret_dir / "microsoft.pfx").is_file():
            raise ValueError("The certificate mount file is missing.")
        self.compose("config", "--quiet")
        print("Production configuration format and Compose configuration are valid. Provider authorization is not checked.")

    def up(self, image=None, no_build=False):
        self.check()
        public = read_env(self.directory / ".env")
        public["MAIL_IMAGE"] = image or "clarvio-mail:" + revision()
        write_private(self.directory / ".env", encode_env(public))
        if not no_build:
            self.compose("build", "app")
        self.compose("up", "-d", "--wait", "db")
        self.compose("stop", "app")
        self.compose("run", "--rm", "--no-deps", "migrate")
        services = ["app"] + (["proxy"] if public["MAIL_PROXY_MODE"] == "bundled" else [])
        self.compose("up", "-d", "--wait", *services)
        print("Application is healthy. Verify the public HTTPS endpoint and OAuth sign-in next.")

    def backup(self):
        self.check()
        directory = self.directory / "backups"
        directory.mkdir(mode=0o700, exist_ok=True)
        os.chmod(directory, 0o700)
        target = directory / (datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ") + ".tar.gz")
        with tempfile.TemporaryDirectory(dir=directory) as temp:
            dump = Path(temp) / "database.dump"
            with dump.open("wb") as stream:
                self.compose("exec", "-T", "db", "pg_dump", "-U", "clarvio_mail", "-d", "clarvio_mail", "-Fc", stdout=stream)
            manifest = Path(temp) / "manifest.json"
            manifest.write_text(json.dumps({"revision": revision(), "image": read_env(self.directory / ".env")["MAIL_IMAGE"], "format": 1}))
            write_private(target, b"")
            try:
                with tarfile.open(target, "w:gz") as archive:
                    archive.add(dump, arcname="database.dump")
                    archive.add(manifest, arcname="manifest.json")
                    archive.add(self.directory / ".env", arcname="deployment.env")
                    for filename in ("app.env", "postgres.env", "microsoft.pfx"):
                        archive.add(self.secret_dir / filename, arcname=filename)
            except Exception:
                target.unlink(missing_ok=True)
                raise
        print(f"Backup saved: {target}. It contains private credentials and encryption keys.")
        return target

    def restore(self, source, project_name=None):
        expected = {"database.dump", "manifest.json", "deployment.env", "app.env", "postgres.env", "microsoft.pfx"}
        with tempfile.TemporaryDirectory() as temp:
            with tarfile.open(source, "r:gz") as archive:
                members = archive.getmembers()
                if len(members) != len(expected) or {member.name for member in members} != expected or any(not member.isfile() for member in members):
                    raise ValueError("Backup archive has unexpected members.")
                archive.extractall(temp, filter="data")
            temporary = Path(temp)
            metadata = json.loads((temporary / "manifest.json").read_text())
            if metadata.get("format") != 1:
                raise ValueError("Unsupported backup format.")
            restored_public = read_env(temporary / "deployment.env")
            if project_name:
                if not re.fullmatch(r"[a-z0-9][a-z0-9_-]*", project_name):
                    raise ValueError("Invalid restore project name.")
                restored_public["COMPOSE_PROJECT_NAME"] = project_name
            project = restored_public["COMPOSE_PROJECT_NAME"]
            current_public = read_env(self.directory / ".env") if (self.directory / ".env").exists() else {}
            containers = subprocess.check_output(["docker", "ps", "-aq", "--filter", "label=com.docker.compose.project=" + project], text=True).strip()
            volumes = subprocess.check_output(["docker", "volume", "ls", "-q", "--filter", "label=com.docker.compose.project=" + project], text=True).strip()
            if (containers or volumes) and current_public.get("COMPOSE_PROJECT_NAME") != project:
                raise ValueError("That Compose project already exists outside this deployment directory. Choose a new --project-name.")
            if (self.secret_dir / "postgres.env").exists():
                if read_env(self.secret_dir / "postgres.env") != read_env(temporary / "postgres.env"):
                    raise ValueError("Restore requires the same database credentials or a fresh deployment directory and database volume.")
            self.directory.mkdir(parents=True, exist_ok=True)
            self.secret_dir.mkdir(mode=0o700, exist_ok=True)
            os.chmod(self.secret_dir, 0o700)
            for filename in ("compose.yaml", "compose.external-proxy.yaml", "Caddyfile"):
                source_file = ROOT / "deploy" / filename
                target = self.directory / filename
                if source_file.resolve() != target.resolve():
                    shutil.copyfile(source_file, target)
            write_private(self.directory / ".env", encode_env(restored_public))
            for filename in ("app.env", "postgres.env", "microsoft.pfx"):
                write_private(self.secret_dir / filename, (temporary / filename).read_bytes(), 0o644 if filename.endswith(".pfx") else 0o600)
            self.check()
            self.compose("stop", "app")
            self.compose("up", "-d", "--wait", "db")
            with (temporary / "database.dump").open("rb") as stream:
                self.compose("exec", "-T", "db", "pg_restore", "-U", "clarvio_mail", "-d", "clarvio_mail",
                             "--clean", "--if-exists", "--exit-on-error", stdin=stream)
        print("Database and protected settings restored; app remains stopped. Use the matching release before running up.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, default=ROOT / "deploy")
    commands = parser.add_subparsers(dest="command", required=True)
    for name in ("init", "check", "status", "logs", "stop", "backup"):
        commands.add_parser(name)
    up = commands.add_parser("up")
    up.add_argument("--image")
    up.add_argument("--no-build", action="store_true")
    restore = commands.add_parser("restore")
    restore.add_argument("archive", type=Path)
    restore.add_argument("--confirm-restore", action="store_true", help="Allow replacing the target database and private settings")
    restore.add_argument("--project-name", help="Use a distinct Compose project for a separate restore target")
    args = parser.parse_args()
    deployment = Deployment(args.directory)
    if args.command == "init":
        deployment.initialize()
    elif args.command == "check":
        deployment.check()
    elif args.command == "up":
        deployment.up(args.image, args.no_build)
    elif args.command == "backup":
        deployment.backup()
    elif args.command == "restore":
        if not args.confirm_restore:
            raise ValueError("Restore replaces database contents; supply --confirm-restore for the intended target.")
        deployment.restore(args.archive.resolve(), args.project_name)
    else:
        arguments = {"status": ("ps",), "logs": ("logs", "--tail", "100", "app", "db", "proxy"), "stop": ("stop",)}[args.command]
        deployment.compose(*arguments)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, OSError, subprocess.CalledProcessError, EOFError) as error:
        if isinstance(error, ValueError):
            print(str(error), file=sys.stderr)
        else:
            print("Production command failed. Check the preceding output and protected file paths; credentials were not printed.", file=sys.stderr)
        raise SystemExit(1)
