"""Bounded input reporting against an isolated service and the real C++ worker.
Never uses the running service, production accounts, or online release pointers.
"""
import http.cookiejar
import hashlib
import json
import os
from pathlib import Path
import tempfile
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import urllib.error
import urllib.parse
import urllib.request
import uuid

import auth_matrix as am
import admin_matrix as admin

am.SERVER = Path(os.environ.get("CHISPA_TEST_SERVER", str(am.ROOT / ".local/maintenance-check/server/Chispa.Server.dll")))
checks = 0

def check(name, ok):
    global checks
    assert ok, name
    checks += 1
    print("PASS " + name, flush=True)

def call(h, payload=None, token=None, device=None, method="POST", path="/api/v1/diagnostics", raw=None):
    headers = {"Content-Type": "application/json"}
    if token: headers["Authorization"] = "Bearer " + token
    if device: headers["X-Chispa-Device"] = device
    body = raw if raw is not None else (json.dumps(payload).encode() if payload is not None else None)
    req = urllib.request.Request(h.base + path, body, headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=10) as r: return r.status, r.read().decode()
    except urllib.error.HTTPError as e: return e.code, e.read().decode()

def main():
    root = am.ROOT / ".local/maintenance-check/temp"
    root.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="input-reports-", dir=root) as directory:
        h = am.Harness(Path(directory))
        try:
            h.prepare_data()
            made = h.cli("admin-create", admin.ADMIN, stdin=admin.ADMIN_PASSWORD + "\n")
            assert made.returncode == 0, "fixture admin creation"
            h.build_selftest()
            h.start_server()
            status, data = call(h, method="GET", path="/api/v1/launch/capabilities")
            check("service advertises bounded diagnostic uploads", status == 200 and json.loads(data)["diagnosticReports"])
            payload = {"id": uuid.uuid4().hex, "summary": "HostInput: <script>alert('x')</script>", "logs": "HostInput: Windows build=19045\n"}
            check("anonymous upload refused", call(h, payload)[0] == 401)
            session = h.login()
            check("loader session cannot impersonate a running program", call(h, payload, session)[0] == 401)
            device = "a" * 32
            status, data = call(h, {"ticket": h.issue_ticket(session), "programId": am.PROGRAM,
                                  "version": am.VERSION, "device": device}, path="/api/v1/launch/redeem")
            assert status == 200, "fixture launch lease"
            lease = json.loads(data)["lease"]
            check("wrong device refused", call(h, payload, lease, "b" * 32)[0] == 401)
            check("malformed JSON refused", call(h, token=lease, device=device, raw=b"{")[0] == 400)
            check("path-like report id refused", call(h, {**payload, "id": "../escape"}, lease, device)[0] == 400)
            check("log content capped at 16 KiB", call(h, {**payload, "logs": "x" * (16 * 1024 + 1)}, lease, device)[0] == 400)
            check("request body capped before parsing", call(h, token=lease, device=device, raw=b" " * (129 * 1024))[0] == 413)
            status, data = call(h, {**payload, "account": "forged", "version": "9.9.9"}, lease, device)
            report_files = list((h.data / "diagnostics").glob("*.json"))
            saved = json.loads(report_files[0].read_text())
            check("report persisted with authoritative account and version", status == 201 and
                  saved["account"] == am.ACCOUNT and saved["version"] == am.VERSION)
            check("report paths use opaque account keys", am.ACCOUNT not in report_files[0].name and
                  report_files[0].name.startswith(hashlib.sha256(am.ACCOUNT.upper().encode()).hexdigest() + "--"))
            check("lease and device credentials never persisted", lease not in report_files[0].read_text() and device not in report_files[0].read_text())
            check("retry is idempotent even during cooldown", call(h, payload, lease, device)[0] == 200 and len(report_files) == 1)
            check("new reports rate limited per account", call(h, {**payload, "id": uuid.uuid4().hex}, lease, device)[0] == 429)
            _, _, page = admin.call(admin.DIRECT, "GET", h.base + "/admin/diagnostics")
            check("unauthenticated visitor cannot read reports", "alert('x')" not in page and "HostInput:" not in page)
            jar = http.cookiejar.CookieJar()
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPCookieProcessor(jar))
            _, _, login = admin.call(opener, "GET", h.base + "/admin/login")
            form = urllib.parse.urlencode({"__RequestVerificationToken": admin.token_of(login),
                                            "username": admin.ADMIN, "password": admin.ADMIN_PASSWORD}).encode()
            admin.call(opener, "POST", h.base + "/admin/login", body=form, headers={"Content-Type": "application/x-www-form-urlencoded"})
            status, _, page = admin.call(opener, "GET", h.base + "/admin/diagnostics?account=" + am.ACCOUNT)
            check("admin can find report by account and HTML is escaped", status == 200 and
                  "&lt;script&gt;" in page and "<script>alert" not in page and am.ACCOUNT in page)
            _, _, page = admin.call(opener, "GET", h.base + "/admin/diagnostics?account=nonexistent")
            check("account filter excludes unrelated reports", "HostInput:" not in page)
            check("lease withdrawal accepted", call(h, token=lease, method="DELETE", path="/api/v1/launch/lease")[0] == 204)
            check("withdrawn lease cannot upload", call(h, payload, lease, device)[0] == 401)
            # Allow the next report without waiting a minute; touches only this isolated fixture.
            os.utime(report_files[0], (time.time() - 61, time.time() - 61))
            auth_dir = Path(directory) / "auth"
            h.write_ticket(h.renew_ticket(), auth_dir)
            result = h.run_selftest(["run", "--monitor", "15", "--diagnostic-fault", "1"], auth_dir, timeout=45)
            check("real C++ background worker uploads without stopping heartbeat", result.returncode == 0 and
                  "input fault report uploaded" in result.stdout and "FINAL allowed=1" in result.stdout)
            files = list((h.data / "diagnostics").glob("*.json"))
            texts = [f.read_text() for f in files]
            check("automatic report contains failure and Windows build, excludes auth text", len(files) == 2 and
                  any("only 0 of 2973696" in text and "build=19045" in text for text in texts) and
                  all("do-not-upload" not in text for text in texts))
            # Local fault proxy: exercise actual retry timing and old-server compatibility.
            backend = h.base
            class Proxy(BaseHTTPRequestHandler):
                mode = "retry"
                uploads = 0
                ids = []
                def log_message(self, *_): pass
                def do_GET(self): self.forward()
                def do_POST(self): self.forward()
                def do_DELETE(self): self.forward()
                def forward(self):
                    body = self.rfile.read(int(self.headers.get("Content-Length", 0)))
                    if self.path == "/api/v1/diagnostics":
                        type(self).uploads += 1
                        type(self).ids.append(json.loads(body)["id"])
                        if type(self).uploads == 1:
                            self.send_response(503); self.end_headers(); return
                    headers = {key: value for key, value in self.headers.items() if key.lower() not in ("host", "content-length", "connection")}
                    req = urllib.request.Request(backend + self.path, body if body else None, headers, method=self.command)
                    try:
                        with urllib.request.urlopen(req, timeout=10) as response: status, data = response.status, response.read()
                    except urllib.error.HTTPError as e: status, data = e.code, e.read()
                    if self.path.endswith("/capabilities") and type(self).mode == "old":
                        value = json.loads(data); value.pop("diagnosticReports", None); data = json.dumps(value).encode()
                    self.send_response(status); self.send_header("Content-Type", "application/json"); self.send_header("Content-Length", str(len(data))); self.end_headers(); self.wfile.write(data)
            proxy = ThreadingHTTPServer(("127.0.0.1", 0), Proxy)
            proxy.daemon_threads = True
            thread = threading.Thread(target=proxy.serve_forever, daemon=True); thread.start()
            try:
                h.base = f"http://127.0.0.1:{proxy.server_port}"
                h.build_selftest()
                for f in files: os.utime(f, (time.time() - 61,) * 2)
                auth_dir = Path(directory) / "retry-auth"
                h.write_ticket(h.renew_ticket(), auth_dir)
                result = h.run_selftest(["run", "--monitor", "46", "--diagnostic-fault", "1"], auth_dir, timeout=70)
                check("503 retried in background with same report id and live heartbeat", result.returncode == 0 and
                      "input fault report uploaded" in result.stdout and Proxy.uploads == 2 and len(set(Proxy.ids)) == 1)
                Proxy.mode = "old"; Proxy.uploads = 0
                auth_dir = Path(directory) / "old-service-auth"
                h.write_ticket(h.renew_ticket(), auth_dir)
                result = h.run_selftest(["run", "--monitor", "12", "--diagnostic-fault", "1"], auth_dir, timeout=30)
                check("old service starts normally with no upload attempts", result.returncode == 0 and Proxy.uploads == 0)
            finally:
                h.base = backend; proxy.shutdown(); proxy.server_close(); thread.join(timeout=5)
            # Retention checks use actual on-disk reports and the real timer on restart.
            account_key = hashlib.sha256(am.ACCOUNT.upper().encode()).hexdigest()
            for i in range(12):
                record = {**saved, "id": uuid.uuid4().hex}
                path = h.data / "diagnostics" / (account_key + "--" + record["id"] + ".json")
                path.write_text(json.dumps(record))
                os.utime(path, (time.time() - 120 - i, time.time() - 120 - i))
            expired = h.data / "diagnostics" / ("olduser--" + uuid.uuid4().hex + ".json")
            expired.write_text(json.dumps(saved)); os.utime(expired, (time.time() - 8 * 86400,) * 2)
            for i in range(510):
                path = h.data / "diagnostics" / (f"fixture{i:04}--" + uuid.uuid4().hex + ".json")
                path.write_text(json.dumps(saved)); os.utime(path, (time.time() - 300 - i,) * 2)
            h.stop_server(); h.start_server()
            deadline = time.time() + 5
            while expired.exists() and time.time() < deadline: time.sleep(.1)
            check("retention removes expired reports and caps each account at ten", not expired.exists() and
                  len(list((h.data / "diagnostics").glob(account_key + "--*.json"))) == 10)
            check("global archive capped at 500 reports", len(list((h.data / "diagnostics").glob("*.json"))) == 500)
        finally:
            h.stop_server()
    print(f"{checks} checks passed; isolated files removed.", flush=True)

if __name__ == "__main__": main()
