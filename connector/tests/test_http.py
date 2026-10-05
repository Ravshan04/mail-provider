import asyncio
import json
import socket
from contextlib import asynccontextmanager

import httpx
import uvicorn
from mcp import ClientSession
from mcp.client.streamable_http import create_mcp_http_client, streamable_http_client
from mcp.server.transport_security import TransportSecuritySettings
from test_tools import BOX, KEY

from server import create_server


@asynccontextmanager
async def live_connector():
    def handler(req):
        if req.headers["authorization"] != "Bearer " + KEY:
            return httpx.Response(401)
        return httpx.Response(200, json=[{"id": BOX, "provider": "gmail", "email": "fake@example.com"}])

    server = create_server("http://mail.test", transport=httpx.MockTransport(handler))
    app = server.streamable_http_app(stateless_http=True, json_response=True, max_request_body_size=16 * 1024 * 1024,
                                    transport_security=TransportSecuritySettings(allowed_hosts=["127.0.0.1:*"], allowed_origins=[]))
    sock = socket.socket()
    sock.bind(("127.0.0.1", 0))
    sock.listen()
    instance = uvicorn.Server(uvicorn.Config(app, log_level="critical", access_log=False))
    task = asyncio.create_task(instance.serve(sockets=[sock]))
    try:
        for _ in range(100):
            if instance.started:
                break
            if task.done():
                await task
            await asyncio.sleep(0.01)
        assert instance.started
        yield f"http://127.0.0.1:{sock.getsockname()[1]}"
    finally:
        instance.should_exit = True
        await asyncio.wait_for(task, 5)
        sock.close()


async def test_real_streamable_http_manifest_skills_auth_and_tool_annotations():
    async with live_connector() as url:
        async with httpx.AsyncClient() as http:
            manifest = (await http.get(url + "/manifest")).json()
            assert manifest["key"] == "clarvio_mail" and manifest["auth"]["check_tool"] == "whoami"
            assert manifest["auth"]["scope"] == "user" and manifest["events"] == []
            skill = await http.get(url + manifest["skills"][0]["path"])
            assert skill.is_success and "idempotency_key" in skill.text
            assert (await http.get(url + "/health/live")).is_success
        for key, expected in [(KEY, False), ("cm_sk_" + "b" * 43, True)]:
            async with create_mcp_http_client(headers={"Authorization": "Bearer " + key, "X-Connection-Id": BOX}) as http:
                async with streamable_http_client(url + "/mcp", http_client=http) as streams:
                    async with ClientSession(streams[0], streams[1]) as session:
                        await session.initialize()
                        catalog = await session.list_tools()
                        assert len(catalog.tools) == 17
                        assert all(t.annotations.title for t in catalog.tools)
                        response = await session.call_tool("whoami", {})
                        assert response.is_error == expected
                        if not expected:
                            assert json.loads(response.content[0].text)[0]["id"] == BOX


async def test_mcp_transport_rejects_untrusted_host_and_origin():
    async with live_connector() as url:
        async with httpx.AsyncClient() as http:
            body = {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
                "protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "test", "version": "1"}}}
            headers = {"Accept": "application/json, text/event-stream", "Host": "evil.example"}
            assert (await http.post(url + "/mcp", json=body, headers=headers)).status_code == 421
            headers.pop("Host")
            headers["Origin"] = "https://evil.example"
            assert (await http.post(url + "/mcp", json=body, headers=headers)).status_code == 403
