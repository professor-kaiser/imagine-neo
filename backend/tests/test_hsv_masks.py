"""HSV threshold boundaries, circular hue and typed form metadata."""
import cv2
import numpy as np
import pytest
from app.models.image_types import Rect
from app.plugins.catalog import Parameter, PluginFactory, get_plugin_registry
from app.plugins.executor import PluginExecutor
from app.plugins.masks import decode_mask
from test_masks import generate
from test_plugins import make_registry


def hsv_image(values):
    return cv2.cvtColor(cv2.cvtColor(np.array([values], np.uint8), cv2.COLOR_HSV2BGR), cv2.COLOR_BGR2BGRA)


def test_inclusive_circular_hue_range_and_transparent_pixels():
    image = hsv_image([[h, 255, 255] for h in [0, 5, 10, 11, 169, 170, 179, 90]])
    image[0, 1, 3] = 0
    before = image.copy()
    args = dict(channel="hsv", lower_hsv=[170, 100, 100], upper_hsv=[10, 255, 255])
    np.testing.assert_array_equal(decode_mask(generate("mask_threshold", image, **args)),
                                  [[255, 0, 255, 0, 0, 255, 255, 0]])
    np.testing.assert_array_equal(decode_mask(generate("mask_threshold", image, invert=True, **args)),
                                  [[0, 0, 0, 255, 255, 0, 0, 255]])
    np.testing.assert_array_equal(image, before)


def test_saturation_and_value_bounds_and_default_full_range():
    image = hsv_image([[50, 100, 100], [50, 200, 200], [50, 50, 200], [50, 200, 50], [20, 200, 200]])
    result = decode_mask(generate("mask_threshold", image, channel="hsv", lower_hsv=[40, 90, 90], upper_hsv=[60, 210, 210]))
    np.testing.assert_array_equal(result, [[255, 255, 0, 0, 0]])
    assert np.all(decode_mask(generate("mask_threshold", image, channel="hsv")) == 255)


def test_hsv_blurs_colors_before_conversion_and_preserves_roi():
    image = np.random.default_rng(12).integers(0, 256, (15, 17, 4), np.uint8)
    before = image.copy()
    lo, hi = [20, 30, 40], [120, 200, 240]
    colors = cv2.GaussianBlur(image[:, :, :3], (3, 3), 0)
    expected = cv2.inRange(cv2.cvtColor(colors, cv2.COLOR_BGR2HSV), np.array(lo, np.uint8), np.array(hi, np.uint8))
    expected[image[:, :, 3] == 0] = 0
    np.testing.assert_array_equal(decode_mask(generate("mask_threshold", image, channel="hsv",
        lower_hsv=lo, upper_hsv=hi, blur_size=3)), expected)
    result = PluginExecutor().apply("mask_threshold", dict(channel="hsv", lower_hsv=lo, upper_hsv=hi, blur_size=3),
                                    image, Rect(3, 4, 5, 6))[0]
    roi = np.zeros_like(expected)
    roi[4:10, 3:8] = expected[4:10, 3:8]
    np.testing.assert_array_equal(decode_mask(result), roi)
    np.testing.assert_array_equal(image, before)


@pytest.mark.parametrize("value", [[180, 0, 0], [-1, 0, 0], [0, 256, 0], [0, 0, 256],
                                  [0, 0], [0, 0, 0, 0], [0, False, 0], [0, 0, 1.5], "0,0,0", (0, 0, 0)])
def test_invalid_hsv_triplets(value):
    with pytest.raises(ValueError):
        generate("mask_threshold", channel="hsv", lower_hsv=value)


@pytest.mark.parametrize("lo,hi", [([0, 200, 0], [179, 100, 255]), ([0, 0, 200], [179, 255, 100])])
def test_reversed_sv_bounds_are_rejected(lo, hi):
    with pytest.raises(ValueError, match="lower S/V"):
        generate("mask_threshold", channel="hsv", lower_hsv=lo, upper_hsv=hi)


def test_hsv_binding_is_typed_and_does_not_alias_defaults_or_inputs():
    definition = get_plugin_registry().get("mask_threshold")
    choices = {p["name"]: p for p in definition.describe()["parameters"]}
    assert choices["channel"]["choices"] == ["gray", "blue", "green", "red", "alpha", "hsv"]
    assert choices["lower"]["palette_channel"] == "channel"
    assert choices["lower_hsv"]["visible_when"] == {"channel": ["hsv"]}
    values = definition.bind({"channel": "hsv"})
    values["lower_hsv"][0] = 77
    assert definition.bind({})["lower_hsv"] == [0, 0, 0]
    supplied = [10, 20, 30]
    plugin = PluginFactory.create(definition, {"channel": "hsv", "lower_hsv": supplied})
    plugin.lower_hsv[0] = 25
    assert supplied == [10, 20, 30]


@pytest.mark.parametrize("data", [
    {"type": "text", "choices": []}, {"type": "text", "choices": ["a", "a"]},
    {"type": "text", "choices": [1]}, {"type": "integer", "choices": ["a"]},
    {"type": "text", "choices": ["a"], "default": "b"}, {"type": "hsv", "default": [180, 0, 0]},
    {"type": "integer", "palette_channel": "channel"},
    {"type": "integer", "visible_when": {"channel": ["red"]}},
])
def test_invalid_field_metadata(data):
    with pytest.raises(ValueError):
        Parameter.parse("field", data)


def test_choice_values_are_validated_server_side():
    choice = Parameter.parse("channel", {"type": "text", "choices": ["red", "hsv"], "default": "red"})
    assert choice.validate("hsv") == "hsv"
    with pytest.raises(ValueError):
        choice.validate("free text")


@pytest.mark.parametrize("field", [
    {"type": "integer", "default": 0, "visible_when": {"missing": ["red"]}},
    {"type": "integer", "default": 0, "visible_when": {"label": ["invalid"]}},
    {"type": "integer", "default": 0, "min": 0, "max": 255, "palette_channel": "missing"},
])
def test_invalid_cross_field_references(tmp_path, field):
    registry = make_registry(tmp_path, parameters={
        "amount": field, "label": {"type": "text", "default": "test", "choices": ["test"]},
        "ratio": {"type": "number", "default": 0.5}, "enabled": {"type": "boolean", "default": True}})
    assert registry.errors and not registry.definitions
