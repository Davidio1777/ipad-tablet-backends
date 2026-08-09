import asyncio
import struct
import unittest

from ipad_tablet_backend.usb import (
    PENCIL_FRAME,
    READY_FRAME,
    STREAM_INFO_FRAME,
    VIDEO_FRAME,
    USBOptions,
    encode_frame,
    forwarded_ports,
    read_frame,
    usb_hello,
)


class USBProtocolTests(unittest.IsolatedAsyncioTestCase):
    async def test_round_trip_frame(self) -> None:
        reader = asyncio.StreamReader()
        reader.feed_data(encode_frame(PENCIL_FRAME, b'{"x":0.5}'))
        reader.feed_eof()
        self.assertEqual(await read_frame(reader), (PENCIL_FRAME, b'{"x":0.5}'))

    async def test_reader_handles_split_header_and_payload(self) -> None:
        reader = asyncio.StreamReader()
        task = asyncio.create_task(read_frame(reader))
        frame = encode_frame(VIDEO_FRAME, b"frame")
        reader.feed_data(frame[:3])
        await asyncio.sleep(0)
        reader.feed_data(frame[3:])
        self.assertEqual(await task, (VIDEO_FRAME, b"frame"))

    def test_wire_header_is_network_byte_order(self) -> None:
        frame = encode_frame(VIDEO_FRAME, b"abc")
        self.assertEqual(frame[:5], bytes((VIDEO_FRAME,)) + struct.pack("!I", 3))

    def test_ready_frame_has_empty_payload(self) -> None:
        self.assertEqual(encode_frame(READY_FRAME, b""), bytes((READY_FRAME, 0, 0, 0, 0)))

    def test_stream_info_has_its_own_frame_type(self) -> None:
        self.assertEqual(encode_frame(STREAM_INFO_FRAME, b"{}")[:1], bytes((6,)))

    def test_port_fallback_range(self) -> None:
        options = USBOptions(local_port=18_765, device_port=20_000, port_fallbacks=3)
        self.assertEqual(
            forwarded_ports(options),
            [(18_765, 20_000), (18_766, 20_001), (18_767, 20_002)],
        )

    def test_usb_handshake_does_not_send_lan_token(self) -> None:
        hello = usb_hello({"type": "stream-info", "fps": 120})
        self.assertEqual(hello["transport"], "usb")
        self.assertNotIn("token", hello)


if __name__ == "__main__":
    unittest.main()
