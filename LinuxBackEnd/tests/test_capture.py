import unittest
from unittest.mock import patch

from ipad_tablet_backend.capture import AnnexBAccessUnitParser, CaptureOptions, build_capture_command


def nal(type_: int, payload: bytes = b"x", long: bool = True) -> bytes:
    return (b"\0\0\0\1" if long else b"\0\0\1") + bytes([type_]) + payload


class AnnexBParserTests(unittest.TestCase):
    def test_groups_access_units_across_arbitrary_chunks(self) -> None:
        first = nal(9) + nal(7) + nal(8, long=False) + nal(5)
        second = nal(9, long=False) + nal(1)
        stream = first + second + nal(9)
        parser = AnnexBAccessUnitParser()
        output = []
        for index in range(0, len(stream), 5):
            output.extend(parser.feed(stream[index:index + 5]))
        output.extend(parser.finish())
        self.assertEqual(output[0], first)
        self.assertEqual(output[1], second)

    def test_x11_software_command_is_low_latency_annex_b(self) -> None:
        command, encoder = build_capture_command(CaptureOptions(source="x11", encoder="libx264"))
        self.assertEqual(encoder, "libx264")
        self.assertIn("zerolatency", command)
        self.assertEqual(command[-2:], ["h264", "pipe:1"])

    def test_wayland_uses_explicit_stdout_url(self) -> None:
        command, _ = build_capture_command(CaptureOptions(source="wayland", encoder="libx264"))
        self.assertEqual(command[-2:], ["-f", "pipe:1"])

    def test_gaming_vaapi_uses_one_frame_pipeline_and_short_gop(self) -> None:
        with patch("ipad_tablet_backend.capture.select_vaapi_device", return_value="/dev/dri/renderD128"):
            command, _ = build_capture_command(CaptureOptions(
                source="wayland", encoder="h264_vaapi", width=1280, height=720,
                fps=60, bitrate=8_000_000, gaming_mode=True,
            ))
        self.assertIn("scale_vaapi=w=1280:h=720:format=nv12:out_range=full", command)
        self.assertIn("g=30", command)
        self.assertIn("bf=0", command)
        self.assertIn("async_depth=1", command)
        self.assertIn("bufsize=266666", command)

    def test_vaapi_vbr_allows_a_higher_peak(self) -> None:
        with patch("ipad_tablet_backend.capture.select_vaapi_device", return_value="/dev/dri/renderD128"):
            command, _ = build_capture_command(CaptureOptions(
                source="wayland", encoder="h264_vaapi", bitrate=8_000_000,
                rate_control="vbr",
            ))
        self.assertIn("rc_mode=VBR", command)
        self.assertIn("maxrate=12000000", command)

    def test_120_fps_gaming_profile_reaches_capture_and_encoder(self) -> None:
        with patch("ipad_tablet_backend.capture.select_vaapi_device", return_value="/dev/dri/renderD128"):
            command, _ = build_capture_command(CaptureOptions(
                source="wayland", encoder="h264_vaapi", width=1280, height=720,
                fps=120, bitrate=8_000_000, gaming_mode=True,
            ))
        self.assertIn("120", command)
        self.assertEqual(command[command.index("-r") + 1], "120")
        self.assertIn("g=60", command)

    def test_x11_scales_full_source_instead_of_cropping_it(self) -> None:
        command, _ = build_capture_command(CaptureOptions(
            source="x11", encoder="libx264", width=1280, height=720,
            source_width=2560, source_height=1440,
        ))
        self.assertIn("2560x1440", command)
        self.assertIn("scale=1280:720:flags=fast_bilinear", command)


if __name__ == "__main__":
    unittest.main()
