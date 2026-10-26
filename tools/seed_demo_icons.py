"""Generate local demo PNG icons and fill only programs that have no icon."""

import base64
import json
import math
import os
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont


ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "assets/icons"
DATA = ROOT / ".local/server/programs"
FONT = Path("C:/Windows/Fonts/seguisb.ttf")


def font(size):
    return ImageFont.truetype(str(FONT), size)


def icon(kind, top, bottom):
    if kind == "chispa":
        return Image.open(ROOT / "assets/branding/chispa.png").convert("RGBA")
    size = 192
    image = Image.new("RGB", (size, size))
    pixels = image.load()
    for y in range(size):
        mix = y / (size - 1)
        for x in range(size):
            light = max(0, 1 - math.hypot(x - 80, y - 55) / 200) * 12
            pixels[x, y] = tuple(int(top[i] * (1 - mix) + bottom[i] * mix + light) for i in range(3))
    glow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(glow)
    if kind == "chispa":
        draw.ellipse((37, 37, 155, 155), outline=(191, 231, 96, 175), width=5)
        draw.ellipse((54, 54, 138, 138), outline=(191, 231, 96, 110), width=3)
        draw.text((96, 94), "C", font=font(105), anchor="mm", fill=(232, 255, 184, 255))
    elif kind == "beta-slot":
        draw.ellipse((36, 36, 156, 156), outline=(192, 155, 244, 160), width=5)
        draw.text((96, 91), "β", font=font(107), anchor="mm", fill=(237, 219, 255, 255))
    else:
        for x, y in ((49, 49), (103, 49), (49, 103), (103, 103)):
            draw.rounded_rectangle((x, y, x + 40, y + 40), radius=9,
                                   fill=(156, 218, 249, 255))
    blurred = glow.filter(ImageFilter.GaussianBlur(14))
    image = Image.alpha_composite(image.convert("RGBA"), blurred)
    image = Image.alpha_composite(image, glow)
    return image.convert("RGB")


def main():
    OUTPUT.mkdir(parents=True, exist_ok=True)
    specs = {
        "chispa": ((30, 47, 42), (15, 25, 32)),
        "beta-slot": ((61, 42, 81), (24, 25, 45)),
        "tool-slot": ((34, 57, 78), (17, 29, 43)),
    }
    for program_id, (top, bottom) in specs.items():
        path = OUTPUT / f"{program_id}.png"
        previous = path.read_bytes() if path.exists() else None
        icon(program_id, top, bottom).save(path, format="PNG", optimize=True)
        metadata = DATA / program_id / "program.json"
        if not metadata.exists():
            continue
        program = json.loads(metadata.read_text(encoding="utf-8"))
        previous_value = "data:image/png;base64," + base64.b64encode(previous).decode() if previous else None
        if program.get("icon") and not (program_id == "chispa" and program["icon"] == previous_value):
            continue
        if program.get("icon") and not metadata.with_suffix(".brand-backup.json").exists():
            metadata.with_suffix(".brand-backup.json").write_bytes(metadata.read_bytes())
        program["icon"] = "data:image/png;base64," + base64.b64encode(path.read_bytes()).decode()
        temporary = metadata.with_name(metadata.name + ".new")
        temporary.write_text(json.dumps(program, ensure_ascii=False, indent=2), encoding="utf-8")
        os.replace(temporary, metadata)
        print(f"Seeded {program_id}: {path.name}")


if __name__ == "__main__":
    main()
