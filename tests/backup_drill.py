"""Backup and restore drill (task 05).

Uses throwaway accounts, a throwaway signing key and a throwaway package. Nothing here touches
.local/server or any running instance.

  A. back up a live data directory (accounts with different scopes + a signed release + the key ring)
  B. destroy that directory the way corruption would (accounts, release, keys)
  C. restore into a NEW directory and verify it: admin login, account login, permissions, download
  D. verify the restored copy independently, then confirm the original directory was not touched
  E. a corrupted backup is refused
  F. a half-published (internally inconsistent) data directory is refused
  G. retention keeps the configured number of backups
  H. restore refuses an existing non-empty target

Run: python tests/backup_drill.py
"""

import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
DOTNET = ROOT / ".tooling/dotnet/dotnet.exe"
if not DOTNET.exists():
    DOTNET = "dotnet"
pointer = ROOT / ".local/current-server-build.txt"
SERVER = (ROOT / ".local/server-builds" / pointer.read_text().strip() / "Chispa.Server.dll"
          if pointer.exists() else ROOT / "server/bin/Release/net10.0/Chispa.Server.dll")
SIGNER = ROOT / "tools/Chispa.Sign/bin/Release/net10.0/Chispa.Sign.dll"
BACKUP = ROOT / "tools/backup-server.ps1"
RESTORE = ROOT / "tools/restore-backup.ps1"
ADMIN = "restoreadmin"
ADMIN_PASSWORD = "an excellent admin password"
USER = "scopeduser"
USER_PASSWORD = "correct horse battery staple"
RESULTS = []


def check(name, passed, detail=""):
    RESULTS.append((name, bool(passed), detail))
    print(("PASS " if passed else "FAIL ") + name + ("" if passed else f"  [{str(detail)[:500]}]"))


def free_port():
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def run(arguments, env, stdin=None, timeout=300):
    return subprocess.run(arguments, env=env, input=stdin, capture_output=True, text=True,
                          encoding="utf-8", errors="replace", timeout=timeout)


def tree_hashes(directory: Path):
    import hashlib
    hashes = {}
    for path in sorted(directory.rglob("*")):
        if path.is_file():
            hashes[str(path.relative_to(directory))] = hashlib.sha256(path.read_bytes()).hexdigest()
    return hashes


class Drill:
    def __init__(self, work: Path):
        self.work = work
        self.prod = work / "prod"
        self.backups = work / "backups"
        self.restored = work / "restored"
        self.env = os.environ.copy()
        self.env["DOTNET_CLI_HOME"] = str(ROOT / ".tooling/home")
        self.env["TEMP"] = str(ROOT / ".tooling/temp")
        self.env["TMP"] = str(ROOT / ".tooling/temp")
        self.env["CHISPA_SERVER_DATA"] = str(self.prod)
        self.port = free_port()
        self.base = f"http://127.0.0.1:{self.port}"

    # ---- data preparation ---------------------------------------------------------------
    def build_data(self):
        self.prod.mkdir(parents=True)
        for arguments, stdin in (
            (["admin-create", ADMIN], ADMIN_PASSWORD + "\n"),
            (["user-add", USER], USER_PASSWORD + "\n"),
            (["program-add", "chispa", "Chispa", "Wardogs.exe"], None),
            (["program-add", "other", "Other", "Other.exe"], None),
        ):
            done = run([str(DOTNET), str(SERVER), *arguments], self.env, stdin)
            if done.returncode:
                raise SystemExit(done.stdout + done.stderr)

        # A scope that the restore has to preserve: this account may only see "chispa".
        users_path = self.prod / "users.json"
        users = json.loads(users_path.read_text(encoding="utf-8"))
        for user in users:
            if user["username"] == USER:
                user["accessMode"] = "single"
                user["programIds"] = ["chispa"]
        users_path.write_text(json.dumps(users), encoding="utf-8")

        keys = self.work / "signing"
        self.env["CHISPA_RELEASE_TRUST_KEYS"] = str(keys / "release-public.json")
        done = run([str(DOTNET), str(SIGNER), "keygen", "--out", str(keys), "--id", "drill-key"], self.env)
        if done.returncode:
            raise SystemExit(done.stdout + done.stderr)
        shutil.copy(keys / "release-public.json", self.prod / "release-trust.json")

        runtime = self.work / "runtime"
        runtime.mkdir()
        (runtime / "Wardogs.exe").write_bytes(b"drill-wardogs")
        (runtime / "helper.dll").write_bytes(b"drill-helper")
        package = self.work / "package.zip"
        signature = self.work / "signature.json"
        for arguments in (
            ["pack", "--source", str(runtime), "--out", str(package), "--executable", "Wardogs.exe"],
            ["sign", "--package", str(package), "--program", "chispa", "--version", "1.0.0",
             "--executable", "Wardogs.exe", "--key", str(keys / "release-private.pem"),
             "--key-id", "drill-key", "--out", str(signature)],
        ):
            done = run([str(DOTNET), str(SIGNER), *arguments], self.env)
            if done.returncode:
                raise SystemExit(done.stdout + done.stderr)
        done = run([str(DOTNET), str(SERVER), "publish", "chispa", "1.0.0", str(package), str(signature)], self.env)
        if done.returncode:
            raise SystemExit(done.stdout + done.stderr)
        self.package_sha = __import__("hashlib").sha256(package.read_bytes()).hexdigest()

    # ---- backup / restore ----------------------------------------------------------------
    def backup(self, data_root, target=None, keep=14, allow_repo=True, include=None):
        arguments = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(BACKUP),
                     "-DataRoot", str(data_root), "-Target", str(target or self.backups), "-Keep", str(keep)]
        if include:
            arguments += ["-IncludePath", str(include)]
        if allow_repo:
            arguments.append("-AllowRepositoryTarget")
        return run(arguments, self.env)

    def restore(self, backup_dir, target, admin_user=ADMIN, user_account=USER, extra=None,
                extra_target=None):
        env = dict(self.env)
        env["CHISPA_RESTORE_ADMIN_PASSWORD"] = ADMIN_PASSWORD
        env["CHISPA_RESTORE_USER_PASSWORD"] = USER_PASSWORD
        arguments = ["powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(RESTORE),
                     "-Backup", str(backup_dir), "-Target", str(target), "-ServerDll", str(SERVER)]
        if admin_user:
            arguments += ["-AdminUser", admin_user]
        if user_account:
            arguments += ["-UserAccount", user_account]
        if extra_target:
            arguments += ["-ExtraTarget", str(extra_target)]
        if extra:
            arguments += extra
        return run(arguments, env, timeout=420)


def http(method, url, headers=None, body=None, opener=None):
    request = urllib.request.Request(url, data=body, headers=headers or {}, method=method)
    direct = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with (opener or direct).open(request, timeout=20) as response:
            return response.status, response.headers, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.headers, error.read()


def independent_verification(drill: Drill):
    """Start the restored data on its own port and check it with the HTTP API directly."""
    env = dict(drill.env)
    env["CHISPA_SERVER_DATA"] = str(drill.restored)
    env["CHISPA_SERVER_URL"] = drill.base
    env["CHISPA_TRUSTED_PROXIES"] = "127.0.0.1"
    process = subprocess.Popen([str(DOTNET), str(SERVER)], env=env, stdout=subprocess.DEVNULL,
                               stderr=subprocess.DEVNULL)
    try:
        ready = False
        for _ in range(100):
            try:
                if http("GET", drill.base + "/ready")[0] == 200:
                    ready = True
                    break
            except OSError:
                pass
            time.sleep(0.2)
        check("恢复后的数据能独立启动服务", ready, drill.base)
        if not ready:
            return

        status, _, body = http("POST", drill.base + "/api/v1/session",
                               headers={"Content-Type": "application/json"},
                               body=json.dumps({"username": USER, "password": USER_PASSWORD}).encode())
        check("恢复后账号能用原密码登录", status == 200, f"{status} {body[:120]}")
        if status != 200:
            return
        token = json.loads(body)["token"]
        status, _, body = http("GET", drill.base + "/api/v1/programs",
                               headers={"Authorization": "Bearer " + token})
        programs = json.loads(body)
        check("恢复后账号的授权范围仍然是单程序", [p["id"] for p in programs] == ["chispa"],
              [p["id"] for p in programs])

        status, _, body = http("GET", drill.base + "/api/v1/programs/chispa/releases/latest",
                               headers={"Authorization": "Bearer " + token})
        release = json.loads(body)
        status, _, package = http("GET", drill.base + "/api/v1/programs/chispa/releases/1.0.0/package",
                                  headers={"Authorization": "Bearer " + token})
        import hashlib
        digest = hashlib.sha256(package).hexdigest()
        check("恢复后下载的发布包与清单哈希一致",
              status == 200 and digest == release["sha256"] and digest == drill.package_sha,
              f"{status} {digest[:16]}")

        # The signature that travelled with the release still verifies against the trust anchor
        # that travelled with it: a restore is only useful if the signed material is intact.
        signature_path = drill.restored / "programs/chispa/releases/1.0.0/signature.json"
        trust_path = drill.restored / "release-trust.json"
        package_path = drill.work / "downloaded.zip"
        package_path.write_bytes(package)
        done = run([str(DOTNET), str(SIGNER), "verify", "--package", str(package_path),
                    "--signature", str(signature_path), "--trust", str(trust_path)], drill.env)
        check("恢复后发布包的签名仍然有效", done.returncode == 0, done.stdout + done.stderr)
    finally:
        process.terminate()
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill()


def foreign_keyring_scenario(drill: Drill, restored: Path, work: Path):
    """What a restore looks like when the key ring cannot be decrypted (another machine, another
    Windows account): account logins and data survive, the DPAPI-protected password ciphertexts
    do not. This machine cannot host a second identity, so the key ring is replaced - which is the
    same failure the operator would hit."""
    print("-- foreign key ring (cross-machine equivalent)")
    foreign = work / "foreign"
    shutil.copytree(restored, foreign)
    for key in (foreign / "keys").glob("key-*.xml"):
        key.unlink()

    env = dict(drill.env)
    env["CHISPA_SERVER_DATA"] = str(foreign)
    port = free_port()
    base = f"http://127.0.0.1:{port}"
    env["CHISPA_SERVER_URL"] = base
    env["CHISPA_TRUSTED_PROXIES"] = "127.0.0.1"
    process = subprocess.Popen([str(DOTNET), str(SERVER)], env=env, stdout=subprocess.DEVNULL,
                               stderr=subprocess.DEVNULL)
    try:
        for _ in range(100):
            try:
                if http("GET", base + "/ready")[0] == 200:
                    break
            except OSError:
                pass
            time.sleep(0.2)
        # `import http.cookiejar` would shadow the module-level `http()` helper in this scope.
        import http.cookiejar as cookiejar
        jar = cookiejar.CookieJar()
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}),
                                             urllib.request.HTTPCookieProcessor(jar))
        status, _, page = http("GET", base + "/admin/login", opener=opener)
        import re
        import urllib.parse as urllib_parse
        token = re.search(r"name='__RequestVerificationToken' value='([^']+)'",
                          page.decode("utf-8", "replace")).group(1)
        form = urllib_parse.urlencode({"__RequestVerificationToken": token,
                                       "username": ADMIN, "password": ADMIN_PASSWORD}).encode()
        status, _, _ = http("POST", base + "/admin/login", body=form, opener=opener,
                            headers={"Content-Type": "application/x-www-form-urlencoded"})
        status, _, admin_page = http("GET", base + "/admin", opener=opener)
        signed_in = status == 200 and "创建账号" in admin_page.decode("utf-8", "replace")
        check("换了密钥环后管理员仍能登录（口令是 PBKDF2，与 DPAPI 无关）", signed_in, status)

        token = re.search(r"name='__RequestVerificationToken' value='([^']+)'",
                          admin_page.decode("utf-8", "replace")).group(1)
        status, _, view = http("POST", f"{base}/admin/users/{USER}/password/view",
                               body=urllib_parse.urlencode({"__RequestVerificationToken": token}).encode(),
                               opener=opener,
                               headers={"Content-Type": "application/x-www-form-urlencoded"})
        text = view.decode("utf-8", "replace")
        check("换了密钥环后密码密文无法解密（这就是异机恢复的边界）",
              status >= 400 or USER_PASSWORD not in text, f"{status} {text[:160]}")

        status, _, body = http("POST", base + "/api/v1/session",
                               headers={"Content-Type": "application/json"},
                               body=json.dumps({"username": USER, "password": USER_PASSWORD}).encode())
        check("换机器后账号照常登录、权限与发布包不受影响（本项只验证登录）", status == 200, status)
    finally:
        process.terminate()
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill()


def main():
    for required in (SERVER, SIGNER, BACKUP, RESTORE):
        if not required.exists():
            raise SystemExit(f"Missing {required}. Run ./build.ps1 first.")
    (ROOT / ".local").mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=ROOT / ".local", prefix="backup-drill-") as temporary:
        work = Path(temporary)
        drill = Drill(work)
        drill.build_data()

        # ---- A. backup a live data directory ------------------------------------------------
        done = drill.backup(drill.prod, include=drill.work / "signing")
        check("备份脚本执行成功", done.returncode == 0, done.stdout + done.stderr)
        print("\n".join(line for line in done.stdout.splitlines() if line.startswith("BACKUP_DIR")))
        backup_dirs = sorted(p for p in drill.backups.iterdir() if p.is_dir())
        check("生成了一个备份目录", len(backup_dirs) == 1, [p.name for p in backup_dirs])
        if not backup_dirs:
            return 1
        backup_dir = backup_dirs[0]
        manifest = json.loads((backup_dir / "backup-manifest.json").read_text(encoding="utf-8"))
        check("备份清单格式正确", manifest["schema"] == "chispa-backup-v1", manifest.get("schema"))
        paths = {entry["path"] for entry in manifest["filesIndex"]}
        required = {"users.json", "admin.json", "release-trust.json",
                    "programs/chispa/program.json",
                    "programs/chispa/releases/1.0.0/manifest.json",
                    "programs/chispa/releases/1.0.0/package.zip",
                    "programs/chispa/releases/1.0.0/signature.json"}
        missing = sorted(required - paths)
        has_keys = any(path.startswith("keys/") for path in paths)
        check("备份包含账号、管理员、密钥环、发布包与发布签名",
              not missing and has_keys, f"missing={missing} keys={has_keys}")

        # no secrets in the metadata that travels with the backup
        manifest_text = (backup_dir / "backup-manifest.json").read_text(encoding="utf-8")
        secrets = [ADMIN_PASSWORD, USER_PASSWORD, json.loads((drill.prod / "users.json").read_text(encoding="utf-8"))[0]["salt"]]
        leaked = [value for value in secrets if value and value in manifest_text]
        check("备份清单里没有口令或盐值", not leaked, leaked)
        check("备份清单提示了 DPAPI 依赖", any("DPAPI" in warning for warning in manifest.get("warnings", [])),
              manifest.get("warnings"))
        extras = manifest.get("extras") or []
        check("发布签名私钥被一起备份", any(group["name"] == "signing" for group in extras), extras)
        check("签名私钥确实在备份目录里",
              (backup_dir / "extra/signing/release-private.pem").exists(), "")

        # ---- B. destroy the original the way corruption would -------------------------------
        (drill.prod / "users.json").unlink()
        shutil.rmtree(drill.prod / "keys")
        shutil.rmtree(drill.prod / "programs/chispa/releases/1.0.0")
        damaged = tree_hashes(drill.prod)
        check("原始数据已被破坏（用于演练）", not (drill.prod / "users.json").exists(), "")

        # ---- C. restore into a new directory and verify it ----------------------------------
        extras_target = work / "restored-extras"
        done = drill.restore(backup_dir, drill.restored, extra_target=extras_target)
        output = done.stdout + done.stderr
        check("恢复脚本成功完成", done.returncode == 0, output[-800:])
        for marker in ("备份文件完整", "目标目录是新的", "能启动服务", "管理员能登录（Data Protection 密钥环可用）",
                       "的密码密文可解密", "能登录（PBKDF2", "授权程序列表", "并通过大小与哈希校验",
                       "至少完整校验过一个发布包"):
            check(f"恢复验证包含「{marker}」", marker in output, output[-400:])
        check("恢复输出给出切换步骤", "切换方法" in output, "")

        foreign_keyring_scenario(drill, drill.restored, work)

        # ---- D. independent verification of the restored copy -------------------------------
        check("签名私钥恢复到数据目录之外的独立目录",
              (extras_target / "signing/release-private.pem").exists()
              and not any(drill.restored.rglob("release-private.pem")), "")
        independent_verification(drill)

        # ---- D2. the original directory was not touched by the restore ----------------------
        check("恢复没有改动原数据目录", tree_hashes(drill.prod) == damaged, "原始目录被修改了")

        # ---- E. a corrupted backup is refused ----------------------------------------------
        corrupt = work / "corrupt-backup"
        shutil.copytree(backup_dir, corrupt)
        target = corrupt / "data/programs/chispa/releases/1.0.0/package.zip"
        payload = bytearray(target.read_bytes())
        payload[-3] ^= 0xFF
        target.write_bytes(bytes(payload))
        corrupt_target = work / "corrupt-restore"
        done = drill.restore(corrupt, corrupt_target)
        output = done.stdout + done.stderr
        check("损坏的备份被拒绝", done.returncode != 0 and "哈希不符" in output, output[-500:])
        check("被拒绝时没有留下可用的恢复目录",
              not corrupt_target.exists() or not any(corrupt_target.iterdir()), str(corrupt_target))

        # ---- F. an internally inconsistent data directory cannot be backed up --------------
        broken = work / "broken-prod"
        shutil.copytree(drill.backups / backup_dir.name / "data", broken)
        (broken / "programs/chispa/releases/1.0.0/package.zip").unlink()
        done = drill.backup(broken, target=work / "broken-backups")
        output = done.stdout + done.stderr
        check("半发布状态的数据目录被拒绝备份", done.returncode != 0 and
              ("缺少" in output or "不符" in output), output[-500:])

        # ---- G. retention ------------------------------------------------------------------
        for _ in range(3):
            time.sleep(1.1)   # the backup name is second-resolution
            drill.backup(drill.restored, target=work / "retention", keep=2)
        remaining = sorted(p.name for p in (work / "retention").iterdir() if p.is_dir())
        check("保留策略只保留最新 2 份", len(remaining) == 2, remaining)

        # ---- H. restore refuses an existing non-empty target -------------------------------
        occupied = work / "occupied"
        occupied.mkdir()
        (occupied / "keep-me.txt").write_text("do not delete", encoding="utf-8")
        done = drill.restore(backup_dir, occupied, admin_user="", user_account="")
        output = done.stdout + done.stderr
        check("已存在且非空的目标被拒绝", done.returncode != 0 and "已存在且非空" in output, output[-400:])
        check("被拒绝时原文件仍在", (occupied / "keep-me.txt").read_text(encoding="utf-8") == "do not delete", "")

    failures = [name for name, passed, _ in RESULTS if not passed]
    print()
    print(f"{len(RESULTS) - len(failures)}/{len(RESULTS)} checks passed")
    if failures:
        print("FAILED: " + ", ".join(failures))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
