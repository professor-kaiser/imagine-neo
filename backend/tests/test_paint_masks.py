"""Paint coverage and public color parameter contracts."""
import numpy as np
import pytest
from app.models.image_types import Rect
from app.plugins import validate_bgra, validate_hsv
from app.plugins.catalog import Parameter, PluginFactory, get_plugin_registry
from app.plugins.executor import PluginExecutor
from app.plugins.masks import decode_mask
from test_plugins import make_registry


@pytest.mark.parametrize("color", [[0, 0, 0], [255, 255, 255], [17, 83, 209]])
def test_painted_pixels_are_white_independent_of_preview_color(color):
    image = np.zeros((5, 7, 4), np.uint8)
    image[1:4, 2:6, :3] = color
    image[1:4, 2:6, 3] = 255
    image[2, 3] = 0  # erased
    image[1, 2, 3] = 1  # anti-aliased coverage is still painted
    before = image.copy()
    result = PluginExecutor().apply("mask_paint", {}, image)[0]
    np.testing.assert_array_equal(decode_mask(result), np.where(image[:, :, 3] > 0, 255, 0))
    np.testing.assert_array_equal(image, before)


def test_empty_painting_and_selection():
    executor = PluginExecutor()
    empty = np.zeros((5, 7, 4), np.uint8)
    assert not decode_mask(executor.apply("mask_paint", {}, empty)[0]).any()
    full = np.full_like(empty, 255)
    mask = decode_mask(executor.apply("mask_paint", {}, full, Rect(2, 1, 3, 2))[0])
    expected = np.zeros((5, 7), np.uint8)
    expected[1:3, 2:5] = 255
    np.testing.assert_array_equal(mask, expected)
    descriptor = get_plugin_registry().get("mask_paint").describe()
    assert descriptor["input_kind"] == "painting" and descriptor["output_kind"] == "mask"


@pytest.mark.parametrize("kind,value", [
    ("color_hsv", [120, 200, 240]), ("color_bgra", [11, 22, 33, 44]), ("hsv", [120, 200, 240])])
def test_color_parameter_copies_and_validation(kind, value):
    field = Parameter.parse("any_name", {"type": kind, "default": value})
    bound = field.validate(value)
    assert bound == value and bound is not value
    bound[0] = 0
    assert field.default[0] == value[0]


@pytest.mark.parametrize("kind,value", [
    ("color_bgra", [0, 0, 0]), ("color_bgra", [0, 0, 0, 0, 0]), ("color_bgra", [0, 0, 0, 256]),
    ("color_bgra", [0, 0, -1, 0]), ("color_bgra", [0, 0, 0, False]), ("color_bgra", [0, 0, 1.5, 255]),
    ("color_bgra", "black"), ("color_hsv", [180, 0, 0]), ("color_hsv", [0, 256, 0]),
    ("color_hsv", [0, 0, 0, 0]), ("color_hsv", [0, 0, True])])
def test_invalid_public_colors(kind, value):
    with pytest.raises(ValueError):
        Parameter.parse("color", {"type": kind}).validate(value)


def test_color_names_do_not_determine_the_editor_or_argument_type(tmp_path):
    source = """from app.plugins import ImagePlugin
class Example(ImagePlugin):
    def __init__(self, accent: list[int], tint: list[int]):
        self.accent, self.tint = accent, tint
    def apply(self, image, selection, masks):
        result = image.copy()
        result[:] = self.tint
        return result
"""
    registry = make_registry(tmp_path, source, parameters={
        "accent": {"type": "color_hsv", "default": [120, 255, 255]},
        "tint": {"type": "color_bgra", "default": [11, 22, 33, 44]}})
    assert not registry.errors
    definition = registry.get("example")
    instance = PluginFactory.create(definition, {})
    assert instance.accent == [120, 255, 255] and instance.tint == [11, 22, 33, 44]
    result = PluginExecutor(registry).apply("example", {"tint": [55, 66, 77, 88]}, np.zeros((2, 3, 4), np.uint8))[0]
    np.testing.assert_array_equal(result, np.full((2, 3, 4), [55, 66, 77, 88], np.uint8))
    assert validate_bgra([0, 128, 255, 0]) == [0, 128, 255, 0]
    assert validate_hsv([179, 255, 255]) == [179, 255, 255]


@pytest.mark.parametrize("overrides", [{"input_kind": "unknown"}, {"input_kind": "painting", "output_kind": "image"}])
def test_invalid_input_kind(tmp_path, overrides):
    assert make_registry(tmp_path, **overrides).errors
