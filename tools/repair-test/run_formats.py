"""Common-format live acceptance matrix; reuses run_live's isolated host/queue/cleanup checks."""
import argparse
import hashlib
import json
import shutil
import subprocess
import sys
import time
import traceback
import urllib.error
import urllib.parse
import zipfile
from pathlib import Path

from run_live import Harness, exit_code, sha, stream_counts

VIDEO = [('mp4', 'libx264', 'aac'), ('mkv', 'libx264', 'aac'),
         ('mov', 'libx264', 'aac'), ('m4v', 'libx264', 'aac'),
         ('avi', 'mpeg4', 'mp3'), ('webm', 'libvpx-vp9', 'libopus'),
         ('ts', 'mpeg2video', 'mp2'), ('mpg', 'mpeg2video', 'mp2'),
         ('wmv', 'wmv2', 'wmav2')]
AUDIO = [('mp3', 'libmp3lame'), ('flac', 'flac'), ('wav', 'pcm_s16le'),
         ('ogg', 'libvorbis'), ('opus', 'libopus'), ('m4a', 'aac'),
         ('aac', 'aac'), ('wma', 'wmav2'), ('m4b', 'aac')]
REPORT_ONLY_DOCUMENTS = frozenset(('broken-pdf', 'broken-epub', 'broken-cbz'))
MODES = dict(Playability='PlayabilityFixMode', AudioLanguage='AudioFixMode',
             SubtitleLanguage='SubtitleFixMode', CorruptNfo='NfoFixMode',
             MalwareRisk='SuspiciousFileFixMode', OrphanedDebris='OrphanCleanupFixMode',
             FormatCompatibility='FormatCompatibilityFixMode', Duplicate='DuplicateFixMode',
             Quality='TranscodeFixMode', EmbeddedCoverArt='EmbeddedCoverFixMode')


def pdf_bytes():
    objects = [b'<< /Type /Catalog /Pages 2 0 R >>',
               b'<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
               b'<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>',
               b'<< /Length 0 >>\nstream\n\nendstream']
    data = bytearray(b'%PDF-1.4\n')
    offsets = [0]
    for n, body in enumerate(objects, 1):
        offsets.append(len(data))
        data.extend(str(n).encode() + b' 0 obj\n' + body + b'\nendobj\n')
    xref = len(data)
    data.extend(b'xref\n0 5\n0000000000 65535 f \n')
    for offset in offsets[1:]:
        data.extend(('%010d 00000 n \n' % offset).encode())
    data.extend(('trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n' % xref).encode())
    return bytes(data)


class Formats(Harness):
    def fixture(self, category, case, ext, expected=(), behavior='keep'):
        path = self.media / category / case / (case + '.' + ext)
        path.parent.mkdir(parents=True, exist_ok=True)
        f = dict(case=case, category=category, path=str(path), expected=list(expected), behavior=behavior)
        self.fixtures.append(f)
        return path

    def command(self, args):
        result = subprocess.run(list(map(str, args)), capture_output=True, timeout=120)
        if result.returncode:
            raise RuntimeError(result.stderr.decode(errors='replace') + result.stdout.decode(errors='replace'))

    def encode(self, args):
        r = self.process(['-nostdin', '-v', 'error', *args])
        if r.returncode:
            raise RuntimeError(r.stderr.decode(errors='replace'))

    def build_fixtures(self):
        for ext, video, audio in VIDEO:
            path = self.fixture('movies', 'healthy-' + ext, ext)
            args = ['-f', 'lavfi', '-i', 'testsrc2=size=320x180:rate=24:duration=4',
                    '-f', 'lavfi', '-i', 'sine=duration=4:sample_rate=48000',
                    '-c:v', video, '-c:a', audio, '-metadata:s:a:0', 'language=eng', '-shortest']
            if video == 'libx264':
                args += ['-preset', 'ultrafast']
            if ext == 'm4v':
                args += ['-f', 'mp4']
            self.encode(args + [path])
            self.record('generated ' + path.stem + ' full decode', self.decodes(path))
            for damage in ('zero', 'junk'):
                broken = self.fixture('movies', damage + '-' + ext, ext, ['Playability'], 'corrupt')
                broken.write_bytes(b'' if damage == 'zero' else b'not media\x00' * 1024)
        for ext, codec in AUDIO:
            category = 'audiobooks' if ext == 'm4b' else 'music'
            path = self.fixture(category, 'healthy-' + ext, ext,
                                ['FormatCompatibility'] if ext == 'wma' else [],
                                'convert' if ext == 'wma' else 'keep')
            args = ['-f', 'lavfi', '-i', 'sine=duration=4:sample_rate=48000', '-c:a', codec,
                    '-metadata', 'title=Owned ' + ext, '-metadata', 'artist=MediaDash Test',
                    '-metadata', 'album=Owned ' + ext]
            if ext == 'm4b':
                args += ['-f', 'mp4']
            self.encode(args + [path])
            self.record('generated ' + path.stem + ' full decode', self.decodes(path))
            for damage in ('zero', 'junk'):
                broken = self.fixture(category, damage + '-' + ext, ext, ['Playability'], 'corrupt')
                broken.write_bytes(b'' if damage == 'zero' else b'not audio\x00' * 1024)
        base = self.media / 'movies/healthy-mp4/healthy-mp4.mp4'
        for case, codec, extra in [('healthy-hevc', 'libx265', ['-preset', 'ultrafast', '-x265-params', 'pools=1:log-level=error']),
                                   ('healthy-av1', 'libsvtav1', ['-preset', '12', '-svtav1-params', 'lp=2']),
                                   ('healthy-hdr', 'libx265', ['-preset', 'ultrafast', '-x265-params', 'pools=1:log-level=error', '-pix_fmt', 'yuv420p10le', '-color_primaries', 'bt2020', '-color_trc', 'smpte2084', '-colorspace', 'bt2020nc'])]:
            path = self.fixture('movies', case, 'mkv')
            self.encode(['-i', base, '-c:v', codec, *extra, '-c:a', 'copy', path])
            self.record('generated ' + case + ' full decode', self.decodes(path))
        path = self.fixture('movies', 'silent-video', 'mp4')
        self.encode(['-i', base, '-map', '0:v', '-c', 'copy', path])
        path = self.fixture('movies', 'stream-pointer', 'strm')
        path.write_text('http://127.0.0.1:8099/owned-test-pointer\n', encoding='utf-8')
        for ext in ('mp4', 'mkv'):
            source = self.media / ('movies/healthy-' + ext) / ('healthy-' + ext + '.' + ext)
            path = self.fixture('movies', 'truncated-' + ext, ext, ['Playability'], 'refuse')
            path.write_bytes(source.read_bytes()[:256])
        srt = self.root / 'subtitle.srt'
        srt.write_text('1\n00:00:00,000 --> 00:00:03,000\nOwned subtitle\n', encoding='utf-8')
        for ext, content in [('srt', srt.read_text(encoding='utf-8')),
                             ('vtt', 'WEBVTT\n\n00:00:00.000 --> 00:00:03.000\nOwned caption\n'),
                             ('ass', '[Script Info]\nScriptType: v4.00+\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\nStyle: Default,Arial,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,1,0,2,10,10,10,1\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:00.00,0:00:03.00,Default,,0,0,0,,Owned caption\n')]:
            sidecar = base.with_suffix('.eng.' + ext)
            sidecar.write_text(content, encoding='utf-8')
        tracks = self.fixture('movies', 'multi-language', 'mkv', ['AudioLanguage', 'SubtitleLanguage'], 'tracks')
        self.encode(['-i', base, '-i', srt, '-map', '0:v', '-map', '0:a', '-map', '0:a', '-map', '1:0',
                     '-c', 'copy', '-c:s', 'srt', '-metadata:s:a:0', 'language=eng',
                     '-metadata:s:a:1', 'language=fra', '-metadata:s:s:0', 'language=fra', tracks])
        for case in ('foreign-only', 'unicode café 日本語 & [test]'):
            path = self.fixture('movies', case, 'mp4')
            self.encode(['-i', base, '-c', 'copy', '-metadata:s:a:0', 'language=fra', path])
        for case, size in [('duplicate-low', '160x90'), ('duplicate-high', '320x180')]:
            path = self.fixture('movies', case, 'mp4', ['Duplicate'] if case.endswith('low') else [],
                                'recycle' if case.endswith('low') else 'keep')
            path = path.with_name('Owned Duplicate (2026).mp4')
            self.fixtures[-1]['path'] = str(path)
            self.encode(['-i', base, '-vf', 'scale=' + size, '-c:v', 'libx264', '-preset', 'ultrafast', '-c:a', 'copy', path])
            path.with_suffix('.nfo').write_text('<movie><title>Owned Duplicate</title><year>2026</year><uniqueid type="tmdb" default="true">999999991</uniqueid></movie>', encoding='utf-8')
        path = self.fixture('movies', 'wrong-extension', 'mkv', ['Playability'], 'rename')
        shutil.copyfile(base, path)
        path = self.fixture('movies', 'oversized', 'mp4', ['Quality'], 'quality')
        self.encode(['-f', 'lavfi', '-i', 'testsrc2=size=640x360:rate=24:duration=4',
                     '-f', 'lavfi', '-i', 'sine=duration=4', '-c:v', 'libx264', '-preset', 'ultrafast',
                     '-c:a', 'aac', '-metadata:s:a:0', 'language=eng', '-shortest', path])
        for ext in ('jpg', 'png', 'gif', 'webp', 'bmp', 'tiff'):
            path = self.fixture('photos', 'healthy-' + ext, ext,
                                ['FormatCompatibility'] if ext in ('bmp', 'tiff') else [],
                                'convert' if ext in ('bmp', 'tiff') else 'keep')
            self.encode(['-f', 'lavfi', '-i', 'color=c=blue:size=64x64', '-frames:v', '1', '-update', '1', path])
        page = self.media / 'photos/healthy-jpg/healthy-jpg.jpg'
        cover_audio = self.fixture('music', 'embedded-cover', 'mp3')
        self.encode(['-i', self.media / 'music/healthy-mp3/healthy-mp3.mp3', '-i', page,
                     '-map', '0:a', '-map', '1:v', '-c', 'copy', '-id3v2_version', '3', '-disposition:v', 'attached_pic', cover_audio])
        for ext in ('pdf', 'epub', 'cbz'):
            path = self.fixture('books', 'healthy-' + ext, ext)
            if ext == 'pdf':
                path.write_bytes(pdf_bytes())
            elif ext == 'cbz':
                with zipfile.ZipFile(path, 'w') as z:
                    z.write(page, '001.jpg')
            else:
                with zipfile.ZipFile(path, 'w') as z:
                    z.writestr('mimetype', 'application/epub+zip')
                    z.writestr('META-INF/container.xml', '<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>')
                    z.writestr('content.opf', '<package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="id"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">owned</dc:identifier><dc:title>Owned</dc:title><dc:language>en</dc:language></metadata><manifest><item id="p" href="page.xhtml" media-type="application/xhtml+xml"/></manifest><spine><itemref idref="p"/></spine></package>')
                    z.writestr('page.xhtml', '<html xmlns="http://www.w3.org/1999/xhtml"><head><title>Owned</title></head><body><p>Owned test book.</p></body></html>')
            broken = self.fixture('books', 'broken-' + ext, ext, ['Playability'], 'corrupt')
            broken.write_bytes(b'broken book\x00' * 128)
        for ext in ('cbr', 'cb7'):
            path = self.fixture('books', 'healthy-' + ext, ext, ['FormatCompatibility'], 'convert')
            if ext == 'cbr':
                self.command([self.args.rar, 'a', '-ep', '-idq', path, page])
            else:
                self.command([self.args.sevenzip, 'a', '-t7z', path, page])
        text = self.fixture('books', 'plain-text', 'txt', ['FormatCompatibility'], 'convert')
        text.write_text('Owned book\n\nA short original story used only for MediaDash acceptance testing.\n', encoding='utf-8')
        mobi = self.fixture('books', 'healthy-mobi', 'mobi', ['FormatCompatibility'], 'convert')
        self.command([self.args.calibre, self.media / 'books/healthy-epub/healthy-epub.epub', mobi])
        drm = self.fixture('books', 'drm-epub', 'epub', ['Playability'], 'drm')
        shutil.copyfile(self.media / 'books/healthy-epub/healthy-epub.epub', drm)
        with zipfile.ZipFile(drm, 'a') as z:
            z.writestr('META-INF/encryption.xml', '<encryption><CipherReference URI="page.xhtml"/></encryption>')
        for case, ext, content, issue in [('broken-nfo', 'nfo', b'<movie><broken', 'CorruptNfo'),
                                          ('suspicious-script', 'ps1', b'# inert fixture; never executed\n', 'MalwareRisk'),
                                          ('orphan-subtitle', 'srt', srt.read_bytes(), 'OrphanedDebris')]:
            path = self.fixture('movies', case, ext, [issue], 'recycle')
            path.write_bytes(content)
        for f in self.fixtures:
            path = Path(f['path'])
            f.update(sha256=sha(path), size=path.stat().st_size)
        self.save('fixtures.json', self.fixtures)

    def add_typed(self, category, kind):
        name = 'Common formats ' + self.root.name + ' ' + category
        self.added.append(name)
        query = urllib.parse.urlencode(dict(name=name, collectionType=kind, paths=str(self.media / category), refreshLibrary='false'))
        self.api('/Library/VirtualFolders?' + query, 'POST', {'LibraryOptions': {
            'EnableRealtimeMonitor': False, 'EnableInternetProviders': False,
            'EnableChapterImageExtraction': False, 'AutomaticRefreshIntervalDays': 0}})
        return name

    def owned_issues(self):
        return [i for i in self.open_issues() if Path(i['Path']).is_relative_to(self.media)]

    def load_fixture_source(self, source_root):
        source_root = Path(source_root).resolve(strict=True)
        source_media = source_root / 'media'
        fixtures = json.loads((source_root / 'fixtures.json').read_text(encoding='utf-8'))
        if not isinstance(fixtures, list) or not fixtures:
            raise ValueError('Fixture source is empty or invalid')
        # Validate the entire tree before copying. Never follow a source link or
        # continue into a live fix after the original-hash prerequisite failed.
        if source_media.is_symlink() or any(p.is_symlink() or p.resolve() != p.absolute() for p in source_media.rglob('*')):
            raise ValueError('Fixture source contains filesystem links')
        paths = set()
        for fixture in fixtures:
            path = Path(fixture['path']).resolve(strict=True)
            relative = path.relative_to(source_media)
            if relative in paths:
                raise ValueError('Duplicate fixture source path')
            paths.add(relative)
            if sha(path) != fixture['sha256']:
                raise ValueError('Fixture source hash mismatch: ' + str(relative))
            fixture['path'] = str(self.media / relative)
        shutil.copytree(source_media, self.media, dirs_exist_ok=True)
        self.fixtures = fixtures
        self.save('fixtures.json', self.fixtures)

    def execute(self):
        if self.args.fixtures_source:
            self.load_fixture_source(self.args.fixtures_source)
        else:
            self.build_fixtures()
        self.setup()
        names = [self.add_typed(category, kind) for category, kind in
               [('movies', 'movies'), ('music', 'music'), ('audiobooks', 'books'), ('books', 'books'), ('photos', 'homevideos')]]
        self.run_task('RefreshLibrary', '/Library/Refresh')
        ids = [l['ItemId'] for l in self.api('/Library/VirtualFolders') if l['Name'] in names]
        if len(ids) != len(names):
            raise RuntimeError('Not all fixture libraries were materialized by Jellyfin')
        cfg = dict(self.safe)
        cfg.update({mode: 'ManualApprove' for mode in MODES.values()})
        cfg.update({k: 'RecycleBin' for k in cfg if k.endswith('Disposal')})
        cfg.update(EnabledLibraries=ids, FirstRunDone=True, DryRun=True, AnalyticsEnabled=False,
                   ThoroughPlayabilityCheck=True, PauseDuringPlayback=False, FixWindowStart='', FixWindowEnd='',
                   MinScanFileSizeMb=0, AllowedAudioLanguages=['eng'], AllowedSubtitleLanguages=['eng'],
                   MaxResolutionHeight=240, MaxBitrateMbpsAt1080p=4, PreferredCodec='h264',
                   UseHardwareEncoder=False, TargetContainer='mkv', DuplicateMinAgeDays=0,
                   ConvertEbooks=True, ConvertComics=True, ConvertImages=True, ConvertMusic=True,
                   ConvertAudiobooks=True, ConvertVideos=True, CalibreOverridePath=str(Path(self.args.calibre).parent),
                   QualityMinSavingsPercent=0, QualityMinSavingsBytes=0)
        self.configure(cfg)
        self.scan()
        issues = self.owned_issues()
        self.save('issues-before.json', issues)
        items = self.api('/Items?' + urllib.parse.urlencode(dict(Recursive='true', Fields='Path')) )['Items']
        self.save('items-before.json', [i for i in items if i.get('Path') and Path(i['Path']).is_relative_to(self.media)])
        for f in self.fixtures:
            found = [i for i in issues if i['Path'] == f['path']]
            f['detected'] = sorted({i['Type'] for i in found})
            if f['expected']:
                self.record(f['case'] + ' expected detection', set(f['expected']).issubset(f['detected']), f['detected'])
            else:
                self.record(f['case'] + ' no false playability/language alarm', not set(f['detected']) & {'Playability', 'AudioLanguage', 'SubtitleLanguage'}, f['detected'])
        self.save('fixtures.json', self.fixtures)
        if self.args.pause_for_ui:
            self.save('ui-ready.json', {'libraries': ids, 'issues': len(issues)})
            print('UI READY: create ui-continue marker to proceed', flush=True)
            deadline = time.monotonic() + 900
            while not (self.root / 'ui-continue').exists():
                if time.monotonic() > deadline:
                    raise TimeoutError('UI handoff timed out')
                time.sleep(1)
        chosen = []
        for f in self.fixtures:
            matches = [i for i in issues if i['Path'] == f['path'] and i['Type'] in f['expected']]
            if f['case'] in REPORT_ONLY_DOCUMENTS:
                self.verify_report_only(f, matches)
            else:
                chosen.extend(matches)
        for i in chosen:
            self.api('/MediaDash/Issues/' + str(i['Id']) + '/Approve', 'POST')
        if {i['Id'] for i in self.open_issues() if i['Status'] == 'Queued'} != {i['Id'] for i in chosen}:
            raise RuntimeError('Foreign queued work before dry run')
        self.run_task('MediaDashFix', '/MediaDash/Fix')
        for f in self.fixtures:
            self.record(f['case'] + ' dry run preserves bytes', Path(f['path']).is_file() and sha(f['path']) == f['sha256'])
        for i in self.owned_issues():
            if i['Status'] == 'Queued':
                self.api('/MediaDash/Issues/' + str(i['Id']) + '/Revert', 'POST')
        cfg['DryRun'] = False
        self.configure(cfg)
        for f in self.fixtures:
            if not f['expected']:
                continue
            if f['case'] in REPORT_ONLY_DOCUMENTS:
                source = Path(f['path'])
                self.record(f['case'] + ' report-only source unchanged', source.is_file() and sha(source) == f['sha256']
                            and not any(r['path'] == f['path'] for r in self.rows()))
                continue
            try:
                self.exercise_format(f)
            except Exception:
                self.record(f['case'] + ' execution', False, traceback.format_exc())
                self.idle('MediaDashFix')
            finally:
                self.idle('MediaDashFix')
                for i in self.owned_issues():
                    if i['Status'] == 'Queued':
                        self.api('/MediaDash/Issues/' + str(i['Id']) + '/Revert', 'POST')
        self.save('history-final.json', self.rows())
        self.save('status-final.json', self.api('/MediaDash/Status'))
        self.record('no foreign history writes', all(Path(r['path']).is_relative_to(self.media) for r in self.rows()))
        for f in self.fixtures:
            if not f['expected']:
                self.record(f['case'] + ' healthy control preserved', Path(f['path']).is_file() and sha(f['path']) == f['sha256'])

    def verify_report_only(self, fixture, issues):
        name = fixture['case'] + ' report-only approval refused'
        if len(issues) != 1 or issues[0]['Type'] != 'Playability' or issues[0]['Status'] != 'Detected':
            self.record(name, False, 'Expected one detected Playability issue')
            return
        try:
            self.api('/MediaDash/Issues/' + str(issues[0]['Id']) + '/Approve', 'POST')
        except urllib.error.HTTPError as error:
            self.record(name, error.code == 409 and b'This finding is report-only.' in error.read(), error.code)
        else:
            self.record(name, False, 'Report-only issue was approved')

    def exercise_format(self, f):
        issues = [i for i in self.owned_issues() if i['Path'] == f['path'] and i['Type'] in f['expected']]
        if not issues:
            self.record(f['case'] + ' fix available', False, 'Expected issue was not detected; no synthetic DB rows inserted')
            return
        start = self.rows('SELECT COALESCE(MAX(id),0) AS n FROM history', ())[0]['n']
        for i in issues:
            if i['Status'] == 'Detected':
                self.api('/MediaDash/Issues/' + str(i['Id']) + '/Approve', 'POST')
        if {i['Id'] for i in self.open_issues() if i['Status'] == 'Queued'} != {i['Id'] for i in issues}:
            raise RuntimeError('Foreign queued work before real fix')
        self.run_task('MediaDashFix', '/MediaDash/Fix')
        ids = {i['Id'] for i in issues}
        rows = [r for r in self.rows() if r['id'] > start and r['issue_id'] in ids and r['path'] == f['path']]
        if not self.record(f['case'] + ' attributable history', len(rows) == len(issues), [r['action'] for r in rows]):
            return
        source = Path(f['path'])
        if f['behavior'] == 'corrupt' and all(not r['success'] for r in rows):
            self.record(f['case'] + ' refusal preserves original', source.is_file() and sha(source) == f['sha256'] and not any(r['recycle_path'] for r in rows), rows)
            return
        if f['behavior'] in ('drm', 'refuse'):
            self.record(f['case'] + ' protected content preserved', source.is_file() and sha(source) == f['sha256'] and not any(r['recycle_path'] for r in rows), rows)
            if f['behavior'] == 'refuse':
                self.record(f['case'] + ' unsafe mutation refused', all(not r['success'] for r in rows))
            return
        if not self.record(f['case'] + ' successful user action', all(r['success'] for r in rows), [r['action'] for r in rows]):
            self.record(f['case'] + ' failure preserves original', source.is_file() and sha(source) == f['sha256'])
            return
        recycled = [r for r in rows if r['recycle_path']]
        if f['behavior'] == 'rename':
            output = source.with_suffix('.mp4')
            self.record(f['case'] + ' rename preserves payload', output.is_file() and sha(output) == f['sha256'] and not source.exists())
            output.rename(source)  # Restore the owned filename for repeatable fixtures.
        else:
            self.record(f['case'] + ' original recoverable', bool(recycled) and any(Path(r['recycle_path']).is_file() and sha(r['recycle_path']) == f['sha256'] for r in recycled))
            outputs = [p for p in source.parent.iterdir() if p.is_file() and p.suffix.lower() in {'.mp4', '.mkv', '.mp3', '.flac', '.jpg', '.cbz', '.epub'}] if source.parent.is_dir() else []
            if f['behavior'] in ('tracks', 'quality'):
                self.record(f['case'] + ' output decodes', len(outputs) == 1 and self.decodes(outputs[0]))
                if outputs and f['behavior'] == 'tracks':
                    streams = self.probe(outputs[0]).get('streams', [])
                    self.record('track fix keeps one English audio and video', sum(s['codec_type'] == 'audio' for s in streams) == 1 and any(s['codec_type'] == 'video' for s in streams) and not any(s['codec_type'] == 'subtitle' for s in streams), streams)
            elif f['behavior'] == 'convert':
                self.record(f['case'] + ' converted output exists', bool(outputs) and not source.exists(), [str(p) for p in outputs])
                for output in outputs:
                    if output.suffix in ('.mp3', '.flac', '.jpg'):
                        self.record(f['case'] + ' converted output decodes', self.decodes(output))
                    elif output.suffix in ('.cbz', '.epub'):
                        with zipfile.ZipFile(output) as z:
                            self.record(f['case'] + ' converted archive integrity', z.testzip() is None and bool(z.namelist()))
            elif f['behavior'] == 'recycle':
                self.record(f['case'] + ' removed from library', not source.exists())
            restored_paths = set()
            for r in recycled:
                # A coalesced audio/subtitle action records both issue IDs but recycles once.
                if r['recycle_path'] in restored_paths:
                    continue
                self.api('/MediaDash/History/' + str(r['id']) + '/Restore?force=true', 'POST')
                restored_paths.add(r['recycle_path'])
            self.record(f['case'] + ' restore exact original', source.is_file() and sha(source) == f['sha256'])


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--self-test', action='store_true')
    p.add_argument('--url', default='http://localhost:8099')
    p.add_argument('--username', default='test')
    p.add_argument('--password-env', default='MEDIADASH_TEST_PASSWORD')
    p.add_argument('--ffmpeg')
    p.add_argument('--data-dir')
    p.add_argument('--output')
    p.add_argument('--timeout', type=float, default=600)
    p.add_argument('--confirm-live', action='store_true')
    p.add_argument('--hold-existing-queue', action='store_true')
    p.add_argument('--pause-for-ui', action='store_true')
    p.add_argument('--generate-only', action='store_true', help='Create and validate owned media without connecting to Jellyfin')
    p.add_argument('--fixtures-source', help='Reuse media and manifest from a prior generate-only run')
    p.add_argument('--calibre', default=shutil.which('ebook-convert') or 'ebook-convert')
    p.add_argument('--rar', default=shutil.which('rar') or 'rar')
    p.add_argument('--sevenzip', default=shutil.which('7z') or '7z')
    args = p.parse_args()
    if args.self_test:
        pdf = pdf_bytes()
        offset = int(pdf.split(b'startxref\n')[1].splitlines()[0])
        assert pdf[offset:offset + 4] == b'xref' and pdf.endswith(b'%%EOF\n')
        assert len({r[0] for r in VIDEO}) == len(VIDEO)
        assert len({r[0] for r in AUDIO}) == len(AUDIO)
        assert exit_code([{'status': 'FAIL'}]) == 1
        print('PASS fixture PDF, format uniqueness, failure exit code')
        return 0
    if args.timeout <= 0 or not all((args.ffmpeg, args.output)) or (not args.generate_only and not (args.confirm_live and args.data_dir)):
        p.error('--ffmpeg and --output are required; live runs also require --confirm-live and --data-dir')
    h = Formats(args)
    if args.generate_only:
        h.build_fixtures()
        return exit_code(h.results)
    try:
        h.execute()
    except (Exception, KeyboardInterrupt):
        h.record('matrix execution', False, traceback.format_exc())
    finally:
        if h.original is not None:
            h.cleanup()
    code = exit_code(h.results)
    h.save('summary.json', dict(exitCode=code, passed=sum(r['status'] == 'PASS' for r in h.results),
                              failed=sum(r['status'] == 'FAIL' for r in h.results), fixtures=len(h.fixtures)))
    print('Evidence:', h.root, 'exit:', code, flush=True)
    return code


if __name__ == '__main__':
    sys.exit(main())
