"""Independent acceptance regressions. Uses disposable data, fake EXEs and loopback only."""
import argparse
import hashlib
import http.server
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import threading
import urllib.request

import version_matrix as fixtures

ROOT = Path(__file__).resolve().parents[1]
RESULTS = []


def check(name, passed):
    RESULTS.append(bool(passed))
    print(('PASS ' if passed else 'FAIL ') + name, flush=True)


def restore(backup, target, *extra):
    return subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                           str(ROOT / 'tools/restore-backup.ps1'), '-Backup', str(backup),
                           '-Target', str(target), '-SkipVerify', *extra],
                          capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=30)


def backup_paths(work):
    backup = work / 'backup'
    (backup / 'data').mkdir(parents=True)
    payload = b'unsafe replacement'
    (backup / 'escape.txt').write_bytes(payload)
    sentinel = work / 'escape.txt'
    sentinel.write_bytes(b'keep me')
    entry = {'path': '../escape.txt', 'size': len(payload), 'sha256': hashlib.sha256(payload).hexdigest()}
    manifest = {'schema': 'chispa-backup-v1', 'files': 1, 'filesIndex': [entry], 'extras': []}
    manifest_path = backup / 'backup-manifest.json'
    manifest_path.write_text(json.dumps(manifest))
    done = restore(backup, work / 'restore-bad-path')
    check('restore rejects traversal before writing outside target', done.returncode != 0 and sentinel.read_bytes() == b'keep me')

    (backup / 'data/users.json').write_bytes(b'[]')
    entry.update(path='users.json', size=2, sha256=hashlib.sha256(b'[]').hexdigest())
    (backup / 'extra/signing').mkdir(parents=True)
    (backup / 'extra/signing/key.pem').write_bytes(b'corrupt')
    manifest['extras'] = [{'name': 'signing', 'files': [{'path': 'key.pem', 'size': 7, 'sha256': '0' * 64}]}]
    manifest_path.write_text(json.dumps(manifest))
    target = work / 'restore-bad-extra'
    done = restore(backup, target)
    check('all extra files are verified before any restore output is created', done.returncode != 0 and not target.exists())
    manifest['extras'] = []
    manifest_path.write_text(json.dumps(manifest))
    good_target = work / 'restore-good'
    done = restore(backup, good_target)
    check('valid backup still restores with the new path checks', done.returncode == 0 and (good_target / 'users.json').read_bytes() == b'[]')



def server_boundaries(h):
    session = h.user_token()
    signature_path = h.data / 'programs/chispa/releases/1.0.0/signature.json'
    original_signature = signature_path.read_bytes()
    signature_path.unlink()
    launch_path = h.base + '/api/v1/programs/chispa/releases/1.0.0/launch'
    status, _, body = fixtures.call(fixtures.DIRECT, 'POST', launch_path,
                                    headers={'Authorization': 'Bearer ' + session}, body=b'')
    assert status == 200 and json.loads(body).get('signature') is None
    signature_path.write_bytes(original_signature)
    status, _, body = fixtures.call(fixtures.DIRECT, 'POST', launch_path,
                                    headers={'Authorization': 'Bearer ' + session}, body=b'')
    check('attaching a signature updates an already cached launch manifest', status == 200 and bool(json.loads(body).get('signature')))

    status, _, body = fixtures.call(fixtures.DIRECT, 'POST', h.base + '/api/v1/programs/chispa/releases/1.0.0/launch-ticket',
                                   headers={'Authorization': 'Bearer ' + session}, body=b'')
    assert status == 200
    ticket = json.loads(body)['ticket']
    fixtures.call(fixtures.DIRECT, 'DELETE', h.base + '/api/v1/session', headers={'Authorization': 'Bearer ' + session})
    status, _, _ = fixtures.call(fixtures.DIRECT, 'POST', h.base + '/api/v1/launch/redeem',
                                headers={'Content-Type': 'application/json'}, body=json.dumps({
                                    'ticket': ticket, 'programId': 'chispa', 'version': '1.0.0', 'device': 'a' * 32}).encode())
    check('logout revokes a ticket that has not yet been redeemed', status != 200)

    first, csrf = h.admin()
    status, _, page = h.admin_form(first, csrf, f'/admin/users/{fixtures.USER}/password/stepup',
                                  {'adminPassword': fixtures.ADMIN_PASSWORD})
    assert status == 200
    grant = re.search(rb"name='grant' value='([^']+)'", page).group(1).decode()
    other, other_csrf = h.admin()
    status, _, _ = h.admin_form(other, other_csrf, f'/admin/users/{fixtures.USER}/password/view', {'grant': grant})
    check('password grant cannot be used by another login of the same administrator', status == 403)
    status, _, _ = h.admin_form(first, csrf, '/admin/users/different/password/view', {'grant': grant})
    check('password grant is bound to the requested user', status == 403)

    from concurrent.futures import ThreadPoolExecutor
    def view(_):
        return h.admin_form(first, csrf, f'/admin/users/{fixtures.USER}/password/view', {'grant': grant})[0]
    with ThreadPoolExecutor(max_workers=8) as pool:
        statuses = list(pool.map(view, range(8)))
    check('concurrent password views cannot exceed the grant use count', statuses.count(200) == 3)

    status, _, _ = fixtures.call(fixtures.DIRECT, 'POST', h.base + '/api/v1/launch/redeem',
                                headers={'Content-Type': 'application/json'}, body=b' ' * (600 * 1024))
    check('unauthenticated redeem body has a small size limit', status == 413)


def install_resume(h, smoke):
    class Proxy(http.server.BaseHTTPRequestHandler):
        dropping = True
        bad_range = False
        ranges = []

        def log_message(self, *_): pass

        def forward(self):
            headers = {k: v for k, v in self.headers.items() if k.lower() not in ('host', 'connection', 'transfer-encoding', 'content-length')}
            length = int(self.headers.get('Content-Length', 0))
            if self.headers.get('Transfer-Encoding', '').lower() == 'chunked':
                chunks = []
                while True:
                    size = int(self.rfile.readline().strip().split(b';')[0], 16)
                    if size == 0:
                        self.rfile.readline()
                        break
                    chunks.append(self.rfile.read(size))
                    self.rfile.read(2)
                body = b''.join(chunks)
            else:
                body = self.rfile.read(length) if length else None
            status, returned, payload = fixtures.call(fixtures.DIRECT, self.command, h.base + self.path, headers=headers, body=body)
            self.send_response(status)
            for key, value in returned.items():
                if key.lower() not in ('transfer-encoding', 'connection', 'content-length'):
                    if self.bad_range and key.lower() == 'content-range': value = 'bytes 0-0/1'
                    self.send_header(key, value)
            self.send_header('Content-Length', str(len(payload)))
            self.send_header('Connection', 'close')
            self.end_headers()
            if self.path.endswith('/package'):
                self.ranges.append(self.headers.get('Range'))
                if self.dropping: payload = payload[:10]
            self.wfile.write(payload)
            self.wfile.flush()
            self.close_connection = True

        do_GET = forward
        do_POST = forward

    proxy = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Proxy)
    thread = threading.Thread(target=proxy.serve_forever, daemon=True)
    thread.start()
    env = dict(h.env)
    env['CHISPA_TRUST_KEYS'] = str(h.keys / 'release-public.json')
    install = h.work / 'installed'
    env['CHISPA_INSTALL_ROOT'] = str(install)
    args = [str(fixtures.DOTNET), str(smoke), '--install-check', f'http://127.0.0.1:{proxy.server_port}',
            'chispa', '1.0.0', fixtures.USER, fixtures.USER_PASSWORD]
    try:
        first = subprocess.run(args, env=env, capture_output=True, timeout=45)
        partials = list(install.glob('*.partial'))
        check('exhausted retries retain one resumable partial', first.returncode != 0 and len(partials) == 1)
        if len(partials) != 1: print('DETAIL first:',first.returncode,first.stdout.decode('utf-8','replace'),first.stderr.decode('utf-8','replace'),flush=True)
        checkpoint = partials[0].stat().st_size if len(partials) == 1 else -1
        Proxy.dropping = False
        Proxy.bad_range = True
        Proxy.ranges.clear()
        invalid_range = subprocess.run(args, env=env, capture_output=True, timeout=45)
        check('a false Content-Range fails immediately without appending or retrying',
              invalid_range.returncode != 0 and len(Proxy.ranges) == 1 and partials[0].stat().st_size == checkpoint)
        Proxy.bad_range = False
        Proxy.ranges.clear()
        second = subprocess.run(args, env=env, capture_output=True, timeout=45)
        if second.returncode: print('DETAIL second:', second.returncode,second.stdout.decode('utf-8','replace'),second.stderr.decode('utf-8','replace'),flush=True)
        check('a separate installer process resumes the same signed package',
              second.returncode == 0 and Proxy.ranges and Proxy.ranges[0] == f'bytes={checkpoint}-')
        check('successful installation leaves no abandoned partial files', not list(install.glob('*.partial')))
        _, manifest = h.latest(h.user_token())
        pointer = (install / 'current.json').read_bytes()
        bad_partial = install / ('download-1.0.0-' + manifest['sha256'].lower() + '.zip.partial')
        bad_partial.write_bytes(b'0' * manifest['size'])
        rejected = subprocess.run(args, env=env, capture_output=True, timeout=45)
        check('corrupt complete partial is removed without changing the active installation',
              rejected.returncode != 0 and not bad_partial.exists() and (install / 'current.json').read_bytes() == pointer)

    finally:
        proxy.shutdown()
        proxy.server_close()
        thread.join(timeout=5)


def main():
    # Windows PowerShell must discover its own modules, not inherit PowerShell 7's paths.
    os.environ.pop('PSModulePath', None)
    parser = argparse.ArgumentParser()
    parser.add_argument('--build-root', type=Path, default=ROOT / '.local/acceptance')
    args = parser.parse_args()
    fixtures.SERVER = args.build_root / 'server/Chispa.Server.dll'
    fixtures.SIGNER = args.build_root / 'signer/Chispa.Sign.dll'
    with tempfile.TemporaryDirectory(dir=ROOT / '.local', prefix='review-regression-') as temp:
        work = Path(temp)
        backup_paths(work)
        h = fixtures.Harness(work / 'service')
        h.prepare()
        h.publish('chispa', '1.0.0')
        h.start()
        try:
            server_boundaries(h)
            install_resume(h, args.build_root / 'tests/Chispa.Smoke.dll')
        finally:
            h.stop()
    print(f'{sum(RESULTS)}/{len(RESULTS)} review regressions passed')
    return 0 if all(RESULTS) else 1


if __name__ == '__main__':
    raise SystemExit(main())
