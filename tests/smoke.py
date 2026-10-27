"""Local service integration smoke test. No real Chispa executable is run."""

import hashlib
import base64
import html
import io
import json
import os
from pathlib import Path
import re
import socket
import subprocess
import tempfile
import time
from datetime import datetime, timedelta, timezone
from http.cookiejar import CookieJar
import urllib.error
import urllib.parse
import urllib.request
import zipfile


ROOT = Path(__file__).resolve().parents[1]
DOTNET = ROOT / ".tooling/dotnet/dotnet.exe"
if not DOTNET.exists():
    DOTNET = "dotnet"
pointer = ROOT / ".local/current-server-build.txt"
SERVER = (ROOT / ".local/server-builds" / pointer.read_text().strip() / "Chispa.Server.dll"
          if pointer.exists() else ROOT / "server/bin/Release/net10.0/Chispa.Server.dll")
CLIENT_SMOKE = ROOT / "tests/bin/Release/net10.0-windows/Chispa.Smoke.dll"


def request(url, payload=None, token=None, method=None):
    headers = {}
    if payload is not None:
        headers["Content-Type"] = "application/json"
    if token:
        headers["Authorization"] = "Bearer " + token
    body = json.dumps(payload).encode() if payload is not None else None
    return urllib.request.urlopen(urllib.request.Request(url, data=body, headers=headers, method=method), timeout=5)


def main():
    if not SERVER.exists():
        raise SystemExit("Build the Release server first: ./build.ps1")
    if not CLIENT_SMOKE.exists():
        raise SystemExit("Build the client smoke test first: dotnet build tests/Chispa.Smoke.csproj -c Release")
    (ROOT / ".local").mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=ROOT / ".local") as temporary:
        temp = Path(temporary)
        env = os.environ.copy()
        env["CHISPA_SERVER_DATA"] = str(temp / "data")
        # This suite deliberately signs in many accounts in seconds; limits have a dedicated matrix.
        env["CHISPA_LIMIT_LOGIN_PER_MINUTE"] = "600"
        env["DOTNET_CLI_HOME"] = str(ROOT / ".tooling/home")
        kwargs = {"env": env, "check": True, "capture_output": True, "text": True}

        # Every release is signed, and the test uses its own throwaway key: the server verifies
        # uploads against the public half, and the loader (Chispa.Smoke) is pointed at the same
        # file so its compiled-in trust anchors are replaced for the duration of the test.
        signer = ROOT / "tools/Chispa.Sign/bin/Release/net10.0/Chispa.Sign.dll"
        if not signer.exists():
            raise SystemExit("Build the signing tool first: ./build.ps1")
        keys = temp / "signing"
        made = subprocess.run([str(DOTNET), str(signer), "keygen", "--out", str(keys), "--id", "smoke-key"],
                              env=env, capture_output=True, text=True, encoding="utf-8", errors="replace")
        if made.returncode:
            raise AssertionError(made.stdout + made.stderr)
        trust_path = keys / "release-public.json"
        env["CHISPA_RELEASE_TRUST_KEYS"] = str(trust_path)
        env["CHISPA_TRUST_KEYS"] = str(trust_path)

        def pack_and_sign(program_id, version, folder, executable, out_dir=None):
            """Package and sign exactly as the release environment does."""
            out_dir = out_dir or temp
            archive = out_dir / f"{program_id}-{version}.zip"
            signature = out_dir / f"{program_id}-{version}.signature.json"
            packed = subprocess.run([str(DOTNET), str(signer), "pack", "--source", str(folder),
                                     "--out", str(archive), "--executable", executable],
                                    env=env, capture_output=True, text=True,
                                    encoding="utf-8", errors="replace")
            if packed.returncode:
                raise AssertionError(packed.stdout + packed.stderr)
            signed = subprocess.run([str(DOTNET), str(signer), "sign", "--package", str(archive),
                                     "--program", program_id, "--version", version, "--executable", executable,
                                     "--key", str(keys / "release-private.pem"), "--key-id", "smoke-key",
                                     "--out", str(signature)], env=env, capture_output=True, text=True,
                                    encoding="utf-8", errors="replace")
            if signed.returncode:
                raise AssertionError(signed.stdout + signed.stderr)
            return archive, signature

        def sign_zip(program_id, version, archive, executable):
            """Sign an archive that already exists (used for tampering tests)."""
            signature = archive.with_suffix(".signature.json")
            signed = subprocess.run([str(DOTNET), str(signer), "sign", "--package", str(archive),
                                     "--program", program_id, "--version", version, "--executable", executable,
                                     "--key", str(keys / "release-private.pem"), "--key-id", "smoke-key",
                                     "--out", str(signature)], env=env, capture_output=True, text=True,
                                    encoding="utf-8", errors="replace")
            if signed.returncode:
                raise AssertionError(signed.stdout + signed.stderr)
            return signature.read_text(encoding="utf-8")
        added = subprocess.run([str(DOTNET), str(SERVER), "user-add", "testuser"],
                               input="correct horse battery staple\n", **{**kwargs, "check": False})
        if added.returncode:
            raise AssertionError(added.stdout + added.stderr)
        users_path = temp / "data/users.json"
        legacy_users = json.loads(users_path.read_text())
        legacy_users[0].pop("accessMode", None)
        legacy_users[0].pop("programIds", None)
        legacy_users[0].pop("passwordCiphertext", None)
        users_path.write_text(json.dumps(legacy_users))
        admin = subprocess.run([str(DOTNET), str(SERVER), "admin-create", "manager"],
                               input="an excellent admin password\n", **{**kwargs, "check": False})
        if admin.returncode:
            raise AssertionError(admin.stdout + admin.stderr)
        for program_id, name, executable in (("chispa", "Chispa", "Wardogs.exe"),
                                             ("helper", "Helper", "Helper.exe")):
            registered = subprocess.run([str(DOTNET), str(SERVER), "program-add", program_id, name, executable],
                                        **{**kwargs, "check": False})
            if registered.returncode:
                raise AssertionError(registered.stdout + registered.stderr)

        runtime = temp / "runtime"
        (runtime / "assets").mkdir(parents=True)
        for name in ("Wardogs.exe", "vmm.dll", "leechcore.dll", "FTD3XX.dll", "info.db"):
            (runtime / name).write_bytes(b"smoke-test-" + name.encode())
        (runtime / "assets/test.txt").write_text("asset", encoding="utf-8")
        archive, signature = pack_and_sign("chispa", "1.2.3", runtime, "Wardogs.exe")
        published = subprocess.run([str(DOTNET), str(SERVER), "publish", "chispa", "1.2.3",
                                    str(archive), str(signature)],
                                   **{**kwargs, "check": False})
        if published.returncode:
            raise AssertionError(published.stdout + published.stderr)

        helper = temp / "helper"
        helper.mkdir()
        (helper / "Helper.exe").write_bytes(b"smoke-test-helper")
        (helper / "data.txt").write_text("helper data", encoding="utf-8")
        archive, signature = pack_and_sign("helper", "2.0.0", helper, "Helper.exe")
        published = subprocess.run([str(DOTNET), str(SERVER), "publish", "helper", "2.0.0",
                                    str(archive), str(signature)],
                                   **{**kwargs, "check": False})
        if published.returncode:
            raise AssertionError(published.stdout + published.stderr)

        with socket.socket() as s:
            s.bind(("127.0.0.1", 0))
            port = s.getsockname()[1]
        base = f"http://127.0.0.1:{port}"
        env["CHISPA_SERVER_URL"] = base
        hidden = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        server = subprocess.Popen([str(DOTNET), str(SERVER)], env=env,
                                  stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                  creationflags=hidden)
        try:
            for _ in range(100):
                try:
                    with request(base + "/health") as response:
                        if response.status == 200:
                            break
                except urllib.error.URLError:
                    time.sleep(0.1)
            else:
                raise AssertionError("Server did not start")

            with request(base + "/") as response:
                assert response.status == 200
                assert "服务端正在运行" in response.read().decode("utf-8")

            browser = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(CookieJar()))
            def form_token(page):
                match = re.search(r"name='__RequestVerificationToken' value='([^']+)'", page)
                assert match, "CSRF token not found"
                return html.unescape(match.group(1))

            with browser.open(base + "/admin") as response:
                login_page = response.read().decode("utf-8")
                assert response.url.startswith(base + "/admin/login")
            login_form = urllib.parse.urlencode({
                "__RequestVerificationToken": form_token(login_page),
                "username": "manager", "password": "an excellent admin password",
            }).encode()
            with browser.open(base + "/admin/login", data=login_form) as response:
                admin_page = response.read().decode("utf-8")
                assert response.url.startswith(base + "/admin")
                assert "创建账号" in admin_page
                assert "script-src 'self'" in response.headers["Content-Security-Policy"]
            for resource, content_type in (("style.css", "text/css"), ("app.js", "text/javascript"), ("chispa.svg", "image/svg+xml")):
                with request(base + "/admin-assets/" + resource) as response:
                    assert response.headers["Content-Type"].startswith(content_type)

            try:
                browser.open(base + "/admin/users", data=urllib.parse.urlencode({
                    "username": "expiring", "password": "long enough password",
                }).encode())
                raise AssertionError("Admin mutation without CSRF succeeded")
            except urllib.error.HTTPError as error:
                assert error.code == 400

            tomorrow = (datetime.now(timezone.utc) + timedelta(days=1)).date().isoformat()
            create_form = urllib.parse.urlencode({
                "__RequestVerificationToken": form_token(admin_page),
                "username": "expiring", "password": "long enough password",
                "expiry": (datetime.now().astimezone() + timedelta(days=1)).strftime("%Y-%m-%dT%H:%M"),
            }).encode()
            with browser.open(base + "/admin/users", data=create_form) as response:
                admin_page = response.read().decode("utf-8")
                assert "expiring" in admin_page

            try:
                request(base + "/api/v1/programs")
                raise AssertionError("Unauthenticated catalog request succeeded")
            except urllib.error.HTTPError as error:
                assert error.code == 401

            try:
                request(base + "/api/v1/session", {"username": "testuser", "password": "wrong"})
                raise AssertionError("Wrong password succeeded")
            except urllib.error.HTTPError as error:
                assert error.code == 401

            with request(base + "/api/v1/session", {"username": "testuser", "password": "correct horse battery staple"}) as response:
                token = json.load(response)["token"]
            with request(base + "/api/v1/session", {"username": "expiring", "password": "long enough password"}) as response:
                expiring_token = json.load(response)["token"]
            with request(base + "/api/v1/programs", token=token) as response:
                programs = json.load(response)
            assert {p["id"] for p in programs} == {"chispa", "helper"}
            with request(base + "/api/v1/programs/chispa/releases/latest", token=token) as response:
                release = json.load(response)
            assert release["version"] == "1.2.3"
            assert release["executable"] == "Wardogs.exe"
            with request(base + "/api/v1/programs/chispa/releases/1.2.3/package", token=token) as response:
                package = response.read()
            assert len(package) == release["size"]
            assert hashlib.sha256(package).hexdigest() == release["sha256"]
            with zipfile.ZipFile(io.BytesIO(package)) as archive:
                assert "Wardogs.exe" in archive.namelist()
                assert "assets/test.txt" in archive.namelist()
            with request(base + "/api/v1/programs/helper/releases/latest", token=token) as response:
                helper_release = json.load(response)
            assert helper_release["version"] == "2.0.0"
            launch_path = "/api/v1/programs/chispa/releases/1.2.3/launch"
            with request(base + launch_path, token=token, method="POST") as response:
                launch_release = json.load(response)
                assert response.headers["Cache-Control"] == "no-store"
            assert launch_release["binaries"]["Wardogs.exe"].lower() == hashlib.sha256(b"smoke-test-Wardogs.exe").hexdigest()
            assert "vmm.dll" in launch_release["binaries"]
            assert "info.db" not in launch_release["binaries"]
            try:
                request(base + launch_path, method="POST")
                raise AssertionError("Unauthenticated launch was authorized")
            except urllib.error.HTTPError as error:
                assert error.code == 401
            with request(base + "/api/v1/programs/helper/releases/2.0.0/package", token=token) as response:
                helper_package = response.read()
            assert hashlib.sha256(helper_package).hexdigest() == helper_release["sha256"]

            yesterday = (datetime.now(timezone.utc) - timedelta(days=1)).date().isoformat()
            expire_form = urllib.parse.urlencode({
                "__RequestVerificationToken": form_token(admin_page), "expiryDate": yesterday,
            }).encode()
            with browser.open(base + "/admin/users/expiring/expiry", data=expire_form) as response:
                admin_page = response.read().decode("utf-8")
                assert "已到期" in admin_page
            try:
                request(base + launch_path, token=expiring_token, method="POST")
                raise AssertionError("Expired account could authorize launch")
            except urllib.error.HTTPError as error:
                assert error.code == 401
            with request(base + "/api/v1/programs", token=expiring_token) as response:
                assert response.status == 200  # expired subscriptions may still browse the library
            with request(base + "/api/v1/session", token=expiring_token) as response:
                assert datetime.fromisoformat(json.load(response)["accountExpiresAt"]) < datetime.now(timezone.utc)

            permanent_form = urllib.parse.urlencode({
                "__RequestVerificationToken": form_token(admin_page), "expiryDate": "",
            }).encode()
            with browser.open(base + "/admin/users/expiring/expiry", data=permanent_form) as response:
                admin_page = response.read().decode("utf-8")
            with request(base + "/api/v1/session", {"username": "expiring", "password": "long enough password"}) as response:
                new_token = json.load(response)["token"]
            delete_form = urllib.parse.urlencode({
                "__RequestVerificationToken": form_token(admin_page),
            }).encode()
            with browser.open(base + "/admin/users/expiring/delete", data=delete_form) as response:
                assert "expiring" not in response.read().decode("utf-8")
            try:
                request(base + "/api/v1/programs", token=new_token)
                raise AssertionError("Deleted user's existing session remained valid")
            except urllib.error.HTTPError as error:
                assert error.code == 401

            reset = subprocess.run([str(DOTNET), str(SERVER), "admin-reset", "manager"],
                                   input="another excellent admin password\n",
                                   **{**kwargs, "check": False})
            if reset.returncode:
                raise AssertionError(reset.stdout + reset.stderr)
            with browser.open(base + "/admin") as response:
                assert response.url.startswith(base + "/admin/login")
            env["CHISPA_INSTALL_ROOT"] = str(temp / "installed")
            env["CHISPA_CREDENTIAL_FILE"] = str(temp / "credentials.json")
            client = subprocess.run([str(DOTNET), str(CLIENT_SMOKE), base],
                                    **{**kwargs, "env": env, "check": False})
            if client.returncode:
                raise AssertionError(client.stdout + client.stderr)
            assert (temp / "installed/versions/1.2.3/Wardogs.exe").exists()
            assert (temp / "installed/programs/helper/versions/2.0.0/Helper.exe").exists()

            with browser.open(base + "/admin/login") as response:
                login_page = response.read().decode("utf-8")
            login_form = urllib.parse.urlencode({
                "__RequestVerificationToken": form_token(login_page),
                "username": "manager", "password": "another excellent admin password",
            }).encode()
            with browser.open(base + "/admin/login", data=login_form) as response:
                admin_page = response.read().decode("utf-8")
            register_form = urllib.parse.urlencode({
                "__RequestVerificationToken": form_token(admin_page),
                "id": "third", "name": "Third", "executable": "Third.exe",
            }).encode()
            with browser.open(base + "/admin/programs", data=register_form) as response:
                admin_page = response.read().decode("utf-8")
                assert "third" in admin_page
            with request(base + "/api/v1/programs", token=token) as response:
                catalog = {p["id"]: p for p in json.load(response)}
                assert set(catalog) == {"chispa", "helper", "third"}
                assert catalog["third"]["hasRelease"] is False
            try:
                request(base + "/api/v1/programs/third/releases/latest", token=token)
                raise AssertionError("Unpublished placeholder had a release")
            except urllib.error.HTTPError as error:
                assert error.code == 404

            def upload_package(program_id, version, payload, signature_text):
                boundary = "chispa-smoke-boundary"
                fields = {
                    "__RequestVerificationToken": form_token(admin_page),
                    "programId": program_id, "version": version,
                }
                body = bytearray()
                for name, value in fields.items():
                    body.extend(f"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n".encode())
                body.extend(f"--{boundary}\r\nContent-Disposition: form-data; name=\"package\"; filename=\"runtime.zip\"\r\nContent-Type: application/zip\r\n\r\n".encode())
                body.extend(payload)
                if signature_text is not None:
                    body.extend(f"\r\n--{boundary}\r\nContent-Disposition: form-data; name=\"signature\"; filename=\"signature.json\"\r\nContent-Type: application/json\r\n\r\n".encode())
                    body.extend(signature_text.encode())
                body.extend(f"\r\n--{boundary}--\r\n".encode())
                req = urllib.request.Request(base + "/admin/releases", data=bytes(body),
                    headers={"Content-Type": "multipart/form-data; boundary=" + boundary})
                with browser.open(req) as response:
                    return response.read().decode("utf-8")

            def make_zip(filename):
                buffer = io.BytesIO()
                with zipfile.ZipFile(buffer, "w") as archive:
                    archive.writestr(filename, b"smoke-test-exe")
                return buffer.getvalue()

            wrong_zip = temp / "wrong.zip"
            wrong_zip.write_bytes(make_zip("Wrong.exe"))
            # The signing tool refuses to sign a package without its entry EXE at the top level,
            # so a "wrong" package cannot even be signed - which is the first line of defence.
            unsigned = upload_package("third", "1.0.0", make_zip("Third.exe"), None)
            assert "请同时上传签名文件" in unsigned, unsigned
            with request(base + "/api/v1/programs", token=token) as response:
                assert len(json.load(response)) == 3
            good_zip = temp / "third-1.0.0.zip"
            good_zip.write_bytes(make_zip("Third.exe"))
            signature_text = sign_zip("third", "1.0.0", good_zip, "Third.exe")
            uploaded = upload_package("third", "1.0.0", good_zip.read_bytes(), signature_text)
            assert "最新版本 1.0.0" in uploaded
            with request(base + "/api/v1/programs/third/releases/latest", token=token) as response:
                third_release = json.load(response)
            assert third_release["version"] == "1.0.0"
            with request(base + "/api/v1/programs/third/releases/1.0.0/package", token=token) as response:
                assert hashlib.sha256(response.read()).hexdigest() == third_release["sha256"]

            # The browser uses bounded chunks; publishing continues after the final request.
            large = io.BytesIO()
            with zipfile.ZipFile(large, "w", compression=zipfile.ZIP_STORED) as archive:
                archive.writestr("Third.exe", b"chunked-exe")
                archive.writestr("assets/data.bin", os.urandom(5 * 1024 * 1024))
            payload = large.getvalue()
            csrf_header = {"X-CSRF-TOKEN": form_token(admin_page)}
            large_archive = temp / "third-1.0.1.zip"
            large_archive.write_bytes(payload)
            large_signature = sign_zip("third", "1.0.1", large_archive, "Third.exe")
            start_request = urllib.request.Request(base + "/admin/uploads",
                data=json.dumps({"programId": "third", "version": "1.0.1", "size": len(payload),
                                 "signature": large_signature}).encode(),
                headers={**csrf_header, "Content-Type": "application/json"})
            with browser.open(start_request) as response:
                upload_session = json.load(response)
            upload_url = base + "/admin/uploads/" + upload_session["id"]
            chunk_size = upload_session["chunkSize"]
            for offset in range(0, len(payload), chunk_size):
                chunk = payload[offset:offset + chunk_size]
                chunk_request = urllib.request.Request(upload_url, data=chunk, method="PUT",
                    headers={**csrf_header, "Content-Type": "application/octet-stream",
                             "X-Upload-Offset": str(offset)})
                with browser.open(chunk_request) as response:
                    assert json.load(response)["received"] == offset + len(chunk)
            # Replaying an acknowledged chunk must not corrupt the file.
            with browser.open(chunk_request) as response:
                assert json.load(response)["received"] == len(payload)
            complete_request = urllib.request.Request(upload_url + "/complete", data=b"",
                headers=csrf_header)
            with browser.open(complete_request) as response:
                assert response.status == 202
            for _ in range(150):
                with browser.open(upload_url) as response:
                    progress = json.load(response)
                if progress["state"] != "processing":
                    break
                time.sleep(0.1)
            assert progress["state"] == "published", progress
            with request(base + "/api/v1/programs/third/releases/latest", token=token) as response:
                assert json.load(response)["version"] == "1.0.1"

            def post_admin(path, fields):
                values = {"__RequestVerificationToken": form_token(admin_page), **fields}
                with browser.open(base + path, data=urllib.parse.urlencode(values, doseq=True).encode()) as response:
                    return response.read().decode("utf-8")

            admin_page = post_admin("/admin/programs", {
                "id": "beta-slot", "name": "Beta Slot", "executable": "Beta.exe",
                "iconText": "🧪", "isBeta": "1", "parentProgramId": "chispa",
            })
            assert "占位，未发布" in admin_page
            admin_page = post_admin("/admin/programs", {
                "id": "tool-slot", "name": "Tool Slot", "executable": "Tool.exe",
            })
            with request(base + "/api/v1/programs", token=token) as response:
                assert {p["id"] for p in json.load(response)} == {"chispa", "helper", "third", "tool-slot"}

            admin_page = post_admin("/admin/programs/third/update", {
                "name": "Third Renamed", "iconText": "🎯", "isBeta": "1",
                "parentProgramId": "helper",
            })
            with request(base + "/api/v1/programs", token=token) as response:
                assert {p["id"] for p in json.load(response)} == {"chispa", "helper", "tool-slot"}
            updated = json.loads((temp / "data/programs/third/program.json").read_text())
            assert updated["name"] == "Third Renamed"
            assert updated["icon"] == "🎯"
            assert updated["isBeta"] is True

            png = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9jlr8AAAAASUVORK5CYII=")
            boundary = "chispa-icon-boundary"
            icon_body = bytearray()
            for name, value in {
                "__RequestVerificationToken": form_token(admin_page),
                "name": "Third Renamed", "isBeta": "1", "parentProgramId": "helper",
            }.items():
                icon_body.extend(f"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n".encode())
            icon_body.extend(f"--{boundary}\r\nContent-Disposition: form-data; name=\"iconFile\"; filename=\"icon.png\"\r\nContent-Type: image/png\r\n\r\n".encode())
            icon_body.extend(png)
            icon_body.extend(f"\r\n--{boundary}--\r\n".encode())
            icon_request = urllib.request.Request(base + "/admin/programs/third/update", data=bytes(icon_body),
                headers={"Content-Type": "multipart/form-data; boundary=" + boundary})
            with browser.open(icon_request) as response:
                admin_page = response.read().decode("utf-8")
                assert "data:image/png;base64," in admin_page
            updated = json.loads((temp / "data/programs/third/program.json").read_text())
            assert updated["icon"].startswith("data:image/png;base64,")

            accounts = [
                ("betauser", "all", [], True),
                ("singleuser", "single", ["helper"], True),
                ("multiuser", "multiple", ["chispa", "helper"], True),
                ("placeholderuser", "single", ["tool-slot"], True),
                ("stableonly", "multiple", ["helper", "tool-slot"], False),
            ]
            for name, mode, ids, beta_access in accounts:
                admin_page = post_admin("/admin/users", {
                    "username": name, "password": "test account password",
                    "accessMode": mode, "programIds": ids,
                    **({"betaAccess": "1"} if beta_access else {}),
                })
                assert name in admin_page
            expected = {
                "betauser": {"chispa", "helper", "third", "beta-slot", "tool-slot"},
                "singleuser": {"helper", "third"},
                "multiuser": {"chispa", "helper", "third", "beta-slot"},
                "placeholderuser": {"tool-slot"},
                "stableonly": {"helper", "tool-slot"},
            }
            account_tokens = {}
            for name, ids in expected.items():
                with request(base + "/api/v1/session", {"username": name, "password": "test account password"}) as response:
                    account_tokens[name] = json.load(response)["token"]
                with request(base + "/api/v1/programs", token=account_tokens[name]) as response:
                    assert {p["id"] for p in json.load(response)} == ids
            with request(base + "/api/v1/programs/third/releases/latest", token=account_tokens["betauser"]) as response:
                assert json.load(response)["version"] == "1.0.1"
            with request(base + "/api/v1/programs/chispa/releases/latest", token=account_tokens["betauser"]) as response:
                assert json.load(response)["version"] == "1.2.3"
            with request(base + "/api/v1/programs/chispa/releases/1.2.3/package", token=account_tokens["betauser"]) as response:
                assert hashlib.sha256(response.read()).hexdigest() == release["sha256"]
            with request(base + "/api/v1/programs/third/releases/latest", token=account_tokens["singleuser"]) as response:
                assert json.load(response)["version"] == "1.0.1"
            for path in ("/api/v1/programs/chispa/releases/latest",
                         "/api/v1/programs/chispa/releases/1.2.3/package"):
                try:
                    request(base + path, token=account_tokens["singleuser"])
                    raise AssertionError("Scoped Beta user accessed an unselected program")
                except urllib.error.HTTPError as error:
                    assert error.code == 403
            for path in ("/api/v1/programs/third/releases/latest",
                         "/api/v1/programs/third/releases/1.0.0/package"):
                try:
                    request(base + path, token=token)
                    raise AssertionError("All-stable user accessed Beta")
                except urllib.error.HTTPError as error:
                    assert error.code == 403
                try:
                    request(base + path, token=account_tokens["stableonly"])
                    raise AssertionError("Selected Beta program bypassed Beta flag")
                except urllib.error.HTTPError as error:
                    assert error.code == 403
            admin_page = post_admin("/admin/users/singleuser/access", {"accessMode": "all"})
            try:
                request(base + "/api/v1/programs/third/releases/1.0.0/launch", token=account_tokens["singleuser"], method="POST")
                raise AssertionError("Revoked Beta access could authorize launch")
            except urllib.error.HTTPError as error:
                assert error.code == 403
            with request(base + "/api/v1/programs", token=account_tokens["singleuser"]) as response:
                assert {p["id"] for p in json.load(response)} == {"chispa", "helper", "tool-slot"}
            admin_page = post_admin("/admin/users/singleuser/access", {"accessMode": "all", "betaAccess": "1"})
            with request(base + "/api/v1/programs", token=account_tokens["singleuser"]) as response:
                assert {p["id"] for p in json.load(response)} == {"chispa", "helper", "third", "beta-slot", "tool-slot"}

            legacy_users = json.loads(users_path.read_text())
            legacy_beta = next(user for user in legacy_users if user["username"] == "betauser")
            legacy_beta["accessMode"] = "beta"
            legacy_beta.pop("betaAccess", None)
            users_path.write_text(json.dumps(legacy_users))
            with request(base + "/api/v1/programs", token=account_tokens["betauser"]) as response:
                assert {p["id"] for p in json.load(response)} == {"chispa", "helper", "third", "beta-slot", "tool-slot"}

            with browser.open(base + "/admin/metrics") as response:
                stats = json.load(response)
            assert stats["requests"] > 10
            assert stats["apiRequests"] > 0
            assert stats["downloads"] >= 3
            assert stats["clientProbes"] >= 1
            assert stats["lastClientProbeAddress"] == "127.0.0.1"
            assert stats["activeSessions"] >= 4
            assert len(stats["lastHour"]) == 60
            with request(base + "/api/v1/session", token=account_tokens["placeholderuser"], method="DELETE") as response:
                assert response.status == 204
            try:
                request(base + "/api/v1/programs", token=account_tokens["placeholderuser"])
                raise AssertionError("Logged-out token remained valid")
            except urllib.error.HTTPError as error:
                assert error.code == 401

            # Expiry keeps hour and minute precision and is shown as plain local time.
            future = datetime.now().astimezone() + timedelta(hours=2)
            admin_page = post_admin("/admin/users", {
                "username": "minuteuser", "password": "minute user password",
                "expiry": future.strftime("%Y-%m-%dT%H:%M"),
            })
            assert "minuteuser" in admin_page
            assert "有效期至" in admin_page and future.strftime("%H:%M") in admin_page
            assert "datetime-local" in admin_page
            with request(base + "/api/v1/session", {"username": "minuteuser", "password": "minute user password"}) as response:
                minute_token = json.load(response)["token"]

            # Password reset requires the explicit confirmation tick, then revokes sessions.
            admin_page = post_admin("/admin/users/minuteuser/password", {
                "password": "a brand new password", "passwordConfirm": "a brand new password"})
            assert "请先勾选" in admin_page
            admin_page = post_admin("/admin/users/minuteuser/password", {
                "enableReset": "1", "password": "a brand new password",
                "passwordConfirm": "a brand new one"})
            assert "两次输入的新密码不一致" in admin_page
            admin_page = post_admin("/admin/users/minuteuser/password", {
                "enableReset": "1", "password": "a brand new password",
                "passwordConfirm": "a brand new password"})
            assert "密码已更新" in admin_page
            saved_users = users_path.read_text()
            assert "a brand new password" not in saved_users
            # Viewing a password now needs a fresh admin re-authentication; that is the point of
            # the step-up, so the smoke test walks the real two-step path.
            import re as _re
            stepup = post_admin("/admin/users/minuteuser/password/stepup", {"adminPassword": "another excellent admin password"})
            grant = _re.search(r"name='grant' value='([^']+)'", stepup)
            assert grant is not None, stepup[:200]
            revealed = post_admin("/admin/users/minuteuser/password/view", {"grant": grant.group(1)})
            assert "a brand new password" in revealed
            stepup = post_admin("/admin/users/testuser/password/stepup", {"adminPassword": "another excellent admin password"})
            grant = _re.search(r"name='grant' value='([^']+)'", stepup)
            legacy_reveal = post_admin("/admin/users/testuser/password/view", {"grant": grant.group(1)})
            assert "旧密码无法还原" in legacy_reveal
            try:
                request(base + "/api/v1/programs", token=minute_token)
                raise AssertionError("Session survived an admin password reset")
            except urllib.error.HTTPError as error:
                assert error.code == 401
            try:
                request(base + "/api/v1/session", {"username": "minuteuser", "password": "minute user password"})
                raise AssertionError("Old password still worked after a reset")
            except urllib.error.HTTPError as error:
                assert error.code == 401
            with request(base + "/api/v1/session", {"username": "minuteuser", "password": "a brand new password"}) as response:
                minute_token = json.load(response)["token"]

            # A past minute expires the account immediately.
            admin_page = post_admin("/admin/users/minuteuser/expiry", {
                "expiry": (datetime.now().astimezone() - timedelta(minutes=1)).strftime("%Y-%m-%dT%H:%M")})
            assert "已到期" in admin_page
            try:
                request(base + "/api/v1/programs/chispa/releases/latest", token=minute_token)
                raise AssertionError("Expired subscription could access a release after a minute-precision expiry")
            except urllib.error.HTTPError as error:
                assert error.code == 401
            admin_page = post_admin("/admin/users/minuteuser/expiry", {"expiry": ""})
            assert "永久" in admin_page

            # Signature policy. The server no longer repairs or normalises an uploaded ZIP, because
            # rewriting bytes would break the signature: a package that needs repairing has to be
            # rebuilt with the signing tool, and one that was tampered with after signing is refused.
            def upload(program_id, version, entries, signature="signed", name="Third.exe"):
                buffer = io.BytesIO()
                with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as archive:
                    for entry_name, data in entries.items():
                        archive.writestr(entry_name, data)
                archive_path = temp / f"upload-{program_id}-{version}.zip"
                archive_path.write_bytes(buffer.getvalue())
                if signature == "signed":
                    signature = sign_zip(program_id, version, archive_path, name)
                return upload_package(program_id, version, buffer.getvalue(), signature)

            # A wrapped folder cannot be signed by the tool in the first place, and a signature
            # fabricated over the raw bytes is what a compromised server would try: either way the
            # package is refused and no release appears.
            wrapped = io.BytesIO()
            with zipfile.ZipFile(wrapped, "w") as archive:
                archive.writestr("Third/Third.exe", b"wrapped-exe")
                archive.writestr("Third/assets/data.bin", b"payload")
            wrapped_path = temp / "wrapped.zip"
            wrapped_path.write_bytes(wrapped.getvalue())
            tool = subprocess.run([str(DOTNET), str(signer), "sign", "--package", str(wrapped_path),
                                   "--program", "third", "--version", "1.1.0", "--executable", "Third.exe",
                                   "--key", str(keys / "release-private.pem"), "--key-id", "smoke-key",
                                   "--out", str(temp / "wrapped.signature.json")],
                                  env=env, capture_output=True, text=True,
                                  encoding="utf-8", errors="replace")
            assert tool.returncode != 0 and "入口文件" in (tool.stderr + tool.stdout), tool.stdout + tool.stderr
            admin_page = upload("third", "1.2.0", {"Third.exe": b"root-exe", "Thumbs.db": b"junk"},
                                signature="signed")
            assert "系统垃圾文件" in admin_page, admin_page
            admin_page = upload("third", "1.3.0", {"top/middle/Third.exe": b"too deep"}, signature=None)
            assert "请同时上传签名文件" in admin_page, admin_page
            # Tampering after signing: same manifest, different bytes.
            tampered = io.BytesIO()
            with zipfile.ZipFile(tampered, "w") as archive:
                archive.writestr("Third.exe", b"original-exe")
            tampered_path = temp / "tampered.zip"
            tampered_path.write_bytes(tampered.getvalue())
            tampered_signature = sign_zip("third", "1.4.0", tampered_path, "Third.exe")
            swapped = io.BytesIO()
            with zipfile.ZipFile(swapped, "w") as archive:
                archive.writestr("Third.exe", b"swapped-exe")
            admin_page = upload("third", "1.4.0", {"Third.exe": b"swapped-exe"},
                                signature=tampered_signature)
            assert "签名" in admin_page or "哈希" in admin_page, admin_page
            # Nothing was published: the tampered release directory does not exist at all.
            assert not (temp / "data/programs/third/releases/1.4.0").exists()

            # Deleting a program removes its packages and prunes account scopes.
            assert (temp / "data/programs/third").exists()
            admin_page = post_admin("/admin/programs/third/delete", {})
            assert "程序及其发布包已删除" in admin_page
            assert not (temp / "data/programs/third").exists()
            admin_page = post_admin("/admin/programs/third/delete", {})
            assert "程序不存在" in admin_page
            with request(base + "/api/v1/programs", token=token) as response:
                assert {p["id"] for p in json.load(response)} == {"chispa", "helper", "tool-slot"}

            admin_page = post_admin("/admin/programs", {
                "id": "helper-beta", "name": "Helper Beta", "executable": "HelperBeta.exe",
                "isBeta": "1", "parentProgramId": "helper"})
            admin_page = post_admin("/admin/programs/helper/delete", {})
            assert "程序及其发布包已删除" in admin_page
            orphan = json.loads((temp / "data/programs/helper-beta/program.json").read_text())
            assert orphan["parentProgramId"] is None, orphan
            admin_page = post_admin("/admin/programs/tool-slot/delete", {})
            assert "程序及其发布包已删除" in admin_page
            assert "tool-slot" not in admin_page
            scopes = {user["username"]: (user.get("accessMode"), user.get("programIds") or [])
                      for user in json.loads(users_path.read_text())}
            assert scopes["multiuser"] == ("single", ["chispa"]), scopes["multiuser"]
            assert scopes["stableonly"] == ("none", []), scopes["stableonly"]
            with request(base + "/api/v1/programs", token=account_tokens["multiuser"]) as response:
                assert {p["id"] for p in json.load(response)} == {"chispa", "beta-slot"}
            with request(base + "/api/v1/programs", token=account_tokens["stableonly"]) as response:
                assert json.load(response) == []

            print(client.stdout.strip())
            print("PASS: admin login, CSRF, account lifecycle and password reset revocation")
            print("PASS: login, hidden catalog, two releases, download and SHA-256")
            print("PASS: admin program registration and ZIP upload validation")
            print("PASS: metrics, program editing, beta and scoped account access")
            print("PASS: launch authorization checks expiry and live permissions; logout revokes token")
            print("PASS: expiry keeps minutes, admin password reset needs confirmation and revokes sessions")
            print("PASS: unsigned, wrapped and post-signing-tampered uploads are refused; deleting programs prunes scopes and packages")
        finally:
            server.terminate()
            server.wait(timeout=5)


if __name__ == "__main__":
    main()
