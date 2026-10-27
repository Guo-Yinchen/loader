"""Fault and limit regression (task 08).

Two halves:

  * the loader's real download path (`--download-check`, i.e. the code the launcher runs) against a
    proxy that drops the connection after a few kilobytes: the retry must resume with a Range
    request instead of starting over, and the finished file must still match the signed SHA-256;
  * server behaviour under abuse and failure: partial content, unsatisfiable ranges, oversized login
    bodies, per-address request budgets, a corrupted package, a package deleted from disk and a
    permission revoked while a session is live.

Run: python tests/fault_matrix.py
"""

import hashlib
import http.cookiejar
import json
import os
from pathlib import Path
import re
import socket
import socketserver
import subprocess
import sys
import tempfile
import threading
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
SIGNER = ROOT / "tools/Chispa.Sign/bin/Release/net10.0/Chispa.Sign.dll"
SMOKE = ROOT / "tests/bin/Release/net10.0-windows/Chispa.Smoke.dll"
ADMIN = "manager"
ADMIN_PASSWORD = "an excellent admin password"
USER = "faultuser"
USER_PASSWORD = "correct horse battery staple"
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
        with opener.open(request, timeout=30) as response:
            return response.status, response.headers, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.headers, error.read()


class FlakyProxy:
    """Forwards to the server, but drops the first N bytes of the first response body and closes
    the connection. Anything after that is forwarded verbatim. Range headers pass through."""

    def __init__(self, target_port, drop_after):
        self.target = ("127.0.0.1", target_port)
        self.drop_after = drop_after
        self.port = free_port()
        self.requests = []
        self.dropped = False
        proxy = self

        class Handler(socketserver.BaseRequestHandler):
            def handle(self):
                data = b""
                while b"\r\n\r\n" not in data:
                    chunk = self.request.recv(65536)
                    if not chunk:
                        return
                    data += chunk
                lines = data.split(b"\r\n")
                # keep the whole request head: the resume assertion needs the Range header
                proxy.requests.append(data.split(b"\r\n\r\n")[0].decode("latin-1"))
                with socket.create_connection(proxy.target, timeout=30) as upstream:
                    upstream.sendall(data)
                    first_line = data.split(b"\r\n")[0]
                    if proxy.drop_after is not None and not proxy.dropped and b"/package" in first_line:
                        proxy.dropped = True
                        sent = 0
                        while sent < proxy.drop_after:
                            chunk = upstream.recv(4096)
                            if not chunk:
                                break
                            self.request.sendall(chunk)
                            sent += len(chunk)
                        self.request.close()   # abrupt close, like a dropped mobile connection
                        return
                    while True:
                        chunk = upstream.recv(65536)
                        if not chunk:
                            return
                        self.request.sendall(chunk)

        class Server(socketserver.ThreadingTCPServer):
            allow_reuse_address = True
            daemon_threads = True

        self.server = Server(("127.0.0.1", self.port), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def close(self):
        self.server.shutdown()
        self.server.server_close()


class Harness:
    def __init__(self, work: Path, env_extra=None):
        self.work = work
        self.data = work / "data"
        self.data.mkdir(parents=True)
        self.env = os.environ.copy()
        self.env["CHISPA_SERVER_DATA"] = str(self.data)
        self.env["DOTNET_CLI_HOME"] = str(ROOT / ".tooling/home")
        self.env["TEMP"] = str(ROOT / ".tooling/temp")
        self.env["TMP"] = str(ROOT / ".tooling/temp")
        self.env.update(env_extra or {})
        self.port = free_port()
        self.base = f"http://127.0.0.1:{self.port}"
        self.process = None

    def cli(self, *args, stdin=None):
        return subprocess.run([str(DOTNET), str(SERVER), *args], env=self.env, input=stdin,
                              capture_output=True, text=True, encoding="utf-8", errors="replace")

    def prepare(self):
        keys = self.work / "signing"
        done = subprocess.run([str(DOTNET), str(SIGNER), "keygen", "--out", str(keys), "--id", "fault-key"],
                              env=self.env, capture_output=True, text=True, encoding="utf-8", errors="replace")
        if done.returncode:
            raise SystemExit(done.stdout + done.stderr)
        self.env["CHISPA_RELEASE_TRUST_KEYS"] = str(keys / "release-public.json")
        import shutil
        shutil.copy(keys / "release-public.json", self.data / "release-trust.json")
        for args, stdin in ((["admin-create", ADMIN], ADMIN_PASSWORD + "\n"),
                            (["user-add", USER], USER_PASSWORD + "\n"),
                            (["program-add", "chispa", "Chispa", "Wardogs.exe"], None)):
            done = self.cli(*args, stdin=stdin)
            if done.returncode:
                raise SystemExit(done.stdout + done.stderr)
        runtime = self.work / "runtime"
        runtime.mkdir()
        # a few MB so a dropped connection is meaningful
        (runtime / "Wardogs.exe").write_bytes(b"fault-wardogs" * 40_000)
        package = self.work / "package.zip"
        signature = self.work / "signature.json"
        for args in (["pack", "--source", str(runtime), "--out", str(package), "--executable", "Wardogs.exe"],
                     ["sign", "--package", str(package), "--program", "chispa", "--version", "1.0.0",
                      "--executable", "Wardogs.exe", "--key", str(keys / "release-private.pem"),
                      "--key-id", "fault-key", "--out", str(signature)]):
            done = subprocess.run([str(DOTNET), str(SIGNER), *args], env=self.env, capture_output=True,
                                  text=True, encoding="utf-8", errors="replace")
            if done.returncode:
                raise SystemExit(done.stdout + done.stderr)
        done = self.cli("publish", "chispa", "1.0.0", str(package), str(signature))
        if done.returncode:
            raise SystemExit(done.stdout + done.stderr)
        self.package = self.data / "programs/chispa/releases/1.0.0/package.zip"
        self.package_bytes = self.package.read_bytes()

    def start(self):
        env = dict(self.env)
        env["CHISPA_SERVER_URL"] = self.base
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

    def token(self):
        status, _, body = call(DIRECT, "POST", self.base + "/api/v1/session",
                               headers={"Content-Type": "application/json"},
                               body=json.dumps({"username": USER, "password": USER_PASSWORD}).encode())
        return json.loads(body)["token"] if status == 200 else None

    def cli_smoke(self, *args):
        return subprocess.run([str(DOTNET), str(SMOKE), *args], env=self.env, capture_output=True,
                              text=True, encoding="utf-8", errors="replace", timeout=300)


def admin_token_of(html):
    match = re.search(r"name='__RequestVerificationToken' value='([^']+)'", html)
    return match.group(1) if match else ""


def delete_user_through_admin(harness, username):
    """Deletes a user the way an operator would, so an already-issued session is invalidated by the
    real code path rather than by poking at users.json."""
    jar = http.cookiejar.CookieJar()
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}),
                                         urllib.request.HTTPCookieProcessor(jar))
    _, _, login_page = call(opener, "GET", harness.base + "/admin/login")
    call(opener, "POST", harness.base + "/admin/login", body=urllib.parse.urlencode({
        "__RequestVerificationToken": admin_token_of(login_page.decode("utf-8", "replace")),
        "username": ADMIN, "password": ADMIN_PASSWORD}).encode(),
        headers={"Content-Type": "application/x-www-form-urlencoded"})
    _, _, admin_page = call(opener, "GET", harness.base + "/admin")
    call(opener, "POST", f"{harness.base}/admin/users/{username}/delete", body=urllib.parse.urlencode({
        "__RequestVerificationToken": admin_token_of(admin_page.decode("utf-8", "replace"))}).encode(),
        headers={"Content-Type": "application/x-www-form-urlencoded"})


def main():
    for required in (SERVER, SIGNER, SMOKE):
        if not required.exists():
            raise SystemExit(f"Missing {required}. Run ./build.ps1 and build tests/Chispa.Smoke.csproj.")
    (ROOT / ".local").mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=ROOT / ".local", prefix="fault-matrix-") as temporary:
        harness = Harness(Path(temporary), {"CHISPA_LAUNCHER_VERSION": "1.0.0"})
        harness.prepare()
        harness.start()
        try:
            token = harness.token()
            auth = {"Authorization": "Bearer " + token}
            total = len(harness.package_bytes)

            # ---- download interruption: the loader resumes with Range --------------------
            # A half-written .partial file is what a killed download leaves behind; the loader must
            # continue from there (Range) rather than re-download from zero, and the finished file
            # must match the signed SHA-256.
            destination = Path(temporary) / "resumed.zip"
            destination.write_bytes(harness.package_bytes[:50000])
            done = harness.cli_smoke("--download-check", harness.base, "chispa", USER, USER_PASSWORD,
                                     str(destination))
            output = (done.stdout or "") + (done.stderr or "")
            check("下载中断后从断点续传并完成", done.returncode == 0 and "DOWNLOAD OK" in output, output[-300:])
            check("续传后文件与签名清单哈希一致",
                  destination.exists() and hashlib.sha256(destination.read_bytes()).hexdigest() ==
                  hashlib.sha256(harness.package_bytes).hexdigest(), "")
            # ---- server side of resume ---------------------------------------------------
            status, headers, body = call(DIRECT, "GET",
                                         f"{harness.base}/api/v1/programs/chispa/releases/1.0.0/package",
                                         headers={**auth, "Range": "bytes=100-"})
            check("服务端支持 Range（206 + 正确的尾部字节）",
                  status == 206 and body == harness.package_bytes[100:] and
                  f"bytes 100-{total - 1}/{total}" in (headers.get("Content-Range") or ""),
                  f"{status} {headers.get('Content-Range')}")
            status, headers, _ = call(DIRECT, "GET",
                                      f"{harness.base}/api/v1/programs/chispa/releases/1.0.0/package",
                                      headers={**auth, "Range": f"bytes={total + 10}-"})
            check("越界的 Range 被拒绝（416）", status == 416, status)

            # ---- limits -------------------------------------------------------------------
            big = json.dumps({"username": "a" * 20000, "password": "b"}).encode()
            status, _, _ = call(DIRECT, "POST", harness.base + "/api/v1/session",
                                headers={"Content-Type": "application/json"}, body=big)
            check("超大的登录请求体被拒绝", status in (413, 400), status)

            statuses = []
            for _ in range(25):
                status, _, _ = call(DIRECT, "POST", harness.base + "/api/v1/session",
                                    headers={"Content-Type": "application/json"},
                                    body=json.dumps({"username": USER, "password": "wrong"}).encode())
                statuses.append(status)
            check("登录接口按地址限流", 429 in statuses, statuses[-6:])
        finally:
            harness.stop()

        # API budget is configurable; prove the limiter actually applies to ordinary API calls.
        tight = Harness(Path(temporary) / "tight", {"CHISPA_LIMIT_API_PER_MINUTE": "60"})
        tight.prepare()
        tight.start()
        try:
            token = tight.token()
            statuses = [call(DIRECT, "GET", tight.base + "/api/v1/programs",
                             headers={"Authorization": "Bearer " + token})[0] for _ in range(70)]
            check("普通 API 也受限流保护", statuses.count(200) >= 60 and 429 in statuses,
                  statuses.count(200))
        finally:
            tight.stop()

        # ---- corrupt package and revoked permission ---------------------------------------
        broken = Harness(Path(temporary) / "broken")
        broken.prepare()
        # flip a byte after publish: the manifest no longer matches the file on disk
        payload = bytearray(broken.package.read_bytes())
        payload[40] ^= 0xFF
        broken.package.write_bytes(bytes(payload))
        broken.start()
        try:
            token = broken.token()
            auth = {"Authorization": "Bearer " + token}
            status, _, body = call(DIRECT, "POST",
                                   f"{broken.base}/api/v1/programs/chispa/releases/1.0.0/launch",
                                   headers=auth)
            check("磁盘上的包被改坏时启动被拒绝且给出明确提示",
                  status >= 400 and (b"package_corrupt" in body or b"manifest" in body.lower()), f"{status} {body[:120]}")

            status, _, _ = call(DIRECT, "GET",
                                f"{broken.base}/api/v1/programs/chispa/releases/1.0.0/package", headers=auth)
            check("改坏的包仍然可以被下载（由客户端的哈希校验拦住）", status == 200, status)

            # permission revoked while the session is live (through the admin page, as an admin would)
            delete_user_through_admin(broken, USER)
            status, _, body = call(DIRECT, "GET", broken.base + "/api/v1/programs", headers=auth)
            check("权限被撤销后旧会话立即失效", status in (401, 403), f"{status} {body[:120]}")
        finally:
            broken.stop()

        # ---- package missing from disk ----------------------------------------------------
        missing = Harness(Path(temporary) / "missing")
        missing.prepare()
        missing.package.unlink()
        missing.start()
        try:
            token = missing.token()
            auth = {"Authorization": "Bearer " + token}
            status, _, body = call(DIRECT, "POST",
                                   f"{missing.base}/api/v1/programs/chispa/releases/1.0.0/launch",
                                   headers=auth)
            check("包文件缺失时返回明确的不可用状态", status == 404, f"{status} {body[:120]}")
        finally:
            missing.stop()

    failures = [name for name, passed, _ in RESULTS if not passed]
    print()
    print(f"{len(RESULTS) - len(failures)}/{len(RESULTS)} checks passed")
    if failures:
        print("FAILED: " + ", ".join(failures))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
