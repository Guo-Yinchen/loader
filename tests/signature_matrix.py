"""Release-signature acceptance matrix (task 03).

Two layers, because a release is verified at two different moments and for two different reasons:

  * the manifest      - is this program/version/size/hash what the release environment signed?
  * the per-file map  - are the files the loader is about to trust the ones that were signed?

Part A feeds the loader's own code manifests it would never receive from a healthy server (a valid
signature over a different program, a substituted file map). Part B drives the same decisions
through the real server by tampering with what it stores, which is what "the download server and
its SHA-256 list were both replaced" looks like from the outside.

Run:  python tests/signature_matrix.py
Needs: ./build.ps1 (server, client, signing tool) and tests/Chispa.Smoke built.
"""

import base64
import hashlib
import io
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
DOTNET = ROOT / ".tooling/dotnet/dotnet.exe"
if not DOTNET.exists():
    DOTNET = "dotnet"
pointer = ROOT / ".local/current-server-build.txt"
SERVER = (ROOT / ".local/server-builds" / pointer.read_text().strip() / "Chispa.Server.dll"
          if pointer.exists() else ROOT / "server/bin/Release/net10.0/Chispa.Server.dll")
SIGNER = ROOT / "tools/Chispa.Sign/bin/Release/net10.0/Chispa.Sign.dll"
SMOKE = ROOT / "tests/bin/Release/net10.0-windows/Chispa.Smoke.dll"
PASSWORD = "correct horse battery staple"
ACCOUNT = "siguser"
PROGRAM = "chispa"
VERSION = "1.0.0"

RESULTS = []


def check(name, passed, detail=""):
    RESULTS.append((name, bool(passed), detail))
    print(("PASS " if passed else "FAIL ") + name + ("" if passed else f"  [{str(detail)[:600]}]"))


def request(url, payload=None, token=None, method=None):
    headers = {}
    if payload is not None:
        headers["Content-Type"] = "application/json"
    if token:
        headers["Authorization"] = "Bearer " + token
    body = json.dumps(payload).encode() if payload is not None else None
    return urllib.request.urlopen(urllib.request.Request(url, data=body, headers=headers, method=method), timeout=10)


class Harness:
    def __init__(self, temp: Path):
        self.temp = temp
        self.data = temp / "data"
        self.env = os.environ.copy()
        self.env["CHISPA_SERVER_DATA"] = str(self.data)
        self.env["DOTNET_CLI_HOME"] = str(ROOT / ".tooling/home")
        self.env["TEMP"] = str(ROOT / ".tooling/temp")
        self.env["TMP"] = str(ROOT / ".tooling/temp")
        with socket.socket() as probe:
            probe.bind(("127.0.0.1", 0))
            self.port = probe.getsockname()[1]
        self.base = f"http://127.0.0.1:{self.port}"
        self.server = None
        self.keys = temp / "keys"
        self.keys_b = temp / "keys-b"

    # ---- tooling -------------------------------------------------------------------------
    def run(self, *args, **kwargs):
        return subprocess.run([str(DOTNET), *map(str, args)], env=self.env, capture_output=True,
                              text=True, encoding="utf-8", errors="replace", **kwargs)

    def keygen(self, out: Path, key_id: str):
        done = self.run(SIGNER, "keygen", "--out", out, "--id", key_id)
        if done.returncode:
            raise SystemExit(done.stdout + done.stderr)

    def pack(self, source: Path, archive: Path, executable: str):
        done = self.run(SIGNER, "pack", "--source", source, "--out", archive, "--executable", executable)
        if done.returncode:
            raise SystemExit(done.stdout + done.stderr)
        return archive

    def sign(self, archive: Path, program: str, version: str, executable: str, key_dir: Path, key_id: str,
             out: Path):
        done = self.run(SIGNER, "sign", "--package", archive, "--program", program, "--version", version,
                        "--executable", executable, "--key", key_dir / "release-private.pem",
                        "--key-id", key_id, "--out", out)
        if done.returncode:
            raise SystemExit(done.stdout + done.stderr)
        return out

    def server_cli(self, *args, stdin=None):
        return subprocess.run([str(DOTNET), str(SERVER), *map(str, args)], env=self.env, input=stdin,
                              capture_output=True, text=True, encoding="utf-8", errors="replace")

    # ---- data and server ----------------------------------------------------------------
    def prepare(self):
        runtime = self.temp / "runtime"
        (runtime / "assets").mkdir(parents=True)
        (runtime / "Wardogs.exe").write_bytes(b"signed-wardogs")
        (runtime / "vmm.dll").write_bytes(b"signed-vmm")
        (runtime / "assets" / "data.txt").write_text("payload", encoding="utf-8")
        self.runtime = runtime
        self.keygen(self.keys, "matrix-key")
        self.keygen(self.keys_b, "matrix-key-b")
        self.env["CHISPA_RELEASE_TRUST_KEYS"] = str(self.keys / "release-public.json")
        for args, stdin in ((["program-add", PROGRAM, "Chispa", "Wardogs.exe"], None),
                            (["user-add", ACCOUNT], PASSWORD + "\n")):
            done = self.server_cli(*args, stdin=stdin)
            if done.returncode:
                raise SystemExit(done.stdout + done.stderr)
        self.original_archive = self.pack(runtime, self.temp / "package.zip", "Wardogs.exe")
        self.original_signature = self.sign(self.original_archive, PROGRAM, VERSION, "Wardogs.exe",
                                            self.keys, "matrix-key", self.temp / "signature.json")
        published = self.server_cli("publish", PROGRAM, VERSION, self.original_archive, self.original_signature)
        if published.returncode:
            raise SystemExit(published.stdout + published.stderr)
        self.release_dir = self.data / "programs" / PROGRAM / "releases" / VERSION

    def start(self):
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
        raise SystemExit("server did not start")

    def stop(self):
        if self.server is None:
            return
        self.server.terminate()
        try:
            self.server.wait(timeout=15)
        except subprocess.TimeoutExpired:
            self.server.kill()
        self.server = None

    # ---- client entry points ------------------------------------------------------------
    def install(self, version=VERSION, root="install"):
        env = dict(os.environ)
        env["CHISPA_TRUST_KEYS"] = str(self.keys / "release-public.json")
        env["CHISPA_INSTALL_ROOT"] = str(self.temp / root)
        done = subprocess.run([str(DOTNET), str(SMOKE), "--install-check", self.base, PROGRAM, version,
                               ACCOUNT, PASSWORD], env=env, capture_output=True, text=True,
                              encoding="utf-8", errors="replace")
        return done.returncode, done.stdout.strip() + done.stderr.strip()

    def verify_release(self, manifest_path: Path, trust: Path = None):
        trust = trust or (self.keys / "release-public.json")
        env = dict(os.environ)
        env["CHISPA_TRUST_KEYS"] = str(trust)
        done = subprocess.run([str(DOTNET), str(SMOKE), "--verify-release", str(trust), str(manifest_path)],
                              env=env, capture_output=True, text=True, encoding="utf-8", errors="replace")
        return done.returncode, done.stdout.strip() + done.stderr.strip()

    # ---- stored release manipulation ----------------------------------------------------
    def stored_manifest(self):
        return json.loads((self.release_dir / "manifest.json").read_text(encoding="utf-8"))

    def write_stored_manifest(self, manifest):
        (self.release_dir / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")

    def manifest_as_wire(self, manifest):
        """The shape the client receives: manifest fields plus the signature as base64."""
        wire = dict(manifest)
        signature_path = self.release_dir / "signature.json"
        wire["signature"] = (base64.b64encode(signature_path.read_bytes()).decode()
                             if signature_path.exists() else None)
        return wire

    def write_wire(self, name, wire):
        path = self.temp / name
        path.write_text(json.dumps(wire), encoding="utf-8")
        return path

    def restore(self):
        (self.release_dir / "package.zip").write_bytes(self.original_archive.read_bytes())
        (self.release_dir / "signature.json").write_text(
            self.original_signature.read_text(encoding="utf-8"), encoding="utf-8")
        manifest = self.stored_manifest()
        manifest.pop("signature", None)
        manifest["sha256"] = hashlib.sha256(self.original_archive.read_bytes()).hexdigest()
        manifest["size"] = self.original_archive.stat().st_size
        self.write_stored_manifest(manifest)


def part_a_client_rules(harness: Harness):
    """The loader refuses a manifest that is not the signed one - the core of the task."""
    manifest = harness.stored_manifest()
    good = harness.manifest_as_wire(manifest)
    code, out = harness.verify_release(harness.write_wire("good.json", good))
    check("a valid signed manifest verifies", code == 0 and "VERIFY OK" in out, out)

    def variant(**changes):
        wire = dict(good)
        wire.update(changes)
        return wire

    code, out = harness.verify_release(harness.write_wire("wrong-program.json",
                                                          variant(programId="helper")))
    check("a signature for another program is refused", code == 3 and "程序 ID" in out, out)

    code, out = harness.verify_release(harness.write_wire("wrong-version.json", variant(version="9.9.9")))
    check("a signature for another version is refused", code == 3 and "版本" in out, out)

    code, out = harness.verify_release(harness.write_wire("wrong-exe.json", variant(executable="Other.exe")))
    check("a changed entry executable is refused", code == 3 and "入口文件" in out, out)

    code, out = harness.verify_release(harness.write_wire("wrong-size.json", variant(size=manifest["size"] + 1)))
    check("a changed package size is refused", code == 3 and "包大小" in out, out)

    code, out = harness.verify_release(harness.write_wire("wrong-hash.json",
                                                          variant(sha256="0" * 64)))
    check("a changed package hash is refused", code == 3 and "包哈希" in out, out)

    code, out = harness.verify_release(harness.write_wire("no-signature.json", variant(signature=None)))
    check("a manifest with no signature is refused", code == 3 and "没有签名" in out, out)

    # A signature from a key this client does not trust: the case a rotated-out key produces.
    other = harness.sign(harness.original_archive, PROGRAM, VERSION, "Wardogs.exe", harness.keys_b,
                         "matrix-key-b", harness.temp / "untrusted-signature.json")
    wire = dict(good)
    wire["signature"] = base64.b64encode(other.read_bytes()).decode()
    code, out = harness.verify_release(harness.write_wire("untrusted-key.json", wire))
    check("a signature from an unknown key is refused", code == 3 and "密钥不受信任" in out, out)

    # Valid signature, one byte of it changed.
    envelope = json.loads(base64.b64decode(good["signature"]))
    raw = bytearray(base64.b64decode(envelope["signature"]))
    raw[5] ^= 0xFF
    envelope["signature"] = base64.b64encode(bytes(raw)).decode()
    wire = dict(good)
    wire["signature"] = base64.b64encode(json.dumps(envelope).encode()).decode()
    code, out = harness.verify_release(harness.write_wire("bad-signature.json", wire))
    check("a corrupted signature is refused", code == 3 and "签名校验失败" in out, out)

    # The file map the loader checks installed files against, substituted while the package
    # statement stays valid: this is the "compromised server, modified local DLL" attempt.
    wire = dict(good)
    wire["binaries"] = {"Wardogs.exe": "0" * 64}
    code, out = harness.verify_release(harness.write_wire("swapped-binaries.json", wire))
    check("a substituted per-file hash map is refused", code == 3 and "文件清单" in out, out)

    # Rotation: a client that trusts both keys accepts the new one while still accepting the old.
    # This is the state a key rollover has to pass through before the old key is removed.
    both = harness.temp / "trust-both.json"
    both.write_text(json.dumps({"keys": [
        {"keyId": "matrix-key", "publicKey": json.loads((harness.keys / "release-public.json")
                                                        .read_text(encoding="utf-8"))["keys"][0]["publicKey"]},
        {"keyId": "matrix-key-b", "publicKey": json.loads((harness.keys_b / "release-public.json")
                                                          .read_text(encoding="utf-8"))["keys"][0]["publicKey"]},
    ]}), encoding="utf-8")
    rotated = harness.temp / "rotated-signature.json"
    harness.sign(harness.original_archive, PROGRAM, VERSION, "Wardogs.exe", harness.keys_b, "matrix-key-b", rotated)
    wire = dict(good)
    wire["signature"] = base64.b64encode(rotated.read_bytes()).decode()
    wire["signatureKeyId"] = "matrix-key-b"
    path = harness.write_wire("rotated.json", wire)
    code, out = harness.verify_release(path, trust=both)
    check("during rotation both keys are accepted", code == 0 and "VERIFY OK" in out, out)
    code, out = harness.verify_release(path)
    check("...and the old client still refuses the new key", code == 3 and "密钥不受信任" in out, out)


def part_b_stored_release(harness: Harness):
    """The same decisions, driven through the server: what a replaced download server looks like."""
    code, out = harness.install(root="install-good")
    check("a correctly signed release installs", code == 0 and "INSTALL OK" in out, out)
    check("...and the files really landed", (harness.temp / "install-good/versions" / VERSION / "Wardogs.exe").exists())

    # The server stores a different package but keeps the manifest's numbers consistent with it.
    forged = harness.temp / "forged.zip"
    payload = bytearray(harness.original_archive.read_bytes())
    payload[-10] ^= 0xFF
    forged.write_bytes(bytes(payload))
    (harness.release_dir / "package.zip").write_bytes(bytes(payload))
    manifest = harness.stored_manifest()
    manifest["sha256"] = hashlib.sha256(bytes(payload)).hexdigest()
    manifest["size"] = len(payload)
    harness.write_stored_manifest(manifest)
    code, out = harness.install(root="install-forged")
    check("a substituted package with a matching manifest is refused", code == 3 and "包哈希" in out, out)
    harness.restore()

    # Only the manifest's numbers are changed - the package on disk is untouched.
    manifest = harness.stored_manifest()
    manifest["sha256"] = "1" * 64
    harness.write_stored_manifest(manifest)
    code, out = harness.install(root="install-bad-hash")
    check("a tampered manifest hash is refused", code == 3 and "包哈希" in out, out)
    harness.restore()

    # The signature file is removed, as on a release published before signing existed.
    signature_copy = (harness.release_dir / "signature.json").read_text(encoding="utf-8")
    (harness.release_dir / "signature.json").unlink()
    code, out = harness.install(root="install-unsigned")
    check("an unsigned release is refused", code == 3 and "没有签名" in out, out)

    # Migration: attach a signature to the existing release, and it installs again.
    signed = harness.server_cli("sign-release", PROGRAM, VERSION, harness.original_signature)
    check("sign-release migrates an already-published package", signed.returncode == 0, signed.stdout + signed.stderr)
    code, out = harness.install(root="install-migrated")
    check("...and the migrated release installs", code == 0 and "INSTALL OK" in out, out)

    # A release whose manifest was written before signatures existed has no per-file map; the
    # migration above has to have produced one, or the launch check would have nothing to bind to.
    manifest = harness.stored_manifest()
    check("the migrated manifest carries the signed file map",
          isinstance(manifest.get("binaries"), dict) and manifest["binaries"].get("Wardogs.exe"),
          manifest.get("binaries"))
    (harness.release_dir / "signature.json").write_text(signature_copy, encoding="utf-8")


def part_c_publish_rules(harness: Harness):
    """What the server refuses to store, so a broken or hostile signing step fails before users."""
    runtime = harness.runtime
    archive = harness.pack(runtime, harness.temp / "c-package.zip", "Wardogs.exe")

    # Signed by a key that is not trusted: a compromised server cannot add its own key.
    untrusted = harness.sign(archive, PROGRAM, "2.0.0", "Wardogs.exe", harness.keys_b, "matrix-key-b",
                             harness.temp / "c-untrusted.json")
    refused = harness.server_cli("publish", PROGRAM, "2.0.0", archive, untrusted)
    check("publishing with an untrusted key is refused", refused.returncode != 0 and "密钥不受信任" in
          (refused.stdout + refused.stderr), refused.stdout + refused.stderr)

    # Signed for another program, attempted under this one.
    wrong_program = harness.sign(archive, "helper", "2.0.1", "Wardogs.exe", harness.keys, "matrix-key",
                                 harness.temp / "c-wrong-program.json")
    refused = harness.server_cli("publish", PROGRAM, "2.0.1", archive, wrong_program)
    check("publishing a package signed for another program is refused",
          refused.returncode != 0 and "程序 ID" in (refused.stdout + refused.stderr),
          refused.stdout + refused.stderr)

    # Signature valid, but the archive that arrives is a different one. Rebuilt rather than
    # byte-flipped so it is still a well-formed ZIP - flipping a byte inside an archive would be
    # rejected by the ZIP reader instead, which tests the wrong layer.
    signature = harness.sign(archive, PROGRAM, "2.0.2", "Wardogs.exe", harness.keys, "matrix-key",
                             harness.temp / "c-signature.json")
    swapped_source = harness.temp / "swapped-runtime"
    (swapped_source / "assets").mkdir(parents=True)
    (swapped_source / "Wardogs.exe").write_bytes(b"swapped-wardogs")
    (swapped_source / "vmm.dll").write_bytes(b"signed-vmm")
    (swapped_source / "assets" / "data.txt").write_text("payload", encoding="utf-8")
    swapped = harness.pack(swapped_source, harness.temp / "c-tampered.zip", "Wardogs.exe")
    refused = harness.server_cli("publish", PROGRAM, "2.0.2", swapped, signature)
    check("publishing a package swapped after signing is refused",
          refused.returncode != 0 and "签名" in (refused.stdout + refused.stderr),
          refused.stdout + refused.stderr)

    # The signing tool refuses a package whose entry EXE is not at the top level.
    wrapped = harness.temp / "c-wrapped.zip"
    with zipfile.ZipFile(wrapped, "w") as archive_file:
        archive_file.writestr("Chispa/Wardogs.exe", b"wrapped")
    signed = harness.run(SIGNER, "sign", "--package", wrapped, "--program", PROGRAM, "--version", "2.0.3",
                         "--executable", "Wardogs.exe", "--key", harness.keys / "release-private.pem",
                         "--key-id", "matrix-key", "--out", harness.temp / "c-wrapped.json")
    check("the signing tool refuses a package without the entry EXE on top",
          signed.returncode != 0 and "入口文件" in (signed.stdout + signed.stderr), signed.stdout + signed.stderr)

    # Publishing without a trust configuration fails closed instead of storing something unchecked.
    no_trust = dict(harness.env)
    del no_trust["CHISPA_RELEASE_TRUST_KEYS"]
    refused = subprocess.run([str(DOTNET), str(SERVER), "publish", PROGRAM, "2.0.4", str(archive),
                              str(signature)], env=no_trust, capture_output=True, text=True,
                             encoding="utf-8", errors="replace")
    check("publishing without configured trust keys fails closed",
          refused.returncode != 0 and "公钥" in (refused.stdout + refused.stderr), refused.stdout + refused.stderr)


def main():
    for required in (SERVER, SIGNER, SMOKE):
        if not required.exists():
            raise SystemExit(f"Missing {required}. Run ./build.ps1 and build tests/Chispa.Smoke.csproj.")
    (ROOT / ".local").mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=ROOT / ".local", prefix="sig-matrix-") as temporary:
        harness = Harness(Path(temporary))
        harness.prepare()
        part_a_client_rules(harness)
        part_c_publish_rules(harness)
        harness.start()
        try:
            part_b_stored_release(harness)
        finally:
            harness.stop()
    failures = [name for name, passed, _ in RESULTS if not passed]
    print()
    print(f"{len(RESULTS) - len(failures)}/{len(RESULTS)} checks passed")
    if failures:
        print("FAILED: " + ", ".join(failures))
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
