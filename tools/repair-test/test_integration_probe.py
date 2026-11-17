import importlib.util
import io
import json
from pathlib import Path
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('integration_probe', Path(__file__).resolve().parents[1] / 'integration_probe.py')
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


class IntegrationProbeContracts(unittest.TestCase):
    def test_authenticated_probe_replaces_bootstrap_authorization(self):
        class StopAfterAuthentication(Exception):
            pass

        def open_request(request, timeout):
            if request.full_url.endswith('/Plugins'):
                self.assertIn('Token="owned-token"', request.get_header('Authorization'))
                raise StopAfterAuthentication()
            body = {'AccessToken': 'owned-token'} if request.full_url.endswith('/AuthenticateByName') else {}
            return io.BytesIO(json.dumps(body).encode())

        with patch.object(probe.urllib.request, 'build_opener') as opener:
            opener.return_value.open.side_effect = open_request
            with self.assertRaises(StopAfterAuthentication):
                probe.run(8102)

    def test_old_idle_completion_is_not_success(self):
        result = dict(StartTimeUtc='old', EndTimeUtc='old-end', Status='Completed')
        self.assertFalse(probe.completed(result, dict(State='Idle', LastExecutionResult=result)))

    def test_failed_scan_does_not_pass(self):
        with self.assertRaises(RuntimeError):
            probe.completed({}, dict(State='Idle', LastExecutionResult=dict(StartTimeUtc='new', EndTimeUtc='end', Status='Failed')))

    def test_fast_completed_scan_is_accepted(self):
        task = dict(State='Idle', LastExecutionResult=dict(StartTimeUtc='new', EndTimeUtc='end', Status='Completed'))
        self.assertEqual(task, probe.completed({}, task))

    def test_missing_or_wrong_issue_shape_cannot_pass(self):
        for rows in ([], {}, [dict(Type='Duplicate')]):
            with self.subTest(rows=rows), self.assertRaises((AssertionError, ValueError)):
                probe.assert_counts(rows)

    def test_wait_is_bounded(self):
        with patch.object(probe.time, 'monotonic', side_effect=[0, 0, 100]), patch.object(probe.time, 'sleep'):
            with self.assertRaises(TimeoutError):
                probe.wait_for(lambda: False, 2, 'not ready')
