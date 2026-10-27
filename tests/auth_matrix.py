"""Launch-authorization acceptance matrix (task 02).

Everything here runs against a throwaway service on a random loopback port and a throwaway
data directory. The client side is the real module: tests/auth/build-selftest.ps1 compiles
Usermode/Auth/*.cpp from the Wardogs repository and links them into a console entry point, so
what is exercised below is the same gate the overlay runs, not a model of it.

Scenarios (the brief's acceptance list):
  direct start with no ticket and no terminal        -> refused, exit 3
  direct start with account sign-in                   -> allowed, exit 0
  valid loader ticket (written by the C# loader)      -> allowed, exit 0
  ticket for another program / version                -> refused as a mismatch, exit 7
  ticket expired (client-side guard)                  -> refused, exit 4
  ticket expired (service-side answer)                -> refused, exit 4
  replayed ticket                                     -> refused, exit 6
  account expired while running                       -> stopped, exit 4
  signed out of the loader while running              -> stopped, exit 4
  network lost while running                          -> keeps running until the lease lapses
  service down at start                               -> refused, exit 5

Run:  python tests/auth_matrix.py
Requires: a Release server build (./build.ps1) and tests/Chispa.Smoke built (see README).
"""

import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import time
from datetime import datetime, timedelta, timezone
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
DOTNET = ROOT / ".tooling/dotnet/dotnet.exe"
if not DOTNET.exists():
    DOTNET = "dotnet"
pointer = ROOT / ".local/current-server-build.txt"
SERVER = (ROOT / ".local/server-builds" / pointer.read_text().strip() / "Chispa.Server.dll"
          if pointer.exists() else ROOT / "server/bin/Release/net10.0/Chispa.Server.dll")
SMOKE = ROOT / "tests/bin/Release/net10.0-windows/Chispa.Smoke.dll"
WARD = Path(os.environ.get("CHISPA_WARD_REPO", str(ROOT.parent / "wardogs chispa")))
PASSWORD = "correct horse battery staple"
ACCOUNT = "matrixuser"
PROGRAM = "chispa"
VERSION = "1.2.3"

RESULTS = []


def check(name, passed, detail=""):
    RESULTS.append((name, bool(passed), detail))
    print(("PASS " if passed else "FAIL ") + name + ("" if passed else f"  [{detail}]"))
    return passed


def request(url, payload=None, token=None, method=None):
    headers = {}
    if payload is not None:
        headers["Content-Type"] = "application/json"
    if token:
        headers["Authorization"] = "Bearer " + token
    body = json.dumps(payload).encode() if payload is not None else None
    return urllib.request.urlopen(urllib.request.Request(url, data=body, headers=headers, method=method), timeout=10)


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class Harness:
    def __init__(self, temp: Path):
        self.temp = temp
        self.data = temp / "data"
        self.env = os.environ.copy()
        self.env["CHISPA_SERVER_DATA"] = str(self.data)
        self.env["DOTNET_CLI_HOME"] = str(ROOT / ".tooling/home")
        self.env["TEMP"] = str(ROOT / ".tooling/temp")
        self.env["TMP"] = str(ROOT / ".tooling/temp")
        self.env["CHISPA_LAUNCH_TICKET_SECONDS"] = "10"
        self.env["CHISPA_LAUNCH_LEASE_SECONDS"] = "30"
        self.env["CHISPA_LAUNCH_HEARTBEAT_SECONDS"] = "5"
        self.env["CHISPA_LIMIT_LOGIN_PER_MINUTE"] = "600"  # rate-limit behavior has its own fault matrix
        self.port = free_port()
        self.base = f"http://127.0.0.1:{self.port}"
        self.server = None
        self.selftest = None

    # ---- setup -----------------------------------------------------------------------------
    def cli(self, *args, stdin=None):
        return subprocess.run([str(DOTNET), str(SERVER), *args], env=self.env, input=stdin,
                              capture_output=True, text=True, check=False)

    def prepare_data(self):
        for program, name, executable in ((PROGRAM, "Chispa", "Wardogs.exe"), ("helper", "Helper", "Helper.exe")):
            done = self.cli("program-add", program, name, executable)
            if done.returncode:
                raise SystemExit(done.stdout + done.stderr)
        added = self.cli("user-add", ACCOUNT, stdin=PASSWORD + "\n")
        if added.returncode:
            raise SystemExit(added.stdout + added.stderr)
        runtime = self.temp / "runtime"
        runtime.mkdir()
        (runtime / "Wardogs.exe").write_bytes(b"matrix-exe")
        # Releases are signed now, including the throwaway one this matrix needs: publishing is
        # signed-only, so a test that skipped it would be testing a server that no longer exists.
        signer = ROOT / "tools/Chispa.Sign/bin/Release/net10.0/Chispa.Sign.dll"
        if not signer.exists():
            raise SystemExit("Build the signing tool first: ./build.ps1")
        keys = self.temp / "signing"
        made = subprocess.run([str(DOTNET), str(signer), "keygen", "--out", str(keys), "--id", "matrix-key"],
                              env=self.env, capture_output=True, text=True,
                              encoding="utf-8", errors="replace")
        if made.returncode:
            raise SystemExit(made.stdout + made.stderr)
        self.env["CHISPA_RELEASE_TRUST_KEYS"] = str(keys / "release-public.json")
        archive = self.temp / "package.zip"
        signature = self.temp / "signature.json"
        packed = subprocess.run([str(DOTNET), str(signer), "pack", "--source", str(runtime),
                                 "--out", str(archive), "--executable", "Wardogs.exe"], env=self.env,
                                capture_output=True, text=True, encoding="utf-8", errors="replace")
        if packed.returncode:
            raise SystemExit(packed.stdout + packed.stderr)
        signed = subprocess.run([str(DOTNET), str(signer), "sign", "--package", str(archive),
                                 "--program", PROGRAM, "--version", VERSION, "--executable", "Wardogs.exe",
                                 "--key", str(keys / "release-private.pem"), "--key-id", "matrix-key",
                                 "--out", str(signature)], env=self.env, capture_output=True, text=True,
                                encoding="utf-8", errors="replace")
        if signed.returncode:
            raise SystemExit(signed.stdout + signed.stderr)
        published = self.cli("publish", PROGRAM, VERSION, str(archive), str(signature))
        if published.returncode:
            raise SystemExit(published.stdout + published.stderr)

    def build_selftest(self):
        script = WARD / "tests/auth/build-selftest.ps1"
        if not script.exists():
            raise SystemExit(f"Missing {script}")
        output = self.temp / "selftest"
        built = subprocess.run(["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script),
                                "-ServerUrl", self.base, "-Version", VERSION, "-Output", str(output)],
                               cwd=str(WARD), capture_output=True, text=True, check=False)
        if built.returncode:
            raise SystemExit("Selftest build failed:\n" + built.stdout + built.stderr)
        self.selftest = output / "auth-selftest.exe"
        if not self.selftest.exists():
            raise SystemExit("Selftest binary missing after a successful build")

    def start_server(self):
        env = dict(self.env)
        env["CHISPA_SERVER_URL"] = self.base
        hidden = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        self.server = subprocess.Popen([str(DOTNET), str(SERVER)], env=env, stdout=subprocess.DEVNULL,
                                       stderr=subprocess.DEVNULL, creationflags=hidden)
        for _ in range(200):
            try:
                with urllib.request.urlopen(self.base + "/health", timeout=2) as response:
                    if response.status == 200:
                        return
            except OSError:
                time.sleep(0.1)
        raise SystemExit("The service did not start")

    def stop_server(self):
        if self.server is None:
            return
        self.server.terminate()
        try:
            self.server.wait(timeout=15)
        except subprocess.TimeoutExpired:
            self.server.kill()
        self.server = None

    # ---- account / ticket helpers ------------------------------------------------------------
    def login(self):
        with request(self.base + "/api/v1/session", {"username": ACCOUNT, "password": PASSWORD}) as response:
            return json.load(response)["token"]

    def issue_ticket(self, token, program=PROGRAM, version=VERSION):
        with request(f"{self.base}/api/v1/programs/{program}/releases/{version}/launch-ticket", {},
                     token=token, method="POST") as response:
            return json.load(response)["ticket"]

    def renew_ticket(self):
        return self.issue_ticket(self.login())

    def run_selftest(self, command, auth_dir, stdin_data=None, timeout=90, extra_env=None):
        env = dict(os.environ)
        env["CHISPA_AUTH_DIR"] = str(auth_dir)
        if extra_env:
            env.update(extra_env)
        return subprocess.run([str(self.selftest), *command], input=stdin_data, env=env,
                              capture_output=True, text=True, timeout=timeout)

    def write_ticket_with_csharp(self, ticket, auth_dir, expires=None, program=PROGRAM, version=VERSION,
                                 server=None, unique=False):
        expires = expires or (datetime.now(timezone.utc) + timedelta(minutes=5))
        env = dict(os.environ)
        env["CHISPA_AUTH_DIR"] = str(auth_dir)
        command = [str(DOTNET), str(SMOKE), "--write-launch-ticket", program,
                               server or self.base, version, ticket,
                               expires.isoformat(timespec="milliseconds"), str(auth_dir)]
        if unique:
            command.append("unique")
        done = subprocess.run(command,
                              env=env, capture_output=True, text=True, encoding="utf-8", check=False)
        if done.returncode:
            raise SystemExit("The loader's ticket writer failed:\n" + done.stdout + done.stderr)
        return done.stdout.strip()

    def write_ticket(self, ticket, auth_dir, expires_in=120, program=PROGRAM, version=VERSION, server=None,
                     file_program=None):
        command = ["write-ticket", "--program", program, "--version", version, "--ticket", ticket,
                   "--expires-in", str(expires_in), "--server", server or self.base]
        if file_program:
            command += ["--file-program", file_program]
        done = self.run_selftest(command, auth_dir)
        if done.returncode:
            raise SystemExit("write-ticket failed:\n" + done.stdout + done.stderr)

    def monitor_start(self, auth_dir):
        env = dict(os.environ)
        env["CHISPA_AUTH_DIR"] = str(auth_dir)
        process = subprocess.Popen([str(self.selftest), "run", "--monitor", "45"], env=env,
                                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        lines = []
        deadline = time.time() + 30
        while time.time() < deadline:
            line = process.stdout.readline()
            if not line:
                break
            lines.append(line.rstrip())
            if line.startswith("MONITOR started"):
                return process, lines
            if line.startswith("FINAL"):
                break
        process.kill()
        raise SystemExit("The gate never reached the monitoring phase:\n" + "\n".join(lines))

    def drain(self, process, lines, timeout=60):
        deadline = time.time() + timeout
        while process.poll() is None and time.time() < deadline:
            line = process.stdout.readline()
            if not line:
                break
            lines.append(line.rstrip())
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
        remainder = process.stdout.read() or ""
        lines.extend(line for line in remainder.splitlines() if line)
        return lines


def set_account_expiry(harness, when):
    """Rewrites the account's expiry in place. The store re-reads users.json on every call, so
    this is the same thing the admin page does, without needing an admin session."""
    path = harness.data / "users.json"
    users = json.loads(path.read_text(encoding="utf-8"))
    for user in users:
        if user.get("username", user.get("Username", "")).lower() == ACCOUNT:
            key = "expiresAt" if "expiresAt" in user else "ExpiresAt"
            user[key] = when.isoformat()
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(users), encoding="utf-8")
    os.replace(temporary, path)


def main():
    if not SERVER.exists():
        raise SystemExit("Build the Release server first: ./build.ps1")
    if not SMOKE.exists():
        raise SystemExit("Build the client smoke test first: dotnet build tests/Chispa.Smoke.csproj -c Release")
    root = ROOT / ".local"
    root.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=root, prefix="auth-matrix-") as temporary:
        temp = Path(temporary)
        harness = Harness(temp)
        harness.prepare_data()
        harness.build_selftest()
        harness.start_server()
        try:
            with request(harness.base + "/api/v1/launch/capabilities") as response:
                capabilities = json.load(response)
            if capabilities.get("api") != 1:
                raise SystemExit("The service does not advertise the launch API this test needs.")
        except urllib.error.HTTPError as error:
            if error.code == 404:
                raise SystemExit("The server build in use predates launch authorization. "
                                 "Run ./build.ps1 to rebuild the Release server, then re-run this test.")
            raise
        auth = temp / "auth"
        auth.mkdir()
        try:
            run_matrix(harness, auth, temp)
        finally:
            harness.stop_server()
    failures = [name for name, passed, _ in RESULTS if not passed]
    print()
    print(f"{len(RESULTS) - len(failures)}/{len(RESULTS)} checks passed")
    if failures:
        print("FAILED: " + ", ".join(failures))
        return 1
    return 0


def run_matrix(harness, auth, temp):
    # ---- 1. direct start, no credential, no terminal ---------------------------------------
    done = harness.run_selftest(["clear-ticket", "--program", PROGRAM], auth)
    done = harness.run_selftest(["run"], auth)
    check("direct start without a ticket or a terminal is refused", done.returncode == 3,
          f"exit {done.returncode}: {done.stdout.strip()} {done.stderr.strip()}")
    check("...and says why", "no console" in done.stdout, done.stdout.strip())

    # ---- 2. direct start with an account sign-in -------------------------------------------
    done = harness.run_selftest(["run"], auth, stdin_data=f"{ACCOUNT}\n{PASSWORD}\n",
                                extra_env={"CHISPA_AUTH_TEST_STDIN": "1"})
    check("direct start with account sign-in is allowed", done.returncode == 0,
          f"exit {done.returncode}: {done.stdout.strip()}")
    check("...and the lease names the account", f"account={ACCOUNT}" in done.stdout, done.stdout.strip())
    check("permanent subscription is reported explicitly for the menu", f"ACCOUNT name={ACCOUNT} expires=0" in done.stdout,
          done.stdout.strip())

    # ---- 3. a valid loader ticket, written by the C# loader --------------------------------
    ticket = harness.renew_ticket()
    path = Path(harness.write_ticket_with_csharp(ticket, auth))
    raw = path.read_bytes() if path.exists() else b""
    check("the hand-off file carries no plain-text ticket",
          len(raw) > 100 and ticket.encode() not in raw, f"{len(raw)} bytes")
    done = harness.run_selftest(["run"], auth)
    check("the loader's own ticket file is accepted by the program", done.returncode == 0,
          f"exit {done.returncode}: {done.stdout.strip()} {done.stderr.strip()}")
    check("the hand-off file is consumed when the program reads it", not path.exists(), str(path))

    # Explicit path survives a changed profile/auth directory and cannot collide with another start.
    ticket = harness.renew_ticket()
    handoff_dir = temp / "loader handoff 中文"
    path = Path(harness.write_ticket_with_csharp(ticket, handoff_dir, unique=True))
    other_auth = temp / "other-profile"
    other_auth.mkdir()
    done = harness.run_selftest(["run", "--chispa-ticket", str(path)], other_auth)
    check("explicit loader hand-off works across profile paths with spaces and Unicode",
          done.returncode == 0 and not path.exists(), done.stdout.strip())
    done = harness.run_selftest(["run", "--chispa-ticket", str(path)], other_auth,
                                stdin_data=f"{ACCOUNT}\n{PASSWORD}\n",
                                extra_env={"CHISPA_AUTH_TEST_STDIN": "1"})
    check("a missing explicit hand-off reports failure without asking for the password again",
          done.returncode == 3 and "Account:" not in done.stdout and "hand-off failed" in done.stdout,
          done.stdout.strip())

    # Expired accounts can inspect their library, but cannot receive a launch credential.
    set_account_expiry(harness, datetime.now(timezone.utc) - timedelta(minutes=1))
    expired_token = harness.login()
    with request(harness.base + "/api/v1/session", token=expired_token) as response:
        account = json.load(response)
    check("expired account can log in and retrieve its real subscription expiry",
          account["username"] == ACCOUNT and datetime.fromisoformat(account["accountExpiresAt"]) < datetime.now(timezone.utc))
    with request(harness.base + "/api/v1/programs", token=expired_token) as response:
        check("expired account can inspect its library", len(json.load(response)) > 0)
    try:
        harness.issue_ticket(expired_token)
        check("expired account cannot launch", False)
    except urllib.error.HTTPError as error:
        check("expired account cannot launch", error.code == 401)
    expired_client = subprocess.run([str(DOTNET), str(SMOKE), "--account-check", harness.base, ACCOUNT, PASSWORD],
                                    env=harness.env, capture_output=True, text=True)
    check("real loader accepts expired login and identifies the expired subscription",
          expired_client.returncode == 0 and json.loads(expired_client.stdout).get("AccountExpired") is True,
          expired_client.stdout + expired_client.stderr)
    set_account_expiry(harness, datetime.now(timezone.utc) + timedelta(days=30))
    # A failed launch must not destroy the library session; renewal works without signing in again.
    with request(harness.base + "/api/v1/session", token=expired_token) as response:
        check("renewal updates an existing expired login", datetime.fromisoformat(json.load(response)["accountExpiresAt"]) > datetime.now(timezone.utc))
    check("renewed account can obtain a launch ticket without logging in again", bool(harness.issue_ticket(expired_token)))

    # ---- 4. the same ticket presented for another program ----------------------------------
    ticket = harness.renew_ticket()
    harness.write_ticket(ticket, auth, program="helper", file_program=PROGRAM)
    done = harness.run_selftest(["run"], auth)
    check("a ticket issued for another program is refused", done.returncode == 7,
          f"exit {done.returncode}: {done.stdout.strip()}")

    # ---- 5. the same ticket presented for another version ----------------------------------
    ticket = harness.renew_ticket()
    harness.write_ticket(ticket, auth, version="9.9.9")
    done = harness.run_selftest(["run"], auth)
    check("a ticket for another version is refused", done.returncode == 7,
          f"exit {done.returncode}: {done.stdout.strip()}")

    # ---- 6. ticket expired before it was redeemed (client-side guard) ----------------------
    ticket = harness.renew_ticket()
    harness.write_ticket(ticket, auth, expires_in=-600)
    done = harness.run_selftest(["run"], auth)
    check("a ticket that expired before redemption is refused", done.returncode == 4,
          f"exit {done.returncode}: {done.stdout.strip()}")
    check("...as expired, not as a network problem", "expired" in done.stdout, done.stdout.strip())

    # ---- 7. replayed ticket ----------------------------------------------------------------
    ticket = harness.renew_ticket()
    harness.write_ticket(ticket, auth)
    first = harness.run_selftest(["run"], auth)
    harness.write_ticket(ticket, auth)
    second = harness.run_selftest(["run"], auth)
    check("a fresh ticket works once", first.returncode == 0, f"exit {first.returncode}: {first.stdout.strip()}")
    check("the same ticket again is refused as a replay", second.returncode == 6,
          f"exit {second.returncode}: {second.stdout.strip()}")
    check("...and the service calls it ticket_used", "ticket_used" in second.stdout, second.stdout.strip())

    # ---- 8. account expired while the program is running -----------------------------------
    path = auth / "ticket-chispa.bin"
    token = harness.login()
    harness.write_ticket(harness.issue_ticket(token), auth)
    process, lines = harness.monitor_start(auth)
    set_account_expiry(harness, datetime.now(timezone.utc) - timedelta(hours=1))
    lines = harness.drain(process, lines, timeout=40)
    check("an account that expires mid-session stops the program", process.returncode == 4,
          f"exit {process.returncode}: {lines[-6:]}")
    check("...and the reason is the account, not a network fault",
          any("account" in line for line in lines), lines[-6:])
    set_account_expiry(harness, datetime.now(timezone.utc) + timedelta(days=30))

    # ---- 9. signed out of the loader while the program is running --------------------------
    token = harness.login()
    harness.write_ticket(harness.issue_ticket(token), auth)
    process, lines = harness.monitor_start(auth)
    with request(harness.base + "/api/v1/session", token=token, method="DELETE") as response:
        assert response.status == 204
    lines = harness.drain(process, lines, timeout=40)
    check("signing out of the loader stops a running program", process.returncode == 4,
          f"exit {process.returncode}: {lines[-6:]}")
    check("...with the sign-out recorded as the reason",
          any("signed_out" in line for line in lines), lines[-6:])

    # ---- 10. ticket expired at the service (nothing local says so) -------------------------
    ticket = harness.renew_ticket()
    time.sleep(11)  # the test service issues 10 second tickets
    harness.write_ticket(ticket, auth, expires_in=120)
    done = harness.run_selftest(["run"], auth)
    check("the service refusing an expired ticket is reported", done.returncode == 4,
          f"exit {done.returncode}: {done.stdout.strip()}")
    check("...with the service's own reason", "ticket_expired" in done.stdout, done.stdout.strip())

    # Account deadline is earlier than the next client heartbeat. Even offline, it must stop there.
    account_deadline = datetime.now(timezone.utc) + timedelta(seconds=10)
    set_account_expiry(harness, account_deadline)
    token = harness.login()
    with request(f"{harness.base}/api/v1/programs/{PROGRAM}/releases/{VERSION}/launch-ticket", {}, token) as response:
        ticket = json.load(response)["ticket"]
    # Inspect protocol data independently of the C++ rendering path.
    with request(harness.base + "/api/v1/launch/redeem", {
        "ticket": ticket, "programId": PROGRAM, "version": VERSION, "device": "0" * 32}) as response:
        lease = json.load(response)
    check("issued lease contains account expiry and never outlives it",
          datetime.fromisoformat(lease["leaseExpiresAt"]) <= account_deadline
          and datetime.fromisoformat(lease["accountExpiresAt"]) == account_deadline)
    # Also cover the renewal answer, which supplies the menu's updated expiry.
    heartbeat = urllib.request.Request(harness.base + "/api/v1/launch/heartbeat", data=b"{}",
        headers={"Authorization": "Bearer " + lease["lease"], "Content-Type": "application/json", "X-Chispa-Device": "0" * 32})
    with urllib.request.urlopen(heartbeat, timeout=10) as response:
        renewed = json.load(response)
    check("heartbeat preserves the account deadline", datetime.fromisoformat(renewed["leaseExpiresAt"]) <= account_deadline
          and datetime.fromisoformat(renewed["accountExpiresAt"]) == account_deadline)
    harness.write_ticket(harness.issue_ticket(token), auth)
    process, lines = harness.monitor_start(auth)
    harness.stop_server()
    started = time.monotonic()
    lines = harness.drain(process, lines, timeout=35)
    elapsed = time.monotonic() - started
    check("offline program stops at subscription expiry before the next heartbeat",
          process.returncode == 4 and elapsed < 14, f"elapsed={elapsed:.1f}s, exit={process.returncode}, {lines[-5:]}")
    set_account_expiry(harness, datetime.now(timezone.utc) + timedelta(days=30))
    harness.start_server()

    # ---- 11. network lost while running: allowed until the lease lapses --------------------
    ticket = harness.renew_ticket()
    harness.write_ticket(ticket, auth)
    process, lines = harness.monitor_start(auth)
    time.sleep(3)
    harness.stop_server()
    stopped_at = time.time()
    lines = harness.drain(process, lines, timeout=60)
    elapsed = time.time() - stopped_at
    check("a running program survives a network outage on its lease", elapsed > 15,
          f"stopped after {elapsed:.1f}s")
    check("...and then stops when the lease lapses", process.returncode == 4,
          f"exit {process.returncode}: {lines[-8:]}")
    check("...because the lease lapsed, not because of a refusal",
          any("lapsed" in line for line in lines), lines[-8:])

    # ---- 12. no service at start -----------------------------------------------------------
    harness.write_ticket("selftest-ticket-offline-000000", auth)
    done = harness.run_selftest(["run"], auth)
    check("starting with no service at all is refused", done.returncode == 5,
          f"exit {done.returncode}: {done.stdout.strip()}")
    check("...and it says the service could not be reached", "cannot reach" in done.stdout,
          done.stdout.strip())


if __name__ == "__main__":
    sys.exit(main())
