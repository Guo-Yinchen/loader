"""Checks for the release packaging entry point (publish-client.ps1).

Default mode runs the address-validation matrix in a few seconds and never builds.
With --build it also produces a real production single-file EXE in .local/qa-publish,
verifies the address by running that EXE, and proves a failed publish keeps the
previous artifact. It never touches .local/server or .local/distribution.

Usage:
    python tests/publish_checks.py
    python tests/publish_checks.py --build
"""

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "publish-client.ps1"
CONFIG = ROOT / "release/release.json"
QA = ROOT / ".local/qa-publish"
POWERSHELL = "powershell.exe"
ARTIFACT = "Chispa.Launcher.exe"


def decode(data: bytes) -> str:
    for encoding in ("utf-8", "gbk", "cp1252"):
        try:
            return data.decode(encoding)
        except UnicodeDecodeError:
            continue
    return data.decode("utf-8", "replace")


def run_publish(*arguments, timeout=1200):
    command = [POWERSHELL, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(SCRIPT), *arguments]
    result = subprocess.run(command, cwd=ROOT, capture_output=True, timeout=timeout)
    return result.returncode, decode(result.stdout), decode(result.stderr)


def run_artifact(exe: Path, *arguments, timeout=180):
    result = subprocess.run([str(exe), *arguments], capture_output=True, timeout=timeout)
    return result.returncode


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def check_config():
    config = json.loads(CONFIG.read_text(encoding="utf-8"))
    production = config["production"]["serverUrl"]
    local = config["local"]["serverUrl"]
    assert production.startswith("https://"), production
    for marker in ("127.0.0.1", "localhost", "[::1]", "0.0.0.0"):
        assert marker not in production, f"production address looks like {marker}: {production}"
    assert production.count("/") == 2, f"production address must be an origin: {production}"
    assert local.startswith("http://127.0.0.1:"), local
    print(f"PASS: 发布配置 production={production} local={local}")
    return production


def check_rejections():
    cases = [
        ("回环地址（明文 HTTP）", ["-ServerUrl", "http://127.0.0.1:5115"], ("HTTPS", "loopback")),
        ("回环地址（HTTPS 也不行）", ["-ServerUrl", "https://127.0.0.1:24154"], ("loopback",)),
        ("远程明文 HTTP", ["-ServerUrl", "http://teamviewer.rm7lwjwtg.nyat.app:24154"], ("HTTPS",)),
        ("带路径", ["-ServerUrl", "https://example.com/chispa"], ("origin",)),
        ("带查询参数", ["-ServerUrl", "https://example.com:24154/?x=1"], ("origin",)),
        ("带账号密码", ["-ServerUrl", "https://user:pass@example.com:24154"], ("username or password",)),
        ("localhost", ["-ServerUrl", "https://localhost:24154"], ("loopback",)),
        ("无效端口", ["-ServerUrl", "https://example.com:70000"], ("port",)),
        ("本地档案给公网地址", ["-Profile", "local", "-ServerUrl", "https://example.com:24154"], ("loopback",)),
        ("本地档案缺端口", ["-Profile", "local", "-ServerUrl", "http://127.0.0.1"], ("port",)),
        ("未知档案", ["-Profile", "staging"], ("production", "local", "staging")),
        ("配置不存在", ["-ConfigPath", ".local/qa-publish/missing.json"], ("not found",)),
        ("输出目录是仓库根", ["-OutputDirectory", "."], ("repository root",)),
    ]
    for label, arguments, keywords in cases:
        code, out, err = run_publish(*arguments)
        text = out + err
        assert code != 0, f"{label}: 预期失败但退出码为 0\n{text}"
        assert any(keyword in text for keyword in keywords), f"{label}: 错误信息里缺少 {keywords}：\n{text}"
        print(f"PASS: 拒绝 {label}（退出码 {code}）")


def check_artifact(exe: Path, expected: str):
    assert exe.is_file(), exe
    assert [p.name for p in exe.parent.iterdir()] == [ARTIFACT], \
        f"分发目录应当只有一个文件：{[p.name for p in exe.parent.iterdir()]}"
    assert run_artifact(exe, "--print-server-url", str(QA / "reported-url.txt"), timeout=300) == 0
    reported = (QA / "reported-url.txt").read_text(encoding="utf-8").strip()
    assert reported == expected, f"产物报告 {reported}，预期 {expected}"
    assert run_artifact(exe, "--verify-server-url", expected) == 0, "产物自检应当成功"
    assert run_artifact(exe, "--verify-server-url", expected + ".invalid") != 0, "产物自检不应对错误地址返回成功"
    print(f"PASS: 产物自检 {exe.name} → 实际内嵌 {reported}（运行产物读出，非命令行回显）")


def check_build(production_url: str):
    QA.mkdir(parents=True, exist_ok=True)
    output = QA / "client"
    first_exe = output / ARTIFACT

    code, out, err = run_publish("-OutputDirectory", str(output))
    assert code == 0, f"首次打包失败：\n{out}\n{err}"
    check_artifact(first_exe, production_url)
    first_hash = sha256(first_exe)

    # Second run must keep the previous artifact next to the output folder.
    code, out, err = run_publish("-OutputDirectory", str(output))
    assert code == 0, f"二次打包失败：\n{out}\n{err}"
    check_artifact(first_exe, production_url)
    previous = output.parent / (output.name + ".previous") / ARTIFACT
    assert previous.is_file(), f"没有保留上一版：{previous}"
    assert sha256(previous) == first_hash, "上一版产物与原产物不一致"
    print(f"PASS: 二次打包保留上一版 → {previous.relative_to(ROOT)}")

    # A rejected publish must leave the artifact byte-identical.
    good_hash = sha256(first_exe)
    bad_config = QA / "bad-release.json"
    bad_config.write_text(json.dumps({
        "production": {"serverUrl": "http://127.0.0.1:5115"},
        "local": {"serverUrl": "http://127.0.0.1:5115"},
    }), encoding="utf-8")
    code, out, err = run_publish("-OutputDirectory", str(output), "-ConfigPath", str(bad_config))
    assert code != 0, "错误配置应当发布失败"
    assert sha256(first_exe) == good_hash, "发布失败后分发产物被改动了"
    print("PASS: 配置错误时发布失败，原产物逐字节未变")

    code, out, err = run_publish("-VerifyArtifact", str(first_exe))
    assert code == 0, f"独立校验失败：\n{out}\n{err}"
    code, out, err = run_publish("-VerifyArtifact", str(first_exe), "-Profile", "local")
    assert code != 0, "用本地档案校验正式产物本应失败"
    print("PASS: -VerifyArtifact 可复核产物，且地址不符时失败")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--build", action="store_true", help="执行真实打包并验证产物（较慢）")
    args = parser.parse_args()
    production = check_config()
    check_rejections()
    if args.build:
        check_build(production)
    else:
        print("SKIP: 未加 --build，未执行真实打包")
    print("ALL PASS: 发布入口检查")


if __name__ == "__main__":
    sys.exit(main())
