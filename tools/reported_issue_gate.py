"""Mandatory offline regressions plus source-bound acceptance evidence. No media/service mutations."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys
import uuid
import xml.etree.ElementTree as ET


def git(root, *args):
    return subprocess.run(
        ["git", "-c", f"safe.directory={root.as_posix()}", "-C", str(root), *args],
        check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    ).stdout


def source_digest(root):
    paths = set(git(root, "ls-files", "-z", "--cached", "--others", "--exclude-standard").decode("utf-8").split("\0"))
    digest = hashlib.sha256()
    for name in sorted(paths):
        # Evidence and prose can be committed after testing without invalidating the tested code.
        if not name or name.startswith("docs/") or name.lower().endswith(".md"):
            continue
        path = root / name
        if path.is_symlink() or not path.resolve().is_relative_to(root.resolve()):
            raise ValueError(f"Source symlink cannot be fingerprinted safely: {name}")
        raw = path.read_bytes() if path.is_file() else b"deleted"
        try:
            raw.decode("utf-8")
            if b"\0" not in raw:
                raw = raw.replace(b"\r\n", b"\n")
        except UnicodeDecodeError:
            pass
        content = hashlib.sha256(raw).hexdigest()
        digest.update(f"{name}\0{content}\n".encode("utf-8"))
    return digest.hexdigest()


def check_trx(path, requirements):
    try:
        tree = ET.parse(path)
        results = tree.findall(".//{*}UnitTestResult")
    except (OSError, ET.ParseError) as error:
        return [f"Missing/unreadable .NET results: {error}"]
    problems = []
    if not results:
        problems.append(".NET selected zero tests")
    for result in results:
        if result.get("outcome") != "Passed":
            problems.append(f"{result.get('outcome', 'unknown')}: {result.get('testName', 'unnamed test')}")
    passed = [r.get("testName", "") for r in results if r.get("outcome") == "Passed"]
    for case in requirements:
        for pattern in case["tests"]:
            if not any(pattern in name for name in passed):
                problems.append(f"{case['id']}: no passing coverage for {pattern}")
    return problems


def check_tap(output):
    counters = dict(re.findall(r"^# (tests|pass|fail|skipped|cancelled|todo) (\d+)\s*$", output, re.MULTILINE))
    if not counters or int(counters.get("tests", "0")) == 0:
        return ["UI runner produced no test results"]
    if int(counters.get("pass", "0")) != int(counters["tests"]) or any(int(counters.get(k, "0")) for k in ("fail", "skipped", "cancelled", "todo")):
        return ["UI tests failed, skipped, were cancelled or contain TODOs"]
    return []


def load_requirements(root):
    manifest = json.loads((root / "tools/reported-issues/requirements.json").read_text(encoding="utf-8"))
    reports = manifest["cases"]
    media = manifest["mediaAcceptance"]
    if not reports or not media or len({case["id"] for case in reports + media}) != len(reports) + len(media):
        raise ValueError("Acceptance requirements are empty or duplicated")
    for case in media:
        checks = case.get("requiredChecks")
        if not isinstance(checks, list) or not checks or any(not isinstance(name, str) or not name.strip() for name in checks):
            raise ValueError(f"{case['id']}: missing named media acceptance checks")
    return reports, media


# Report cases whose live acceptance may be deferred for any release, on the exact tested source.
DEFERRABLE_CASES = frozenset(("issue-81-progress", "issue-87", "issue-89", "issue-92"))

# One-off owner decision (2026-11-27): 1.0.8.2 ships with these cases deferred as well. They apply
# only while build.yaml says 1.0.8.2 and the deferral record names that version; any other release
# is back to DEFERRABLE_CASES alone. Remove this entry once 1.0.8.2 is published.
RELEASE_DEFERRABLE_CASES = {
    "1.0.8.2": frozenset((
        "issue-80", "issue-81-83", "issue-86", "issue-88", "issue-43", "reddit-disappearing",
        "media-live-workflows", "media-fate", "media-matroska", "media-hevc", "media-epub",
    )),
}


def release_version(root):
    try:
        match = re.search(r'^version:\s*"?([0-9.]+)"?\s*$', (root / "build.yaml").read_text(encoding="utf-8"), re.MULTILINE)
    except OSError:
        return None
    return match.group(1) if match else None


def deferrable_cases(root, deferral):
    version = release_version(root)
    if version is not None and deferral.get("version") == version:
        return DEFERRABLE_CASES | RELEASE_DEFERRABLE_CASES.get(version, frozenset())
    return DEFERRABLE_CASES


def check_acceptance(root, evidence_path, digest, requirements, deferred=None):
    try:
        raw_index = evidence_path.read_bytes()
        relative_index = evidence_path.relative_to(root).as_posix()
        try:
            committed_index = git(root, "show", f"HEAD:{relative_index}")
        except subprocess.CalledProcessError as error:
            raise ValueError("acceptance index is not committed in HEAD") from error
        if raw_index.replace(b"\r\n", b"\n") != committed_index.replace(b"\r\n", b"\n"):
            raise ValueError("acceptance index differs from committed HEAD")
        evidence = json.loads(raw_index.decode("utf-8-sig"))
        if evidence.get("schemaVersion") != 1 or not isinstance(evidence.get("cases"), dict):
            raise ValueError("expected schemaVersion=1 and a cases object")
        deferral = evidence.get("releaseDeferral", {})
        deferred_ids = set()
        if deferral.get("sourceDigest") == digest:
            ids = deferral.get("cases")
            allowed = deferrable_cases(root, deferral)
            if (not isinstance(ids, list) or not ids or
                    any(case_id not in allowed for case_id in ids)):
                raise ValueError("release deferral includes unsupported cases")
            deferred_ids = set(ids)
            if deferred is not None:
                deferred.extend(sorted(deferred_ids))
    except (OSError, ValueError, AttributeError) as error:
        return [f"Acceptance evidence missing/invalid: {error}"] + [f"{case['id']}: UNVERIFIED - {case['acceptance']}" for case in requirements]
    problems = []
    for case in requirements:
        if case["id"] in deferred_ids:
            continue
        try:
            record = evidence["cases"][case["id"]]
            artifact_name = record["artifact"]
            if not isinstance(artifact_name, str) or not artifact_name.startswith("docs/testing/reported-issues-evidence/"):
                raise ValueError("artifact must be committed under docs/testing/reported-issues-evidence/")
            artifact = root / artifact_name
            if not (root / "docs/testing/reported-issues-evidence").resolve().is_relative_to(root.resolve()):
                raise ValueError("evidence directory escapes repository")
            if artifact.resolve().is_relative_to((root / "docs/testing/reported-issues-evidence").resolve()) is False:
                raise ValueError("artifact escapes evidence directory")
            raw = artifact.read_bytes()
            try:
                committed_artifact = git(root, "show", f"HEAD:{artifact_name}")
            except subprocess.CalledProcessError as error:
                raise ValueError("acceptance artifact is not committed in HEAD") from error
            if raw != committed_artifact:
                raise ValueError("acceptance artifact differs from committed HEAD; save artifacts as UTF-8/LF")
            if hashlib.sha256(raw).hexdigest() != record["sha256"]:
                raise ValueError("artifact hash does not match")
            run = json.loads(raw.decode("utf-8-sig"))
            if run["caseId"] != case["id"] or run["sourceDigest"] != digest:
                raise ValueError("evidence belongs to different code or a different case")
            if not isinstance(run["environment"], str) or not run["environment"].strip():
                raise ValueError("test environment is missing")
            checks = run["checks"]
            if not isinstance(checks, list) or not checks:
                raise ValueError("zero acceptance checks")
            if any(not isinstance(c, dict) or not isinstance(c.get("name"), str) or not c["name"].strip()
                   or c.get("passed") is not True or c.get("skipped", False) is not False for c in checks):
                raise ValueError("acceptance has failed, skipped or malformed checks")
            missing_checks = set(case.get("requiredChecks", ())) - {check["name"] for check in checks}
            if missing_checks:
                raise ValueError(f"missing required checks: {', '.join(sorted(missing_checks))}")
        except (KeyError, OSError, ValueError, TypeError, AttributeError) as error:
            problems.append(f"{case['id']}: UNVERIFIED - {error}; {case['acceptance']}")
    return problems


def require_committed_source(root):
    for entry in git(root, "ls-files", "-v", "-z").decode("utf-8").split("\0"):
        if entry and (entry[0].islower() or entry[0] == "S"):
            raise ValueError(f"Tracked source has assume-unchanged/skip-worktree flags: {entry[2:]}")
    if git(root, "status", "--porcelain", "--untracked-files=all").strip():
        raise ValueError("Push refused: commit the intended source/tests first; dirty working-tree tests cannot certify outgoing commits")


def check_push_source(root, records):
    head = git(root, "rev-parse", "HEAD").decode().strip()
    lines = records.splitlines()
    if not lines:
        raise ValueError("Pre-push received no outgoing refs")
    for line in lines:
        parts = line.split()
        if len(parts) != 4:
            raise ValueError("Malformed pre-push ref record")
        local_ref, local_sha, _, _ = parts
        if not re.fullmatch(r"(?:[a-fA-F0-9]{40}|[a-fA-F0-9]{64})", local_sha):
            raise ValueError("Malformed pre-push commit hash")
        if set(local_sha) == {"0"}:
            continue
        revision = git(root, "rev-parse", f"{local_sha}^{{commit}}").decode().strip()
        if revision != head:
            raise ValueError(f"Outgoing {local_ref} differs from tested HEAD")
    require_committed_source(root)
    return head


def run(command, root, log):
    try:
        result = subprocess.run(command, cwd=root, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace")
        log.write_text(result.stdout, encoding="utf-8")
        return result.returncode, result.stdout
    except OSError as error:
        log.write_text(str(error), encoding="utf-8")
        return 1, str(error)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pre-push", action="store_true")
    parser.add_argument("--require-clean", action="store_true")
    parser.add_argument("--expected-revision")
    parser.add_argument("--fingerprint", action="store_true")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    try:
        push_head = None
        if args.pre_push:
            push_head = check_push_source(root, sys.stdin.read())
        if args.require_clean:
            require_committed_source(root)
        if args.expected_revision and git(root, "rev-parse", "HEAD").decode().strip() != args.expected_revision:
            raise ValueError("HEAD differs from the intended release revision")
        digest = source_digest(root)
        if args.fingerprint:
            print(digest)
            return 0
        requirements, media_requirements = load_requirements(root)
        output = args.output or root / "artifacts/reported-issues" / uuid.uuid4().hex
        output = output.resolve()
        output.mkdir(parents=True, exist_ok=False)
        problems = []
        code, _ = run([sys.executable, "-B", "-m", "unittest", "discover", "-s", "tools/repair-test", "-p", "test_reported_issue_gate.py"],
                      root, output / "gate-self-tests.log")
        if code:
            problems.append(f"Gate self-tests exited {code}")
        code, _ = run(["dotnet", "test", "Jellyfin.Plugin.MediaDash.sln", "--configuration", "Release",
                       "--filter", "Category!=ReportedIssueExternal", "--logger", "trx;LogFileName=reported-issues.trx",
                       "--results-directory", str(output), "--verbosity", "quiet"], root, output / "dotnet.log")
        if code:
            problems.append(f".NET build/tests exited {code}; see {output / 'dotnet.log'}")
        problems.extend(check_trx(output / "reported-issues.trx", requirements))
        code, tap = run(["node", "--test", "--test-reporter=tap", "tools/comprehensive-test/ui-lifecycle-self-tests.js",
                         "tools/comprehensive-test/ui-regression-self-tests.js", "tools/comprehensive-test/ui-issue-fixes-self-tests.js"], root, output / "ui.log")
        if code:
            problems.append(f"UI runner exited {code}")
        problems.extend(check_tap(tap))
        deferred = []
        problems.extend(check_acceptance(root, root / "docs/testing/reported-issues-evidence.json", digest,
                                         requirements + media_requirements, deferred))
        if source_digest(root) != digest:
            problems.append("Source changed during the gate; results cannot certify it")
        if args.pre_push:
            # Recheck cleanliness and HEAD after builds/tests, before Git sends any objects.
            check_push_source(root, f"HEAD {push_head} refs/heads/check {'0' * 40}")
        if args.expected_revision and git(root, "rev-parse", "HEAD").decode().strip() != args.expected_revision:
            problems.append("HEAD changed during the release gate")
        if args.require_clean:
            require_committed_source(root)
        (output / "summary.json").write_text(json.dumps({"sourceDigest": digest, "passed": not problems,
            "externalTests": "Required acceptance evidence; not executed by the offline suite",
            "deferredAcceptance": deferred, "problems": problems}, indent=2), encoding="utf-8")
        for case_id in deferred:
            print(f"DEFERRED: {case_id} acceptance for this source fingerprint")
        for problem in problems:
            print(f"BLOCKED: {problem}")
        print(f"{'DO NOT PUSH' if problems else 'PASS'} - evidence: {output}")
        return 1 if problems else 0
    except (OSError, ValueError, KeyError, subprocess.CalledProcessError) as error:
        print(f"DO NOT PUSH: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
