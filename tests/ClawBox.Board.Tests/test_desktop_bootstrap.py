import importlib.util
from pathlib import Path
import sys
import tempfile
import types
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'src/ClawBox.Board.Network'))
from clawbox_network.desktop import DesktopBootstrap
from clawbox_network.protocol import ProtocolError


class DesktopBootstrapTests(unittest.TestCase):
    def test_rejects_commands_admin_credentials_and_invalid_worker_before_any_writes(self):
        with tempfile.TemporaryDirectory() as root:
            home = Path(root)
            backend = DesktopBootstrap(home=home, assets=home / 'assets', run=lambda *a, **k: self.fail('must not run'))
            for request in [{'op': 'desktop_setup', 'worker_token': 'bad'},
                            {'op': 'desktop_setup', 'worker_token': 'A' * 64, 'command': 'anything'},
                            {'op': 'desktop_setup', 'worker_token': 'A' * 64, 'admin_token': 'B' * 64}]:
                with self.assertRaises(ProtocolError):
                    backend.setup('fixture-owner', request)
            self.assertEqual([], list(home.iterdir()))

    def test_missing_factory_assets_cannot_execute_or_expose_existing_gateway(self):
        with tempfile.TemporaryDirectory() as root:
            backend = DesktopBootstrap(home=Path(root), assets=Path(root)/'missing', run=lambda *a, **k: self.fail('must not run'))
            with self.assertRaisesRegex(ProtocolError, 'desktop_setup_failed'):
                backend.setup('fixture-owner', {'op':'desktop_setup','worker_token':'A'*64})
