#!/usr/bin/env python3
"""Store Gmail OAuth credentials without printing secrets or changing Microsoft settings."""
import argparse
import getpass
import importlib.util
import json
from pathlib import Path
import re
import sys

helper_spec = importlib.util.spec_from_file_location("clarvio_configuration", Path(__file__).with_name("configure-microsoft.py"))
helper = importlib.util.module_from_spec(helper_spec)
helper_spec.loader.exec_module(helper)
CONFIG = helper.CONFIG
atomic_private_write = helper.atomic_private_write


def client_id_valid(value):
    return isinstance(value, str) and re.fullmatch(r"[0-9]+-[A-Za-z0-9_-]+\.apps\.googleusercontent\.com", value) is not None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--credentials", type=Path, help="Import a Google Web application client JSON file.")
    parser.add_argument("--check", action="store_true", help="Check presence and format without showing credentials.")
    args = parser.parse_args()
    if not CONFIG.is_file():
        print("Avval lokal servis sozlamalarini yarating: python3 scripts/configure-microsoft.py", file=sys.stderr)
        return 1
    if not args.check and not sys.stdin.isatty():
        print("Bu buyruqni o'zingizning terminalingizda ishga tushiring.", file=sys.stderr)
        return 1
    data = json.loads(CONFIG.read_text(encoding="utf-8"))
    mail = data["Mail"]
    if args.check:
        google = mail.get("Google", {})
        if not client_id_valid(google.get("ClientId")) or not google.get("ClientSecret", "").strip():
            print("Google Client ID yoki secret hali kiritilmagan.")
            return 1
        print("Google Client ID va secret lokal faylda mavjud. Gmail bilan ulanish hali tekshirilmagan.")
        return 0
    if args.credentials:
        downloaded = json.loads(args.credentials.expanduser().read_text(encoding="utf-8"))
        if not isinstance(downloaded, dict) or not isinstance(downloaded.get("web"), dict):
            print("Web application turidagi OAuth client JSON fayli kerak.", file=sys.stderr)
            return 1
        client_id = downloaded["web"].get("client_id", "")
        secret = downloaded["web"].get("client_secret", "")
        expected_redirect = mail.get("PublicUrl", "http://localhost:8740").rstrip("/") + "/auth/gmail/callback"
        if expected_redirect not in downloaded["web"].get("redirect_uris", []):
            print("Google client Authorized redirect URIs ichiga shu manzilni qo'shing: " + expected_redirect, file=sys.stderr)
            return 1
    else:
        client_id = input("Google OAuth Client ID: ").strip()
        secret = getpass.getpass("Google Client secret (ekranda ko'rinmaydi): ").strip()
    if not client_id_valid(client_id):
        print("Google Client ID .apps.googleusercontent.com bilan tugashi kerak.", file=sys.stderr)
        return 1
    if not isinstance(secret, str) or not secret.strip() or any(ord(character) < 32 for character in secret):
        print("Google Client secret bo'sh yoki noto'g'ri.", file=sys.stderr)
        return 1
    email = input("Google Auth Platform -> Test users ichiga qo'shgan Gmail: ").strip()
    if len(email) > 254 or not re.fullmatch(r"[^\s@]+@[^\s@]+\.[^\s@]+", email):
        print("Email manzilini to'g'ri kiriting.", file=sys.stderr)
        return 1
    mail["Google"] = {"ClientId": client_id, "ClientSecret": secret.strip()}
    allowed = mail.setdefault("AllowedLoginEmails", [])
    if email.casefold() not in [value.casefold() for value in allowed]:
        allowed.append(email)
    atomic_private_write(CONFIG, json.dumps(data, indent=2) + "\n")
    print("Google sozlamalari lokal faylga saqlandi. Secret ekranga chiqarilmadi.")
    print("Servisni qayta ishga tushirish: python3 scripts/local.py restart")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (KeyboardInterrupt, EOFError):
        print("\nBekor qilindi. Credential saqlanmadi.", file=sys.stderr)
        raise SystemExit(1)
    except (OSError, ValueError, KeyError, TypeError):
        print("Credential yoki lokal sozlama faylini o'qish/saqlash imkoni bo'lmadi.", file=sys.stderr)
        raise SystemExit(1)
