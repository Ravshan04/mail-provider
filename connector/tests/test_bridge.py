import hashlib
import hmac
import json
import time
from uuid import uuid4

import httpx
import pytest
from starlette.applications import Starlette
from starlette.routing import Route
from test_tools import BOX, OP

from bridge import EventBridge

BINDING = str(uuid4())
MAIL_SECRET = "fake-mail-signing-secret"
AGENT_SECRET = "fake-agent-infrastructure-secret"
EVENT = {"eventId": str(uuid4()), "type": "message.received", "mailboxId": BOX,
         "messageId": "provider-message-id", "createdAt": "2026-10-05T07:00:00Z"}


def signed(event=EVENT, *, timestamp=None, secret=MAIL_SECRET):
    raw = json.dumps(event).encode()
    timestamp = str(timestamp or int(time.time()))
    signature = hmac.new(secret.encode(), timestamp.encode() + b"." + raw, hashlib.sha256).hexdigest()
    return raw, {"X-Clarvio-Mail-Event": event["eventId"], "X-Clarvio-Mail-Signature": f"t={timestamp},v1={signature}"}


def app(handler):
    bridge = EventBridge("http://agent:8000/webhooks/connectors/clarvio_mail", AGENT_SECRET,
                         {BINDING: {"connection_id": OP, "mailbox_id": BOX, "signing_secret": MAIL_SECRET}},
                         transport=httpx.MockTransport(handler))
    return Starlette(routes=[Route("/webhooks/mail/{binding_id}", bridge.receive, methods=["POST"])])


async def test_bridge_translates_signatures_connection_and_deduplicatable_event():
    seen = []

    def handler(req):
        seen.append(req)
        expected = "sha256=" + hmac.new(AGENT_SECRET.encode(), req.content, hashlib.sha256).hexdigest()
        assert req.headers["x-signature"] == expected
        body = json.loads(req.content)
        assert body["connection_id"] == OP and body["event_id"] == EVENT["eventId"]
        assert body["event_type"] == "message.received" and body["payload"]["mailboxId"] == BOX
        assert MAIL_SECRET not in req.content.decode() and "authorization" not in req.headers
        return httpx.Response(202)

    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app(handler)), base_url="http://bridge.test") as http:
        for _ in range(2):
            raw, headers = signed()
            assert (await http.post("/webhooks/mail/" + BINDING, content=raw, headers=headers)).status_code == 202
    assert len(seen) == 2 and seen[0].content == seen[1].content


@pytest.mark.parametrize("scenario", ["bad_secret", "expired", "wrong_mailbox", "changed_body", "wrong_event_header", "unknown_binding"])
async def test_invalid_events_never_reach_agent(scenario):
    target = app(lambda _: pytest.fail("Invalid event must not reach Agent"))
    raw, headers = signed()
    path = "/webhooks/mail/" + BINDING
    if scenario == "bad_secret":
        raw, headers = signed(secret="wrong")
    elif scenario == "expired":
        raw, headers = signed(timestamp=int(time.time()) - 600)
    elif scenario == "wrong_mailbox":
        raw, headers = signed(EVENT | {"mailboxId": str(uuid4())})
    elif scenario == "changed_body":
        raw += b" "
    elif scenario == "wrong_event_header":
        headers["X-Clarvio-Mail-Event"] = str(uuid4())
    elif scenario == "unknown_binding":
        path += "missing"
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=target), base_url="http://bridge.test") as http:
        assert (await http.post(path, content=raw, headers=headers)).status_code in (400, 403, 404)


@pytest.mark.parametrize("status", [403, 404, 500, 302])
async def test_bridge_never_acknowledges_failed_agent_delivery(status):
    seen = []

    def handler(req):
        seen.append(req)
        return httpx.Response(status, headers={"Location": "http://evil.example"})

    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app(handler)), base_url="http://bridge.test") as http:
        raw, headers = signed()
        assert (await http.post("/webhooks/mail/" + BINDING, content=raw, headers=headers)).status_code == 503
    assert len(seen) == 1


async def test_bridge_returns_retryable_failure_when_agent_times_out():
    def handler(req):
        raise httpx.ReadTimeout("private diagnostic", request=req)
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app(handler)), base_url="http://bridge.test") as http:
        raw, headers = signed()
        assert (await http.post("/webhooks/mail/" + BINDING, content=raw, headers=headers)).status_code == 503


async def test_oversized_event_is_rejected_before_signature_or_delivery():
    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app(lambda _: pytest.fail("No Agent request"))), base_url="http://bridge.test") as http:
        assert (await http.post("/webhooks/mail/" + BINDING, content=b"a" * (64 * 1024 + 1))).status_code == 413
