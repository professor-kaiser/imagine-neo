"""Selection limits for all mask generators, including direct Python calls."""
import numpy as np
import pytest
from app.models.image_types import Rect
from app.plugins import encode_mask
from app.plugins.catalog import PluginFactory, get_plugin_registry
from app.plugins.executor import PluginExecutor, PluginResultError
from app.plugins.masks import decode_mask
from test_plugins import make_registry


CASES = [
    ("mask_from_image", {}),
    ("mask_from_image", {"invert": True}),
    ("mask_threshold", {"channel": "red", "lower": 40, "upper": 170}),
    ("mask_threshold", {"channel": "red", "lower": 40, "upper": 170, "invert": True}),
    ("mask_threshold", {"channel": "alpha", "lower": 0, "upper": 127}),
    ("mask_threshold", {"channel": "hsv", "lower_hsv": [170, 20, 40], "upper_hsv": [10, 255, 255]}),
    ("mask_threshold", {"channel": "hsv", "invert": True, "blur_size": 3}),
    ("mask_paint", {}),
]


@pytest.mark.parametrize("identity,arguments", CASES)
@pytest.mark.parametrize("roi", [Rect(2, 1, 4, 3), Rect(7, 5, 1, 1), Rect(0, 0, 8, 6)])
def test_generators_keep_full_dimensions_and_zero_outside_roi(identity, arguments, roi):
    image = np.random.default_rng(51).integers(0, 256, (6, 8, 4), np.uint8)
    image[0, 0] = [0, 0, 0, 0]
    image[2, 3] = [0, 0, 255, 255]
    before = image.copy()
    definition = get_plugin_registry().get(identity)
    plugin = PluginFactory.create(definition, arguments)
    full = decode_mask(plugin.apply(image, None, []))
    area = np.s_[roi.y:roi.y + roi.height, roi.x:roi.x + roi.width]
    expected = np.zeros((6, 8), np.uint8)
    expected[area] = full[area]
    direct = plugin.apply(image, roi, [])
    hosted = PluginExecutor().apply(identity, arguments, image, roi)[0]
    for result in [direct, hosted]:
        assert result.shape == image.shape and result.dtype == np.uint8
        np.testing.assert_array_equal(decode_mask(result), expected)
        assert np.all(result[:, :, 3] == 255)
    np.testing.assert_array_equal(image, before)


@pytest.mark.parametrize("outside", [0, 255])
def test_mask_host_supplies_full_image_and_enforces_roi_for_custom_plugins(tmp_path, outside):
    source = """from app.plugins import ImagePlugin
class Example(ImagePlugin):
    def apply(self, image, selection, masks):
        assert image.shape == (6, 8, 4)
        assert (selection.x, selection.y, selection.width, selection.height) == (2, 1, 4, 3)
        image[:] = 255  # Intentionally ignores ROI.
        return image
"""
    registry = make_registry(tmp_path, source, parameters={}, output_kind="mask")
    image = np.zeros((6, 8, 4), np.uint8)
    result = PluginExecutor(registry).apply("example", {}, image, Rect(2, 1, 4, 3), mask_outside_value=outside)[0]
    expected = np.full((6, 8), outside, np.uint8)
    expected[1:4, 2:6] = 255
    np.testing.assert_array_equal(decode_mask(result), expected)
    assert not image.any()


def test_mask_host_rejects_resized_output_with_roi(tmp_path):
    source = """from app.plugins import ImagePlugin
class Example(ImagePlugin):
    def apply(self, image, selection, masks):
        image[:] = 255
        return image[:2, :3]
"""
    registry = make_registry(tmp_path, source, parameters={}, output_kind="mask")
    with pytest.raises(PluginResultError, match="source dimensions"):
        PluginExecutor(registry).apply("example", {}, np.zeros((6, 8, 4), np.uint8), Rect(2, 1, 4, 3))


def test_encoding_keeps_mask_values_and_does_not_modify_input():
    mask = np.array([[0, 1, 127], [255, 42, 80]], np.uint8)
    before = mask.copy()
    np.testing.assert_array_equal(decode_mask(encode_mask(mask)), mask)
    result = decode_mask(encode_mask(mask, Rect(1, 0, 1, 2)))
    np.testing.assert_array_equal(result, [[0, 1, 0], [0, 42, 0]])
    np.testing.assert_array_equal(mask, before)
    with pytest.raises(ValueError, match="outside"):
        encode_mask(mask, Rect(2, 0, 2, 1))
