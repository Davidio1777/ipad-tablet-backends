import asyncio
import json
import unittest

from ipad_tablet_backend.udp import (
    HEADER,
    METADATA_PACKET,
    VIDEO_PACKET,
    UDPBridge,
    UDPOptions,
    decode_packet,
    encode_packets,
)


class FakeDatagramTransport:
    def __init__(self) -> None:
        self.sent: list[tuple[bytes, tuple[str, int]]] = []

    def sendto(self, data: bytes, address: tuple[str, int]) -> None:
        self.sent.append((data, address))

    def close(self) -> None:
        pass


class UDPProtocolTests(unittest.IsolatedAsyncioTestCase):
    def test_video_frame_is_fragmented_below_mtu_and_reassembles(self) -> None:
        payload = bytes(range(256)) * 25
        packets = list(encode_packets(VIDEO_PACKET, 42, payload, mtu=1200))
        self.assertGreater(len(packets), 1)
        self.assertTrue(all(len(packet) <= 1200 for packet in packets))
        decoded = [decode_packet(packet) for packet in packets]
        self.assertTrue(all(item[0] == VIDEO_PACKET and item[1] == 42 for item in decoded))
        self.assertEqual(b"".join(item[4] for item in decoded), payload)
        self.assertEqual(HEADER.size, 14)

    async def test_authenticated_hello_registers_video_and_input_session(self) -> None:
        received: list[dict[str, object]] = []

        async def handle(message: dict[str, object]) -> None:
            received.append(message)

        bridge = UDPBridge(
            UDPOptions(client_timeout=60),
            "127.0.0.1",
            lambda: {"type": "stream-info", "fps": 120},
            "secret",
            handle,
            lambda: None,
        )
        transport = FakeDatagramTransport()
        bridge.video_transport = transport  # type: ignore[assignment]
        address = ("192.0.2.5", 50_000)
        bridge.handle_video_datagram(
            json.dumps({"type": "hello", "token": "secret", "session": "abc"}).encode(),
            address,
        )
        self.assertEqual(bridge.connected_clients, 1)
        self.assertEqual(decode_packet(transport.sent[0][0])[0], METADATA_PACKET)

        bridge.handle_input_datagram(
            json.dumps({
                "type": "input",
                "token": "secret",
                "session": "abc",
                "payload": {"type": "pencil", "sequence": 9},
            }).encode(),
            ("192.0.2.5", 50_001),
        )
        await asyncio.sleep(0)
        self.assertEqual(received, [{"type": "pencil", "sequence": 9}])
        self.assertEqual(bridge.input_packets_received, 1)

    async def test_wrong_token_or_unknown_session_is_dropped(self) -> None:
        received: list[dict[str, object]] = []

        async def handle(message: dict[str, object]) -> None:
            received.append(message)

        bridge = UDPBridge(
            UDPOptions(client_timeout=60),
            "127.0.0.1",
            dict,
            "secret",
            handle,
            lambda: None,
        )
        transport = FakeDatagramTransport()
        bridge.video_transport = transport  # type: ignore[assignment]
        bridge.handle_video_datagram(
            json.dumps({"type": "hello", "token": "wrong", "session": "abc"}).encode(),
            ("192.0.2.5", 50_000),
        )
        bridge.handle_input_datagram(
            json.dumps({
                "type": "input",
                "token": "secret",
                "session": "missing",
                "payload": {"type": "pencil"},
            }).encode(),
            ("192.0.2.5", 50_001),
        )
        await asyncio.sleep(0)
        self.assertEqual(bridge.connected_clients, 0)
        self.assertEqual(received, [])
        self.assertEqual(bridge.dropped_input_packets, 1)

    def test_empty_token_is_rejected(self) -> None:
        async def handle(_message: dict[str, object]) -> None:
            pass

        with self.assertRaisesRegex(ValueError, "non-empty"):
            UDPBridge(UDPOptions(), "127.0.0.1", dict, "", handle, lambda: None)


if __name__ == "__main__":
    unittest.main()
