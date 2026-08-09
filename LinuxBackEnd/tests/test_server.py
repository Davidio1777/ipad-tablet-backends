import asyncio
import unittest
from unittest.mock import patch

from ipad_tablet_backend.capture import CaptureOptions
from ipad_tablet_backend.server import ServerOptions, TabletServer


class StreamSettingsTests(unittest.IsolatedAsyncioTestCase):
    async def test_gaming_profile_can_be_applied_and_restored(self) -> None:
        options = ServerOptions(
            host="127.0.0.1",
            token="",
            input_mode="none",
            uinput_path="/dev/null",
            uhid_path="/dev/null",
            rotation=0,
            pressure_curve=1.0,
            capture=CaptureOptions(
                source="x11", encoder="libx264", width=2560, height=1440,
                source_width=2560, source_height=1440, bitrate=16_000_000,
            ),
        )

        async def idle_capture() -> None:
            await asyncio.Event().wait()

        with patch(
            "ipad_tablet_backend.server.build_capture_command",
            return_value=(["fake-capture"], "h264_vaapi"),
        ):
            server = TabletServer(options)
            server.capture_loop = idle_capture  # type: ignore[method-assign]
            server._capture_task = asyncio.create_task(idle_capture())

            await server.apply_stream_settings({
                "type": "stream-settings",
                "enabled": True,
                "width": 1280,
                "height": 720,
                "fps": 120,
                "bitrate": 8_000_000,
                "rateControl": "vbr",
            })
            self.assertTrue(server.options.capture.gaming_mode)
            self.assertEqual((server.options.capture.width, server.options.capture.height), (1280, 720))
            self.assertEqual(server.options.capture.fps, 120)
            self.assertEqual(server.options.capture.bitrate, 8_000_000)
            self.assertEqual(server.options.capture.rate_control, "vbr")
            self.assertEqual(server.stream_revision, 1)

            await server.apply_stream_settings({"type": "stream-settings", "enabled": False})
            self.assertFalse(server.options.capture.gaming_mode)
            self.assertEqual((server.options.capture.width, server.options.capture.height), (2560, 1440))
            self.assertEqual(server.options.capture.fps, 60)
            self.assertEqual(server.options.capture.bitrate, 16_000_000)
            self.assertEqual(server.stream_revision, 2)

            assert server._capture_task
            server._capture_task.cancel()
            await asyncio.gather(server._capture_task, return_exceptions=True)

    async def test_pencil_batch_preserves_every_coalesced_sample(self) -> None:
        options = ServerOptions(
            host="127.0.0.1",
            token="",
            input_mode="none",
            uinput_path="/dev/null",
            uhid_path="/dev/null",
            rotation=0,
            pressure_curve=1.0,
            capture=CaptureOptions(source="x11", encoder="libx264"),
        )

        with patch(
            "ipad_tablet_backend.server.build_capture_command",
            return_value=(["fake-capture"], "libx264"),
        ):
            server = TabletServer(options)
            received: list[dict[str, object]] = []
            server.tablet.apply = received.append  # type: ignore[method-assign]
            await server.handle_input_message({
                "type": "pencil-batch",
                "samples": [
                    {"type": "pencil", "phase": "move", "sequence": 10},
                    {"type": "pencil", "phase": "move", "sequence": 11},
                    {"type": "pencil", "phase": "move", "sequence": 12},
                ],
            })

            self.assertEqual([sample["sequence"] for sample in received], [10, 11, 12])
            self.assertEqual(server.input_samples, 3)
            self.assertEqual(server.input_rate_hz, 3)

    async def test_video_can_stop_without_stopping_input_and_restart(self) -> None:
        options = ServerOptions(
            host="127.0.0.1",
            token="",
            input_mode="none",
            uinput_path="/dev/null",
            uhid_path="/dev/null",
            rotation=0,
            pressure_curve=1.0,
            capture=CaptureOptions(source="x11", encoder="libx264"),
        )

        async def idle_capture() -> None:
            await asyncio.Event().wait()

        with patch(
            "ipad_tablet_backend.server.build_capture_command",
            return_value=(["fake-capture"], "libx264"),
        ):
            server = TabletServer(options)
            server.capture_loop = idle_capture  # type: ignore[method-assign]
            server._capture_task = asyncio.create_task(idle_capture())

            await server.apply_stream_settings({
                "type": "stream-settings", "enabled": True, "videoEnabled": False,
            })
            self.assertFalse(server.video_enabled)
            self.assertIsNone(server._capture_task)
            self.assertFalse(server.metadata["videoEnabled"])

            received: list[dict[str, object]] = []
            server.tablet.apply = received.append  # type: ignore[method-assign]
            await server.handle_input_message({"type": "pencil", "phase": "move", "x": 0.5})
            self.assertEqual(len(received), 1)

            await server.apply_stream_settings({
                "type": "stream-settings", "enabled": True, "videoEnabled": True,
            })
            self.assertTrue(server.video_enabled)
            self.assertIsNotNone(server._capture_task)
            self.assertTrue(server.metadata["videoEnabled"])

            assert server._capture_task
            server._capture_task.cancel()
            await asyncio.gather(server._capture_task, return_exceptions=True)


if __name__ == "__main__":
    unittest.main()
