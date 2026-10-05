"""MCP inputs; file bytes stay out of tool descriptions and error messages."""

import base64
import binascii
import re
from typing import Annotated
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field

MAX_FILE_BYTES = 10 * 1024 * 1024
MailboxId = UUID
ResourceId = Annotated[str, Field(min_length=1, max_length=2048)]
IdempotencyKey = Annotated[str, Field(min_length=16, max_length=128, pattern=r"^[\x21-\x7e]+$")]


class Address(BaseModel):
    model_config = ConfigDict(extra="forbid", hide_input_in_errors=True)
    email: str
    name: str | None = None


class Attachment(BaseModel):
    model_config = ConfigDict(extra="forbid", hide_input_in_errors=True)
    name: str
    content_base64: str = Field(description="The file's bytes, base64-encoded", repr=False)
    content_type: str = "application/octet-stream"


class Message(BaseModel):
    model_config = ConfigDict(extra="forbid", hide_input_in_errors=True)
    to: list[Address]
    subject: str
    text_body: str
    cc: list[Address] = Field(default_factory=list)
    bcc: list[Address] = Field(default_factory=list)
    html_body: str | None = None
    attachments: list[Attachment] = Field(default_factory=list)

    def api_body(self, *, draft: bool = False) -> dict:
        recipients = self.to + self.cc + self.bcc
        if (not draft and not self.to) or len(recipients) > 50:
            raise ValueError("Use at most 50 recipients, with at least one To address when sending.")
        for address in recipients:
            if (len(address.email) > 254 or not re.fullmatch(r"[^\s<>@,;]+@[^\s<>@,;]+", address.email)
                    or any(ord(c) < 32 or ord(c) == 127 for c in address.email + (address.name or ""))):
                raise ValueError("A recipient address is invalid.")
        if len(self.subject) > 500 or (not draft and not self.subject.strip()) or any(ord(c) < 32 or ord(c) == 127 for c in self.subject):
            raise ValueError("Use a subject of at most 500 characters without line breaks; sending needs a subject.")
        if len(self.text_body) > 100_000 or len(self.html_body or "") > 100_000:
            raise ValueError("The email body exceeds 100,000 characters.")
        if len(self.attachments) > 10:
            raise ValueError("Use at most 10 attachments.")
        total = 0
        for file in self.attachments:
            if (not file.name.strip() or len(file.name) > 255 or file.name in (".", "..")
                    or any(c in "/\\" or ord(c) < 32 or ord(c) == 127 for c in file.name)):
                raise ValueError("An attachment filename is invalid.")
            if not re.fullmatch(r"[A-Za-z0-9!#$&^_.+-]+/[A-Za-z0-9!#$&^_.+-]+", file.content_type):
                raise ValueError("An attachment MIME type is invalid.")
            if len(file.content_base64) > ((MAX_FILE_BYTES + 2) // 3) * 4:
                raise ValueError("Attachments exceed the 10 MiB combined limit.")
            try:
                total += len(base64.b64decode(file.content_base64, validate=True))
            except (ValueError, binascii.Error):
                raise ValueError("An attachment is not valid base64. Nothing was sent.") from None
            if total > MAX_FILE_BYTES:
                raise ValueError("Attachments exceed the 10 MiB combined limit.")
        return {
            "to": [x.model_dump() for x in self.to], "cc": [x.model_dump() for x in self.cc],
            "bcc": [x.model_dump() for x in self.bcc], "subject": self.subject,
            "textBody": self.text_body, "htmlBody": self.html_body,
            "attachments": [{"name": f.name, "contentType": f.content_type, "contentBase64": f.content_base64}
                            for f in self.attachments],
        }
