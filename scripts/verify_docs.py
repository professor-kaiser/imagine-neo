"""Execute the documented plugin through the real catalogue and host."""
import re
import sys
from pathlib import Path
from tempfile import TemporaryDirectory

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "backend"))

import numpy as np
from app.models.image_types import Rect
from app.plugins.catalog import PluginRegistry
from app.plugins.executor import PluginExecutor


def snippets(path):
    markdown = path.read_text(encoding="utf-8")
    source = next(block for block in re.findall(r"```python\n(.*?)\n```", markdown, re.S)
                  if "class TintPlugin(ImagePlugin)" in block)
    config = next(block for block in re.findall(r"```yaml\n(.*?)\n```", markdown, re.S)
                  if "class: TintPlugin" in block)
    return source, config


def main():
    source = (ROOT / "docs/examples/tint/plugin.py").read_text(encoding="utf-8").strip()
    config = (ROOT / "docs/examples/tint/plugins.yaml").read_text(encoding="utf-8").strip()
    for path in [ROOT / "README.md", ROOT / "README.en.md", ROOT / "backend/README.md"]:
        py, yaml = snippets(path)
        assert py == source and yaml == config, f"Example differs from documentation: {path}"
    with TemporaryDirectory(prefix="imagine-neo-docs-") as folder:
        root = Path(folder)
        (root / "tint").mkdir()
        (root / "plugins.yaml").write_text("version: 1\nplugins:\n  - tint\n", encoding="utf-8")
        (root / "tint/plugin.py").write_text(source + "\n", encoding="utf-8")
        (root / "tint/plugins.yaml").write_text(config + "\n", encoding="utf-8")
        registry = PluginRegistry(root)
        assert not registry.errors, registry.errors
        descriptor = registry.get("tint").describe()
        types = {p["name"]: p["type"] for p in descriptor["parameters"]}
        assert types == {"color": "color_bgra", "amount": "number"}
        image = np.full((8, 10, 4), [40, 80, 120, 255], dtype=np.uint8)
        image[:, :, 3] = np.arange(80, dtype=np.uint8).reshape(8, 10) * 3
        original = image.copy()
        image.setflags(write=False)
        executor = PluginExecutor(registry)
        # YAML defaults: red 128/255 alpha, amount 0.4.
        result = executor.apply("tint", {}, image)[0]
        weight = 0.4 * 128 / 255
        expected_color = np.rint(np.array([40, 80, 120]) * (1 - weight)
                                 + np.array([0, 0, 255]) * weight).astype(np.uint8)
        np.testing.assert_array_equal(result[:, :, :3], np.broadcast_to(expected_color, (8, 10, 3)))
        np.testing.assert_array_equal(result[:, :, 3], original[:, :, 3])
        np.testing.assert_array_equal(image, original)
        assert not np.shares_memory(result, image)
        selection = Rect(2, 1, 5, 4)
        first = np.zeros((8, 10), dtype=np.uint8)
        second = first.copy()
        first[2:4, 1:4] = 255
        second[3:6, 5:8] = 255
        active = (first != 0) | (second != 0)
        roi = np.zeros_like(active)
        roi[1:5, 2:7] = True
        active &= roi
        masks_before = [first.copy(), second.copy()]
        partial = executor.apply("tint", {}, image, selection, [first, second])[0]
        expected = original.copy()
        expected[active, :3] = expected_color
        np.testing.assert_array_equal(partial, expected)
        np.testing.assert_array_equal(first, masks_before[0])
        np.testing.assert_array_equal(second, masks_before[1])
        blocked = executor.apply("tint", {}, image, selection, [np.zeros_like(first)])[0]
        np.testing.assert_array_equal(blocked, original)
        unchanged = executor.apply("tint", {"amount": 0}, image)[0]
        np.testing.assert_array_equal(unchanged, original)
        for arguments in [{"color": [255, 0, 0]}, {"color": [True, 0, 0, 255]}, {"amount": 2}]:
            try:
                executor.apply("tint", arguments, image)
            except ValueError:
                pass
            else:
                raise AssertionError(f"Invalid documented arguments accepted: {arguments}")
        np.testing.assert_array_equal(image, original)
    example_registry = PluginRegistry(ROOT / "docs/examples")
    assert not example_registry.errors, example_registry.errors
    print("Documented Python/YAML execute correctly: parameters, colors, alpha, ROI, masks and original verified.")


if __name__ == "__main__":
    main()
