import math
import unittest

from ipad_tablet_backend.tablet import TabletMapping


class TabletMappingTests(unittest.TestCase):
    def test_rotation(self) -> None:
        self.assertEqual(TabletMapping(rotation=90).point(0.25, 0.75), (0.25, 0.25))
        self.assertEqual(TabletMapping(rotation=180).point(0.25, 0.75), (0.75, 0.25))
        self.assertEqual(TabletMapping(rotation=270).point(0.25, 0.75), (0.75, 0.75))

    def test_clamps_and_curves_pressure(self) -> None:
        mapping = TabletMapping(pressure_curve=2)
        self.assertTrue(math.isclose(mapping.pressure(0.5), 0.25))
        self.assertEqual(mapping.pressure(-1), 0)
        self.assertEqual(mapping.pressure(2), 1)


if __name__ == "__main__":
    unittest.main()
