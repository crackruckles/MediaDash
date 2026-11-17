"""Scan-only acceptance against the disposable container owned by integration-test.sh."""
import argparse
import json
import sys
import time
import urllib.parse
import urllib.request


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args):
        raise RuntimeError('Refusing an integration-server redirect')


def wait_for(predicate, timeout, description):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        result = predicate()
        if result:
            return result
        time.sleep(2)
    raise TimeoutError(description)


def completed(before, task):
    result = task.get('LastExecutionResult') or {}
    if task.get('State') != 'Idle' or not result.get('EndTimeUtc') or result.get('StartTimeUtc') == before.get('StartTimeUtc'):
        return False
    if not result.get('StartTimeUtc'):
        return False
    if result.get('Status') != 'Completed':
        raise RuntimeError('Scan failed: ' + str(result))
    return task


def assert_counts(issues):
    if not isinstance(issues, list):
        raise ValueError('Issues endpoint must return an array')
    for kind in ('Duplicate', 'Quality', 'SubtitleLanguage', 'AudioLanguage'):
        count = sum(row.get('Type') == kind for row in issues)
        if count < 1:
            raise AssertionError('Expected at least one ' + kind + ' issue')
        print('PASS', kind, count, flush=True)


def run(port):
    if port in (8096, 8099) or not 1024 <= port <= 65535:
        raise ValueError('Use a dedicated disposable-container port, excluding 8096/8099')
    base = 'http://127.0.0.1:' + str(port)
    http = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect())
    headers = {'Authorization': 'MediaBrowser Client="itest", Device="ci", DeviceId="ci", Version="1"'}

    def api(path, method='GET', body=None):
        payload = None if body is None else json.dumps(body).encode()
        request = urllib.request.Request(base + path, data=payload, headers=dict(headers, **{'Content-Type': 'application/json'}), method=method)
        with http.open(request, timeout=15) as response:
            data = response.read()
            return json.loads(data) if data else None

    api('/Startup/Configuration', 'POST', dict(UICulture='en-US', MetadataCountryCode='US', PreferredMetadataLanguage='en'))
    api('/Startup/User')
    api('/Startup/User', 'POST', dict(Name='ci', Password='ci'))
    api('/Startup/Complete', 'POST')
    auth = api('/Users/AuthenticateByName', 'POST', dict(Username='ci', Pw='ci'))
    headers['Authorization'] = 'MediaBrowser Token="' + auth['AccessToken'] + '", Client="itest", Device="ci", DeviceId="ci", Version="1"'
    plugin = next(p for p in api('/Plugins') if p['Name'] == 'MediaDash')
    if plugin['Status'] != 'Active':
        raise RuntimeError('Plugin is not active')
    api('/Library/VirtualFolders?' + urllib.parse.urlencode(dict(name='Fixtures', collectionType='movies', paths='/media/movies', refreshLibrary='true')), 'POST', {'LibraryOptions': {'EnableRealtimeMonitor': False, 'EnableInternetProviders': False}})
    wait_for(lambda: api('/Items?IncludeItemTypes=Movie&Recursive=true')['TotalRecordCount'] >= 5, 180, 'Library never indexed five fixtures')
    libraries = api('/Library/VirtualFolders')
    config_path = '/Plugins/' + plugin['Id'] + '/Configuration'
    config = api(config_path)
    config.update({k: 'ManualApprove' for k in config if k.endswith('FixMode')})
    config.update(AllowedAudioLanguages=['eng'], AllowedSubtitleLanguages=['eng'], FirstRunDone=True, DryRun=True,
                  EnabledLibraries=[entry['ItemId'] for entry in libraries], MinScanFileSizeMb=0, DuplicateMinAgeDays=0)
    api(config_path, 'POST', config)

    def task():
        return next(t for t in api('/ScheduledTasks') if t['Key'] == 'MediaDashScan')

    # Drain a configuration-triggered scan, then attribute the manual scan to a
    # fresh completed execution. An old Idle result cannot satisfy this gate.
    time.sleep(2)
    before = wait_for(lambda: (t if (t := task())['State'] == 'Idle' else False), 300, 'Prior scan did not stop')
    api('/MediaDash/Scan', 'POST')
    wait_for(lambda: completed(before.get('LastExecutionResult') or {}, task()), 300, 'New scan did not finish')
    assert_counts(api('/MediaDash/Issues?openOnly=true'))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', type=int, default=8097)
    args = parser.parse_args()
    try:
        run(args.port)
    except Exception as error:
        print('FAIL:', error, file=sys.stderr)
        sys.exit(1)
