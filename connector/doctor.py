"""Read-only live account check. Prompt for a key; never put secrets in command arguments."""

import argparse
import asyncio
import getpass
import json
from uuid import uuid4

from mcp import ClientSession
from mcp.client.streamable_http import create_mcp_http_client, streamable_http_client


async def check(url: str, key: str):
    async with create_mcp_http_client(headers={"Authorization": "Bearer " + key, "X-Connection-Id": str(uuid4())}) as http:
        async with streamable_http_client(url.rstrip("/") + "/mcp", http_client=http) as streams:
            async with ClientSession(streams[0], streams[1]) as session:
                await session.initialize()
                catalog = await session.list_tools()
                result = await session.call_tool("whoami", {})
                if result.is_error:
                    raise RuntimeError("The Mail service rejected the credentials or is unavailable.")
                boxes = json.loads(result.content[0].text)
                for box in boxes:
                    response = await session.call_tool("list_messages", {"mailbox_id": box["id"], "limit": 1})
                    if response.is_error:
                        raise RuntimeError("A mailbox read failed. Check mail:read scope and provider authorization.")
                print(f"OK: {len(catalog.tools)} tools; credentials accepted; {len(boxes)} mailbox(es) checked read-only.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default="http://127.0.0.1:8741")
    args = parser.parse_args()
    key = getpass.getpass("Clarvio Mail API key: ")
    try:
        asyncio.run(asyncio.wait_for(check(args.url, key), 60))
    except Exception:
        parser.exit(1, "Doctor failed: check service connectivity, key scopes and mailbox authorization. No write was attempted.\n")
