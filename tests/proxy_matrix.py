"""Proxy-trust, cookie and readiness acceptance matrix (task 04).

The deployment this protects: public :24154 -> TLS terminates in the tunnel client on this machine
-> plain HTTP from 127.0.0.1:5115 carrying X-Forwarded-For / X-Forwarded-Proto. A probe of the live
tunnel showed the tunnel client *appends* the real client to X-Forwarded-For, leaves X-Real-IP and
RFC 7239 Forwarded exactly as the client sent them, and always overwrites X-Forwarded-Proto.

So there are two peers to test against:
  * 127.0.0.1 - the configured proxy. Its headers may be believed, but only its own entry.
  * a non-loopback address of this machine - an untrusted peer. Nothing it says may be believed,
    and plain HTTP from it must still be refused.

Run: python tests/proxy_matrix.py
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
ADMIN = "manager"
ADMIN_PASSWORD = "an excellent admin password"
USER_PASSWORD = "correct horse battery staple"
RESULTS = []


def check(name, passed, detail=""):
    RESULTS.append((name, bool(passed), detail))
    print(("PASS " if passed else "FAIL ") + name + ("" if passed else f"  [{str(detail)[:500]}]"))


def free_port():
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def non_loopback_ipv4():
    """An address of this machine that is not 127.0.0.1, used as an untrusted peer."""
    candidates = []
    # A real interface address, not the VPN/tunnel one: 198.18.0.0/15 is the address block the
    # local proxy uses for its tunnel adapter, and connecting to it from this machine lands back on
    # the local stack (so the service would still see loopback and the test would prove nothing).
    try:
        listing = subprocess.run(
            ["powershell.exe", "-NoProfile", "-Command",
             "Get-NetIPAddress -AddressFamily IPv4 | Select-Object -ExpandProperty IPAddress"],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30)
        candidates.extend(line.strip() for line in listing.stdout.splitlines() if line.strip())
    except (OSError, subprocess.SubprocessError):
        pass
    try:
        for info in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET):
            candidates.append(info[4][0])
    except OSError:
        pass

    def usable(address):
        if not address or address.startswith("127.") or address == "0.0.0.0":
            return False
        if address.startswith("169.254."):
            return False
        return not re.match(r"^198\.1[89]\.", address)

    private = [address for address in candidates if usable(address)
               and re.match(r"^(10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[01])\.)", address)]
    return private[0] if private else next((address for address in candidates if usable(address)), None)


# The machine running this test has an HTTP proxy configured for outbound traffic; a test that is
# supposed to reach a local listener directly must not go through it.
class _PermissivePolicy(http.cookiejar.DefaultCookiePolicy):
    """Return Secure cookies over http. The server's flags are what this test asserts on; a real
    browser would refuse to send them, which is exactly why they are set in production."""

    def return_ok_secure(self, cookie, request):
        return True


def cookie_jar():
    return http.cookiejar.CookieJar(_PermissivePolicy())


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    # 3xx is an answer, not an instruction: the tests assert on the status line.
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


DIRECT = urllib.request.build_opener(urllib.request.ProxyHandler({}), _NoRedirect())


def call(method, url, headers=None, body=None, timeout=10, opener=None):
    request = urllib.request.Request(url, data=body, headers=headers or {}, method=method)
    open_with = opener.open if opener else DIRECT.open
    try:
        with open_with(request, timeout=timeout) as response:
            # The raw header container, not a dict: Set-Cookie can appear more than once and this
            # test needs every one of them.
            return response.status, response.headers, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.headers, error.read()


class Deployment:
    """One server instance with a temp data directory and a chosen deployment configuration."""

    def __init__(self, temp: Path, name: str, env_extra: dict, bind: str = "0.0.0.0"):
        self.temp = temp / name
        self.data = self.temp / "data"
        self.data.mkdir(parents=True, exist_ok=True)
        self.port = free_port()
        self.bind = bind
        self.base = f"http://127.0.0.1:{self.port}"
        self.process = None
        self.env = os.environ.copy()
        self.env["CHISPA_SERVER_DATA"] = str(self.data)
        self.env["DOTNET_CLI_HOME"] = str(ROOT / ".tooling/home")
        self.env["TEMP"] = str(ROOT / ".tooling/temp")
        self.env["TMP"] = str(ROOT / ".tooling/temp")
        self.env.update(env_extra)

    def cli(self, *args, stdin=None):
        return subprocess.run([str(DOTNET), str(SERVER), *map(str, args)], env=self.env, input=stdin,
                              capture_output=True, text=True, encoding="utf-8", errors="replace")

    def prepare(self, trusted_proxies=None):
        self.env["CHISPA_TRUSTED_PROXIES"] = trusted_proxies or ""
        self.env["CHISPA_SERVER_URL"] = f"http://{self.bind}:{self.port}"
        for args, stdin in ((["admin-create", ADMIN], ADMIN_PASSWORD + "\n"),
                            (["user-add", "probeuser"], USER_PASSWORD + "\n")):
            done = self.cli(*args, stdin=stdin)
            if done.returncode:
                raise SystemExit(done.stdout + done.stderr)

    def start(self):
        hidden = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        self.process = subprocess.Popen([str(DOTNET), str(SERVER)], env=self.env, stdout=subprocess.PIPE,
                                        stderr=subprocess.STDOUT, text=True, encoding="utf-8",
                                        errors="replace", creationflags=hidden)
        for _ in range(200):
            try:
                status, _, _ = call("GET", self.base + "/health")
                if status == 200:
                    return
            except OSError:
                time.sleep(0.1)
        raise SystemExit("server did not start: " + (self.process.stdout.read() if self.process.stdout else ""))

    def stop(self):
        if self.process is None:
            return
        self.process.terminate()
        try:
            self.process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            self.process.kill()
        self.process = None

    def admin_jar(self):
        """A logged-in admin cookie jar (used only against the non-production instance)."""
        jar = cookie_jar()
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}),
                                             urllib.request.HTTPCookieProcessor(jar))
        status, _, page = call("GET", self.base + "/admin", opener=opener)
        page = page.decode("utf-8", "replace")
        token = re.search(r"name='__RequestVerificationToken' value='([^']+)'", page)
        if not token:
            raise SystemExit("no antiforgery token on the login page")
        form = urllib.parse.urlencode({"__RequestVerificationToken": token.group(1),
                                       "username": ADMIN, "password": ADMIN_PASSWORD}).encode()
        status, _, page = call("POST", self.base + "/admin/login", body=form, opener=opener,
                               headers={"Content-Type": "application/x-www-form-urlencoded"})
        return opener, page.decode("utf-8", "replace")


def part_untrusted_peer():
    """A peer that is not in the configured list cannot make the service believe anything it says.

    This machine has no second host to connect from - a request to one of its own LAN addresses is
    intercepted by the local proxy stack and arrives from loopback - so the peer used here is
    loopback, with the trusted list naming some *other* address. That is the same code path the
    tunnel's requests would take if the tunnel were not the configured proxy: the peer is checked
    against the list, and a peer that is not on it gets no say. Two deployments are checked, one
    with a decoy list and one with no list at all.
    """
    for label, configured in (("decoy list", "10.255.255.254"), ("no list", None)):
        print(f"-- untrusted peer ({label})")
        with tempfile.TemporaryDirectory(dir=ROOT / ".local", prefix="proxy-untrusted-") as temporary:
            deployment = Deployment(Path(temporary), label.replace(" ", "-"), {"CHISPA_PRODUCTION": "1"})
            deployment.prepare(trusted_proxies=configured)
            deployment.start()
            try:
                base = deployment.base
                status, _, _ = call("GET", base + "/health")
                check(f"{label}: the liveness probe still answers", status == 200, status)

                status, _, _ = call("GET", base + "/api/v1/connectivity/client")
                check(f"{label}: plain HTTP from the unlisted peer is refused", status == 403, status)

                status, _, _ = call("GET", base + "/api/v1/connectivity/client",
                                    headers={"X-Forwarded-Proto": "https"})
                check(f"{label}: forging X-Forwarded-Proto does not lift the refusal", status == 403, status)

                status, _, _ = call("GET", base + "/api/v1/connectivity/client",
                                    headers={"X-Forwarded-For": "6.6.6.6", "X-Real-IP": "7.7.7.7",
                                             "Forwarded": "for=6.6.6.6;proto=https",
                                             "X-Forwarded-Proto": "https"})
                check(f"{label}: forging X-Forwarded-For does not lift the refusal", status == 403, status)

                status, _, _ = call("GET", base + "/admin", headers={"X-Forwarded-Proto": "https"})
                check(f"{label}: the admin page stays refused too", status == 403, status)
            finally:
                deployment.stop()


def part_trusted_proxy():
    """With CHISPA_TRUSTED_PROXIES=127.0.0.1, only that peer's headers are used, and only the entry
    it appended - which is the one a client cannot have written."""
    print("-- trusted proxy: 127.0.0.1")
    with tempfile.TemporaryDirectory(dir=ROOT / ".local", prefix="proxy-trust-") as temporary:
        deployment = Deployment(Path(temporary), "trusted", {"CHISPA_LOG_DIRECTORY": ""})
        deployment.prepare(trusted_proxies="127.0.0.1")
        deployment.start()
        try:
            base = deployment.base
            status, _, _ = call("GET", base + "/health", headers={"X-Forwarded-Proto": "https"})
            check("a configured proxy can report HTTPS for a plain connection", status == 200, status)

            # A proxy that forwards a real client while reporting plaintext: the request is now
            # from a public address asking for HTTP, which is exactly what must not be served.
            # (/health is exempt from the gate on purpose - a local watchdog probes it - so the
            # check has to use a real endpoint.)
            status, _, _ = call("GET", base + "/api/v1/connectivity/client",
                                headers={"X-Forwarded-For": "192.0.2.5", "X-Forwarded-Proto": "http"})
            check("a proxied plaintext request from a public address is refused", status == 403, status)

            # The admin metrics page reports the address the last client probe came from, which is
            # the address the service would rate-limit and log. That is the observable "real source".
            opener, _ = deployment.admin_jar()
            call("GET", base + "/api/v1/connectivity/client",
                 headers={"X-Forwarded-For": "203.0.113.7", "X-Forwarded-Proto": "https"})
            status, _, body = call("GET", base + "/admin/metrics", opener=opener)
            snapshot = json.loads(body)
            check("the forwarded client address is the one the service records",
                  snapshot.get("lastClientProbeAddress") == "203.0.113.7", snapshot.get("lastClientProbeAddress"))

            # A client-supplied list in front of the proxy's own entry: the proxy appends, so the
            # right-most entry is the real one and the forged ones must be dropped.
            call("GET", base + "/api/v1/connectivity/client",
                 headers={"X-Forwarded-For": "6.6.6.6, 198.51.100.23", "X-Forwarded-Proto": "https"})
            status, _, body = call("GET", base + "/admin/metrics", opener=opener)
            snapshot = json.loads(body)
            check("a forged X-Forwarded-For entry is ignored, the proxy's own entry is used",
                  snapshot.get("lastClientProbeAddress") == "198.51.100.23", snapshot.get("lastClientProbeAddress"))

            # X-Real-IP and RFC 7239 Forwarded are forwarded verbatim by the tunnel client, so they
            # must have no influence at all.
            call("GET", base + "/api/v1/connectivity/client",
                 headers={"X-Forwarded-For": "198.51.100.24", "X-Real-IP": "7.7.7.7",
                          "Forwarded": "for=6.6.6.6;proto=https", "X-Forwarded-Proto": "https"})
            status, _, body = call("GET", base + "/admin/metrics", opener=opener)
            snapshot = json.loads(body)
            check("X-Real-IP and Forwarded are not trusted",
                  snapshot.get("lastClientProbeAddress") == "198.51.100.24", snapshot.get("lastClientProbeAddress"))
        finally:
            deployment.stop()


def part_rate_limit():
    """Per-IP login limiting has to follow the same address the rest of the service uses."""
    print("-- rate limiting follows the effective source")
    with tempfile.TemporaryDirectory(dir=ROOT / ".local", prefix="proxy-rate-") as temporary:
        deployment = Deployment(Path(temporary), "rate", {})
        deployment.prepare(trusted_proxies="127.0.0.1")
        deployment.start()
        try:
            def attempt(address, password="wrong password", proto="https"):
                headers = {"Content-Type": "application/json", "X-Forwarded-Proto": proto}
                if address:
                    headers["X-Forwarded-For"] = address
                body = json.dumps({"username": "probeuser", "password": password}).encode()
                return call("POST", deployment.base + "/api/v1/session", headers=headers, body=body)[0]

            codes = [attempt("192.0.2.10") for _ in range(5)]
            check("five failures from one address are refused normally", codes == [401] * 5, codes)
            check("the sixth failure from that address is rate limited", attempt("192.0.2.10") == 429,
                  "expected 429")
            check("another address is not affected by that limit", attempt("192.0.2.11") == 401,
                  "expected 401")
            check("the tunnel client's own entry is what counts (forged prefix ignored)",
                  attempt("1.2.3.4, 192.0.2.10") == 429, "expected 429 for the right-most entry")
        finally:
            deployment.stop()


def part_production_gate():
    """In production there is no plaintext admin access, not even from loopback; probes stay exempt."""
    print("-- production HTTPS gate")
    with tempfile.TemporaryDirectory(dir=ROOT / ".local", prefix="proxy-prodgate-") as temporary:
        deployment = Deployment(Path(temporary), "prodgate", {"CHISPA_PRODUCTION": "1"})
        deployment.prepare(trusted_proxies="127.0.0.1")
        deployment.start()
        try:
            base = deployment.base
            status, _, _ = call("GET", base + "/admin")
            check("production refuses plaintext admin access from loopback", status == 403, status)
            status, _, _ = call("GET", base + "/ready")
            check("...but the readiness probe still works over plain HTTP", status == 200, status)
            status, _, _ = call("GET", base + "/admin/login", headers={"X-Forwarded-Proto": "https"})
            check("...and the proxied HTTPS view of the same page works", status == 200, status)
        finally:
            deployment.stop()


def part_cookies():
    """Production marks the admin cookies Secure; the explicit local mode keeps working."""
    print("-- cookie policy")
    with tempfile.TemporaryDirectory(dir=ROOT / ".local", prefix="proxy-cookie-") as temporary:
        temp = Path(temporary)
        for label, extra, expect_secure in (
            ("production", {"CHISPA_PRODUCTION": "1"}, True),
            ("local", {"CHISPA_PRODUCTION": "0"}, False),
        ):
            deployment = Deployment(temp, label, extra)
            deployment.prepare(trusted_proxies="127.0.0.1")
            deployment.start()
            try:
                jar = cookie_jar()
                # Redirects are not followed here: the login answer is a 302 and the cookie this
                # test is about rides on it.
                opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), _NoRedirect(),
                                                     urllib.request.HTTPCookieProcessor(jar))
                forwarded = {"X-Forwarded-Proto": "https"} if expect_secure else {}
                status, headers, page = call("GET", deployment.base + "/admin/login", opener=opener,
                                             headers=forwarded)
                cookie_headers = headers.get_all("Set-Cookie") or []
                csrf_cookie = next((value for value in cookie_headers if "chispa_admin_csrf=" in value), "")
                check(f"{label}: the antiforgery cookie is issued", bool(csrf_cookie), cookie_headers)
                check(f"{label}: antiforgery cookie is HttpOnly", "httponly" in csrf_cookie.lower(), csrf_cookie)
                check(f"{label}: antiforgery cookie is SameSite=Strict",
                      "samesite=strict" in csrf_cookie.lower(), csrf_cookie)
                check(f"{label}: antiforgery Secure flag {'present' if expect_secure else 'absent'}",
                      ("secure" in csrf_cookie.lower()) == expect_secure, csrf_cookie)

                page = page.decode("utf-8", "replace")
                token = re.search(r"name='__RequestVerificationToken' value='([^']+)'", page).group(1)
                form = urllib.parse.urlencode({"__RequestVerificationToken": token,
                                               "username": ADMIN, "password": ADMIN_PASSWORD}).encode()
                status, headers, _ = call("POST", deployment.base + "/admin/login", body=form, opener=opener,
                                          headers={"Content-Type": "application/x-www-form-urlencoded",
                                                   **forwarded})
                login_cookies = headers.get_all("Set-Cookie") or []
                admin_cookie = next((value for value in login_cookies if "chispa_admin=" in value), "")
                check(f"{label}: a successful login issues the admin cookie", bool(admin_cookie),
                      f"{status} {login_cookies}")
                check(f"{label}: admin cookie is HttpOnly", "httponly" in admin_cookie.lower(), admin_cookie)
                check(f"{label}: admin cookie is SameSite=Strict", "samesite=strict" in admin_cookie.lower(),
                      admin_cookie)
                check(f"{label}: admin Secure flag {'present' if expect_secure else 'absent'}",
                      ("secure" in admin_cookie.lower()) == expect_secure, admin_cookie)

                # CSRF must survive the production hardening. Sent with the session cookie but
                # without a token: the request has to be refused before anything is created.
                status, _, _ = call("POST", deployment.base + "/admin/users",
                                    body=urllib.parse.urlencode({"username": "csrfvictim",
                                                                 "password": "long enough password"}).encode(),
                                    opener=opener,
                                    headers={"Content-Type": "application/x-www-form-urlencoded",
                                             "X-Forwarded-Proto": "https"})
                check(f"{label}: a CSRF-less admin mutation is refused", status == 400, status)
            finally:
                deployment.stop()


def part_readiness():
    """Ready/not-ready without telling strangers anything, and no server banner."""
    print("-- readiness")
    with tempfile.TemporaryDirectory(dir=ROOT / ".local", prefix="proxy-ready-") as temporary:
        deployment = Deployment(Path(temporary), "ready", {"CHISPA_READINESS_CACHE_SECONDS": "0"})
        deployment.prepare(trusted_proxies="127.0.0.1")
        deployment.start()
        try:
            status, headers, body = call("GET", deployment.base + "/ready")
            text = body.decode("utf-8")
            check("a healthy instance reports ready", status == 200 and json.loads(text)["status"] == "ready", text)
            check("the readiness body carries nothing else", set(json.loads(text)) == {"status"}, text)
            leaked = [word for word in ("C:\\", ".dll", "net10", "Chispa", "users.json") if word in text]
            check("the readiness body leaks no internals", not leaked, leaked)
            check("no Server banner is sent", "Server" not in headers, headers)

            users = deployment.data / "users.json"
            backup = users.read_text(encoding="utf-8")
            users.write_text("{ this is not json", encoding="utf-8")
            status, _, body = call("GET", deployment.base + "/ready")
            text = body.decode("utf-8")
            check("unreadable account data reports not-ready", status == 503 and json.loads(text)["status"] == "not-ready",
                  f"{status} {text}")
            check("...still without leaking the cause", "json" not in text.lower() and "users" not in text.lower(), text)
            users.write_text(backup, encoding="utf-8")
            status, _, _ = call("GET", deployment.base + "/ready")
            check("it recovers when the data is readable again", status == 200, status)
        finally:
            deployment.stop()


def part_health_script():
    print("-- availability script")
    script = ROOT / "tools/health-check.ps1"
    check("the availability script exists", script.exists(), str(script))
    if not script.exists():
        return
    with tempfile.TemporaryDirectory(dir=ROOT / ".local", prefix="proxy-health-") as temporary:
        deployment = Deployment(Path(temporary), "health", {})
        deployment.prepare(trusted_proxies="127.0.0.1")
        deployment.start()
        try:
            done = subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                                   str(script), "-Url", deployment.base + "/ready", "-Once"],
                                  capture_output=True, text=True, encoding="utf-8", errors="replace")
            check("it reports a healthy service", done.returncode == 0, done.stdout + done.stderr)
            check("it stays quiet when nothing is wrong",
                  "alert" not in done.stdout.lower(), done.stdout)
            deployment.stop()
            done = subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                                   str(script), "-Url", deployment.base + "/ready", "-Once",
                                   "-Attempts", "1", "-TimeoutSeconds", "3"],
                                  capture_output=True, text=True, encoding="utf-8", errors="replace")
            check("it reports a dead service with a non-zero exit", done.returncode != 0, done.stdout + done.stderr)
            check("...and says no alert destination is configured, sending nothing",
                  "AlertWebhook is empty" in done.stdout or "未配置" in done.stdout, done.stdout)
        finally:
            deployment.stop()


def main():
    if not SERVER.exists():
        raise SystemExit("Build the Release server first: ./build.ps1")
    (ROOT / ".local").mkdir(exist_ok=True)
    part_untrusted_peer()
    part_trusted_proxy()
    part_rate_limit()
    part_production_gate()
    part_cookies()
    part_readiness()
    part_health_script()
    failures = [name for name, passed, _ in RESULTS if not passed]
    print()
    print(f"{len(RESULTS) - len(failures)}/{len(RESULTS)} checks passed")
    if failures:
        print("FAILED: " + ", ".join(failures))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
