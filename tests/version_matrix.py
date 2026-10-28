"""Version recommendation, withdrawal and ban matrix (task 07).

  * the loader is offered the recommended version, not the highest one (rollback works)
  * "withdrawn" stops downloads but keeps installed copies launchable
  * "banned" stops downloads AND launches, including a lease that is already running
  * the launch-ticket / redeem / heartbeat path agrees with /launch and the download endpoint
  * a recommendation that points at a withdrawn or missing version cannot break the program
  * other programs are unaffected
  * the loader metadata endpoint exposes a sane current version and never a non-https link

State is driven through the admin HTTP forms, so the buttons an operator actually clicks are the
ones under test.

Run: python tests/version_matrix.py
"""

import http.cookiejar
import json
import os
from pathlib import Path
import re
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
DOTNET = ROOT / ".tooling/dotnet/dotnet.exe"
if not DOTNET.exists():
    DOTNET = "dotnet"
pointer = ROOT / ".local/current-server-build.txt"
SERVER = (ROOT / ".local/server-builds" / pointer.read_text().strip() / "Chispa.Server.dll"
          if pointer.exists() else ROOT / "server/bin/Release/net10.0/Chispa.Server.dll")
SERVER = Path(os.environ.get('CHISPA_TEST_SERVER', str(SERVER)))
SIGNER = ROOT / "tools/Chispa.Sign/bin/Release/net10.0/Chispa.Sign.dll"
ADMIN = "manager"
ADMIN_PASSWORD = "an excellent admin password"
USER = "versionuser"
USER_PASSWORD = "correct horse battery staple"
DEVICE = "a" * 32
RESULTS = []
DIRECT = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def check(name, passed, detail=""):
    RESULTS.append((name, bool(passed), detail))
    print(("PASS " if passed else "FAIL ") + name + ("" if passed else f"  [{str(detail)[:400]}]"))


def free_port():
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def call(opener, method, url, headers=None, body=None):
    request = urllib.request.Request(url, data=body, headers=headers or {}, method=method)
    try:
        with opener.open(request, timeout=20) as response:
            return response.status, response.headers, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.headers, error.read()


def json_of(payload):
    try:
        return json.loads(payload)
    except Exception:
        return payload.decode("utf-8", "replace")[:200]


def token_of(html):
    match = re.search(r"name='__RequestVerificationToken' value='([^']+)'", html.decode("utf-8", "replace"))
    return match.group(1) if match else ""


class Harness:
    def __init__(self, work: Path):
        self.work = work
        self.data = work / "data"
        self.data.mkdir(parents=True)
        self.env = os.environ.copy()
        self.env["CHISPA_SERVER_DATA"] = str(self.data)
        self.env["DOTNET_CLI_HOME"] = str(ROOT / ".tooling/home")
        self.env["TEMP"] = str(ROOT / ".tooling/temp")
        self.env["TMP"] = str(ROOT / ".tooling/temp")
        self.port = free_port()
        self.base = f"http://127.0.0.1:{self.port}"
        self.process = None

    def cli(self, *args, stdin=None):
        return subprocess.run([str(DOTNET), str(SERVER), *args], env=self.env, input=stdin,
                              capture_output=True, text=True, encoding="utf-8", errors="replace")

    def signer(self, *args):
        return subprocess.run([str(DOTNET), str(SIGNER), *args], env=self.env, capture_output=True,
                              text=True, encoding="utf-8", errors="replace")

    def prepare(self):
        keys = self.work / "signing"
        done = self.signer("keygen", "--out", str(keys), "--id", "version-key")
        if done.returncode:
            raise SystemExit(done.stdout + done.stderr)
        self.env["CHISPA_RELEASE_TRUST_KEYS"] = str(keys / "release-public.json")
        import shutil
        shutil.copy(keys / "release-public.json", self.data / "release-trust.json")
        for args, stdin in ((["admin-create", ADMIN], ADMIN_PASSWORD + "\n"),
                            (["user-add", USER], USER_PASSWORD + "\n"),
                            (["program-add", "chispa", "Chispa", "Wardogs.exe"], None),
                            (["program-add", "other", "Other", "Other.exe"], None)):
            done = self.cli(*args, stdin=stdin)
            if done.returncode:
                raise SystemExit(done.stdout + done.stderr)
        runtime = self.work / "runtime"
        runtime.mkdir()
        (runtime / "Wardogs.exe").write_bytes(b"version-wardogs")
        self.package = self.work / "package.zip"
        done = self.signer("pack", "--source", str(runtime), "--out", str(self.package),
                           "--executable", "Wardogs.exe")
        if done.returncode:
            raise SystemExit(done.stdout + done.stderr)
        self.keys = keys

    def publish(self, program, version):
        executable = "Wardogs.exe" if program == "chispa" else "Other.exe"
        runtime = self.work / ("runtime-" + program)
        runtime.mkdir(exist_ok=True)
        (runtime / executable).write_bytes(b"version-" + program.encode())
        package = self.work / f"package-{program}.zip"
        done = self.signer("pack", "--source", str(runtime), "--out", str(package), "--executable", executable)
        if done.returncode:
            raise SystemExit(done.stdout + done.stderr)
        signature = self.work / f"{program}-{version}.signature.json"
        done = self.signer("sign", "--package", str(package), "--program", program, "--version", version,
                           "--executable", executable,
                           "--key", str(self.keys / "release-private.pem"), "--key-id", "version-key",
                           "--out", str(signature))
        if done.returncode:
            raise SystemExit(done.stdout + done.stderr)
        done = self.cli("publish", program, version, str(package), str(signature))
        if done.returncode:
            raise SystemExit(done.stdout + done.stderr)

    def start(self):
        env = dict(self.env)
        env["CHISPA_SERVER_URL"] = self.base
        env["CHISPA_LAUNCHER_VERSION"] = "1.4.0"
        env["CHISPA_LAUNCHER_MINIMUM"] = "1.2.0"
        env["CHISPA_LAUNCHER_DOWNLOAD_URL"] = "https://example.com/chispa-launcher.exe"
        env["CHISPA_LAUNCHER_NOTES"] = "修复下载中断"
        self.process = subprocess.Popen([str(DOTNET), str(SERVER)], env=env, stdout=subprocess.DEVNULL,
                                        stderr=subprocess.DEVNULL)
        for _ in range(200):
            try:
                if DIRECT.open(self.base + "/health", timeout=2).status == 200:
                    return
            except OSError:
                pass
            time.sleep(0.1)
        raise SystemExit("server did not start")

    def stop(self):
        if self.process:
            self.process.terminate()
            try:
                self.process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                self.process.kill()
            self.process = None

    # ---- sessions -------------------------------------------------------------------------
    def admin(self):
        jar = http.cookiejar.CookieJar()
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}),
                                             urllib.request.HTTPCookieProcessor(jar))
        _, _, page = call(opener, "GET", self.base + "/admin/login")
        call(opener, "POST", self.base + "/admin/login", body=urllib.parse.urlencode({
            "__RequestVerificationToken": token_of(page), "username": ADMIN,
            "password": ADMIN_PASSWORD}).encode(),
            headers={"Content-Type": "application/x-www-form-urlencoded"})
        _, _, admin_page = call(opener, "GET", self.base + "/admin")
        return opener, token_of(admin_page)

    def admin_form(self, opener, token, path, fields):
        payload = {"__RequestVerificationToken": token}
        payload.update(fields)
        return call(opener, "POST", self.base + path, body=urllib.parse.urlencode(payload).encode(),
                    headers={"Content-Type": "application/x-www-form-urlencoded"})

    def user_token(self):
        status, _, body = call(DIRECT, "POST", self.base + "/api/v1/session",
                               headers={"Content-Type": "application/json"},
                               body=json.dumps({"username": USER, "password": USER_PASSWORD}).encode())
        return json.loads(body)["token"]

    def latest(self, token, program="chispa"):
        status, _, body = call(DIRECT, "GET", f"{self.base}/api/v1/programs/{program}/releases/latest",
                               headers={"Authorization": "Bearer " + token})
        return status, json_of(body)


def main():
    for required in (SERVER, SIGNER):
        if not required.exists():
            raise SystemExit(f"Missing {required}. Run ./build.ps1 first.")
    (ROOT / ".local").mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=ROOT / ".local", prefix="version-matrix-") as temporary:
        harness = Harness(Path(temporary))
        harness.prepare()
        harness.publish("chispa", "1.0.0")
        harness.publish("chispa", "1.1.0")
        harness.publish("other", "5.0.0")
        harness.start()
        try:
            token = harness.user_token()
            opener, admin_token = harness.admin()

            status, latest = harness.latest(token)
            check("默认推荐最高版本", status == 200 and latest["version"] == "1.1.0", latest)

            # ---- rollback through the admin form -------------------------------------------
            status, _, _ = harness.admin_form(opener, admin_token, "/admin/programs/chispa/recommend",
                                              {"version": "1.0.0"})
            status, latest = harness.latest(token)
            check("后台把推荐版本切回旧版本", status == 200 and latest["version"] == "1.0.0", latest)
            status, _, package = call(DIRECT, "GET",
                                      f"{harness.base}/api/v1/programs/chispa/releases/1.0.0/package",
                                      headers={"Authorization": "Bearer " + token})
            check("推荐版本可以下载", status == 200 and len(package) > 0, status)

            # ---- withdrawn ------------------------------------------------------------------
            status, _, _ = harness.admin_form(opener, admin_token,
                                              "/admin/programs/chispa/releases/1.1.0/status",
                                              {"status": "withdrawn"})
            status, _, body = call(DIRECT, "GET",
                                   f"{harness.base}/api/v1/programs/chispa/releases/1.1.0/package",
                                   headers={"Authorization": "Bearer " + token})
            check("撤回的版本不能下载", status == 404 and b"release_withdrawn" in body, f"{status} {body[:80]}")
            status, _, body = call(DIRECT, "POST",
                                   f"{harness.base}/api/v1/programs/chispa/releases/1.1.0/launch",
                                   headers={"Authorization": "Bearer " + token})
            check("撤回的版本仍可启动（已安装的用户不受影响）", status == 200, f"{status} {body[:120]}")
            status, latest = harness.latest(token)
            check("撤回后推荐版本仍是显式指定的那个", latest["version"] == "1.0.0", latest)

            # ---- banned --------------------------------------------------------------------
            status, _, _ = harness.admin_form(opener, admin_token,
                                              "/admin/programs/chispa/releases/1.1.0/status",
                                              {"status": "banned"})
            status, _, body = call(DIRECT, "POST",
                                   f"{harness.base}/api/v1/programs/chispa/releases/1.1.0/launch",
                                   headers={"Authorization": "Bearer " + token})
            check("禁止的版本不能启动", status == 403 and b"release_banned" in body, f"{status} {body[:80]}")
            status, _, body = call(DIRECT, "GET",
                                   f"{harness.base}/api/v1/programs/chispa/releases/1.1.0/package",
                                   headers={"Authorization": "Bearer " + token})
            check("禁止的版本也不能下载", status == 404, status)
            status, _, body = call(DIRECT, "POST",
                                   f"{harness.base}/api/v1/programs/chispa/releases/1.1.0/launch-ticket",
                                   headers={"Authorization": "Bearer " + token})
            check("禁止的版本不签发启动票据", status == 403 and b"release_banned" in body, f"{status} {body[:80]}")

            # ---- the task-02 path agrees ---------------------------------------------------
            status, _, body = call(DIRECT, "POST", f"{harness.base}/api/v1/programs/other/releases/5.0.0/launch-ticket",
                                   headers={"Authorization": "Bearer " + token})
            ticket = json.loads(body)["ticket"] if status == 200 else None
            check("其他程序照常签发票据（互不影响）", status == 200 and ticket, status)
            status, _, body = call(DIRECT, "POST", f"{harness.base}/api/v1/launch/redeem",
                                   headers={"Content-Type": "application/json"},
                                   body=json.dumps({"ticket": ticket, "programId": "other", "version": "5.0.0",
                                                    "device": DEVICE}).encode())
            lease = json.loads(body).get("lease") if status == 200 else None
            check("其他程序的租约正常", status == 200 and lease, status)

            status, _, body = call(DIRECT, "POST", f"{harness.base}/api/v1/programs/chispa/releases/1.0.0/launch-ticket",
                                   headers={"Authorization": "Bearer " + token})
            banned_ticket = json.loads(body)["ticket"]
            status, _, _ = harness.admin_form(opener, admin_token,
                                              "/admin/programs/chispa/releases/1.0.0/status",
                                              {"status": "banned"})
            status, _, body = call(DIRECT, "POST", f"{harness.base}/api/v1/launch/redeem",
                                   headers={"Content-Type": "application/json"},
                                   body=json.dumps({"ticket": banned_ticket, "programId": "chispa", "version": "1.0.0",
                                                    "device": DEVICE}).encode())
            check("票据签发后被禁止的版本，兑换时被拒绝", status == 403 and b"release_banned" in body,
                  f"{status} {body[:100]}")

            # a lease taken while the version was active, then the version is banned
            status, _, _ = harness.admin_form(opener, admin_token,
                                              "/admin/programs/chispa/releases/1.0.0/status",
                                              {"status": "active"})
            status, _, body = call(DIRECT, "POST", f"{harness.base}/api/v1/programs/chispa/releases/1.0.0/launch-ticket",
                                   headers={"Authorization": "Bearer " + token})
            active_ticket = json.loads(body)["ticket"]
            status, _, body = call(DIRECT, "POST", f"{harness.base}/api/v1/launch/redeem",
                                   headers={"Content-Type": "application/json"},
                                   body=json.dumps({"ticket": active_ticket, "programId": "chispa", "version": "1.0.0",
                                                    "device": DEVICE}).encode())
            chispa_lease = json.loads(body).get("lease") if status == 200 else None
            check("可用版本的租约正常", status == 200 and chispa_lease, status)
            harness.admin_form(opener, admin_token, "/admin/programs/chispa/releases/1.0.0/status",
                               {"status": "banned"})
            status, _, body = call(DIRECT, "POST", f"{harness.base}/api/v1/launch/heartbeat",
                                   headers={"Authorization": "Bearer " + chispa_lease, "X-Chispa-Device": DEVICE})
            check("运行中的程序在版本被禁止后于下一个心跳被停", status == 403 and b"release_banned" in body,
                  f"{status} {body[:100]}")

            # ---- recommendation rules ------------------------------------------------------
            harness.admin_form(opener, admin_token, "/admin/programs/chispa/releases/1.0.0/status",
                               {"status": "active"})
            status, _, body = harness.admin_form(opener, admin_token, "/admin/programs/chispa/recommend",
                                                 {"version": "1.1.0"})
            check("禁止的版本不能设为推荐", "不能设为推荐" in body.decode("utf-8", "replace"), body[:160])
            status, _, body = harness.admin_form(opener, admin_token, "/admin/programs/chispa/recommend",
                                                 {"version": "9.9.9"})
            check("不存在的版本不能设为推荐", "不存在" in body.decode("utf-8", "replace"), body[:160])
            harness.admin_form(opener, admin_token, "/admin/programs/chispa/recommend", {"version": ""})
            status, latest = harness.latest(token)
            check("清除推荐后回落到最高可用版本", status == 200 and latest["version"] == "1.0.0", latest)

            # dangling recommendation: point at 1.1.0 then ban it; latest must fall back, not break
            harness.admin_form(opener, admin_token, "/admin/programs/chispa/releases/1.1.0/status",
                               {"status": "active"})
            harness.admin_form(opener, admin_token, "/admin/programs/chispa/recommend", {"version": "1.1.0"})
            harness.admin_form(opener, admin_token, "/admin/programs/chispa/releases/1.1.0/status",
                               {"status": "banned"})
            status, latest = harness.latest(token)
            check("推荐版本后来被禁止时自动回落，不把程序变成不可用",
                  status == 200 and latest["version"] == "1.0.0", latest)

            # ---- other program untouched ---------------------------------------------------
            status, latest_other = harness.latest(token, "other")
            check("其他程序的最新版本不受影响", status == 200 and latest_other["version"] == "5.0.0", latest_other)

            # Delete only a non-current release; test both the real button and HTTP guardrails.
            delete_path = '/admin/programs/chispa/releases/1.1.0/delete'
            status, _, _ = call(opener, 'POST', harness.base + delete_path)
            check('删除版本需要CSRF', status == 400)
            status, _, _ = call(DIRECT, 'POST', harness.base + delete_path)
            target = harness.data / 'programs/chispa/releases/1.1.0'
            check('匿名不能删除版本', target.exists())
            status, _, page = harness.admin_form(opener, admin_token,
                '/admin/programs/chispa/releases/1.0.0/delete', {})
            check('自动推荐版本不能删除', '不能删除当前推荐版本'.encode() in page and
                  (harness.data / 'programs/chispa/releases/1.0.0/package.zip').exists())
            # 1.1.0 is still explicitly recommended (now banned), so clear that pointer first.
            status, _, page = harness.admin_form(opener, admin_token, delete_path, {})
            check('显式推荐指针仍受保护', '不能删除当前推荐版本'.encode() in page and target.exists())
            harness.admin_form(opener, admin_token, '/admin/programs/chispa/recommend', {'version': ''})
            status, _, page = call(opener, 'GET', harness.base + '/admin')
            check('旧版本显示删除按钮', delete_path.encode() in page and '删除旧版本'.encode() in page)
            # Make the old release launchable and lease it before removal.
            harness.admin_form(opener, admin_token, '/admin/programs/chispa/recommend', {'version': '1.0.0'})
            harness.admin_form(opener, admin_token, '/admin/programs/chispa/releases/1.1.0/status', {'status': 'active'})
            status, _, raw = call(DIRECT, 'POST', harness.base + '/api/v1/programs/chispa/releases/1.1.0/launch-ticket',
                                  headers={'Authorization': 'Bearer ' + token})
            old_ticket = json.loads(raw)['ticket']
            status, _, raw = call(DIRECT, 'POST', harness.base + '/api/v1/launch/redeem',
                                  headers={'Content-Type': 'application/json'},
                                  body=json.dumps({'ticket': old_ticket, 'programId': 'chispa', 'version': '1.1.0', 'device': DEVICE}).encode())
            old_lease = json.loads(raw)['lease']
            with (target / 'package.zip').open('rb'):
                status, _, page = harness.admin_form(opener, admin_token, delete_path, {})
                check('占用中的版本不能删除且保留全部文件', '版本文件正在使用'.encode() in page and
                      all((target / name).exists() for name in ['package.zip', 'signature.json', 'manifest.json']))
            status, _, _ = harness.admin_form(opener, admin_token, delete_path, {})
            check('删除旧版本同时移除包签名清单', status == 200 and not target.exists())
            status, _, _ = call(DIRECT, 'GET', harness.base + '/api/v1/programs/chispa/releases/1.1.0/package',
                                 headers={'Authorization': 'Bearer ' + token})
            check('删除后不能下载', status == 404)
            status, _, _ = call(DIRECT, 'POST', harness.base + '/api/v1/programs/chispa/releases/1.1.0/launch',
                                 headers={'Authorization': 'Bearer ' + token})
            check('删除后不能启动', status == 404)
            status, _, raw = call(DIRECT, 'POST', harness.base + '/api/v1/launch/heartbeat',
                                 headers={'Authorization': 'Bearer ' + old_lease, 'X-Chispa-Device': DEVICE})
            check('删除后运行租约在下一心跳失效', status == 404 and json.loads(raw)['code'] == 'program_unavailable')
            check('删除后当前版本仍正常', harness.latest(token)[1]['version'] == '1.0.0')
            check('删除不会影响其他程序', harness.latest(token, 'other')[1]['version'] == '5.0.0')
            audit = (harness.data / 'admin-audit.log').read_text(encoding='utf-8')
            check('删除版本写入审计', 'release-delete' in audit and 'chispa/1.1.0' in audit)

            # ---- launcher metadata ---------------------------------------------------------
            status, _, body = call(DIRECT, "GET", f"{harness.base}/api/v1/launcher/latest")
            metadata = json_of(body)
            check("加载器版本信息可读", status == 200 and metadata.get("version") == "1.4.0", metadata)
            check("下载入口是配置项且为 https", metadata.get("downloadUrl", "").startswith("https://"), metadata)
            check("加载器能力以能力标识声明，而不是版本号",
                  "launch-auth/1" in (metadata.get("capabilities") or []), metadata.get("capabilities"))
        finally:
            harness.stop()

        # bad download address must never reach the loader as a link
        bad = Harness(Path(temporary) / "badurl")
        bad.prepare()
        env = dict(bad.env)
        env["CHISPA_SERVER_URL"] = bad.base
        env["CHISPA_LAUNCHER_VERSION"] = "9.9.9"
        env["CHISPA_LAUNCHER_DOWNLOAD_URL"] = "C:\\Windows\\System32\\calc.exe"
        process = subprocess.Popen([str(DOTNET), str(SERVER)], env=env, stdout=subprocess.DEVNULL,
                                   stderr=subprocess.DEVNULL)
        try:
            for _ in range(200):
                try:
                    if DIRECT.open(bad.base + "/health", timeout=2).status == 200:
                        break
                except OSError:
                    pass
                time.sleep(0.1)
            status, _, body = call(DIRECT, "GET", f"{bad.base}/api/v1/launcher/latest")
            metadata = json_of(body)
            check("非 https 的下载地址被清空（不会被当成可执行入口）",
                  status == 200 and metadata.get("downloadUrl") == "", metadata)
        finally:
            process.terminate()
            process.wait(timeout=10)

    failures = [name for name, passed, _ in RESULTS if not passed]
    print()
    print(f"{len(RESULTS) - len(failures)}/{len(RESULTS)} checks passed")
    if failures:
        print("FAILED: " + ", ".join(failures))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
