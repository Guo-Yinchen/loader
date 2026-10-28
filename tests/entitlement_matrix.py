"""Per-program renewal, legacy migration and Beta passport integration tests.

Uses signed throwaway packages and the same HTTP paths as the launcher/admin.
"""
import csv
import io
import json
import tempfile
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
from pathlib import Path

from admin_matrix import ROOT, call, DIRECT
from version_matrix import Harness, DEVICE


def main():
    checks = 0

    def check(name, condition):
        nonlocal checks
        assert condition, name
        checks += 1
        print('PASS ' + name)

    with tempfile.TemporaryDirectory(dir=ROOT / '.local/maintenance-check', prefix='entitlements-') as temporary:
        harness = Harness(Path(temporary))
        harness.prepare()  # chispa=A and other=B
        assert harness.cli('program-add', 'chispa-beta', 'A Beta', 'Other.exe', 'beta', 'chispa').returncode == 0
        assert harness.cli('program-add', 'other-beta', 'B Beta', 'Other.exe', 'beta', 'other').returncode == 0
        for program, version in [('chispa', '1.0.0'), ('other', '5.0.0'), ('chispa-beta', '1.0.0'), ('other-beta', '5.0.0')]:
            harness.publish(program, version)
        harness.env['CHISPA_LIMIT_LOGIN_PER_MINUTE'] = '600'
        harness.start()
        try:
            admin, csrf = harness.admin()

            def api(path, body=None, token=None, method=None):
                headers = {'Content-Type': 'application/json'}
                if token:
                    headers['Authorization'] = 'Bearer ' + token
                status, _, raw = call(DIRECT, method or ('POST' if body is not None else 'GET'),
                                      harness.base + '/api/v1/' + path, headers,
                                      json.dumps(body).encode() if body is not None else None)
                return status, json.loads(raw) if raw else None

            def code(mode='single', ids=('chispa',), days=30, beta=False):
                fields = [('__RequestVerificationToken', csrf), ('count', '1'), ('days', str(days)),
                          ('accessMode', mode), ('betaAccess', '1' if beta else '')]
                fields += [('programIds', value) for value in ids]
                import urllib.parse
                status, _, raw = call(admin, 'POST', harness.base + '/admin/activation/generate',
                                      {'Content-Type': 'application/x-www-form-urlencoded'},
                                      urllib.parse.urlencode(fields).encode())
                assert status == 200, raw
                return next(csv.DictReader(io.StringIO(raw)))['code']

            def register(name, activation):
                email = name + '@example.com'
                status, result = api('register', {'email': email, 'password': 'registration-password-123', 'code': activation})
                assert status == 200, result
                return login(email)

            def login(name, password='registration-password-123'):
                status, result = api('session', {'username': name, 'password': password})
                assert status == 200, result
                return result['token']

            def redeem(token, activation):
                return api('activation/redeem', {'code': activation}, token)

            def programs(token):
                status, result = api('programs', token=token)
                assert status == 200
                return {p['id']: p for p in result}

            def expiry(token, program='chispa'):
                raw = programs(token)[program]['entitlementExpiresAt']
                return datetime.fromisoformat(raw) if raw else None

            def record(name):
                return next(u for u in json.loads((harness.data / 'users.json').read_text()) if u['username'] == name + '@example.com')

            def set_expiry(name, program, when):
                status, _, raw = harness.admin_form(admin, csrf,
                    f'/admin/users/{name}@example.com/programs/{program}/expiry',
                    {'expiry': when.astimezone().strftime('%Y-%m-%dT%H:%M')})
                assert status == 200, raw

            token = register('mixed', code('multiple', ('chispa', 'other')))
            a, b = expiry(token), expiry(token, 'other')
            check('AB each start with thirty days', a == b and abs((a - datetime.now(timezone.utc)).total_seconds() - 30 * 86400) < 10)
            check('AB30 plus A30 accepted', redeem(token, code())[0] == 200)
            check('AB30 plus A30 gives A60 B30', expiry(token) == a + timedelta(days=30) and expiry(token, 'other') == b)
            check('all-program code accepted after mixed scope', redeem(token, code('all'))[0] == 200)
            check('all-program code adds thirty to each separately', expiry(token) == a + timedelta(days=60) and expiry(token, 'other') == b + timedelta(days=30))
            check('activation history retained', sum(k.get('usedBy') == 'mixed@example.com' for k in json.loads((harness.data / 'activation-keys.json').read_text())) == 3)
            concurrent = [code(days=7), code(ids=('other',), days=7)]
            previous_a, previous_b = expiry(token), expiry(token, 'other')
            with ThreadPoolExecutor(max_workers=2) as pool:
                responses = list(pool.map(lambda activation: redeem(token, activation)[0], concurrent))
            check('concurrent different codes preserve both program updates', responses == [200, 200] and
                  expiry(token) == previous_a + timedelta(days=7) and expiry(token, 'other') == previous_b + timedelta(days=7))
            once = code(days=1)
            before_race = expiry(token)
            with ThreadPoolExecutor(max_workers=2) as pool:
                responses = list(pool.map(lambda _: redeem(token, once)[0], range(2)))
            check('concurrent same code adds time exactly once', sorted(responses) == [200, 400] and expiry(token) == before_race + timedelta(days=1))

            single = register('single', code())
            original = expiry(single)
            check('A only cannot see B or Beta', set(programs(single)) == {'chispa'})
            check('A account can add B', redeem(single, code(ids=('other',)))[0] == 200)
            check('adding B preserves A', set(programs(single)) == {'chispa', 'other'} and expiry(single) == original)
            before_passport = record('single')['programExpiries']
            check('Beta-only passport can be redeemed', redeem(single, code('beta'))[0] == 200)
            check('Beta passport changes no stable time', record('single')['programExpiries'] == before_passport)
            check('AB with passport sees both Betas', set(programs(single)) == {'chispa', 'other', 'chispa-beta', 'other-beta'})
            check('each Beta uses its stable expiry', expiry(single, 'chispa-beta') == expiry(single) and expiry(single, 'other-beta') == expiry(single, 'other'))
            check('duplicate passport is not consumed', redeem(single, code('beta'))[0] == 400)
            a_only = register('beta-a', code(beta=True))
            check('A with Beta permission only opens A Beta', set(programs(a_only)) == {'chispa', 'chispa-beta'})
            b_only = register('beta-b', code(ids=('other',), beta=True))
            check('B with Beta permission only opens B Beta', set(programs(b_only)) == {'other', 'other-beta'})
            passport_only = register('passport', code('beta'))
            check('passport alone grants no program', programs(passport_only) == {})
            check('passport account reports an empty entitlement map', api('session', token=passport_only)[1]['programExpiries'] == {})
            check('passport remains usable when stable added later', redeem(passport_only, code())[0] == 200 and set(programs(passport_only)) == {'chispa', 'chispa-beta'})

            # Issue a real ticket and lease to verify that B's longer expiry cannot keep A running.
            status, result = api('programs/chispa/releases/1.0.0/launch-ticket', {}, single)
            assert status == 200, result
            ticket = result['ticket']
            status, result = api('launch/redeem', {'ticket': ticket, 'programId': 'chispa', 'version': '1.0.0', 'device': DEVICE})
            assert status == 200, result
            lease = result['lease']
            check('lease reports A expiry', datetime.fromisoformat(result['accountExpiresAt']) == expiry(single))
            set_expiry('single', 'chispa', datetime.now().astimezone() - timedelta(days=1))
            check('expired A kept in catalog with its own deadline', expiry(single) < datetime.now(timezone.utc) < expiry(single, 'other'))
            for path in ['programs/chispa/releases/latest', 'programs/chispa/releases/1.0.0/package',
                         'programs/chispa-beta/releases/latest']:
                check('expired A denied: ' + path, api(path, token=single)[0] == 403)
            check('expired A cannot get ticket', api('programs/chispa/releases/1.0.0/launch-ticket', {}, single)[0] == 403)
            check('B remains available when A expires', api('programs/other/releases/latest', token=single)[0] == 200)
            status, _, _ = call(DIRECT, 'POST', harness.base + '/api/v1/launch/heartbeat',
                                {'Authorization': 'Bearer ' + lease, 'X-Chispa-Device': DEVICE})
            check('expired A loses running lease despite active B', status == 403)
            previous_b = expiry(single, 'other')
            check('expired A can renew', redeem(single, code(days=1))[0] == 200)
            check('expired A restarts from now without extending B', abs((expiry(single) - datetime.now(timezone.utc)).total_seconds() - 86400) < 10 and expiry(single, 'other') == previous_b)
            check('renewed A Beta follows automatically', api('programs/chispa-beta/releases/latest', token=single)[0] == 200)
            check('A can become permanent', redeem(single, code(days=0))[0] == 200 and expiry(single) is None)
            check('permanent A does not waste single code', redeem(single, code())[0] == 400)
            old_b = expiry(single, 'other')
            check('permanent A does not prevent renewing B via all code', redeem(single, code('all'))[0] == 200 and expiry(single) is None and expiry(single, 'other') == old_b + timedelta(days=30))
            kept = record('single')['programExpiries']
            harness.admin_form(admin, csrf, '/admin/users/single@example.com/access',
                {'accessMode': 'all', 'betaAccess': '1'})
            check('admin scope save preserves individual times', record('single')['programExpiries'] == kept)
            status, _, _ = call(admin, 'POST', harness.base + '/admin/users/single@example.com/programs/chispa/expiry',
                                {'Content-Type': 'application/x-www-form-urlencoded'}, b'expiry=')
            check('per-program admin expiry requires CSRF', status == 400 and record('single')['programExpiries'] == kept)

            # Legacy scope is expanded exactly once; its old shared deadline is preserved.
            legacy = harness.user_token()
            check('legacy account has no per-program map initially', next(u for u in json.loads((harness.data / 'users.json').read_text()) if u['username'] == 'versionuser').get('programExpiries') is None)
            end = datetime.now().astimezone() + timedelta(days=30)
            harness.admin_form(admin, csrf, '/admin/users/versionuser/expiry', {'expiry': end.strftime('%Y-%m-%dT%H:%M')})
            old = expiry(legacy)
            check('legacy all account accepts single code', redeem(legacy, code())[0] == 200)
            check('legacy A increases and B deadline preserved', expiry(legacy) == old + timedelta(days=30) and expiry(legacy, 'other') == old)
            before_restart = record('single')
            harness.stop()
            harness.start()
            single = login('single@example.com')
            check('separate times and passport survive restart', record('single') == before_restart and set(programs(single)) == {'chispa', 'other', 'chispa-beta', 'other-beta'})
            print(f'{checks} entitlement checks passed')
        finally:
            harness.stop()


if __name__ == '__main__':
    main()
