"""Owned-fixture Playability E2E. Standard library only; see README and COVERAGE.md."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import random
import sqlite3
import subprocess
import sys
import time
import traceback
import urllib.error
import urllib.parse
import urllib.request

RUNGS = {'remux': 'RepairAttemptRemux', 'drop': 'RepairAttemptDropStreams',
         'coerce': 'RepairAttemptContainerCoerce', 'audio': 'RepairAttemptAudioReencode',
         'video': 'RepairAttemptReencode'}
LABELS = {'remux': 'quick remux', 'drop': 'dropped broken streams',
          'coerce': 'container changed', 'audio': 'audio re-encoded', 'video': 'video re-encoded'}
CASES = [
    ('healthy', 'movie.mp4', 'copy', 'healthy', None),
    ('unicode', '健全 café [2026] & $demo.mp4', 'copy', 'healthy', None),
    ('duplicate-basename', 'movie.mp4', 'tail', 'repair', 'remux'),
    ('partial', 'partial.mp4', 'half', 'review-delete', 'remux'),
    ('bitrot', 'bitrot.mp4', 'video', 'repair', 'video'),
    ('drop-audio', 'two-audio.mp4', 'audio2first', 'repair', 'drop'),
    ('second-audio', 'second-audio.mp4', 'audio2', 'repair', 'drop'),
    ('audio-only', 'single-audio.mp4', 'audio1', 'repair', 'audio'),
    ('coerce', 'coerce.mp4', 'tail', 'repair', 'coerce'),
    ('zero', 'zero.mp4', 'zero', 'review-delete', None),
    ('junk', 'junk.mp4', 'junk', 'refuse', None),
    ('header', 'header.mp4', 'header', 'review-delete', None),
    ('disabled', 'disabled.mp4', 'tail', 'refuse', 'off'),
    ('rename', 'odd [01] & $name.mkv', 'copy', 'rename', None),
    ('collision', 'collision.mkv', 'copy', 'collision', None),
]


def validate_url(url):
    parsed = urllib.parse.urlsplit(url)
    if (parsed.scheme not in ('http', 'https') or
            parsed.hostname not in ('localhost', '127.0.0.1', '::1') or
            parsed.username or parsed.password or parsed.query or parsed.fragment):
        raise ValueError('Only a loopback HTTP(S) URL without credentials/query/fragment is allowed')
    return url.rstrip('/')


def fresh_completion(before, task):
    result = task.get('LastExecutionResult') or {}
    if (task.get('State') != 'Idle' or not result.get('EndTimeUtc') or
            not result.get('StartTimeUtc') or result.get('StartTimeUtc') == before.get('StartTimeUtc')):
        return False
    if result.get('Status') != 'Completed':
        raise RuntimeError('New scheduled task did not complete successfully: ' + str(result))
    return True


def exit_code(results):
    if any(r['status'] == 'FAIL' for r in results):
        return 1
    if not results or any(r['status'] != 'PASS' for r in results):
        return 2
    return 0


def match_rows(rows, issue_id, path, after_id):
    return [r for r in rows if r['id'] > after_id and r['issue_id'] == issue_id and r['path'] == path]


def is_delete_review(issue):
    return (issue.get('Status') == 'Detected' and issue.get('RepairFailed') is True
            and any(w.get('blocking', w.get('Blocking')) for w in issue.get('Warnings', [])))


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def stream_counts(probe):
    return {kind: sum(s.get('codec_type') == kind for s in probe.get('streams', []))
            for kind in ('video', 'audio', 'subtitle')}


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise RuntimeError('Unexpected redirect; refusing to forward local credentials')


class Harness:
    def __init__(self, args):
        self.args = args
        self.url = validate_url(args.url)
        self.root = Path(args.output).resolve()
        self.root.mkdir(parents=True, exist_ok=False)
        self.media = self.root / 'media'
        self.media.mkdir()
        self.results = []
        self.headers = {}
        self.http = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
        self.ffmpeg = Path(args.ffmpeg).resolve(strict=True)
        self.ffprobe = self.ffmpeg.with_name('ffprobe' + self.ffmpeg.suffix)
        if not self.ffprobe.is_file():
            raise ValueError('ffprobe must be next to the supplied Jellyfin ffmpeg')
        self.original = None
        self.changed = False
        self.added = []
        self.held_queue = []
        self.owned_tasks = set()
        self.serial = 0
        self.fixtures = []

    def save(self, name, value):
        (self.root / name).write_text(json.dumps(value, indent=2, ensure_ascii=False), encoding='utf-8')

    def record(self, name, ok, detail='', blocked=False):
        status = 'BLOCKED' if blocked else ('PASS' if ok else 'FAIL')
        self.results.append(dict(test=name, status=status, detail=str(detail)))
        self.save('results.json', self.results)
        # External audit P2 #11: an ASCII stdout (Windows default when not under a UTF-8 terminal)
        # raises UnicodeEncodeError on arrows, non-Latin filenames, or non-ASCII diagnostics,
        # which crashes mid-run and interrupts subsequent cleanup. Fall back to the stream's
        # replacement codec so a diagnostic can always render even when the console is narrow.
        line = status + ' ' + name + (' :: ' + str(detail) if detail else '')
        encoding = getattr(sys.stdout, 'encoding', None) or 'ascii'
        try:
            print(line, flush=True)
        except UnicodeEncodeError:
            print(line.encode(encoding, errors='backslashreplace').decode(encoding, errors='replace'), flush=True)
        return ok

    def api(self, path, method='GET', body=None, raw=False, headers=None):
        hdr = dict(self.headers)
        hdr.update(headers or {})
        payload = None if body is None else json.dumps(body).encode('utf-8')
        if payload is not None:
            hdr['Content-Type'] = 'application/json'
        request = urllib.request.Request(self.url + path, data=payload, headers=hdr, method=method)
        with self.http.open(request, timeout=30) as response:
            data = response.read()
            return data if raw else (json.loads(data) if data else None)

    def rows(self, query='SELECT * FROM history WHERE id > ?', params=None):
        with sqlite3.connect(self.db.as_uri() + '?mode=ro', uri=True) as conn:
            conn.row_factory = sqlite3.Row
            return [dict(r) for r in conn.execute(query, params if params is not None else (self.history_start,))]

    def process(self, args, probe=False):
        self.serial += 1
        executable = self.ffprobe if probe else self.ffmpeg
        result = subprocess.run([str(executable), *map(str, args)], capture_output=True, timeout=120)
        (self.root / ('process-%03d.log' % self.serial)).write_bytes(result.stderr)
        return result

    def probe(self, path):
        r = self.process(['-v', 'error', '-show_streams', '-show_format', '-of', 'json', path], probe=True)
        return json.loads(r.stdout) if r.returncode == 0 else {}

    def decodes(self, path, stream=None):
        mapping = ['-map', stream] if stream else ['-map', '0:v?', '-map', '0:a?']
        return self.process(['-nostdin', '-v', 'error', '-xerror', '-i', path, *mapping, '-f', 'null', '-']).returncode == 0

    def video_hash(self, path):
        r = self.process(['-nostdin', '-v', 'error', '-i', path, '-map', '0:v:0', '-f', 'hash', '-hash', 'sha256', '-'])
        if r.returncode:
            raise RuntimeError('Cannot hash decoded video: ' + str(path))
        return r.stdout.decode().strip()

    def task(self, key):
        return next(t for t in self.api('/ScheduledTasks') if t['Key'] == key)

    def idle(self, key):
        deadline = time.monotonic() + self.args.timeout
        while time.monotonic() < deadline:
            if self.task(key)['State'] == 'Idle':
                return
            time.sleep(.5)
        raise TimeoutError(key + ' did not become idle')

    def run_task(self, key, route):
        before = self.task(key)
        if before['State'] != 'Idle':
            raise RuntimeError('Refusing to overlap existing task ' + key)
        self.owned_tasks.add(key)
        self.api(route, 'POST')
        deadline = time.monotonic() + self.args.timeout
        while time.monotonic() < deadline:
            task = self.task(key)
            if fresh_completion(before.get('LastExecutionResult') or {}, task):
                self.owned_tasks.discard(key)
                self.record(key + ' fresh completion', True)
                return
            time.sleep(.5)
        raise TimeoutError('No fresh completion for ' + key)

    def configure(self, config):
        # Mark BEFORE sending: a request timeout can happen after the server committed.
        self.changed = True
        self.api(self.config_path, 'POST', config)
        if self.api(self.config_path) != config:
            raise RuntimeError('Configuration read-back mismatch')
        # ConfigChangeRescanner debounces; observe a stable idle period before explicit scans.
        time.sleep(1.5)
        self.idle('MediaDashScan')

    def open_issues(self):
        return self.api('/MediaDash/Issues?openOnly=true')

    def issues(self):
        return [i for i in self.api('/MediaDash/Issues?type=Playability&openOnly=true')
                if Path(i['Path']).is_relative_to(self.media)]

    def setup(self):
        auth = self.api('/Users/AuthenticateByName', 'POST',
                        {'Username': self.args.username, 'Pw': os.environ.get(self.args.password_env, 'test')},
                        headers={'Authorization': 'MediaBrowser Client="RepairHarness", Device="local", DeviceId="repair-harness", Version="2"'})
        self.headers = {'Authorization': 'MediaBrowser Token="' + auth['AccessToken'] + '", Client="RepairHarness", Device="local", DeviceId="repair-harness", Version="2"'}
        info = self.api('/System/Info')
        actual_data = Path(info['ProgramDataPath']).resolve()
        if actual_data != Path(self.args.data_dir).resolve():
            raise RuntimeError('Active datadir does not match --data-dir: ' + str(actual_data))
        self.db = actual_data / 'data/mediadash/mediadash.db'
        self.history_start = self.rows('SELECT COALESCE(MAX(id),0) AS n FROM history', ())[0]['n']
        plugin = next(p for p in self.api('/Plugins') if p['Name'] == 'MediaDash')
        if plugin['Status'] != 'Active':
            raise RuntimeError('MediaDash is not active')
        self.save('server.json', {'Version': info['Version'], 'ProgramDataPath': str(actual_data), 'Plugin': plugin})
        self.record('active server identity', True, str(actual_data))
        for key in ('MediaDashFix', 'MediaDashScan', 'RefreshLibrary'):
            if self.task(key)['State'] != 'Idle':
                raise RuntimeError('Existing task is active: ' + key)
        queued = [i for i in self.open_issues() if i.get('Status') == 'Queued']
        if queued and not self.args.hold_existing_queue:
            raise RuntimeError('Existing queued work; refusing global Fix task. Use an empty test server or --hold-existing-queue.')
        if any(i.get('Type') == 'Stale' for i in queued):
            raise RuntimeError('Cannot hold informational Stale approvals: the approval API cannot restore them. Use an empty test server.')
        for library in self.api('/Library/VirtualFolders'):
            for location in library.get('Locations', []):
                existing = Path(location).resolve()
                if self.root.is_relative_to(existing) or existing.is_relative_to(self.root):
                    raise RuntimeError('Output overlaps existing library: ' + str(existing))
        self.config_path = '/Plugins/' + plugin['Id'] + '/Configuration'
        self.original = self.api(self.config_path)
        self.save('config-before.json', self.original)
        self.save('status-before.json', self.api('/MediaDash/Status'))
        self.safe = dict(self.original)
        for k in self.safe:
            if k.endswith('FixMode'):
                self.safe[k] = 'Off'
        self.safe.update(DryRun=True, AnalyticsEnabled=False, EnabledLibraries=['00000000000000000000000000000000'])
        self.configure(self.safe)
        self.held_queue = queued
        self.save('queue-before.json', queued)
        for issue in queued:
            # Dismissed rows survive rescans/config invalidation; Detected rows do not.
            self.api('/MediaDash/Issues/' + str(issue['Id']) + '/Dismiss', 'POST')

    def build_fixtures(self):
        base = self.root / 'baseline.mp4'
        r = self.process(['-nostdin', '-v', 'error', '-f', 'lavfi', '-i', 'testsrc2=size=320x180:rate=24:duration=12',
                          '-f', 'lavfi', '-i', 'sine=frequency=440:duration=12', '-c:v', 'libx264', '-preset', 'ultrafast',
                          '-c:a', 'aac', '-movflags', '+faststart', '-shortest', base])
        if r.returncode or not self.decodes(base):
            raise RuntimeError('Generated baseline does not decode')
        data = base.read_bytes()
        for case, filename, damage, outcome, rung in CASES:
            folder = self.media / case
            folder.mkdir()
            path = folder / filename
            path.write_bytes(data)
            if damage in ('audio2', 'audio2first'):
                path.unlink()  # Only the just-created owned fixture; never user media.
                r = self.process(['-nostdin', '-v', 'error', '-i', base, '-map', '0:v', '-map', '0:a', '-map', '0:a', '-c', 'copy', '-movflags', '+faststart', path])
                if r.returncode:
                    raise RuntimeError('Two-audio fixture generation failed')
            if damage in ('tail', 'half'):
                original = path.read_bytes()
                path.write_bytes(original[:int(len(original) * (.55 if damage == 'half' else .94))])
            elif damage in ('audio1', 'audio2', 'audio2first', 'video'):
                stream = 'a:1' if damage == 'audio2' else ('a:0' if damage in ('audio1', 'audio2first') else 'v:0')
                packets = self.process(['-v', 'error', '-select_streams', stream, '-show_packets', '-show_entries', 'packet=pos,size', '-of', 'json', path], probe=True)
                if packets.returncode:
                    raise RuntimeError('Cannot locate fixture packets')
                packets = json.loads(packets.stdout)['packets']
                buf = bytearray(path.read_bytes())
                rng = random.Random(22)
                for packet in packets[len(packets)//3:len(packets)//3 + 8]:
                    start, size = int(packet['pos']), int(packet['size'])
                    for n in range(start + min(12, size), start + size):
                        buf[n] ^= rng.randint(1, 255)
                path.write_bytes(buf)
            elif damage == 'zero':
                path.write_bytes(b'')
            elif damage == 'junk':
                path.write_bytes(b'not a media file\x00' * 256)
            elif damage == 'header':
                path.write_bytes(data[:256])
            sentinel = None
            sidecar = path.with_suffix('.en.srt')
            sidecar.write_text('1\n00:00:00,000 --> 00:00:01,000\nOwned fixture\n', encoding='utf-8')
            fixture = dict(case=case, path=str(path), size=path.stat().st_size, sha256=sha(path), outcome=outcome,
                           rung=rung, sidecar=str(sidecar), sidecar_hash=sha(sidecar), sentinel=str(sentinel) if sentinel else None,
                           sentinel_hash=sha(sentinel) if sentinel else None, counts=stream_counts(self.probe(path)), ready=True)
            if damage in ('audio1', 'audio2', 'audio2first'):
                broken = '0:a:1' if damage == 'audio2' else '0:a:0'
                ready = self.decodes(path, '0:v:0') and not self.decodes(path, broken)
                if damage == 'audio2':
                    ready = ready and self.decodes(path, '0:a:0')
                if damage == 'audio2first':
                    ready = ready and self.decodes(path, '0:a:1')
                fixture['ready'] = self.record(case + ' isolated audio damage', ready, blocked=not ready)
                fixture['video_hash'] = self.video_hash(path)
            elif outcome in ('repair', 'partial', 'recycle', 'refuse', 'review-delete'):
                fixture['ready'] = self.record(case + ' is actually broken', not self.decodes(path))
            else:
                fixture['ready'] = self.record(case + ' clean payload', self.decodes(path))
            self.fixtures.append(fixture)
        self.save('fixtures.json', self.fixtures)
        outside = self.root / 'excluded'
        outside.mkdir()
        self.excluded = outside / 'excluded.mp4'
        self.excluded.write_bytes(data[:int(len(data)*.8)])
        self.excluded_hash = sha(self.excluded)

    def add_library(self, name, path):
        # Track before POST so cleanup also handles ambiguous request timeouts.
        self.added.append(name)
        query = urllib.parse.urlencode(dict(name=name, collectionType='movies', paths=str(path), refreshLibrary='false'))
        self.api('/Library/VirtualFolders?' + query, 'POST', {'LibraryOptions': {'EnableRealtimeMonitor': False, 'EnableInternetProviders': False, 'EnableChapterImageExtraction': False}})

    def indexed(self):
        query = urllib.parse.urlencode(dict(Recursive='true', ParentId=self.lib_id, Fields='Path', IncludeItemTypes='Movie'))
        return self.api('/Items?' + query)['Items']

    def scan(self):
        self.idle('MediaDashScan')
        self.run_task('MediaDashScan', '/MediaDash/Scan')

    def execute(self):
        self.setup()
        self.build_fixtures()
        name = 'Repair harness ' + self.root.name
        self.add_library(name, self.media)
        self.add_library(name + ' excluded', self.excluded.parent)
        self.run_task('RefreshLibrary', '/Library/Refresh')
        self.lib_id = next(l['ItemId'] for l in self.api('/Library/VirtualFolders') if l['Name'] == name)
        cfg = dict(self.safe)
        cfg.update(EnabledLibraries=[self.lib_id], PlayabilityFixMode='ManualApprove', PlayabilityDisposal='RecycleBin',
                   ThoroughPlayabilityCheck=True, FixWindowStart='', FixWindowEnd='', PauseDuringPlayback=False)
        cfg.update({prop: True for prop in RUNGS.values()})
        self.configure(cfg)
        indexed = {i['Path'] for i in self.indexed()}
        self.scan()
        issues = self.issues()
        self.save('issues-before.json', issues)
        for f in self.fixtures:
            if not self.record(f['case'] + ' indexed', f['path'] in indexed):
                f['ready'] = False
            found = [i for i in issues if i['Path'] == f['path']]
            expected = f['outcome'] != 'healthy'
            if not self.record(f['case'] + ' detection', bool(found) == expected):
                f['ready'] = False
        self.record('excluded library not scanned', not any(i['Path'] == str(self.excluded) for i in self.open_issues()))
        # Dry-run exercises real history but must leave every file and both summary counters untouched.
        approved = []
        for i in issues:
            if any(f['ready'] and f['path'] == i['Path'] for f in self.fixtures):
                self.api('/MediaDash/Issues/' + str(i['Id']) + '/Approve', 'POST')
                approved.append(i)
        if {i['Id'] for i in self.open_issues() if i['Status'] == 'Queued'} != {i['Id'] for i in approved}:
            raise RuntimeError('Unexpected queued work before dry run')
        before = self.api('/MediaDash/Status')['Repair']
        self.run_task('MediaDashFix', '/MediaDash/Fix')
        for f in self.fixtures:
            self.record(f['case'] + ' dry-run hash', sha(f['path']) == f['sha256'])
        self.record('dry-run summary unchanged', self.api('/MediaDash/Status')['Repair'] == before)
        dryrows = self.rows()
        self.record('dry-run history only', bool(approved) and len(dryrows) == len(approved)
                    and all(len(match_rows(dryrows, i['Id'], i['Path'], self.history_start)) == 1 for i in approved)
                    and all((r['dry_run'] or not r['success']) and not r['recycle_path']
                            and r['bytes_freed'] == 0 and r['saved_bytes'] == 0 for r in dryrows))
        self.save('history-dry.json', dryrows)
        # A dry-run may leave queued rows; clear only our own approvals before per-case toggles.
        for issue in self.issues():
            if issue['Status'] == 'Queued':
                self.api('/MediaDash/Issues/' + str(issue['Id']) + '/Revert', 'POST')
        self.restore_rows = []
        self.exercise_cases(cfg)
        self.run_task('RefreshLibrary', '/Library/Refresh')
        self.scan()
        allowed = {f['path'] for f in self.fixtures if f['outcome'] in ('collision', 'refuse') or not f['ready']}
        remaining = self.issues()
        self.save('issues-rescan.json', remaining)
        self.record('rescan contains only deliberate conflicts', all(i['Path'] in allowed for i in remaining), [i['Path'] for i in remaining])
        for f in self.fixtures:
            if f['outcome'] == 'healthy':
                self.record(f['case'] + ' remains byte-identical', sha(f['path']) == f['sha256'])
            self.record(f['case'] + ' sidecar preserved', sha(f['sidecar']) == f['sidecar_hash'])
            if f['sentinel']:
                self.record('collision target not overwritten', sha(f['sentinel']) == f['sentinel_hash'])
        self.record('excluded library source preserved', sha(self.excluded) == self.excluded_hash)
        allrows = self.rows()
        self.record('no foreign history writes', all(Path(r['path']).is_relative_to(self.media) for r in allrows))
        self.save('history-all.json', allrows)
        self.save('status-after.json', self.api('/MediaDash/Status'))
        for row, f in self.restore_rows:
            try:
                self.api('/MediaDash/History/' + str(row['id']) + '/Restore?force=true', 'POST')
                self.record(f['case'] + ' restore exact hash', sha(f['path']) == f['sha256'])
            except Exception:
                self.record(f['case'] + ' restore', False, traceback.format_exc())
        leftovers = [str(p) for p in self.media.rglob('*') if p.is_file() and ('mediadash.tmp' in p.name.lower() or 'mediadash-recover-' in p.name.lower())]
        self.record('no abandoned repair/recovery files', not leftovers, leftovers)

    def exercise_cases(self, cfg):
        for f in self.fixtures:
            if not f['ready'] or f['outcome'] == 'healthy':
                continue
            try:
                self.exercise(f, cfg)
            except Exception:
                self.record(f['case'] + ' execution', False, traceback.format_exc())
            finally:
                # Recorded failures do not throw. Refusals can legitimately
                # retain a queued row, so release owned approvals on every path.
                self.idle('MediaDashFix')
                for pending in self.issues():
                    if pending['Status'] == 'Queued':
                        self.api('/MediaDash/Issues/' + str(pending['Id']) + '/Revert', 'POST')

    def review_then_delete(self, f, row):
        source = Path(f['path'])
        self.record(f['case'] + ' failed repair preserves original', not row['success'] and not row['recycle_path']
                    and not row['bytes_freed'] and not row['saved_bytes'] and source.is_file() and sha(source) == f['sha256'], row['action'])
        self.scan()
        reviews = [i for i in self.issues() if i['Path'] == f['path']]
        if not self.record(f['case'] + ' rescan requires fresh Delete approval', len(reviews) == 1
                           and is_delete_review(reviews[0]), reviews):
            return
        review = reviews[0]
        if any(i['Status'] == 'Queued' for i in self.open_issues()):
            raise RuntimeError('Unexpected approval before Delete-consent negative control')
        before = self.api('/MediaDash/Status')['Repair']
        self.run_task('MediaDashFix', '/MediaDash/Fix')
        self.record(f['case'] + ' unapproved Delete leaves original', source.is_file() and sha(source) == f['sha256'])
        self.api('/MediaDash/Issues/' + str(review['Id']) + '/Approve', 'POST')
        if {i['Id'] for i in self.open_issues() if i['Status'] == 'Queued'} != {review['Id']}:
            raise RuntimeError('Unexpected queued work before freshly approved Delete')
        start = self.rows('SELECT COALESCE(MAX(id),0) AS n FROM history', ())[0]['n']
        self.run_task('MediaDashFix', '/MediaDash/Fix')
        rows = match_rows(self.rows(), review['Id'], f['path'], start)
        if not self.record(f['case'] + ' fresh Delete has attributable history', len(rows) == 1, rows):
            return
        deleted = rows[0]
        recycled = Path(deleted['recycle_path']) if deleted['recycle_path'] else None
        ok = bool(deleted['success'] and not source.exists() and recycled and recycled.is_file() and sha(recycled) == f['sha256'])
        self.record(f['case'] + ' fresh Delete recycles exact original', ok, deleted['action'])
        self.record(f['case'] + ' Delete does not claim repair savings', not deleted['saved_bytes']
                    and self.api('/MediaDash/Status')['Repair'] == before)
        if recycled and recycled.is_file():
            self.restore_rows.append((deleted, f))

    def exercise(self, f, cfg):
        cfg = dict(cfg, DryRun=False)
        cfg.update({prop: f['rung'] is None or key == f['rung'] for key, prop in RUNGS.items()})
        self.configure(cfg)
        self.scan()
        issues = [i for i in self.issues() if i['Path'] == f['path']]
        if len(issues) != 1:
            raise RuntimeError('Expected exactly one current issue, got ' + str(len(issues)))
        if f['outcome'] == 'collision':
            # Create only after indexing: Jellyfin otherwise groups this as an alternate version.
            sentinel = Path(f['path']).with_suffix('.mp4')
            with sentinel.open('xb') as stream:
                stream.write((self.root / 'baseline.mp4').read_bytes())
            f.update(sentinel=str(sentinel), sentinel_hash=sha(sentinel))
            self.save('fixtures.json', self.fixtures)
        issue = issues[0]
        if issue['Status'] == 'Detected':
            self.api('/MediaDash/Issues/' + str(issue['Id']) + '/Approve', 'POST')
        elif issue['Status'] != 'Queued':
            raise RuntimeError('Unexpected issue state ' + issue['Status'])
        queued = [i for i in self.open_issues() if i.get('Status') == 'Queued']
        if {i['Id'] for i in queued} != {issue['Id']}:
            raise RuntimeError('Unexpected queued work before single-case fix')
        start = self.rows('SELECT COALESCE(MAX(id),0) AS n FROM history', ())[0]['n']
        before = self.api('/MediaDash/Status')['Repair']
        self.run_task('MediaDashFix', '/MediaDash/Fix')
        rows = match_rows(self.rows(), issue['Id'], f['path'], start)
        if not self.record(f['case'] + ' one attributable history row', len(rows) == 1, len(rows)):
            return
        row = rows[0]
        after = self.api('/MediaDash/Status')['Repair']
        repair = row['success'] and row['action'].startswith(('repaired ', 'partially repaired '))
        self.record(f['case'] + ' summary count delta', after['CountLifetime'] - before['CountLifetime'] == int(repair))
        self.record(f['case'] + ' summary bytes delta', after['SavedBytesLifetime'] - before['SavedBytesLifetime'] == (f['size'] if repair else 0))
        self.record(f['case'] + ' recent summary count delta', after['Count30d'] - before['Count30d'] == int(repair))
        self.record(f['case'] + ' recent summary bytes delta', after['SavedBytes30d'] - before['SavedBytes30d'] == (f['size'] if repair else 0))
        if f['outcome'] == 'review-delete':
            self.review_then_delete(f, row)
            return
        source = Path(f['path'])
        if row['recycle_path']:
            binpath = Path(row['recycle_path'])
            self.record(f['case'] + ' exact recycled original', binpath.is_file() and sha(binpath) == f['sha256'])
            self.restore_rows.append((row, f))
        if f['outcome'] in ('collision', 'refuse'):
            self.record(f['case'] + ' refuses without mutation', source.is_file() and sha(source) == f['sha256']
                        and not row['recycle_path'] and not row['success'] and not row['bytes_freed'] and not row['saved_bytes'], row['action'])
            return
        self.record(f['case'] + ' successful action', bool(row['success']), row['action'])
        if f['outcome'] == 'recycle':
            self.record(f['case'] + ' safely recycled', not source.exists() and bool(row['recycle_path']) and not repair)
            return
        if f['outcome'] == 'rename':
            output = source.with_suffix('.mp4')
            self.record('rename preserves content', not source.exists() and output.is_file() and sha(output) == f['sha256'] and not row['recycle_path'])
        else:
            output = source.with_suffix('.mkv') if f['rung'] in ('coerce', 'video') else source
            self.record(f['case'] + ' intended rung used', repair and LABELS[f['rung']] in row['action'], row['action'])
            self.record(f['case'] + ' accounting separated', row['saved_bytes'] == f['size'] and row['bytes_freed'] == 0)
            self.record(f['case'] + ' must keep original in bin', bool(row['recycle_path']))
            if f['outcome'] == 'partial':
                self.record('large truncation disclosed', row['action'].startswith('partially repaired '), row['action'])
        if not self.record(f['case'] + ' expected output exists', output.is_file(), str(output)):
            return
        self.record(f['case'] + ' full video/audio decode', self.decodes(output))
        counts = stream_counts(self.probe(output))
        expected_audio = f['counts']['audio'] - (1 if f['rung'] == 'drop' else 0)
        self.record(f['case'] + ' expected stream inventory', counts['video'] == f['counts']['video'] and counts['audio'] == expected_audio and counts['video'] > 0 and counts['audio'] > 0, counts)
        if f.get('video_hash'):
            self.record(f['case'] + ' copied video unchanged', self.video_hash(output) == f['video_hash'])
        self.run_task('RefreshLibrary', '/Library/Refresh')
        item = next((i for i in self.indexed() if i.get('Path') == str(output)), None)
        if self.record(f['case'] + ' playable item indexed', item is not None):
            # Full HTTP body hash, not just the first 1024 bytes. Generated fixtures are small.
            data = self.api('/Videos/' + item['Id'] + '/stream?static=true', raw=True)
            self.record(f['case'] + ' complete Jellyfin stream hash', hashlib.sha256(data).hexdigest() == sha(output))

    def cleanup(self):
        # Keep media/evidence. Never empty a shared bin or delete shared database rows.
        idle = True
        for key, route in [('MediaDashFix', '/MediaDash/Fix/Cancel'), ('MediaDashScan', '/MediaDash/Scan/Cancel'), ('RefreshLibrary', None)]:
            try:
                if key in self.owned_tasks and self.task(key)['State'] != 'Idle':
                    if route:
                        self.api(route, 'POST')
                    self.idle(key)
            except Exception as error:
                idle = False
                self.record('cleanup wait ' + key, False, error)
        if not idle:
            self.record('cleanup requires manual recovery', False, 'Retained isolated config; use config-before.json after tasks stop')
            return
        for name in reversed(self.added):
            try:
                if any(l['Name'] == name for l in self.api('/Library/VirtualFolders')):
                    self.api('/Library/VirtualFolders?' + urllib.parse.urlencode(dict(name=name, refreshLibrary='false')), 'DELETE')
                self.record('temporary library removed', not any(l['Name'] == name for l in self.api('/Library/VirtualFolders')), name)
            except Exception as error:
                self.record('library cleanup', False, error)
        if self.changed:
            try:
                self.run_task('RefreshLibrary', '/Library/Refresh')
                for issue in self.open_issues():
                    if Path(issue['Path']).is_relative_to(self.root):
                        self.api('/MediaDash/Issues/' + str(issue['Id']) + '/Dismiss', 'POST')
                self.record('owned open issues dismissed', not any(Path(i['Path']).is_relative_to(self.root) for i in self.open_issues()))
            except Exception as error:
                self.record('issue cleanup', False, error)
            try:
                if self.held_queue:
                    # Approval now enforces modes. Requeue under read-only, excluded-library isolation before restoring the user's settings.
                    restore_config = dict(self.safe, DryRun=True, EnabledLibraries=['00000000000000000000000000000000'], FixerDisabled=[])
                    restore_config.update({k: 'ManualApprove' for k in restore_config if k.endswith('FixMode')})
                    self.configure(restore_config)
                # Older builds default the unfiltered endpoint to Detected only.
                # Query ignored rows explicitly so held approvals remain discoverable.
                current = {i['Id']: i for i in self.open_issues() + self.api('/MediaDash/Issues?status=Dismissed')} if self.held_queue else {}
                for issue in self.held_queue:
                    now = current.get(issue['Id'])
                    if now is None or now['Path'] != issue['Path']:
                        raise RuntimeError('Held queue issue changed; inspect queue-before.json')
                    if now['Status'] == 'Dismissed':
                        self.api('/MediaDash/Issues/' + str(issue['Id']) + '/Revert', 'POST')
                    if now['Status'] in ('Detected', 'Dismissed'):
                        self.api('/MediaDash/Issues/' + str(issue['Id']) + '/Approve', 'POST')
                restored = {i['Id'] for i in self.open_issues() if i['Status'] == 'Queued'}
                self.record('original queue restored', {i['Id'] for i in self.held_queue} == restored)
            except Exception as error:
                self.record('queue restoration', False, error)
            try:
                self.api(self.config_path, 'POST', self.original)
                self.record('original settings restored', self.api(self.config_path) == self.original)
            except Exception as error:
                self.record('config restoration', False, error)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--url', default='http://localhost:8099')
    parser.add_argument('--username', default='test')
    parser.add_argument('--password-env', default='MEDIADASH_TEST_PASSWORD')
    parser.add_argument('--ffmpeg')
    parser.add_argument('--data-dir')
    parser.add_argument('--output')
    parser.add_argument('--timeout', type=float, default=300)
    parser.add_argument('--confirm-live', action='store_true')
    parser.add_argument('--hold-existing-queue', action='store_true', help='Temporarily dismiss existing approvals and restore them in cleanup; snapshot saved to queue-before.json')
    parser.add_argument('--list-cases', action='store_true')
    args = parser.parse_args(argv)
    if args.list_cases:
        for case, filename, damage, outcome, rung in CASES:
            print(case, outcome, rung or 'default ladder')
        return 0
    if not args.confirm_live or not all((args.ffmpeg, args.data_dir, args.output)) or args.timeout <= 0:
        parser.error('--confirm-live, --ffmpeg, --data-dir, --output and a positive timeout are required')
    harness = Harness(args)
    try:
        harness.execute()
    except (Exception, KeyboardInterrupt):
        harness.record('harness execution', False, traceback.format_exc())
    finally:
        if harness.original is not None:
            harness.cleanup()
    code = exit_code(harness.results)
    harness.save('summary.json', dict(exitCode=code, passed=sum(r['status'] == 'PASS' for r in harness.results),
                                     failed=sum(r['status'] == 'FAIL' for r in harness.results),
                                     blocked=sum(r['status'] == 'BLOCKED' for r in harness.results)))
    print('Evidence:', harness.root, 'exit:', code, flush=True)
    return code


if __name__ == '__main__':
    sys.exit(main())

