"""Disposable-server registration, activation, renewal and crash-recovery integration tests."""
import csv
import io
import json
import tempfile
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
from pathlib import Path

from admin_matrix import Instance, ROOT, call, form_post, token_of, USER, USER_PASSWORD


def main():
    checks = 0

    def check(name, condition):
        nonlocal checks
        assert condition, name
        checks += 1
        print("PASS " + name)

    with tempfile.TemporaryDirectory(dir=ROOT / '.local', prefix='activation-matrix-') as temporary:
        instance = Instance(Path(temporary), 'main', {'CHISPA_LIMIT_LOGIN_PER_MINUTE': '600'})
        instance.prepare()
        assert instance.cli('program-add', 'sample', 'Sample', 'Sample.exe').returncode == 0
        instance.start()
        try:
            admin, page = instance.session()
            csrf = token_of(page)
            check('admin activation section', "data-view='activation'" in page)
            anon = urllib.request.build_opener(urllib.request.ProxyHandler({}))

            def api(path, body=None, token=None, method='POST'):
                headers = {'Content-Type': 'application/json'}
                if token:
                    headers['Authorization'] = 'Bearer ' + token
                status, _, text = call(anon, method, instance.base + '/api/v1/' + path,
                                       headers=headers, body=json.dumps(body).encode() if body is not None else None)
                return status, json.loads(text) if text else None

            def generate(count=1, days=30, mode='all', beta=False):
                status, headers, content = form_post(admin, instance.base + '/admin/activation/generate', {
                    '__RequestVerificationToken': csrf, 'count': count, 'days': days,
                    'accessMode': mode, 'programIds': 'sample', 'betaAccess': '1' if beta else ''})
                assert status == 200, content
                assert headers.get('Cache-Control') == 'no-store'
                return list(csv.DictReader(io.StringIO(content)))

            def register(email, code, password='registration-password-123'):
                return api('register', {'email': email, 'password': password, 'code': code})

            def login(email, password='registration-password-123'):
                status, result = api('session', {'username': email, 'password': password})
                assert status == 200, result
                return result['token']

            def keys():
                return json.loads((instance.data / 'activation-keys.json').read_text())

            def state(row):
                return next(k for k in keys() if k['id'] == row['id'])

            def users():
                return json.loads((instance.data / 'users.json').read_text())

            status, _, _ = form_post(admin, instance.base + '/admin/activation/generate', {'count': 1, 'days': 30})
            check('generation requires CSRF', status == 400)
            status, _, text = form_post(anon, instance.base + '/admin/activation/generate', {'count': 1, 'days': 30})
            check('generation requires admin login', 'code,id,days' not in text)
            batch = generate(4)
            check('unique random codes', len({row['code'] for row in batch}) == 4)
            check('ledger stores hash only', all(row['code'] not in (instance.data / 'activation-keys.json').read_text() for row in batch))
            first = batch[0]
            check('email format required', register('plainuser', first['code'])[0] == 400)
            check('malformed and unsupported mailbox forms rejected', all(register(email, first['code'])[0] == 400
                  for email in ['.one@example.com', 'one..two@example.com', 'one/two@example.com', 'one@-example.com']))
            check('password length required', register('one@example.com', first['code'], 'short')[0] == 400)
            check('validation failure does not consume code', state(first)['state'] == 'unused')
            status, account = register(' One@Example.com ', first['code'].lower())
            check('registration normalizes email and code', status == 200 and account['username'] == 'one@example.com')
            expiry = datetime.fromisoformat(account['accountExpiresAt'])
            check('duration starts at redemption', abs((expiry - datetime.now(timezone.utc)).total_seconds() - 30 * 86400) < 10)
            check('code bound to registered account', state(first)['usedBy'] == 'one@example.com')
            check('replay refused', register('replay@example.com', first['code'])[0] == 400)
            check('duplicate email refused without consumption', register('ONE@EXAMPLE.COM', batch[1]['code'])[0] == 400 and state(batch[1])['state'] == 'unused')
            check('registered password is hash only', next(u for u in users() if u['username'] == 'one@example.com')['passwordCiphertext'] is None)
            token = login('ONE@example.com')
            check('admin-created username remains usable', bool(login(USER, USER_PASSWORD)))
            check('renewal requires session', api('activation/redeem', {'code': batch[1]['code']})[0] == 401)
            status, renewed = api('activation/redeem', {'code': batch[1]['code']}, token)
            check('active renewal preserves remaining duration', status == 200 and datetime.fromisoformat(renewed['accountExpiresAt']) == expiry + timedelta(days=30))
            check('renewal replay refused', api('activation/redeem', {'code': batch[1]['code']}, token)[0] == 400)
            # Two concurrent registrations must never both redeem the same code.
            with ThreadPoolExecutor(max_workers=8) as pool:
                responses = list(pool.map(lambda n: register(f'race{n}@example.com', batch[2]['code'])[0], range(8)))
            check('concurrent registration has exactly one winner', responses.count(200) == 1 and responses.count(400) == 7)
            check('concurrent failure leaves no orphan account', sum(u['username'].startswith('race') for u in users()) == 1)
            status, _, _ = form_post(admin, instance.base + '/admin/activation/' + batch[3]['id'] + '/revoke', {'__RequestVerificationToken': csrf})
            check('revoked key cannot register', status == 200 and register('revoked@example.com', batch[3]['code'])[0] == 400)
            # Codes can extend one program independently or add a Beta passport.
            scoped = generate(mode='single')[0]
            check('single code renews an all-program account', api('activation/redeem', {'code': scoped['code']}, token)[0] == 200 and state(scoped)['state'] == 'used')
            beta = generate(beta=True)[0]
            check('code can add Beta permission while renewing', api('activation/redeem', {'code': beta['code']}, token)[0] == 200 and state(beta)['state'] == 'used')
            scoped = generate(mode='single')[0]
            status, _ = register('scoped@example.com', scoped['code'])
            scoped_token = login('scoped@example.com')
            status, catalog = api('programs', token=scoped_token, method='GET')
            check('registered scope controls catalog', status == 200 and [p['id'] for p in catalog] == ['sample'])
            expired = generate()[0]
            ledger = keys()
            next(k for k in ledger if k['id'] == expired['id'])['redeemBefore'] = (datetime.now(timezone.utc) - timedelta(seconds=1)).isoformat()
            (instance.data / 'activation-keys.json').write_text(json.dumps(ledger))
            check('past redemption deadline refused', register('expired-code@example.com', expired['code'])[0] == 400 and state(expired)['state'] == 'unused')
            # Admin expires an account: login remains available, then renewal starts now.
            records = users()
            expired_user = next(u for u in records if u['username'] == 'one@example.com')
            expired_user['expiresAt'] = (datetime.now(timezone.utc) - timedelta(days=2)).isoformat()
            expired_user['programExpiries'] = {'sample': expired_user['expiresAt']}
            (instance.data / 'users.json').write_text(json.dumps(records))
            token = login('one@example.com')
            daily = generate(days=1)[0]
            status, renewed = api('activation/redeem', {'code': daily['code']}, token)
            check('expired account can renew from now', status == 200 and abs((datetime.fromisoformat(renewed['accountExpiresAt']) - datetime.now(timezone.utc)).total_seconds() - 86400) < 10)
            forever = generate(days=0)[0]
            status, renewed = api('activation/redeem', {'code': forever['code']}, token)
            check('permanent upgrade', status == 200 and renewed['accountExpiresAt'] is None)
            spare = generate()[0]
            check('permanent account does not waste a code', api('activation/redeem', {'code': spare['code']}, token)[0] == 400 and state(spare)['state'] == 'unused')
            # Replay a committed intent after a simulated process interruption between file writes.
            instance.stop()
            records, ledger = users(), keys()
            recovery_key = next(k for k in ledger if k['id'] == spare['id'])
            recovery_key.update(state='used', usedBy='recovered@example.com', usedAt=datetime.now(timezone.utc).isoformat())
            recovered = dict(next(u for u in records if u['username'] == 'one@example.com'))
            recovered['username'] = 'recovered@example.com'
            records.append(recovered)
            (instance.data / 'activation-transaction.json').write_text(json.dumps({'users': records, 'keys': ledger}))
            (instance.data / 'users.json').write_text(json.dumps(records))
            instance.start()
            check('journal recovery restores code consumption', state(spare)['state'] == 'used' and not (instance.data / 'activation-transaction.json').exists())
            check('journal recovery preserves registered account', bool(login('recovered@example.com')))
            check('recovered key cannot be reused', register('after-crash@example.com', spare['code'])[0] == 400)
            check('no raw activation codes in audit', all(row['code'] not in (instance.data / 'admin-audit.log').read_text() for row in batch))
            print(f'{checks} activation checks passed')
        finally:
            instance.stop()


if __name__ == '__main__':
    main()
