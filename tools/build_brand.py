"""Build the Chispa vector mark and Windows icon from one set of curves.

Run: python tools/build_brand.py (Pillow required). No external artwork/fonts.
"""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1] / "assets/branding"
# A bright four-point spark leaning forward, accompanied by its smaller spark.
CURVES = [
    ((135, 27), (137, 91), (154, 108), (218, 115)),
    ((218, 115), (156, 127), (138, 147), (121, 218)),
    ((121, 218), (117, 153), (100, 137), (36, 130)),
    ((36, 130), (99, 119), (117, 99), (135, 27)),
]
SMALL = [(204, 27), (210, 44), (228, 50), (210, 56), (204, 74), (198, 56), (181, 50), (198, 44)]

def points():
    result = []
    for a, b, c, d in CURVES:
        for i in range(65):
            t = i / 64
            result.append(tuple((1-t)**3*a[k] + 3*(1-t)**2*t*b[k] + 3*(1-t)*t*t*c[k] + t**3*d[k] for k in (0, 1)))
    return result

def main():
    ROOT.mkdir(parents=True, exist_ok=True)
    d = "M135 27 " + " ".join(f"C{b[0]} {b[1]} {c[0]} {c[1]} {end[0]} {end[1]}" for _, b, c, end in CURVES) + "Z"
    shapes = f'<path d="{d}" fill="url(#spark)"/><polygon points="{" ".join(f"{x},{y}" for x,y in SMALL)}" fill="#c7fff4"/>'
    defs = '<defs><linearGradient id="spark" x1="0" y1="0" x2="1" y2="1"><stop stop-color="#dbfff6"/><stop offset=".48" stop-color="#79f2d6"/><stop offset="1" stop-color="#45bde4"/></linearGradient></defs>'
    for name, bg in [("chispa-mark.svg", ""), ("chispa.svg", '<rect x="1" y="1" width="254" height="254" rx="58" fill="#101f30" stroke="#355160" stroke-width="2"/>')]:
        (ROOT / name).write_text(f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" role="img" aria-label="Chispa spark">{defs}{bg}{shapes}</svg>\n', encoding="utf-8")
    size = 1024
    mask = Image.new("L", (size, size))
    ImageDraw.Draw(mask).polygon([(x*4,y*4) for x,y in points()], fill=255)
    mark = Image.new("RGBA", (size,size))
    draw = ImageDraw.Draw(mark)
    for y in range(size):
        t = y / (size-1)
        color = tuple(round(a*(1-t)+b*t) for a,b in zip((211,255,244),(69,189,228)))
        draw.line((0,y,size,y), fill=(*color,255))
    mark.putalpha(mask)
    ImageDraw.Draw(mark).polygon([(x*4,y*4) for x,y in SMALL], fill=(199,255,244,255))
    mark.resize((512,512),Image.Resampling.LANCZOS).save(ROOT / "chispa-mark.png")
    icon = Image.new("RGBA", (size,size))
    ImageDraw.Draw(icon).rounded_rectangle((4,4,size-4,size-4),radius=232,fill=(16,31,48,255),outline=(53,81,96,255),width=6)
    icon.alpha_composite(mark)
    icon = icon.resize((256,256),Image.Resampling.LANCZOS)
    icon.save(ROOT / "chispa.png")
    icon.save(ROOT / "chispa.ico",sizes=[(16,16),(20,20),(24,24),(32,32),(40,40),(48,48),(64,64),(128,128),(256,256)])
    print("Generated SVG, PNG and multi-resolution ICO in assets/branding")

if __name__ == "__main__":
    main()
