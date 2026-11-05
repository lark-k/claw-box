"""Update box model metadata while retaining existing credentials and pairings."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

module = Path('/opt/clawbox-network/clawbox_network/model.py')
config = Path('/var/lib/quantum-harness/model-gateway.json')
source = Path(sys.argv[1]).resolve(strict=True)
context = int(sys.argv[2])
if not 1 <= context <= 10485760:
    raise SystemExit('Invalid context window')
if os.geteuid() != 0 or module.is_symlink() or config.is_symlink() or not source.is_file():
    raise SystemExit('Unsafe metadata update')
subprocess.run(['/usr/bin/python3', '-B', '-m', 'py_compile', str(source)], check=True)
backup_root = Path('/var/backups/quantum-harness')
backup_root.mkdir(mode=0o700, parents=True, exist_ok=True)
backup = Path(tempfile.mkdtemp(prefix='model-metadata-', dir=backup_root))
shutil.copy2(module, backup / 'model.py')
shutil.copy2(config, backup / 'model-gateway.json')


def write(path, data):
    fd, name = tempfile.mkstemp(prefix='.quantum-', dir=path.parent)
    try:
        os.fchmod(fd, 0o600)
        with os.fdopen(fd, 'wb') as output:
            output.write(data)
            output.flush()
            os.fsync(output.fileno())
        os.replace(name, path)
    finally:
        Path(name).unlink(missing_ok=True)


try:
    data = json.loads(config.read_text(encoding='utf-8'))
    data['contextWindow'] = context
    write(module, source.read_bytes())
    write(config, json.dumps(data).encode())
    subprocess.run(['systemctl', 'restart', 'clawbox-network.service'], check=True, timeout=60)
    subprocess.run(['systemctl', 'is-active', '--quiet', 'clawbox-network.service'], check=True, timeout=60)
except Exception:
    write(module, (backup / 'model.py').read_bytes())
    write(config, (backup / 'model-gateway.json').read_bytes())
    subprocess.run(['systemctl', 'restart', 'clawbox-network.service'], timeout=60)
    raise
print('Model context metadata updated; backup:', backup)
