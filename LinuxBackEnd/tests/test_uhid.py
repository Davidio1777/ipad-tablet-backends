import struct
import unittest
from unittest.mock import patch

from ipad_tablet_backend.tablet import TabletMapping
from ipad_tablet_backend.uhid import (
    AXIS_MAX,
    BUS_USB,
    PRESSURE_MAX,
    PRODUCT_ID,
    REPORT_DESCRIPTOR,
    UHID_CREATE2,
    UHID_INPUT2,
    UHIDTablet,
    VENDOR_ID,
    create_event,
    input_event,
)


class UHIDProtocolTests(unittest.TestCase):
    def test_create_event_contains_identity_and_descriptor(self) -> None:
        event = create_event()
        self.assertEqual(struct.unpack_from("<I", event)[0], UHID_CREATE2)
        self.assertEqual(struct.unpack_from("<H", event, 260)[0], len(REPORT_DESCRIPTOR))
        self.assertEqual(struct.unpack_from("<H", event, 262)[0], BUS_USB)
        self.assertEqual(struct.unpack_from("<I", event, 264)[0], VENDOR_ID)
        self.assertEqual(struct.unpack_from("<I", event, 268)[0], PRODUCT_ID)
        self.assertEqual(event[280:280 + len(REPORT_DESCRIPTOR)], REPORT_DESCRIPTOR)

    def test_input_event_is_a_short_valid_uhid_event(self) -> None:
        report = struct.pack("<BBHHHbb", 1, 0, 123, 456, 789, -12, 34)
        event = input_event(report)
        self.assertEqual(struct.unpack_from("<IH", event), (UHID_INPUT2, 10))
        self.assertEqual(event[6:], report)

    @patch("ipad_tablet_backend.uhid.os.close")
    @patch("ipad_tablet_backend.uhid.os.write")
    @patch("ipad_tablet_backend.uhid.os.open", return_value=42)
    def test_pencil_message_becomes_otd_tablet_report(self, _open, write, _close) -> None:
        tablet = UHIDTablet(mapping=TabletMapping())
        write.reset_mock()
        tablet.apply({
            "type": "pencil", "phase": "move", "x": 0.25, "y": 0.75,
            "pressure": 0.5, "altitude": 0.0, "azimuth": 0.0, "sequence": 1,
        })
        event = write.call_args.args[1]
        report_id, buttons, x, y, pressure, tilt_x, tilt_y = struct.unpack("<BBHHHbb", event[6:])
        self.assertEqual(report_id, 1)
        self.assertEqual(buttons, 0)
        self.assertEqual(x, round(0.25 * AXIS_MAX))
        self.assertEqual(y, round(0.75 * AXIS_MAX))
        self.assertEqual(pressure, round(0.5 * PRESSURE_MAX))
        self.assertEqual(tilt_x, 0)
        self.assertEqual(tilt_y, -90)
        self.assertEqual(tablet.events_received, 1)
        tablet.close()


if __name__ == "__main__":
    unittest.main()
