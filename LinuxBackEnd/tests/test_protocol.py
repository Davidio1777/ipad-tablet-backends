import asyncio
import unittest

from ipad_tablet_backend.protocol import WebSocket


class FakeWriter:
    def __init__(self) -> None:
        self.data = bytearray()

    def write(self, data: bytes) -> None:
        self.data.extend(data)

    async def drain(self) -> None:
        pass


class WebSocketTests(unittest.IsolatedAsyncioTestCase):
    async def test_binary_frame_lengths(self) -> None:
        writer = FakeWriter()
        socket = WebSocket(asyncio.StreamReader(), writer)  # type: ignore[arg-type]
        await socket.send_binary(b"a" * 200)
        self.assertEqual(writer.data[:2], b"\x82\x7e")
        self.assertEqual(writer.data[2:4], b"\x00\xc8")
        self.assertEqual(len(writer.data), 204)


if __name__ == "__main__":
    unittest.main()
