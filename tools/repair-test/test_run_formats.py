import json
import tempfile
import unittest
from io import BytesIO
from pathlib import Path
from unittest.mock import Mock
from urllib.error import HTTPError

from run_formats import Formats
from run_live import sha


class FixtureSourceContracts(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root / 'source'
        (self.source / 'media').mkdir(parents=True)
        self.harness = Formats.__new__(Formats)
        self.harness.media = self.root / 'destination'
        self.harness.media.mkdir()
        self.harness.record = Mock()
        self.harness.save = Mock()

    def manifest(self, entries):
        (self.source / 'fixtures.json').write_text(json.dumps(entries), encoding='utf-8')

    def test_empty_fixture_source_is_refused(self):
        self.manifest([])
        with self.assertRaisesRegex(ValueError, 'empty'):
            self.harness.load_fixture_source(self.source)

    def test_hash_mismatch_stops_before_copy_or_fix(self):
        media = self.source / 'media' / 'movie.mp4'
        media.write_bytes(b'changed')
        self.manifest([dict(case='movie', path=str(media), sha256='0' * 64)])
        with self.assertRaisesRegex(ValueError, 'hash'):
            self.harness.load_fixture_source(self.source)
        self.assertEqual([], list(self.harness.media.iterdir()))

    def test_escaped_manifest_path_is_refused(self):
        foreign = self.root / 'foreign.mp4'
        foreign.write_bytes(b'foreign')
        self.manifest([dict(case='foreign', path=str(foreign), sha256=sha(foreign))])
        with self.assertRaises(ValueError):
            self.harness.load_fixture_source(self.source)

    def test_valid_source_is_rebased_and_copied(self):
        media = self.source / 'media' / 'movie.mp4'
        media.write_bytes(b'owned fixture')
        self.manifest([dict(case='movie', path=str(media), sha256=sha(media))])
        self.harness.load_fixture_source(self.source)
        output = Path(self.harness.fixtures[0]['path'])
        self.assertEqual(self.harness.media / 'movie.mp4', output)
        self.assertEqual(b'owned fixture', output.read_bytes())

    def test_report_only_document_requires_the_expected_approval_refusal(self):
        issue = dict(Id=42, Type='Playability', Status='Detected')
        self.harness.api = Mock(side_effect=HTTPError('', 409, 'Conflict', None, BytesIO(b'This finding is report-only.')))
        self.harness.verify_report_only(dict(case='broken-pdf'), [issue])
        self.harness.api.assert_called_once_with('/MediaDash/Issues/42/Approve', 'POST')
        self.harness.record.assert_called_once_with('broken-pdf report-only approval refused', True, 409)

    def test_unrelated_approval_conflict_does_not_count_as_report_only(self):
        issue = dict(Id=42, Type='Playability', Status='Detected')
        self.harness.api = Mock(side_effect=HTTPError('', 409, 'Conflict', None, BytesIO(b'Library disabled')))
        self.harness.verify_report_only(dict(case='broken-pdf'), [issue])
        self.harness.record.assert_called_once_with('broken-pdf report-only approval refused', False, 409)


if __name__ == '__main__':
    unittest.main()
