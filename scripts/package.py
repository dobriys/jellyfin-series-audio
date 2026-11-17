#!/usr/bin/env python3
"""Package the already-built DLL with installation instructions, without host dependencies."""
from pathlib import Path
import hashlib
import json
import shutil
import zipfile

root = Path(__file__).resolve().parents[1]
output = root / 'artifacts/SeriesAudio-0.1.0-jellyfin-12.4'
output.mkdir(parents=True, exist_ok=True)
files = {
    'Jellyfin.Plugin.SeriesAudio.dll': root / 'src/Jellyfin.Plugin.SeriesAudio/bin/Release/net10.0/Jellyfin.Plugin.SeriesAudio.dll',
    'install.py': root / 'scripts/install.py',
    '99-series-audio.sh': root / 'scripts/99-series-audio.sh',
    'docs/INSTALL.md': root / 'docs/INSTALL.md',
    'docs/TV-CHECKLIST.md': root / 'docs/TV-CHECKLIST.md',
    'README.md': root / 'README.md',
}
for name, source in files.items():
    (output / name).parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, output / name)
checksums = {name: hashlib.sha256((output / name).read_bytes()).hexdigest() for name in files}
(output / 'checksums.json').write_text(json.dumps(checksums, indent=2) + '\n')
archive = Path(str(output) + '.zip')
with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as bundle:
    for name in [*files, 'checksums.json']:
        bundle.write(output / name, f'{output.name}/{name}')
with zipfile.ZipFile(archive) as bundle:
    assert bundle.testzip() is None
print(archive)
