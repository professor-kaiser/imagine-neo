# Imagine Neo brand assets

An original black and white vector logo: a tall, slender smiling stick figure with folded arms, a raised eyebrow and a thought bubble containing **neo**, on a white rounded tile.

- `imagine-neo-logo.svg`: editable vector source.
- `imagine-neo-logo.png`: 512 × 512 image used in the app header.
- `imagine-neo-logo-1024.png`: 1,024 × 1,024 image for larger displays.
- `imagine-neo.ico`: Windows icon with 16, 24, 32, 48, 64, 128 and 256 pixel sizes, embedded in the executable and assigned to the window.

Regenerate these assets on Windows with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/create_brand_assets.ps1
```

The SVG is the single drawing source. The script renders it with `rsvg-convert` from the local photolab Conda environment and produces PNGs and a multi-resolution Windows icon. The background outside the rounded tile is transparent. The drawing uses vector shapes and Segoe UI lettering; no external artwork is required.
