"""Gate contract tests use owned files/Git repositories; no real remote or Jellyfin."""
import hashlib
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location("reported_issue_gate", Path(__file__).resolve().parents[1] / "reported_issue_gate.py")
gate = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(gate)


class ReportedIssueGateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="md-gate-test-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.required = [{"id": "issue-89", "tests": ["UncTest"], "acceptance": "real owned UNC workflow"}]
        self.evidence = self.root / "docs/testing/reported-issues-evidence.json"
        self.evidence.parent.mkdir(parents=True)
        self.artifact = self.root / "docs/testing/reported-issues-evidence/unc.json"
        self.artifact.parent.mkdir()
        self.digest = "a" * 64
        self.record = {"caseId": "issue-89", "sourceDigest": self.digest, "environment": "Owned Windows fixture",
                       "checks": [{"name": "source restored with matching hash", "passed": True}]}

    def evidence_problems(self, deferral=None, deferred=None):
        self.artifact.write_text(json.dumps(self.record), encoding="utf-8")
        index = {"schemaVersion": 1, "cases": {"issue-89": {
            "artifact": self.artifact.relative_to(self.root).as_posix(),
            "sha256": hashlib.sha256(self.artifact.read_bytes()).hexdigest()}}}
        if deferral is not None:
            index["releaseDeferral"] = deferral
        self.evidence.write_text(json.dumps(index), encoding="utf-8")
        if not (self.root / ".git").exists():
            gate.git(self.root, "init", "--quiet")
        gate.git(self.root, "add", "-f", "docs/testing/reported-issues-evidence.json", "docs/testing/reported-issues-evidence/unc.json")
        gate.git(self.root, "-c", "user.name=Owned gate test", "-c", "user.email=gate@example.invalid", "commit", "--quiet", "--allow-empty", "-m", "Owned evidence fixture")
        return gate.check_acceptance(self.root, self.evidence, self.digest, self.required, deferred)

    def test_matching_source_and_all_passing_checks_are_accepted(self):
        self.assertEqual([], self.evidence_problems())

    def test_release_deferral_only_applies_to_named_report_cases_on_the_exact_source(self):
        self.record["checks"][0]["passed"] = False
        deferral = {"sourceDigest": self.digest, "cases": ["issue-89"]}
        deferred = []
        self.assertEqual([], self.evidence_problems(deferral, deferred))
        self.assertEqual(["issue-89"], deferred)
        deferral["sourceDigest"] = "b" * 64
        self.assertTrue(self.evidence_problems(deferral))
        deferral.update(sourceDigest=self.digest, cases=["media-docker-smoke"])
        self.assertTrue(any("unsupported" in error for error in self.evidence_problems(deferral)))

    def test_media_acceptance_requires_each_named_check(self):
        self.required[0]["requiredChecks"] = ["full-decode", "source-restored"]
        self.record["checks"] = [{"name": "full-decode", "passed": True}]
        self.assertTrue(any("source-restored" in problem for problem in self.evidence_problems()))
        self.record["checks"].append({"name": "source-restored", "passed": True})
        self.assertEqual([], self.evidence_problems())

    def test_gate_loads_media_and_reported_issue_requirements(self):
        root = Path(__file__).resolve().parents[2]
        reports, media = gate.load_requirements(root)
        self.assertEqual(10, len(reports))
        self.assertEqual({
            "media-host-matrix", "media-live-workflows", "media-docker-smoke",
            "media-fate", "media-matroska", "media-hevc",
            "media-epub",
        }, {case["id"] for case in media})
        for case in media:
            self.assertTrue(case["requiredChecks"], case["id"])

    def test_missing_evidence_fails_with_the_missing_case(self):
        self.assertTrue(any("issue-89" in p for p in gate.check_acceptance(self.root, self.evidence, self.digest, self.required)))

    def test_empty_case_map_is_not_acceptance(self):
        self.evidence.write_text('{"schemaVersion":1,"cases":{}}')
        self.assertTrue(gate.check_acceptance(self.root, self.evidence, self.digest, self.required))

    def test_old_source_evidence_is_rejected(self):
        self.record["sourceDigest"] = "b" * 64
        self.assertTrue(self.evidence_problems())

    def test_evidence_for_another_case_is_rejected(self):
        self.record["caseId"] = "issue-92"
        self.assertTrue(self.evidence_problems())

    def test_failed_skipped_empty_or_string_boolean_checks_are_rejected(self):
        for checks in ([], [{"name": "failed", "passed": False}], [{"name": "skipped", "passed": True, "skipped": True}],
                       [{"name": "not a boolean", "passed": "true"}], [{"name": "", "passed": True}]):
            with self.subTest(checks=checks):
                self.record["checks"] = checks
                self.assertTrue(self.evidence_problems())

    def test_changed_artifact_is_rejected(self):
        self.assertEqual([], self.evidence_problems())
        self.artifact.write_text("changed after acceptance")
        self.assertTrue(gate.check_acceptance(self.root, self.evidence, self.digest, self.required))

    def test_ignored_local_artifact_cannot_certify_an_outgoing_commit_that_lacks_it(self):
        self.assertEqual([], self.evidence_problems())
        relative = self.artifact.relative_to(self.root).as_posix()
        gate.git(self.root, "rm", "--cached", "--", relative)
        gate.git(self.root, "-c", "user.name=Owned gate test", "-c", "user.email=gate@example.invalid", "commit", "--quiet", "-m", "Owned missing-artifact fixture")
        (self.root / ".git/info/exclude").write_text(relative + "\n")
        self.assertEqual(b"", gate.git(self.root, "status", "--porcelain").strip())
        self.assertTrue(any("artifact is not committed" in problem for problem in gate.check_acceptance(self.root, self.evidence, self.digest, self.required)))

    def test_artifacts_outside_the_committed_evidence_directory_are_rejected(self):
        self.evidence_problems()
        index = json.loads(self.evidence.read_text())
        index["cases"]["issue-89"]["artifact"] = "../outside.json"
        self.evidence.write_text(json.dumps(index))
        self.assertTrue(gate.check_acceptance(self.root, self.evidence, self.digest, self.required))

    def test_zero_tests_skip_failure_and_missing_coverage_are_rejected(self):
        path = self.root / "tests.trx"
        for results in ("", '<UnitTestResult testName="UncTest" outcome="NotExecuted"/>',
                        '<UnitTestResult testName="UncTest" outcome="Failed"/>',
                        '<UnitTestResult testName="OtherTest" outcome="Passed"/>'):
            with self.subTest(results=results):
                path.write_text(f'<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>{results}</Results></TestRun>')
                self.assertTrue(gate.check_trx(path, self.required))
        path.write_text('<TestRun><Results><UnitTestResult testName="UncTest" outcome="Passed"/></Results></TestRun>')
        self.assertEqual([], gate.check_trx(path, self.required))

    def test_missing_or_malformed_trx_is_rejected(self):
        path = self.root / "missing.trx"
        self.assertTrue(gate.check_trx(path, self.required))
        path.write_text("not XML")
        self.assertTrue(gate.check_trx(path, self.required))

    def test_ui_tests_cannot_pass_with_skips_or_no_results(self):
        self.assertTrue(gate.check_tap(""))
        self.assertTrue(gate.check_tap("# tests 0\n# pass 0\n# skipped 0\n"))
        self.assertTrue(gate.check_tap("# tests 2\n# pass 1\n# skipped 1\n"))
        self.assertTrue(gate.check_tap("# tests 1\n# pass 1\n# todo 1\n"))
        self.assertEqual([], gate.check_tap("# tests 2\n# pass 2\n# fail 0\n# skipped 0\n"))

    def make_repo(self):
        gate.git(self.root, "init", "--quiet")
        (self.root / "source.cs").write_text("owned source\n")
        gate.git(self.root, "add", "source.cs")
        gate.git(self.root, "-c", "user.name=Owned gate test", "-c", "user.email=gate@example.invalid", "commit", "--quiet", "-m", "Owned fixture")
        # Evidence fixture files are not inputs to these clean-source tests.
        (self.root / ".git/info/exclude").write_text("docs/\n")
        return gate.git(self.root, "rev-parse", "HEAD").decode().strip()

    def test_code_changes_invalidate_evidence_but_text_line_endings_and_docs_do_not(self):
        self.make_repo()
        before = gate.source_digest(self.root)
        (self.root / "source.cs").write_bytes(b"owned source\r\n")
        self.assertEqual(before, gate.source_digest(self.root))
        (self.root / "docs/note.md").write_text("acceptance notes")
        self.assertEqual(before, gate.source_digest(self.root))
        (self.root / "source.cs").write_text("changed source\n")
        self.assertNotEqual(before, gate.source_digest(self.root))

    def test_push_checks_require_clean_source_and_the_outgoing_commit(self):
        head = self.make_repo()
        records = f"refs/heads/main {head} refs/heads/main {'0' * 40}\n"
        self.assertEqual(head, gate.check_push_source(self.root, records))
        (self.root / "source.cs").write_text("uncommitted repair")
        with self.assertRaisesRegex(ValueError, "dirty working-tree"):
            gate.check_push_source(self.root, records)
        gate.git(self.root, "add", "source.cs")
        gate.git(self.root, "-c", "user.name=Owned gate test", "-c", "user.email=gate@example.invalid", "commit", "--quiet", "-m", "New fixture HEAD")
        with self.assertRaisesRegex(ValueError, "differs from tested HEAD"):
            gate.check_push_source(self.root, records)

    def test_missing_or_malformed_push_ref_records_are_rejected(self):
        self.make_repo()
        for records in ("", "missing fields", "HEAD --help refs/heads/main " + "0" * 40):
            with self.subTest(records=records), self.assertRaises(ValueError):
                gate.check_push_source(self.root, records)

    def test_index_flags_cannot_hide_a_repair_that_is_not_in_the_outgoing_commit(self):
        head = self.make_repo()
        for flag in ("assume-unchanged", "skip-worktree"):
            with self.subTest(flag=flag):
                gate.git(self.root, "update-index", "--" + flag, "source.cs")
                (self.root / "source.cs").write_text("hidden uncommitted repair")
                self.assertEqual(b"", gate.git(self.root, "status", "--porcelain").strip())
                with self.assertRaisesRegex(ValueError, "assume-unchanged/skip-worktree"):
                    gate.check_push_source(self.root, f"HEAD {head} refs/heads/main {'0' * 40}")
                gate.git(self.root, "update-index", "--no-" + flag, "source.cs")
                (self.root / "source.cs").write_text("owned source\n")

    def install_hook_fixture(self):
        head = self.make_repo()
        source_root = Path(__file__).resolve().parents[2]
        for relative in ("tools/install-push-gate.ps1", "tools/reported_issue_gate.py", ".githooks/pre-push"):
            target = self.root / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source_root / relative, target)
        gate.git(self.root, "add", "tools", ".githooks")
        gate.git(self.root, "-c", "user.name=Owned gate test", "-c", "user.email=gate@example.invalid", "commit", "--quiet", "-m", "Owned hook fixture")
        result = subprocess.run(["pwsh", "-NoProfile", "-NonInteractive", "-File", str(self.root / "tools/install-push-gate.ps1")],
                                capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        return head

    def test_installed_hook_rejects_a_real_push_to_an_owned_local_remote(self):
        self.install_hook_fixture()
        remote = tempfile.TemporaryDirectory(prefix="md-gate-remote-")
        self.addCleanup(remote.cleanup)
        subprocess.run(["git", "init", "--bare", "--quiet", remote.name], check=True)
        (self.root / "source.cs").write_text("uncommitted repair must not certify HEAD")
        result = subprocess.run(["git", "-c", f"safe.directory={self.root.as_posix()}", "-C", str(self.root),
                                 "push", remote.name, "HEAD:refs/heads/owned-gate-check"], capture_output=True, text=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("dirty working-tree", result.stderr + result.stdout)
        published = subprocess.run(["git", "--git-dir", remote.name, "show-ref", "--verify", "refs/heads/owned-gate-check"], capture_output=True)
        self.assertNotEqual(0, published.returncode)

    def test_installer_preserves_an_existing_different_hook(self):
        self.install_hook_fixture()
        hook = self.root / ".git/hooks/pre-push"
        original = "#!/bin/sh\necho owned previous hook\n"
        hook.write_text(original)
        result = subprocess.run(["pwsh", "-NoProfile", "-NonInteractive", "-File", str(self.root / "tools/install-push-gate.ps1")], capture_output=True)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(original, hook.read_text())

    def test_every_report_family_has_automated_and_external_acceptance_requirements(self):
        manifest = json.loads((Path(__file__).resolve().parents[1] / "reported-issues/requirements.json").read_text())
        self.assertEqual({"issue-80", "issue-81-83", "issue-81-progress", "issue-86", "issue-87", "issue-88", "issue-89", "issue-92", "issue-43", "reddit-disappearing"}, {case["id"] for case in manifest["cases"]})
        for case in manifest["cases"]:
            self.assertTrue(case["tests"])
            self.assertTrue(case["acceptance"].strip())


if __name__ == "__main__":
    unittest.main()
