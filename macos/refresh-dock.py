#!/usr/bin/env python3
"""Refresh only Vision's Dock tiles; no optional dockutil dependency."""

import argparse
import os
import plistlib
import subprocess
import time
import uuid
from pathlib import Path
from urllib.parse import unquote, urlparse


APP = Path('/Applications/Hollow Knight Vision.app')
BUNDLE_ID = 'com.ballroller.hollow-knight-vision'
LABEL = 'Hollow Knight Vision'
DOMAIN = 'com.apple.dock'
SECTIONS = ('persistent-apps', 'persistent-others', 'recent-apps')


def app_path(tile):
    data = tile.get('tile-data', {}).get('file-data', {})
    raw = data.get('_CFURLString', '')
    return unquote(urlparse(raw).path).rstrip('/') if raw.startswith('file:') else raw.rstrip('/')


def is_vision(tile):
    data = tile.get('tile-data', {})
    bundle = data.get('bundle-identifier', '')
    # A known, different bundle wins over a coincidentally matching label.
    if bundle:
        return bundle == BUNDLE_ID
    path = app_path(tile)
    return (Path(path).name == APP.name
            or (not path and data.get('file-label') == LABEL))


def has_pin(preferences):
    return any(is_vision(tile) for section in SECTIONS[:2]
               for tile in preferences.get(section, []))


def refreshed_sections(preferences, pin=False):
    fresh = {
        'GUID': uuid.uuid4().int & 0xFFFFFFFF,
        'tile-type': 'file-tile',
        'tile-data': {
            'bundle-identifier': BUNDLE_ID,
            'file-label': LABEL,
            'file-type': 41,
            'file-data': {'_CFURLString': APP.as_uri() + '/', '_CFURLStringType': 15},
        },
    }
    # Recreate Vision's tile without its old bookmark, icon cache or file IDs.
    # Keep its first pinned position and leave every unrelated tile untouched.
    output = {}
    inserted = False
    apps = []
    for tile in preferences.get('persistent-apps', []):
        if is_vision(tile):
            if not inserted:
                apps.append(fresh)
                inserted = True
        else:
            apps.append(tile)
    if (pin or has_pin(preferences)) and not inserted:
        apps.append(fresh)
    output['persistent-apps'] = apps
    for section in SECTIONS[1:]:
        if section in preferences:
            output[section] = [tile for tile in preferences[section] if not is_vision(tile)]
    return output


def read_preferences(domain=DOMAIN):
    return plistlib.loads(subprocess.check_output(['/usr/bin/defaults', 'export', domain, '-']))


def write_section(section, tiles, domain=DOMAIN):
    # defaults writes through cfprefsd. Editing the plist file directly can be
    # overwritten by its cached copy. Write only changed arrays, never the domain.
    subprocess.run(['/usr/bin/defaults', 'write', domain, section, '-array',
                    *(plistlib.dumps(tile).decode() for tile in tiles)], check=True)


def verify(preferences, expected_pin):
    pins = [tile for tile in preferences.get('persistent-apps', []) if is_vision(tile)]
    return (len(pins) == int(expected_pin)
            and all(app_path(tile) == str(APP) for tile in pins)
            and all(not is_vision(tile) for section in SECTIONS[1:]
                    for tile in preferences.get(section, [])))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pin', action='store_true', help='Add Vision if it is not already pinned')
    args = parser.parse_args()
    if not APP.is_dir():
        parser.error(f'Installed application is missing: {APP}')
    before = read_preferences()
    expected_pin = args.pin or has_pin(before)
    for section, tiles in refreshed_sections(before, args.pin).items():
        if tiles != before.get(section, []):
            if read_preferences().get(section, []) != before.get(section, []):
                raise RuntimeError('Dock changed during installation; run the installer again')
            write_section(section, tiles)
    # Clear transient tiles belonging to the old process as well as icon caches.
    # This does not change Dock settings or pins for other applications.
    result = subprocess.run(['/usr/bin/killall', '-u', str(os.getuid()), 'Dock'],
                            capture_output=True)
    if result.returncode not in (0, 1):
        raise RuntimeError(f'Could not refresh Dock: {result.stderr.decode()}')
    for _ in range(20):
        if verify(read_preferences(), expected_pin):
            print('Dock verified: one installed Vision pin' if expected_pin else 'Dock verified: Vision remains unpinned')
            return
        time.sleep(0.25)
    raise RuntimeError('Dock verification failed: stale or duplicate Vision entry remains')


if __name__ == '__main__':
    main()
