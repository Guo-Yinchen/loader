"""Check that the distributed single-file GUI reaches its login window on Windows."""

import ctypes
from pathlib import Path
import os
import subprocess
import tempfile
import time


ROOT = Path(__file__).resolve().parents[1]
EXE = Path(os.environ.get("CHISPA_TEST_LAUNCHER", ROOT / ".local/distribution/client/Chispa.Launcher.exe"))
USER32 = ctypes.windll.user32
GDI32 = ctypes.windll.gdi32
ENUM_CALLBACK = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
GDI32.CreateRectRgn.restype = ctypes.c_void_p
GDI32.GetRgnBox.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
GDI32.PtInRegion.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int]
GDI32.DeleteObject.argtypes = [ctypes.c_void_p]
USER32.GetWindowRgn.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
USER32.GetWindowLongW.argtypes = [ctypes.c_void_p, ctypes.c_int]


class RECT(ctypes.Structure):
    _fields_ = [("left", ctypes.c_long), ("top", ctypes.c_long),
                ("right", ctypes.c_long), ("bottom", ctypes.c_long)]


def form_windows(pid):
    found = []

    @ENUM_CALLBACK
    def visit(hwnd, _):
        owner = ctypes.c_ulong()
        USER32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid:
            name = ctypes.create_unicode_buffer(256)
            USER32.GetClassNameW(hwnd, name, len(name))
            if name.value.startswith("WindowsForms10.Window"):
                found.append(hwnd)
        return True

    USER32.EnumWindows(visit, 0)
    return found


def has_four_rounded_corners(hwnd):
    region = GDI32.CreateRectRgn(0, 0, 1, 1)
    try:
        assert USER32.GetWindowRgn(hwnd, region) == 3, "Window has no rounded region."
        bounds = RECT()
        GDI32.GetRgnBox(region, ctypes.byref(bounds))
        corners = ((bounds.left, bounds.top), (bounds.right - 1, bounds.top),
                   (bounds.left, bounds.bottom - 1), (bounds.right - 1, bounds.bottom - 1))
        return all(not GDI32.PtInRegion(region, x, y) for x, y in corners)
    finally:
        GDI32.DeleteObject(region)


def main():
    assert EXE.is_file(), "Publish the client first."
    if not os.environ.get("CHISPA_TEST_LAUNCHER"):
        assert [p.name for p in EXE.parent.iterdir()] == [EXE.name], "Distribution has extra files."
    with tempfile.TemporaryDirectory(dir=ROOT / ".local") as diagnostic:
        env = os.environ.copy()
        env["CHISPA_LAUNCHER_LOG_DIRECTORY"] = diagnostic
        startup = subprocess.STARTUPINFO()
        startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        startup.wShowWindow = 0
        process = subprocess.Popen([str(EXE)], env=env, startupinfo=startup)
        try:
            for _ in range(80):
                if process.poll() is not None:
                    break
                windows = form_windows(process.pid)
                if windows:
                    style = USER32.GetWindowLongW(windows[0], -16)
                    assert not style & 0x00C40000, "Native Windows caption or resize frame is still present."
                    assert has_four_rounded_corners(windows[0]), "One or more window corners are square."
                    print("PASS: launcher opened without native frame and with four rounded corners")
                    return
                time.sleep(0.1)
            log = Path(diagnostic) / "launcher-error.log"
            detail = log.read_text(errors="replace") if log.exists() else "No managed error log."
            raise AssertionError(f"Launcher did not open its window (exit={process.poll()}):\n{detail}")
        finally:
            if process.poll() is None:
                process.terminate()
            process.wait(timeout=10)


if __name__ == "__main__":
    main()
