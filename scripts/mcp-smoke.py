#!/usr/bin/env python3
"""Exercise the shipped MCP Docker image against a disposable fake Mail API.

Run with connector/.venv/bin/python; no live providers, user credentials or Agent registry writes.
"""

import asyncio
import base64
import json
import subprocess
import time
import urllib.request
from uuid import uuid4

from mcp import ClientSession
from mcp.client.streamable_http import create_mcp_http_client, streamable_http_client

IMAGE = "clarvio-mail-mcp:local"
KEY = "cm_sk_" + "a" * 43
BOX = "8b250276-3c5e-4b69-bd86-dcc69b350016"
SIZE = 10 * 1024 * 1024
FAKE_API = r'''
import base64, json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
BOX = "8b250276-3c5e-4b69-bd86-dcc69b350016"
SIZE = 10 * 1024 * 1024
class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args): pass
    def respond(self, status, data, headers={}):
        self.send_response(status)
        for name,value in headers.items(): self.send_header(name,value)
        self.end_headers(); self.wfile.write(data)
    def authorized(self):
        valid = self.headers.get("Authorization") == "Bearer cm_sk_" + "a" * 43
        if not valid: self.respond(401,b'{}')
        return valid
    def do_GET(self):
        if not self.authorized(): return
        if "/attachments/" in self.path:
            return self.respond(200, b"x" * SIZE, {"Content-Disposition":"attachment; filename=large.bin", "Content-Type":"application/octet-stream"})
        self.respond(200,json.dumps([{"id":BOX,"email":"fake@example.com","provider":"gmail"}]).encode(),{"Content-Type":"application/json"})
    def do_POST(self):
        if not self.authorized(): return
        data=json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        assert self.headers.get("Idempotency-Key") == "docker-smoke-send-001"
        assert len(base64.b64decode(data["attachments"][0]["contentBase64"])) == SIZE
        self.respond(200,b'{"id":"fb27f835-965a-40b7-b864-d72b5dd68419","status":"accepted"}',{"Content-Type":"application/json"})
ThreadingHTTPServer(("0.0.0.0",8740),Handler).serve_forever()
'''


def docker(*args):
    return subprocess.run(["docker", *args], check=True, text=True, capture_output=True).stdout.strip()


async def check(url):
    async with create_mcp_http_client(headers={"Authorization": "Bearer " + KEY, "X-Connection-Id": str(uuid4())}) as http:
        async with streamable_http_client(url + "/mcp", http_client=http) as streams:
            async with ClientSession(streams[0], streams[1]) as session:
                await session.initialize()
                assert len((await session.list_tools()).tools) == 17
                identity = await session.call_tool("whoami", {})
                assert not identity.is_error and json.loads(identity.content[0].text)[0]["id"] == BOX
                sent = await session.call_tool("send_message", {"mailbox_id": BOX, "idempotency_key": "docker-smoke-send-001", "message": {
                    "to": [{"email": "fake@example.com"}], "subject": "Mock only", "text_body": "No real email",
                    "attachments": [{"name": "large.bin", "content_base64": base64.b64encode(b"x" * SIZE).decode()}]}})
                assert not sent.is_error
                file = await session.call_tool("download_attachment", {"mailbox_id": BOX, "message_id": "m", "attachment_id": "a"})
                assert not file.is_error and len(base64.b64decode(file.content[1].resource.blob)) == SIZE
    async with create_mcp_http_client(headers={"Authorization": "Bearer cm_sk_" + "b" * 43}) as http:
        async with streamable_http_client(url + "/mcp", http_client=http) as streams:
            async with ClientSession(streams[0], streams[1]) as session:
                await session.initialize()
                assert (await session.call_tool("whoami", {})).is_error


def main():
    prefix = "cm-mcp-smoke-" + uuid4().hex[:10]
    names = [prefix + "-mcp", prefix + "-api"]
    try:
        docker("network", "create", "--internal", prefix)
        docker("run", "-d", "--name", names[1], "--network", prefix, "--network-alias", "fake-mail",
               "--read-only", IMAGE, "python", "-c", FAKE_API)
        docker("run", "-d", "--name", names[0], "--network", prefix, "--read-only", "--cap-drop=ALL",
               "--security-opt=no-new-privileges", "--tmpfs=/tmp:rw,size=32m,mode=1777", "-p", "127.0.0.1::8741",
               "-e", "MAIL_API_URL=http://fake-mail:8740", IMAGE)
        # Internal Docker networks don't publish host ports on all engine versions.
        docker("network", "connect", "bridge", names[0])
        port = docker("port", names[0], "8741/tcp").rsplit(":", 1)[1]
        url = "http://127.0.0.1:" + port
        for _ in range(60):
            try:
                with urllib.request.urlopen(url + "/health/live", timeout=1) as response:
                    assert response.status == 200
                break
            except OSError:
                time.sleep(0.5)
        else:
            raise RuntimeError("MCP container did not become healthy")
        inspect = json.loads(docker("inspect", names[0]))[0]
        assert inspect["Config"]["User"] == "1654:1654" and inspect["HostConfig"]["ReadonlyRootfs"]
        assert docker("exec", names[0], "python", "-c", "import os; print(os.getuid())") == "1654"
        asyncio.run(asyncio.wait_for(check(url), 60))
        print("MCP Docker smoke passed: non-root, read-only, 17 tools, scoped auth, 10 MiB upload and embedded download.")
    finally:
        for name in names:
            subprocess.run(["docker", "rm", "-f", name], capture_output=True)
        subprocess.run(["docker", "network", "rm", prefix], capture_output=True)


if __name__ == "__main__":
    main()
