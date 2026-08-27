#!/usr/bin/env python3
"""Generate the Agent Sandbox Windows icon and package logo assets.

Requires Pillow (`python -m pip install Pillow`). The artwork is drawn at high
resolution and downsampled so the taskbar-sized variants remain crisp.
"""

from __future__ import annotations

from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "AgentSandbox.App" / "Assets"
SUPERSAMPLE = 4


def rounded_mask(size: int, margin: int, radius: int) -> Image.Image:
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (margin, margin, size - margin - 1, size - margin - 1),
        radius=radius,
        fill=255,
    )
    return mask


def draw_mark(output_size: int) -> Image.Image:
    size = 256 * SUPERSAMPLE
    scale = SUPERSAMPLE
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))

    margin = 8 * scale
    radius = 54 * scale
    mask = rounded_mask(size, margin, radius)

    # Deep navy-to-indigo surface matching the app's dark navigation palette.
    surface = Image.new("RGBA", (size, size))
    pixels = surface.load()
    top = (20, 29, 50)
    bottom = (70, 92, 232)
    for y in range(size):
        t = y / (size - 1)
        # Keep the upper half dark so the white terminal mark has strong contrast.
        eased = t * t * (3 - 2 * t)
        color = tuple(round(top[i] + (bottom[i] - top[i]) * eased) for i in range(3))
        for x in range(size):
            # A restrained blue highlight toward the upper-right corner.
            glow = max(0.0, 1.0 - (((x / size) - 0.78) ** 2 + ((y / size) - 0.20) ** 2) ** 0.5 * 2.2)
            pixels[x, y] = (
                min(255, color[0] + round(10 * glow)),
                min(255, color[1] + round(15 * glow)),
                min(255, color[2] + round(24 * glow)),
                255,
            )

    shadow = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    shadow_mask = mask.filter(ImageFilter.GaussianBlur(7 * scale))
    shadow.putalpha(shadow_mask.point(lambda value: value * 90 // 255))
    canvas.alpha_composite(shadow, (0, 4 * scale))
    surface.putalpha(mask)
    canvas.alpha_composite(surface)

    draw = ImageDraw.Draw(canvas)
    white = (244, 247, 255, 255)
    muted = (190, 202, 255, 255)
    cyan = (104, 222, 255, 255)

    # A compact terminal window communicates both the VM and coding-agent purpose.
    box = (47 * scale, 57 * scale, 209 * scale, 195 * scale)
    draw.rounded_rectangle(box, radius=21 * scale, outline=white, width=10 * scale)
    draw.line((51 * scale, 96 * scale, 205 * scale, 96 * scale), fill=muted, width=8 * scale)
    for x, color in ((72, cyan), (94, muted), (116, muted)):
        draw.ellipse(
            ((x - 5) * scale, 74 * scale, (x + 5) * scale, 84 * scale),
            fill=color,
        )

    # Prompt glyph, built from strokes rather than a font so every size is stable.
    stroke = 11 * scale
    draw.line(
        (82 * scale, 126 * scale, 108 * scale, 148 * scale, 82 * scale, 170 * scale),
        fill=white,
        width=stroke,
        joint="curve",
    )
    draw.line(
        (124 * scale, 170 * scale, 166 * scale, 170 * scale),
        fill=cyan,
        width=stroke,
    )

    return canvas.resize((output_size, output_size), Image.Resampling.LANCZOS)


def centered_asset(width: int, height: int, mark_size: int) -> Image.Image:
    image = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    mark = draw_mark(mark_size)
    image.alpha_composite(mark, ((width - mark_size) // 2, (height - mark_size) // 2))
    return image


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)

    # A multi-resolution ICO is required for sharp taskbar, Alt-Tab, and window icons.
    icon = draw_mark(256)
    icon.save(
        ASSETS / "AppIcon.ico",
        format="ICO",
        sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)],
        bitmap_format="png",
    )

    assets = {
        "Square44x44Logo.scale-200.png": centered_asset(88, 88, 80),
        "Square44x44Logo.targetsize-24_altform-unplated.png": draw_mark(24),
        "Square44x44Logo.targetsize-48_altform-lightunplated.png": draw_mark(48),
        "Square150x150Logo.scale-200.png": centered_asset(300, 300, 256),
        "StoreLogo.png": draw_mark(50),
        "LockScreenLogo.scale-200.png": draw_mark(48),
        "Wide310x150Logo.scale-200.png": centered_asset(620, 300, 256),
        "SplashScreen.scale-200.png": centered_asset(1240, 600, 280),
    }
    for name, image in assets.items():
        image.save(ASSETS / name, format="PNG", optimize=True)


if __name__ == "__main__":
    main()
