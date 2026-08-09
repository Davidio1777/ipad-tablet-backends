from __future__ import annotations

import os
import struct
from collections import deque

from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF


MAGIC = b"IPAE"
VERSION = 2
VIDEO_ENVELOPE = 1
CONTROL_ENVELOPE = 3
HEADER = struct.Struct("!4sBB12s")
TAG_SIZE = 16
SALT = b"ipad-tablet-secure-udp-v2"


def derive_key(token: str, direction: str) -> bytes:
    if len(token.encode()) < 16:
        raise ValueError("secure UDP token must contain at least 16 UTF-8 bytes")
    return HKDF(
        algorithm=hashes.SHA256(), length=32, salt=SALT, info=direction.encode()
    ).derive(token.encode())


class SecureDatagrams:
    def __init__(self, token: str, *, sending_direction: str, receiving_direction: str) -> None:
        self._sender = AESGCM(derive_key(token, sending_direction))
        self._receiver = AESGCM(derive_key(token, receiving_direction))
        self._seen: set[bytes] = set()
        self._seen_order: deque[bytes] = deque()

    def seal(self, envelope_type: int, payload: bytes) -> bytes:
        nonce = os.urandom(12)
        header = HEADER.pack(MAGIC, VERSION, envelope_type, nonce)
        return header + self._sender.encrypt(nonce, payload, header)

    def open(self, packet: bytes, *, expected_type: int) -> bytes | None:
        if len(packet) < HEADER.size + TAG_SIZE:
            return None
        magic, version, envelope_type, nonce = HEADER.unpack_from(packet)
        if magic != MAGIC or version != VERSION or envelope_type != expected_type:
            return None
        if nonce in self._seen:
            return None
        header = packet[:HEADER.size]
        try:
            plaintext = self._receiver.decrypt(nonce, packet[HEADER.size:], header)
        except InvalidTag:
            return None
        self._seen.add(nonce)
        self._seen_order.append(nonce)
        while len(self._seen_order) > 4096:
            self._seen.discard(self._seen_order.popleft())
        return plaintext
