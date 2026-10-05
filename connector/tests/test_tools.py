import asyncio
import base64
import json
from types import SimpleNamespace

import httpx
import pytest

from client import MAX_TEXT_CHARS, MailClient, MailError, api_url, result
from models import MAX_FILE_BYTES
from server import create_server

BOX = "8b250276-3c5e-4b69-bd86-dcc69b350016"
OP = "fb27f835-965a-40b7-b864-d72b5dd68419"
KEY = "cm_sk_" + "a" * 43
CTX = SimpleNamespace(headers={"authorization": "Bearer " + KEY, "x-connection-id": OP})
MESSAGE = {"to": [{"email": "to@example.com"}], "subject": "Subject", "text_body": "Body",
           "cc": [{"email": "cc@example.com"}], "bcc": [{"email": "hidden@example.com"}],
           "attachments": [{"name": "a.txt", "content_type": "text/plain", "content_base64": "SGVsbG8="}]}

CASES = [
    ("whoami", {}, "GET", "/mailboxes"),
    ("list_mailboxes", {}, "GET", "/mailboxes"),
    ("list_folders", {"mailbox_id": BOX}, "GET", f"/mailboxes/{BOX}/folders"),
    ("list_messages", {"mailbox_id": BOX, "query": "invoice", "cursor": "cursor", "limit": 3}, "GET", f"/mailboxes/{BOX}/messages"),
    ("get_message", {"mailbox_id": BOX, "message_id": "message"}, "GET", f"/mailboxes/{BOX}/messages/message"),
    ("download_attachment", {"mailbox_id": BOX, "message_id": "message", "attachment_id": "file"}, "GET", f"/mailboxes/{BOX}/messages/message/attachments/file"),
    ("mark_read", {"mailbox_id": BOX, "message_id": "message", "is_read": False}, "PATCH", f"/mailboxes/{BOX}/messages/message/read"),
    ("move_message", {"mailbox_id": BOX, "message_id": "message", "destination_folder_id": "archive", "source_folder_id": "inbox"}, "POST", f"/mailboxes/{BOX}/messages/message/move"),
    ("trash_message", {"mailbox_id": BOX, "message_id": "message"}, "DELETE", f"/mailboxes/{BOX}/messages/message"),
    ("get_draft", {"mailbox_id": BOX, "draft_id": "draft"}, "GET", f"/mailboxes/{BOX}/drafts/draft"),
    ("create_draft", {"mailbox_id": BOX, "message": MESSAGE, "reply_message_id": "original"}, "POST", f"/mailboxes/{BOX}/drafts"),
    ("update_draft", {"mailbox_id": BOX, "draft_id": "draft", "message": MESSAGE, "keep_attachment_ids": []}, "PUT", f"/mailboxes/{BOX}/drafts/draft"),
    ("delete_draft", {"mailbox_id": BOX, "draft_id": "draft"}, "DELETE", f"/mailboxes/{BOX}/drafts/draft"),
    ("send_message", {"mailbox_id": BOX, "message": MESSAGE, "idempotency_key": "intended-send-001"}, "POST", f"/mailboxes/{BOX}/messages"),
    ("reply_message", {"mailbox_id": BOX, "message_id": "message", "message": MESSAGE, "idempotency_key": "intended-send-002"}, "POST", f"/mailboxes/{BOX}/messages/message/reply"),
    ("send_draft", {"mailbox_id": BOX, "draft_id": "draft", "idempotency_key": "intended-send-003"}, "POST", f"/mailboxes/{BOX}/drafts/draft/send"),
    ("get_send_operation", {"operation_id": OP}, "GET", f"/operations/{OP}"),
]


@pytest.mark.parametrize("name,args,method,path", CASES)
async def test_every_tool_maps_to_scoped_mail_api(name, args, method, path):
    requests = []

    def handler(req):
        requests.append(req)
        assert req.method == method and req.url.path == "/api/v1" + path
        assert req.headers["authorization"] == "Bearer " + KEY
        assert "cookie" not in req.headers and "x-connection-id" not in req.headers
        if name == "download_attachment":
            return httpx.Response(200, content=b"Hello", headers={"Content-Disposition": "attachment; filename*=UTF-8''invoice%20one.txt", "Content-Type": "text/plain"})
        if name == "list_messages":
            assert dict(req.url.params) == {"folder": "inbox", "query": "invoice", "cursor": "cursor", "limit": "3"}
        if method not in ("GET", "DELETE") and name != "send_draft":
            body = json.loads(req.content)
            if name in ("create_draft", "update_draft"):
                assert body.get("replyMessageId") == args.get("reply_message_id")
                assert body.get("keepAttachmentIds") == args.get("keep_attachment_ids")
                body = body["message"]
            if "message" in args:
                assert body["textBody"] == "Body" and body["cc"][0]["email"] == "cc@example.com"
                assert body["bcc"][0]["email"] == "hidden@example.com"
                assert body["attachments"][0]["contentBase64"] == "SGVsbG8="
            if name == "mark_read":
                assert body == {"isRead": False}
            if name == "move_message":
                assert body == {"destinationFolderId": "archive", "sourceFolderId": "inbox"}
        if name in ("send_message", "reply_message", "send_draft"):
            assert req.headers["Idempotency-Key"] == args["idempotency_key"]
        return httpx.Response(204) if method in ("DELETE", "PATCH") else httpx.Response(200, json={"id": OP, "status": "accepted"})

    server = create_server("http://mail.test", transport=httpx.MockTransport(handler))
    response = await server.call_tool(name, args, context=CTX)
    assert not response.is_error
    assert len(requests) == 1
    if name == "download_attachment":
        assert len(response.content) == 2
        assert response.content[1].resource.uri == "file:///invoice%20one.txt"
        assert base64.b64decode(response.content[1].resource.blob) == b"Hello"
        assert "SGVsbG8=" not in response.content[0].text


async def test_catalog_hints_protect_every_write_and_destructive_tool():
    catalog = {t.name: t for t in await create_server("http://mail.test").list_tools()}
    assert set(catalog) == {case[0] for case in CASES}
    for name, _, method, _ in CASES:
        hints = catalog[name].annotations
        assert hints.title
        assert hints.read_only_hint == (method == "GET")
        assert hints.destructive_hint == (name in ("trash_message", "delete_draft"))
    assert catalog["send_message"].annotations.idempotent_hint
    schema = json.dumps(catalog["send_message"].input_schema)
    assert "content_base64" in schema and "ctx" not in catalog["send_message"].input_schema["properties"]


async def test_concurrent_accounts_never_share_credentials_or_cookies():
    seen = []

    async def handler(req):
        await asyncio.sleep(0)
        token = req.headers["authorization"]
        seen.append(token)
        return httpx.Response(200, json=[{"id": BOX, "email": token[-1] + "@example.com"}], headers={"set-cookie": "session=untrusted"})

    server = create_server("http://mail.test", transport=httpx.MockTransport(handler))
    contexts = [SimpleNamespace(headers={"authorization": "Bearer cm_sk_" + char * 43}) for char in ("a", "b")]
    responses = await asyncio.gather(*(server.call_tool("whoami", {}, context=ctx) for ctx in contexts))
    assert json.loads(responses[0].content[0].text)[0]["email"] == "a@example.com"
    assert json.loads(responses[1].content[0].text)[0]["email"] == "b@example.com"
    assert len(set(seen)) == 2


@pytest.mark.parametrize("status", [401, 403, 404, 409, 429, 503])
async def test_mail_errors_are_redacted_and_writes_are_never_retried(status):
    seen = []

    def handler(req):
        seen.append(req)
        return httpx.Response(status, text=f"private upstream diagnostic {KEY}")

    server = create_server("http://mail.test", transport=httpx.MockTransport(handler))
    response = await server.call_tool("send_draft", {"mailbox_id": BOX, "draft_id": "draft", "idempotency_key": "intended-send-003"}, context=CTX)
    assert response.is_error and len(seen) == 1
    assert KEY not in response.content[0].text and "private upstream" not in response.content[0].text
    assert json.loads(response.content[0].text)["status"] == status


async def test_write_timeout_is_unknown_and_not_retried():
    seen = []

    def handler(req):
        seen.append(req)
        raise httpx.ReadTimeout("secret diagnostic", request=req)

    server = create_server("http://mail.test", transport=httpx.MockTransport(handler))
    response = await server.call_tool("send_draft", {"mailbox_id": BOX, "draft_id": "draft", "idempotency_key": "intended-send-003"}, context=CTX)
    assert response.is_error and len(seen) == 1
    assert json.loads(response.content[0].text)["outcome_unknown"] is True


@pytest.mark.parametrize("headers", [{}, {"authorization": "Bearer wrong"}, {"cookie": "cm_session=anything"}])
async def test_bad_or_missing_credentials_fail_before_network(headers):
    server = create_server("http://mail.test", transport=httpx.MockTransport(lambda _: pytest.fail("Must not call Mail")))
    response = await server.call_tool("whoami", {}, context=SimpleNamespace(headers=headers))
    assert response.is_error


@pytest.mark.parametrize("change", [
    {"attachments": [{"name": "../unsafe", "content_base64": "SGVsbG8="}]},
    {"attachments": [{"name": "file", "content_base64": "not-base64"}]},
    {"subject": "Bad\r\nsubject"}, {"to": []}, {"to": [{"email": "bad@example.com\r\nInjected"}]},
])
async def test_invalid_message_is_rejected_before_upstream(change):
    server = create_server("http://mail.test", transport=httpx.MockTransport(lambda _: pytest.fail("Must not call Mail")))
    response = await server.call_tool("send_message", {"mailbox_id": BOX, "message": MESSAGE | change,
                                                       "idempotency_key": "intended-send-001"}, context=CTX)
    assert response.is_error


async def test_ten_mib_file_is_accepted_and_excess_is_rejected():
    seen = []

    def handler(req):
        seen.append(req)
        assert len(base64.b64decode(json.loads(req.content)["attachments"][0]["contentBase64"])) == MAX_FILE_BYTES
        return httpx.Response(200, json={"id": OP, "status": "accepted"})

    server = create_server("http://mail.test", transport=httpx.MockTransport(handler))
    message = MESSAGE | {"attachments": [{"name": "large.bin", "content_base64": base64.b64encode(b"a" * MAX_FILE_BYTES).decode()}]}
    args = {"mailbox_id": BOX, "message": message, "idempotency_key": "intended-send-001"}
    assert not (await server.call_tool("send_message", args, context=CTX)).is_error
    message["attachments"][0]["content_base64"] = base64.b64encode(b"a" * (MAX_FILE_BYTES + 1)).decode()
    assert (await server.call_tool("send_message", args, context=CTX)).is_error
    assert len(seen) == 1


async def test_download_limit_and_non_json_upstream_are_bounded():
    client = MailClient("http://mail.test", transport=httpx.MockTransport(lambda _: httpx.Response(200, content=b"a" * 101)))
    with pytest.raises(MailError):
        await client.request(CTX, "GET", "/file", binary=True, max_bytes=100)
    client.transport = httpx.MockTransport(lambda _: httpx.Response(200, text="not json"))
    with pytest.raises(MailError) as failure:
        await client.request(CTX, "POST", "/send")
    assert failure.value.body["outcome_unknown"]


def test_output_cap_preserves_ids_cursor_and_marks_truncated_email_text():
    data = {"id": "unchanged", "nextCursor": "opaque", "textBody": "x" * 100_000, "htmlBody": "<script>bad</script>"}
    output = json.loads(result(data).content[0].text)
    assert output["id"] == "unchanged" and output["nextCursor"] == "opaque"
    assert output["truncated_fields"] == ["textBody"] and "htmlBody" not in output
    response = result({"data": "x" * (MAX_TEXT_CHARS + 1)})
    assert response.is_error and len(response.content[0].text) < 1000


def test_html_only_messages_become_plain_text_without_active_markup():
    response = result({"textBody": None, "htmlBody": "<style>hidden</style><p>Hello &amp; welcome</p><script>bad()</script><div>Invoice</div>"})
    body = json.loads(response.content[0].text)
    assert body["textBody"] == "Hello & welcome\nInvoice" and "htmlBody" not in body


async def test_outgoing_id_is_encoded_as_one_path_segment():
    seen = []

    def handler(req):
        seen.append(req.url.raw_path)
        return httpx.Response(200, json={"id": "a/b?c#d"})

    server = create_server("http://mail.test", transport=httpx.MockTransport(handler))
    assert not (await server.call_tool("get_message", {"mailbox_id": BOX, "message_id": "a/b?c#d"}, context=CTX)).is_error
    assert seen[0].endswith(b"/messages/a%2Fb%3Fc%23d")


async def test_request_credentials_and_secret_response_fields_are_redacted():
    server = create_server("http://mail.test", transport=httpx.MockTransport(
        lambda _: httpx.Response(200, json={"textBody": "Reflected key: " + KEY, "accessToken": "another-secret"})))
    response = await server.call_tool("get_message", {"mailbox_id": BOX, "message_id": "m"}, context=CTX)
    assert KEY not in response.content[0].text and "another-secret" not in response.content[0].text


@pytest.mark.parametrize("url", ["http://u:p@host", "http://host/api", "http://host?url=evil", "file:///tmp/mail"])
def test_upstream_is_an_operator_configured_origin(url):
    with pytest.raises(ValueError):
        api_url(url)
