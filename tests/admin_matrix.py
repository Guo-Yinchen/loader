"""Admin-panel hardening matrix (task 06).

The "view the user's password" feature is kept; what is tested is the path to it:

  * not signed in                          -> never shown
  * signed in, no fresh re-authentication  -> refused
  * wrong admin password on re-auth        -> refused, audited, rate limited
  * expired grant (server clock)           -> refused
  * missing CSRF token                     -> refused (400)
  * valid re-authentication                -> shown once, no-store, auto-clear page
  * repeated failures                      -> 429
  * audit log                              -> who/when/which/outcome, no secrets
  * admin network restriction              -> applies to the forwarded client address

Run: python tests/admin_matrix.py
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
ADMIN = "manager"
ADMIN_PASSWORD = "an excellent admin password"
USER = "alice"
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
        with opener.open(request, timeout=20) as response:
            return response.status, response.headers, response.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as error:
        return error.code, error.headers, error.read().decode("utf-8", "replace")


def token_of(html):
    match = re.search(r"name='__RequestVerificationToken' value='([^']+)'", html)
    return match.group(1) if match else ""


class Instance:
    def __init__(self, work: Path, name: str, env_extra=None):
        self.data = work / name / "data"
        self.data.mkdir(parents=True)
        self.port = free_port()
        self.base = f"http://127.0.0.1:{self.port}"
        self.env = os.environ.copy()
        self.env["CHISPA_SERVER_DATA"] = str(self.data)
        self.env["DOTNET_CLI_HOME"] = str(ROOT / ".tooling/home")
        self.env["TEMP"] = str(ROOT / ".tooling/temp")
        self.env["TMP"] = str(ROOT / ".tooling/temp")
        self.env.update(env_extra or {})
        self.process = None

    def cli(self, *args, stdin=None):
        return subprocess.run([str(DOTNET), str(SERVER), *args], env=self.env, input=stdin,
                              capture_output=True, text=True, encoding="utf-8", errors="replace")

    def prepare(self):
        for args, stdin in ((["admin-create", ADMIN], ADMIN_PASSWORD + "\n"),
                            (["user-add", USER], USER_PASSWORD + "\n")):
            done = self.cli(*args, stdin=stdin)
            if done.returncode:
                raise SystemExit(done.stdout + done.stderr)

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

    def session(self):
        jar = http.cookiejar.CookieJar()
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}),
                                             urllib.request.HTTPCookieProcessor(jar))
        _, _, page = call(opener, "GET", self.base + "/admin/login")
        form = urllib.parse.urlencode({"__RequestVerificationToken": token_of(page),
                                       "username": ADMIN, "password": ADMIN_PASSWORD}).encode()
        call(opener, "POST", self.base + "/admin/login", body=form,
             headers={"Content-Type": "application/x-www-form-urlencoded"})
        _, _, admin_page = call(opener, "GET", self.base + "/admin")
        return opener, admin_page

    def audit_lines(self):
        path = self.data / "admin-audit.log"
        if not path.exists():
            return []
        return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def form_post(opener, url, fields, extra_headers=None):
    headers = {"Content-Type": "application/x-www-form-urlencoded"}
    headers.update(extra_headers or {})
    return call(opener, "POST", url, body=urllib.parse.urlencode(fields).encode(), headers=headers)


def main():
    if not SERVER.exists():
        raise SystemExit("Build the Release server first: ./build.ps1")
    (ROOT / ".local").mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=ROOT / ".local", prefix="admin-matrix-") as temporary:
        work = Path(temporary)
        instance = Instance(work, "main", {"CHISPA_ADMIN_STEPUP_SECONDS": "30"})
        instance.prepare()
        instance.start()
        try:
            # ---- not signed in ---------------------------------------------------------------
            anonymous = urllib.request.build_opener(urllib.request.ProxyHandler({}),
                                                    urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
            status, _, page = form_post(anonymous, f"{instance.base}/admin/users/{USER}/password/view",
                                        {"grant": "whatever"})
            check("未登录不能查看密码", USER_PASSWORD not in page and "登录" in page, status)

            opener, admin_page = instance.session()
            admin_token = token_of(admin_page)

            # ---- signed in, but no fresh re-authentication ------------------------------------
            status, headers, page = form_post(opener, f"{instance.base}/admin/users/{USER}/password/view",
                                              {"__RequestVerificationToken": admin_token})
            check("已登录但没有二次验证时拒绝查看", status == 403 and USER_PASSWORD not in page, status)
            check("...并提示需要重新验证", "重新验证" in page, page[:120])

            # ---- CSRF -------------------------------------------------------------------------
            status, _, page = form_post(opener, f"{instance.base}/admin/users/{USER}/password/stepup",
                                        {"adminPassword": ADMIN_PASSWORD})
            check("缺少 CSRF 的验证请求被拒绝", status == 400, status)
            status, _, page = form_post(opener, f"{instance.base}/admin/users/{USER}/password/view",
                                        {"grant": "x", "__RequestVerificationToken": "wrong"})
            check("缺少有效 CSRF 的查看请求被拒绝", status == 400, status)

            # ---- wrong admin password on re-authentication ------------------------------------
            status, _, page = form_post(opener, f"{instance.base}/admin/users/{USER}/password/stepup",
                                        {"__RequestVerificationToken": admin_token, "adminPassword": "wrong password"})
            check("二次验证密码错误时拒绝", status == 401 and "验证失败" in page, status)

            # ---- correct re-authentication, then view -----------------------------------------
            status, _, page = form_post(opener, f"{instance.base}/admin/users/{USER}/password/stepup",
                                        {"__RequestVerificationToken": admin_token, "adminPassword": ADMIN_PASSWORD})
            grant = re.search(r"name='grant' value='([^']+)'", page)
            check("正确的管理员密码可以换取一次性授权", status == 200 and grant is not None, status)
            grant_value = grant.group(1) if grant else ""
            view_token = token_of(page)

            status, headers, page = form_post(opener, f"{instance.base}/admin/users/{USER}/password/view",
                                              {"__RequestVerificationToken": view_token, "grant": grant_value})
            check("有效授权可以看到密码", status == 200 and USER_PASSWORD in page, status)
            check("密码页面禁止缓存",
                  "no-store" in (headers.get("Cache-Control") or ""), headers.get("Cache-Control"))
            check("密码页面带自动清除提示", "id='secretCountdown'" in page or "自动清除" in page, "")

            # ---- grant is limited -------------------------------------------------------------
            codes = []
            for _ in range(3):
                status, _, page = form_post(opener, f"{instance.base}/admin/users/{USER}/password/view",
                                            {"__RequestVerificationToken": view_token, "grant": grant_value})
                codes.append(status)
            check("一次性授权用满后失效（3 次上限）", codes[0] == 200 and codes[-1] == 403, codes)

            # ---- expired grant ----------------------------------------------------------------
            status, _, page = form_post(opener, f"{instance.base}/admin/users/{USER}/password/stepup",
                                        {"__RequestVerificationToken": admin_token, "adminPassword": ADMIN_PASSWORD})
            grant_value = re.search(r"name='grant' value='([^']+)'", page).group(1)
            view_token = token_of(page)
            time.sleep(31)  # the grant lives 30 seconds on this instance
            status, _, page = form_post(opener, f"{instance.base}/admin/users/{USER}/password/view",
                                        {"__RequestVerificationToken": view_token, "grant": grant_value})
            check("过期的授权被拒绝", status == 403 and USER_PASSWORD not in page, status)

            # ---- rate limit on repeated wrong passwords ---------------------------------------
            codes = []
            for _ in range(6):
                status, _, _ = form_post(opener, f"{instance.base}/admin/users/{USER}/password/stepup",
                                         {"__RequestVerificationToken": admin_token,
                                          "adminPassword": "definitely wrong"})
                codes.append(status)
            check("连续错误最终触发限流", codes[-1] == 429 and 401 in codes, codes)

            # ---- audit -----------------------------------------------------------------------
            entries = instance.audit_lines()
            events = {(entry["eventName"], entry["result"]) for entry in entries}
            check("审计记录了验证成败", ("step-up", "ok") in events and ("step-up", "denied-password") in events, events)
            check("审计记录了查看结果", ("password-view", "ok") in events and
                  ("password-view", "denied-missing-grant") in events, events)
            check("审计记录了被限流", ("step-up", "denied-rate") in events, events)
            check("审计记录了目标账号",
                  any(entry["target"] == USER for entry in entries), "")
            check("审计不含密码明文", USER_PASSWORD not in json.dumps(entries), "")
            raw = (instance.data / "admin-audit.log").read_text(encoding="utf-8")
            users = json.loads((instance.data / "users.json").read_text(encoding="utf-8"))
            ciphertext = next(u["passwordCiphertext"] for u in users if u["username"] == USER)
            check("审计不含密码密文", ciphertext[:24] not in raw, "")

            # ---- logout ----------------------------------------------------------------------
            call(opener, "POST", f"{instance.base}/admin/logout",
                 body=urllib.parse.urlencode({"__RequestVerificationToken": admin_token}).encode(),
                 headers={"Content-Type": "application/x-www-form-urlencoded"})
            status, _, page = form_post(opener, f"{instance.base}/admin/users/{USER}/password/stepup",
                                        {"__RequestVerificationToken": admin_token, "adminPassword": ADMIN_PASSWORD})
            check("退出登录后不能再次查看", USER_PASSWORD not in page, status)
        finally:
            instance.stop()

        # ---- admin network restriction -------------------------------------------------------
        # The restriction is applied to the effective client address, i.e. after the trusted proxy
        # has supplied it - which is how the tunnel presents every request in production.
        limited = Instance(work, "netrestrict", {"CHISPA_TRUSTED_PROXIES": "127.0.0.1",
                                                 "CHISPA_ADMIN_ALLOWED_NETWORKS": "203.0.113.0/24"})
        limited.prepare()
        limited.start()
        try:
            status, _, _ = call(DIRECT, "GET", limited.base + "/admin/login",
                                headers={"X-Forwarded-For": "203.0.113.7", "X-Forwarded-Proto": "https"})
            check("白名单内的来源地址可以打开后台", status == 200, status)
            status, _, _ = call(DIRECT, "GET", limited.base + "/admin/login",
                                headers={"X-Forwarded-For": "198.51.100.9", "X-Forwarded-Proto": "https"})
            check("白名单外的来源地址被拒绝", status == 403, status)
            status, _, _ = call(DIRECT, "GET", limited.base + "/admin/login")
            check("本机（loopback）始终允许，避免把自己锁在门外", status == 200, status)
        finally:
            limited.stop()

    failures = [name for name, passed, _ in RESULTS if not passed]
    print()
    print(f"{len(RESULTS) - len(failures)}/{len(RESULTS)} checks passed")
    if failures:
        print("FAILED: " + ", ".join(failures))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
