#!/usr/bin/env python3
"""Run the isolated Clarvio Mail database and API on localhost:8740."""
import argparse
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import time
from urllib.error import URLError
from urllib.request import urlopen

ROOT = Path(__file__).resolve().parents[1]
LOCAL = ROOT / ".local"
CONFIG = LOCAL / "settings.json"
DLL = ROOT / "src/Clarvio.Mail/bin/Debug/net10.0/Clarvio.Mail.dll"
PID = LOCAL / "api.pid"


def run(command, env=None):
    subprocess.run(command, cwd=ROOT, env=env, check=True)


def own_pid():
    if not PID.exists():
        return None
    try:
        pid = int(PID.read_text())
        command = Path(f"/proc/{pid}/cmdline").read_bytes().split(b"\0")
        return pid if str(DLL).encode() in command else None
    except (ValueError, OSError):
        return None


def ready():
    try:
        with urlopen("http://127.0.0.1:8740/health/ready", timeout=2) as response:
            data = json.load(response)
            return data.get("service") == "clarvio-mail" and data.get("status") == "ready"
    except (URLError, TimeoutError, OSError, ValueError):
        return False


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["start", "restart", "stop", "status"])
    args = parser.parse_args()
    if args.action == "status":
        print("Clarvio Mail: " + ("http://localhost:8740 (ready)" if own_pid() and ready() else "stopped or unavailable"))
        return 0 if own_pid() and ready() else 1
    if args.action in ("stop", "restart"):
        if (pid := own_pid()):
            os.kill(pid, signal.SIGTERM)
            for _ in range(100):
                if own_pid() is None:
                    break
                time.sleep(0.1)
            if own_pid() is not None:
                print("API is still stopping; try again shortly.", file=sys.stderr)
                return 1
            PID.unlink(missing_ok=True)
        if args.action == "stop":
            run(["docker", "compose", "-f", "compose.local.yaml", "stop"])
            print("Clarvio Mail stopped. Database volume retained.")
            return 0
    if own_pid():
        print("Clarvio Mail already running. " + ("http://localhost:8740" if ready() else "Check .local/api.log"))
        return 0 if ready() else 1
    if not CONFIG.exists():
        print("First run: python3 scripts/configure-microsoft.py", file=sys.stderr)
        return 1
    configuration = json.loads(CONFIG.read_text())
    connection = configuration["Mail"]["Database"]
    match = re.fullmatch(r"Host=127\.0\.0\.1;Port=5548;Database=clarvio_mail;Username=clarvio_mail;Password=([A-Za-z0-9_-]+)", connection)
    if not match:
        print("This launcher supports the generated local database configuration only.", file=sys.stderr)
        return 1
    envfile = LOCAL / "database.env"
    descriptor = os.open(envfile, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(descriptor, "w") as stream:
        stream.write("POSTGRES_USER=clarvio_mail\nPOSTGRES_DB=clarvio_mail\nPOSTGRES_PASSWORD=" + match.group(1) + "\n")
    os.chmod(envfile, 0o600)
    env = os.environ.copy()
    env.update(ASPNETCORE_ENVIRONMENT="Development", CLARVIO_MAIL_CONFIG=str(CONFIG), ASPNETCORE_URLS="http://127.0.0.1:8740")
    run(["docker", "compose", "-f", "compose.local.yaml", "up", "-d", "--wait"], env)
    run(["dotnet", "build", "--nologo", "--verbosity", "quiet"], env)
    run(["dotnet", str(DLL), "--migrate"], env)
    logpath = LOCAL / "api.log"
    descriptor = os.open(logpath, os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o600)
    with os.fdopen(descriptor, "ab") as output:
        process = subprocess.Popen(["dotnet", str(DLL)], cwd=ROOT / "src/Clarvio.Mail", env=env,
                                   stdin=subprocess.DEVNULL, stdout=output, stderr=output, start_new_session=True)
    PID.write_text(str(process.pid))
    os.chmod(PID, 0o600)
    for _ in range(40):
        if process.poll() is not None:
            print("API failed to start. Check .local/api.log", file=sys.stderr)
            return 1
        if ready():
            print("Clarvio Mail ready: http://localhost:8740")
            return 0
        time.sleep(0.5)
    print("API is not ready. Check .local/api.log", file=sys.stderr)
    return 1


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (subprocess.CalledProcessError, OSError, ValueError, KeyError):
        print("Local startup failed; check the preceding error. Credentials were not printed.", file=sys.stderr)
        raise SystemExit(1)
