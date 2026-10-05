#!/usr/bin/env python3
"""Exercise production deployment in disposable containers, using fake OAuth credentials."""
import argparse
import base64
import http.client
import ipaddress
import json
import os
from pathlib import Path
import secrets
import shutil
import socket
import ssl
import subprocess
import tempfile
import time
from unittest.mock import patch

from production import Deployment, ROOT, encode_env, read_env, write_private


def free_port():
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def free_subnet():
    ids = subprocess.check_output(["docker", "network", "ls", "-q"], text=True).split()
    existing = []
    if ids:
        for network in json.loads(subprocess.check_output(["docker", "network", "inspect", *ids])):
            for config in network.get("IPAM", {}).get("Config", []) or []:
                if config.get("Subnet"):
                    existing.append(ipaddress.ip_network(config["Subnet"], strict=False))
    for number in range(32, 250):
        candidate = ipaddress.ip_network(f"172.27.{number}.0/24")
        if not any(candidate.version == network.version and candidate.overlaps(network) for network in existing):
            return candidate
    raise RuntimeError("No available smoke-test subnet.")


def run(image, skip_build):
    local = ROOT / ".local"
    local.mkdir(mode=0o700, exist_ok=True)
    directory = Path(tempfile.mkdtemp(prefix="production-smoke-", dir=local))
    deployment = Deployment(directory)
    helper = Deployment(directory / "setup-check")
    raw_secret = 'fake$smoke"quoted\'secret'
    with patch("builtins.input", side_effect=["mail.example.com", "smoke@example.com", "external", "fake-smoke-client", ""]), patch("getpass.getpass", return_value=raw_secret):
        helper.initialize()
    assert read_env(helper.secret_dir / "app.env")["Mail__Google__ClientSecret"] == raw_secret
    try:
        helper.initialize()
    except ValueError:
        pass
    else:
        raise AssertionError("Setup must not overwrite existing encryption keys")
    project = "clarvio-mail-smoke-" + secrets.token_hex(4)
    port = free_port()
    http_port = free_port()
    subnet = free_subnet()
    public = read_env(ROOT / "deploy/.env.example")
    public.update(COMPOSE_PROJECT_NAME=project, MAIL_DOMAIN="mail.example.com", ACME_EMAIL="smoke@example.com",
                  MAIL_IMAGE=image, MAIL_PROXY_SUBNET=str(subnet), MAIL_PROXY_IP=str(subnet[2]),
                  MAIL_PROXY_GATEWAY=str(subnet[1]), MAIL_APP_IP=str(subnet[3]), MAIL_BIND_PORT=str(port))
    secret_dir = directory / "secrets"
    secret_dir.mkdir(mode=0o700)
    password = secrets.token_urlsafe(32)
    app = {
        "ASPNETCORE_ENVIRONMENT": "Production", "AllowedHosts": "mail.example.com;localhost;127.0.0.1",
        "Mail__PublicUrl": "https://mail.example.com",
        "Mail__Database": f"Host=db;Port=5432;Database=clarvio_mail;Username=clarvio_mail;Password={password}",
        "Mail__ActiveKey": "v1", "Mail__EncryptionKeys__v1": base64.b64encode(secrets.token_bytes(32)).decode(),
        "Mail__AllowSignup": "false", "Mail__AllowedLoginEmails__0": "smoke@example.com",
        "Mail__Google__ClientId": "fake-smoke-client", "Mail__Google__ClientSecret": raw_secret,
        "Mail__EnableSwagger": "false", "Mail__PollSeconds": "60",
    }
    write_private(directory / ".env", encode_env(public))
    write_private(secret_dir / "app.env", encode_env(app))
    write_private(secret_dir / "postgres.env", encode_env({"POSTGRES_USER": "clarvio_mail", "POSTGRES_DB": "clarvio_mail", "POSTGRES_PASSWORD": password}))
    certificate_env = dict(os.environ, CLARVIO_SMOKE_CERTIFICATE_PASSWORD=secrets.token_urlsafe(20))
    key_file = directory / "test.key"
    cert_file = directory / "test.pem"
    subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-noenc", "-keyout", str(key_file),
                    "-out", str(cert_file), "-days", "1", "-subj", "/CN=Clarvio Mail Test"],
                   check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    subprocess.run(["openssl", "pkcs12", "-export", "-out", str(secret_dir / "microsoft.pfx"), "-inkey", str(key_file),
                    "-in", str(cert_file), "-passout", "env:CLARVIO_SMOKE_CERTIFICATE_PASSWORD"],
                   env=certificate_env, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    key_file.unlink()
    cert_file.unlink()
    (secret_dir / "microsoft.pfx").chmod(0o644)
    app.update(Mail__Microsoft__ClientId="00000000-0000-0000-0000-000000000001", Mail__Microsoft__Tenant="common",
               Mail__Microsoft__CertificatePath="/run/secrets/microsoft.pfx",
               Mail__Microsoft__CertificatePassword=certificate_env["CLARVIO_SMOKE_CERTIFICATE_PASSWORD"])
    write_private(secret_dir / "app.env", encode_env(app))
    for filename in ("compose.yaml", "compose.external-proxy.yaml"):
        shutil.copyfile(ROOT / "deploy" / filename, directory / filename)
    # Internal CA and loopback ports are used only in this disposable test.
    (directory / "Caddyfile").write_text("{\n admin off\n}\nmail.example.com {\n tls internal\n reverse_proxy app:8740\n}\n")
    override = directory / "compose.smoke.yaml"
    override.write_text(f"services:\n  proxy:\n    ports: !override ['127.0.0.1:{http_port}:80', '127.0.0.1:{port}:443']\n")
    original_compose = deployment.compose

    def compose(*arguments, **kwargs):
        # Compose accepts additional -f options before a subcommand.
        return original_compose("-f", str(override), *arguments, **kwargs)

    deployment.compose = compose
    try:
        deployment.up(image=image, no_build=skip_build)
        inspect = json.loads(subprocess.check_output(["docker", "inspect", project + "-app-1"]))[0]
        assert inspect["Config"]["User"] not in ("", "0", "root"), "Container must run as non-root"
        assert inspect["HostConfig"]["ReadonlyRootfs"], "Application root must be read-only"
        assert not inspect["HostConfig"].get("PortBindings"), "Bundled mode must not publish the API"
        assert "Mail__Google__ClientSecret=" + raw_secret in inspect["Config"]["Env"], "Compose must preserve raw secret characters"
        root_certificate = directory / "root.crt"
        subprocess.run(["docker", "cp", project + "-proxy-1:/data/caddy/pki/authorities/local/root.crt", str(root_certificate)], check=True, stdout=subprocess.DEVNULL)
        context = ssl.create_default_context(cafile=str(root_certificate))

        def request(path, tls=True, forwarded_ip=None):
            if tls:
                connection = http.client.HTTPSConnection("mail.example.com", port, context=context, timeout=10)
                connection.sock = context.wrap_socket(socket.create_connection(("127.0.0.1", port), timeout=10), server_hostname="mail.example.com")
            else:
                connection = http.client.HTTPConnection("127.0.0.1", port, timeout=10)
            request_headers = {"Host": "mail.example.com"}
            if forwarded_ip:
                request_headers.update({"X-Forwarded-For": forwarded_ip, "X-Forwarded-Proto": "https"})
            connection.request("GET", path, headers=request_headers)
            response = connection.getresponse()
            body = response.read()
            result = response.status, dict(response.getheaders()), body
            connection.close()
            return result

        # Wait for Caddy's locally issued test certificate to be ready.
        for attempt in range(20):
            try:
                status, headers, body = request("/health/ready")
                if status == 200:
                    break
            except (OSError, ssl.SSLError):
                pass
            time.sleep(1)
        else:
            raise AssertionError("HTTPS readiness failed")
        assert json.loads(body)["status"] == "ready"
        assert request("/")[0] == 200
        assert request("/swagger/index.html")[0] == 404
        assert request("/openapi/v1.json")[0] == 404
        assert request("/api/v1/mailboxes")[0] == 401
        status, headers, body = request("/auth/gmail")
        assert status == 302 and "https%3A%2F%2Fmail.example.com%2Fauth%2Fgmail%2Fcallback" in headers["Location"]
        assert "secure" in headers["Set-Cookie"].lower() and "httponly" in headers["Set-Cookie"].lower()
        assert request("/auth/outlook")[0] == 302
        deployment.compose("exec", "-T", "db", "psql", "-U", "clarvio_mail", "-d", "clarvio_mail", "-v", "ON_ERROR_STOP=1",
                           "-c", "CREATE TABLE smoke_probe(value integer); INSERT INTO smoke_probe VALUES (7);", stdout=subprocess.DEVNULL)
        backup = deployment.backup()
        collision = Deployment(directory / "collision-check")
        try:
            collision.restore(backup)
        except ValueError as error:
            assert "already exists" in str(error)
        else:
            raise AssertionError("Restore must not target another deployment's existing project")
        original_key = app["Mail__EncryptionKeys__v1"]
        deployment.compose("exec", "-T", "db", "psql", "-U", "clarvio_mail", "-d", "clarvio_mail", "-c", "DELETE FROM smoke_probe;", stdout=subprocess.DEVNULL)
        deployment.restore(backup)
        assert read_env(secret_dir / "app.env")["Mail__EncryptionKeys__v1"] == original_key
        probe = deployment.compose("exec", "-T", "db", "psql", "-U", "clarvio_mail", "-d", "clarvio_mail", "-tAc", "SELECT value FROM smoke_probe", capture_output=True, text=True)
        assert probe.stdout.strip() == "7", "Backup restore must recover the database contents"
        replica = Deployment(directory / "restore-drill")
        try:
            replica.restore(backup, project_name=project + "-restore")
            restored_probe = replica.compose("exec", "-T", "db", "psql", "-U", "clarvio_mail", "-d", "clarvio_mail", "-tAc", "SELECT value FROM smoke_probe", capture_output=True, text=True)
            assert restored_probe.stdout.strip() == "7"
        finally:
            replica.compose("down", "--volumes", "--remove-orphans")
        app["Mail__EnableSwagger"] = "true"
        write_private(secret_dir / "app.env", encode_env(app))
        deployment.up(image=image, no_build=True)
        assert request("/swagger/index.html")[0] == 200
        document = json.loads(request("/openapi/v1.json")[2])
        assert sum(len(path) for path in document["paths"].values()) == 32
        deployment.compose("stop", "proxy")
        public["MAIL_PROXY_MODE"] = "external"
        write_private(directory / ".env", encode_env(public))
        deployment.up(image=image, no_build=True)
        assert request("/health/ready", tls=False)[0] == 200
        for _ in range(20):
            assert request("/auth/gmail", tls=False, forwarded_ip="198.51.100.20")[0] == 302
        assert request("/auth/gmail", tls=False, forwarded_ip="198.51.100.20")[0] == 429
        assert request("/auth/gmail", tls=False, forwarded_ip="198.51.100.21")[0] == 302, "Host proxy must preserve separate client rate-limit partitions"
        print("PASS: setup, raw secrets, certificate validation, non-root/read-only API, HTTPS, OAuth redirects/cookie, Swagger policy, backup/restore, restore isolation, external proxy mode.")
    finally:
        try:
            deployment.compose("down", "--volumes", "--remove-orphans")
        finally:
            shutil.rmtree(directory)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image", default="clarvio-mail:smoke")
    parser.add_argument("--skip-build", action="store_true")
    args = parser.parse_args()
    run(args.image, args.skip_build)
