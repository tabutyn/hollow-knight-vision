import copy
import importlib.util
import plistlib
import subprocess
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location('refresh_dock', Path(__file__).with_name('refresh-dock.py'))
dock = importlib.util.module_from_spec(spec)
spec.loader.exec_module(dock)


def tile(label, path, bundle=None):
    data = {'file-label': label, 'file-data': {'_CFURLString': path, '_CFURLStringType': 15}}
    if bundle is not None:
        data['bundle-identifier'] = bundle
    return {'GUID': 7, 'tile-type': 'file-tile', 'tile-data': data}


class DockRefreshTests(unittest.TestCase):
    def setUp(self):
        self.other = tile('Other App', 'file:///Applications/Other.app/', 'example.other')
        self.game = tile('Hollow Knight', 'file:///Games/hollow_knight.app/', 'unity.Team Cherry.Hollow Knight')
        self.vision = tile(dock.LABEL, dock.APP.as_uri() + '/', dock.BUNDLE_ID)
        self.old = tile('Old Vision', 'file:///tmp/build/Hollow%20Knight%20Vision.app/', dock.BUNDLE_ID)

    def test_duplicate_pins_collapse_at_first_position(self):
        before = {'persistent-apps': [self.other, self.old, self.game, self.vision]}
        saved = copy.deepcopy(before)
        after = dock.refreshed_sections(before)
        self.assertEqual([item['tile-data']['file-label'] for item in after['persistent-apps']],
                         ['Other App', dock.LABEL, 'Hollow Knight'])
        self.assertEqual(after['persistent-apps'][0], self.other)
        self.assertEqual(after['persistent-apps'][2], self.game)
        self.assertEqual(before, saved)
        self.assertTrue(dock.verify(after, True))

    def test_bookmark_and_cached_icon_metadata_are_discarded(self):
        self.vision['tile-data'].update({'book': b'stale bookmark', 'file-mod-date': 123, 'parent-mod-date': 456})
        after = dock.refreshed_sections({'persistent-apps': [self.vision]})
        data = after['persistent-apps'][0]['tile-data']
        self.assertNotIn('book', data)
        self.assertNotIn('file-mod-date', data)
        self.assertNotIn('parent-mod-date', data)
        self.assertEqual(dock.app_path(after['persistent-apps'][0]), str(dock.APP))

    def test_renamed_pin_and_missing_bundle_id_are_recognized(self):
        missing_id = tile('Legacy', '/old/Hollow Knight Vision.app')
        after = dock.refreshed_sections({'persistent-apps': [missing_id, self.old]})
        self.assertTrue(dock.verify(after, True))

    def test_recent_and_wrong_section_copies_are_removed(self):
        before = {'persistent-apps': [self.vision], 'recent-apps': [self.old, self.game],
                  'persistent-others': [self.old, self.other]}
        after = dock.refreshed_sections(before)
        self.assertEqual(after['recent-apps'], [self.game])
        self.assertEqual(after['persistent-others'], [self.other])
        self.assertTrue(dock.verify(after, True))

    def test_pin_in_wrong_section_moves_to_apps(self):
        before = {'persistent-apps': [self.other], 'persistent-others': [self.old]}
        self.assertTrue(dock.has_pin(before))
        after = dock.refreshed_sections(before)
        self.assertTrue(dock.verify(after, True))
        self.assertEqual(after['persistent-apps'][0], self.other)

    def test_unpinned_app_stays_unpinned(self):
        after = dock.refreshed_sections({'persistent-apps': [self.other], 'recent-apps': [self.old]})
        self.assertEqual(after['persistent-apps'], [self.other])
        self.assertTrue(dock.verify(after, False))

    def test_explicit_pin_adds_one(self):
        after = dock.refreshed_sections({'persistent-apps': [self.other]}, pin=True)
        self.assertEqual(after['persistent-apps'][0], self.other)
        self.assertTrue(dock.verify(after, True))

    def test_repeated_updates_never_duplicate_or_reorder(self):
        prefs = {'persistent-apps': [self.other, self.old, self.game, self.vision]}
        for _ in range(5):
            prefs = dock.refreshed_sections(prefs, pin=True)
            self.assertTrue(dock.verify(prefs, True))
            self.assertEqual(prefs['persistent-apps'][0], self.other)
            self.assertEqual(prefs['persistent-apps'][2], self.game)

    def test_different_bundle_with_same_label_is_preserved(self):
        unrelated = tile(dock.LABEL, 'file:///Applications/Other.app/', 'example.other')
        after = dock.refreshed_sections({'persistent-apps': [unrelated]})
        self.assertEqual(after['persistent-apps'], [unrelated])

    def test_verification_rejects_duplicate_and_wrong_paths(self):
        self.assertFalse(dock.verify({'persistent-apps': [self.vision, self.old]}, True))
        self.assertFalse(dock.verify({'persistent-apps': [self.old]}, True))

    def test_defaults_round_trip_preserves_unrelated_preferences(self):
        with tempfile.TemporaryDirectory(prefix='hkv-dock-test-') as temp:
            domain = str(Path(temp) / 'fixture')
            self.other['tile-data']['book'] = b'unrelated bookmark'
            before = {'persistent-apps': [self.other, self.old, self.vision],
                      'recent-apps': [self.old, self.game], 'autohide': True, 'tilesize': 48}
            subprocess.run(['/usr/bin/defaults', 'import', domain, '-'],
                           input=plistlib.dumps(before), check=True)
            for section, entries in dock.refreshed_sections(before).items():
                dock.write_section(section, entries, domain)
            after = dock.read_preferences(domain)
            self.assertTrue(dock.verify(after, True))
            self.assertEqual(after['persistent-apps'][0], self.other)
            self.assertEqual(after['recent-apps'], [self.game])
            self.assertTrue(after['autohide'])
            self.assertEqual(after['tilesize'], 48)
            dock.write_section('recent-apps', [], domain)
            self.assertEqual(dock.read_preferences(domain)['recent-apps'], [])


if __name__ == '__main__':
    unittest.main()
