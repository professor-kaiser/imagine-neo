"""Generate public demo images from the real project processing code."""
from pathlib import Path
import json
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "backend"))
sys.path.insert(0, str(ROOT))

import cv2
import numpy as np
from app.models.image_types import Rect
from app.processing.filters import to_grayscale
from plugins.denoise.plugin import DenoisePlugin

ASSETS = ROOT / "docs" / "site" / "assets"
SELECTION = Rect(80, 70, 800, 460)
PARAMETERS = dict(strength=15.0, template_window=7, search_window=21)


def scene():
    y, x = np.indices((600, 960))
    image = np.empty((600, 960, 4), dtype=np.uint8)
    image[:, :, 0] = np.clip(170 + y * 0.08, 0, 255)
    image[:, :, 1] = np.clip(115 + y * 0.15, 0, 255)
    image[:, :, 2] = np.clip(70 + y * 0.13, 0, 255)
    image[:, :, 3] = 255
    cv2.circle(image, (740, 155), 65, (135, 222, 247, 255), -1, cv2.LINE_AA)
    cv2.fillPoly(image, [np.array([[0, 395], [260, 165], [515, 420]])], (118, 97, 68, 255))
    cv2.fillPoly(image, [np.array([[280, 420], [585, 230], [890, 420]])], (128, 142, 92, 255))
    cv2.fillPoly(image, [np.array([[0, 420], [0, 310], [390, 440], [960, 315], [960, 600], [0, 600]])], (112, 150, 112, 255))
    cv2.ellipse(image, (475, 575), (500, 115), 0, 180, 360, (101, 124, 73, 255), -1, cv2.LINE_AA)
    cv2.rectangle(image, (170, 345), (320, 465), (160, 193, 220, 255), -1)
    cv2.fillPoly(image, [np.array([[145, 350], [245, 260], [345, 350]])], (67, 81, 160, 255))
    cv2.rectangle(image, (224, 390), (262, 465), (89, 99, 68, 255), -1)
    cv2.rectangle(image, (184, 370), (213, 400), (178, 230, 248, 255), -1)
    for px, py, radius in [(625, 375, 54), (850, 390, 65), (90, 435, 36)]:
        cv2.rectangle(image, (px - 6, py), (px + 6, py + 100), (73, 96, 92, 255), -1)
        cv2.circle(image, (px, py - 18), radius, (73, 123, 53, 255), -1, cv2.LINE_AA)
    noise = np.random.default_rng(2026).normal(0, 18, image[:, :, :3].shape)
    image[:, :, :3] = np.clip(image[:, :, :3].astype(np.float64) + noise, 0, 255).astype(np.uint8)
    return image


def process(original):
    saved = original.copy()
    denoised = DenoisePlugin(**PARAMETERS).apply(original, SELECTION, [])
    grayscale = to_grayscale(original)
    np.testing.assert_array_equal(original, saved)
    outside = np.ones(original.shape[:2], dtype=bool)
    r = SELECTION
    outside[r.y:r.y+r.height, r.x:r.x+r.width] = False
    np.testing.assert_array_equal(denoised[outside], original[outside])
    for pixels in [denoised, grayscale]:
        assert pixels.shape == original.shape and pixels.dtype == np.uint8
        np.testing.assert_array_equal(pixels[:, :, 3], original[:, :, 3])
    return denoised, grayscale


def write_png(name, pixels):
    if not cv2.imwrite(str(ASSETS / name), pixels):
        raise OSError(f"Cannot write {name}")


def main():
    ASSETS.mkdir(parents=True, exist_ok=True)
    original = scene()
    denoised, grayscale = process(original)
    for name, pixels in [("original.png", original), ("denoise.png", denoised), ("grayscale.png", grayscale)]:
        write_png(name, pixels)
    card = np.full((630, 1200, 3), (21, 27, 24), dtype=np.uint8)
    cv2.putText(card, "IMAGINE NEO", (54, 93), cv2.FONT_HERSHEY_SIMPLEX, 1.6, (220, 241, 226), 3, cv2.LINE_AA)
    cv2.putText(card, "Windows image editor / Python + OpenCV", (57, 137), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (168, 192, 177), 1, cv2.LINE_AA)
    left = cv2.resize(original[:, :, :3], (540, 338))
    right = cv2.resize(denoised[:, :, :3], (540, 338))
    card[212:550, 50:590] = left
    card[212:550, 610:1150] = right
    for word, px in [("BEFORE", 50), ("DENOISED", 610)]:
        cv2.putText(card, word, (px, 192), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (220, 241, 226), 1, cv2.LINE_AA)
    cv2.putText(card, "WinUI 3    C#    NumPy    YAML plugins", (54, 593), cv2.FONT_HERSHEY_SIMPLEX, 0.65, (168, 192, 177), 1, cv2.LINE_AA)
    write_png("social-preview.png", card)
    metadata = {"source": "Deterministic synthetic scene; no external photographs", "seed": 2026,
                "shape": list(original.shape), "selection": {"x":80,"y":70,"width":800,"height":460},
                "plugin": "plugins.denoise.plugin.DenoisePlugin", "parameters": PARAMETERS,
                "grayscale": "app.processing.filters.to_grayscale", "opencv": cv2.__version__}
    (ASSETS / "demo.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
    print("Demo generated; original, alpha and outside-selection pixels verified.")


if __name__ == "__main__":
    main()
