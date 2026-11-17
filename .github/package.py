#!/usr/bin/env python3
"""Build the Jellyfin catalog archive and its GitHub Releases manifest."""
from datetime import datetime, timezone
from pathlib import Path
from xml.etree import ElementTree
import hashlib
import json
import zipfile

root = Path(__file__).resolve().parents[1]
project = root / 'src/Jellyfin.Plugin.SeriesAudio'
settings = ElementTree.parse(project / 'Jellyfin.Plugin.SeriesAudio.csproj')
version = settings.findtext('./PropertyGroup/Version')
assembly_version = settings.findtext('./PropertyGroup/AssemblyVersion')
abi = settings.find("./ItemGroup/PackageReference[@Include='Jellyfin.Controller']").attrib['Version']
framework = settings.findtext('./PropertyGroup/TargetFramework')
dll = project / 'bin/Release' / framework / 'Jellyfin.Plugin.SeriesAudio.dll'

output = root / 'artifacts'
output.mkdir(exist_ok=True)
archive = output / f'SeriesAudio-{version}-jellyfin-12.4.zip'
with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as bundle:
    bundle.writestr(zipfile.ZipInfo(dll.name), dll.read_bytes(), compress_type=zipfile.ZIP_DEFLATED)
with zipfile.ZipFile(archive) as bundle:
    assert bundle.testzip() is None

release = f'https://github.com/dobriys/jellyfin-series-audio/releases/download/v{version}'
manifest = [{
    'guid': 'f8f6c53c-43c2-4448-a56c-e6d6a18436b4',
    'name': 'Series Audio',
    'description': 'Choose a preferred audio track per TV season and Jellyfin profile.',
    'overview': 'Season audio preferences for Jellyfin Web',
    'owner': 'dobriys',
    'category': 'General',
    'versions': [{
        'version': assembly_version,
        'targetAbi': f'{abi}.0',
        'sourceUrl': f'{release}/{archive.name}',
        # Jellyfin's catalog installer expects an MD5 checksum of the ZIP.
        'checksum': hashlib.md5(archive.read_bytes(), usedforsecurity=False).hexdigest(),
        'timestamp': datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'),
        'changelog': f'Series Audio {version}. See the GitHub release notes for details.',
    }],
}]
(output / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
print(archive)
print(output / 'manifest.json')
