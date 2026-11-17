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
    target = plugin_parent / 'SeriesAudio_0.4.0.0'
    previous_versions = [plugin_parent / f'SeriesAudio_{version}' for version in ('0.1.0.0', '0.2.0.0', '0.2.1.0', '0.3.0.0')]
    for candidate in plugin_parent.glob('*/Jellyfin.Plugin.SeriesAudio.dll'):
        if candidate.parent != target and candidate.parent not in previous_versions:
            raise SystemExit(f'Another Series Audio version exists: {candidate.parent}. Move that version out of plugins before installing.')
    if args.init_script.exists() and args.init_script.read_bytes() != (package / '99-series-audio.sh').read_bytes():
        raise SystemExit('The requested init-script path already contains different data. Choose a new path.')
    backup = config / 'series-audio-backups' / datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%fZ')
    backup.mkdir(parents=True)
    if target.exists(): shutil.copytree(target, backup / target.name)
    existing_preferences = config / 'data/data/series-audio'
    if existing_preferences.exists(): shutil.copytree(existing_preferences, backup / 'preferences')
    # Match the existing Jellyfin data owner when installed with sudo.
    owner = (config / 'data').stat()
    dll = 'Jellyfin.Plugin.SeriesAudio.dll'
    staging = backup / 'staging'
    staging.mkdir()
    shutil.copy2(package / dll, staging / dll)
    (staging / dll).chmod(0o644)
    if os.geteuid() == 0:
        os.chown(staging, owner.st_uid, owner.st_gid)
        os.chown(staging / dll, owner.st_uid, owner.st_gid)
    if not args.init_script.exists():
        args.init_script.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(package / '99-series-audio.sh', args.init_script)
        args.init_script.chmod(0o755)
    plugin_parent.mkdir(parents=True, exist_ok=True)
    moved_previous = []
    try:
        for previous in previous_versions:
            if previous.exists():
                previous.rename(backup / previous.name)
                moved_previous.append(previous)
        if target.exists():
            (staging / dll).replace(target / dll)
            staging.rmdir()
        else:
            staging.rename(target)
    except OSError:
        for previous in reversed(moved_previous):
            (backup / previous.name).rename(previous)
        raise
    print(f'Plugin installed: {target}')
    print(f'Backup: {backup}')
    print('If this bind mount is already configured, restart the container. For a new installation, add it and recreate the container:')
    print(f'  - {args.init_script.resolve()}:/custom-cont-init.d/99-series-audio:ro')
    print('No container was stopped or restarted. After restarting, refresh the browser without cache.')


if __name__ == '__main__':
    main()
