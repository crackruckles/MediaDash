import unittest
import run_live as h

class HarnessContracts(unittest.TestCase):
    def test_reject_remote_credentials_and_non_http_urls(self):
        for url in ['http://192.168.1.117:8096', 'http://localhost.evil:8099', 'file:///tmp/server', 'http://test:secret@localhost:8099', 'http://localhost:8099?x=1', 'http://localhost:8099/#token']:
            with self.subTest(url=url), self.assertRaises(ValueError): h.validate_url(url)

    def test_accept_loopback_and_base_path(self):
        for url in ['http://localhost:8099', 'http://127.0.0.1:8099/jellyfin', 'http://[::1]:8099']:
            self.assertEqual(url, h.validate_url(url+'/'))

    def test_old_idle_result_is_not_a_new_run(self):
        old={'StartTimeUtc':'2026-01-01','EndTimeUtc':'2026-01-02','Status':'Completed'}
        self.assertFalse(h.fresh_completion(old, {'State':'Idle','LastExecutionResult':old}))

    def test_running_or_cancelling_is_not_complete(self):
        for state in ['Running','Cancelling']:
            self.assertFalse(h.fresh_completion({}, {'State':state,'LastExecutionResult':{'StartTimeUtc':'new','Status':'Completed'}}))

    def test_fast_new_run_can_finish_between_polls(self):
        self.assertTrue(h.fresh_completion({}, {'State':'Idle','LastExecutionResult':{'StartTimeUtc':'new','EndTimeUtc':'end','Status':'Completed'}}))

    def test_failed_new_run_is_not_success(self):
        for status in ['Failed','Cancelled','Aborted']:
            with self.subTest(status=status), self.assertRaises(RuntimeError):
                h.fresh_completion({}, {'State':'Idle','LastExecutionResult':{'StartTimeUtc':'new','EndTimeUtc':'end','Status':status}})

    def test_exit_codes_do_not_hide_failure_or_missing_coverage(self):
        for results,expected in [([],2),([{'status':'PASS'}],0),([{'status':'FAIL'}],1),([{'status':'BLOCKED'}],2),([{'status':'PASS'},{'status':'FAIL'}],1),([{'status':'FAIL'},{'status':'BLOCKED'}],1)]:
            with self.subTest(results=results): self.assertEqual(expected,h.exit_code(results))

    def test_current_history_must_match_id_and_full_path(self):
        rows=[{'id':1,'issue_id':7,'path':'A/movie.mkv'}, {'id':3,'issue_id':8,'path':'A/movie.mkv'}, {'id':4,'issue_id':7,'path':'B/movie.mkv'}, {'id':5,'issue_id':7,'path':'A/movie.mkv'}]
        self.assertEqual([rows[-1]],h.match_rows(rows,7,'A/movie.mkv',2))


class HarnessWorkflowContracts(unittest.TestCase):
    def make_harness(self):
        from types import SimpleNamespace
        harness = h.Harness.__new__(h.Harness)
        harness.args = SimpleNamespace(timeout=1)
        harness.owned_tasks = set()
        harness.record = lambda *args, **kwargs: None
        return harness

    def test_task_http_rejection_does_not_pass(self):
        from unittest.mock import Mock
        harness = self.make_harness()
        harness.task = Mock(return_value={'State':'Idle'})
        harness.api = Mock(side_effect=RuntimeError('HTTP 409'))
        with self.assertRaisesRegex(RuntimeError, '409'):
            harness.run_task('MediaDashFix','/MediaDash/Fix')
        self.assertIn('MediaDashFix',harness.owned_tasks)

    def test_running_existing_task_is_not_hijacked(self):
        from unittest.mock import Mock
        harness = self.make_harness()
        harness.task = Mock(return_value={'State':'Running'})
        harness.api = Mock()
        with self.assertRaisesRegex(RuntimeError, 'overlap'):
            harness.run_task('MediaDashFix','/MediaDash/Fix')
        harness.api.assert_not_called()
        self.assertFalse(harness.owned_tasks)

    def test_old_idle_timeout_is_failure(self):
        from unittest.mock import Mock, patch
        harness = self.make_harness()
        harness.api = Mock()
        harness.task = Mock(return_value={'State':'Idle','LastExecutionResult':{'StartTimeUtc':'old','EndTimeUtc':'old-end','Status':'Completed'}})
        with patch.object(h.time,'monotonic',side_effect=[0,.1,2]), patch.object(h.time,'sleep'):
            with self.assertRaises(TimeoutError): harness.run_task('MediaDashFix','/MediaDash/Fix')
        self.assertIn('MediaDashFix',harness.owned_tasks)

    def test_fast_completion_is_attributed_and_releases_ownership(self):
        from unittest.mock import Mock
        harness = self.make_harness()
        harness.api = Mock()
        harness.task = Mock(side_effect=[{'State':'Idle'}, {'State':'Idle','LastExecutionResult':{'StartTimeUtc':'new','EndTimeUtc':'new-end','Status':'Completed'}}])
        harness.run_task('MediaDashFix','/MediaDash/Fix')
        self.assertFalse(harness.owned_tasks)

    def test_config_timeout_still_marks_restoration_required(self):
        from unittest.mock import Mock
        harness = self.make_harness()
        harness.config_path='/Plugins/test/Configuration'
        harness.changed=False
        harness.api=Mock(side_effect=TimeoutError('response lost'))
        with self.assertRaises(TimeoutError): harness.configure({'DryRun':True})
        self.assertTrue(harness.changed)

    def test_redirect_never_forwards_token(self):
        with self.assertRaisesRegex(RuntimeError, 'redirect'):
            h.NoRedirect().redirect_request(None,None,302,'',{},'https://elsewhere.invalid')

    def test_stream_count_checks_every_audio_track(self):
        self.assertEqual({'video':1,'audio':2,'subtitle':1},h.stream_counts({'streams':[{'codec_type':'video'},{'codec_type':'audio'},{'codec_type':'audio'},{'codec_type':'subtitle'}]}))

    def test_case_catalog_has_unique_ids_and_all_five_rungs(self):
        self.assertEqual(len(h.CASES),len({c[0] for c in h.CASES}))
        self.assertTrue(set(h.RUNGS).issubset({c[4] for c in h.CASES}))
        self.assertTrue({'healthy','repair','review-delete','refuse','rename','collision'}.issubset({c[3] for c in h.CASES}))

    def test_issue_queries_include_queued_and_keep_owned_scope(self):
        from pathlib import Path
        from unittest.mock import Mock
        harness = self.make_harness()
        harness.media=Path('owned').resolve()
        ours={'Path':str(harness.media/'movie.mp4'), 'Status':'Queued'}
        foreign={'Path':str(harness.media.parent/'foreign.mp4'), 'Status':'Queued'}
        harness.api=Mock(return_value=[ours,foreign])
        self.assertEqual([ours],harness.issues())
        self.assertIn('openOnly=true',harness.api.call_args.args[0])
        self.assertEqual([ours,foreign],harness.open_issues())
        self.assertIn('openOnly=true',harness.api.call_args.args[0])



    def cleanup_harness(self):
        from pathlib import Path
        from unittest.mock import Mock
        harness=self.make_harness()
        harness.root=Path('owned').resolve()
        harness.added=[]
        harness.changed=True
        harness.config_path='/Plugins/test/Configuration'
        harness.original={'DryRun':False}
        harness.safe={'DryRun':True,'AudioFixMode':'Off','EnabledLibraries':['00000000000000000000000000000000'],'FixerDisabled':['AudioLanguage']}
        harness.held_queue=[]
        harness.run_task=Mock()
        harness.task=Mock(return_value={'State':'Idle'})
        harness.idle=Mock()
        harness.open_issues=Mock(return_value=[])
        harness.api=Mock(return_value=harness.original)
        harness.record=Mock()
        return harness

    def test_cleanup_does_not_cancel_someone_elses_task(self):
        harness=self.cleanup_harness()
        harness.cleanup()
        self.assertFalse(any('/Cancel' in call.args[0] for call in harness.api.call_args_list))

    def test_cleanup_restores_queue_before_original_config(self):
        from unittest.mock import Mock
        harness=self.cleanup_harness()
        queued={'Id':44,'Path':str(harness.root.parent/'foreign.mp4'),'Status':'Queued'}
        detected=dict(queued,Status='Detected')
        dismissed=dict(queued,Status='Dismissed')
        harness.held_queue=[queued]
        harness.open_issues=Mock(side_effect=[[detected],[detected],[detected],[queued]])
        harness.api=Mock(side_effect=lambda path,*args,**kwargs: [dismissed] if path=='/MediaDash/Issues?status=Dismissed' else harness.original)
        harness.configure=Mock()
        harness.cleanup()
        restore_config=harness.configure.call_args.args[0]
        self.assertTrue(restore_config['DryRun'])
        self.assertEqual(['00000000000000000000000000000000'],restore_config['EnabledLibraries'])
        self.assertEqual('ManualApprove',restore_config['AudioFixMode'])
        self.assertEqual([],restore_config['FixerDisabled'])
        paths=[call.args[0] for call in harness.api.call_args_list]
        self.assertLess(paths.index('/MediaDash/Issues/44/Revert'), paths.index('/MediaDash/Issues/44/Approve'))
        self.assertLess(paths.index('/MediaDash/Issues/44/Approve'), paths.index(harness.config_path))
        self.assertNotIn('/MediaDash/Issues/44/Dismiss',paths)
        harness.record.assert_any_call('original queue restored',True)

    def test_failed_restore_isolation_never_attempts_reapproval(self):
        from unittest.mock import Mock
        harness=self.cleanup_harness()
        harness.held_queue=[{'Id':44,'Path':str(harness.root.parent/'foreign.mp4'),'Status':'Queued'}]
        harness.configure=Mock(side_effect=RuntimeError('read-back mismatch'))
        harness.cleanup()
        self.assertFalse(any('/Approve' in call.args[0] for call in harness.api.call_args_list))
        self.assertTrue(any(call.args[0]=='queue restoration' and not call.args[1] for call in harness.record.call_args_list))

    def test_unstoppable_owned_task_retains_isolation_and_reports_failure(self):
        from unittest.mock import Mock
        harness=self.cleanup_harness()
        harness.owned_tasks={'MediaDashFix'}
        harness.task=Mock(return_value={'State':'Running'})
        harness.idle=Mock(side_effect=TimeoutError('still running'))
        harness.cleanup()
        self.assertFalse(any(call.args[0]==harness.config_path for call in harness.api.call_args_list))
        self.assertTrue(any(call.args[0]=='cleanup requires manual recovery' and not call.args[1] for call in harness.record.call_args_list))

    def test_cleanup_config_restore_failure_is_reported(self):
        from unittest.mock import Mock
        harness=self.cleanup_harness()
        harness.api=Mock(side_effect=TimeoutError('offline'))
        harness.cleanup()
        self.assertTrue(any(call.args[0]=='config restoration' and not call.args[1] for call in harness.record.call_args_list))



    def test_unicode_diagnostic_cannot_crash_ascii_console(self):
        import io
        from unittest.mock import Mock, patch
        harness=self.make_harness()
        harness.results=[]
        harness.save=Mock()
        buffer=io.BytesIO()
        stream=io.TextIOWrapper(buffer,encoding='ascii')
        with patch.object(h.sys,'stdout',stream):
            h.Harness.record(harness,'unicode',True,'healthy \u2192 repaired \u5065\u5168')
        self.assertEqual('PASS',harness.results[0]['status'])

    def test_each_case_releases_owned_queue_even_after_recorded_failure(self):
        from unittest.mock import Mock
        harness=self.make_harness()
        harness.fixtures=[dict(ready=True,outcome='refuse',case='disabled')]
        harness.exercise=Mock(return_value=None)
        harness.idle=Mock()
        harness.issues=Mock(return_value=[dict(Id=7,Status='Queued')])
        harness.api=Mock()
        harness.exercise_cases({})
        harness.api.assert_called_once_with('/MediaDash/Issues/7/Revert','POST')

    def test_failed_repair_fixture_requires_fresh_delete_consent(self):
        outcomes={case[0]:case[3] for case in h.CASES}
        for case in ('partial','zero','header'):
            self.assertEqual('review-delete',outcomes[case])
        self.assertEqual('refuse',outcomes['disabled'])

    def test_delete_review_uses_warning_json_field_names(self):
        for key in ('blocking','Blocking'):
            self.assertTrue(h.is_delete_review(dict(Status='Detected',RepairFailed=True,Warnings=[{key:True}])))
        self.assertFalse(h.is_delete_review(dict(Status='Queued',RepairFailed=True,Warnings=[{'blocking':True}])))
        self.assertFalse(h.is_delete_review(dict(Status='Detected',RepairFailed=True,Warnings=[])))

    def test_delete_review_never_approves_without_failed_repair_state(self):
        import tempfile
        from pathlib import Path
        from unittest.mock import Mock
        with tempfile.TemporaryDirectory() as root:
            path=Path(root)/'movie.mp4'
            path.write_bytes(b'unchanged')
            harness=self.make_harness()
            harness.record=lambda name,ok,*args,**kwargs: ok
            harness.scan=Mock()
            harness.issues=Mock(return_value=[dict(Id=7,Path=str(path),Status='Detected',RepairFailed=False,Warnings=[])])
            harness.api=Mock()
            harness.review_then_delete(dict(case='review',path=str(path),sha256=h.sha(path)),dict(success=0,recycle_path=None,bytes_freed=0,saved_bytes=0,action='failed'))
            harness.api.assert_not_called()

if __name__=='__main__': unittest.main()
