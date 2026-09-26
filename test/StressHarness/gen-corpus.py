#!/usr/bin/env python3
"""Stress corpus generator for test\StressHarness.

Usage:
    python gen-corpus.py --out <dir> [--scale quick|full]

quick : small & few (CI): 4 photo PNG, 2 photo JPG, 4 screenshots, 2 alpha, 2 small
full  : bigger & many  : 48/24/48/36/36 at production-ish sizes
"""
import argparse
import os

import numpy as np
from PIL import Image, ImageDraw, ImageFilter


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="stress-corpus")
    ap.add_argument("--scale", choices=["quick", "full"], default="full")
    a = ap.parse_args()

    quick = a.scale == "quick"
    os.makedirs(a.out, exist_ok=True)
    rng = np.random.default_rng(42)

    pw, ph = (640, 360) if quick else (1600, 900)
    sw, sh = (1280, 720) if quick else (2560, 1440)
    aw = 512 if quick else 1024
    zw = 256 if quick else 512
    n_photo_png = 4 if quick else 48
    n_photo_jpg = 2 if quick else 24
    n_screen = 4 if quick else 48
    n_alpha = 2 if quick else 36
    n_small = 2 if quick else 36

    def photo(w, h):
        y = np.linspace(0, 1, h)[:, None]
        x = np.linspace(0, 1, w)[None, :]
        r = (120 + 100 * (1 - y) * np.cos(x * 3)).astype(np.float32)
        g = (100 + 90 * y * np.sin(x * 2 + 1)).astype(np.float32)
        b = (160 + 80 * (1 - x) * (1 - y)).astype(np.float32)
        img = np.stack([np.broadcast_to(r, (h, w)), np.broadcast_to(g, (h, w)), np.broadcast_to(b, (h, w))], -1)
        noise = rng.normal(0, 14, (h, w, 1)).astype(np.float32)
        return Image.fromarray(np.clip(img + noise, 0, 255).astype(np.uint8), "RGB")

    def screen(w, h):
        img = Image.new("RGB", (w, h), (243, 243, 243))
        d = ImageDraw.Draw(img)
        for i in range(60 if quick else 120):
            x0, y0 = int(rng.integers(0, max(1, w - 60))), int(rng.integers(0, max(1, h - 30)))
            c = tuple(int(v) for v in rng.integers(20, 240, 3))
            if i % 3 == 0:
                d.rectangle([x0, y0, x0 + int(rng.integers(40, 500)), y0 + int(rng.integers(8, 40))], fill=c)
            elif i % 3 == 1:
                d.ellipse([x0, y0, x0 + int(rng.integers(20, 90)), y0 + int(rng.integers(20, 90))], fill=c)
            else:
                d.line([x0, y0, x0 + int(rng.integers(10, 800)), y0 + int(rng.integers(2, 12))], fill=c, width=3)
        for _ in range(30 if quick else 60):
            x0, y0 = int(rng.integers(0, max(1, w - 300))), int(rng.integers(0, max(1, h - 16)))
            d.rectangle([x0, y0, x0 + int(rng.integers(40, 280)), y0 + 10], fill=(30, 30, 30))
        return img

    def alpha(w, h):
        img = photo(w, h).convert("RGBA")
        a = rng.integers(0, 255, (h, w), dtype=np.uint8)
        a = np.array(Image.fromarray(a).filter(ImageFilter.GaussianBlur(2)))
        img.putalpha(Image.fromarray(a, "L"))
        return img

    n = 0
    for i in range(n_photo_png):
        photo(pw, ph).save(os.path.join(a.out, f"photo_{i:03d}.png")); n += 1
    for i in range(n_photo_jpg):
        photo(pw, ph).save(os.path.join(a.out, f"photo_{i:03d}.jpg"), quality=92); n += 1
    for i in range(n_screen):
        screen(sw, sh).save(os.path.join(a.out, f"screen_{i:03d}.png")); n += 1
    for i in range(n_alpha):
        alpha(aw, aw).save(os.path.join(a.out, f"alpha_{i:03d}.png")); n += 1
    for i in range(n_small):
        screen(zw, zw).save(os.path.join(a.out, f"small_{i:03d}.png")); n += 1

    total = sum(os.path.getsize(os.path.join(a.out, f)) for f in os.listdir(a.out))
    print(f"generated {n} files, {total / 1048576:.1f} MB in {a.out} (scale={a.scale})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
