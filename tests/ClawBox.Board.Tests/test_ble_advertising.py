import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("ble_advertising", Path(__file__).parents[2] / "scripts/board/configure-ble-advertising.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class AdvertisingConfigTests(unittest.TestCase):
    def test_preserves_other_sections_and_is_idempotent(self):
        source = "[General]\nName=Box\n[LE]\n#MinAdvertisementInterval=\n#MaxAdvertisementInterval=\n[Policy]\nAutoEnable=true\n"
        result = module.revised_config(source)
        self.assertIn("[LE]\nMinAdvertisementInterval=0x00a0\nMaxAdvertisementInterval=0x00f0\n", result)
        self.assertIn("[General]\nName=Box\n", result)
        self.assertIn("[Policy]\nAutoEnable=true\n", result)
        self.assertEqual(result, module.revised_config(result))

    def test_conflicts_and_duplicate_sections_are_never_overwritten(self):
        for source in ("[LE]\nMinAdvertisementInterval=0x0100\n", "[LE]\n[LE]\n", "[General]\n"):
            with self.assertRaises(ValueError):
                module.revised_config(source)


if __name__ == "__main__":
    unittest.main()
