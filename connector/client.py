"""Fixed upstream, request-local credentials, bounded responses, no write retries."""

import json
import re
from html.parser import HTMLParser
from urllib.parse import quote, urlsplit

import httpx
from mcp.server.mcpserver import Context
from mcp.types import CallToolResult, TextContent

MAX_JSON_BYTES = 2 * 1024 * 1024
MAX_TEXT_CHARS = 40_000


class MailText(HTMLParser):
    """Extract display text only; never execute or return provider HTML."""
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.parts: list[str] = []
        self.hidden = 0

    def handle_starttag(self, tag, attrs):
        if tag in ("script", "style"):
            self.hidden += 1
        elif tag in ("br", "p", "div", "tr", "li") and not self.hidden:
            self.parts.append("\n")

    def handle_endtag(self, tag):
        if tag in ("script", "style") and self.hidden:
            self.hidden -= 1

    def handle_data(self, data):
        if not self.hidden:
            self.parts.append(data)


def part(value: object) -> str:
    return quote(str(value), safe="")


def api_url(value: str) -> str:
    url = urlsplit(value)
    if (url.scheme not in ("http", "https") or not url.hostname or url.username or url.password
            or url.query or url.fragment or url.path not in ("", "/")):
        raise ValueError("MAIL_API_URL must be the fixed Mail service origin, without a path or credentials.")
    return value.rstrip("/")


def authorization(ctx: Context) -> str:
    headers = {k.lower(): v for k, v in (ctx.headers or {}).items()}
    value = headers.get("authorization", "")
    if not re.fullmatch(r"Bearer cm_sk_[A-Za-z0-9_-]{16,190}", value):
        raise ValueError("Missing or invalid Clarvio Mail API key. Connect the account again.")
    return value


class MailError(Exception):
    def __init__(self, code: str, message: str, *, status: int | None = None, unknown: bool = False):
        self.body = {"code": code, "message": message, "status": status, "outcome_unknown": unknown}


def result(payload: object, *, error: bool = False) -> CallToolResult:
    # Mail text is untrusted data. Preserve identifiers and cursors; trim only display/body fields.
    def trim(value: object) -> object:
        if isinstance(value, list):
            return [trim(v) for v in value]
        if isinstance(value, dict):
            if not value.get("textBody") and isinstance(value.get("htmlBody"), str):
                parser = MailText()
                parser.feed(value["htmlBody"])
                value = value | {"textBody": "".join(parser.parts).strip()}
            out = {}
            shortened = []
            for key, item in value.items():
                if key.lower().replace("_", "") in {"authorization", "apikey", "accesstoken", "refreshtoken", "clientsecret", "signingsecret", "password", "tokenhash"}:
                    out[key] = "[redacted]"
                    continue
                if key == "htmlBody":
                    continue  # plain text is sufficient; do not propagate active HTML
                limit = 16_000 if key == "textBody" else 800 if key in ("preview", "subject", "name") else None
                if limit and isinstance(item, str) and len(item) > limit:
                    out[key] = item[:limit]
                    shortened.append(key)
                else:
                    out[key] = trim(item)
            if shortened:
                out["truncated_fields"] = shortened
            return out
        return value
    text = json.dumps(trim(payload), ensure_ascii=False, separators=(",", ":"))
    if len(text) > MAX_TEXT_CHARS:
        return result({"code": "result_too_large", "message": "Result exceeds 40,000 characters. Use a smaller page or narrower query."}, error=True)
    return CallToolResult(content=[TextContent(type="text", text=text)], isError=error)


class MailClient:
    def __init__(self, base_url: str, *, transport: httpx.AsyncBaseTransport | None = None):
        self.base_url = api_url(base_url)
        self.transport = transport

    async def request(self, ctx: Context, method: str, path: str, *, params: dict | None = None,
                      body: dict | None = None, idempotency_key: str | None = None,
                      max_bytes: int = MAX_JSON_BYTES, binary: bool = False):
        key = authorization(ctx)
        headers = {"Authorization": key, "Accept": "application/octet-stream" if binary else "application/json"}
        if idempotency_key:
            headers["Idempotency-Key"] = idempotency_key
        # A new upstream client for this request: no cookie jar, credential cache or cross-account state.
        try:
            async with httpx.AsyncClient(transport=self.transport, timeout=httpx.Timeout(120, connect=5),
                                         follow_redirects=False, trust_env=False) as http:
                async with http.stream(method, self.base_url + "/api/v1" + path, headers=headers, params=params,
                                       json=body) as response:
                    chunks = bytearray()
                    async for chunk in response.aiter_bytes():
                        if len(chunks) + len(chunk) > max_bytes:
                            raise MailError("response_too_large", "Mail response exceeds the supported size.", unknown=method != "GET")
                        chunks.extend(chunk)
                    if not response.is_success:
                        code = {401: "invalid_credentials", 403: "scope_denied", 404: "not_found",
                                409: "conflict", 413: "attachment_too_large", 429: "rate_limited"}.get(response.status_code, "mail_api_error")
                        # Never reflect upstream HTML, tokens or provider diagnostics.
                        raise MailError(code, "Mail rejected the request. Check the API key scopes, resource IDs and send operation.",
                                        status=response.status_code, unknown=method != "GET" and response.status_code >= 500)
                    if binary:
                        return bytes(chunks), response.headers
                    if not chunks:
                        return {"ok": True}
                    try:
                        # Never expose the request credential, even if a future API accidentally reflects it.
                        return json.loads(chunks.decode("utf-8").replace(key.removeprefix("Bearer "), "[redacted]"))
                    except (ValueError, UnicodeError):
                        raise MailError("invalid_response", "Mail returned an invalid response.", unknown=method != "GET") from None
        except httpx.HTTPError:
            raise MailError("mail_unavailable", "Mail did not respond. For a send, check the operation or reuse the same idempotency key; never resend with a new key.",
                            unknown=method != "GET") from None
