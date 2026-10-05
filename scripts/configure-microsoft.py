#!/usr/bin/env python3
"""Store Microsoft OAuth credentials locally without printing the secret."""
import argparse
import base64
import getpass
import json
import os
from pathlib import Path
import re
import secrets
import sys
import tempfile
import uuid

ROOT = Path(__file__).resolve().parents[1]
CONFIG = ROOT / ".local" / "settings.json"


def atomic_private_write(path: Path, content: str) -> None:
    descriptor, temporary = tempfile.mkstemp(prefix=".settings-", dir=path.parent)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Check presence and format; never display credentials.")
    args = parser.parse_args()
    if args.check:
        if not CONFIG.is_file():
            print("Microsoft sozlamalari hali kiritilmagan.")
            return 1
        data = json.loads(CONFIG.read_text(encoding="utf-8"))
        microsoft = data.get("Mail", {}).get("Microsoft", {})
        try:
            uuid.UUID(microsoft.get("ClientId", ""))
        except ValueError:
            print("Application (client) ID formati noto'g'ri.")
            return 1
        if not microsoft.get("ClientSecret", "").strip():
            print("Client secret kiritilmagan.")
            return 1
        print("Client ID va secret lokal faylda mavjud. Azure bilan ulanish hali tekshirilmagan.")
        return 0
    if not sys.stdin.isatty():
        print("Bu buyruqni o'zingizning terminalingizda ishga tushiring.", file=sys.stderr)
        return 1
    client_id = input("Overview -> Application (client) ID: ").strip()
    try:
        client_id = str(uuid.UUID(client_id))
    except ValueError:
        print("Client ID GUID formatida bo'lishi kerak. Object ID emas, Application (client) ID ni oling.", file=sys.stderr)
        return 1
    secret = getpass.getpass("Client secret VALUE (ekranda ko'rinmaydi): ").strip()
    if not secret or any(ord(character) < 32 for character in secret):
        print("Client secret VALUE bo'sh yoki noto'g'ri.", file=sys.stderr)
        return 1
    email = input("Sinovda ulanadigan Outlook / Microsoft 365 email: ").strip()
    if len(email) > 254 or not re.fullmatch(r"[^\s@]+@[^\s@]+\.[^\s@]+", email):
        print("Email manzilini to'g'ri kiriting.", file=sys.stderr)
        return 1
    CONFIG.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    os.chmod(CONFIG.parent, 0o700)
    data = json.loads(CONFIG.read_text(encoding="utf-8")) if CONFIG.exists() else {"Mail": {}}
    mail = data.setdefault("Mail", {})
    mail.setdefault("PublicUrl", "http://localhost:8740")
    mail.setdefault("Database", "Host=127.0.0.1;Port=5548;Database=clarvio_mail;Username=clarvio_mail;Password=" + secrets.token_urlsafe(32))
    # Preserve the existing key: replacing it would make connected accounts unreadable.
    if not mail.get("EncryptionKeys"):
        mail["ActiveKey"] = "v1"
        mail["EncryptionKeys"] = {"v1": base64.b64encode(secrets.token_bytes(32)).decode("ascii")}
    mail["Microsoft"] = {"ClientId": client_id, "ClientSecret": secret, "Tenant": "common"}
    mail["AllowSignup"] = False
    allowed = mail.setdefault("AllowedLoginEmails", [])
    if email.casefold() not in [value.casefold() for value in allowed]:
        allowed.append(email)
    atomic_private_write(CONFIG, json.dumps(data, indent=2) + "\n")
    print("Saqlandi: " + str(CONFIG))
    print("Secret ekranga chiqarilmadi; fayl faqat sizga ochiq (0600) va .gitignore ichida.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (KeyboardInterrupt, EOFError):
        print("\nBekor qilindi. Credential saqlanmadi.", file=sys.stderr)
        raise SystemExit(1)
    except (OSError, json.JSONDecodeError):
        print("Lokal sozlama faylini o'qish yoki saqlash imkoni bo'lmadi.", file=sys.stderr)
        raise SystemExit(1)
