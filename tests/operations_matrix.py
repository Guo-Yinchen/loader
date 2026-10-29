"""Isolated admin operations regression; CHISPA_TEST_SERVER selects the validation build."""
import csv
from datetime import datetime, timedelta, timezone
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import urllib.parse

from version_matrix import Harness, ROOT, DIRECT, DEVICE, USER, call


def main():
    checks = 0

    def check(name, passed):
        nonlocal checks
        assert passed, name
        checks += 1
        print('PASS ' + name)

    output = ROOT / '.local/maintenance-check'
    output.mkdir(exist_ok=True, parents=True)
    with tempfile.TemporaryDirectory(prefix='operations-', dir=output) as temporary:
        harness = Harness(Path(temporary))
        try:
            harness.prepare()
            harness.publish('chispa', '1.0.0')
            harness.publish('other', '1.0.0')
            assert harness.cli('program-add', 'chispa-beta', 'Chispa Beta', 'Wardogs.exe', 'beta', 'chispa').returncode == 0
            # Synthetic accounts only; real account storage and deployment pointers are never used.
            user_file = harness.data / 'users.json'
            initial = json.loads(user_file.read_text())
            end = (datetime.now(timezone.utc) + timedelta(days=2)).isoformat()
            initial[0]['expiresAt'] = end
            initial[0]['betaAccess'] = True
            for i in range(25):
                initial.append({**initial[0], 'username': f'demo{i:02}',
                    'expiresAt': None if i == 0 else (datetime.now(timezone.utc) - timedelta(days=1)).isoformat() if i == 1 else end})
            user_file.write_text(json.dumps(initial))
            harness.start()
            admin, csrf = harness.admin()
            token = harness.user_token()

            def api(path, data=None, bearer=None, method=None):
                headers = {'Authorization': 'Bearer ' + (bearer or token), 'Content-Type': 'application/json'}
                status, _, body = call(DIRECT, method or ('POST' if data is not None else 'GET'),
                    harness.base + '/api/v1/' + path, headers, json.dumps(data).encode() if data is not None else None)
                return status, json.loads(body) if body else None

            def get(path):
                status, _, body = call(admin, 'GET', harness.base + path)
                assert status == 200
                return json.loads(body)

            def form(path, fields):
                status, _, body = harness.admin_form(admin, csrf, path, fields)
                assert status == 200
                assert b"class='error'" not in body, body[:200]
                return body

            def expiry(program='chispa'):
                values = api('programs')[1]
                return datetime.fromisoformat(next(p['entitlementExpiresAt'] for p in values if p['id'] == program))

            def generate(days=1):
                status, _, body = harness.admin_form(admin, csrf, '/admin/activation/generate',
                    {'count': 1, 'days': days, 'accessMode': 'single', 'programIds': 'chispa'})
                assert status == 200
                return next(csv.DictReader(io.StringIO(body.decode())))['code']

            status, _, unauthenticated = call(DIRECT, 'GET', harness.base + '/admin/online')
            check('online API requires admin authentication', status in (401, 403) or (b'/admin/login' in unauthenticated and b'"programs"' not in unauthenticated))
            check('login alone is not program presence', get('/admin/online')['onlineUsers'] == 0)
            status, ticket = api('programs/chispa/releases/1.0.0/launch-ticket', {})
            assert status == 200, ticket
            status, lease = api('launch/redeem', {'ticket': ticket['ticket'], 'programId': 'chispa', 'version': '1.0.0', 'device': DEVICE})
            assert status == 200, lease
            online = get('/admin/online')
            check('real running lease identifies user and version', online['onlineUsers'] == 1 and online['programs'][0]['username'] == USER and online['programs'][0]['version'] == '1.0.0')
            check('online payload excludes authentication secrets', not any(k in json.dumps(online).lower() for k in ['userhash', 'sessionid', '"token"', '"device"', lease['lease'].lower()]))
            call(DIRECT, 'POST', harness.base + '/api/v1/launch/heartbeat', {'Authorization': 'Bearer ' + lease['lease'], 'X-Chispa-Device': DEVICE})
            check('heartbeat advances last seen', get('/admin/online')['programs'][0]['lastSeen'] >= online['programs'][0]['lastSeen'])
            check('withdraw lease succeeds', api('launch/lease', bearer=lease['lease'], method='DELETE')[0] == 204)
            check('withdrawn program disappears', get('/admin/online')['onlineUsers'] == 0)

            program_file = harness.data / 'programs/chispa/program.json'
            before = program_file.read_bytes()
            status, _, _ = call(admin, 'POST', harness.base + '/admin/programs/chispa/billing',
                {'Content-Type': 'application/x-www-form-urlencoded'}, b'paused=1')
            check('billing rejects missing CSRF without mutation', status == 400 and program_file.read_bytes() == before)
            a, b = expiry(), expiry('other')
            form('/admin/programs/chispa/billing', {'paused': '1'})
            first_pause = json.loads(program_file.read_text())['billingPausedAt']
            time.sleep(1.1)
            frozen = expiry()
            check('pause freezes remaining time only for target program', (frozen-a).total_seconds() >= 1 and expiry('other') == b)
            form('/admin/programs/chispa/billing', {'paused': '1'})
            check('repeated pause is idempotent', json.loads(program_file.read_text())['billingPausedAt'] == first_pause)
            check('Beta shares frozen stable deadline', abs((expiry('chispa-beta')-expiry()).total_seconds()) < .02)
            status, registered = api('register', {'email': 'newpause@example.com', 'password': 'a test account password', 'code': generate()})
            check('new activation during pause gets only purchased time', status == 200 and abs((datetime.fromisoformat(registered['accountExpiresAt'])-datetime.now(timezone.utc)).total_seconds()-86400) < .2)
            manual = (datetime.now(timezone.utc)+timedelta(days=3)).replace(second=0, microsecond=0)
            form('/admin/users/newpause@example.com/programs/chispa/expiry', {'expiry': manual.astimezone().strftime('%Y-%m-%dT%H:%M')})
            status, new_session = api('session', {'username': 'newpause@example.com', 'password': 'a test account password'})
            assert status == 200
            check('manual deadline during pause does not double-count earlier pause', abs((datetime.fromisoformat(new_session['accountExpiresAt'])-manual).total_seconds()) < .2)
            form('/admin/users/newpause@example.com/delete', {})
            expired_status, expired_session = api('session', {'username': 'demo01', 'password': 'correct horse battery staple'})
            assert expired_status == 200
            check('pause does not revive expired accounts', api('programs/chispa/releases/latest', bearer=expired_session['token'])[0] in (401, 403))
            code = generate()
            old_remaining = (expiry()-datetime.now(timezone.utc)).total_seconds()
            check('renewal while paused succeeds', api('activation/redeem', {'code': code})[0] == 200)
            check('renewal adds exactly one day while frozen', abs((expiry()-datetime.now(timezone.utc)).total_seconds()-old_remaining-86400) < .2)
            form('/admin/programs/chispa/compensate', {'minutes': '60'})
            check('compensation adds requested duration during pause', abs((expiry()-datetime.now(timezone.utc)).total_seconds()-old_remaining-90000) < .2)
            users = json.loads(user_file.read_text())
            check('permanent and expired accounts excluded from compensation', next(u for u in users if u['username'] == 'demo00')['expiresAt'] is None and next(u for u in users if u['username'] == 'demo01')['expiresAt'] == initial[2]['expiresAt'])
            persisted_remaining = (expiry()-datetime.now(timezone.utc)).total_seconds()
            harness.stop()
            time.sleep(1.1)
            harness.start()
            admin, csrf = harness.admin()
            token = harness.user_token()
            check('pause survives restart and downtime', abs((expiry()-datetime.now(timezone.utc)).total_seconds()-persisted_remaining) < .2)
            form('/admin/programs/chispa/billing', {'paused': '0'})
            resumed = expiry()
            time.sleep(1.1)
            check('resume stops extending the deadline', expiry() == resumed)
            form('/admin/programs/chispa/billing', {'paused': '0'})
            check('repeated resume adds no compensation', expiry() == resumed)
            form('/admin/programs/chispa/compensate', {'minutes': '30'})
            check('normal compensation is exact and other program unchanged', expiry() == resumed + timedelta(minutes=30) and expiry('other') == b)
            before = user_file.read_bytes()
            status, _, page = harness.admin_form(admin, csrf, '/admin/programs/chispa/compensate', {'minutes': '-1'})
            check('invalid compensation makes no changes', b"class='error'" in page and user_file.read_bytes() == before)
            check('API account deadline matches program deadline', abs((datetime.fromisoformat(api('session')[1]['accountExpiresAt'])-expiry()).total_seconds()) < .01)

            baseline = get('/admin/metrics')
            package = (harness.work / 'package-chispa.zip').read_bytes()
            status, _, body = call(DIRECT, 'GET', harness.base + '/api/v1/programs/chispa/releases/1.0.0/package', {'Authorization': 'Bearer ' + token})
            check('stream metering preserves full package bytes', status == 200 and body == package)
            after = get('/admin/metrics')
            check('download increments actual response bytes', after['sentBytes'] >= baseline['sentBytes'] + len(package))
            status, _, body = call(DIRECT, 'GET', harness.base + '/api/v1/programs/chispa/releases/1.0.0/package', {'Authorization': 'Bearer ' + token, 'Range': 'bytes=0-7'})
            check('range downloads remain correct', status == 206 and body == package[:8])
            headers = {'X-CSRF-TOKEN': csrf, 'Content-Type': 'application/json'}
            status, _, result = call(admin, 'POST', harness.base + '/admin/uploads', headers,
                json.dumps({'programId': 'chispa', 'version': '9.0.0', 'size': len(package), 'signature': (harness.work / 'chispa-1.0.0.signature.json').read_text()}).encode())
            assert status == 200, result
            upload = json.loads(result)['id']
            before = get('/admin/metrics')
            status, _, result = call(admin, 'PUT', harness.base + '/admin/uploads/' + upload,
                {'X-CSRF-TOKEN': csrf, 'Content-Type': 'application/octet-stream', 'X-Upload-Offset': '0'}, package)
            check('chunk upload remains functional', status == 200 and json.loads(result)['received'] == len(package))
            check('upload increments exact consumed request bytes', get('/admin/metrics')['receivedBytes']-before['receivedBytes'] == len(package))
            generate(7)
            generate(30)
            status, _, html = call(admin, 'GET', harness.base + '/admin')
            check('card menu has program and duration levels', b"class='key-scope'" in html and html.count(b"class='key-duration'") >= 3)
            check('each stable program exposes billing actions', html.count(b'/compensate') == 2 and html.count(b'/billing') == 2)
            check('account filters and pager rendered', all(value in html for value in [b'id=\'accountState\'', b'id=\'accountProgram\'', b'id=\'accountNext\'']))
            audit = '\n'.join(p.read_text(encoding='utf-8') for p in harness.data.rglob('*audit*.jsonl'))
            if not audit:
                audit = '\n'.join(p.read_text(encoding='utf-8') for p in harness.data.rglob('*audit*.log'))
            check('billing operations audited', all(action in audit for action in ['billing-pause', 'billing-resume', 'billing-compensate']))
            if os.environ.get('CHISPA_TEST_BROWSER') == '1':
                status, ticket = api('programs/chispa/releases/1.0.0/launch-ticket', {})
                assert status == 200
                assert api('launch/redeem', {'ticket': ticket['ticket'], 'programId': 'chispa', 'version': '1.0.0', 'device': DEVICE})[0] == 200
                subprocess.run(['node', str(ROOT / 'tests/operations_browser.mjs'), harness.base], check=True)
            print(f'{checks} operations checks passed')
        finally:
            harness.stop()


if __name__ == '__main__':
    main()
