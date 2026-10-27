"""Publish explicit local artifacts as an atomic, verified GitHub Release.

Credentials come from Git Credential Manager and are never printed or written to disk.
Create/update a draft, upload and verify every asset, then make the release visible.
Existing assets are retained only if their size and SHA-256 match; never overwrite a release.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import urllib.error
import urllib.parse
import urllib.request


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--repo', required=True)
    parser.add_argument('--tag', required=True)
    parser.add_argument('--target', required=True)
    parser.add_argument('--name', required=True)
    parser.add_argument('--notes-file', type=Path, required=True)
    parser.add_argument('--asset', type=Path, action='append', required=True)
    args = parser.parse_args()
    if args.repo not in ('Guo-Yinchen/loader', 'Guo-Yinchen/Wardogs'):
        raise SystemExit('Repository is outside this workspace release configuration.')
    credentials = subprocess.run(['git', '-c', 'credential.interactive=never', 'credential', 'fill'],
                                 input=b'protocol=https\nhost=github.com\n\n', capture_output=True, check=True).stdout
    token = next(line[9:].decode() for line in credentials.splitlines() if line.startswith(b'password='))

    def request(method, path, payload=None, data=None, content_type='application/json'):
        url = path if path.startswith('https://') else 'https://api.github.com' + path
        if urllib.parse.urlsplit(url).hostname not in ('api.github.com', 'uploads.github.com'):
            raise RuntimeError('Unexpected GitHub API host.')
        if payload is not None:
            data = json.dumps(payload).encode()
        headers = {'Authorization': 'Bearer ' + token, 'Accept': 'application/vnd.github+json',
                   'User-Agent': 'Chispa-Release-Publisher', 'Content-Type': content_type,
                   'X-GitHub-Api-Version': '2022-11-28'}
        with urllib.request.urlopen(urllib.request.Request(url, headers=headers, data=data, method=method), timeout=300) as response:
            return json.load(response)

    asset_info = []
    for path in args.asset:
        path = path.resolve(strict=True)
        asset_info.append((path, path.stat().st_size, hashlib.sha256(path.read_bytes()).hexdigest()))
    if len({path.name for path, _, _ in asset_info}) != len(asset_info):
        raise SystemExit('Duplicate release asset names.')
    base = '/repos/' + args.repo
    try:
        release = request('GET', base + '/releases/tags/' + urllib.parse.quote(args.tag, safe=''))
    except urllib.error.HTTPError as error:
        if error.code != 404:
            raise
        # Include drafts too, allowing an interrupted upload to resume safely.
        releases = request('GET', base + '/releases?per_page=100')
        release = next((item for item in releases if item['tag_name'] == args.tag), None)
    if release is None:
        release = request('POST', base + '/releases', {'tag_name': args.tag, 'target_commitish': args.target,
            'name': args.name, 'body': args.notes_file.read_text(encoding='utf-8-sig'), 'draft': True})
    existing = {asset['name']: asset for asset in request('GET', base + f"/releases/{release['id']}/assets?per_page=100")}
    for path, size, digest in asset_info:
        asset = existing.get(path.name)
        if asset is None:
            if not release['draft']:
                raise RuntimeError('Refusing to add missing assets to an already published release.')
            print('Uploading ' + path.name + f' ({size / 1048576:.1f} MiB)', flush=True)
            upload_url = release['upload_url'].split('{')[0] + '?name=' + urllib.parse.quote(path.name)
            asset = request('POST', upload_url, data=path.read_bytes(), content_type='application/octet-stream')
        if asset['size'] != size or asset.get('digest') != 'sha256:' + digest:
            raise RuntimeError('GitHub asset size/digest mismatch: ' + path.name)
        print('Verified ' + path.name + ' sha256:' + digest, flush=True)
    if release['draft']:
        release = request('PATCH', base + f"/releases/{release['id']}", {'draft': False})
    print('Published ' + release['html_url'], flush=True)


if __name__ == '__main__':
    main()
