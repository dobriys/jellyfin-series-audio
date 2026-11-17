#!/usr/bin/env python3
"""Install a prepared package into a LinuxServer Jellyfin bind mount.

Does not stop/restart containers, change compose, or overwrite existing custom.js.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import shutil


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', type=Path, required=True, help='Host directory mounted at /config')
    parser.add_argument('--init-script', type=Path, required=True, help='Host path for the new container init script')
    args = parser.parse_args()
    package = Path(__file__).resolve().parent
    config = args.config.resolve()
    if not (config / 'system.xml').is_file() or not (config / 'data').is_dir():
        raise SystemExit('Expected an existing LinuxServer Jellyfin /config directory with system.xml and data/. No files changed.')
    checksums = json.loads((package / 'checksums.json').read_text())
    for name, expected in checksums.items():
        if hashlib.sha256((package / name).read_bytes()).hexdigest() != expected:
            raise SystemExit(f'Checksum mismatch: {name}. No files changed.')
    plugin_parent = config / 'data/plugins'
    target = plugin_parent / 'SeriesAudio_0.1.0.0'
    for candidate in plugin_parent.glob('*/Jellyfin.Plugin.SeriesAudio.dll'):
        if candidate.parent != target:
            raise SystemExit(f'Another Series Audio version exists: {candidate.parent}. Move that version out of plugins before installing.')
    if args.init_script.exists() and args.init_script.read_bytes() != (package / '99-series-audio.sh').read_bytes():
        raise SystemExit('The requested init-script path already contains different data. Choose a new path.')
    backup = config / 'series-audio-backups' / datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
    backup.mkdir(parents=True)
    if target.exists(): shutil.copytree(target, backup / target.name)
    existing_preferences = config / 'data/data/series-audio'
    if existing_preferences.exists(): shutil.copytree(existing_preferences, backup / 'preferences')
    target.mkdir(parents=True, exist_ok=True)
    # Match the existing Jellyfin data owner when installed with sudo.
    owner = (config / 'data').stat()
    for name in ['Jellyfin.Plugin.SeriesAudio.dll']:
        temporary = target / (name + '.installing')
        shutil.copy2(package / name, temporary)
        temporary.chmod(0o644)
        if os.geteuid() == 0: os.chown(temporary, owner.st_uid, owner.st_gid)
        temporary.replace(target / name)
    if os.geteuid() == 0: os.chown(target, owner.st_uid, owner.st_gid)
    args.init_script.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(package / '99-series-audio.sh', args.init_script)
    args.init_script.chmod(0o755)
    print(f'Plugin installed: {target}')
    print(f'Backup: {backup}')
    print('Add this bind mount to the Jellyfin service, then recreate the container:')
    print(f'  - {args.init_script.resolve()}:/custom-cont-init.d/99-series-audio:ro')
    print('No container was stopped or restarted. After restarting, refresh the browser without cache.')


if __name__ == '__main__':
    main()
