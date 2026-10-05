"""Optional signed Mail webhook -> Clarvio Agent adapter, with persistent operator bindings.

Mail owns the retry queue. Return success only after Agent accepts/stores an event;
duplicates keep the same event_id and are deduplicated by Agent.
"""

import hashlib
import hmac
import json
import os
import re
import time
from pathlib import Path
from urllib.parse import urlsplit
from uuid import UUID

import httpx
from starlette.requests import Request
from starlette.responses import JSONResponse


class EventBridge:
    def __init__(self, agent_url: str, agent_secret: str, bindings: dict,
                 *, transport: httpx.AsyncBaseTransport | None = None):
        url = urlsplit(agent_url)
        if (url.scheme not in ("http", "https") or not url.hostname or url.username or url.password
                or url.query or url.fragment or url.path != "/webhooks/connectors/clarvio_mail"):
            raise ValueError("AGENT_WEBHOOK_URL must end in /webhooks/connectors/clarvio_mail.")
        if len(agent_secret) < 16:
            raise ValueError("The infrastructure webhook secret must be at least 16 characters.")
        for binding_id, binding in bindings.items():
            UUID(binding_id)
            UUID(binding["connection_id"])
            UUID(binding["mailbox_id"])
            if len(binding["signing_secret"]) < 16:
                raise ValueError("A Mail webhook signing secret is invalid.")
        self.agent_url, self.agent_secret, self.bindings, self.transport = agent_url, agent_secret, bindings, transport

    @classmethod
    def from_env(cls):
        path = os.environ.get("MCP_WEBHOOK_BINDINGS_FILE")
        if not path:
            return None
        # Fail closed on malformed bindings. This mounted file persists across restarts.
        bindings = json.loads(Path(path).read_text())
        secret = Path(os.environ["AGENT_WEBHOOK_SECRET_FILE"]).read_text().strip()
        return cls(os.environ["AGENT_WEBHOOK_URL"], secret, bindings)

    async def receive(self, request: Request) -> JSONResponse:
        binding = self.bindings.get(request.path_params["binding_id"])
        if not binding:
            return JSONResponse({"error": "unknown_binding"}, status_code=404)
        raw = bytearray()
        async for chunk in request.stream():
            if len(raw) + len(chunk) > 64 * 1024:
                return JSONResponse({"error": "event_too_large"}, status_code=413)
            raw.extend(chunk)
        signature = request.headers.get("x-clarvio-mail-signature", "")
        match = re.fullmatch(r"t=([0-9]{1,12}),v1=([0-9a-f]{64})", signature)
        if not match or abs(time.time() - int(match[1])) > 300:
            return JSONResponse({"error": "invalid_signature"}, status_code=403)
        expected = hmac.new(binding["signing_secret"].encode(), match[1].encode() + b"." + raw, hashlib.sha256).hexdigest()
        if not hmac.compare_digest(match[2], expected):
            return JSONResponse({"error": "invalid_signature"}, status_code=403)
        try:
            event = json.loads(raw)
            if (event["type"] != "message.received" or str(UUID(event["mailboxId"])) != binding["mailbox_id"]
                    or str(UUID(event["eventId"])) != request.headers.get("x-clarvio-mail-event")
                    or not isinstance(event["messageId"], str) or not 1 <= len(event["messageId"]) <= 2048
                    or not isinstance(event["createdAt"], str) or len(event["createdAt"]) > 64):
                raise ValueError()
        except (ValueError, KeyError, TypeError):
            return JSONResponse({"error": "invalid_event"}, status_code=400)
        body = json.dumps({"connection_id": binding["connection_id"], "event_type": event["type"],
                           "event_id": event["eventId"], "payload": {
                               "mailboxId": event["mailboxId"], "messageId": event["messageId"],
                               "createdAt": event["createdAt"],
                           }}, separators=(",", ":")).encode()
        signature = "sha256=" + hmac.new(self.agent_secret.encode(), body, hashlib.sha256).hexdigest()
        try:
            async with httpx.AsyncClient(transport=self.transport, timeout=10, follow_redirects=False,
                                         trust_env=False) as http:
                response = await http.post(self.agent_url, content=body,
                                           headers={"Content-Type": "application/json", "X-Signature": signature})
            if response.status_code == 202:
                return JSONResponse({"accepted": True}, status_code=202)
        except httpx.HTTPError:
            pass
        # Do not acknowledge loss. Mail's durable delivery queue retries, including after restart.
        return JSONResponse({"error": "agent_did_not_accept"}, status_code=503)
