#!/usr/bin/env python3
"""Exercise a real Jellyfin 12.4 process on loopback with generated, disposable media.

Usage: python3 scripts/smoke-test.py --dotnet /path/to/dotnet --server /path/to/jellyfin.dll
Requires ffmpeg on PATH. Never connects to an existing Jellyfin server.
"""
import argparse
import json
import os
from pathlib import Path
import secrets
import shutil
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--server', required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    dll = root / 'src/Jellyfin.Plugin.SeriesAudio/bin/Release/net10.0/Jellyfin.Plugin.SeriesAudio.dll'
    assert dll.exists(), 'Build the plugin first'
    with tempfile.TemporaryDirectory(prefix='series-audio-smoke-') as directory:
        work = Path(directory)
        for name in ['data', 'config', 'cache', 'logs', 'media']:
            (work / name).mkdir()
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0))
            port = sock.getsockname()[1]
        (work / 'config/network.xml').write_text(f'''<NetworkConfiguration>
<InternalHttpPort>{port}</InternalHttpPort><PublicHttpPort>{port}</PublicHttpPort>
<EnableIPv4>true</EnableIPv4><EnableIPv6>false</EnableIPv6><EnableRemoteAccess>false</EnableRemoteAccess>
<EnableUPnP>false</EnableUPnP><LocalNetworkAddresses><string>127.0.0.1</string></LocalNetworkAddresses>
</NetworkConfiguration>''')
        plugin_dir = work / 'data/plugins/SeriesAudio_0.4.0.0'
        plugin_dir.mkdir(parents=True)
        shutil.copy2(dll, plugin_dir / dll.name)
        season = work / 'media/Audio Fixture/Season 01'
        season.mkdir(parents=True)
        fixture = [
            (1, 1, [('Default Studio', 'rus'), ('Preferred Studio', 'rus'), ('Unknown', 'eng'),
                    ('Unknown', 'und'), ('Unknown', 'rus'), ('', 'rus')]),
            (1, 2, [('Preferred Studio', 'rus'), ('Default Studio', 'rus')]),
            (1, 3, [('Default Studio', 'rus')]),
            (2, 1, [('Season Two Studio', 'jpn'), ('Preferred Studio', 'rus')]),
            (3, 1, [('Only Studio', 'rus')]),
        ]
        for season_number, episode, tracks in fixture:
            command = ['ffmpeg', '-hide_banner', '-loglevel', 'error', '-f', 'lavfi', '-i', 'color=size=160x90:rate=5']
            for index in range(len(tracks)):
                command += ['-f', 'lavfi', '-i', f'sine=frequency={440 + 220 * index}:sample_rate=48000']
            command += ['-map', '0:v']
            for index in range(len(tracks)):
                command += ['-map', f'{index + 1}:a']
            command += ['-t', '3', '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-c:a', 'aac']
            for index, (title, language) in enumerate(tracks):
                command += [f'-metadata:s:a:{index}', f'title={title}', f'-metadata:s:a:{index}', f'language={language}']
            default_index = 1 if (season_number, episode) == (1, 2) else 0
            command += [f'-disposition:a:{default_index}', 'default']
            folder = season if season_number == 1 else season.parent
            command += ['-y', str(folder / f'Audio Fixture S{season_number:02}E{episode:02}.mkv')]
            subprocess.run(command, check=True, stdout=subprocess.DEVNULL)
        base = f'http://127.0.0.1:{port}'
        identity = 'MediaBrowser Client="SeriesAudioSmoke", Device="LocalTest", DeviceId="series-audio-smoke", Version="0.4.0"'
        token = None

        def api(path, method='GET', data=None, expected=200, auth=True):
            headers = {'Authorization': identity + (f', Token="{token}"' if auth and token else ''), 'Accept': 'application/json; profile="PascalCase"'}
            if data is not None:
                headers['Content-Type'] = 'application/json'
            request = urllib.request.Request(base + path, data=json.dumps(data).encode() if data is not None else None, headers=headers, method=method)
            try:
                response = urllib.request.urlopen(request, timeout=20)
            except urllib.error.HTTPError as error:
                response = error
            body = response.read()
            assert response.status == expected, f'{method} {path.split("?")[0]}: expected {expected}, got {response.status}: {body[:400]!r}'
            if response.headers.get('Content-Type', '').startswith('application/json'):
                return json.loads(body) if body else None
            return body

        command = [args.dotnet, args.server, '--nowebclient', '--nonetchange', '--datadir', str(work / 'data'), '--configdir', str(work / 'config'), '--cachedir', str(work / 'cache'), '--logdir', str(work / 'logs'), '--ffmpeg', shutil.which('ffmpeg')]
        log = open(work / 'server.log', 'wb')
        process = None

        def start():
            nonlocal process
            process = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT, env={**os.environ, 'DOTNET_CLI_TELEMETRY_OPTOUT': '1'})
            deadline = time.monotonic() + 60
            while time.monotonic() < deadline:
                if process.poll() is not None:
                    raise RuntimeError('Test server exited during startup')
                try:
                    if api('/System/Info/Public', auth=False).get('Version') == '12.4.0': return
                except (OSError, AssertionError): pass
                time.sleep(0.5)
            raise TimeoutError('Test server startup exceeded 60 seconds')

        def stop():
            if process is not None and process.poll() is None:
                process.terminate()
                try: process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()

        try:
            start()
            print('PASS: Jellyfin 12.4 starts with plugin', flush=True)
            api('/Startup/User')
            password = secrets.token_urlsafe(24)
            api('/Startup/User', 'POST', {'Name': 'smoke-admin', 'Password': password}, 204)
            api('/Startup/Complete', 'POST', expected=204)
            login = api('/Users/AuthenticateByName', 'POST', {'Username': 'smoke-admin', 'Pw': password})
            token = login['AccessToken']
            admin_id = login['User']['Id']
            assert any(p['Name'] == 'Series Audio' for p in api('/Plugins')), 'Plugin not loaded'
            assert b'Series Audio' in api('/SeriesAudio/web.js', auth=False)
            api('/SeriesAudio/Season/00000000000000000000000000000001', auth=False, expected=401)
            api('/Library/VirtualFolders?' + urllib.parse.urlencode({'name': 'Audio Test', 'collectionType': 'tvshows', 'refreshLibrary': 'true'}), 'POST', {
                'LibraryOptions': {'PathInfos': [{'Path': str(work / 'media')}], 'EnableInternetProviders': False, 'EnableRealtimeMonitor': False,
                    'TypeOptions': [{'Type': kind, 'MetadataFetchers': [], 'ImageFetchers': []} for kind in ['Series', 'Season', 'Episode']]}
            }, 204)
            deadline = time.monotonic() + 60
            retry_scan_at = time.monotonic() + 12
            episodes = []
            while time.monotonic() < deadline:
                episodes = api(f'/Items?userId={admin_id}&recursive=true&includeItemTypes=Episode&fields=MediaSources,MediaStreams&sortBy=ParentIndexNumber,IndexNumber')['Items']
                if len(episodes) == len(fixture) and all(e.get('MediaSources') and e['MediaSources'][0].get('MediaStreams') for e in episodes): break
                if retry_scan_at is not None and time.monotonic() >= retry_scan_at:
                    api('/Library/Refresh', 'POST', expected=204)
                    retry_scan_at = None
                time.sleep(1)
            assert len(episodes) == len(fixture), f'Expected {len(fixture)} scanned episodes, got {len(episodes)}'
            season_ids = {e['ParentIndexNumber']: e['SeasonId'] for e in episodes}
            season_one, season_two = [f'/SeriesAudio/Season/{season_ids[n]}' for n in (1, 2)]
            assert len(set(season_ids.values())) == 3 and len({e['SeriesId'] for e in episodes}) == 1
            overview = f'/SeriesAudio/Series/{episodes[0]["SeriesId"]}/Seasons'
            api(overview, auth=False, expected=401)
            api(f'/SeriesAudio/Series/{episodes[0]["Id"]}/Seasons', expected=404)
            route = season_one
            choices = api(route)
            assert 'preference' not in choices or choices['preference'] is None
            assert choices['defaults'] == [{'title': 'Default Studio', 'language': 'rus'}], choices
            assert {(c['preference']['title'], c['preference']['language']) for c in choices['choices']} == {
                ('Default Studio', 'rus'), ('Preferred Studio', 'rus'), ('', 'eng')
            }, choices
            other_choices = api(season_two)['choices']
            assert {c['preference']['title'] for c in other_choices} == {'Season Two Studio', 'Preferred Studio'}, other_choices
            rows = api(overview)
            assert [row['id'].replace('-', '') for row in rows] == [season_ids[n].replace('-', '') for n in (1, 2, 3)], rows
            assert [row['audio']['choices'] for row in rows[:2]] == [choices['choices'], other_choices], rows
            assert len(rows[2]['audio']['choices']) == 1
            assert rows[2]['audio']['defaults'] == [{'title': 'Only Studio', 'language': 'rus'}]
            api(f'/SeriesAudio/Season/{episodes[0]["SeriesId"]}', expected=404)
            choice = next(c for c in choices['choices'] if c['preference']['title'] == 'Preferred Studio')
            selection = {k: choice[k] for k in ['episodeId', 'mediaSourceId', 'audioStreamIndex']}
            api(route, 'PUT', {**selection, 'audioStreamIndex': -1}, 400)
            api(route, 'PUT', {**selection, 'mediaSourceId': None}, 400)
            api(season_two, 'PUT', selection, 400)
            foreign_choice = next(c for c in other_choices if c['preference']['title'] == 'Season Two Studio')
            foreign_selection = {k: foreign_choice[k] for k in selection}
            api(route, 'PUT', foreign_selection, 400)
            api(route, 'PUT', selection, 204)
            assert {t['title'] for t in api(route)['defaults']} == {'Default Studio', 'Preferred Studio'}
            assert api(season_two).get('preference') is None
            print('PASS: separate season catalogs, unnamed/ambiguous tracks and cross-season validation', flush=True)

            profile = {
                'Name': 'Smoke HLS', 'MaxStreamingBitrate': 10000000,
                'TranscodingProfiles': [{'Container': 'ts', 'Type': 'Video', 'VideoCodec': 'h264', 'AudioCodec': 'aac', 'Protocol': 'hls', 'Context': 'Streaming'}]
            }
            for episode in episodes:
                source_id = episode['MediaSources'][0]['Id']
                result = api(f'/Items/{episode["Id"]}/PlaybackInfo', 'POST', {
                    'UserId': admin_id, 'MediaSourceId': source_id, 'DeviceProfile': profile,
                    'EnableDirectPlay': False, 'EnableDirectStream': False, 'EnableTranscoding': True
                })
                source = result['MediaSources'][0]
                streams = [s for s in source['MediaStreams'] if s['Type'] == 'Audio']
                wanted = streams[0]
                if episode['ParentIndexNumber'] == 1:
                    wanted = next((s for s in streams if s.get('Title') == 'Preferred Studio'), wanted)
                assert source['DefaultAudioStreamIndex'] == wanted['Index'], 'Incorrect audio after reorder/fallback'
                details = api(f'/Users/{admin_id}/Items/{episode["Id"]}')
                assert details['MediaSources'][0]['DefaultAudioStreamIndex'] == wanted['Index'], 'Episode page shows a different default'
                url = source.get('TranscodingUrl', '')
                query = urllib.parse.parse_qs(urllib.parse.urlsplit(url).query)
                assert query.get('AudioStreamIndex') == [str(wanted['Index'])], 'Transcoding URL uses incorrect audio'
            print('PASS: reordered tracks, fallback, season isolation and HLS audio indices', flush=True)

            api(season_two, 'PUT', foreign_selection, 204)
            rows = api(overview)
            assert [row['audio']['preference']['title'] for row in rows[:2]] == ['Preferred Studio', 'Season Two Studio']
            assert {t['title'] for t in rows[0]['audio']['defaults']} == {'Default Studio', 'Preferred Studio'}
            print('PASS: series, season and episode report effective defaults and per-episode fallback', flush=True)

            # Another profile must retain Jellyfin defaults and cannot edit the admin.
            other = api('/Users/New', 'POST', {'Name': 'smoke-viewer', 'Password': password})
            other_id = other['Id']
            admin_token = token
            token = api('/Users/AuthenticateByName', 'POST', {'Username': 'smoke-viewer', 'Pw': password})['AccessToken']
            assert api(route).get('preference') is None
            assert all(row['audio'].get('preference') is None for row in api(overview + f'?userId={admin_id}'))
            api(route + f'?userId={admin_id}', 'PUT', selection, 403)
            source = api(f'/Items/{episodes[0]["Id"]}/PlaybackInfo?userId={other_id}')['MediaSources'][0]
            assert source['DefaultAudioStreamIndex'] == 1, 'Preference leaked between users'
            token = admin_token
            print('PASS: profile isolation and cross-profile authorization', flush=True)
            stop()
            start()
            assert api(route)['preference']['title'] == 'Preferred Studio'
            api(route, 'DELETE', expected=204)
            assert api(route).get('preference') is None
            assert api(route)['defaults'] == [{'title': 'Default Studio', 'language': 'rus'}]
            assert api(season_two)['preference']['title'] == 'Season Two Studio'
            print('PASS: persistence across restart and reset', flush=True)
        except Exception:
            output = root / 'artifacts/smoke-server.log'
            output.parent.mkdir(exist_ok=True)
            log.flush()
            shutil.copy2(work / 'server.log', output)
            print(f'Test server log retained locally: {output}', flush=True)
            raise
        finally:
            stop()
            log.close()


if __name__ == '__main__':
    main()
